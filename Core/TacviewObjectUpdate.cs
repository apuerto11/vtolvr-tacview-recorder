using System;

namespace VTOLVRTacviewSPRecorder.Core;

public enum TacviewObjectRemovalReason
{
    LeftArea,
    Destroyed
}

public readonly struct TacviewObjectUpdate
{
    private TacviewObjectUpdate(
        ulong objectId,
        AircraftTransform transform,
        AircraftMetadata metadata,
        bool isDelete,
        TacviewObjectRemovalReason removalReason,
        string removalLabel)
    {
        ObjectId = objectId;
        Transform = transform;
        Metadata = metadata;
        IsDelete = isDelete;
        RemovalReason = removalReason;
        RemovalLabel = removalLabel;
    }

    public ulong ObjectId { get; }

    public AircraftTransform Transform { get; }

    public AircraftMetadata Metadata { get; }

    public bool IsDelete { get; }

    public TacviewObjectRemovalReason RemovalReason { get; }

    public string RemovalLabel { get; }

    public static TacviewObjectUpdate Upsert(
        ulong objectId,
        AircraftTransform transform,
        AircraftMetadata metadata)
    {
        if (metadata == null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        return new TacviewObjectUpdate(
            objectId,
            transform,
            metadata,
            false,
            TacviewObjectRemovalReason.LeftArea,
            null);
    }

    public static TacviewObjectUpdate Delete(
        ulong objectId,
        TacviewObjectRemovalReason removalReason,
        string removalLabel)
    {
        if (string.IsNullOrWhiteSpace(removalLabel))
        {
            throw new ArgumentException("A non-empty removal label is required.", nameof(removalLabel));
        }

        return new TacviewObjectUpdate(
            objectId,
            default,
            null,
            true,
            removalReason,
            removalLabel.Trim());
    }
}
