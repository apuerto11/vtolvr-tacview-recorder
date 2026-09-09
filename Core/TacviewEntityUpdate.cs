using System;

namespace VTOLVRTacviewSPRecorder.Core;

public enum TacviewEntityRemovalReason
{
    LeftArea,
    Destroyed,
    Deleted
}

public enum TacviewEventKind
{
    Timeout
}

public readonly struct TacviewEntityUpdate
{
    private TacviewEntityUpdate(
        ulong objectId,
        EntityTransform transform,
        EntityMetadata metadata,
        bool isDelete,
        TacviewEntityRemovalReason removalReason,
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

    public EntityTransform Transform { get; }

    public EntityMetadata Metadata { get; }

    public bool IsDelete { get; }

    public TacviewEntityRemovalReason RemovalReason { get; }

    public string RemovalLabel { get; }

    public static TacviewEntityUpdate Upsert(
        ulong objectId,
        EntityTransform transform,
        EntityMetadata metadata)
    {
        if (objectId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(objectId));
        }

        if (metadata == null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        return new TacviewEntityUpdate(
            objectId,
            transform,
            metadata,
            false,
            TacviewEntityRemovalReason.Deleted,
            null);
    }

    public static TacviewEntityUpdate Delete(
        ulong objectId,
        TacviewEntityRemovalReason removalReason,
        string removalLabel)
    {
        if (objectId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(objectId));
        }

        if (string.IsNullOrWhiteSpace(removalLabel))
        {
            throw new ArgumentException("A non-empty removal label is required.", nameof(removalLabel));
        }

        return new TacviewEntityUpdate(
            objectId,
            default,
            null,
            true,
            removalReason,
            removalLabel.Trim());
    }
}

public readonly struct TacviewEvent
{
    private TacviewEvent(
        TacviewEventKind kind,
        ulong sourceObjectId,
        ulong? targetObjectId,
        string ammoType,
        int ammoCount,
        string outcome)
    {
        Kind = kind;
        SourceObjectId = sourceObjectId;
        TargetObjectId = targetObjectId;
        AmmoType = ammoType;
        AmmoCount = ammoCount;
        Outcome = outcome;
    }

    public TacviewEventKind Kind { get; }

    public ulong SourceObjectId { get; }

    public ulong? TargetObjectId { get; }

    public string AmmoType { get; }

    public int AmmoCount { get; }

    public string Outcome { get; }

    public static TacviewEvent Timeout(
        ulong sourceObjectId,
        string ammoType,
        ulong? targetObjectId = null,
        string outcome = null,
        int ammoCount = 1)
    {
        if (string.IsNullOrWhiteSpace(ammoType))
        {
            throw new ArgumentException("A non-empty ammunition type is required.", nameof(ammoType));
        }

        if (ammoCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ammoCount));
        }

        if (sourceObjectId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceObjectId));
        }

        if (targetObjectId.HasValue && targetObjectId.Value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetObjectId));
        }

        return new TacviewEvent(
            TacviewEventKind.Timeout,
            sourceObjectId,
            targetObjectId,
            ammoType.Trim(),
            ammoCount,
            string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim());
    }
}
