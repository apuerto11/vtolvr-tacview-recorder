using System;
using System.Collections.Generic;

namespace VTOLVRTacviewSPRecorder.Core;

public readonly struct AircraftSamplingTarget
{
    public AircraftSamplingTarget(TrackedAircraft aircraft, IAircraftTransformSource source)
    {
        Aircraft = aircraft ?? throw new ArgumentNullException(nameof(aircraft));
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public TrackedAircraft Aircraft { get; }

    public IAircraftTransformSource Source { get; }
}

public sealed class AircraftFrameCollector
{
    private readonly AircraftSampleScheduler _scheduler;

    public AircraftFrameCollector(AircraftSampleScheduler scheduler)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    }

    public void Collect(
        double nowSeconds,
        IEnumerable<AircraftSamplingTarget> targets,
        List<TacviewObjectUpdate> destination,
        Action<TrackedAircraft, string> onSampleSkipped)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }

        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        foreach (AircraftSamplingTarget target in targets)
        {
            if (!_scheduler.ShouldSample(target.Aircraft, nowSeconds))
            {
                continue;
            }

            try
            {
                if (target.Source.TryGetTransform(out AircraftTransform transform))
                {
                    if (!HasFiniteValues(transform))
                    {
                        onSampleSkipped?.Invoke(
                            target.Aircraft,
                            "transform contains a non-finite numeric value");
                        continue;
                    }

                    destination.Add(TacviewObjectUpdate.Upsert(
                        target.Aircraft.ObjectId,
                        transform,
                        target.Aircraft.Metadata));
                }
                else
                {
                    onSampleSkipped?.Invoke(
                        target.Aircraft,
                        "actor, transform, or map coordinate is unavailable");
                }
            }
            catch (Exception ex)
            {
                onSampleSkipped?.Invoke(target.Aircraft, ex.Message);
            }
        }
    }

    private static bool HasFiniteValues(AircraftTransform transform)
    {
        return IsFinite(transform.LongitudeDegrees) &&
            IsFinite(transform.LatitudeDegrees) &&
            IsFinite(transform.AltitudeMeters) &&
            IsFinite(transform.RollDegrees) &&
            IsFinite(transform.PitchDegrees) &&
            IsFinite(transform.YawDegrees) &&
            IsFinite(transform.NativeUPositionMeters) &&
            IsFinite(transform.NativeVPositionMeters) &&
            IsFinite(transform.NativeHeadingDegrees);
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
