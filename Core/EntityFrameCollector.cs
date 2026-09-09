using System;
using System.Collections.Generic;

namespace VTOLVRTacviewSPRecorder.Core;

public readonly struct EntitySamplingTarget
{
    public EntitySamplingTarget(TrackedEntity entity, IEntityTransformSource source)
    {
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public TrackedEntity Entity { get; }

    public IEntityTransformSource Source { get; }
}

public sealed class EntityFrameCollector
{
    private readonly EntitySampleScheduler _scheduler;
    private readonly bool _suppressUnchangedStationary;
    private readonly Dictionary<ulong, EntityTransform> _lastSuccessfulTransforms =
        new Dictionary<ulong, EntityTransform>();
    private readonly Dictionary<ulong, int> _lastSuccessfulMetadataRevisions =
        new Dictionary<ulong, int>();

    public EntityFrameCollector(
        EntitySampleScheduler scheduler,
        bool suppressUnchangedStationary = true)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _suppressUnchangedStationary = suppressUnchangedStationary;
    }

    public void Collect(
        double nowSeconds,
        IReadOnlyList<EntitySamplingTarget> targets,
        List<EntityTransformUpdate> destination,
        Action<TrackedEntity, string> onSampleSkipped)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }

        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
        {
            EntitySamplingTarget target = targets[targetIndex];
            if (!_scheduler.IsDue(target.Entity, nowSeconds))
            {
                continue;
            }

            try
            {
                if (!target.Source.TryGetTransform(out EntityTransform transform))
                {
                    CompleteFailure(
                        target.Entity,
                        nowSeconds,
                        onSampleSkipped,
                        "entity, transform, or map coordinate is unavailable");
                    continue;
                }

                if (!transform.HasFiniteValues)
                {
                    CompleteFailure(
                        target.Entity,
                        nowSeconds,
                        onSampleSkipped,
                        "transform contains a non-finite numeric value");
                    continue;
                }

                bool unchangedStationary = _suppressUnchangedStationary &&
                    IsStationary(target.Entity) &&
                    _lastSuccessfulTransforms.TryGetValue(
                        target.Entity.ObjectId,
                        out EntityTransform previous) &&
                    previous.Equals(transform) &&
                    _lastSuccessfulMetadataRevisions.TryGetValue(
                        target.Entity.ObjectId,
                        out int previousMetadataRevision) &&
                    previousMetadataRevision == target.Entity.MetadataRevision;

                if (!unchangedStationary)
                {
                    destination.Add(new EntityTransformUpdate(target.Entity, transform));
                    _lastSuccessfulTransforms[target.Entity.ObjectId] = transform;
                    _lastSuccessfulMetadataRevisions[target.Entity.ObjectId] =
                        target.Entity.MetadataRevision;
                }

                _scheduler.CompleteSuccess(target.Entity, nowSeconds);
            }
            catch (Exception ex)
            {
                CompleteFailure(target.Entity, nowSeconds, onSampleSkipped, ex.Message);
            }
        }
    }

    public void Forget(ulong objectId)
    {
        _scheduler.Forget(objectId);
        _lastSuccessfulTransforms.Remove(objectId);
        _lastSuccessfulMetadataRevisions.Remove(objectId);
    }

    public void Clear()
    {
        _scheduler.Clear();
        _lastSuccessfulTransforms.Clear();
        _lastSuccessfulMetadataRevisions.Clear();
    }

    private void CompleteFailure(
        TrackedEntity entity,
        double nowSeconds,
        Action<TrackedEntity, string> onSampleSkipped,
        string reason)
    {
        _scheduler.CompleteFailure(entity, nowSeconds);

        if (onSampleSkipped == null)
        {
            return;
        }

        try
        {
            onSampleSkipped(entity, reason);
        }
        catch
        {
            // A diagnostic callback must not stop collection of other entities.
        }
    }

    private static bool IsStationary(TrackedEntity entity)
    {
        return entity.SamplingClass == SamplingClass.Static ||
            entity.Kind == EntityKind.Ground ||
            entity.Kind == EntityKind.Sea;
    }
}
