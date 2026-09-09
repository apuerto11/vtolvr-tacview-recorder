using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using VTOLVRTacviewSPRecorder.Core;
using Xunit;

namespace VTOLVRTacviewSPRecorder.Core.Tests;

public sealed class TacviewEntityTests
{
    private static readonly DateTimeOffset ReferenceTime =
        new DateTimeOffset(2026, 8, 10, 12, 34, 56, 789, TimeSpan.Zero);

    [Fact]
    public void DefaultClassificationUsesOfficialTacviewTags()
    {
        Assert.Equal("Air", TacviewEntityType.GetDefault(EntityKind.Aircraft));
        Assert.Equal("Weapon+Missile", TacviewEntityType.GetDefault(EntityKind.Missile));
        Assert.Equal("Weapon+Rocket", TacviewEntityType.GetDefault(EntityKind.Rocket));
        Assert.Equal("Weapon+Bomb", TacviewEntityType.GetDefault(EntityKind.Bomb));
        Assert.Equal("Weapon+Projectile", TacviewEntityType.GetDefault(EntityKind.Projectile));
        Assert.Equal("Ground+Vehicle", TacviewEntityType.GetDefault(EntityKind.Ground));
        Assert.Equal("Sea+Watercraft", TacviewEntityType.GetDefault(EntityKind.Sea));
        Assert.Equal("Ground+Static", TacviewEntityType.GetDefault(EntityKind.Static));
    }

    [Fact]
    public void CoalitionPaletteMapsAlliedEnemyAndDefensiveNeutralExactly()
    {
        Assert.Equal("Allies", TacviewCoalitionPalette.GetCoalition(TacviewCoalitionSide.Allied));
        Assert.Equal("Blue", TacviewCoalitionPalette.GetColor(TacviewCoalitionSide.Allied));
        Assert.Equal("Enemies", TacviewCoalitionPalette.GetCoalition(TacviewCoalitionSide.Enemy));
        Assert.Equal("Red", TacviewCoalitionPalette.GetColor(TacviewCoalitionSide.Enemy));
        Assert.Equal("Neutral", TacviewCoalitionPalette.GetCoalition(TacviewCoalitionSide.Neutral));
        Assert.Equal("White", TacviewCoalitionPalette.GetColor(TacviewCoalitionSide.Neutral));
    }

    [Fact]
    public void WriterSerializesMixedEntitiesAndOfficialRelationships()
    {
        var text = new StringWriter();
        using var writer = new TacviewEntityWriter(text, "Mixed", ReferenceTime, leaveOpen: true);
        var updates = new List<TacviewEntityUpdate>
        {
            Upsert(0x100, EntityKind.Aircraft, "Ownship", "Allies", "Blue"),
            Upsert(0x101, EntityKind.Missile, "AIM-120", "Allies", "Blue", 0x100, 0x102),
            Upsert(0x102, EntityKind.Aircraft, "Bandit", "Enemies", "Red"),
            Upsert(0x103, EntityKind.Ground, "SAM", "Enemies", "Red"),
            Upsert(0x104, EntityKind.Sea, "Cruiser", "Enemies", "Red"),
            TacviewEntityUpdate.Upsert(
                0x105,
                Transform(),
                new EntityMetadata(
                    "Navaid+Static+Waypoint",
                    "Waypoint 1",
                    "Neutral",
                    "White",
                    nextObjectId: 0x106)),
            TacviewEntityUpdate.Upsert(
                0x106,
                Transform(),
                new EntityMetadata(
                    "Navaid+Static+Waypoint",
                    "Waypoint 2",
                    "Neutral",
                    "White"))
        };

        writer.WriteFrame(0.0, updates, new List<TacviewEvent>());

        string output = text.ToString();
        Assert.Contains("101,T=", output);
        Assert.Contains("Type=Weapon+Missile", output);
        Assert.Contains("Parent=100,LockedTarget=102", output);
        Assert.Contains("Type=Ground+Vehicle", output);
        Assert.Contains("Type=Sea+Watercraft", output);
        Assert.Contains("Next=106", output);
        Assert.DoesNotContain("Launcher=", output);
        Assert.DoesNotContain(",Target=", output);
    }

