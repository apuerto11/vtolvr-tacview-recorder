using System;
using System.Collections.Generic;
using VTOLVRTacviewSPRecorder.Core;
using Xunit;

namespace VTOLVRTacviewSPRecorder.Core.Tests;

public sealed class CountermeasureCoreTests
{
    [Fact]
    public void FlareWritesImmediateSpawnSamplesAtFourHzAndWritesFinalBeforeDelete()
    {
        var entityRegistry = new EntityRegistry();
        TrackedEntity owner = RegisterOwner(entityRegistry);
        var source = new MutableSource(Transform(longitude: 7.0));
        var countermeasures = CreateRegistry(entityRegistry);

        Assert.True(countermeasures.TrySpawn(
            Request(
                CountermeasureKind.Flare,
                source,
                Transform(longitude: 7.0),
                owner.ObjectId),
            0.0,
            out CountermeasureHandle flare));

        TacviewEntityUpdate spawn = Assert.Single(DrainNextFrame(countermeasures));
        Assert.False(spawn.IsDelete);
        Assert.Equal(flare.ObjectId, spawn.ObjectId);
        Assert.Equal("Misc+Decoy+Flare", spawn.Metadata.Type);
        Assert.Equal("Flare", spawn.Metadata.Name);
        Assert.Equal("Allies", spawn.Metadata.Coalition);
        Assert.Equal("Blue", spawn.Metadata.Color);
        Assert.Equal(owner.ObjectId, spawn.Metadata.ParentObjectId.GetValueOrDefault());
        Assert.Equal(EntityKind.Flare, flare.Entity.Kind);

        source.Transform = Transform(longitude: 8.0);
        countermeasures.Refresh(0.25);
        TacviewEntityUpdate sampled = Assert.Single(DrainNextFrame(countermeasures));
        Assert.False(sampled.IsDelete);
        Assert.Equal(8.0, sampled.Transform.LongitudeDegrees, 10);

        Assert.True(countermeasures.MarkForRemoval(
            flare,
            0.30,
            Transform(longitude: 9.0)));
        TacviewEntityUpdate final = Assert.Single(DrainNextFrame(countermeasures));
        Assert.False(final.IsDelete);
        Assert.Equal(9.0, final.Transform.LongitudeDegrees, 10);

        TacviewEntityUpdate deletion = Assert.Single(DrainNextFrame(countermeasures));
        Assert.True(deletion.IsDelete);
        Assert.Equal(TacviewEntityRemovalReason.Deleted, deletion.RemovalReason);
        Assert.Equal("Flare", deletion.RemovalLabel);
        Assert.Equal(0, countermeasures.ActiveCountermeasureCount);
    }

    [Fact]
    public void ChaffUsesObservedFirePositionAndNeverInventsAnOwnerPosition()
    {
        var entityRegistry = new EntityRegistry();
        TrackedEntity owner = RegisterOwner(entityRegistry);
        var countermeasures = CreateRegistry(entityRegistry);
        EntityTransform firePosition = Transform(longitude: 11.0, latitude: 52.0);

        Assert.True(countermeasures.TrySpawn(
            Request(
                CountermeasureKind.Chaff,
                source: null,
                spawnTransform: firePosition,
                parentObjectId: owner.ObjectId,
                maximumLifetimeSeconds: 0.01),
            0.0,
            out CountermeasureHandle chaff));

        TacviewEntityUpdate spawn = Assert.Single(DrainNextFrame(countermeasures));
        Assert.Equal("Misc+Decoy+Chaff", spawn.Metadata.Type);
        Assert.Equal("Chaff", spawn.Metadata.Name);
        Assert.Equal(EntityKind.Chaff, chaff.Entity.Kind);
        Assert.Equal(11.0, spawn.Transform.LongitudeDegrees, 10);
        Assert.Equal(52.0, spawn.Transform.LatitudeDegrees, 10);

        countermeasures.Refresh(0.01);
        TacviewEntityUpdate final = Assert.Single(DrainNextFrame(countermeasures));
        Assert.False(final.IsDelete);
        Assert.Equal(11.0, final.Transform.LongitudeDegrees, 10);
        TacviewEntityUpdate deletion = Assert.Single(DrainNextFrame(countermeasures));
        Assert.True(deletion.IsDelete);
        Assert.Equal(chaff.ObjectId, deletion.ObjectId);
    }

