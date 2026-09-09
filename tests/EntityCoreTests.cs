using System;
using System.Collections.Generic;
using VTOLVRTacviewSPRecorder.Core;
using Xunit;

namespace VTOLVRTacviewSPRecorder.Core.Tests;

public sealed class EntityCoreTests
{
    [Fact]
    public void MetadataIsImmutableAndRelationshipsCanBeReplaced()
    {
        var metadata = new EntityMetadata(
            "Weapon+Missile",
            "AIM-120C",
            "Allies",
            "Blue",
            "Viper 1-1",
            "Pilot",
            0x100,
            0x201,
            0x301);

        EntityMetadata clone = metadata.Clone();
        EntityMetadata changed = metadata.WithRelationships(0x101, null, 0x302);

        Assert.NotSame(metadata, clone);
        Assert.Equal(metadata.Type, clone.Type);
        Assert.Equal((ulong)0x100, clone.ParentObjectId.GetValueOrDefault());
        Assert.Equal((ulong)0x101, changed.ParentObjectId.GetValueOrDefault());
        Assert.Null(changed.LockedTargetObjectId);
        Assert.Equal((ulong)0x302, changed.NextObjectId.GetValueOrDefault());
        Assert.Equal(metadata.Name, changed.Name);
        Assert.Throws<ArgumentOutOfRangeException>(() => Metadata(parentObjectId: 0));
    }

    [Fact]
    public void RegistryUsesOneMonotonicSessionWideIdSequenceAndNeverReusesIds()
    {
        var registry = new EntityRegistry();
        object ownshipIdentity = new object();
        object missileIdentity = new object();
        object groundIdentity = new object();
        object respawnedOwnshipIdentity = new object();

        TrackedEntity ownship = registry.Register(
            ownshipIdentity,
            EntityKind.Aircraft,
            true,
            Metadata("Ownship"));
        TrackedEntity missile = registry.Register(
            missileIdentity,
            EntityKind.Missile,
            false,
            Metadata("AIM-120C", type: "Weapon+Missile"));
        TrackedEntity ground = registry.Register(
            groundIdentity,
            EntityKind.Ground,
            false,
            Metadata("Truck", type: "Ground+Vehicle"));

        Assert.Equal(EntityRegistry.InitialOwnshipObjectId, ownship.ObjectId);
        Assert.Equal(0x101UL, missile.ObjectId);
        Assert.Equal(0x102UL, ground.ObjectId);
        Assert.Equal(SamplingClass.FastWeapon, missile.SamplingClass);
        Assert.Equal(SamplingClass.Surface, ground.SamplingClass);

        Assert.True(registry.Remove(missileIdentity, out TrackedEntity removedMissile));
        Assert.Same(missile, removedMissile);
        TrackedEntity replacement = registry.Register(
            new object(),
            EntityKind.Rocket,
            false,
            Metadata("Rocket", type: "Weapon+Rocket"));
        Assert.Equal(0x103UL, replacement.ObjectId);

        Assert.True(registry.Remove(ownshipIdentity, out TrackedEntity removedOwnship));
        Assert.Equal(EntityRegistry.InitialOwnshipObjectId, removedOwnship.ObjectId);
        TrackedEntity respawnedOwnship = registry.Register(
            respawnedOwnshipIdentity,
            EntityKind.Aircraft,
            true,
            Metadata("Respawned Ownship"));
        Assert.Equal(0x104UL, respawnedOwnship.ObjectId);

        registry.Clear();
        Assert.Equal(
            EntityRegistry.InitialOwnshipObjectId,
            registry.Register(new object(), EntityKind.Aircraft, true, Metadata("New Session")).ObjectId);
    }

