namespace VTOLVRTacviewSPRecorder.Core;

public interface IAircraftTransformSource
{
    bool TryGetTransform(out AircraftTransform transform);
}
