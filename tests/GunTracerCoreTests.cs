using System;
using System.Collections.Generic;
using System.IO;
using VTOLVRTacviewSPRecorder.Core;
using Xunit;

namespace VTOLVRTacviewSPRecorder.Core.Tests;

public sealed class GunTracerCoreTests
{
    [Fact]
    public void CadenceLimitsOneGunToTenVisualTracersPerSecond()
    {
        var entityRegistry = new EntityRegistry();
        var tracers = new GunTracerRegistry(
            entityRegistry,
            new GunTracerSettings(
                maximumTracersPerGunPerSecond: 10,
                maximumTracersPerSecond: 60,
                activeTracerCap: 32,
                sampleIntervalSeconds: 0.025));
        object gun = new object();
        var source = new MutableSource(Transform());

        for (int index = 0; index < 10; index++)
        {
            Assert.True(tracers.TrySpawn(
                Request(gun, source),
                index * 0.01,
                out GunTracerHandle _));
        }

        Assert.False(tracers.TrySpawn(Request(gun, source), 0.10, out GunTracerHandle _));
        Assert.True(tracers.TrySpawn(Request(gun, source), 1.00, out GunTracerHandle _));
    }

    [Fact]
    public void GlobalAndActiveCapsAreIndependentAndHardBounded()
    {
        var globalRegistry = new GunTracerRegistry(
            new EntityRegistry(),
            new GunTracerSettings(
                maximumTracersPerGunPerSecond: 10,
                maximumTracersPerSecond: 2,
                activeTracerCap: 32,
                sampleIntervalSeconds: 0.025));
        var source = new MutableSource(Transform());

        Assert.True(globalRegistry.TrySpawn(Request(new object(), source), 0.0, out GunTracerHandle _));
        Assert.True(globalRegistry.TrySpawn(Request(new object(), source), 0.0, out GunTracerHandle _));
        Assert.False(globalRegistry.TrySpawn(Request(new object(), source), 0.0, out GunTracerHandle _));

        var activeRegistry = new GunTracerRegistry(
            new EntityRegistry(),
            new GunTracerSettings(
                maximumTracersPerGunPerSecond: 10,
                maximumTracersPerSecond: 60,
                activeTracerCap: 2,
                sampleIntervalSeconds: 0.025));

        Assert.True(activeRegistry.TrySpawn(Request(new object(), source), 0.0, out GunTracerHandle _));
        Assert.True(activeRegistry.TrySpawn(Request(new object(), source), 0.0, out GunTracerHandle _));
        Assert.Equal(2, activeRegistry.ActiveTracerCount);
        Assert.False(activeRegistry.TrySpawn(Request(new object(), source), 0.0, out GunTracerHandle _));
    }

    [Fact]
    public void AmmoFallbackRequiresABaselineAndFailsClosedForReloadsAndAmbiguousDeltas()
    {
        var state = new GunAmmoPollState();

        Assert.Equal(
            GunAmmoPollObservation.Baseline,
            state.Observe(800, 800, isFiring: false, rpm: 4200.0, nowSeconds: 0.0));
        Assert.Equal(
            GunAmmoPollObservation.ShotObserved,
            state.Observe(799, 800, isFiring: true, rpm: 4200.0, nowSeconds: 0.025));
        Assert.Equal(
            GunAmmoPollObservation.Reloaded,
            state.Observe(800, 800, isFiring: true, rpm: 4200.0, nowSeconds: 0.050));
        Assert.Equal(
            GunAmmoPollObservation.UnconfirmedDecrease,
            state.Observe(790, 800, isFiring: true, rpm: 4200.0, nowSeconds: 0.060));
        Assert.Equal(
            GunAmmoPollObservation.UnconfirmedDecrease,
            state.Observe(789, 800, isFiring: false, rpm: 4200.0, nowSeconds: 0.085));
        Assert.Equal(
            GunAmmoPollObservation.Invalid,
            state.Observe(-1, 800, isFiring: true, rpm: 4200.0, nowSeconds: 0.100));
        Assert.Equal(
            GunAmmoPollObservation.Baseline,
            state.Observe(788, 800, isFiring: true, rpm: 4200.0, nowSeconds: 0.125));
    }

    [Fact]
    public void ReusedGunGetsDistinctTracerGenerationsAfterRealTermination()
    {
        var entityRegistry = new EntityRegistry();
        var tracers = new GunTracerRegistry(entityRegistry);
        object gun = new object();
        var source = new MutableSource(Transform());

        Assert.True(tracers.TrySpawn(Request(gun, source), 0.0, out GunTracerHandle first));
        Assert.Equal(1, tracers.ActiveTracerCount);
        Assert.True(tracers.Terminate(first, Transform(longitude: 2.0)));
        Drain(tracers, 0.0);
        Drain(tracers, 0.025);
        Drain(tracers, 0.050);
        Assert.Equal(0, tracers.ActiveTracerCount);

        Assert.True(tracers.TrySpawn(Request(gun, source), 1.0, out GunTracerHandle second));
        Assert.NotEqual(first.ObjectId, second.ObjectId);
        Assert.True(second.ObjectId > first.ObjectId);
    }

