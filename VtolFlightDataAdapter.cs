using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VTOLVRTacviewSPRecorder.Core;

namespace VTOLVRTacviewSPRecorder.Main;

/// <summary>
/// Samples per-actor flight data, radar locks and sensor ranges at a reduced
/// rate. The writer then only writes values that changed beyond a threshold.
///
/// Game-API verification status (this code has not been run in game):
/// - Verified elsewhere in this mod: Actor (transform, parentActor, alive,
///   GetComponent/GetComponentInParent/GetComponentsInChildren),
///   VTMapManager.WorldPositionToGPSCoords, EntityRegistry lookups.
/// - Unity API: Rigidbody.velocity, Transform.InverseTransformDirection,
///   Component.GetComponentsInChildren(Type, bool).
/// - UNVERIFIED VTOL VR members, looked up by name through reflection so a
///   missing or renamed member only disables that property:
///   FlightInfo.radarAltitude (Actor.flightInfo or a FlightInfo component),
///   FuelTank.fuel, ModuleEngine.finalThrottle/throttle,
///   LockingRadar.IsLocked()/currentLock(.actor), Radar.radarEnabled/radarRange,
///   SAMLauncher/IRSamLauncher range members (see candidate lists below).
/// </summary>
internal sealed class VtolFlightDataAdapter
{
    private static readonly string[] AglMembers = { "radarAltitude", "radarAlt" };
    private static readonly string[] FuelMembers = { "fuel", "currentFuel" };
    private static readonly string[] ThrottleMembers = { "finalThrottle", "throttle", "inputThrottle" };
    private static readonly string[] LockMembers = { "currentLock" };
    private static readonly string[] LockActorMembers = { "actor" };
    private static readonly string[] RadarEnabledMembers = { "radarEnabled" };
    private static readonly string[] RadarRangeMembers = { "radarRange", "detectionRange", "maxRange" };
    private static readonly string[] EngagementRangeMembers = { "engageRange", "fireRange", "maxEngageRange", "maxRange", "range" };
    private static readonly string[] VerticalRangeMembers = { "maxEngageAltitude", "maxAltitude", "engageAltitude" };
    private static readonly string[] LauncherTypeNames = { "SAMLauncher", "IRSamLauncher" };

    private readonly VTMapManager _mapManager;
    private readonly EntityRegistry _registry;
    private readonly bool _flightData;
    private readonly bool _engagement;
    private readonly bool _sensors;
    private readonly TelemetryRateGate _gate;
    private readonly Dictionary<Actor, ActorProbe> _probes = new Dictionary<Actor, ActorProbe>();
    private readonly List<Actor> _staleActors = new List<Actor>();
    private readonly HashSet<Actor> _seen = new HashSet<Actor>();
    private readonly Type _flightInfoType;
    private readonly Type _fuelTankType;
    private readonly Type _engineType;
    private readonly Type _lockingRadarType;
    private readonly Type _radarType;
    private readonly List<Type> _launcherTypes = new List<Type>();
    private double _nextWarningTime;

    public VtolFlightDataAdapter(
        VTMapManager mapManager,
        EntityRegistry registry,
        bool flightData,
        bool engagement,
        bool sensors,
        double rateHz)
    {
        _mapManager = mapManager ?? throw new ArgumentNullException(nameof(mapManager));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _flightData = flightData;
        _engagement = engagement;
        _sensors = sensors;
        _gate = new TelemetryRateGate(rateHz);

        Assembly game = typeof(Actor).Assembly;
        _flightInfoType = FindType(game, "FlightInfo");
        _fuelTankType = FindType(game, "FuelTank");
        _engineType = FindType(game, "ModuleEngine");
        _lockingRadarType = FindType(game, "LockingRadar");
        _radarType = FindType(game, "Radar");
        foreach (string name in LauncherTypeNames)
        {
            Type type = FindType(game, name);
            if (type != null)
            {
                _launcherTypes.Add(type);
            }
        }

        Log("Flight data export: " +
            $"flight={_flightData}, engagement={_engagement}, sensors={_sensors}, rate={rateHz:0.##} Hz; " +
            $"types found: FlightInfo={_flightInfoType != null}, FuelTank={_fuelTankType != null}, " +
            $"ModuleEngine={_engineType != null}, LockingRadar={_lockingRadarType != null}, " +
            $"Radar={_radarType != null}, launchers={_launcherTypes.Count}.");
    }