    [Fact]
    public void RegistryPromotesLateAircraftUsingFreshOwnshipSemantics()
    {
        var registry = new EntityRegistry();
        object identity = new object();
        TrackedEntity initiallyRegular = registry.Register(
            identity,
            EntityKind.Aircraft,
            false,
            Metadata("Late Player"));

        Assert.True(registry.PromoteToOwnship(
            identity,
            Metadata("Ownship"),
            out TrackedEntity previous,
            out TrackedEntity promoted));

        Assert.Same(initiallyRegular, previous);
        Assert.Equal(0x101UL, previous.ObjectId);
        Assert.NotSame(previous, promoted);
        Assert.Equal(EntityRegistry.InitialOwnshipObjectId, promoted.ObjectId);
        Assert.True(promoted.IsOwnship);
        Assert.Equal(SamplingClass.Ownship, promoted.SamplingClass);
        Assert.True(registry.TryGet(identity, out TrackedEntity current));
        Assert.Same(promoted, current);
        Assert.Equal(0x102UL, registry.Register(
            new object(),
            EntityKind.Aircraft,
            false,
            Metadata("Next")).ObjectId);
    }

    [Fact]
    public void RegistryRefreshesMetadataAndTracksRevision()
    {
        var registry = new EntityRegistry();
        object identity = new object();
        TrackedEntity entity = registry.Register(
            identity,
            EntityKind.Aircraft,
            false,
            Metadata("Old"));

        TrackedEntity refreshed = registry.Register(
            identity,
            EntityKind.Aircraft,
            false,
            Metadata("New"));

        Assert.Same(entity, refreshed);
        Assert.Equal(1, refreshed.MetadataRevision);
        Assert.Equal("New", refreshed.Metadata.Name);

        registry.Register(
            identity,
            EntityKind.Aircraft,
            false,
            Metadata("New"));
        Assert.Equal(1, refreshed.MetadataRevision);
    }

    [Fact]
    public void SchedulerUsesExactCadencesAndStaticCompletionIsOneShot()
    {
        var registry = new EntityRegistry();
        TrackedEntity ownship = registry.Register(new object(), EntityKind.Aircraft, true, Metadata("Ownship"));
        TrackedEntity aircraft = registry.Register(new object(), EntityKind.Aircraft, false, Metadata("Wingman"));
        TrackedEntity missile = registry.Register(new object(), EntityKind.Missile, false, Metadata("Missile", type: "Weapon+Missile"));
        TrackedEntity bomb = registry.Register(new object(), EntityKind.Bomb, false, Metadata("Bomb", type: "Weapon+Bomb"));
        TrackedEntity staticObject = registry.Register(new object(), EntityKind.Static, false, Metadata("Bullseye", type: "Navaid+Static+Bullseye"));
        var scheduler = new EntitySampleScheduler();

        Assert.True(scheduler.IsDue(ownship, 0.0));
        scheduler.CompleteSuccess(ownship, 0.0);
        Assert.False(scheduler.IsDue(ownship, 0.099));
        Assert.True(scheduler.IsDue(ownship, 0.1));

        Assert.True(scheduler.IsDue(aircraft, 0.0));
        scheduler.CompleteSuccess(aircraft, 0.0);
        Assert.False(scheduler.IsDue(aircraft, 0.199));
        Assert.True(scheduler.IsDue(aircraft, 0.2));

        Assert.True(scheduler.IsDue(missile, 0.0));
        scheduler.CompleteSuccess(missile, 0.0);
        Assert.False(scheduler.IsDue(missile, 0.124));
        Assert.True(scheduler.IsDue(missile, 0.125));

        Assert.True(scheduler.IsDue(bomb, 0.0));
        scheduler.CompleteSuccess(bomb, 0.0);
        Assert.False(scheduler.IsDue(bomb, 0.499));
        Assert.True(scheduler.IsDue(bomb, 0.5));

        Assert.True(scheduler.IsDue(staticObject, 0.0));
        scheduler.CompleteSuccess(staticObject, 0.0);
        Assert.False(scheduler.IsDue(staticObject, 999.0));
    }

