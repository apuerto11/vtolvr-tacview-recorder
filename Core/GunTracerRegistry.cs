using System;
using System.Collections.Generic;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// Explicit, bounded policy for live gun bullets.  A tracer represents one
/// sampled physical Bullet instance, not every physical round fired by a
/// weapon.  Its lifetime is controlled by the game's Bullet lifecycle; this
/// class deliberately has no synthetic timeout.
/// </summary>
public sealed class GunTracerSettings
{
    public const int DefaultMaximumTracersPerGunPerSecond = 10;
    public const int DefaultMaximumTracersPerSecond = 60;
    public const int DefaultActiveTracerCap = 32;
    public const double DefaultSampleIntervalSeconds = 0.025;

    public GunTracerSettings(
        int maximumTracersPerGunPerSecond = DefaultMaximumTracersPerGunPerSecond,
        int maximumTracersPerSecond = DefaultMaximumTracersPerSecond,
        int activeTracerCap = DefaultActiveTracerCap,
        double sampleIntervalSeconds = DefaultSampleIntervalSeconds)
    {
        if (maximumTracersPerGunPerSecond < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTracersPerGunPerSecond));
        }

        if (maximumTracersPerSecond < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTracersPerSecond));
        }

        if (activeTracerCap < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(activeTracerCap));
        }

        if (!IsFinitePositive(sampleIntervalSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleIntervalSeconds));
        }

        MaximumTracersPerGunPerSecond = maximumTracersPerGunPerSecond;
        MaximumTracersPerSecond = maximumTracersPerSecond;
        ActiveTracerCap = activeTracerCap;
        SampleIntervalSeconds = sampleIntervalSeconds;
    }

    public int MaximumTracersPerGunPerSecond { get; }

    public int MaximumTracersPerSecond { get; }

    public int ActiveTracerCap { get; }

    public double SampleIntervalSeconds { get; }

    private static bool IsFinitePositive(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0.0;
    }
}

/// <summary>
/// Immutable adapter input for one independently observed live Bullet.
/// The adapter must supply the actual Bullet transform at spawn and a source
/// that samples that same Bullet instance; this core deliberately never
/// substitutes an owner transform or extrapolates a synthetic trajectory.
/// </summary>
public sealed class GunTracerSpawnRequest
{
    public GunTracerSpawnRequest(
        object emitterIdentity,
        string name,
        string coalition,
        string color,
        EntityTransform spawnTransform,
        IEntityTransformSource trajectorySource,
        ulong? parentObjectId)
    {
        EmitterIdentity = emitterIdentity ?? throw new ArgumentNullException(nameof(emitterIdentity));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A non-empty gun tracer name is required.", nameof(name));
        }

        if (!spawnTransform.HasFiniteValues)
        {
            throw new ArgumentOutOfRangeException(
                nameof(spawnTransform),
                "A gun tracer spawn transform must be finite.");
        }

        TrajectorySource = trajectorySource ?? throw new ArgumentNullException(nameof(trajectorySource));
        if (parentObjectId.HasValue && parentObjectId.Value == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parentObjectId),
                "Global ACMI object id 0 cannot be a gun tracer parent.");
        }

        Name = name.Trim();
        Coalition = coalition;
        Color = color;
        SpawnTransform = spawnTransform;
        ParentObjectId = parentObjectId;
    }

    /// <summary>
    /// Reference identity of the concrete Gun, VTOLCannon, or other real
    /// emitter. It is used only for the per-emitter capture budget; every
    /// accepted Bullet receives a fresh opaque session identity.
    /// </summary>
    public object EmitterIdentity { get; }

    public string Name { get; }

    public string Coalition { get; }

    public string Color { get; }

    public EntityTransform SpawnTransform { get; }

    public IEntityTransformSource TrajectorySource { get; }

    public ulong? ParentObjectId { get; }
}

/// <summary>
/// Per-shot opaque handle.  It never reuses a game component identity, so
/// multiple samples from one gun remain distinct ACMI objects.
/// </summary>
public sealed class GunTracerHandle
{
    internal GunTracerHandle()
    {
    }

    public ulong ObjectId { get; private set; }

    public TrackedEntity Entity { get; private set; }

    internal void SetEntity(TrackedEntity entity)
    {
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        ObjectId = entity.ObjectId;
    }
}