    [Fact]
    public void WriterSuppressesFullyUnchangedObjectsAndKeepsDeltasIndependent()
    {
        var text = new StringWriter();
        using var writer = new TacviewEntityWriter(text, "Deltas", ReferenceTime, leaveOpen: true);
        var events = new List<TacviewEvent>();

        writer.WriteFrame(0.0, new List<TacviewEntityUpdate>
        {
            Upsert(0x101, EntityKind.Aircraft, "One", transform: Transform(longitude: 1.0)),
            Upsert(0x102, EntityKind.Aircraft, "Two", transform: Transform(longitude: 10.0))
        }, events);
        writer.WriteFrame(0.1, new List<TacviewEntityUpdate>
        {
            Upsert(0x101, EntityKind.Aircraft, "One", transform: Transform(longitude: 2.0)),
            Upsert(0x102, EntityKind.Aircraft, "Two", transform: Transform(longitude: 10.0))
        }, events);

        string secondFrame = text.ToString().Split(new[] { "#0.100\n" }, StringSplitOptions.None)[1];
        Assert.StartsWith("101,T=2.0000000||||||||\n", secondFrame);
        Assert.DoesNotContain("102,", secondFrame);
    }

    [Fact]
    public void WriterEmitsDestroyedTimeoutAndLeftAreaOnlyFromExplicitData()
    {
        var text = new StringWriter();
        using var writer = new TacviewEntityWriter(text, "Lifecycle", ReferenceTime, leaveOpen: true);
        var noEvents = new List<TacviewEvent>();

        writer.WriteFrame(0.0, new List<TacviewEntityUpdate>
        {
            Upsert(0x100, EntityKind.Aircraft, "Launcher"),
            Upsert(0x101, EntityKind.Missile, "FOX 3", parent: 0x100),
            Upsert(0x102, EntityKind.Rocket, "Hydra", parent: 0x100),
            Upsert(0x103, EntityKind.Bomb, "GBU", parent: 0x100),
            Upsert(0x200, EntityKind.Aircraft, "Target")
        }, noEvents);
        writer.WriteFrame(1.0, new List<TacviewEntityUpdate>
        {
            TacviewEntityUpdate.Delete(0x101, TacviewEntityRemovalReason.Destroyed, "FOX 3")
        }, noEvents);
        writer.WriteFrame(2.0, new List<TacviewEntityUpdate>
        {
            TacviewEntityUpdate.Delete(0x102, TacviewEntityRemovalReason.Deleted, "Hydra")
        }, new List<TacviewEvent>
        {
            TacviewEvent.Timeout(0x100, "Hydra", 0x200, "Miss")
        });
        writer.WriteFrame(3.0, new List<TacviewEntityUpdate>
        {
            TacviewEntityUpdate.Delete(0x103, TacviewEntityRemovalReason.LeftArea, "GBU")
        }, noEvents);

        string output = text.ToString();
        Assert.Contains("Event=Destroyed|101|FOX 3 destroyed\n-101", output);
        Assert.Contains("Event=Timeout|SourceId:100|AmmoType:Hydra|AmmoCount:1|TargetId:200|Outcome:Miss\n-102", output);
        Assert.Contains("Event=LeftArea|103|GBU left area\n-103", output);
        Assert.DoesNotContain("Event=Fired", output);
        Assert.DoesNotContain("Event=Hit", output);
    }

    [Fact]
    public void WriterSuppressesUnknownDeleteAndUnknownEventReferences()
    {
        var text = new StringWriter();
        using var writer = new TacviewEntityWriter(text, "Unknown references", ReferenceTime, leaveOpen: true);

        writer.WriteFrame(0.0, new List<TacviewEntityUpdate>
        {
            Upsert(0x102, EntityKind.Missile, "Missile", parent: 0x101, target: 0x103),
            TacviewEntityUpdate.Delete(0x101, TacviewEntityRemovalReason.Destroyed, "Short lived actor")
        }, new List<TacviewEvent>
        {
            TacviewEvent.Timeout(0x101, "Missile", 0x103, "Miss")
        });

        string output = text.ToString();
        Assert.Contains("102,T=", output);
        Assert.DoesNotContain("Parent=101", output);
        Assert.DoesNotContain("LockedTarget=103", output);
        Assert.DoesNotContain("SourceId:101", output);
        Assert.DoesNotContain("TargetId:103", output);
        Assert.DoesNotContain("Event=Destroyed|101", output);
        Assert.DoesNotContain("-101", output);
        Assert.Throws<InvalidOperationException>(() => writer.WriteFrame(
            0.1,
            new List<TacviewEntityUpdate>
            {
                Upsert(0x101, EntityKind.Ground, "Reused")
            },
            new List<TacviewEvent>()));
    }