    [Fact]
    public void AdvancedChaffSlotTrackerDetectsOnlyPostFireGenerations()
    {
        var tracker = new CountermeasureSlotGenerationTracker(16);
        for (int index = 0; index < tracker.SlotCount; index++)
        {
            Assert.False(tracker.Observe(index, isActive: false, 0.0));
        }

        Assert.True(tracker.Observe(3, isActive: true, 12.5));
        Assert.False(tracker.Observe(3, isActive: true, 12.5));
        Assert.False(tracker.Observe(3, isActive: true, 12.5005));

        Assert.True(tracker.Observe(3, isActive: true, 12.75));
    }

    [Fact]
    public void AdvancedChaffSlotTrackerFindsEveryReleaseInADoubleSalvo()
    {
        var tracker = new CountermeasureSlotGenerationTracker(16);

        Assert.True(tracker.Observe(5, isActive: true, 20.0));
        Assert.True(tracker.Observe(6, isActive: true, 20.0));

        Assert.False(tracker.Observe(5, isActive: true, 20.0));
        Assert.False(tracker.Observe(6, isActive: true, 20.0));
    }

    [Fact]
    public void AdvancedChaffSlotTrackerTreatsReusedInactiveSlotAsFreshIdentity()
    {
        var tracker = new CountermeasureSlotGenerationTracker(1);

        Assert.True(tracker.Observe(0, isActive: true, 30.0));
        Assert.False(tracker.Observe(0, isActive: false, 0.0));
        Assert.True(tracker.Observe(0, isActive: true, 30.0));
    }

    [Fact]
    public void UnknownChaffTransformSuppressesDeleteInsteadOfFabricatingPosition()
    {
        var warnings = new CollectingWarningSink();
        var entityRegistry = new EntityRegistry();
        var countermeasures = CreateRegistry(entityRegistry, warningSink: warnings);

        Assert.True(countermeasures.TrySpawn(
            Request(
                CountermeasureKind.Chaff,
                source: null,
                spawnTransform: null,
                parentObjectId: null,
                maximumLifetimeSeconds: 0.0),
            0.0,
            out CountermeasureHandle _));

        countermeasures.Refresh(0.0);
        Assert.Empty(DrainNextFrame(countermeasures));
        Assert.Equal(0, countermeasures.ActiveCountermeasureCount);
        Assert.Contains(warnings.Warnings, warning =>
            warning.Kind == CountermeasureWarningKind.LifecycleSuppressed);
    }

    [Fact]
    public void SalvoGetsDistinctSessionIdsAndPooledSourceNeverReusesAnId()
    {
        var entityRegistry = new EntityRegistry();
        RegisterOwner(entityRegistry);
        var source = new MutableSource(Transform(longitude: 1.0));
        var countermeasures = CreateRegistry(entityRegistry);

        var salvoRequests = new List<CountermeasureSpawnRequest>
        {
            Request(CountermeasureKind.Flare, source, Transform(longitude: 1.0)),
            Request(CountermeasureKind.Flare, source, Transform(longitude: 2.0)),
            Request(CountermeasureKind.Flare, source, Transform(longitude: 3.0))
        };
        var salvo = new List<CountermeasureHandle>();
        Assert.Equal(3, countermeasures.TrySpawnSalvo(salvoRequests, 0.0, salvo));
        Assert.Equal(3, salvo.Count);
        var ids = new HashSet<ulong>();
        for (int index = 0; index < salvo.Count; index++)
        {
            Assert.True(ids.Add(salvo[index].ObjectId));
        }

        Assert.Equal(3, DrainNextFrame(countermeasures).Count);

        // The exact same source instance models a reused pooled CMFlare.
        CountermeasureHandle pooledFirst = salvo[0];
        Assert.True(countermeasures.MarkForRemoval(
            pooledFirst,
            0.01,
            Transform(longitude: 4.0)));
        DrainUntilRemoved(countermeasures, pooledFirst.ObjectId);

        Assert.True(countermeasures.TrySpawn(
            Request(CountermeasureKind.Flare, source, Transform(longitude: 5.0)),
            0.02,
            out CountermeasureHandle pooledSecond));
        Assert.NotEqual(pooledFirst.ObjectId, pooledSecond.ObjectId);
        Assert.True(pooledSecond.ObjectId > pooledFirst.ObjectId);
    }

