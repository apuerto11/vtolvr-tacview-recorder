using System;
using System.Collections.Generic;
using System.IO;
using VTOLVRTacviewSPRecorder.Core;
using Xunit;

namespace VTOLVRTacviewSPRecorder.Core.Tests;

public sealed class V040CoreTests
{
    [Fact]
    public void ShapeIsWrittenInitiallyAndThenDeltaEncoded()
    {
        var text = new StringWriter();
        using var writer = new TacviewEntityWriter(
            text,
            "Shape delta test",
            DateTimeOffset.Parse("2026-08-11T12:00:00Z"),
            leaveOpen: true);
        EntityTransform transform = Transform();
        EntityMetadata metadata = Metadata("F/A-26B", "FixedWing.F-18E.obj");

        writer.WriteFrame(0.0, Updates(0x100, transform, metadata), NoEvents());
        writer.WriteFrame(0.1, Updates(0x100, transform, metadata), NoEvents());

        string acmi = text.ToString();
        Assert.Contains(
            "100,T=7.0000000|51.0000000|1000.00|0.00|0.00|0.00|0.00|0.00|0.00" +
            ",Type=Air+FixedWing,Name=F/A-26B,Shape=FixedWing.F-18E.obj",
            acmi);
        Assert.Equal(1, CountOccurrences(acmi, "Shape=FixedWing.F-18E.obj"));
        Assert.DoesNotContain("#0.100", acmi);
    }

    [Fact]
    public void ShapeChangeIsSerializedWithoutReplacingFictionalIdentity()
    {
        var text = new StringWriter();
        using var writer = new TacviewEntityWriter(
            text,
            "Shape change test",
            DateTimeOffset.Parse("2026-08-11T12:00:00Z"),
            leaveOpen: true);
        EntityTransform transform = Transform();

        writer.WriteFrame(
            0.0,
            Updates(0x100, transform, Metadata("F/A-26B", "FixedWing.F-18E.obj")),
            NoEvents());
        writer.WriteFrame(
            0.1,
            Updates(0x100, transform, Metadata("F/A-26B", "FixedWing.F-15.obj")),
            NoEvents());

        string acmi = text.ToString();
        Assert.Contains("#0.100\n100,Shape=FixedWing.F-15.obj\n", acmi);
        Assert.Equal(1, CountOccurrences(acmi, "Name=F/A-26B"));
        Assert.DoesNotContain("Name=F-15", acmi);
        Assert.DoesNotContain("CallSign=F-15", acmi);
        Assert.DoesNotContain("Pilot=F-15", acmi);
    }

    [Fact]
    public void DedicatedCountermeasureKindsResolveToDecoySampling()
    {
        var registry = new EntityRegistry();
        var countermeasures = new CountermeasureRegistry(registry);

        Assert.True(countermeasures.TrySpawn(
            new CountermeasureSpawnRequest(
                CountermeasureKind.Flare,
                "Allies",
                "Blue",
                spawnTransform: Transform()),
            0.0,
            out CountermeasureHandle flare));
        Assert.True(countermeasures.TrySpawn(
            new CountermeasureSpawnRequest(
                CountermeasureKind.Chaff,
                "Allies",
                "Blue",
                spawnTransform: Transform()),
            0.0,
            out CountermeasureHandle chaff));

        Assert.Equal(EntityKind.Flare, flare.Entity.Kind);
        Assert.Equal(EntityKind.Chaff, chaff.Entity.Kind);
        Assert.Equal(SamplingClass.Decoy, flare.Entity.SamplingClass);
        Assert.Equal(SamplingClass.Decoy, chaff.Entity.SamplingClass);
        Assert.Equal(0.25, EntitySampleScheduler.GetIntervalSeconds(SamplingClass.Decoy));
    }

    private static EntityMetadata Metadata(string name, string shape)
    {
        return new EntityMetadata(
            "Air+FixedWing",
            name,
            "Allies",
            "Blue",
            callSign: "Wasp 1",
            pilot: "Test Pilot",
            shape: shape);
    }

    private static EntityTransform Transform()
    {
        return new EntityTransform(7.0, 51.0, 1000.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
    }

    private static List<TacviewEntityUpdate> Updates(
        ulong objectId,
        EntityTransform transform,
        EntityMetadata metadata)
    {
        return new List<TacviewEntityUpdate>
        {
            TacviewEntityUpdate.Upsert(objectId, transform, metadata)
        };
    }

    private static List<TacviewEvent> NoEvents()
    {
        return new List<TacviewEvent>();
    }

    private static int CountOccurrences(string value, string needle)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }

        return count;
    }
}