    [Fact]
    public void WriterAcceptsSameFrameMaterializedRelationshipsAndTimeoutTarget()
    {
        var text = new StringWriter();
        using var writer = new TacviewEntityWriter(text, "Known references", ReferenceTime, leaveOpen: true);

        writer.WriteFrame(0.0, new List<TacviewEntityUpdate>
        {
            Upsert(0x100, EntityKind.Aircraft, "Launcher"),
            Upsert(0x101, EntityKind.Aircraft, "Target"),
            Upsert(0x102, EntityKind.Missile, "Missile", parent: 0x100, target: 0x101)
        }, new List<TacviewEvent>
        {
            TacviewEvent.Timeout(0x100, "Missile", 0x101, "Miss")
        });

        string output = text.ToString();
        Assert.Contains("Parent=100,LockedTarget=101", output);
        Assert.Contains("SourceId:100", output);
        Assert.Contains("TargetId:101", output);
    }

    [Fact]
    public void WriterRetriesRelationshipAfterReferencedEntityMaterializes()
    {
        var text = new StringWriter();
        using var writer = new TacviewEntityWriter(text, "Deferred relationship", ReferenceTime, leaveOpen: true);
        TacviewEntityUpdate weapon = Upsert(
            0x102,
            EntityKind.Missile,
            "Missile",
            parent: 0x101);

        writer.WriteFrame(
            0.0,
            new List<TacviewEntityUpdate> { weapon },
            new List<TacviewEvent>());
        writer.WriteFrame(0.1, new List<TacviewEntityUpdate>
        {
            Upsert(0x101, EntityKind.Aircraft, "Late launcher"),
            weapon
        }, new List<TacviewEvent>());

        string[] frames = text.ToString().Split(new[] { "#0.100\n" }, StringSplitOptions.None);
        Assert.DoesNotContain("Parent=101", frames[0]);
        Assert.Contains("102,Parent=101", frames[1]);
    }

    [Fact]
    public void WriterKeepsTimeoutForKnownSourceButOmitsUnknownTarget()
    {
        var text = new StringWriter();
        using var writer = new TacviewEntityWriter(text, "Partial timeout", ReferenceTime, leaveOpen: true);

        writer.WriteFrame(0.0, new List<TacviewEntityUpdate>
        {
            Upsert(0x100, EntityKind.Aircraft, "Launcher")
        }, new List<TacviewEvent>
        {
            TacviewEvent.Timeout(0x100, "Missile", 0x999, "Miss")
        });

        string output = text.ToString();
        Assert.Contains("Event=Timeout|SourceId:100|AmmoType:Missile|AmmoCount:1|Outcome:Miss", output);
        Assert.DoesNotContain("TargetId:999", output);
    }

    [Fact]
    public void WriterPreservesFinalActorSnapshotBeforeFollowupDelete()
    {
        var text = new StringWriter();
        using var writer = new TacviewEntityWriter(text, "Final actor position", ReferenceTime, leaveOpen: true);

        writer.WriteFrame(0.0, new List<TacviewEntityUpdate>
        {
            Upsert(0x101, EntityKind.Ground, "Ground unit", transform: Transform(longitude: 1.0))
        }, new List<TacviewEvent>());
        writer.WriteFrame(0.5, new List<TacviewEntityUpdate>
        {
            Upsert(0x101, EntityKind.Ground, "Ground unit", transform: Transform(longitude: 2.0))
        }, new List<TacviewEvent>());
        writer.WriteFrame(0.525, new List<TacviewEntityUpdate>
        {
            TacviewEntityUpdate.Delete(0x101, TacviewEntityRemovalReason.Destroyed, "Ground unit")
        }, new List<TacviewEvent>());

        string output = text.ToString();
        int finalTransform = output.IndexOf("#0.500\n101,T=2.0000000", StringComparison.Ordinal);
        int deletion = output.IndexOf("#0.525\n0,Event=Destroyed|101", StringComparison.Ordinal);
        Assert.True(finalTransform >= 0);
        Assert.True(deletion > finalTransform);
    }

