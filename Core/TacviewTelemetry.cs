using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// Numeric per-object ACMI 2.2 properties exported in addition to the
/// transform: flight data, sensor ranges and lock state.
/// </summary>
public enum TacviewTelemetryProperty
{
    IAS,
    TAS,
    Mach,
    AOA,
    AOS,
    AGL,
    Throttle,
    FuelVolume,
    RadarMode,
    RadarRange,
    EngagementRange,
    VerticalEngagementRange,
    LockedTargetMode
}

/// <summary>
/// ACMI name, output precision and change threshold of each telemetry
/// property. A value is written again only when it moved by at least the
/// threshold since the last value written for that object.
/// </summary>
public static class TacviewTelemetryCatalog
{
    private static readonly Definition[] Definitions =
    {
        new Definition("IAS", "0.0", 1.0),
        new Definition("TAS", "0.0", 1.0),
        new Definition("Mach", "0.000", 0.005),
        new Definition("AOA", "0.0", 0.5),
        new Definition("AOS", "0.0", 0.5),
        new Definition("AGL", "0.0", 2.0),
        new Definition("Throttle", "0.00", 0.02),
        new Definition("FuelVolume", "0", 1.0),
        new Definition("RadarMode", "0", 0.5),
        new Definition("RadarRange", "0", 1.0),
        new Definition("EngagementRange", "0", 1.0),
        new Definition("VerticalEngagementRange", "0", 1.0),
        new Definition("LockedTargetMode", "0", 0.5)
    };

    public static int Count => Definitions.Length;

    public static string GetName(TacviewTelemetryProperty property) => Definitions[(int)property].Name;

    public static string GetFormat(TacviewTelemetryProperty property) => Definitions[(int)property].Format;

    public static double GetThreshold(TacviewTelemetryProperty property) => Definitions[(int)property].Threshold;

    private readonly struct Definition
    {
        public Definition(string name, string format, double threshold)
        {
            Name = name;
            Format = format;
            Threshold = threshold;
        }

        public string Name { get; }

        public string Format { get; }

        public double Threshold { get; }
    }
}

/// <summary>
/// One sample of telemetry for an object. Unset properties are not written.
/// Non-finite values are ignored on <see cref="Set"/>.
/// </summary>
public sealed class TacviewTelemetry
{
    private readonly double?[] _values = new double?[TacviewTelemetryCatalog.Count];

    /// <summary>Radar lock target object id; written as <c>LockedTarget</c>.</summary>
    public ulong? LockedTargetObjectId { get; private set; }

