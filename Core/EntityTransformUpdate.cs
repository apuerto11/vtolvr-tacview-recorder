using System;

namespace VTOLVRTacviewSPRecorder.Core;

public readonly struct EntityTransformUpdate
{
    public EntityTransformUpdate(TrackedEntity entity, EntityTransform transform)
    {
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        Transform = transform;
    }

    public TrackedEntity Entity { get; }

    public EntityTransform Transform { get; }
}
