using System;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// Immutable game-adapter input for one physical countermeasure release.
/// A null transform is intentional: the core never fabricates a position for
/// chaff or a flare when the game has not supplied one.
/// </summary>
public sealed class CountermeasureSpawnRequest
{
    public CountermeasureSpawnRequest(
        CountermeasureKind kind,
        string coalition,
        string color,
        ulong? parentObjectId = null,
        IEntityTransformSource source = null,
        EntityTransform? spawnTransform = null,
        double? maximumLifetimeSeconds = null,
        TacviewEntityRemovalReason expiryRemovalReason = TacviewEntityRemovalReason.Deleted)
    {
        // Validate the enum before a release can consume an ACMI object id.
        CountermeasureMetadataFactory.GetName(kind);

        if (parentObjectId.HasValue && parentObjectId.Value == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parentObjectId),
                "Global ACMI object id 0 cannot be used as a countermeasure parent.");
        }

        if (spawnTransform.HasValue && !spawnTransform.Value.HasFiniteValues)
        {
            throw new ArgumentOutOfRangeException(
                nameof(spawnTransform),
                "A countermeasure spawn transform must be finite.");
        }

        if (maximumLifetimeSeconds.HasValue &&
            (double.IsNaN(maximumLifetimeSeconds.Value) ||
             double.IsInfinity(maximumLifetimeSeconds.Value) ||
             maximumLifetimeSeconds.Value < 0.0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLifetimeSeconds),
                "A countermeasure lifetime must be finite and non-negative.");
        }

        Kind = kind;
        Coalition = coalition;
        Color = color;
        ParentObjectId = parentObjectId;
        Source = source;
        SpawnTransform = spawnTransform;
        MaximumLifetimeSeconds = maximumLifetimeSeconds;
        ExpiryRemovalReason = expiryRemovalReason;
    }

    public CountermeasureKind Kind { get; }

    public string Coalition { get; }

    public string Color { get; }

    /// <summary>
    /// Set this only when the firing aircraft has a known active ACMI object id.
    /// </summary>
    public ulong? ParentObjectId { get; }

    /// <summary>
    /// Optional live transform source for 2-5 Hz sampling. This is not used as
    /// an identity key, which prevents pooled Unity objects from reusing IDs.
    /// </summary>
    public IEntityTransformSource Source { get; }

    /// <summary>
    /// Actual release-time transform captured by the game adapter, if known.
    /// </summary>
    public EntityTransform? SpawnTransform { get; }

    /// <summary>
    /// Optional evidence-based maximum lifetime. A null lifetime means that the
    /// game adapter is responsible for reporting decay.
    /// </summary>
    public double? MaximumLifetimeSeconds { get; }

    public TacviewEntityRemovalReason ExpiryRemovalReason { get; }
}
