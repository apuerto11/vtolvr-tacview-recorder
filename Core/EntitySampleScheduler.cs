using System;
using System.Collections.Generic;

namespace VTOLVRTacviewSPRecorder.Core;

public sealed class EntitySampleScheduler
{
    public const double OwnshipIntervalSeconds = 0.1;
    public const double AircraftIntervalSeconds = 0.2;
    public const double FastWeaponIntervalSeconds = 0.125;
    public const double BallisticWeaponIntervalSeconds = 0.5;
    public const double DecoyIntervalSeconds = 0.25;
    public const double SurfaceIntervalSeconds = 0.5;
    public const double StaticFailureRetryIntervalSeconds = 1.0;

    private readonly Dictionary<ulong, SampleState> _states =
        new Dictionary<ulong, SampleState>();

    public bool IsDue(TrackedEntity entity, double nowSeconds)
    {
        if (entity == null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        ValidateFinite(nowSeconds, nameof(nowSeconds));

        if (!_states.TryGetValue(entity.ObjectId, out SampleState state))
        {
            return true;
        }

        if (entity.SamplingClass == SamplingClass.Static)
        {
            return (!state.StaticCompleted ||
                    state.CompletedMetadataRevision != entity.MetadataRevision) &&
                (!state.HasNextDueTime || nowSeconds >= state.NextDueTimeSeconds);
        }

        return !state.HasNextDueTime || nowSeconds >= state.NextDueTimeSeconds;
    }

    public void CompleteSuccess(TrackedEntity entity, double nowSeconds)
    {
        if (entity == null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        ValidateFinite(nowSeconds, nameof(nowSeconds));

        _states.TryGetValue(entity.ObjectId, out SampleState state);
        if (entity.SamplingClass == SamplingClass.Static)
        {
            state.StaticCompleted = true;
            state.CompletedMetadataRevision = entity.MetadataRevision;
            state.HasNextDueTime = false;
        }
        else
        {
            state.NextDueTimeSeconds = GetNextDueTime(
                state.HasNextDueTime,
                state.NextDueTimeSeconds,
                nowSeconds,
                GetIntervalSeconds(entity.SamplingClass));
            state.HasNextDueTime = true;
        }

        _states[entity.ObjectId] = state;
    }

    public void CompleteFailure(TrackedEntity entity, double nowSeconds)
    {
        if (entity == null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        ValidateFinite(nowSeconds, nameof(nowSeconds));

        _states.TryGetValue(entity.ObjectId, out SampleState state);
        if (entity.SamplingClass == SamplingClass.Static)
        {
            state.StaticCompleted = false;
            state.NextDueTimeSeconds = nowSeconds + StaticFailureRetryIntervalSeconds;
            state.HasNextDueTime = true;
        }
        else
        {
            state.NextDueTimeSeconds = GetNextDueTime(
                state.HasNextDueTime,
                state.NextDueTimeSeconds,
                nowSeconds,
                GetIntervalSeconds(entity.SamplingClass));
            state.HasNextDueTime = true;
        }

        _states[entity.ObjectId] = state;
    }

    public void Forget(ulong objectId)
    {
        _states.Remove(objectId);
    }

    public void Clear()
    {
        _states.Clear();
    }

    public static double GetIntervalSeconds(SamplingClass samplingClass)
    {
        switch (samplingClass)
        {
            case SamplingClass.Ownship:
                return OwnshipIntervalSeconds;
            case SamplingClass.Aircraft:
                return AircraftIntervalSeconds;
            case SamplingClass.FastWeapon:
                return FastWeaponIntervalSeconds;
            case SamplingClass.BallisticWeapon:
                return BallisticWeaponIntervalSeconds;
            case SamplingClass.Decoy:
                return DecoyIntervalSeconds;
            case SamplingClass.Surface:
                return SurfaceIntervalSeconds;
            case SamplingClass.Static:
                throw new InvalidOperationException("Static entities have no recurring sampling interval.");
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(samplingClass),
                    samplingClass,
                    "Unsupported sampling class.");
        }
    }

    private static double GetNextDueTime(
        bool hasPreviousDueTime,
        double previousDueTimeSeconds,
        double nowSeconds,
        double intervalSeconds)
    {
        if (!hasPreviousDueTime)
        {
            return nowSeconds + intervalSeconds;
        }

        double candidate = previousDueTimeSeconds + intervalSeconds;
        return candidate > nowSeconds ? candidate : nowSeconds + intervalSeconds;
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Sampling time must be finite.");
        }
    }

    private struct SampleState
    {
        public bool HasNextDueTime;

        public double NextDueTimeSeconds;

        public bool StaticCompleted;

        public int CompletedMetadataRevision;
    }
}