    [Fact]
    public void CountermeasureCapIsSeparateFromWeaponsAndSalvosAreBounded()
    {
        var entityRegistry = new EntityRegistry();
        for (int index = 0; index < 64; index++)
        {
            entityRegistry.Register(
                new object(),
                EntityKind.Missile,
                false,
                Metadata("Missile", "Weapon+Missile"));
        }

        var warnings = new CollectingWarningSink();
        var countermeasures = CreateRegistry(
            entityRegistry,
            new CountermeasureSamplingSettings(
                activeCountermeasureCap: 2,
                maximumCountermeasuresPerSalvo: 2,
                sampleIntervalSeconds: 0.25),
            warningSink: warnings);
        var source = new MutableSource(Transform());
        var requests = new List<CountermeasureSpawnRequest>
        {
            Request(CountermeasureKind.Flare, source, Transform(longitude: 1.0)),
            Request(CountermeasureKind.Flare, source, Transform(longitude: 2.0)),
            Request(CountermeasureKind.Flare, source, Transform(longitude: 3.0)),
            Request(CountermeasureKind.Flare, source, Transform(longitude: 4.0))
        };
        var handles = new List<CountermeasureHandle>();

        Assert.Equal(2, countermeasures.TrySpawnSalvo(requests, 0.0, handles));
        Assert.Equal(2, countermeasures.ActiveCountermeasureCount);
        Assert.Equal(2, handles.Count);
        Assert.Contains(warnings.Warnings, warning =>
            warning.Kind == CountermeasureWarningKind.SalvoCapReached);
        Assert.False(countermeasures.TrySpawn(
            Request(CountermeasureKind.Chaff, source, Transform(longitude: 5.0)),
            0.01,
            out CountermeasureHandle _));
        Assert.Contains(warnings.Warnings, warning =>
            warning.Kind == CountermeasureWarningKind.ActiveCapReached);
    }

    [Fact]
    public void SourceFailuresAreIsolatedAndWarningsAreThrottledPerRelease()
    {
        var entityRegistry = new EntityRegistry();
        var warnings = new CollectingWarningSink();
        var countermeasures = CreateRegistry(
            entityRegistry,
            warningThrottle: new WarningThrottle(10.0),
            warningSink: warnings);
        var broken = new ThrowingSource();
        var healthy = new MutableSource(Transform(longitude: 1.0));

        Assert.True(countermeasures.TrySpawn(
            Request(CountermeasureKind.Flare, broken, Transform(longitude: 1.0)),
            0.0,
            out CountermeasureHandle brokenFlare));
        Assert.True(countermeasures.TrySpawn(
            Request(CountermeasureKind.Flare, healthy, Transform(longitude: 2.0)),
            0.0,
            out CountermeasureHandle healthyFlare));
        DrainNextFrame(countermeasures);

        healthy.Transform = Transform(longitude: 7.0);
        countermeasures.Refresh(0.25);
        List<TacviewEntityUpdate> firstDueFrame = DrainNextFrame(countermeasures);
        Assert.Single(firstDueFrame);
        Assert.Equal(healthyFlare.ObjectId, firstDueFrame[0].ObjectId);
        Assert.Equal(7.0, firstDueFrame[0].Transform.LongitudeDegrees, 10);

        countermeasures.Refresh(0.50);
        DrainNextFrame(countermeasures);
        int sourceFaultWarnings = 0;
        for (int index = 0; index < warnings.Warnings.Count; index++)
        {
            if (warnings.Warnings[index].Kind == CountermeasureWarningKind.SourceFault &&
                warnings.Warnings[index].ObjectId == brokenFlare.ObjectId)
            {
                sourceFaultWarnings++;
            }
        }

        Assert.Equal(1, sourceFaultWarnings);
    }

    [Fact]
    public void VeryShortFlareStillGetsSpawnAndDeleteAcrossLifecycleFrames()
    {
        var entityRegistry = new EntityRegistry();
        var countermeasures = CreateRegistry(entityRegistry);

        Assert.True(countermeasures.TrySpawn(
            Request(
                CountermeasureKind.Flare,
                source: null,
                spawnTransform: Transform(longitude: 1.0),
                parentObjectId: null,
                maximumLifetimeSeconds: 0.001),
            0.0,
            out CountermeasureHandle flare));
        countermeasures.Refresh(0.001);

        TacviewEntityUpdate spawn = Assert.Single(DrainNextFrame(countermeasures));
        Assert.False(spawn.IsDelete);
        Assert.Equal(flare.ObjectId, spawn.ObjectId);
        TacviewEntityUpdate final = Assert.Single(DrainNextFrame(countermeasures));
        Assert.False(final.IsDelete);
        TacviewEntityUpdate deletion = Assert.Single(DrainNextFrame(countermeasures));
        Assert.True(deletion.IsDelete);
        Assert.Equal(flare.ObjectId, deletion.ObjectId);
    }

