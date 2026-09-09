using System;

namespace VTOLVRTacviewSPRecorder.Core;

public sealed class TrackedAircraft
{
    internal TrackedAircraft(
        object identity,
        ulong objectId,
        bool isOwnship,
        AircraftMetadata metadata)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        ObjectId = objectId;
        IsOwnship = isOwnship;
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }

    public object Identity { get; }

    public ulong ObjectId { get; }

    public bool IsOwnship { get; }

    public AircraftMetadata Metadata { get; private set; }

    internal void UpdateMetadata(AircraftMetadata metadata)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }
}