    [Fact]
    public void WriterUsesEventlessDeleteForGenericDisappearance()
    {
        var text = new StringWriter();
        using var writer = new TacviewEntityWriter(text, "Generic disappearance", ReferenceTime, leaveOpen: true);

        writer.WriteFrame(0.0, new List<TacviewEntityUpdate>
        {
            Upsert(0x101, EntityKind.Sea, "Ship")
        }, new List<TacviewEvent>());
        writer.WriteFrame(0.5, new List<TacviewEntityUpdate>
        {
            TacviewEntityUpdate.Delete(0x101, TacviewEntityRemovalReason.Deleted, "Ship")
        }, new List<TacviewEvent>());

        string frame = text.ToString().Split(new[] { "#0.500\n" }, StringSplitOptions.None)[1];
        Assert.StartsWith("-101\n", frame);
        Assert.DoesNotContain("Event=", frame);
    }

    [Fact]
    public void WriterAllowsReferencesToMaterializedHistoricalObjects()
    {
        var text = new StringWriter();
        using var writer = new TacviewEntityWriter(text, "Historical reference", ReferenceTime, leaveOpen: true);

        writer.WriteFrame(0.0, new List<TacviewEntityUpdate>
        {
            Upsert(0x100, EntityKind.Aircraft, "Launcher"),
            Upsert(0x101, EntityKind.Aircraft, "Target")
        }, new List<TacviewEvent>());
        writer.WriteFrame(0.1, new List<TacviewEntityUpdate>
        {
            TacviewEntityUpdate.Delete(0x101, TacviewEntityRemovalReason.Destroyed, "Target")
        }, new List<TacviewEvent>());
        writer.WriteFrame(0.2, new List<TacviewEntityUpdate>
        {
            Upsert(0x102, EntityKind.Missile, "Missile", parent: 0x100, target: 0x101)
        }, new List<TacviewEvent>
        {
            TacviewEvent.Timeout(0x100, "Missile", 0x101, "Miss")
        });

        string frame = text.ToString().Split(new[] { "#0.200\n" }, StringSplitOptions.None)[1];
        Assert.Contains("Parent=100,LockedTarget=101", frame);
        Assert.Contains("TargetId:101", frame);
    }

    [Fact]
    public void WriterUsesOneFlushForMixedFrameAndSkipsEmptyDeltaFrame()
    {
        var text = new CountingWriter();
        using var writer = new TacviewEntityWriter(text, "Flush", ReferenceTime, leaveOpen: true);
        var update = Upsert(0x101, EntityKind.Ground, "Truck");

        writer.WriteFrame(0.0, new List<TacviewEntityUpdate> { update }, new List<TacviewEvent>());
        writer.WriteFrame(0.5, new List<TacviewEntityUpdate> { update }, new List<TacviewEvent>());

        Assert.Equal(1, text.FlushCount);
        Assert.Equal(1, Count(text.ToString(), "#"));
    }

    [Fact]
    public void HeaderContainsRealMissionContextWithoutReferenceOffsets()
    {
        var text = new StringWriter();
        using var writer = new TacviewEntityWriter(
            text,
            "Mission",
            ReferenceTime,
            new TacviewRecordingMetadata("Akutan", "Singleplayer", "Briefing"),
            leaveOpen: true);

        string output = text.ToString();
        Assert.Contains("0,Theater=Akutan\n", output);
        Assert.Contains("0,Category=Singleplayer\n", output);
        Assert.Contains("0,Briefing=Briefing\n", output);
        Assert.DoesNotContain("ReferenceLongitude", output);
        Assert.DoesNotContain("ReferenceLatitude", output);
    }

    [Fact]
    public void FlushFailureDoesNotCommitEntityDeltaState()
    {
        var text = new CountingWriter { ThrowOnFlush = true };
        using var writer = new TacviewEntityWriter(text, "Failure", ReferenceTime, leaveOpen: true);
        var updates = new List<TacviewEntityUpdate>
        {
            Upsert(0x100, EntityKind.Aircraft, "Launcher"),
            Upsert(0x101, EntityKind.Missile, "Missile", parent: 0x100)
        };

        Assert.Throws<IOException>(() => writer.WriteFrame(0.0, updates, new List<TacviewEvent>()));
        text.ThrowOnFlush = false;
        writer.WriteFrame(0.1, updates, new List<TacviewEvent>());

        string lastFrame = text.ToString().Split(new[] { "#0.100\n" }, StringSplitOptions.None)[1];
        Assert.Contains("Type=Weapon+Missile", lastFrame);
        Assert.Contains("Parent=100", lastFrame);
    }