/// <summary>
/// Pure .NET lifecycle for sampled gun bullets.  Its quotas are intentionally
/// separate from missiles and rockets: bullets must never consume the recorder's
/// existing 64 active-weapon slots.
/// </summary>
public sealed class GunTracerRegistry
{
    private readonly EntityRegistry _entityRegistry;
    private readonly GunTracerSettings _settings;
    private readonly Dictionary<GunTracerHandle, TrackedGunTracer> _entries =
        new Dictionary<GunTracerHandle, TrackedGunTracer>();
    private readonly Dictionary<object, Queue<double>> _emitterEmissionTimes =
        new Dictionary<object, Queue<double>>(ReferenceEqualityComparer.Instance);
    private readonly Queue<double> _globalEmissionTimes = new Queue<double>();
    private readonly List<GunTracerHandle> _entriesToRemove = new List<GunTracerHandle>();

    public GunTracerRegistry(EntityRegistry entityRegistry, GunTracerSettings settings = null)
    {
        _entityRegistry = entityRegistry ?? throw new ArgumentNullException(nameof(entityRegistry));
        _settings = settings ?? new GunTracerSettings();
    }

    public int ActiveTracerCount => _entries.Count;

    /// <summary>
    /// Diagnostic count for bounded emitter-state tests. It excludes active
    /// tracer entries and must fall when an adapter releases a removed emitter.
    /// </summary>
    public int RememberedEmitterCount => _emitterEmissionTimes.Count;

    public GunTracerSettings Settings => _settings;

    /// <summary>
    /// Attempts to record one actual observed Bullet emission. A rejection is
    /// silent and non-destructive: it simply means that this physical shot was
    /// outside its per-emitter, global, or active budget. No fallback sample is
    /// created when a real hook is unavailable.
    /// </summary>
    public bool TrySpawn(
        GunTracerSpawnRequest request,
        double nowSeconds,
        out GunTracerHandle handle)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ValidateFinite(nowSeconds, nameof(nowSeconds));
        handle = null;
        PruneEmissionTimes(_globalEmissionTimes, nowSeconds);

        if (_entries.Count >= _settings.ActiveTracerCap ||
            _globalEmissionTimes.Count >= _settings.MaximumTracersPerSecond)
        {
            return false;
        }

        if (!_emitterEmissionTimes.TryGetValue(request.EmitterIdentity, out Queue<double> emitterTimes))
        {
            emitterTimes = new Queue<double>();
            _emitterEmissionTimes.Add(request.EmitterIdentity, emitterTimes);
        }

        PruneEmissionTimes(emitterTimes, nowSeconds);
        if (emitterTimes.Count >= _settings.MaximumTracersPerGunPerSecond)
        {
            return false;
        }

