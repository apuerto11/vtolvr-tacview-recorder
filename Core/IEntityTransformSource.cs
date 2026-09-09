namespace VTOLVRTacviewSPRecorder.Core;

public interface IEntityTransformSource
{
    bool TryGetTransform(out EntityTransform transform);
}
