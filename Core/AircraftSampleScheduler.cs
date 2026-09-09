using System;
using System.Collections.Generic;

namespace VTOLVRTacviewSPRecorder.Core;

public sealed class AircraftSampleScheduler
{
    public const double OwnshipIntervalSeconds = 0.1;
    public const double OtherAircraftIntervalSeconds = 0.2;

    private readonly Dictionary<ulong, double> _nextSampleTimes =
        new Dictionary<ulong, double>();

    public bool ShouldSample(TrackedAircraft aircraft, double nowSeconds)
    {
        if (aircraft == null)
        {
            throw new ArgumentNullException(nameof(aircraft));
        }

        if (!_nextSampleTimes.TryGetValue(aircraft.ObjectId, out double dueTime) ||
            nowSeconds >= dueTime)
        {
            double interval = aircraft.IsOwnship
                ? OwnshipIntervalSeconds
                : OtherAircraftIntervalSeconds;
            _nextSampleTimes[aircraft.ObjectId] = NextDueTime(dueTime, nowSeconds, interval);
            return true;
        }

        return false;
    }

    public void Forget(ulong objectId)
    {
        _nextSampleTimes.Remove(objectId);
    }

    public void Clear()
    {
        _nextSampleTimes.Clear();
    }

    private static double NextDueTime(double previousDueTime, double nowSeconds, double interval)
    {
        // Do not replay a backlog after a frame stall. Tacview can interpolate
        // the gap, while a burst would add main-thread work and duplicate time.
        return nowSeconds + interval;
    }
}