    [Fact]
    public void SchedulerAvoidsJitterDriftDropsStallBacklogAndRetriesStaticFailures()
    {
        var registry = new EntityRegistry();
        TrackedEntity ownship = registry.Register(new object(), EntityKind.Aircraft, true, Metadata("Ownship"));
        TrackedEntity staticObject = registry.Register(new object(), EntityKind.Static, false, Metadata("Waypoint", type: "Navaid+Static+Waypoint"));
        var scheduler = new EntitySampleScheduler();

        Assert.True(scheduler.IsDue(ownship, 0.0));
        scheduler.CompleteSuccess(ownship, 0.0);
        Assert.True(scheduler.IsDue(ownship, 0.101));
        scheduler.CompleteSuccess(ownship, 0.101);
        Assert.False(scheduler.IsDue(ownship, 0.199));
        Assert.True(scheduler.IsDue(ownship, 0.2));
        scheduler.CompleteSuccess(ownship, 0.2);

        Assert.True(scheduler.IsDue(ownship, 60.0));
        scheduler.CompleteSuccess(ownship, 60.0);
        Assert.False(scheduler.IsDue(ownship, 60.099));
        Assert.True(scheduler.IsDue(ownship, 60.1));

        Assert.True(scheduler.IsDue(staticObject, 0.0));
        scheduler.CompleteFailure(staticObject, 0.0);
        Assert.False(scheduler.IsDue(staticObject, 0.999));
        Assert.True(scheduler.IsDue(staticObject, 1.0));
        scheduler.CompleteSuccess(staticObject, 1.0);
        Assert.False(scheduler.IsDue(staticObject, 2.0));
    }

    [Fact]
    public void CollectorIsolatesFaultsAndRejectsNonFiniteTransforms()
    {
        var registry = new EntityRegistry();
        TrackedEntity throwing = registry.Register(new object(), EntityKind.Missile, false, Metadata("Throwing", type: "Weapon+Missile"));
        TrackedEntity invalid = registry.Register(new object(), EntityKind.Bomb, false, Metadata("Invalid", type: "Weapon+Bomb"));
        TrackedEntity healthy = registry.Register(new object(), EntityKind.Aircraft, false, Metadata("Healthy"));
        var collector = new EntityFrameCollector(new EntitySampleScheduler());
        var updates = new List<EntityTransformUpdate>();
        var skipped = new List<ulong>();

        collector.Collect(
            0.0,
            new[]
            {
                new EntitySamplingTarget(throwing, new ThrowingSource()),
                new EntitySamplingTarget(invalid, new FixedSource(Transform(longitude: double.NaN))),
                new EntitySamplingTarget(healthy, new FixedSource(Transform(longitude: 42.0)))
            },
            updates,
            (entity, _) => skipped.Add(entity.ObjectId));

        Assert.Equal(new[] { throwing.ObjectId, invalid.ObjectId }, skipped);
        EntityTransformUpdate update = Assert.Single(updates);
        Assert.Same(healthy, update.Entity);
        Assert.Equal(42.0, update.Transform.LongitudeDegrees, 10);
    }

    [Fact]
    public void CollectorSuppressesUnchangedGroundSeaAndStaticSamplesWhenEnabled()
    {
        var registry = new EntityRegistry();
        TrackedEntity ground = registry.Register(new object(), EntityKind.Ground, false, Metadata("Truck", type: "Ground+Vehicle"));
        TrackedEntity staticObject = registry.Register(new object(), EntityKind.Static, false, Metadata("Bullseye", type: "Navaid+Static+Bullseye"));
        EntityTransform transform = Transform();
        var collector = new EntityFrameCollector(new EntitySampleScheduler());
        var updates = new List<EntityTransformUpdate>();

        collector.Collect(
            0.0,
            new[]
            {
                new EntitySamplingTarget(ground, new FixedSource(transform)),
                new EntitySamplingTarget(staticObject, new FixedSource(transform))
            },
            updates,
            null);
        Assert.Equal(2, updates.Count);

        updates.Clear();
        collector.Collect(
            0.5,
            new[]
            {
                new EntitySamplingTarget(ground, new FixedSource(transform)),
                new EntitySamplingTarget(staticObject, new FixedSource(transform))
            },
            updates,
            null);
        Assert.Empty(updates);

        var unsuppressed = new EntityFrameCollector(
            new EntitySampleScheduler(),
            suppressUnchangedStationary: false);
        unsuppressed.Collect(
            0.0,
            new[] { new EntitySamplingTarget(ground, new FixedSource(transform)) },
            updates,
            null);
        updates.Clear();
        unsuppressed.Collect(
            0.5,
            new[] { new EntitySamplingTarget(ground, new FixedSource(transform)) },
            updates,
            null);
        Assert.Single(updates);
    }

