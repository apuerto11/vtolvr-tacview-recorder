using System;
using System.Collections.Generic;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// Bounded sampling policy for visible countermeasures. The default 4 Hz rate
/// stays within Tacview's 2-5 Hz Decoy guidance while keeping a separate budget
/// from weapons.
/// </summary>
public sealed class CountermeasureSamplingSettings
{
    public const int DefaultActiveCountermeasureCap = 256;
    public const int DefaultMaximumCountermeasuresPerSalvo = 64;
    public const double DefaultSampleIntervalSeconds = 0.25;

    public CountermeasureSamplingSettings(
        int activeCountermeasureCap = DefaultActiveCountermeasureCap,
        int maximumCountermeasuresPerSalvo = DefaultMaximumCountermeasuresPerSalvo,
        double sampleIntervalSeconds = DefaultSampleIntervalSeconds)
    {
        if (activeCountermeasureCap < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(activeCountermeasureCap));
        }

        if (maximumCountermeasuresPerSalvo < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCountermeasuresPerSalvo));
        }

        if (double.IsNaN(sampleIntervalSeconds) ||
            double.IsInfinity(sampleIntervalSeconds) ||
            sampleIntervalSeconds < 0.2 ||
            sampleIntervalSeconds > 0.5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleIntervalSeconds),
                "Countermeasure sampling must be between 2 Hz and 5 Hz.");
        }

        ActiveCountermeasureCap = activeCountermeasureCap;
        MaximumCountermeasuresPerSalvo = maximumCountermeasuresPerSalvo;
        SampleIntervalSeconds = sampleIntervalSeconds;
    }

    public int ActiveCountermeasureCap { get; }

    public int MaximumCountermeasuresPerSalvo { get; }

    public double SampleIntervalSeconds { get; }
}

/// <summary>
/// Opaque per-release handle. A new instance is created for every release and
/// is deliberately unrelated to the game object's identity, including pooled
/// CMFlare objects.
/// </summary>
public sealed class CountermeasureHandle
{
    internal CountermeasureHandle(CountermeasureKind kind)
    {
        Kind = kind;
    }

    public CountermeasureKind Kind { get; }

    public ulong ObjectId { get; private set; }

    public TrackedEntity Entity { get; private set; }

    internal void SetEntity(TrackedEntity entity)
    {
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        ObjectId = entity.ObjectId;
    }
}

/// <summary>
/// Pure .NET lifecycle and sampling core for chaff and flares. A game-specific
/// adapter feeds it actual release/decay observations; it neither reflects over
/// game types nor scans Unity objects.
/// </summary>
public sealed class CountermeasureRegistry
{
    private readonly EntityRegistry _entityRegistry;
    private readonly CountermeasureSamplingSettings _settings;
    private readonly CountermeasureEntityKindResolver _entityKindResolver;
    private readonly CountermeasureWarningReporter _warnings;
    private readonly Dictionary<CountermeasureHandle, TrackedCountermeasure> _entries =
        new Dictionary<CountermeasureHandle, TrackedCountermeasure>();
    private readonly List<CountermeasureHandle> _entriesToRemove =
        new List<CountermeasureHandle>();
    private double _lastObservedTimeSeconds;

    public CountermeasureRegistry(
        EntityRegistry entityRegistry,
        CountermeasureSamplingSettings settings = null,
        CountermeasureEntityKindResolver entityKindResolver = null,
        WarningThrottle warningThrottle = null,
        ICountermeasureWarningSink warningSink = null)
    {
        _entityRegistry = entityRegistry ?? throw new ArgumentNullException(nameof(entityRegistry));
        _settings = settings ?? new CountermeasureSamplingSettings();
        _entityKindResolver = entityKindResolver ?? DefaultEntityKindResolver;
        _warnings = new CountermeasureWarningReporter(warningThrottle, warningSink);
    }

    /// <summary>
    /// Counts countermeasures only. This is intentionally separate from any
    /// missile/rocket cap maintained by a weapon adapter.
    /// </summary>
    public int ActiveCountermeasureCount => _entries.Count;

