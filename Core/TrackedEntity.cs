using System;

namespace VTOLVRTacviewSPRecorder.Core;

public sealed class TrackedEntity
{
    internal TrackedEntity(
        object identity,
        ulong objectId,
        EntityKind kind,
        bool isOwnship,
        EntityMetadata metadata)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        ObjectId = objectId;
        Kind = kind;
        IsOwnship = isOwnship;
        SamplingClass = EntitySamplingClassResolver.Resolve(kind, isOwnship);
        Metadata = (metadata ?? throw new ArgumentNullException(nameof(metadata))).Clone();
    }

    public object Identity { get; }

    public ulong ObjectId { get; }

    public EntityKind Kind { get; }

    public bool IsOwnship { get; }

    public SamplingClass SamplingClass { get; }

    public EntityMetadata Metadata { get; private set; }

    public int MetadataRevision { get; private set; }

    internal void UpdateMetadata(EntityMetadata metadata)
    {
        if (metadata == null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        if (Metadata.ValueEquals(metadata))
        {
            return;
        }

        Metadata = metadata.Clone();
        MetadataRevision++;
    }
}