    public bool IsEnabled => _flightData || _engagement || _sensors;

    /// <summary>True when a sampling pass is due (rate-limited).</summary>
    public bool TryBeginPass(double nowSeconds)
    {
        return IsEnabled && _gate.TryEnter(nowSeconds);
    }

    public void Collect(List<Actor> actors, List<KeyValuePair<ulong, TacviewTelemetry>> destination)
    {
        if (actors == null || destination == null)
        {
            return;
        }

        _seen.Clear();
        for (int index = 0; index < actors.Count; index++)
        {
            Actor actor = actors[index];
            if (actor == null || !actor.alive || !_registry.TryGet(actor, out TrackedEntity entity))
            {
                continue;
            }

            _seen.Add(actor);
            try
            {
                TacviewTelemetry sample = Sample(actor, entity);
                if (sample != null && !sample.IsEmpty)
                {
                    destination.Add(new KeyValuePair<ulong, TacviewTelemetry>(entity.ObjectId, sample));
                }
            }
            catch (Exception ex)
            {
                WarnThrottled($"Flight data skipped for {entity.ObjectId:X}: {ex.Message}");
            }
        }

        _staleActors.Clear();
        foreach (Actor actor in _probes.Keys)
        {
            if (!_seen.Contains(actor))
            {
                _staleActors.Add(actor);
            }
        }

        for (int index = 0; index < _staleActors.Count; index++)
        {
            _probes.Remove(_staleActors[index]);
        }

        _staleActors.Clear();
        _seen.Clear();
    }

    public void Clear()
    {
        _probes.Clear();
        _seen.Clear();
        _staleActors.Clear();
    }

    private TacviewTelemetry Sample(Actor actor, TrackedEntity entity)
    {
        bool isAircraft = entity.Kind == EntityKind.Aircraft;
        bool isSurface = entity.Kind == EntityKind.Ground || entity.Kind == EntityKind.Sea;
        if (!isAircraft && !isSurface)
        {
            return null;
        }

        if (!_probes.TryGetValue(actor, out ActorProbe probe))
        {
            probe = BuildProbe(actor, isAircraft, isSurface);
            _probes.Add(actor, probe);
        }

        var sample = new TacviewTelemetry();
        if (_flightData && isAircraft)
        {
            SampleFlight(actor, probe, sample);
        }

        if (_engagement)
        {
            SampleLock(probe, sample);
        }

        if (_sensors && isSurface)
        {
            SampleSensors(probe, sample);
        }

        return sample;
    }

    private void SampleFlight(Actor actor, ActorProbe probe, TacviewTelemetry sample)
    {
        Rigidbody body = probe.Body;
        if (body != null && actor.transform != null)
        {
            Vector3 velocity = body.velocity;
            double tas = velocity.magnitude;
            if (!double.IsNaN(tas) && !double.IsInfinity(tas))
            {
                // No wind model: TAS is the speed relative to the ground frame.
                double altitude = _mapManager.WorldPositionToGPSCoords(actor.transform.position).z;
                sample.Set(TacviewTelemetryProperty.TAS, tas);
                sample.Set(TacviewTelemetryProperty.Mach, FlightDataMath.Mach(tas, altitude));
                sample.Set(TacviewTelemetryProperty.IAS, FlightDataMath.IndicatedAirspeed(tas, altitude));
                if (tas > 5.0)
                {
                    Vector3 local = actor.transform.InverseTransformDirection(velocity);
                    sample.Set(TacviewTelemetryProperty.AOA, FlightDataMath.AngleOfAttack(local.x, local.y, local.z));
                    sample.Set(TacviewTelemetryProperty.AOS, FlightDataMath.AngleOfSideslip(local.x, local.y, local.z));
                }
            }
        }

        double? agl = ReadNumber(probe.FlightInfo, AglMembers);
        if (agl.HasValue && agl.Value >= 0.0 && agl.Value < 100000.0)
        {
            sample.Set(TacviewTelemetryProperty.AGL, agl.Value);
        }

        if (probe.FuelTanks.Count > 0)
        {
            double total = 0.0;
            bool any = false;
            for (int index = 0; index < probe.FuelTanks.Count; index++)
            {
                double? fuel = ReadNumber(probe.FuelTanks[index], FuelMembers);
                if (fuel.HasValue && fuel.Value >= 0.0)
                {
                    total += fuel.Value;
                    any = true;
                }
            }

            if (any)
            {
                sample.Set(TacviewTelemetryProperty.FuelVolume, total);
            }
        }

        if (probe.Engines.Count > 0)
        {
            double total = 0.0;
            int count = 0;
            for (int index = 0; index < probe.Engines.Count; index++)
            {
                double? throttle = ReadNumber(probe.Engines[index], ThrottleMembers);
                if (throttle.HasValue && throttle.Value >= 0.0 && throttle.Value <= 2.0)
                {
                    total += throttle.Value;
                    count++;
                }
            }

            if (count > 0)
            {
                sample.Set(TacviewTelemetryProperty.Throttle, total / count);
            }
        }
    }

