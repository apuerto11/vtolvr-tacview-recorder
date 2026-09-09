using System;

namespace VTOLVRTacviewSPRecorder.Core;

public sealed class SampledEntityLifecycleState
{
    public SampledEntityLifecycleState(EntityTransform? initialTransform = null)
    {
        if (initialTransform.HasValue && !initialTransform.Value.HasFiniteValues)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialTransform),
                "An initial lifecycle transform must be finite.");
        }

        InitialTransform = initialTransform;
        RemovalReason = TacviewEntityRemovalReason.Deleted;
    }

    public bool HasPendingRemoval { get; private set; }

    public bool CanSample =>
        !HasPendingRemoval && InitialSnapshotEmitted && !SnapshotQueuedThisFrame;

    public bool NeedsInitialCapture =>
        !InitialSnapshotEmitted && !InitialTransform.HasValue;

    public TacviewEntityRemovalReason RemovalReason { get; private set; }

    public double? MissingSinceSeconds { get; private set; }

    public EntityTransform? FinalTransform { get; private set; }

    public bool FinalSnapshotEmitted { get; private set; }

    public EntityTransform? InitialTransform { get; private set; }

    public bool InitialSnapshotEmitted { get; private set; }

    public bool SnapshotQueuedThisFrame { get; private set; }

    public bool CanFinalizeRemoval =>
        HasPendingRemoval &&
        (!FinalTransform.HasValue || FinalSnapshotEmitted) &&
        (!InitialTransform.HasValue || InitialSnapshotEmitted);

    public void BeginFrame()
    {
        SnapshotQueuedThisFrame = false;
    }

    public bool TrySetInitialTransform(EntityTransform transform)
    {
        if (!transform.HasFiniteValues)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transform),
                "An initial lifecycle transform must be finite.");
        }

        if (!NeedsInitialCapture)
        {
            return false;
        }

        InitialTransform = transform;
        return true;
    }

    public bool TryQueueMandatorySnapshot(out EntityTransform transform)
    {
        transform = default;
        if (SnapshotQueuedThisFrame)
        {
            return false;
        }

        if (HasPendingRemoval && FinalTransform.HasValue && !FinalSnapshotEmitted)
        {
            transform = FinalTransform.Value;
            FinalSnapshotEmitted = true;
            InitialSnapshotEmitted = true;
            SnapshotQueuedThisFrame = true;
            return true;
        }

        if (!InitialSnapshotEmitted && InitialTransform.HasValue)
        {
            transform = InitialTransform.Value;
            InitialSnapshotEmitted = true;
            SnapshotQueuedThisFrame = true;
            return true;
        }

        return false;
    }

    public void MarkMissing(double nowSeconds)
    {
        ValidateTime(nowSeconds, nameof(nowSeconds));
        MissingSinceSeconds = nowSeconds;
    }

    public void ClearMissingSince()
    {
        MissingSinceSeconds = null;
    }

    public bool HasMissingTimedOut(double nowSeconds, double graceSeconds)
    {
        ValidateTime(nowSeconds, nameof(nowSeconds));
        if (double.IsNaN(graceSeconds) || double.IsInfinity(graceSeconds) || graceSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(graceSeconds));
        }

        return MissingSinceSeconds.HasValue &&
            nowSeconds - MissingSinceSeconds.Value >= graceSeconds;
    }

    public bool MarkForRemoval(
        TacviewEntityRemovalReason reason,
        EntityTransform? finalTransform)
    {
        if (finalTransform.HasValue && !finalTransform.Value.HasFiniteValues)
        {
            throw new ArgumentOutOfRangeException(
                nameof(finalTransform),
                "A final lifecycle transform must be finite.");
        }

        if (HasPendingRemoval)
        {
            return false;
        }

        HasPendingRemoval = true;
        RemovalReason = reason;
        FinalTransform = finalTransform;
        MissingSinceSeconds = null;
        return true;
    }

    private static void ValidateTime(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Lifecycle time must be finite.");
        }
    }
}
