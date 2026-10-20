using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using VTOLAPI;
using VTOLVR.Multiplayer;
using VTOLVRTacviewSPRecorder.Core;

namespace VTOLVRTacviewSPRecorder.Main;

internal sealed class RecorderLifecycle : IDisposable
{
    private const int MissionReadyMaxAttempts = 120;
    private const float MissionReadyRetrySeconds = 0.25f;
    // Campaign scenes can advertise scenarioReady before their static Unity
    // Terrain tiles become active. Keep this bounded and before writer/header
    // creation: a source which never appears still records cleanly without a
    // MapId instead of delaying a mission indefinitely.
    private const int TerrainDiscoveryMaxAttempts = 20;

    private readonly MonoBehaviour _coroutineHost;
    private readonly TacviewRealtimeServer _realtimeServer;
    private readonly TacviewRealtimeSettings _settings;
    private RecorderSession _activeSession;
    private IEnumerator _missionWaitRoutine;
    private bool _eventsSubscribed;
    private bool _disposed;

    public RecorderLifecycle(
        MonoBehaviour coroutineHost,
        TacviewRealtimeServer realtimeServer = null,
        TacviewRealtimeSettings settings = null)
    {
        _coroutineHost = coroutineHost ?? throw new ArgumentNullException(nameof(coroutineHost));
        _realtimeServer = realtimeServer;
        _settings = settings;
    }

    public void StartListening()
    {
        if (_eventsSubscribed)
        {
            Log("Recorder lifecycle subscription ignored: already listening.");
            return;
        }

        if (_disposed)
        {
            LogWarn("Recorder lifecycle subscription ignored: lifecycle is disposed.");
            return;
        }

        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        global::VTOLAPI.VTAPI.SceneLoaded += OnSceneLoaded;
        global::VTOLAPI.VTAPI.MissionReloaded += OnMissionReloaded;
        _eventsSubscribed = true;

        ScheduleMissionStart(global::VTOLAPI.VTAPI.currentScene, "initial state");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            Log("Recorder lifecycle dispose ignored: already disposed.");
            return;
        }

        _disposed = true;

        if (_eventsSubscribed)
        {
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            global::VTOLAPI.VTAPI.SceneLoaded -= OnSceneLoaded;
            global::VTOLAPI.VTAPI.MissionReloaded -= OnMissionReloaded;
            _eventsSubscribed = false;
        }