    [Fact]
    public void MissionEndDrainRetainsEveryCountermeasureLifecycleUpdate()
    {
        var entityRegistry = new EntityRegistry();
        var countermeasures = CreateRegistry(entityRegistry);
        Assert.True(countermeasures.TrySpawn(
            Request(CountermeasureKind.Flare, null, Transform(longitude: 1.0)),
            0.0,
            out CountermeasureHandle flare));
        Assert.True(countermeasures.TrySpawn(
            Request(CountermeasureKind.Chaff, null, Transform(longitude: 2.0)),
            0.0,
            out CountermeasureHandle chaff));

        countermeasures.PrepareFinalDrain(0.1);
        var allUpdates = new List<TacviewEntityUpdate>();
        for (int pass = 0; pass < 8 && countermeasures.ActiveCountermeasureCount > 0; pass++)
        {
            allUpdates.AddRange(DrainNextFrame(countermeasures));
        }

        Assert.Equal(0, countermeasures.ActiveCountermeasureCount);
        Assert.Contains(allUpdates, update =>
            !update.IsDelete && update.ObjectId == flare.ObjectId);
        Assert.Contains(allUpdates, update =>
            !update.IsDelete && update.ObjectId == chaff.ObjectId);
        Assert.Contains(allUpdates, update =>
            update.IsDelete && update.ObjectId == flare.ObjectId);
        Assert.Contains(allUpdates, update =>
            update.IsDelete && update.ObjectId == chaff.ObjectId);
    }

    private static CountermeasureRegistry CreateRegistry(
        EntityRegistry entityRegistry,
        CountermeasureSamplingSettings settings = null,
        WarningThrottle warningThrottle = null,
        ICountermeasureWarningSink warningSink = null)
    {
        return new CountermeasureRegistry(
            entityRegistry,
            settings,
            null,
            warningThrottle,
            warningSink);
    }

    private static CountermeasureSpawnRequest Request(
        CountermeasureKind kind,
        IEntityTransformSource source,
        EntityTransform? spawnTransform,
        ulong? parentObjectId = null,
        double? maximumLifetimeSeconds = null)
    {
        return new CountermeasureSpawnRequest(
            kind,
            "Allies",
            "Blue",
            parentObjectId,
            source,
            spawnTransform,
            maximumLifetimeSeconds);
    }

    private static TrackedEntity RegisterOwner(EntityRegistry entityRegistry)
    {
        return entityRegistry.Register(
            new object(),
            EntityKind.Aircraft,
            true,
            Metadata("Owner", "Air+FixedWing"));
    }

    private static EntityMetadata Metadata(string name, string type)
    {
        return new EntityMetadata(type, name, "Allies", "Blue");
    }

    private static List<TacviewEntityUpdate> DrainNextFrame(CountermeasureRegistry countermeasures)
    {
        countermeasures.BeginFrame();
        var updates = new List<TacviewEntityUpdate>();
        countermeasures.DrainFrameDataTo(updates);
        return updates;
    }

    private static void DrainUntilRemoved(
        CountermeasureRegistry countermeasures,
        ulong expectedObjectId)
    {
        for (int pass = 0; pass < 8 && countermeasures.ActiveCountermeasureCount > 2; pass++)
        {
            List<TacviewEntityUpdate> updates = DrainNextFrame(countermeasures);
            if (updates.Count > 0)
            {
                Assert.Equal(expectedObjectId, updates[0].ObjectId);
            }
        }

        Assert.Equal(2, countermeasures.ActiveCountermeasureCount);
    }

    private static EntityTransform Transform(
        double longitude = 7.0,
        double latitude = 51.0,
        double altitude = 1000.0)
    {
        return new EntityTransform(
            longitude,
            latitude,
            altitude,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0);
    }

    private sealed class MutableSource : IEntityTransformSource
    {
        public MutableSource(EntityTransform transform)
        {
            Transform = transform;
        }

        public EntityTransform Transform { get; set; }

        public bool TryGetTransform(out EntityTransform transform)
        {
            transform = Transform;
            return true;
        }
    }

    private sealed class ThrowingSource : IEntityTransformSource
    {
        public bool TryGetTransform(out EntityTransform transform)
        {
            transform = default;
            throw new InvalidOperationException("simulated countermeasure source fault");
        }
    }

    private sealed class CollectingWarningSink : ICountermeasureWarningSink
    {
        public List<CountermeasureWarning> Warnings { get; } =
            new List<CountermeasureWarning>();

        public void Warn(CountermeasureWarning warning)
        {
            Warnings.Add(warning);
        }
    }
}