    public bool IsEmpty
    {
        get
        {
            if (LockedTargetObjectId.HasValue)
            {
                return false;
            }

            for (int index = 0; index < _values.Length; index++)
            {
                if (_values[index].HasValue)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public TacviewTelemetry Set(TacviewTelemetryProperty property, double value)
    {
        if (!double.IsNaN(value) && !double.IsInfinity(value))
        {
            _values[(int)property] = value;
        }

        return this;
    }

    public double? Get(TacviewTelemetryProperty property) => _values[(int)property];

    /// <summary>
    /// Locked: <c>LockedTargetMode=1</c> plus the target id (when known).
    /// Not locked: <c>LockedTargetMode=0</c>.
    /// </summary>
    public TacviewTelemetry SetLock(bool locked, ulong? targetObjectId)
    {
        Set(TacviewTelemetryProperty.LockedTargetMode, locked ? 1.0 : 0.0);
        LockedTargetObjectId = locked && targetObjectId.HasValue && targetObjectId.Value != 0
            ? targetObjectId
            : null;
        return this;
    }

    internal TacviewTelemetry CloneWithLockedTarget(ulong? lockedTarget)
    {
        var clone = new TacviewTelemetry();
        Array.Copy(_values, clone._values, _values.Length);
        clone.LockedTargetObjectId = lockedTarget;
        return clone;
    }

    internal void CopyValueFrom(TacviewTelemetry source, int index)
    {
        _values[index] = source._values[index];
    }

    internal double? GetAt(int index) => _values[index];
}

/// <summary>
/// Change-threshold encoding of <see cref="TacviewTelemetry"/> into ACMI
/// property text. Pure: callers keep the returned state as "last written".
/// </summary>
public static class TacviewTelemetryEncoder
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// Appends <c>,Name=value</c> for every property of <paramref name="current"/>
    /// that was never written or moved by at least its threshold since
    /// <paramref name="lastWritten"/>, and <c>,LockedTarget=id</c> when the lock
    /// target changed and <paramref name="isKnownObject"/> accepts it.
    /// Returns the new "last written" state (unchanged properties keep their
    /// previously written value so slow drifts are eventually written).
    /// </summary>
    public static TacviewTelemetry AppendChanges(
        StringBuilder builder,
        TacviewTelemetry current,
        TacviewTelemetry lastWritten,
        Func<ulong, bool> isKnownObject,
        ulong? lockedTargetAlreadyWritten = null)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (current == null)
        {
            return lastWritten;
        }

        ulong? writtenLock = lastWritten?.LockedTargetObjectId;
        ulong? target = current.LockedTargetObjectId;
        bool lockReleased = current.Get(TacviewTelemetryProperty.LockedTargetMode) == 0.0;
        if (lockReleased)
        {
            // Mode 0 clears the lock in Tacview; the next lock, even on the
            // same target, must write LockedTarget again.
            writtenLock = null;
        }
        else if (target.HasValue &&
            target != writtenLock &&
            (isKnownObject == null || isKnownObject(target.Value)))
        {
            if (target != lockedTargetAlreadyWritten)
            {
                builder.Append(",LockedTarget=");
                builder.Append(target.Value.ToString("X", Invariant));
            }

            writtenLock = target;
        }

        TacviewTelemetry next = (lastWritten ?? new TacviewTelemetry()).CloneWithLockedTarget(writtenLock);
        for (int index = 0; index < TacviewTelemetryCatalog.Count; index++)
        {
            double? value = current.GetAt(index);
            if (!value.HasValue)
            {
                continue;
            }

            var property = (TacviewTelemetryProperty)index;
            double? previous = lastWritten?.GetAt(index);
            if (previous.HasValue &&
                Math.Abs(value.Value - previous.Value) < TacviewTelemetryCatalog.GetThreshold(property))
            {
                continue;
            }

            builder.Append(',');
            builder.Append(TacviewTelemetryCatalog.GetName(property));
            builder.Append('=');
            builder.Append(value.Value.ToString(TacviewTelemetryCatalog.GetFormat(property), Invariant));
            next.CopyValueFrom(current, index);
        }

        return next;
    }
}

/// <summary>
/// Attaches telemetry to the updates of a frame: merged into an existing
/// upsert of the same object, or added as a telemetry-only update. Objects
/// deleted in this frame get none (an ACMI frame holds each id once).
/// </summary>
public static class TacviewTelemetryMerger
{
    public static void Merge(
        List<TacviewEntityUpdate> updates,
        IList<KeyValuePair<ulong, TacviewTelemetry>> telemetry)
    {
        if (updates == null)
        {
            throw new ArgumentNullException(nameof(updates));
        }

        if (telemetry == null || telemetry.Count == 0)
        {
            return;
        }

        var indexById = new Dictionary<ulong, int>(updates.Count);
        for (int index = 0; index < updates.Count; index++)
        {
            indexById[updates[index].ObjectId] = index;
        }

        for (int index = 0; index < telemetry.Count; index++)
        {
            ulong objectId = telemetry[index].Key;
            TacviewTelemetry sample = telemetry[index].Value;
            if (objectId == 0 || sample == null || sample.IsEmpty)
            {
                continue;
            }

            if (indexById.TryGetValue(objectId, out int existing))
            {
                TacviewEntityUpdate update = updates[existing];
                if (!update.IsDelete)
                {
                    updates[existing] = update.WithTelemetry(sample);
                }

                continue;
            }

            indexById[objectId] = updates.Count;
            updates.Add(TacviewEntityUpdate.TelemetryOnly(objectId, sample));
        }
    }
}

/// <summary>Fixed-rate gate (e.g. 2 Hz) for telemetry sampling.</summary>
public sealed class TelemetryRateGate
{
    private double _nextDueSeconds = double.NegativeInfinity;