        CancelPendingMissionStart("mod unloaded");
        StopSession("mod unloaded");
    }

    private void OnActiveSceneChanged(Scene previous, Scene next)
    {
        CancelPendingMissionStart($"Unity scene changed to {next.name}");
        StopSession($"Unity scene changed from {previous.name} to {next.name}");
    }

    private void OnSceneLoaded(VTScenes scene)
    {
        CancelPendingMissionStart($"VTAPI scene loaded: {scene}");

        if (!IsMissionScene(scene))
        {
            StopSession($"left mission scene for {scene}");
            return;
        }

        ScheduleMissionStart(scene, $"VTAPI scene loaded: {scene}");
    }

    private void OnMissionReloaded()
    {
        CancelPendingMissionStart("mission reloaded");
        StopSession("mission reloaded");
        ScheduleMissionStart(global::VTOLAPI.VTAPI.currentScene, "mission reloaded");
    }

    private void ScheduleMissionStart(VTScenes scene, string reason)
    {
        if (_disposed || !IsMissionScene(scene))
        {
            return;
        }

        CancelPendingMissionStart("new mission start check");

        IEnumerator routine = WaitForReadyMission(scene, reason);
        _missionWaitRoutine = routine;
        _coroutineHost.StartCoroutine(routine);
    }

    private IEnumerator WaitForReadyMission(VTScenes scene, string reason)
    {
        var retryDelay = new WaitForSecondsRealtime(MissionReadyRetrySeconds);
        int terrainDiscoveryAttempts = 0;

        for (int attempt = 1; attempt <= MissionReadyMaxAttempts; attempt++)
        {
            if (_disposed || !_eventsSubscribed || global::VTOLAPI.VTAPI.currentScene != scene)
            {
                CompleteMissionWait();
                yield break;
            }

            if (VTOLMPUtils.IsMultiplayer())
            {
                StopSession("multiplayer mission detected");
                Log("Recorder session start ignored: multiplayer mission.");
                CompleteMissionWait();
                yield break;
            }

            VTMapManager mapManager = VTMapManager.fetch;
            VTScenario scenario = VTScenario.current;

            if (mapManager != null && mapManager.map != null && mapManager.scenarioReady && scenario != null)
            {
                if (VtolTerrainExporter.IsTerrainSourceAvailable(mapManager, out string terrainSourceError))
                {
                    StartSession(scene, mapManager, scenario);
                    CompleteMissionWait();
                    yield break;
                }

                terrainDiscoveryAttempts++;
                if (terrainDiscoveryAttempts >= TerrainDiscoveryMaxAttempts)
                {
                    LogWarn(
                        $"Tacview terrain source discovery did not complete after " +
                        $"{TerrainDiscoveryMaxAttempts * MissionReadyRetrySeconds:0.#}s: " +
                        $"{terrainSourceError}. Recording will start without a MapId if the source remains unsupported.");
                    StartSession(scene, mapManager, scenario);
                    CompleteMissionWait();
                    yield break;
                }
            }

            yield return retryDelay;
        }

        LogWarn($"Recorder session start timed out after {MissionReadyMaxAttempts * MissionReadyRetrySeconds:0.#}s: {scene} was not ready ({reason}).");
        CompleteMissionWait();
    }

    private void CompleteMissionWait()
    {
        _missionWaitRoutine = null;
    }

    private void CancelPendingMissionStart(string reason)
    {
        IEnumerator routine = _missionWaitRoutine;
        _missionWaitRoutine = null;

        if (routine == null)
        {
            return;
        }

        try
        {
            _coroutineHost.StopCoroutine(routine);
        }
        catch (Exception ex)
        {
            LogWarn($"Failed to stop pending recorder coroutine ({reason}): {ex.Message}");
        }

        Log($"Pending recorder session start cancelled ({reason}).");
    }

    private void StartSession(VTScenes scene, VTMapManager mapManager, VTScenario scenario)
    {
        if (_activeSession != null)
        {
            if (_activeSession.Matches(scene, scenario))
            {
                Log($"Recorder session start ignored: '{_activeSession.MissionName}' is already active.");
                return;
            }

            StopSession("mission changed");
        }

        string missionName = GetMissionName(scenario);
        var session = new RecorderSession(_coroutineHost, scene, scenario, missionName, mapManager, _realtimeServer, _settings);

        bool started;
        try
        {
            started = session.TryStart();
        }
        finally
        {
            // TryStart hands a successful source to the session-owned exporter.
            // On failure nothing should retain the 1.15M-triangle factory
            // snapshot. Either way the static handoff cache is no longer needed.
            VtolTerrainSourceFactory.ClearMeshCache();
        }

        if (!started)
        {
            session.Dispose();
            LogError($"Recorder session could not start: '{missionName}'.");
            return;
        }

        _activeSession = session;
        Log($"Recorder session started: '{missionName}' ({scene}), output '{session.OutputPath}'.");
    }

    private void StopSession(string reason)
    {
        VtolTerrainSourceFactory.ClearMeshCache();
        if (_activeSession == null)
        {
            Log($"Recorder session stop ignored: no active session ({reason}).");
            return;
        }

        RecorderSession session = _activeSession;
        _activeSession = null;
        try
        {
            StopActiveSession(session, reason);
        }
        finally
        {
            // Late real-time clients must not receive the finished mission's state.
            _realtimeServer?.EndSession();
        }
    }

    private static void StopActiveSession(RecorderSession session, string reason)
    {
        if (!session.TryStop())
        {
            Log($"Recorder session stop ignored: '{session.MissionName}' is already stopped ({reason}).");
            session.Dispose();
            return;
        }

        if (session.HasFinalizationFailure)
        {
            LogError($"Recorder session stopped with incomplete ACMI output: '{session.MissionName}' " +
                $"({reason}), retained temporary path '{session.OutputPath}.tmp'.");
        }
        else
        {
            Log($"Recorder session stopped: '{session.MissionName}' ({reason}), output '{session.OutputPath}'.");
        }

        session.Dispose();
    }

    private static bool IsMissionScene(VTScenes scene)
    {
        return scene == VTScenes.Akutan
            || scene == VTScenes.CustomMapBase
            || scene == VTScenes.CustomMapBase_OverCloud;
    }

    private static string GetMissionName(VTScenario scenario)
    {
        if (!string.IsNullOrWhiteSpace(scenario.scenarioName))
        {
            return scenario.scenarioName;
        }

        if (!string.IsNullOrWhiteSpace(scenario.scenarioID))
        {
            return scenario.scenarioID;
        }

        return "unnamed mission";
    }
}
