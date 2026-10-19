global using static VTOLVRTacviewSPRecorder.Main.Logger;
using System.IO;
using System;
using System.Reflection;
using ModLoader.Framework;
using ModLoader.Framework.Attributes;
using VTOLVRTacviewSPRecorder.Core;

namespace VTOLVRTacviewSPRecorder.Main;

[ItemId("de.kalag.vtolvr.tacviewsprecorder")] // Harmony ID for your mod, make sure this is unique
public class Main : VtolMod
{
    public string ModFolder;

    private RecorderLifecycle _recorderLifecycle;
    private TacviewRealtimeServer _realtimeServer;

    private void Awake()
    {
        ModFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        Log($"Awake at {ModFolder}");

        try
        {
            TacviewAircraftDatabaseStatus status = TacviewAircraftDatabase.EnsureInstalled(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            Log(status == TacviewAircraftDatabaseStatus.Current
                ? "Tacview aircraft labels are current."
                : $"Tacview aircraft labels {status.ToString().ToLowerInvariant()}. Restart Tacview to load them.");
        }
        catch (Exception exception)
        {
            // A viewer configuration failure must not prevent flight recording.
            LogWarn($"Could not install Tacview aircraft labels; recording will continue: {exception.Message}");
        }

        _realtimeServer = StartRealtimeServer();
        _recorderLifecycle = new RecorderLifecycle(this, _realtimeServer);
        _recorderLifecycle.StartListening();
    }

    public override void UnLoad()
    {
        _recorderLifecycle?.Dispose();
        _recorderLifecycle = null;
        _realtimeServer?.Dispose();
        _realtimeServer = null;
    }

    private TacviewRealtimeServer StartRealtimeServer()
    {
        // Optional live view (Tacview Real-Time Telemetry). Any failure here
        // leaves file recording untouched.
        try
        {
            var warnings = new System.Collections.Generic.List<string>();
            string path = Path.Combine(ModFolder, TacviewRealtimeSettings.FileName);
            TacviewRealtimeSettings settings = TacviewRealtimeSettings.LoadOrCreate(path, warnings);
            foreach (string warning in warnings)
            {
                LogWarn($"{TacviewRealtimeSettings.FileName}: {warning}");
            }

            if (!settings.Enabled)
            {
                Log("Tacview real-time telemetry is disabled.");
                return null;
            }

            var server = new TacviewRealtimeServer(settings, message => Log(message));
            server.Start();
            return server;
        }
        catch (Exception exception)
        {
            LogWarn($"Tacview real-time telemetry is unavailable; recording will continue: {exception.Message}");
            return null;
        }
    }
}
