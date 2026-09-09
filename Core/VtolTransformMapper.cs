namespace VTOLVRTacviewSPRecorder.Core;

public static class VtolTransformMapper
{
    public static AircraftTransform Map(
        double gpsLatitudeX,
        double gpsLongitudeY,
        double gpsAltitudeZ,
        double globalWorldX,
        double globalWorldZ,
        Attitude attitude)
    {
        // VTOL VR returns GPS as x=latitude, y=longitude, z=MSL altitude.
        // Tacview flat-world U/V are the simulator's native horizontal axes:
        // U=VTOL global X and V=VTOL global Z, without a sign change. Unity
        // positive Y yaw turns from +Z toward +X, so it is clockwise from +V.
        return new AircraftTransform(
            gpsLongitudeY,
            gpsLatitudeX,
            gpsAltitudeZ,
            attitude.RollDegrees,
            attitude.PitchDegrees,
            attitude.YawDegrees,
            globalWorldX,
            globalWorldZ,
            attitude.YawDegrees);
    }

    public static EntityTransform MapEntity(
        double gpsLatitudeX,
        double gpsLongitudeY,
        double gpsAltitudeZ,
        double globalWorldX,
        double globalWorldZ,
        Attitude attitude)
    {
        return new EntityTransform(
            gpsLongitudeY,
            gpsLatitudeX,
            gpsAltitudeZ,
            attitude.RollDegrees,
            attitude.PitchDegrees,
            attitude.YawDegrees,
            globalWorldX,
            globalWorldZ,
            attitude.YawDegrees);
    }
}