        GunTracerHandle candidate = null;
        bool registered = false;
        try
        {
            candidate = new GunTracerHandle();
            EntityMetadata metadata = GunTracerMetadataFactory.Create(
                request.Name,
                request.Coalition,
                request.Color,
                request.ParentObjectId);
            TrackedEntity entity = _entityRegistry.Register(
                candidate,
                EntityKind.Projectile,
                false,
                metadata);
            registered = true;
            candidate.SetEntity(entity);
            _entries.Add(candidate, new TrackedGunTracer(
                candidate,
                request.SpawnTransform,
                request.TrajectorySource,
                nowSeconds,
                _settings.SampleIntervalSeconds));
            emitterTimes.Enqueue(nowSeconds);
            _globalEmissionTimes.Enqueue(nowSeconds);
            handle = candidate;
            return true;
        }
        catch
        {
            if (registered)
            {
                _entityRegistry.Remove(candidate, out TrackedEntity _);
            }

            return false;
        }
    }

    /// <summary>
    /// Starts an ACMI-frame boundary.  At most one lifecycle update per tracer
    /// can be emitted until the next boundary, preserving Spawn -> final ->
    /// Delete sequencing for even very short-lived samples.
    /// </summary>
    public void BeginFrame()
    {
        foreach (TrackedGunTracer tracer in _entries.Values)
        {
            tracer.BeginFrame();
        }
    }

    /// <summary>
    /// Samples the actual live Bullet transforms.  No Fired, Hit, Destroyed,
    /// or Timeout events are synthesized for bullets; termination must come
    /// from Bullet.KillBullet, Bullet.OnDisable, or the recorder's final drain.
    /// </summary>
    public void Refresh(double nowSeconds)
    {
        ValidateFinite(nowSeconds, nameof(nowSeconds));
        foreach (TrackedGunTracer tracer in _entries.Values)
        {
            if (tracer.HasPendingRemoval)
            {
                continue;
            }

            if (!tracer.IsSampleDue(nowSeconds))
            {
                continue;
            }

            tracer.CompleteSamplingAttempt(nowSeconds, TryCaptureTransform(tracer.TrajectorySource));
        }
    }

    /// <summary>
    /// Ends a tracked Bullet generation at an endpoint supplied by the real
    /// game lifecycle. The endpoint is emitted once, followed by an eventless
    /// Delete on a later drain. A null endpoint uses the last live transform
    /// observed from that same Bullet instance.
    /// </summary>
    public bool Terminate(
        GunTracerHandle handle,
        EntityTransform? finalTransform = null)
    {
        if (handle == null || !_entries.TryGetValue(handle, out TrackedGunTracer tracer))
        {
            return false;
        }

        if (finalTransform.HasValue && !finalTransform.Value.HasFiniteValues)
        {
            finalTransform = null;
        }

        tracer.MarkForRemoval(finalTransform ?? TryCaptureTransform(tracer.TrajectorySource));
        return true;
    }

    /// <summary>
    /// Marks active samples for a bounded, eventless final drain.  The caller
    /// must repeat BeginFrame + DrainFrameDataTo until the active count is zero.
    /// </summary>
    public void PrepareFinalDrain()
    {
        foreach (TrackedGunTracer tracer in _entries.Values)
        {
            if (!tracer.HasPendingRemoval)
            {
                tracer.MarkForRemoval(TryCaptureTransform(tracer.TrajectorySource));
            }
        }
    }

    public void DrainFrameDataTo(List<TacviewEntityUpdate> destination)
    {
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        _entriesToRemove.Clear();
        foreach (KeyValuePair<GunTracerHandle, TrackedGunTracer> pair in _entries)
        {
            TrackedGunTracer tracer = pair.Value;
            if (tracer.TryDequeue(out TacviewEntityUpdate update))
            {
                destination.Add(update);
            }

            if (tracer.IsTerminal)
            {
                _entriesToRemove.Add(pair.Key);
            }
        }

        for (int index = 0; index < _entriesToRemove.Count; index++)
        {
            GunTracerHandle handle = _entriesToRemove[index];
            _entries.Remove(handle);
            _entityRegistry.Remove(handle, out TrackedEntity _);
        }

        _entriesToRemove.Clear();
    }

    public void Clear()
    {
        foreach (GunTracerHandle handle in _entries.Keys)
        {
            _entityRegistry.Remove(handle, out TrackedEntity _);
        }

        _entries.Clear();
        _entriesToRemove.Clear();
        _emitterEmissionTimes.Clear();
        _globalEmissionTimes.Clear();
    }

    /// <summary>
    /// Releases only the per-emitter cadence state when a Unity component is
    /// removed. Existing tracer entries deliberately remain alive long enough
    /// to emit their normal final snapshot and eventless Delete.
    /// </summary>
    public void ForgetEmitter(object emitterIdentity)
    {
        if (emitterIdentity == null)
        {
            return;
        }

        _emitterEmissionTimes.Remove(emitterIdentity);
    }

    private void PruneEmissionTimes(Queue<double> emissionTimes, double nowSeconds)
    {
        const double OneSecond = 1.0;
        while (emissionTimes.Count > 0 && nowSeconds - emissionTimes.Peek() >= OneSecond)
        {
            emissionTimes.Dequeue();
        }
    }

    private static EntityTransform? TryCaptureTransform(IEntityTransformSource source)
    {
        try
        {
            return source.TryGetTransform(out EntityTransform transform) && transform.HasFiniteValues
                ? transform
                : (EntityTransform?)null;
        }
        catch
        {
            return null;
        }
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Gun tracer time must be finite.");
        }
    }

    private sealed class TrackedGunTracer
    {
        private readonly EntityTransform _spawnTransform;
        private readonly double _sampleIntervalSeconds;
        private EntityTransform? _latestTransform;
        private EntityTransform? _pendingRegularTransform;
        private EntityTransform? _finalTransform;
        private double _nextSampleTimeSeconds;
        private bool _spawnEmitted;
        private bool _finalSnapshotEmitted;
        private bool _pendingRemoval;
        private bool _canDrainThisFrame = true;

        public TrackedGunTracer(
            GunTracerHandle handle,
            EntityTransform spawnTransform,
            IEntityTransformSource trajectorySource,
            double spawnedAtSeconds,
            double sampleIntervalSeconds)
        {
            Handle = handle ?? throw new ArgumentNullException(nameof(handle));
            TrajectorySource = trajectorySource ?? throw new ArgumentNullException(nameof(trajectorySource));
            _spawnTransform = spawnTransform;
            _latestTransform = spawnTransform;
            _sampleIntervalSeconds = sampleIntervalSeconds;
            _nextSampleTimeSeconds = spawnedAtSeconds + sampleIntervalSeconds;
        }

        public GunTracerHandle Handle { get; }

        public IEntityTransformSource TrajectorySource { get; }

        public bool HasPendingRemoval => _pendingRemoval;

        public bool IsTerminal { get; private set; }

        public void BeginFrame()
        {
            _canDrainThisFrame = true;
        }

        public bool IsSampleDue(double nowSeconds)
        {
            return nowSeconds >= _nextSampleTimeSeconds;
        }

        public void CompleteSamplingAttempt(double nowSeconds, EntityTransform? transform)
        {
            _nextSampleTimeSeconds = GetNextSampleTime(nowSeconds);
            if (!transform.HasValue)
            {
                return;
            }

            _latestTransform = transform.Value;
            _pendingRegularTransform = transform.Value;
        }

        public void MarkForRemoval(EntityTransform? observedFinalTransform)
        {
            if (_pendingRemoval || IsTerminal)
            {
                return;
            }

            _pendingRemoval = true;
            _pendingRegularTransform = null;
            _finalTransform = observedFinalTransform ?? _latestTransform;
        }

        public bool TryDequeue(out TacviewEntityUpdate update)
        {
            update = default;
            if (IsTerminal || !_canDrainThisFrame)
            {
                return false;
            }

            if (!_spawnEmitted)
            {
                _spawnEmitted = true;
                _canDrainThisFrame = false;
                update = TacviewEntityUpdate.Upsert(
                    Handle.ObjectId,
                    _spawnTransform,
                    Handle.Entity.Metadata);
                return true;
            }

            if (_pendingRemoval)
            {
                if (!_finalSnapshotEmitted && _finalTransform.HasValue)
                {
                    _finalSnapshotEmitted = true;
                    _canDrainThisFrame = false;
                    update = TacviewEntityUpdate.Upsert(
                        Handle.ObjectId,
                        _finalTransform.Value,
                        Handle.Entity.Metadata);
                    return true;
                }

                _finalSnapshotEmitted = true;
                _canDrainThisFrame = false;
                IsTerminal = true;
                update = TacviewEntityUpdate.Delete(
                    Handle.ObjectId,
                    TacviewEntityRemovalReason.Deleted,
                    Handle.Entity.Metadata.Name);
                return true;
            }

            if (_pendingRegularTransform.HasValue)
            {
                EntityTransform transform = _pendingRegularTransform.Value;
                _pendingRegularTransform = null;
                _canDrainThisFrame = false;
                update = TacviewEntityUpdate.Upsert(
                    Handle.ObjectId,
                    transform,
                    Handle.Entity.Metadata);
                return true;
            }

            return false;
        }

        private double GetNextSampleTime(double nowSeconds)
        {
            double candidate = _nextSampleTimeSeconds + _sampleIntervalSeconds;
            return candidate > nowSeconds ? candidate : nowSeconds + _sampleIntervalSeconds;
        }
    }
}

/// <summary>
/// Canonical ACMI metadata for a sampled physical Bullet. It intentionally has
/// no event metadata: the trace is not a separate claim about a hit, miss, or
/// round count; its endpoint is only the real Bullet lifecycle endpoint.
/// </summary>
public static class GunTracerMetadataFactory
{
    public const string TacviewType = "Projectile+Bullet";

    public static EntityMetadata Create(
        string name,
        string coalition,
        string color,
        ulong? parentObjectId)
    {
        return new EntityMetadata(
            TacviewType,
            name,
            coalition,
            color,
            parentObjectId: parentObjectId);
    }
}
