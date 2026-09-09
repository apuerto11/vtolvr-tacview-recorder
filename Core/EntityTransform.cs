using System;

namespace VTOLVRTacviewSPRecorder.Core;

public readonly struct EntityTransform : IEquatable<EntityTransform>
{
    public EntityTransform(
        double longitudeDegrees,
        double latitudeDegrees,
        double altitudeMeters,
        double rollDegrees,
        double pitchDegrees,
        double yawDegrees,
        double nativeUPositionMeters,
        double nativeVPositionMeters,
        double nativeHeadingDegrees)
    {
        LongitudeDegrees = longitudeDegrees;
        LatitudeDegrees = latitudeDegrees;
        AltitudeMeters = altitudeMeters;
        RollDegrees = rollDegrees;
        PitchDegrees = pitchDegrees;
        YawDegrees = yawDegrees;
        NativeUPositionMeters = nativeUPositionMeters;
        NativeVPositionMeters = nativeVPositionMeters;
        NativeHeadingDegrees = nativeHeadingDegrees;
    }

    public double LongitudeDegrees { get; }

    public double LatitudeDegrees { get; }

    public double AltitudeMeters { get; }

    public double RollDegrees { get; }

    public double PitchDegrees { get; }

    public double YawDegrees { get; }

    public double NativeUPositionMeters { get; }

    public double NativeVPositionMeters { get; }

    public double NativeHeadingDegrees { get; }

    public bool HasFiniteValues
    {
        get
        {
            return IsFinite(LongitudeDegrees) &&
                IsFinite(LatitudeDegrees) &&
                IsFinite(AltitudeMeters) &&
                IsFinite(RollDegrees) &&
                IsFinite(PitchDegrees) &&
                IsFinite(YawDegrees) &&
                IsFinite(NativeUPositionMeters) &&
                IsFinite(NativeVPositionMeters) &&
                IsFinite(NativeHeadingDegrees);
        }
    }

    public bool Equals(EntityTransform other)
    {
        return LongitudeDegrees.Equals(other.LongitudeDegrees) &&
            LatitudeDegrees.Equals(other.LatitudeDegrees) &&
            AltitudeMeters.Equals(other.AltitudeMeters) &&
            RollDegrees.Equals(other.RollDegrees) &&
            PitchDegrees.Equals(other.PitchDegrees) &&
            YawDegrees.Equals(other.YawDegrees) &&
            NativeUPositionMeters.Equals(other.NativeUPositionMeters) &&
            NativeVPositionMeters.Equals(other.NativeVPositionMeters) &&
            NativeHeadingDegrees.Equals(other.NativeHeadingDegrees);
    }

    public override bool Equals(object obj)
    {
        return obj is EntityTransform && Equals((EntityTransform)obj);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + LongitudeDegrees.GetHashCode();
            hash = (hash * 31) + LatitudeDegrees.GetHashCode();
            hash = (hash * 31) + AltitudeMeters.GetHashCode();
            hash = (hash * 31) + RollDegrees.GetHashCode();
            hash = (hash * 31) + PitchDegrees.GetHashCode();
            hash = (hash * 31) + YawDegrees.GetHashCode();
            hash = (hash * 31) + NativeUPositionMeters.GetHashCode();
            hash = (hash * 31) + NativeVPositionMeters.GetHashCode();
            hash = (hash * 31) + NativeHeadingDegrees.GetHashCode();
            return hash;
        }
    }

    public static bool operator ==(EntityTransform left, EntityTransform right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(EntityTransform left, EntityTransform right)
    {
        return !left.Equals(right);
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