    private void SampleLock(ActorProbe probe, TacviewTelemetry sample)
    {
        if (probe.LockingRadars.Count == 0)
        {
            return;
        }

        for (int index = 0; index < probe.LockingRadars.Count; index++)
        {
            Component radar = probe.LockingRadars[index];
            if (radar == null)
            {
                continue;
            }

            bool? locked = InvokeBool(radar, "IsLocked");
            object lockData = ReadObject(radar, LockMembers);
            Actor target = ReadObject(lockData, LockActorMembers) as Actor;
            if (locked == false || target == null || !target.alive)
            {
                continue;
            }

            sample.SetLock(true, ResolveObjectId(target));
            return;
        }

        sample.SetLock(false, null);
    }

    private void SampleSensors(ActorProbe probe, TacviewTelemetry sample)
    {
        for (int index = 0; index < probe.Radars.Count; index++)
        {
            Component radar = probe.Radars[index];
            if (radar == null)
            {
                continue;
            }

            bool? enabled = ReadBool(radar, RadarEnabledMembers);
            if (enabled.HasValue)
            {
                sample.Set(TacviewTelemetryProperty.RadarMode, enabled.Value ? 1.0 : 0.0);
            }

            SetRange(sample, TacviewTelemetryProperty.RadarRange, ReadNumber(radar, RadarRangeMembers));
            break;
        }

        for (int index = 0; index < probe.Launchers.Count; index++)
        {
            Component launcher = probe.Launchers[index];
            if (launcher == null)
            {
                continue;
            }

            SetRange(sample, TacviewTelemetryProperty.EngagementRange, ReadNumber(launcher, EngagementRangeMembers));
            SetRange(sample, TacviewTelemetryProperty.VerticalEngagementRange, ReadNumber(launcher, VerticalRangeMembers));
            break;
        }
    }

    private static void SetRange(TacviewTelemetry sample, TacviewTelemetryProperty property, double? meters)
    {
        // Plausibility guard against a member with another meaning or unit.
        if (meters.HasValue && meters.Value > 10.0 && meters.Value < 1000000.0)
        {
            sample.Set(property, meters.Value);
        }
    }

    private ActorProbe BuildProbe(Actor actor, bool isAircraft, bool isSurface)
    {
        var probe = new ActorProbe();
        if (isAircraft && _flightData)
        {
            // Explicit Unity null checks: '??' bypasses UnityEngine.Object equality.
            Rigidbody body = actor.GetComponent<Rigidbody>();
            if (body == null)
            {
                body = actor.GetComponentInParent<Rigidbody>();
            }

            probe.Body = body == null ? null : body;
            probe.FlightInfo = ReadObject(actor, new[] { "flightInfo" }) ?? FirstComponent(actor, _flightInfoType);
            AddComponents(actor, _fuelTankType, probe.FuelTanks);
            AddComponents(actor, _engineType, probe.Engines);
        }

        if (_engagement)
        {
            AddComponents(actor, _lockingRadarType, probe.LockingRadars);
        }

        if (isSurface && _sensors)
        {
            AddComponents(actor, _radarType, probe.Radars);
            foreach (Type launcherType in _launcherTypes)
            {
                AddComponents(actor, launcherType, probe.Launchers);
            }
        }

        return probe;
    }

