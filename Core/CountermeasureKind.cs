using System;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// Semantic countermeasure kinds. This is deliberately independent from the
/// generic entity kind used by the pre-0.4 registry so a release can never be
/// conflated with a pooled game object or a projectile.
/// </summary>
public enum CountermeasureKind
{
    Flare,
    Chaff
}

/// <summary>
/// Lets the integration select the concrete generic entity kind registered for
/// each countermeasure. The default maps to the dedicated Flare and Chaff
/// values in <see cref="EntityKind"/>.
/// </summary>
public delegate EntityKind CountermeasureEntityKindResolver(CountermeasureKind kind);

/// <summary>
/// Creates the canonical Tacview metadata for visible countermeasures.
/// The type strings are the locally verified Tacview SDK composite types.
/// </summary>
public static class CountermeasureMetadataFactory
{
    public const string FlareTacviewType = "Misc+Decoy+Flare";
    public const string ChaffTacviewType = "Misc+Decoy+Chaff";

    public static EntityMetadata Create(
        CountermeasureKind kind,
        string coalition,
        string color,
        ulong? parentObjectId = null)
    {
        return new EntityMetadata(
            GetTacviewType(kind),
            GetName(kind),
            coalition,
            color,
            parentObjectId: parentObjectId);
    }

    public static string GetName(CountermeasureKind kind)
    {
        switch (kind)
        {
            case CountermeasureKind.Flare:
                return "Flare";
            case CountermeasureKind.Chaff:
                return "Chaff";
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported countermeasure kind.");
        }
    }

    public static string GetTacviewType(CountermeasureKind kind)
    {
        switch (kind)
        {
            case CountermeasureKind.Flare:
                return FlareTacviewType;
            case CountermeasureKind.Chaff:
                return ChaffTacviewType;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported countermeasure kind.");
        }
    }
}