    [Fact]
    public void ForgetEmitterReleasesCadenceIdentityWithoutDeletingItsActiveTracer()
    {
        var entityRegistry = new EntityRegistry();
        var tracers = new GunTracerRegistry(entityRegistry);
        object gun = new object();
        var source = new MutableSource(Transform());

        Assert.True(tracers.TrySpawn(Request(gun, source), 0.0, out GunTracerHandle first));
        Assert.Equal(1, tracers.RememberedEmitterCount);
        Assert.Equal(1, tracers.ActiveTracerCount);

        tracers.ForgetEmitter(gun);

        Assert.Equal(0, tracers.RememberedEmitterCount);
        Assert.Equal(1, tracers.ActiveTracerCount);
        Assert.True(tracers.TrySpawn(Request(gun, source), 0.01, out GunTracerHandle second));
        Assert.NotEqual(first.ObjectId, second.ObjectId);
    }

    [Fact]
    public void LiveBulletRemainsTrackedUntilExplicitLifecycleTermination()
    {
        var entityRegistry = new EntityRegistry();
        var tracers = new GunTracerRegistry(entityRegistry);
        var source = new MutableSource(Transform(longitude: 1.0));
        Assert.True(tracers.TrySpawn(Request(new object(), source), 0.0, out GunTracerHandle handle));

        Drain(tracers, 5.0);
        Assert.Equal(1, tracers.ActiveTracerCount);

        Assert.True(tracers.Terminate(handle, Transform(longitude: 9.0)));
        Drain(tracers, 5.025);
        Drain(tracers, 5.050);
        Assert.Equal(0, tracers.ActiveTracerCount);
    }

    [Fact]
    public void WriterSequenceIsProjectileBulletWithParentAndEventlessDelete()
    {
        var entityRegistry = new EntityRegistry();
        TrackedEntity owner = entityRegistry.Register(
            new object(),
            EntityKind.Aircraft,
            true,
            new EntityMetadata("Air+FixedWing", "Ownship", "Allies", "Blue"));
        var tracers = new GunTracerRegistry(entityRegistry);
        var source = new MutableSource(Transform(longitude: 1.0));
        object gun = new object();
        Assert.True(tracers.TrySpawn(Request(gun, source, owner.ObjectId), 0.0, out GunTracerHandle tracer));

        var text = new StringWriter();
        using var writer = new TacviewEntityWriter(
            text,
            "Gun tracer lifecycle",
            DateTimeOffset.Parse("2026-08-13T12:00:00Z"),
            leaveOpen: true);

        WriteFrame(
            writer,
            0.0,
            WithOwner(owner, Drain(tracers, 0.0)));
        source.Transform = Transform(longitude: 2.0);
        WriteFrame(writer, 0.025, Drain(tracers, 0.025));
        source.Transform = Transform(longitude: 3.0);
        Assert.True(tracers.Terminate(tracer, source.Transform));
        WriteFrame(writer, 1.0, Drain(tracers, 1.0));
        WriteFrame(writer, 1.025, Drain(tracers, 1.025));

        string acmi = text.ToString();
        string objectId = tracer.ObjectId.ToString("X");
        Assert.Contains($"{objectId},T=", acmi);
        Assert.Contains("Type=Projectile+Bullet", acmi);
        Assert.Contains("Name=M61A1", acmi);
        Assert.Contains("Parent=100", acmi);
        Assert.Contains($"-{objectId}\n", acmi);
        Assert.DoesNotContain("Event=", acmi);
        Assert.DoesNotContain("Fired", acmi);
        Assert.DoesNotContain("Hit", acmi);
        Assert.DoesNotContain("Destroyed", acmi);
        Assert.DoesNotContain("Timeout", acmi);
    }

    private static GunTracerSpawnRequest Request(
        object emitterIdentity,
        IEntityTransformSource source,
        ulong? parentObjectId = null)
    {
        return new GunTracerSpawnRequest(
            emitterIdentity,
            "M61A1",
            "Allies",
            "Blue",
            Transform(),
            source,
            parentObjectId);
    }

    private static List<TacviewEntityUpdate> Drain(GunTracerRegistry tracers, double nowSeconds)
    {
        tracers.BeginFrame();
        tracers.Refresh(nowSeconds);
        var updates = new List<TacviewEntityUpdate>();
        tracers.DrainFrameDataTo(updates);
        return updates;
    }

    private static List<TacviewEntityUpdate> WithOwner(
        TrackedEntity owner,
        List<TacviewEntityUpdate> tracerUpdates)
    {
        tracerUpdates.Insert(
            0,
            TacviewEntityUpdate.Upsert(owner.ObjectId, Transform(), owner.Metadata));
        return tracerUpdates;
    }

    private static void WriteFrame(
        TacviewEntityWriter writer,
        double time,
        List<TacviewEntityUpdate> updates)
    {
        writer.WriteFrame(time, updates, new List<TacviewEvent>());
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
}
