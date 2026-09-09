using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VTOLVRTacviewSPRecorder.Core;

namespace VTOLVRTacviewSPRecorder.Tools.SyntheticAcmi;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("Usage: SyntheticAcmi <absolute-output-path>");
            return 2;
        }

        string outputPath = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        string acmi = Generate(out SyntheticEvidence evidence);
        Validate(acmi, evidence);
        File.WriteAllText(outputPath, acmi, new UTF8Encoding(true));

        Console.WriteLine($"SYNTHETIC_ACMI={outputPath}");
        Console.WriteLine($"AIRCRAFT_MAPPINGS={evidence.Aircraft.Count}");
        Console.WriteLine($"GROUND_MAPPINGS={evidence.GroundVehicles.Count}");
        Console.WriteLine($"FLARE_ID={evidence.Flare.ObjectId:X}");
        Console.WriteLine($"CHAFF_ID={evidence.Chaff.ObjectId:X}");
        Console.WriteLine($"PARENT_ID={evidence.Parent.ObjectId:X}");
        Console.WriteLine($"BYTES={new FileInfo(outputPath).Length}");
        return 0;
    }

    private static string Generate(out SyntheticEvidence evidence)
    {
        var text = new StringWriter();
        var registry = new EntityRegistry();
        var aircraft = new List<TrackedEntity>();
        var groundVehicles = new List<TrackedEntity>();
        var initialUpdates = new List<TacviewEntityUpdate>();
        TrackedEntity parent = null;

        using (var writer = new TacviewEntityWriter(
            text,
            "VTOL VR 0.4.2 all-shapes and decoys validation",
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
            leaveOpen: true))
        {
            IReadOnlyList<VtolAircraftShapeMapping> mappings = VtolAircraftShapeCatalog.Entries;
            for (int index = 0; index < mappings.Count; index++)
            {
                VtolAircraftShapeMapping mapping = mappings[index];
                bool ownship = string.Equals(
                    mapping.AircraftName,
                    "F/A-26B",
                    StringComparison.Ordinal);
                string type = mapping.Shape.StartsWith("Rotorcraft.", StringComparison.Ordinal)
                    ? "Air+Rotorcraft"
                    : "Air+FixedWing";
                var metadata = new EntityMetadata(
                    type,
                    mapping.AircraftName,
                    ownship ? "Allies" : "Enemies",
                    ownship ? "Blue" : "Red",
                    callSign: ownship ? "Wasp 1" : null,
                    pilot: ownship ? "Synthetic Pilot" : null,
                    shape: mapping.Shape);
                TrackedEntity entity = registry.Register(
                    new object(),
                    EntityKind.Aircraft,
                    ownship,
                    metadata);
                aircraft.Add(entity);
                if (ownship)
                {
                    parent = entity;
                }

                initialUpdates.Add(TacviewEntityUpdate.Upsert(
                    entity.ObjectId,
                    Transform(index, 1000.0 + (index * 25.0)),
                    entity.Metadata));
            }

            if (parent == null)
            {
                throw new InvalidOperationException("Synthetic ownship mapping is missing.");
            }

            IReadOnlyList<VtolGroundShapeMapping> groundMappings =
                VtolGroundShapeCatalog.Entries;
            for (int index = 0; index < groundMappings.Count; index++)
            {
                VtolGroundShapeMapping mapping = groundMappings[index];
                var metadata = new EntityMetadata(
                    "Ground+Vehicle",
                    mapping.VehicleName,
                    "Enemies",
                    "Red",
                    shape: mapping.Shape);
                TrackedEntity entity = registry.Register(
                    new object(),
                    EntityKind.Ground,
                    isOwnship: false,
                    metadata);
                groundVehicles.Add(entity);
                initialUpdates.Add(TacviewEntityUpdate.Upsert(
                    entity.ObjectId,
                    Transform(30 + index, 50.0),
                    entity.Metadata));
            }

            writer.WriteFrame(0.0, initialUpdates, new List<TacviewEvent>());

            var flareSource = new MutableSource(Transform(40, 950.0));
            var chaffSource = new MutableSource(Transform(41, 945.0));
            var countermeasures = new CountermeasureRegistry(registry);
            if (!countermeasures.TrySpawn(
                    new CountermeasureSpawnRequest(
                        CountermeasureKind.Flare,
                        parent.Metadata.Coalition,
                        parent.Metadata.Color,
                        parent.ObjectId,
                        flareSource,
                        flareSource.Transform),
                    0.01,
                    out CountermeasureHandle flare) ||
                !countermeasures.TrySpawn(
                    new CountermeasureSpawnRequest(
                        CountermeasureKind.Chaff,
                        parent.Metadata.Coalition,
                        parent.Metadata.Color,
                        parent.ObjectId,
                        chaffSource,
                        chaffSource.Transform),
                    0.01,
                    out CountermeasureHandle chaff))
            {
                throw new InvalidOperationException("Synthetic countermeasure spawn failed.");
            }

            Drain(writer, countermeasures, 0.01);

            flareSource.Transform = Transform(42, 900.0);
            chaffSource.Transform = Transform(43, 890.0);
            countermeasures.Refresh(0.26);
            Drain(writer, countermeasures, 0.26);

            countermeasures.MarkForRemoval(flare, 0.30, Transform(44, 850.0));
            countermeasures.MarkForRemoval(chaff, 0.30, Transform(45, 840.0));
            Drain(writer, countermeasures, 0.30);
            Drain(writer, countermeasures, 0.301);

            if (countermeasures.ActiveCountermeasureCount != 0)
            {
                throw new InvalidOperationException("Synthetic countermeasure lifecycle did not drain.");
            }

            evidence = new SyntheticEvidence(
                aircraft,
                groundVehicles,
                parent,
                flare,
                chaff);
        }

        return text.ToString();
    }

    private static void Drain(
        TacviewEntityWriter writer,
        CountermeasureRegistry countermeasures,
        double time)
    {
        countermeasures.BeginFrame();
        var updates = new List<TacviewEntityUpdate>();
        countermeasures.DrainFrameDataTo(updates);
        if (updates.Count > 0)
        {
            writer.WriteFrame(time, updates, new List<TacviewEvent>());
        }
    }

    private static void Validate(string acmi, SyntheticEvidence evidence)
    {
        Require(acmi.StartsWith("FileType=text/acmi/tacview\nFileVersion=2.2\n", StringComparison.Ordinal),
            "ACMI 2.2 header is missing.");
        Require(evidence.Aircraft.Count == 17, "Expected all 17 aircraft mappings.");
        Require(evidence.GroundVehicles.Count == 6, "Expected all six ground mappings.");

        foreach (TrackedEntity entity in evidence.Aircraft)
        {
            string expected = $"Name={entity.Metadata.Name},Shape={entity.Metadata.Shape}";
            Require(acmi.Contains(expected, StringComparison.Ordinal),
                $"Missing aircraft mapping: {expected}");
        }

        foreach (TrackedEntity entity in evidence.GroundVehicles)
        {
            string expected = $"Name={entity.Metadata.Name},Shape={entity.Metadata.Shape}";
            Require(acmi.Contains(expected, StringComparison.Ordinal),
                $"Missing ground mapping: {expected}");
        }

        ValidateCountermeasure(
            acmi,
            evidence.Flare,
            "Misc+Decoy+Flare",
            "Flare",
            evidence.Parent.ObjectId);
        ValidateCountermeasure(
            acmi,
            evidence.Chaff,
            "Misc+Decoy+Chaff",
            "Chaff",
            evidence.Parent.ObjectId);

        var firstAppearanceIds = new HashSet<ulong>();
        foreach (string line in acmi.Split('\n'))
        {
            if (line.Length == 0 || line[0] == '#' || line[0] == '-' || line[0] == '0' ||
                !line.Contains(",T=", StringComparison.Ordinal))
            {
                continue;
            }

            int comma = line.IndexOf(',');
            ulong id = Convert.ToUInt64(line.Substring(0, comma), 16);
            firstAppearanceIds.Add(id);
        }

        Require(firstAppearanceIds.Count == 25,
            "Expected 17 aircraft, six ground vehicles, and two independently " +
            "materialized countermeasures.");
        Require(evidence.Flare.ObjectId != evidence.Chaff.ObjectId,
            "Countermeasure IDs must be unique.");
    }

    private static void ValidateCountermeasure(
        string acmi,
        CountermeasureHandle handle,
        string type,
        string name,
        ulong parentId)
    {
        string id = handle.ObjectId.ToString("X");
        string metadata = $"Type={type},Name={name},Coalition=Allies,Color=Blue,Parent={parentId:X}";
        Require(acmi.Contains($"{id},T=", StringComparison.Ordinal),
            $"{name} spawn transform is missing.");
        Require(acmi.Contains(metadata, StringComparison.Ordinal),
            $"{name} metadata or Parent relationship is missing.");
        int finalTransform = acmi.LastIndexOf($"{id},T=", StringComparison.Ordinal);
        int deletion = acmi.IndexOf($"-{id}\n", StringComparison.Ordinal);
        Require(finalTransform >= 0 && deletion > finalTransform,
            $"{name} must retain its last transform before Delete.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    private static EntityTransform Transform(int offset, double altitude)
    {
        return new EntityTransform(
            7.0 + (offset * 0.001),
            51.0 + (offset * 0.001),
            altitude,
            offset,
            offset * 0.5,
            offset * 2.0,
            offset * 100.0,
            offset * 110.0,
            offset * 2.0);
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

    private sealed class SyntheticEvidence
    {
        public SyntheticEvidence(
            List<TrackedEntity> aircraft,
            List<TrackedEntity> groundVehicles,
            TrackedEntity parent,
            CountermeasureHandle flare,
            CountermeasureHandle chaff)
        {
            Aircraft = aircraft;
            GroundVehicles = groundVehicles;
            Parent = parent;
            Flare = flare;
            Chaff = chaff;
        }

        public List<TrackedEntity> Aircraft { get; }

        public List<TrackedEntity> GroundVehicles { get; }

        public TrackedEntity Parent { get; }

        public CountermeasureHandle Flare { get; }

        public CountermeasureHandle Chaff { get; }
    }
}