    public bool HasPendingLifecycleData
    {
        get
        {
            foreach (TrackedCountermeasure entry in _entries.Values)
            {
                if (entry.HasPendingLifecycleData)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool TrySpawn(
        CountermeasureSpawnRequest request,
        double nowSeconds,
        out CountermeasureHandle handle)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ValidateFinite(nowSeconds, nameof(nowSeconds));
        _lastObservedTimeSeconds = nowSeconds;
        handle = null;

        if (_entries.Count >= _settings.ActiveCountermeasureCap)
        {
            _warnings.Warn(
                CountermeasureWarningKind.ActiveCapReached,
                0,
                nowSeconds,
                $"Countermeasure capture cap ({_settings.ActiveCountermeasureCap}) reached; " +
                $"additional {CountermeasureMetadataFactory.GetName(request.Kind)} releases are skipped.");
            return false;
        }

        CountermeasureHandle candidate = null;
        bool registered = false;
        try
        {
            candidate = new CountermeasureHandle(request.Kind);
            EntityTransform? initialTransform = request.SpawnTransform ??
                TryCaptureTransform(request.Source, 0, nowSeconds);
            EntityMetadata metadata = CountermeasureMetadataFactory.Create(
                request.Kind,
                request.Coalition,
                request.Color,
                request.ParentObjectId);
            EntityKind entityKind = _entityKindResolver(request.Kind);
            TrackedEntity entity = _entityRegistry.Register(
                candidate,
                entityKind,
                false,
                metadata);
            registered = true;
            candidate.SetEntity(entity);

            _entries.Add(candidate, new TrackedCountermeasure(
                candidate,
                request,
                initialTransform,
                nowSeconds,
                _settings.SampleIntervalSeconds));
            handle = candidate;
            return true;
        }
        catch (Exception ex)
        {
            if (registered)
            {
                _entityRegistry.Remove(candidate, out TrackedEntity _);
            }

            _warnings.Warn(
                CountermeasureWarningKind.SpawnRejected,
                0,
                nowSeconds,
                $"Countermeasure release was not recorded: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Records a bounded set of independently observed releases. The caller
    /// retains each returned handle and must use that handle, not a pooled game
    /// object, when reporting later decay.
    /// </summary>
    public int TrySpawnSalvo(
        IReadOnlyList<CountermeasureSpawnRequest> requests,
        double nowSeconds,
        List<CountermeasureHandle> destination)
    {
        if (requests == null)
        {
            throw new ArgumentNullException(nameof(requests));
        }

        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        ValidateFinite(nowSeconds, nameof(nowSeconds));
        _lastObservedTimeSeconds = nowSeconds;
        int count = Math.Min(requests.Count, _settings.MaximumCountermeasuresPerSalvo);
        if (requests.Count > count)
        {
            _warnings.Warn(
                CountermeasureWarningKind.SalvoCapReached,
                0,
                nowSeconds,
                $"Countermeasure salvo is limited to {count} releases; " +
                $"{requests.Count - count} releases were skipped.");
        }

        int recorded = 0;
        for (int index = 0; index < count; index++)
        {
            CountermeasureSpawnRequest request = requests[index];
            if (request == null)
            {
                _warnings.Warn(
                    CountermeasureWarningKind.SpawnRejected,
                    0,
                    nowSeconds,
                    "A null countermeasure salvo entry was skipped.");
                continue;
            }

            if (TrySpawn(request, nowSeconds, out CountermeasureHandle handle))
            {
                destination.Add(handle);
                recorded++;
                continue;
            }

            if (_entries.Count >= _settings.ActiveCountermeasureCap)
            {
                break;
            }
        }

        return recorded;
    }

    /// <summary>
    /// Starts a new ACMI-frame collection boundary. At most one update per
    /// countermeasure is emitted between boundaries, avoiding duplicate object
    /// ids in an ACMI frame while preserving ordered Spawn -> final -> Delete.
    /// </summary>
    public void BeginFrame()
    {
        foreach (TrackedCountermeasure entry in _entries.Values)
        {
            entry.BeginFrame();
        }
    }

    /// <summary>
    /// Performs due 2-5 Hz samples and evidence-based auto-expiry. Failures of
    /// one source are contained and cannot prevent other releases from sampling.
    /// </summary>
    public void Refresh(double nowSeconds)
    {
        ValidateFinite(nowSeconds, nameof(nowSeconds));
        _lastObservedTimeSeconds = nowSeconds;

        foreach (TrackedCountermeasure entry in _entries.Values)
        {
            try
            {
                RefreshEntry(entry, nowSeconds);
            }
            catch (Exception ex)
            {
                _warnings.Warn(
                    CountermeasureWarningKind.SourceFault,
                    entry.Handle.ObjectId,
                    nowSeconds,
                    $"{entry.Handle.Kind} {entry.Handle.ObjectId:X} refresh failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reports an observed decay/removal. If no final transform is supplied,
    /// the core attempts the request's actual source and otherwise uses only the
    /// last known transform; it never manufactures an owner-based position.
    /// </summary>
    public bool MarkForRemoval(
        CountermeasureHandle handle,
        double nowSeconds,
        EntityTransform? finalTransform = null,
        TacviewEntityRemovalReason removalReason = TacviewEntityRemovalReason.Deleted)
    {
        if (handle == null)
        {
            throw new ArgumentNullException(nameof(handle));
        }

        ValidateFinite(nowSeconds, nameof(nowSeconds));
        _lastObservedTimeSeconds = nowSeconds;
        if (finalTransform.HasValue && !finalTransform.Value.HasFiniteValues)
        {
            _warnings.Warn(
                CountermeasureWarningKind.NonFiniteTransform,
                handle.ObjectId,
                nowSeconds,
                $"{handle.Kind} {handle.ObjectId:X} decay transform was non-finite and was ignored.");
            finalTransform = null;
        }

        if (!_entries.TryGetValue(handle, out TrackedCountermeasure entry))
        {
            return false;
        }

        EntityTransform? observedFinal = finalTransform ??
            TryCaptureTransform(entry.Source, handle.ObjectId, nowSeconds);
        return entry.MarkForRemoval(removalReason, observedFinal);
    }

    /// <summary>
    /// Marks all active releases for orderly finalization. The caller should
    /// repeatedly call BeginFrame + DrainFrameDataTo until this registry becomes
    /// empty; this retains short-lived Spawn -> final -> Delete ordering.
    /// </summary>
    public void PrepareFinalDrain(double nowSeconds)
    {
        ValidateFinite(nowSeconds, nameof(nowSeconds));
        _lastObservedTimeSeconds = nowSeconds;

        foreach (TrackedCountermeasure entry in _entries.Values)
        {
            if (entry.HasPendingRemoval)
            {
                continue;
            }

            EntityTransform? finalTransform = TryCaptureTransform(
                entry.Source,
                entry.Handle.ObjectId,
                nowSeconds);
            entry.MarkForRemoval(TacviewEntityRemovalReason.Deleted, finalTransform);
        }
    }

    /// <summary>
    /// Appends at most one lifecycle update for every active release. Finished
    /// releases are removed from the shared session registry only after their
    /// deletion record has been queued.
    /// </summary>
    public void DrainFrameDataTo(List<TacviewEntityUpdate> destination)
    {
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        _entriesToRemove.Clear();
        foreach (KeyValuePair<CountermeasureHandle, TrackedCountermeasure> pair in _entries)
        {
            TrackedCountermeasure entry = pair.Value;
            if (entry.TryDequeue(out TacviewEntityUpdate update))
            {
                destination.Add(update);
            }

            if (entry.WasSuppressed)
            {
                _warnings.Warn(
                    CountermeasureWarningKind.LifecycleSuppressed,
                    entry.Handle.ObjectId,
                    _lastObservedTimeSeconds,
                    $"{entry.Handle.Kind} {entry.Handle.ObjectId:X} ended without a valid transform; " +
                    "its ACMI delete was suppressed.");
            }

            if (entry.IsTerminal)
            {
                _entriesToRemove.Add(pair.Key);
            }
        }

        for (int index = 0; index < _entriesToRemove.Count; index++)
        {
            CountermeasureHandle handle = _entriesToRemove[index];
            _entries.Remove(handle);
            _entityRegistry.Remove(handle, out TrackedEntity _);
            _warnings.Forget(handle.ObjectId);
        }

        _entriesToRemove.Clear();
    }

    public void Clear()
    {
        foreach (CountermeasureHandle handle in _entries.Keys)
        {
            _entityRegistry.Remove(handle, out TrackedEntity _);
            _warnings.Forget(handle.ObjectId);
        }

        _entries.Clear();
        _entriesToRemove.Clear();
    }

    private void RefreshEntry(TrackedCountermeasure entry, double nowSeconds)
    {
        if (entry.HasPendingRemoval)
        {
            return;
        }

        if (entry.HasExpired(nowSeconds))
        {
            EntityTransform? finalTransform = TryCaptureTransform(
                entry.Source,
                entry.Handle.ObjectId,
                nowSeconds);
            entry.MarkForRemoval(entry.ExpiryRemovalReason, finalTransform);
            return;
        }

        if (entry.Source == null || !entry.IsSampleDue(nowSeconds))
        {
            return;
        }

        EntityTransform? sampledTransform = TryCaptureTransform(
            entry.Source,
            entry.Handle.ObjectId,
            nowSeconds);
        entry.CompleteSamplingAttempt(nowSeconds, sampledTransform);
    }

    private EntityTransform? TryCaptureTransform(
        IEntityTransformSource source,
        ulong objectId,
        double nowSeconds)
    {
        if (source == null)
        {
            return null;
        }

        try
        {
            if (!source.TryGetTransform(out EntityTransform transform))
            {
                _warnings.Warn(
                    CountermeasureWarningKind.SourceUnavailable,
                    objectId,
                    nowSeconds,
                    objectId == 0
                        ? "Countermeasure spawn transform is unavailable."
                        : $"Countermeasure {objectId:X} transform is unavailable.");
                return null;
            }

            if (!transform.HasFiniteValues)
            {
                _warnings.Warn(
                    CountermeasureWarningKind.NonFiniteTransform,
                    objectId,
                    nowSeconds,
                    objectId == 0
                        ? "Countermeasure spawn transform is non-finite."
                        : $"Countermeasure {objectId:X} transform is non-finite.");
                return null;
            }

            return transform;
        }
        catch (Exception ex)
        {
            _warnings.Warn(
                CountermeasureWarningKind.SourceFault,
                objectId,
                nowSeconds,
                objectId == 0
                    ? $"Countermeasure spawn transform failed: {ex.Message}"
                    : $"Countermeasure {objectId:X} transform failed: {ex.Message}");
            return null;
        }
    }

    private static EntityKind DefaultEntityKindResolver(CountermeasureKind kind)
    {
        switch (kind)
        {
            case CountermeasureKind.Flare:
                return EntityKind.Flare;
            case CountermeasureKind.Chaff:
                return EntityKind.Chaff;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported countermeasure kind.");
        }
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Countermeasure time must be finite.");
        }
    }

    private sealed class TrackedCountermeasure
    {
        private readonly double _sampleIntervalSeconds;
        private readonly double? _expiresAtSeconds;
        private EntityTransform? _initialTransform;
        private EntityTransform? _latestTransform;
        private EntityTransform? _pendingRegularTransform;
        private EntityTransform? _finalTransform;
        private double _nextSampleTimeSeconds;
        private bool _spawnEmitted;
        private bool _finalSnapshotEmitted;
        private bool _pendingRemoval;
        private bool _canDrainThisFrame = true;

        public TrackedCountermeasure(
            CountermeasureHandle handle,
            CountermeasureSpawnRequest request,
            EntityTransform? initialTransform,
            double spawnedAtSeconds,
            double sampleIntervalSeconds)
        {
            Handle = handle ?? throw new ArgumentNullException(nameof(handle));
            Source = request.Source;
            ExpiryRemovalReason = request.ExpiryRemovalReason;
            _sampleIntervalSeconds = sampleIntervalSeconds;
            _expiresAtSeconds = request.MaximumLifetimeSeconds.HasValue
                ? spawnedAtSeconds + request.MaximumLifetimeSeconds.Value
                : (double?)null;
            _nextSampleTimeSeconds = spawnedAtSeconds + sampleIntervalSeconds;
            _initialTransform = initialTransform;
            _latestTransform = initialTransform;
        }

        public CountermeasureHandle Handle { get; }

        public IEntityTransformSource Source { get; }

        public TacviewEntityRemovalReason ExpiryRemovalReason { get; }

        public bool HasPendingRemoval => _pendingRemoval;

        public TacviewEntityRemovalReason RemovalReason { get; private set; } =
            TacviewEntityRemovalReason.Deleted;

        public bool WasSuppressed { get; private set; }

        public bool IsTerminal { get; private set; }

        public bool HasPendingLifecycleData =>
            (!_spawnEmitted && _initialTransform.HasValue) || _pendingRemoval;

        public void BeginFrame()
        {
            _canDrainThisFrame = true;
        }

        public bool IsSampleDue(double nowSeconds)
        {
            return nowSeconds >= _nextSampleTimeSeconds;
        }

        public bool HasExpired(double nowSeconds)
        {
            return _expiresAtSeconds.HasValue && nowSeconds >= _expiresAtSeconds.Value;
        }

        public void CompleteSamplingAttempt(
            double nowSeconds,
            EntityTransform? sampledTransform)
        {
            _nextSampleTimeSeconds = GetNextDueTime(nowSeconds);
            if (!sampledTransform.HasValue)
            {
                return;
            }

            if (!_initialTransform.HasValue)
            {
                _initialTransform = sampledTransform.Value;
            }

            _latestTransform = sampledTransform.Value;
            _pendingRegularTransform = sampledTransform.Value;
        }

        public bool MarkForRemoval(
            TacviewEntityRemovalReason removalReason,
            EntityTransform? observedFinalTransform)
        {
            if (_pendingRemoval || IsTerminal)
            {
                return false;
            }

            _pendingRemoval = true;
            RemovalReason = removalReason;

            if (!_initialTransform.HasValue && observedFinalTransform.HasValue)
            {
                // The first real observation doubles as the Spawn sample. Do
                // not manufacture a second, unknown final position.
                _initialTransform = observedFinalTransform.Value;
                _latestTransform = observedFinalTransform.Value;
                _finalSnapshotEmitted = true;
                _pendingRegularTransform = null;
                return true;
            }

            _finalTransform = observedFinalTransform ?? _latestTransform;
            _pendingRegularTransform = null;
            return true;
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
                if (_initialTransform.HasValue)
                {
                    _spawnEmitted = true;
                    _canDrainThisFrame = false;
                    update = TacviewEntityUpdate.Upsert(
                        Handle.ObjectId,
                        _initialTransform.Value,
                        Handle.Entity.Metadata);
                    return true;
                }

                if (_pendingRemoval)
                {
                    WasSuppressed = true;
                    IsTerminal = true;
                }

                return false;
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
                    RemovalReason,
                    CountermeasureMetadataFactory.GetName(Handle.Kind));
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

        private double GetNextDueTime(double nowSeconds)
        {
            double candidate = _nextSampleTimeSeconds + _sampleIntervalSeconds;
            return candidate > nowSeconds
                ? candidate
                : nowSeconds + _sampleIntervalSeconds;
        }
    }
}
