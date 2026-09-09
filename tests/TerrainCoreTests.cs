using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using VTOLVRTacviewSPRecorder.Core;
using Xunit;

namespace VTOLVRTacviewSPRecorder.Core.Tests;

public sealed class TerrainCoreTests
{
    private static readonly DateTimeOffset ReferenceTime =
        new DateTimeOffset(2026, 8, 13, 10, 11, 12, TimeSpan.Zero);

    [Theory]
    [InlineData("  Über / Mountain: Test  ", "uber-mountain-test")]
    [InlineData("../../", "map")]
    [InlineData("East_Coast.Bay-2", "east_coast.bay-2")]
    [InlineData("地图", "map")]
    public void MapSegmentIsStableAsciiAndPathSafe(string input, string expected)
    {
        Assert.Equal(expected, TacviewTerrainId.SanitizeMapSegment(input));
    }

    [Fact]
    public void VersionedMapIdUsesManagedPrefixAndFingerprintPrefix()
    {
        string id = TacviewTerrainId.BuildVersioned(
            "East Coast/Bay",
            "0123456789ABCDEFfedcba9876543210");

        Assert.Equal("VTOLVR.east-coast-bay.0123456789abcdef", id);
        Assert.True(TacviewTerrainId.IsManaged(id));
        Assert.Throws<ArgumentException>(() => TacviewTerrainId.BuildVersioned("map", "not-a-hash"));
        Assert.False(TacviewTerrainId.IsManaged("VTOLVR../foreign"));
        Assert.True(TacviewTerrainId.SanitizeMapSegment(new string('a', 100)).Length <= 48);
    }

    [Fact]
    public void FingerprintIsCultureIndependentAndChangesWithTerrain()
    {
        TacviewTerrainFingerprintInput first = FingerprintInput();
        CultureInfo original = CultureInfo.CurrentCulture;
        string germanHash;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            germanHash = TacviewTerrainId.ComputeFingerprint(first);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        string invariantHash = TacviewTerrainId.ComputeFingerprint(FingerprintInput());
        TacviewTerrainFingerprintInput changed = FingerprintInput();
        changed.SparseHeightSamples[4] += 1.0;
        string changedHash = TacviewTerrainId.ComputeFingerprint(changed);

        Assert.Equal(64, invariantHash.Length);
        Assert.Equal(invariantHash, germanHash);
        Assert.NotEqual(invariantHash, changedHash);
    }

    [Fact]
    public void RasterCoordinatesAreWestToEastAndNorthToSouth()
    {
        Assert.Equal(0.0, TacviewTerrainRaster.NormalizedXForColumn(0, 3));
        Assert.Equal(0.5, TacviewTerrainRaster.NormalizedXForColumn(1, 3));
        Assert.Equal(1.0, TacviewTerrainRaster.NormalizedXForColumn(2, 3));

        Assert.Equal(1.0, TacviewTerrainRaster.NormalizedZForRawRow(0, 3));
        Assert.Equal(0.5, TacviewTerrainRaster.NormalizedZForRawRow(1, 3));
        Assert.Equal(0.0, TacviewTerrainRaster.NormalizedZForRawRow(2, 3));
    }

    [Fact]
    public void StaticTerrainGridUnionsAdjacentTilesAndKeepsRawOrientation()
    {
        TacviewTerrainGrid grid = TacviewTerrainGridBuilder.Build(
            new[]
            {
                Tile("east", 100.0, 0.0, 200.0, 100.0, 11, 11),
                Tile("west", 0.0, 0.0, 100.0, 100.0, 11, 11)
            },
            1025);

        Assert.Equal(0.0, grid.MinX);
        Assert.Equal(0.0, grid.MinZ);
        Assert.Equal(200.0, grid.MaxX);
        Assert.Equal(100.0, grid.MaxZ);
        Assert.Equal(21, grid.SourceWidth);
        Assert.Equal(11, grid.SourceHeight);
        Assert.Equal(0.0, grid.GlobalXForColumn(0));
        Assert.Equal(200.0, grid.GlobalXForColumn(grid.ExportWidth - 1));
        Assert.Equal(100.0, grid.GlobalZForRawRow(0));
        Assert.Equal(0.0, grid.GlobalZForRawRow(grid.ExportHeight - 1));

        Assert.True(grid.TryGetTile(25.0, 50.0, out TacviewTerrainTile west));
        Assert.Equal("west", west.Key);
        Assert.True(grid.TryGetTile(175.0, 50.0, out TacviewTerrainTile east));
        Assert.Equal("east", east.Key);
        // Shared edges use the deterministically sorted first tile only; no
        // arbitrary per-frame source choice can flip a height sample.
        Assert.True(grid.TryGetTile(100.0, 50.0, out TacviewTerrainTile sharedEdge));
        Assert.Equal("west", sharedEdge.Key);
    }

    [Fact]
    public void StaticTerrainGridSupportsDeterministicRectangularMultiTileCoverage()
    {
        TacviewTerrainGrid grid = TacviewTerrainGridBuilder.Build(
            new[]
            {
                Tile("ne", 100.0, 100.0, 200.0, 200.0, 11, 11),
                Tile("sw", 0.0, 0.0, 100.0, 100.0, 11, 11),
                Tile("se", 100.0, 0.0, 200.0, 100.0, 11, 11),
                Tile("nw", 0.0, 100.0, 100.0, 200.0, 11, 11)
            },
            1025);

        Assert.Equal(new[] { "sw", "se", "nw", "ne" }, grid.Tiles.Select(tile => tile.Key));
        Assert.True(grid.TryGetTile(25.0, 175.0, out TacviewTerrainTile northwest));
        Assert.Equal("nw", northwest.Key);
        Assert.True(grid.TryGetTile(175.0, 175.0, out TacviewTerrainTile northeast));
        Assert.Equal("ne", northeast.Key);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("overlap")]
    public void StaticTerrainGridFailsClosedForNonRectangularOrAmbiguousCoverage(string layout)
    {
        TacviewTerrainTile[] tiles = layout == "gap"
            ? new[]
            {
                Tile("west", 0.0, 0.0, 100.0, 100.0, 11, 11),
                Tile("east", 120.0, 0.0, 220.0, 100.0, 11, 11)
            }
            : new[]
            {
                Tile("west", 0.0, 0.0, 120.0, 100.0, 11, 11),
                Tile("east", 100.0, 0.0, 220.0, 100.0, 11, 11)
            };

        Assert.Throws<ArgumentException>(() => TacviewTerrainGridBuilder.Build(tiles, 1025));
    }

