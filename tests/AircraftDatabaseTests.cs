using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using VTOLVRTacviewSPRecorder.Core;
using Xunit;

namespace VTOLVRTacviewSPRecorder.Core.Tests;

public sealed class AircraftDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vtol-aircraft-db-tests", Guid.NewGuid().ToString("N"));
    private string DirectoryPath => Path.Combine(_root, "Tacview", "Data", "Database", "Default Properties");
    private string Target => Path.Combine(DirectoryPath, TacviewAircraftDatabase.FileName);

    [Fact]
    public void InstallsBundledDefinitionsAndDoesNotRewriteCurrentFile()
    {
        Assert.Equal(TacviewAircraftDatabaseStatus.Installed, TacviewAircraftDatabase.EnsureInstalled(_root));
        var oldTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Target, oldTime);
        Assert.Equal(TacviewAircraftDatabaseStatus.Current, TacviewAircraftDatabase.EnsureInstalled(_root));
        Assert.Equal(oldTime, File.GetLastWriteTimeUtc(Target));

        var entries = XDocument.Load(Target).Root.Elements("DefaultProperties").ToArray();
        Assert.Equal(6, entries.Length);
        Assert.Equal(6, entries.Select(e => (string)e.Attribute("Id")).Distinct().Count());
        foreach (var entry in entries)
        {
            var properties = entry.Element("Properties");
            string name = (string)properties.Element("ShortName");
            string shape = (string)properties.Element("Shape");
            Assert.True(VtolAircraftShapeCatalog.TryGetShape(name, out string expected));
            Assert.Equal(expected, shape);
            foreach (var alias in entry.Element("Criteria").Elements("Name"))
            {
                Assert.True(VtolAircraftShapeCatalog.TryGetShape(alias.Value, out string aliasShape));
                Assert.Equal(shape, aliasShape);
            }
            string type = name == "AH-94" ? "Rotorcraft" : "FixedWing";
            Assert.Equal(type, (string)entry.Element("Criteria").Element("Type"));
            Assert.Equal("Core." + type, (string)entry.Attribute("Base"));
            Assert.StartsWith("VTOLVR.", (string)entry.Attribute("Id"));
            Assert.False(string.IsNullOrWhiteSpace((string)properties.Element("LongName")));
            Assert.False(string.IsNullOrWhiteSpace((string)properties.Element("FullName")));
        }
    }

    [Fact]
    public void UpdatesOnlyOwnedFileAndCleansTemporaryFiles()
    {
        TacviewAircraftDatabase.EnsureInstalled(_root);
        byte[] expected = File.ReadAllBytes(Target);
        File.WriteAllText(Target, "previous version");
        string other = Path.Combine(DirectoryPath, "MyCustomAircraft.xml");
        File.WriteAllText(other, "keep my definitions");
        Assert.Equal(TacviewAircraftDatabaseStatus.Updated, TacviewAircraftDatabase.EnsureInstalled(_root));
        Assert.Equal(expected, File.ReadAllBytes(Target));
        Assert.Equal("keep my definitions", File.ReadAllText(other));
        Assert.Empty(Directory.GetFiles(DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void FailedInstallPreservesConflictingDirectoryAndCleansTemporaryFiles()
    {
        Directory.CreateDirectory(Target);
        string marker = Path.Combine(Target, "keep.txt");
        File.WriteAllText(marker, "keep");
        Assert.ThrowsAny<IOException>(() => TacviewAircraftDatabase.EnsureInstalled(_root));
        Assert.Equal("keep", File.ReadAllText(marker));
        Assert.Empty(Directory.GetFiles(DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative-directory")]
    public void InvalidApplicationDataPathIsRejected(string path)
    {
        Assert.Throws<ArgumentException>(() => TacviewAircraftDatabase.EnsureInstalled(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