    [Fact]
    public void AtomicMoveFailureRetainsTemporaryRecording()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TacviewEntityWriterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var writer = TacviewEntityWriter.CreateFile(
                directory,
                "Move Failure",
                ReferenceTime,
                null,
                new ThrowingMover());
            string finalPath = writer.FinalPath;
            string temporaryPath = finalPath + ".tmp";
            writer.WriteFrame(0.0, new List<TacviewEntityUpdate>
            {
                Upsert(0x100, EntityKind.Aircraft, "Ownship")
            }, new List<TacviewEvent>());

            Assert.Throws<IOException>(() => writer.Dispose());
            Assert.True(File.Exists(temporaryPath));
            Assert.False(File.Exists(finalPath));
            writer.Dispose();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AbortedEntityWriterRetainsTemporaryRecording()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TacviewEntityWriterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var writer = TacviewEntityWriter.CreateFile(
                directory,
                "Aborted",
                ReferenceTime);
            string finalPath = writer.FinalPath;
            string temporaryPath = finalPath + ".tmp";
            writer.WriteFrame(0.0, new List<TacviewEntityUpdate>
            {
                Upsert(0x100, EntityKind.Aircraft, "Ownship")
            }, new List<TacviewEvent>());

            writer.Abort();
            writer.Abort();
            Assert.True(File.Exists(temporaryPath));
            Assert.False(File.Exists(finalPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TenThousandEntityCorePassIsBoundedAndLinear()
    {
        const int entityCount = 10_000;
        var registry = new EntityRegistry();
        var targets = new List<EntitySamplingTarget>(entityCount);
        var updates = new List<EntityTransformUpdate>(entityCount);
        var source = new FixedEntitySource(Transform());
        long before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();

        for (int index = 0; index < entityCount; index++)
        {
            EntityKind kind = index % 4 == 0 ? EntityKind.Aircraft :
                index % 4 == 1 ? EntityKind.Missile :
                index % 4 == 2 ? EntityKind.Ground : EntityKind.Sea;
            TrackedEntity entity = registry.Register(
                new object(),
                kind,
                false,
                Metadata(kind, "Synthetic" + index));
            targets.Add(new EntitySamplingTarget(entity, source));
        }

        var collector = new EntityFrameCollector(new EntitySampleScheduler());
        collector.Collect(0.0, targets, updates, null);
        stopwatch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(entityCount, registry.Count);
        Assert.Equal(entityCount, updates.Count);
        Assert.True(allocated < 64L * 1024L * 1024L, $"Allocated {allocated:N0} bytes.");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Elapsed {stopwatch.Elapsed}.");
    }

    private static TacviewEntityUpdate Upsert(
        ulong id,
        EntityKind kind,
        string name,
        string coalition = "Allies",
        string color = "Blue",
        ulong? parent = null,
        ulong? target = null,
        EntityTransform? transform = null)
    {
        return TacviewEntityUpdate.Upsert(
            id,
            transform ?? Transform(),
            new EntityMetadata(
                TacviewEntityType.GetDefault(kind),
                name,
                coalition,
                color,
                parentObjectId: parent,
                lockedTargetObjectId: target));
    }

    private static EntityMetadata Metadata(EntityKind kind, string name)
    {
        return new EntityMetadata(TacviewEntityType.GetDefault(kind), name, "Allies", "Blue");
    }

    private static EntityTransform Transform(double longitude = 7.0)
    {
        return new EntityTransform(longitude, 51.0, 1000.0, 0.0, 0.0, 0.0, 10.0, 20.0, 0.0);
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private sealed class FixedEntitySource : IEntityTransformSource
    {
        private readonly EntityTransform _transform;

        public FixedEntitySource(EntityTransform transform)
        {
            _transform = transform;
        }

        public bool TryGetTransform(out EntityTransform transform)
        {
            transform = _transform;
            return true;
        }
    }

    private sealed class CountingWriter : StringWriter
    {
        public int FlushCount { get; private set; }

        public bool ThrowOnFlush { get; set; }

        public override void Flush()
        {
            FlushCount++;
            if (ThrowOnFlush)
            {
                throw new IOException("Simulated flush failure.");
            }

            base.Flush();
        }
    }

    private sealed class ThrowingMover : IAtomicFileMover
    {
        public void Move(string sourcePath, string destinationPath)
        {
            throw new IOException("Simulated atomic move failure.");
        }
    }
}