    [Fact]
    public void StaticTerrainGridRejectsMissingOrRotatedSources()
    {
        Assert.Throws<ArgumentException>(
            () => TacviewTerrainGridBuilder.Build(Array.Empty<TacviewTerrainTile>(), 1025));
        Assert.Throws<ArgumentException>(() => new TacviewTerrainTile(
            "rotated",
            11,
            11,
            new TacviewTerrainPlanarPoint(0.0, 0.0),
            new TacviewTerrainPlanarPoint(100.0, 10.0),
            new TacviewTerrainPlanarPoint(110.0, 110.0),
            new TacviewTerrainPlanarPoint(10.0, 100.0)));
    }

    [Fact]
    public void RasterEncodingIsSignedLittleEndianAndPreservesRawRowOrder()
    {
        short[] northToSouth =
        {
            0x1234, -2,
            1, short.MinValue
        };

        byte[] bytes = TacviewTerrainRaster.EncodeLittleEndian(northToSouth, 2, 2);

        Assert.Equal(
            new byte[] { 0x34, 0x12, 0xfe, 0xff, 0x01, 0x00, 0x00, 0x80 },
            bytes);
        Assert.Equal((short)13, TacviewTerrainRaster.QuantizeMeters(12.5));
        Assert.Equal((short)-13, TacviewTerrainRaster.QuantizeMeters(-12.5));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TacviewTerrainRaster.QuantizeMeters(double.NaN));
    }

    [Fact]
    public void XmlMatchesTacview195RawHeightmapContract()
    {
        TacviewTerrainDefinition definition = Definition();
        XDocument document = XDocument.Parse(TacviewTerrainXml.Build(definition));
        XElement heightmap = document.Root?
            .Element("CustomHeightmapList")?
            .Element("CustomHeightmap");

        Assert.NotNull(heightmap);
        Assert.Equal("Real World", (string)heightmap.Attribute("Layer"));
        Assert.Equal(definition.TerrainId, (string)heightmap.Attribute("Id"));
        Assert.Null(heightmap.Attribute("ID"));
        Assert.Equal(definition.RawFileName, (string)heightmap.Element("File"));
        Assert.Equal("false", (string)heightmap.Element("Visible"));
        Assert.Equal("0", (string)heightmap.Element("BigEndian"));
        Assert.Equal("true", (string)heightmap.Element("Signed"));
        Assert.Equal("3", (string)heightmap.Element("Width"));
        Assert.Equal("2", (string)heightmap.Element("Height"));
        Assert.Equal("1", (string)heightmap.Element("AltitudeFactor"));
        Assert.Equal("0", (string)heightmap.Element("AltitudeOffset"));
        Assert.Equal("Quad", (string)heightmap.Element("Projection"));
        AssertPoint(heightmap, "BottomLeft", 7.0, 51.0);
        AssertPoint(heightmap, "BottomRight", 8.0, 51.1);
        AssertPoint(heightmap, "TopRight", 8.1, 52.0);
        AssertPoint(heightmap, "TopLeft", 6.9, 51.9);
    }

    [Fact]
    public void XmlIdAndAcmiMapIdAreCaseExactAndIdentical()
    {
        TacviewTerrainDefinition definition = Definition();
        string xml = TacviewTerrainXml.Build(definition);
        XDocument document = XDocument.Parse(xml);
        XElement heightmap = document.Root?
            .Element("CustomHeightmapList")?
            .Element("CustomHeightmap");
        var text = new StringWriter();

        using (var writer = new TacviewEntityWriter(
                   text,
                   "Terrain Mission",
                   ReferenceTime,
                   new TacviewRecordingMetadata(
                       "Theater",
                       "Singleplayer",
                       "Briefing",
                       definition.TerrainId),
                   leaveOpen: true))
        {
        }

        Assert.NotNull(heightmap);
        Assert.Equal(definition.TerrainId, (string)heightmap.Attribute("Id"));
        Assert.Null(heightmap.Attribute("ID"));
        Assert.Contains("0,MapId=" + definition.TerrainId + "\n", text.ToString());
        Assert.DoesNotContain(" ID=", xml, StringComparison.Ordinal);
        Assert.Contains(" Id=\"" + definition.TerrainId + "\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderMapIdExactlyMatchesTerrainId()
    {
        TacviewTerrainDefinition definition = Definition();
        var text = new StringWriter();
        using var writer = new TacviewEntityWriter(
            text,
            "Terrain Mission",
            ReferenceTime,
            new TacviewRecordingMetadata(
                "Theater",
                "Singleplayer",
                "Briefing",
                definition.TerrainId),
            leaveOpen: true);

        Assert.Contains("0,MapId=" + definition.TerrainId + "\n", text.ToString());
    }

    [Fact]
    public void HeaderOmitsMapIdWhenTerrainPreparationFailed()
    {
        var text = new StringWriter();
        using var writer = new TacviewEntityWriter(
            text,
            "No Terrain",
            ReferenceTime,
            new TacviewRecordingMetadata("Theater", "Singleplayer", "Briefing", null),
            leaveOpen: true);

        Assert.DoesNotContain("MapId=", text.ToString());
    }

    [Fact]
    public async Task CurrentCacheConfirmsMapIdInFinalizedFile()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            TacviewTerrainDefinition definition = Definition();
            var cache = new TacviewTerrainCache(Path.Combine(directory, "terrain"));
            Assert.Equal(
                TacviewTerrainInstallStatus.Installed,
                cache.TryInstall(definition, SampleRaw(definition)).Status);

            var writer = CreateTerrainFileWriter(directory, definition);
            TacviewTerrainCacheProbe probe =
                await TacviewTerrainBackground.CheckCacheAsync(cache, definition);
            Assert.True(probe.IsCurrent);
            Assert.Null(probe.Error);

            writer.ConfirmMapId();
            writer.Dispose();

            string finalized = File.ReadAllText(writer.FinalPath, Encoding.UTF8);
            Assert.Contains("0,MapId=" + definition.TerrainId + "\n", finalized);
            Assert.DoesNotContain("//MapId=", finalized);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task SuccessfulNewInstallConfirmsMapIdInFinalizedFile()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            TacviewTerrainDefinition definition = Definition();
            var cache = new TacviewTerrainCache(Path.Combine(directory, "terrain"));
            var writer = CreateTerrainFileWriter(directory, definition);
            var payload = new TacviewTerrainPayload(definition, SampleRaw(definition));

            TacviewTerrainInstallResult install =
                await TacviewTerrainBackground.InstallAsync(cache, payload);
            Assert.Equal(TacviewTerrainInstallStatus.Installed, install.Status);
            Assert.True(cache.IsCurrent(definition));

            writer.ConfirmMapId();
            writer.Dispose();

            string finalized = File.ReadAllText(writer.FinalPath, Encoding.UTF8);
            Assert.Contains("0,MapId=" + definition.TerrainId + "\n", finalized);
            Assert.DoesNotContain("//MapId=", finalized);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Theory]
    [InlineData("export-start-failure")]
    [InlineData("height-sampling-failure")]
    [InlineData("dispose-before-completion")]
    public void UnconfirmedTerrainOutcomesSuppressMapId(string outcome)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            TacviewTerrainDefinition definition = Definition();
            var writer = CreateTerrainFileWriter(directory, definition, outcome);

            // These outcomes deliberately never reach ConfirmMapId().
            writer.Dispose();

            string finalized = File.ReadAllText(writer.FinalPath, Encoding.UTF8);
            Assert.DoesNotContain("\n0,MapId=" + definition.TerrainId + "\n", finalized);
            Assert.Contains("\n//MapId=" + definition.TerrainId + "\n", finalized);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task TerrainIoFailureSuppressesMapIdInFinalizedFile()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            TacviewTerrainDefinition definition = Definition();
            var cache = new TacviewTerrainCache(
                Path.Combine(directory, "terrain"),
                new ThrowingAtomicWriter());
            var payload = new TacviewTerrainPayload(definition, SampleRaw(definition));
            var writer = CreateTerrainFileWriter(directory, definition);

            TacviewTerrainInstallResult result =
                await TacviewTerrainBackground.InstallAsync(cache, payload);
            Assert.Equal(TacviewTerrainInstallStatus.Failed, result.Status);

            writer.Dispose();
            string finalized = File.ReadAllText(writer.FinalPath, Encoding.UTF8);
            Assert.Contains("\n//MapId=" + definition.TerrainId + "\n", finalized);
            Assert.DoesNotContain("\n0,MapId=" + definition.TerrainId + "\n", finalized);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void UnconfirmedMapIdPatchIsLengthNeutralAndChangesOnlyExactHeaderPrefix()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            TacviewTerrainDefinition definition = Definition();
            var writer = CreateTerrainFileWriter(
                directory,
                definition,
                "briefing contains MapId=" + definition.TerrainId);
            string temporaryPath = writer.FinalPath + ".tmp";
            writer.Flush();
            byte[] before;
            using (var snapshot = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite))
            {
                before = new byte[checked((int)snapshot.Length)];
                int offset = 0;
                while (offset < before.Length)
                {
                    int read = snapshot.Read(before, offset, before.Length - offset);
                    Assert.True(read > 0);
                    offset += read;
                }
            }
            byte[] target = Encoding.UTF8.GetBytes("0,MapId=" + definition.TerrainId + "\n");
            int targetIndex = IndexOf(before, target);
            Assert.True(targetIndex >= 0);

            writer.Dispose();
            byte[] after = File.ReadAllBytes(writer.FinalPath);
            int[] changedOffsets = Enumerable.Range(0, before.Length)
                .Where(index => before[index] != after[index])
                .ToArray();

            Assert.Equal(before.Length, after.Length);
            Assert.Equal(new[] { targetIndex, targetIndex + 1 }, changedOffsets);
            Assert.Equal((byte)'/', after[targetIndex]);
            Assert.Equal((byte)'/', after[targetIndex + 1]);
            Assert.Contains(
                "0,Briefing=briefing contains MapId=" + definition.TerrainId + "\n",
                Encoding.UTF8.GetString(after));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void MapIdPatchFailureRetainsTemporaryAndPublishesNoFinalFile()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            TacviewTerrainDefinition definition = Definition();
            var writer = TacviewEntityWriter.CreateFile(
                directory,
                "Patch Failure",
                ReferenceTime,
                RecordingMetadata(definition),
                new AtomicFileMover(),
                new ThrowingMapIdSuppressor());
            string finalPath = writer.FinalPath;
            string temporaryPath = finalPath + ".tmp";

            IOException error = Assert.Throws<IOException>(() => writer.Dispose());

            Assert.Contains("simulated MapId patch failure", error.Message);
            Assert.True(File.Exists(temporaryPath));
            Assert.False(File.Exists(finalPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void MoveFailureAfterMapIdPatchRetainsOnlyCommentedTemporaryFile()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            TacviewTerrainDefinition definition = Definition();
            var writer = TacviewEntityWriter.CreateFile(
                directory,
                "Move Failure",
                ReferenceTime,
                RecordingMetadata(definition),
                new ThrowingFileMover());
            string finalPath = writer.FinalPath;
            string temporaryPath = finalPath + ".tmp";

            Assert.Throws<IOException>(() => writer.Dispose());

            Assert.True(File.Exists(temporaryPath));
            Assert.False(File.Exists(finalPath));
            string temporary = File.ReadAllText(temporaryPath, Encoding.UTF8);
            Assert.Contains("\n//MapId=" + definition.TerrainId + "\n", temporary);
            Assert.DoesNotContain("\n0,MapId=" + definition.TerrainId + "\n", temporary);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void SuppressorCanPatchAnExactMapIdOnTheFirstBomPrefixedLine()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "first-line.acmi.tmp");
            const string escapedMapId = "VTOLVR.first-line.0123456789abcdef";
            byte[] bom = new UTF8Encoding(true).GetPreamble();
            byte[] body = Encoding.UTF8.GetBytes(
                "0,MapId=" + escapedMapId + "\nFileType=text/acmi/tacview\n");
            File.WriteAllBytes(path, bom.Concat(body).ToArray());
            long length = new FileInfo(path).Length;

            new UnconfirmedMapIdSuppressor().Suppress(path, escapedMapId);

            Assert.Equal(length, new FileInfo(path).Length);
            string patched = File.ReadAllText(path, Encoding.UTF8);
            Assert.StartsWith("//MapId=" + escapedMapId + "\n", patched);
            Assert.DoesNotContain("0,MapId=", patched);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void CacheInstallIsIdempotentAndDoesNotTouchForeignCustomList()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string foreignPath = Path.Combine(directory, "CustomHeightmapList.xml");
            File.WriteAllText(foreignPath, "foreign-content", Encoding.UTF8);
            var cache = new TacviewTerrainCache(directory);
            TacviewTerrainDefinition definition = Definition();
            byte[] raw = SampleRaw(definition);

            TacviewTerrainInstallResult first = cache.TryInstall(definition, raw);
            DateTime xmlTimestamp = File.GetLastWriteTimeUtc(first.XmlPath);
            TacviewTerrainInstallResult second = cache.TryInstall(definition, raw);

            Assert.Equal(TacviewTerrainInstallStatus.Installed, first.Status);
            Assert.Equal(TacviewTerrainInstallStatus.AlreadyCurrent, second.Status);
            Assert.True(cache.IsCurrent(definition));
            Assert.Equal(xmlTimestamp, File.GetLastWriteTimeUtc(first.XmlPath));
            Assert.Equal("foreign-content", File.ReadAllText(foreignPath, Encoding.UTF8));
            Assert.Equal(
                new[]
                {
                    "CustomHeightmapList.xml",
                    definition.RawFileName,
                    definition.ChecksumFileName,
                    definition.XmlFileName
                },
                Directory.GetFiles(directory).Select(Path.GetFileName).OrderBy(name => name).ToArray());
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void CacheRepublishesLegacyUppercaseIdXmlBeforeTreatingTerrainAsCurrent()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            TacviewTerrainDefinition definition = Definition();
            byte[] raw = SampleRaw(definition);
            var cache = new TacviewTerrainCache(directory);
            TacviewTerrainInstallResult first = cache.TryInstall(definition, raw);
            Assert.Equal(TacviewTerrainInstallStatus.Installed, first.Status);

            string legacyXml = TacviewTerrainXml.Build(definition)
                .Replace(" Id=\"", " ID=\"");
            File.WriteAllText(first.XmlPath, legacyXml, new UTF8Encoding(false));
            Assert.False(cache.IsCurrent(definition));

            var writer = new RecordingAtomicWriter();
            var repairingCache = new TacviewTerrainCache(directory, writer);
            TacviewTerrainInstallResult repair = repairingCache.TryInstall(definition, raw);

            Assert.Equal(TacviewTerrainInstallStatus.Installed, repair.Status);
            Assert.Equal(
                new[] { definition.RawFileName, definition.ChecksumFileName, definition.XmlFileName },
                writer.FileNames);
            string repairedXml = File.ReadAllText(first.XmlPath, Encoding.UTF8);
            Assert.Contains(" Id=\"" + definition.TerrainId + "\"", repairedXml);
            Assert.DoesNotContain(" ID=", repairedXml);
            Assert.True(repairingCache.IsCurrent(definition));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void CacheDetectsCorruptionAndRepublishesRawBeforeXml()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            TacviewTerrainDefinition definition = Definition();
            byte[] raw = SampleRaw(definition);
            var firstCache = new TacviewTerrainCache(directory);
            TacviewTerrainInstallResult first = firstCache.TryInstall(definition, raw);
            File.WriteAllBytes(first.RawPath, Enumerable.Repeat((byte)0xff, raw.Length).ToArray());
            Assert.False(firstCache.IsCurrent(definition));

            var orderedWriter = new RecordingAtomicWriter();
            var repairingCache = new TacviewTerrainCache(directory, orderedWriter);
            TacviewTerrainInstallResult repair = repairingCache.TryInstall(definition, raw);

            Assert.Equal(TacviewTerrainInstallStatus.Installed, repair.Status);
            Assert.Equal(
                new[] { definition.RawFileName, definition.ChecksumFileName, definition.XmlFileName },
                orderedWriter.FileNames);
            Assert.True(firstCache.IsCurrent(definition));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void DefaultAtomicWriterRepairsExistingManagedFilesWithoutTempResidue()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            TacviewTerrainDefinition definition = Definition();
            byte[] raw = SampleRaw(definition);
            var cache = new TacviewTerrainCache(directory);
            TacviewTerrainInstallResult first = cache.TryInstall(definition, raw);
            File.WriteAllBytes(first.RawPath, Enumerable.Repeat((byte)0xff, raw.Length).ToArray());

            TacviewTerrainInstallResult repair = cache.TryInstall(definition, raw);

            Assert.Equal(TacviewTerrainInstallStatus.Installed, repair.Status);
            Assert.True(cache.IsCurrent(definition));
            Assert.DoesNotContain(
                Directory.GetFiles(directory),
                path => Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void CacheRejectsEscapingOrForeignPaths()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var cache = new TacviewTerrainCache(directory);

            Assert.Throws<InvalidOperationException>(() => cache.ResolveManagedFile("../VTOLVR.escape.raw"));
            Assert.Throws<InvalidOperationException>(() => cache.ResolveManagedFile("VTOLVR.foo/escape.raw"));
            Assert.Throws<InvalidOperationException>(() => cache.ResolveManagedFile("CustomHeightmapList.xml"));
            Assert.Throws<InvalidOperationException>(() => cache.ResolveManagedFile("VTOLVR.foo:stream.raw"));
            Assert.Throws<InvalidOperationException>(() => cache.ResolveManagedFile("VTOLVR.foo.dll"));
            Assert.StartsWith(
                Path.GetFullPath(directory) + Path.DirectorySeparatorChar,
                cache.ResolveManagedFile("VTOLVR.safe.raw"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void CacheRequiresAbsoluteRoot()
    {
        Assert.Throws<ArgumentException>(() => new TacviewTerrainCache("relative/path"));
    }

    [Fact]
    public void TerrainCoreFilesDoNotDependOnUnityOrVtolRuntimeTypes()
    {
        string[] referencedAssemblies = typeof(TacviewTerrainCache).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToArray();

        Assert.DoesNotContain(referencedAssemblies, name => name.StartsWith("UnityEngine", StringComparison.Ordinal));
        Assert.DoesNotContain(referencedAssemblies, name => name.Equals("Assembly-CSharp", StringComparison.Ordinal));
        Assert.DoesNotContain(referencedAssemblies, name => name.Equals("VTOLAPI", StringComparison.Ordinal));
    }

    [Fact]
    public async System.Threading.Tasks.Task BackgroundBoundaryUsesCopiedPayloadAndCoreFileIoOnly()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            TacviewTerrainDefinition definition = Definition();
            byte[] source = SampleRaw(definition);
            var payload = new TacviewTerrainPayload(definition, source);
            Array.Fill(source, (byte)0xff);
            var cache = new TacviewTerrainCache(directory);

            TacviewTerrainCacheProbe before =
                await TacviewTerrainBackground.CheckCacheAsync(cache, definition);
            TacviewTerrainInstallResult install =
                await TacviewTerrainBackground.InstallAsync(cache, payload);
            TacviewTerrainCacheProbe after =
                await TacviewTerrainBackground.CheckCacheAsync(cache, definition);

            Assert.False(before.IsCurrent);
            Assert.Null(before.Error);
            Assert.Equal(TacviewTerrainInstallStatus.Installed, install.Status);
            Assert.True(after.IsCurrent);
            Assert.Null(after.Error);
            Assert.NotEqual(source, File.ReadAllBytes(install.RawPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void CacheReportsWriteFailureWithoutThrowing()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var cache = new TacviewTerrainCache(directory, new ThrowingAtomicWriter());
            TacviewTerrainDefinition definition = Definition();

            TacviewTerrainInstallResult result = cache.TryInstall(definition, SampleRaw(definition));

            Assert.Equal(TacviewTerrainInstallStatus.Failed, result.Status);
            Assert.Contains("simulated write failure", result.Error);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void CorruptAtomicWriterNeverPublishesXml()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var corruptWriter = new CorruptRawAtomicWriter();
            var cache = new TacviewTerrainCache(directory, corruptWriter);
            TacviewTerrainDefinition definition = Definition();

            TacviewTerrainInstallResult result = cache.TryInstall(definition, SampleRaw(definition));

            Assert.Equal(TacviewTerrainInstallStatus.Failed, result.Status);
            Assert.Contains("verification failed", result.Error);
            Assert.DoesNotContain(definition.XmlFileName, corruptWriter.FileNames);
            Assert.False(File.Exists(Path.Combine(directory, definition.XmlFileName)));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void DefinitionRejectsUnmanagedIdAndInvalidCoordinates()
    {
        Assert.Throws<ArgumentException>(() => new TacviewTerrainDefinition(
            "foreign-map",
            2,
            2,
            new TacviewTerrainGeoPoint(0, 0),
            new TacviewTerrainGeoPoint(1, 0),
            new TacviewTerrainGeoPoint(1, 1),
            new TacviewTerrainGeoPoint(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TacviewTerrainDefinition(
            "VTOLVR.safe",
            2,
            2,
            new TacviewTerrainGeoPoint(181, 0),
            new TacviewTerrainGeoPoint(1, 0),
            new TacviewTerrainGeoPoint(1, 1),
                new TacviewTerrainGeoPoint(0, 1)));
    }

    [Fact]
    public void MeshTerrainGridInterpolatesTransformedVerticesAndPreservesRawOrientation()
    {
        // These points model vertices after the runtime has applied a rotated
        // MeshCollider Transform and converted world space to VTOL global/GPS.
        // The pure grid must not make any Unity-coordinate assumptions.
        TacviewMeshTerrainPoint lowerLeft = MeshPoint(100.0, 200.0, 50.0);
        TacviewMeshTerrainPoint lowerRight = MeshPoint(100.0, 210.0, 60.0);
        TacviewMeshTerrainPoint upperLeft = MeshPoint(90.0, 200.0, 70.0);
        TacviewMeshTerrainPoint upperRight = MeshPoint(90.0, 210.0, 80.0);
        TacviewMeshTerrainGrid grid = MeshGrid(
            new TacviewMeshTerrainTriangle(lowerLeft, lowerRight, upperLeft),
            new TacviewMeshTerrainTriangle(lowerRight, upperRight, upperLeft));

        Assert.Equal(90.0, grid.MinX);
        Assert.Equal(200.0, grid.MinZ);
        Assert.Equal(100.0, grid.MaxX);
        Assert.Equal(210.0, grid.MaxZ);
        Assert.Equal(210.0, grid.GlobalZForRawRow(0));
        Assert.Equal(200.0, grid.GlobalZForRawRow(grid.ExportHeight - 1));
        Assert.True(grid.TrySample(95.0, 205.0, out double altitude));
        Assert.Equal(65.0, altitude, 6);
    }

    [Fact]
    public void MeshTerrainGridAcceptsSharedSeamsButRejectsOutOfBoundsSamples()
    {
        TacviewMeshTerrainGrid grid = MeshGrid(RectangleTriangles(
            1000.0,
            -500.0,
            1010.0,
            -490.0,
            25.0,
            35.0,
            45.0,
            55.0));

        Assert.True(grid.TrySample(1005.0, -495.0, out double seamAltitude));
        Assert.Equal(40.0, seamAltitude, 6);
        Assert.False(grid.TrySample(999.0, -495.0, out _));
        Assert.False(grid.TrySample(1005.0, -489.0, out _));
        grid.ValidateExportCoverage(5000);
    }

    [Fact]
    public void MeshTerrainGridFailsClosedForInteriorGapBeforeHeaderPreparation()
    {
        TacviewMeshTerrainGrid grid = MeshGrid(new[]
        {
            new TacviewMeshTerrainTriangle(MeshPoint(0, 0, 0), MeshPoint(1, 0, 0), MeshPoint(0, 2, 0)),
            new TacviewMeshTerrainTriangle(MeshPoint(1, 0, 0), MeshPoint(1, 2, 0), MeshPoint(0, 2, 0)),
            new TacviewMeshTerrainTriangle(MeshPoint(2, 0, 0), MeshPoint(3, 0, 0), MeshPoint(2, 2, 0)),
            new TacviewMeshTerrainTriangle(MeshPoint(3, 0, 0), MeshPoint(3, 2, 0), MeshPoint(2, 2, 0))
        });

        Assert.False(grid.TrySample(1.5, 1.0, out _));
        Assert.Throws<InvalidOperationException>(() => grid.ValidateExportCoverage(5000));
    }

    [Fact]
    public void MeshTerrainGridFailsClosedForOverlappingLodsEvenWhenHeightsMatch()
    {
        TacviewMeshTerrainTriangle first = new TacviewMeshTerrainTriangle(
            MeshPoint(0, 0, 0), MeshPoint(10, 0, 10), MeshPoint(0, 10, 20));
        TacviewMeshTerrainTriangle duplicateLod = new TacviewMeshTerrainTriangle(
            MeshPoint(0, 0, 0), MeshPoint(10, 0, 10), MeshPoint(0, 10, 20));
        TacviewMeshTerrainGrid grid = MeshGrid(first, duplicateLod);

        Assert.False(grid.TrySample(2, 2, out _));
        Assert.Throws<InvalidOperationException>(() => grid.ValidateExportCoverage(5000));
    }

    [Fact]
    public void MeshTerrainGridRejectsVerticalTrianglesAndBoundedIndexOverflows()
    {
        Assert.Throws<ArgumentException>(() => new TacviewMeshTerrainTriangle(
            MeshPoint(0, 0, 0),
            MeshPoint(0, 0, 10),
            MeshPoint(0, 0, 20)));

        TacviewMeshTerrainTriangle triangle = new TacviewMeshTerrainTriangle(
            MeshPoint(0, 0, 0), MeshPoint(100, 0, 0), MeshPoint(0, 100, 0));
        Assert.Throws<InvalidOperationException>(() => TacviewMeshTerrainGridBuilder.Build(
            new[] { triangle, triangle },
            65,
            maximumTriangleCount: 1));
        Assert.Throws<InvalidOperationException>(() => TacviewMeshTerrainGridBuilder.Build(
            Enumerable.Repeat(triangle, 128),
            65,
            maximumSpatialCellCount: 16,
            maximumSpatialReferenceCount: 1));
    }

    [Fact]
    public void MeshTerrainGridCapsRasterAndValidationWork()
    {
        TacviewMeshTerrainGrid grid = TacviewMeshTerrainGridBuilder.Build(
            RectangleTriangles(0, 0, 100, 100, 0, 0, 0, 0),
            maximumExportDimension: 32);

        Assert.True(grid.SourceWidth >= TacviewMeshTerrainGridBuilder.MinimumExportDimension);
        Assert.True(grid.ExportWidth <= 32);
        Assert.True(grid.ExportHeight <= 32);
        Assert.Throws<InvalidOperationException>(() => grid.ValidateExportCoverage(4));
    }

    [Fact]
    public void MeshTerrainClassificationPreservesStrictTrySampleSemantics()
    {
        TacviewMeshTerrainGrid resolved = MeshGrid(RectangleTriangles(
            0, 0, 4, 4, 10, 14, 18, 22));
        Assert.Equal(
            TacviewMeshTerrainSampleKind.Resolved,
            resolved.ClassifySample(1, 1, out double altitude));
        Assert.Equal(13.0, altitude, 6);
        Assert.True(resolved.TrySample(1, 1, out double legacyAltitude));
        Assert.Equal(altitude, legacyAltitude);

        Assert.Equal(
            TacviewMeshTerrainSampleKind.Uncovered,
            resolved.ClassifySample(10, 10, out _));
        Assert.False(resolved.TrySample(10, 10, out _));

        TacviewMeshTerrainTriangle triangle = new TacviewMeshTerrainTriangle(
            MeshPoint(0, 0, 1), MeshPoint(4, 0, 1), MeshPoint(0, 4, 1));
        TacviewMeshTerrainGrid ambiguous = MeshGrid(triangle, triangle);
        Assert.Equal(
            TacviewMeshTerrainSampleKind.Ambiguous,
            ambiguous.ClassifySample(1, 1, out _));
        Assert.False(ambiguous.TrySample(1, 1, out _));
    }

    [Fact]
    public void AkutanResolverAllResolvedKeepsNorthToSouthOrientationAndIndexing()
    {
        TacviewMeshTerrainGrid grid = MeshGrid(RectangleTriangles(
            10, 20, 14, 24, 100, 104, 120, 124));

        TacviewAkutanRaster raster = TacviewAkutanRasterResolver.Resolve(grid, 3, 3, 9);

        Assert.Equal(3, raster.Width);
        Assert.Equal(3, raster.Height);
        Assert.Equal(120.0, raster.GetAltitude(0, 0), 6);
        Assert.Equal(124.0, raster.GetAltitude(0, 2), 6);
        Assert.Equal(100.0, raster.GetAltitude(2, 0), 6);
        Assert.Equal(104.0, raster.GetAltitude(2, 2), 6);
        Assert.Equal(112.0, raster.GetAltitude(1, 1), 6);
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.GetAltitude(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.GetAltitude(0, 3));
    }

    [Fact]
    public void AkutanResolverFillsOneBoundaryOceanComponentAtZeroMsl()
    {
        TacviewMeshTerrainGrid grid = MeshGrid(BoundaryGapTriangles(
            1, 4, 15));

        TacviewAkutanRaster raster = TacviewAkutanRasterResolver.Resolve(grid, 5, 5, 25);

        Assert.Equal(0.0, raster.GetAltitude(0, 0));
        Assert.Equal(0.0, raster.GetAltitude(2, 0));
        Assert.Equal(15.0, raster.GetAltitude(2, 2));
    }

    [Fact]
    public void AkutanResolverAcceptsMultipleSeparateBoundaryOceanComponents()
    {
        TacviewMeshTerrainGrid grid = MeshGrid(BoundaryGapTriangles(
            1, 3, 25));

        TacviewAkutanRaster raster = TacviewAkutanRasterResolver.Resolve(grid, 5, 5, 25);

        Assert.Equal(0.0, raster.GetAltitude(2, 0));
        Assert.Equal(25.0, raster.GetAltitude(2, 2));
        Assert.Equal(0.0, raster.GetAltitude(2, 4));
    }

    [Fact]
    public void AkutanResolverRejectsEnclosedUncoveredHole()
    {
        TacviewMeshTerrainGrid grid = MeshGrid(RingTriangles(0, 0, 4, 4, 1, 3, 30));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => TacviewAkutanRasterResolver.Resolve(grid, 5, 5, 25));
        Assert.Contains("enclosed uncovered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AkutanResolverTreatsDiagonalOnlyConnectionAsEnclosed()
    {
        TacviewMeshTerrainGrid grid = MeshGrid(RectanglesExceptDiagonalSamples());

        Assert.Throws<InvalidOperationException>(
            () => TacviewAkutanRasterResolver.Resolve(grid, 4, 4, 16));
    }

    [Fact]
    public void AkutanResolverRejectsBoundaryAmbiguity()
    {
        TacviewMeshTerrainGrid grid = MeshGrid(
            BoundaryGapTriangles(1, 4, 5)
                .Concat(new[]
                {
                    new TacviewMeshTerrainTriangle(
                        MeshPoint(0.5, 1.5, 5),
                        MeshPoint(1.6, 1.5, 5),
                        MeshPoint(0.5, 2.6, 5))
                })
                .ToArray());

        Assert.Throws<InvalidOperationException>(
            () => TacviewAkutanRasterResolver.Resolve(grid, 5, 5, 25));
    }

    [Fact]
    public void AkutanProductionRasterDimensionIsFixedAt508()
    {
        Assert.Equal(508, TacviewAkutanRasterResolver.ProductionDimension);
        Assert.Equal(
            508 * 508,
            TacviewAkutanRasterResolver.ProductionMaximumSampleCount);
        TacviewMeshTerrainGrid grid = MeshGrid(RectangleTriangles(
            0, 0, 4, 4, 0, 0, 0, 0));
        Assert.Throws<InvalidOperationException>(
            () => TacviewAkutanRasterResolver.ResolveProduction(grid));
    }

    [Fact]
    public void AkutanResolverRejectsAboveProductionCapEvenWhenCallerAllowsIt()
    {
        TacviewMeshTerrainGrid grid = MeshGrid(RectangleTriangles(
            0, 0, 4, 4, 0, 0, 0, 0));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => TacviewAkutanRasterResolver.Resolve(
                grid,
                width: 509,
                height: 508,
                maximumSampleCount: int.MaxValue));

        Assert.Contains("absolute 508x508", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericMeshCoverageRemainsStrictForBoundaryWaterLikeGap()
    {
        TacviewMeshTerrainGrid grid = MeshGrid(BoundaryGapTriangles(
            1, 4, 15));

        Assert.False(grid.TrySample(0, 2, out _));
        Assert.Throws<InvalidOperationException>(() => grid.ValidateExportCoverage(5000));
    }

    private static TacviewTerrainFingerprintInput FingerprintInput()
    {
        return new TacviewTerrainFingerprintInput
        {
            MapIdentity = "e_coastBay",
            SourceSignature = "HeightMap|Coast|North|64|3072|42",
            WorldSizeMeters = 196608.25,
            SourceWidth = 1281,
            SourceHeight = 1281,
            ExportWidth = 1025,
            ExportHeight = 1025,
            MinimumHeightMeters = -80.0,
            MaximumHeightMeters = 6000.0,
            BottomLeft = new TacviewTerrainGeoPoint(7.0, 51.0),
            BottomRight = new TacviewTerrainGeoPoint(8.0, 51.1),
            TopRight = new TacviewTerrainGeoPoint(8.1, 52.0),
            TopLeft = new TacviewTerrainGeoPoint(6.9, 51.9),
            SparseHeightSamples = new[] { 0.0, 1.25, 50.5, 500.0, 1234.75 }
        };
    }

    private static TacviewTerrainDefinition Definition()
    {
        return new TacviewTerrainDefinition(
            "VTOLVR.e-coast-bay.0123456789abcdef",
            3,
            2,
            new TacviewTerrainGeoPoint(7.0, 51.0),
            new TacviewTerrainGeoPoint(8.0, 51.1),
            new TacviewTerrainGeoPoint(8.1, 52.0),
            new TacviewTerrainGeoPoint(6.9, 51.9));
    }

    private static TacviewTerrainTile Tile(
        string key,
        double minX,
        double minZ,
        double maxX,
        double maxZ,
        int sourceWidth,
        int sourceHeight)
    {
        return new TacviewTerrainTile(
            key,
            sourceWidth,
            sourceHeight,
            new TacviewTerrainPlanarPoint(minX, minZ),
            new TacviewTerrainPlanarPoint(maxX, minZ),
            new TacviewTerrainPlanarPoint(maxX, maxZ),
            new TacviewTerrainPlanarPoint(minX, maxZ));
    }

    private static TacviewMeshTerrainGrid MeshGrid(params TacviewMeshTerrainTriangle[] triangles)
    {
        return TacviewMeshTerrainGridBuilder.Build(triangles, maximumExportDimension: 65);
    }

    private static TacviewMeshTerrainTriangle[] RectangleTriangles(
        double minX,
        double minZ,
        double maxX,
        double maxZ,
        double lowerLeftAltitude,
        double lowerRightAltitude,
        double upperLeftAltitude,
        double upperRightAltitude)
    {
        TacviewMeshTerrainPoint lowerLeft = MeshPoint(minX, minZ, lowerLeftAltitude);
        TacviewMeshTerrainPoint lowerRight = MeshPoint(maxX, minZ, lowerRightAltitude);
        TacviewMeshTerrainPoint upperLeft = MeshPoint(minX, maxZ, upperLeftAltitude);
        TacviewMeshTerrainPoint upperRight = MeshPoint(maxX, maxZ, upperRightAltitude);
        return new[]
        {
            new TacviewMeshTerrainTriangle(lowerLeft, lowerRight, upperLeft),
            new TacviewMeshTerrainTriangle(lowerRight, upperRight, upperLeft)
        };
    }

    private static TacviewMeshTerrainTriangle[] RingTriangles(
        double minX,
        double minZ,
        double maxX,
        double maxZ,
        double holeMin,
        double holeMax,
        double altitude)
    {
        return RectangleTriangles(minX, minZ, maxX, holeMin, altitude, altitude, altitude, altitude)
            .Concat(RectangleTriangles(minX, holeMax, maxX, maxZ, altitude, altitude, altitude, altitude))
            .Concat(RectangleTriangles(minX, holeMin, holeMin, holeMax, altitude, altitude, altitude, altitude))
            .Concat(RectangleTriangles(holeMax, holeMin, maxX, holeMax, altitude, altitude, altitude, altitude))
            .ToArray();
    }

    private static TacviewMeshTerrainTriangle[] BoundaryGapTriangles(
        double terrainMinX,
        double terrainMaxX,
        double altitude)
    {
        // Thin geometry between raster rows establishes the full 0..4 bounds
        // without covering either outer-column lattice. It models coastline
        // geometry whose rectangular union includes open ocean.
        return RectangleTriangles(
                terrainMinX, 0, terrainMaxX, 4,
                altitude, altitude, altitude, altitude)
            .Concat(RectangleTriangles(0, 0.4, 0.1, 0.6, altitude, altitude, altitude, altitude))
            .Concat(RectangleTriangles(3.9, 3.4, 4, 3.6, altitude, altitude, altitude, altitude))
            .ToArray();
    }

    private static TacviewMeshTerrainTriangle[] RectanglesExceptDiagonalSamples()
    {
        // On a 4x4 lattice over 0..3, leave only (0,0) and (1,1)
        // uncovered. They touch diagonally, but the interior sample must not
        // inherit boundary connectivity under the required four-neighbour rule.
        var triangles = new List<TacviewMeshTerrainTriangle>();
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                if ((row == 0 && column == 0) || (row == 1 && column == 1))
                {
                    continue;
                }

                triangles.AddRange(RectangleTriangles(
                    Math.Max(0, column - 0.1),
                    Math.Max(0, row - 0.1),
                    Math.Min(3, column + 0.1),
                    Math.Min(3, row + 0.1),
                    40,
                    40,
                    40,
                    40));
            }
        }

        for (int column = 0; column < 4; column++)
        {
            triangles.AddRange(RectangleTriangles(
                Math.Max(0, column - 0.1),
                2.9,
                Math.Min(3, column + 0.1),
                3,
                40,
                40,
                40,
                40));
        }

        return triangles.ToArray();
    }

    private static TacviewMeshTerrainPoint MeshPoint(double x, double z, double altitude)
    {
        return new TacviewMeshTerrainPoint(x, z, altitude);
    }

    private static byte[] SampleRaw(TacviewTerrainDefinition definition)
    {
        short[] samples = Enumerable.Range(0, definition.Width * definition.Height)
            .Select(index => (short)(index * 10))
            .ToArray();
        return TacviewTerrainRaster.EncodeLittleEndian(samples, definition.Width, definition.Height);
    }

    private static TacviewEntityWriter CreateTerrainFileWriter(
        string directory,
        TacviewTerrainDefinition definition,
        string briefing = "Briefing")
    {
        return TacviewEntityWriter.CreateFile(
            directory,
            "Terrain Mission",
            ReferenceTime,
            RecordingMetadata(definition, briefing));
    }

    private static TacviewRecordingMetadata RecordingMetadata(
        TacviewTerrainDefinition definition,
        string briefing = "Briefing")
    {
        return new TacviewRecordingMetadata(
            "Theater",
            "Singleplayer",
            briefing,
            definition.TerrainId);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int index = 0; index <= haystack.Length - needle.Length; index++)
        {
            bool matches = true;
            for (int offset = 0; offset < needle.Length; offset++)
            {
                if (haystack[index + offset] == needle[offset])
                {
                    continue;
                }

                matches = false;
                break;
            }

            if (matches)
            {
                return index;
            }
        }

        return -1;
    }

    private static void AssertPoint(
        XElement heightmap,
        string elementName,
        double expectedLongitude,
        double expectedLatitude)
    {
        XElement point = heightmap.Element(elementName);
        Assert.NotNull(point);
        Assert.Equal(
            expectedLongitude,
            double.Parse((string)point.Element("Longitude"), CultureInfo.InvariantCulture));
        Assert.Equal(
            expectedLatitude,
            double.Parse((string)point.Element("Latitude"), CultureInfo.InvariantCulture));
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "VTOLVRTacviewTerrainTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingAtomicWriter : IAtomicTerrainFileWriter
    {
        public List<string> FileNames { get; } = new List<string>();

        public void Write(string targetPath, byte[] contents)
        {
            FileNames.Add(Path.GetFileName(targetPath));
            File.WriteAllBytes(targetPath, contents);
        }
    }

    private sealed class ThrowingAtomicWriter : IAtomicTerrainFileWriter
    {
        public void Write(string targetPath, byte[] contents)
        {
            throw new IOException("simulated write failure");
        }
    }

    private sealed class CorruptRawAtomicWriter : IAtomicTerrainFileWriter
    {
        public List<string> FileNames { get; } = new List<string>();

        public void Write(string targetPath, byte[] contents)
        {
            string fileName = Path.GetFileName(targetPath);
            FileNames.Add(fileName);
            if (fileName.EndsWith(".raw", StringComparison.Ordinal))
            {
                File.WriteAllBytes(targetPath, Enumerable.Repeat((byte)0xff, contents.Length).ToArray());
            }
            else
            {
                File.WriteAllBytes(targetPath, contents);
            }
        }
    }

    private sealed class ThrowingMapIdSuppressor : IUnconfirmedMapIdSuppressor
    {
        public void Suppress(string path, string escapedMapId)
        {
            throw new IOException("simulated MapId patch failure");
        }
    }

    private sealed class ThrowingFileMover : IAtomicFileMover
    {
        public void Move(string sourcePath, string destinationPath)
        {
            throw new IOException("simulated ACMI move failure");
        }
    }
}
