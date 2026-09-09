using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VTOLVRTacviewSPRecorder.Core;

public enum TacviewTerrainInstallStatus
{
    Installed,
    AlreadyCurrent,
    Failed
}

public sealed class TacviewTerrainInstallResult
{
    internal TacviewTerrainInstallResult(
        TacviewTerrainInstallStatus status,
        string rawPath,
        string xmlPath,
        string checksumPath,
        string error)
    {
        Status = status;
        RawPath = rawPath;
        XmlPath = xmlPath;
        ChecksumPath = checksumPath;
        Error = error;
    }

    public TacviewTerrainInstallStatus Status { get; }

    public string RawPath { get; }

    public string XmlPath { get; }

    public string ChecksumPath { get; }

    public string Error { get; }
}

public interface IAtomicTerrainFileWriter
{
    void Write(string targetPath, byte[] contents);
}

public sealed class TacviewTerrainCache
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private readonly string _rootDirectory;
    private readonly IAtomicTerrainFileWriter _fileWriter;

    public TacviewTerrainCache(string rootDirectory, IAtomicTerrainFileWriter fileWriter = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("A Tacview terrain root is required.", nameof(rootDirectory));
        }

        if (!Path.IsPathRooted(rootDirectory.Trim()))
        {
            throw new ArgumentException("The Tacview terrain root must be an absolute path.", nameof(rootDirectory));
        }

        _rootDirectory = Path.GetFullPath(rootDirectory.Trim());
        _fileWriter = fileWriter ?? new AtomicTerrainFileWriter();
    }

    public string RootDirectory => _rootDirectory;

    public bool IsCurrent(TacviewTerrainDefinition definition)
    {
        if (definition == null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        TerrainPaths paths = GetPaths(definition);
        if (!File.Exists(paths.RawPath) || !File.Exists(paths.XmlPath) ||
            !File.Exists(paths.ChecksumPath))
        {
            return false;
        }

        var rawInfo = new FileInfo(paths.RawPath);
        if (rawInfo.Length != definition.ExpectedRawLength)
        {
            return false;
        }

        string expectedXml = TacviewTerrainXml.Build(definition);
        if (!ByteArraysEqual(File.ReadAllBytes(paths.XmlPath), Utf8NoBom.GetBytes(expectedXml)))
        {
            return false;
        }

        string expectedHash = File.ReadAllText(paths.ChecksumPath, Utf8NoBom).Trim();
        if (expectedHash.Length != 64)
        {
            return false;
        }

        string actualHash = ComputeFileHash(paths.RawPath);
        return string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase);
    }

    public TacviewTerrainInstallResult TryInstall(
        TacviewTerrainDefinition definition,
        byte[] rawBytes)
    {
        TerrainPaths paths = null;
        try
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (rawBytes == null || rawBytes.LongLength != definition.ExpectedRawLength)
            {
                throw new ArgumentException("RAW byte count does not match the terrain dimensions.", nameof(rawBytes));
            }

            paths = GetPaths(definition);
            Directory.CreateDirectory(_rootDirectory);

            if (IsCurrent(definition))
            {
                return Result(TacviewTerrainInstallStatus.AlreadyCurrent, paths, null);
            }

            string xml = TacviewTerrainXml.Build(definition);
            string rawHash;
            using (SHA256 sha256 = SHA256.Create())
            {
                rawHash = TacviewTerrainId.ToLowerHex(sha256.ComputeHash(rawBytes));
            }

            // Publish data first and the XML declaration last. Tacview can never discover
            // a new declaration which points at a partially written RAW file.
            _fileWriter.Write(paths.RawPath, rawBytes);
            _fileWriter.Write(paths.ChecksumPath, Utf8NoBom.GetBytes(rawHash + "\n"));
            // Re-validate data before making the XML discoverable. A failed/corrupt
            // write leaves at most undiscoverable recorder-owned data files.
            if (new FileInfo(paths.RawPath).Length != definition.ExpectedRawLength ||
                !string.Equals(rawHash, ComputeFileHash(paths.RawPath), StringComparison.Ordinal))
            {
                throw new IOException("Terrain RAW verification failed before XML publication.");
            }

            _fileWriter.Write(paths.XmlPath, Utf8NoBom.GetBytes(xml));

            return Result(TacviewTerrainInstallStatus.Installed, paths, null);
        }
        catch (Exception ex)
        {
            return Result(
                TacviewTerrainInstallStatus.Failed,
                paths,
                ex.GetType().Name + ": " + ex.Message);
        }
    }

    public string ResolveManagedFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only direct VTOLVR-prefixed terrain files are managed.");
        }

        string extension = Path.GetExtension(fileName);
        if (!(extension.Equals(".raw", StringComparison.Ordinal) ||
            extension.Equals(".xml", StringComparison.Ordinal) ||
            extension.Equals(".sha256", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Only recorder RAW, XML, and checksum terrain files are managed.");
        }

        string terrainId = fileName.Substring(0, fileName.Length - extension.Length);
        if (!TacviewTerrainId.IsManaged(terrainId))
        {
            throw new InvalidOperationException("The terrain filename does not contain a safe recorder-managed ID.");
        }

        string candidate = Path.GetFullPath(Path.Combine(_rootDirectory, fileName));
        string rootWithSeparator = _rootDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Terrain file escaped the configured Tacview user directory.");
        }

        return candidate;
    }

    private TerrainPaths GetPaths(TacviewTerrainDefinition definition)
    {
        return new TerrainPaths(
            ResolveManagedFile(definition.RawFileName),
            ResolveManagedFile(definition.XmlFileName),
            ResolveManagedFile(definition.ChecksumFileName));
    }

    private static TacviewTerrainInstallResult Result(
        TacviewTerrainInstallStatus status,
        TerrainPaths paths,
        string error)
    {
        return new TacviewTerrainInstallResult(
            status,
            paths?.RawPath,
            paths?.XmlPath,
            paths?.ChecksumPath,
            error);
    }

    private static string ComputeFileHash(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (SHA256 sha256 = SHA256.Create())
        {
            return TacviewTerrainId.ToLowerHex(sha256.ComputeHash(stream));
        }
    }

    private static bool ByteArraysEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    private sealed class TerrainPaths
    {
        public TerrainPaths(string rawPath, string xmlPath, string checksumPath)
        {
            RawPath = rawPath;
            XmlPath = xmlPath;
            ChecksumPath = checksumPath;
        }

        public string RawPath { get; }

        public string XmlPath { get; }

        public string ChecksumPath { get; }
    }

    private sealed class AtomicTerrainFileWriter : IAtomicTerrainFileWriter
    {
        public void Write(string targetPath, byte[] contents)
        {
            string directory = Path.GetDirectoryName(targetPath);
            string temporaryPath = Path.Combine(
                directory,
                Path.GetFileName(targetPath) + ".tmp-" + Guid.NewGuid().ToString("N"));

            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    65536,
                    FileOptions.WriteThrough))
                {
                    stream.Write(contents, 0, contents.Length);
                    stream.Flush(true);
                }

                if (File.Exists(targetPath))
                {
                    File.Replace(temporaryPath, targetPath, null);
                }
                else
                {
                    File.Move(temporaryPath, targetPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }
}
