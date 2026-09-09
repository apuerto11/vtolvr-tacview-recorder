using System;

namespace VTOLVRTacviewSPRecorder.Core;

public sealed class EntityMetadata
{
    public EntityMetadata(
        string type,
        string name,
        string coalition,
        string color,
        string callSign = null,
        string pilot = null,
        ulong? parentObjectId = null,
        ulong? lockedTargetObjectId = null,
        ulong? nextObjectId = null,
        string shape = null)
    {
        Type = RequireValue(type, nameof(type));
        Name = RequireValue(name, nameof(name));
        Coalition = RequireValue(coalition, nameof(coalition));
        Color = RequireValue(color, nameof(color));
        CallSign = NormalizeOptionalValue(callSign);
        Pilot = NormalizeOptionalValue(pilot);
        Shape = NormalizeOptionalValue(shape);
        ParentObjectId = ValidateObjectId(parentObjectId, nameof(parentObjectId));
        LockedTargetObjectId = ValidateObjectId(lockedTargetObjectId, nameof(lockedTargetObjectId));
        NextObjectId = ValidateObjectId(nextObjectId, nameof(nextObjectId));
    }

    public string Type { get; }

    public string Name { get; }

    public string Coalition { get; }

    public string Color { get; }

    public string CallSign { get; }

    public string Pilot { get; }

    public string Shape { get; }

    public ulong? ParentObjectId { get; }

    public ulong? LockedTargetObjectId { get; }

    public ulong? NextObjectId { get; }

    public EntityMetadata Clone()
    {
        return new EntityMetadata(
            Type,
            Name,
            Coalition,
            Color,
            CallSign,
            Pilot,
            ParentObjectId,
            LockedTargetObjectId,
            NextObjectId,
            Shape);
    }

    public EntityMetadata WithRelationships(
        ulong? parentObjectId,
        ulong? lockedTargetObjectId,
        ulong? nextObjectId)
    {
        return new EntityMetadata(
            Type,
            Name,
            Coalition,
            Color,
            CallSign,
            Pilot,
            parentObjectId,
            lockedTargetObjectId,
            nextObjectId,
            Shape);
    }

    public bool ValueEquals(EntityMetadata other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other != null &&
            string.Equals(Type, other.Type, StringComparison.Ordinal) &&
            string.Equals(Name, other.Name, StringComparison.Ordinal) &&
            string.Equals(Coalition, other.Coalition, StringComparison.Ordinal) &&
            string.Equals(Color, other.Color, StringComparison.Ordinal) &&
            string.Equals(CallSign, other.CallSign, StringComparison.Ordinal) &&
            string.Equals(Pilot, other.Pilot, StringComparison.Ordinal) &&
            string.Equals(Shape, other.Shape, StringComparison.Ordinal) &&
            ParentObjectId == other.ParentObjectId &&
            LockedTargetObjectId == other.LockedTargetObjectId &&
            NextObjectId == other.NextObjectId;
    }

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty metadata value is required.", parameterName);
        }

        return value.Trim();
    }

    private static string NormalizeOptionalValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static ulong? ValidateObjectId(ulong? objectId, string parameterName)
    {
        if (objectId.HasValue && objectId.Value == 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Global ACMI object id 0 cannot be used as an entity relationship.");
        }

        return objectId;
    }
}