    [Fact]
    public void StaticMetadataChangeForcesOneNewSnapshotWithoutTransformChange()
    {
        var registry = new EntityRegistry();
        object identity = new object();
        TrackedEntity staticObject = registry.Register(
            identity,
            EntityKind.Static,
            false,
            Metadata("Old Bullseye", type: "Navaid+Static+Bullseye"));
        var collector = new EntityFrameCollector(new EntitySampleScheduler());
        var updates = new List<EntityTransformUpdate>();
        var target = new EntitySamplingTarget(staticObject, new FixedSource(Transform()));

        collector.Collect(0.0, new[] { target }, updates, null);
        Assert.Single(updates);
        updates.Clear();

        registry.Register(
            identity,
            EntityKind.Static,
            false,
            Metadata("Updated Bullseye", type: "Navaid+Static+Bullseye"));
        collector.Collect(0.1, new[] { target }, updates, null);
        Assert.Single(updates);
        Assert.Equal("Updated Bullseye", updates[0].Entity.Metadata.Name);

        updates.Clear();
        collector.Collect(1.0, new[] { target }, updates, null);
        Assert.Empty(updates);
    }

    [Fact]
    public void WarningThrottleUsesObjectAndKindKeysAndCleansUpRemovedEntities()
    {
        var throttle = new WarningThrottle(10.0);

        Assert.True(throttle.ShouldLog(0x101, WarningKind.SourceUnavailable, 0.0));
        Assert.False(throttle.ShouldLog(0x101, WarningKind.SourceUnavailable, 9.999));
        Assert.True(throttle.ShouldLog(0x101, WarningKind.SourceUnavailable, 10.0));
        Assert.True(throttle.ShouldLog(0x101, WarningKind.SourceFault, 10.1));
        Assert.True(throttle.ShouldLog(0x102, WarningKind.SourceUnavailable, 10.1));

        throttle.Forget(0x101);
        Assert.True(throttle.ShouldLog(0x101, WarningKind.SourceUnavailable, 10.2));
        Assert.False(throttle.ShouldLog(0x102, WarningKind.SourceUnavailable, 10.2));
    }

    [Fact]
    public void RemovalSequenceWritesFinalSnapshotBeforeDeleteAcrossTwoPasses()
    {
        var registry = new EntityRegistry();
        TrackedEntity actor = registry.Register(
            new object(),
            EntityKind.Ground,
            false,
            Metadata("Ground", type: "Ground+Vehicle"));
        var sequence = new EntityRemovalSequence(
            actor,
            TacviewEntityRemovalReason.Destroyed,
            "Ground",
            Transform(longitude: 8.0),
            objectWasMaterialized: true);

        Assert.True(sequence.TryDequeue(out TacviewEntityUpdate finalSnapshot));
        Assert.False(finalSnapshot.IsDelete);
        Assert.Equal(8.0, finalSnapshot.Transform.LongitudeDegrees);
        Assert.False(sequence.IsComplete);

        Assert.True(sequence.TryDequeue(out TacviewEntityUpdate deletion));
        Assert.True(deletion.IsDelete);
        Assert.Equal(TacviewEntityRemovalReason.Destroyed, deletion.RemovalReason);
        Assert.True(sequence.IsComplete);
        Assert.False(sequence.TryDequeue(out TacviewEntityUpdate _));
    }

    [Fact]
    public void RemovalSequenceMaterializesShortLivedEntityOrSuppressesUnknownDelete()
    {
        var registry = new EntityRegistry();
        TrackedEntity withTransform = registry.Register(
            new object(),
            EntityKind.Sea,
            false,
            Metadata("Ship", type: "Sea+Watercraft"));
        var materialized = new EntityRemovalSequence(
            withTransform,
            TacviewEntityRemovalReason.Deleted,
            "Ship",
            Transform(),
            objectWasMaterialized: false);

        Assert.True(materialized.TryDequeue(out TacviewEntityUpdate initialSnapshot));
        Assert.False(initialSnapshot.IsDelete);
        Assert.True(materialized.TryDequeue(out TacviewEntityUpdate deletion));
        Assert.True(deletion.IsDelete);

        TrackedEntity withoutTransform = registry.Register(
            new object(),
            EntityKind.Ground,
            false,
            Metadata("Unknown", type: "Ground+Vehicle"));
        var suppressed = new EntityRemovalSequence(
            withoutTransform,
            TacviewEntityRemovalReason.Deleted,
            "Unknown",
            null,
            objectWasMaterialized: false);

        Assert.False(suppressed.TryDequeue(out TacviewEntityUpdate _));
        Assert.True(suppressed.IsComplete);
        Assert.True(suppressed.WasSuppressed);
    }