    private ulong? ResolveObjectId(Actor actor)
    {
        Actor current = actor;
        while (current != null && current.parentActor != null && !ReferenceEquals(current.parentActor, current))
        {
            current = current.parentActor;
        }

        return current != null && _registry.TryGet(current, out TrackedEntity entity)
            ? entity.ObjectId
            : (ulong?)null;
    }

    private static Type FindType(Assembly assembly, string name)
    {
        try
        {
            return assembly.GetType(name, false);
        }
        catch
        {
            return null;
        }
    }

    private static object FirstComponent(Actor actor, Type type)
    {
        var list = new List<Component>();
        AddComponents(actor, type, list);
        return list.Count > 0 ? list[0] : null;
    }

    private static void AddComponents(Actor actor, Type type, List<Component> destination)
    {
        if (type == null || actor == null || !typeof(Component).IsAssignableFrom(type))
        {
            return;
        }

        try
        {
            Component[] components = actor.GetComponentsInChildren(type, true);
            if (components == null)
            {
                return;
            }

            for (int index = 0; index < components.Length; index++)
            {
                if (components[index] != null)
                {
                    destination.Add(components[index]);
                }
            }
        }
        catch
        {
            // Probe stays empty for this component type.
        }
    }

    private static double? ReadNumber(object target, string[] names)
    {
        object value = ReadObject(target, names);
        try
        {
            switch (value)
            {
                case float single:
                    return single;
                case double number:
                    return number;
                case int integer:
                    return integer;
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadBool(object target, string[] names)
    {
        return ReadObject(target, names) is bool value ? value : (bool?)null;
    }

    private static object ReadObject(object target, string[] names)
    {
        if (target == null || (target is UnityEngine.Object unityObject && unityObject == null))
        {
            return null;
        }

        MemberAccessor accessor = MemberAccessor.Get(target.GetType(), names);
        return accessor?.Read(target);
    }

    private static bool? InvokeBool(object target, string methodName)
    {
        if (target == null)
        {
            return null;
        }

        try
        {
            MethodInfo method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            return method != null && method.ReturnType == typeof(bool)
                ? (bool)method.Invoke(target, null)
                : (bool?)null;
        }
        catch
        {
            return null;
        }
    }

    private void WarnThrottled(string message)
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (now < _nextWarningTime)
        {
            return;
        }

        _nextWarningTime = now + 10.0;
        LogWarn(message);
    }

    private sealed class ActorProbe
    {
        public Rigidbody Body;
        public object FlightInfo;
        public readonly List<Component> FuelTanks = new List<Component>();
        public readonly List<Component> Engines = new List<Component>();
        public readonly List<Component> LockingRadars = new List<Component>();
        public readonly List<Component> Radars = new List<Component>();
        public readonly List<Component> Launchers = new List<Component>();
    }

    /// <summary>Cached field-or-property reader for the first matching member name.</summary>
    private sealed class MemberAccessor
    {
        private static readonly Dictionary<string, MemberAccessor> Cache = new Dictionary<string, MemberAccessor>();
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly FieldInfo _field;
        private readonly PropertyInfo _property;

        private MemberAccessor(FieldInfo field, PropertyInfo property)
        {
            _field = field;
            _property = property;
        }

        public static MemberAccessor Get(Type type, string[] names)
        {
            string key = type.FullName + "|" + string.Join(",", names);
            if (Cache.TryGetValue(key, out MemberAccessor cached))
            {
                return cached;
            }

            MemberAccessor accessor = null;
            try
            {
                foreach (string name in names)
                {
                    FieldInfo field = type.GetField(name, Flags);
                    if (field != null)
                    {
                        accessor = new MemberAccessor(field, null);
                        break;
                    }

                    PropertyInfo property = type.GetProperty(name, Flags);
                    if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                    {
                        accessor = new MemberAccessor(null, property);
                        break;
                    }
                }
            }
            catch
            {
                accessor = null;
            }

            Cache[key] = accessor;
            return accessor;
        }

        public object Read(object target)
        {
            try
            {
                return _field != null ? _field.GetValue(target) : _property.GetValue(target, null);
            }
            catch
            {
                return null;
            }
        }
    }
}
