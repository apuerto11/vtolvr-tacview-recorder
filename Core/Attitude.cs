namespace VTOLVRTacviewSPRecorder.Core;

public readonly struct Attitude
{
    public Attitude(double rollDegrees, double pitchDegrees, double yawDegrees)
    {
        RollDegrees = rollDegrees;
        PitchDegrees = pitchDegrees;
        YawDegrees = yawDegrees;
    }

    public double RollDegrees { get; }

    public double PitchDegrees { get; }

    public double YawDegrees { get; }

    public static Attitude FromUnityEuler(double xDegrees, double yDegrees, double zDegrees)
    {
        return new Attitude(
            ToSignedAngle(-zDegrees),
            ToSignedAngle(-xDegrees),
            Normalize360(yDegrees));
    }

    private static double ToSignedAngle(double degrees)
    {
        double normalized = Normalize360(degrees);
        return normalized > 180.0 ? normalized - 360.0 : normalized;
    }

    private static double Normalize360(double degrees)
    {
        double normalized = degrees % 360.0;
        return normalized < 0.0 ? normalized + 360.0 : normalized;
    }
}
