namespace VTOLVRTacviewSPRecorder.Core;

public readonly struct AircraftTransform
{
    public AircraftTransform(
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
}