    [Fact]
    public void SampledLifecycleRetriesInitialCaptureAndGatesNormalSampling()
    {
        var lifecycle = new SampledEntityLifecycleState();
        Assert.True(lifecycle.NeedsInitialCapture);
        Assert.False(lifecycle.CanSample);
        Assert.False(lifecycle.TryQueueMandatorySnapshot(out EntityTransform _));

        Assert.True(lifecycle.TrySetInitialTransform(Transform(longitude: 9.0)));
        Assert.True(lifecycle.TryQueueMandatorySnapshot(out EntityTransform initial));
        Assert.Equal(9.0, initial.LongitudeDegrees);
        Assert.False(lifecycle.CanSample);

        lifecycle.BeginFrame();
        Assert.True(lifecycle.CanSample);
        Assert.False(lifecycle.NeedsInitialCapture);
    }

    [Fact]
    public void SampledLifecycleHonorsMissingGraceAndFinalSnapshotBeforeRemoval()
    {
        var lifecycle = new SampledEntityLifecycleState(Transform(longitude: 1.0));
        Assert.True(lifecycle.TryQueueMandatorySnapshot(out EntityTransform _));
        lifecycle.BeginFrame();

        lifecycle.MarkMissing(10.0);
        Assert.False(lifecycle.HasMissingTimedOut(10.249, 0.25));
        Assert.True(lifecycle.HasMissingTimedOut(10.25, 0.25));
        Assert.True(lifecycle.MarkForRemoval(
            TacviewEntityRemovalReason.Deleted,
            Transform(longitude: 2.0)));
        Assert.False(lifecycle.CanSample);
        Assert.False(lifecycle.CanFinalizeRemoval);

        Assert.True(lifecycle.TryQueueMandatorySnapshot(out EntityTransform final));
        Assert.Equal(2.0, final.LongitudeDegrees);
        Assert.True(lifecycle.CanFinalizeRemoval);
        Assert.False(lifecycle.MarkForRemoval(
            TacviewEntityRemovalReason.Destroyed,
            Transform(longitude: 3.0)));
        Assert.Equal(TacviewEntityRemovalReason.Deleted, lifecycle.RemovalReason);
    }

    private static EntityMetadata Metadata(
        string name = "Entity",
        string type = "Air+FixedWing",
        ulong? parentObjectId = null,
        ulong? lockedTargetObjectId = null,
        ulong? nextObjectId = null)
    {
        return new EntityMetadata(
            type,
            name,
            "Allies",
            "Blue",
            parentObjectId: parentObjectId,
            lockedTargetObjectId: lockedTargetObjectId,
            nextObjectId: nextObjectId);
    }

    private static EntityTransform Transform(
        double longitude = 7.0,
        double latitude = 51.0,
        double altitude = 1000.0,
        double roll = 0.0,
        double pitch = 0.0,
        double yaw = 0.0,
        double u = 0.0,
        double v = 0.0,
        double heading = 0.0)
    {
        return new EntityTransform(
            longitude,
            latitude,
            altitude,
            roll,
            pitch,
            yaw,
            u,
            v,
            heading);
    }

    private sealed class FixedSource : IEntityTransformSource
    {
        private readonly EntityTransform _transform;

        public FixedSource(EntityTransform transform)
        {
            _transform = transform;
        }

        public bool TryGetTransform(out EntityTransform transform)
        {
            transform = _transform;
            return true;
        }
    }

    private sealed class ThrowingSource : IEntityTransformSource
    {
        public bool TryGetTransform(out EntityTransform transform)
        {
            transform = default(EntityTransform);
            throw new InvalidOperationException("simulated entity failure");
        }
    }
}
