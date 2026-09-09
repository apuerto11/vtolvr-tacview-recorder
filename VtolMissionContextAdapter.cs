using System;
using System.Collections.Generic;
using UnityEngine;
using VTOLVRTacviewSPRecorder.Core;

namespace VTOLVRTacviewSPRecorder.Main;

internal sealed class VtolMissionContextAdapter
{
    private const int StaticEntityCap = 512;

    private readonly VTMapManager _mapManager;
    private readonly VTScenario _scenario;
    private readonly EntityRegistry _registry;
    private readonly List<StaticEntitySource> _staticSources = new List<StaticEntitySource>();
    private readonly HashSet<object> _registeredIdentities =
        new HashSet<object>(ReferenceEqualityComparer.Instance);
    private readonly List<AirportManager> _airports = new List<AirportManager>();
    private bool _capWarningLogged;

    public VtolMissionContextAdapter(
        VTMapManager mapManager,
        VTScenario scenario,
        EntityRegistry registry)
    {
        _mapManager = mapManager ?? throw new ArgumentNullException(nameof(mapManager));
        _scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public TacviewRecordingMetadata BuildRecordingMetadata(string mapId = null)
    {
        VTMap map = _mapManager.map;
        string theater = map == null
            ? VtolActorClassifier.FirstNonEmpty(_scenario.mapID)
            : VtolActorClassifier.FirstNonEmpty(map.mapID, map.mapName, map.sceneName, _scenario.mapID);
        string briefing = VtolActorClassifier.FirstNonEmpty(
            _scenario.scenarioDescription,
            map?.mapDescription);
        return new TacviewRecordingMetadata(theater, "Singleplayer", briefing, mapId);
    }

    public void RegisterStaticContext()
    {
        RegisterBullseyes();
        RegisterAirbases();
    }

    public void AppendSamplingTargetsTo(List<EntitySamplingTarget> destination)
    {
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        for (int index = 0; index < _staticSources.Count; index++)
        {
            StaticEntitySource source = _staticSources[index];
            destination.Add(new EntitySamplingTarget(source.Entity, source.Source));
        }
    }

    public void Clear()
    {
        _staticSources.Clear();
        _registeredIdentities.Clear();
        _airports.Clear();
        _capWarningLogged = false;
    }

    private void RegisterBullseyes()
    {
        ScenarioWaypoints waypoints = _scenario.waypoints;
        if (waypoints == null)
        {
            return;
        }

        TryRegisterBullseye(
            waypoints.bullseye,
            waypoints.bullseyeTransform,
            "Allied Bullseye",
            "Allies",
            "Blue");
        TryRegisterBullseye(
            waypoints.bullseyeB,
            waypoints.bullseyeBTransform,
            "Enemy Bullseye",
            "Enemies",
            "Red");
    }

    private void TryRegisterBullseye(
        Waypoint waypoint,
        Transform transform,
        string fallbackName,
        string coalition,
        string color)
    {
        try
        {
            RegisterBullseye(waypoint, transform, fallbackName, coalition, color);
        }
        catch (Exception ex)
        {
            LogWarn($"Mission bullseye skipped: {ex.Message}");
        }
    }

    private void RegisterBullseye(
        Waypoint waypoint,
        Transform transform,
        string fallbackName,
        string coalition,
        string color)
    {
        if (waypoint == null || transform == null)
        {
            return;
        }

        string name = VtolActorClassifier.FirstNonEmpty(waypoint.name, fallbackName);
        RegisterStatic(
            waypoint,
            transform,
            new EntityMetadata(
                "Navaid+Static+Bullseye",
                name,
                coalition,
                color));
    }

    private void RegisterAirbases()
    {
        _airports.Clear();
        try
        {
            _scenario.GetAllAirports(_airports);
        }
        catch (Exception ex)
        {
            LogWarn($"Mission airbase enumeration skipped: {ex.Message}");
            return;
        }

        for (int index = 0; index < _airports.Count; index++)
        {
            try
            {
                AirportManager airport = _airports[index];
                if (airport == null || airport.transform == null || airport.carrierSpawn != null)
                {
                    continue;
                }

                RegisterStatic(
                    airport,
                    airport.transform,
                    new EntityMetadata(
                        "Ground+Static+Aerodrome",
                        VtolActorClassifier.FirstNonEmpty(airport.airportName, "Airbase"),
                        VtolActorClassifier.GetCoalition(airport.team),
                        VtolActorClassifier.GetColor(airport.team)));
            }
            catch (Exception ex)
            {
                LogWarn($"Mission airbase skipped: {ex.Message}");
            }
        }
    }

    private void RegisterStatic(object identity, Transform transform, EntityMetadata metadata)
    {
        if (_staticSources.Count >= StaticEntityCap)
        {
            if (!_capWarningLogged)
            {
                _capWarningLogged = true;
                LogWarn($"Static mission entity cap ({StaticEntityCap}) reached; additional context is skipped.");
            }

            return;
        }

        if (!_registeredIdentities.Add(identity))
        {
            return;
        }

        TrackedEntity entity = _registry.Register(
            identity,
            EntityKind.Static,
            false,
            metadata);
        _staticSources.Add(new StaticEntitySource(
            entity,
            new VtolEntitySource(transform, _mapManager)));
    }

    private readonly struct StaticEntitySource
    {
        public StaticEntitySource(TrackedEntity entity, VtolEntitySource source)
        {
            Entity = entity;
            Source = source;
        }

        public TrackedEntity Entity { get; }

        public VtolEntitySource Source { get; }
    }
}