    public TelemetryRateGate(double rateHz)
    {
        if (double.IsNaN(rateHz) || double.IsInfinity(rateHz) || rateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(rateHz));
        }

        IntervalSeconds = 1.0 / rateHz;
    }

    public double IntervalSeconds { get; }

    /// <summary>True at most once per interval; the first call is always due.</summary>
    public bool TryEnter(double nowSeconds)
    {
        if (nowSeconds < _nextDueSeconds)
        {
            return false;
        }

        // Keep the cadence without bursting after a long stall.
        double next = _nextDueSeconds + IntervalSeconds;
        _nextDueSeconds = next > nowSeconds ? next : nowSeconds + IntervalSeconds;
        return true;
    }
}

/// <summary>
/// Air-data estimates from the International Standard Atmosphere. VTOL VR's
/// own atmosphere model may differ; values are labelled as estimates.
/// </summary>
public static class FlightDataMath
{
    private const double SeaLevelDensity = 1.225;
    private const double SeaLevelTemperature = 288.15;
    private const double LapseRate = 0.0065;
    private const double TropopauseAltitude = 11000.0;
    private const double TropopauseTemperature = 216.65;
    private const double GasConstant = 287.053;
    private const double Gamma = 1.4;
    private const double Gravity = 9.80665;

    public static double TemperatureKelvin(double altitudeMeters)
    {
        double altitude = Math.Max(-500.0, altitudeMeters);
        return altitude <= TropopauseAltitude
            ? SeaLevelTemperature - (LapseRate * altitude)
            : TropopauseTemperature;
    }

    public static double SpeedOfSound(double altitudeMeters)
    {
        return Math.Sqrt(Gamma * GasConstant * TemperatureKelvin(altitudeMeters));
    }

    public static double AirDensity(double altitudeMeters)
    {
        double altitude = Math.Max(-500.0, altitudeMeters);
        double exponent = Gravity / (GasConstant * LapseRate);
        if (altitude <= TropopauseAltitude)
        {
            return SeaLevelDensity * Math.Pow(TemperatureKelvin(altitude) / SeaLevelTemperature, exponent - 1.0);
        }

        double tropopauseDensity = SeaLevelDensity *
            Math.Pow(TropopauseTemperature / SeaLevelTemperature, exponent - 1.0);
        return tropopauseDensity * Math.Exp(-Gravity * (altitude - TropopauseAltitude) / (GasConstant * TropopauseTemperature));
    }

    public static double Mach(double trueAirspeed, double altitudeMeters)
    {
        return trueAirspeed / SpeedOfSound(altitudeMeters);
    }

    /// <summary>Equivalent airspeed, used as the indicated airspeed estimate (no instrument/compressibility error).</summary>
    public static double IndicatedAirspeed(double trueAirspeed, double altitudeMeters)
    {
        return trueAirspeed * Math.Sqrt(AirDensity(altitudeMeters) / SeaLevelDensity);
    }

    /// <summary>
    /// Angle of attack in degrees from the velocity in the body frame
    /// (Unity convention: x right, y up, z forward). Positive nose above the flight path.
    /// </summary>
    public static double AngleOfAttack(double localX, double localY, double localZ)
    {
        return Math.Atan2(-localY, localZ) * (180.0 / Math.PI);
    }

    /// <summary>Sideslip in degrees from the body-frame velocity; positive when the air comes from the right.</summary>
    public static double AngleOfSideslip(double localX, double localY, double localZ)
    {
        double speed = Math.Sqrt((localX * localX) + (localY * localY) + (localZ * localZ));
        return speed <= 0.0 ? 0.0 : Math.Asin(Math.Max(-1.0, Math.Min(1.0, localX / speed))) * (180.0 / Math.PI);
    }
}
