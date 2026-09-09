using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VTOLVRTacviewSPRecorder.Core;

public sealed class TacviewAcmiWriter : IDisposable
{
    public const ulong OwnshipObjectId = 0x100;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly AircraftMetadata LegacyOwnshipMetadata = new AircraftMetadata(
        "Air+FixedWing",
        "Ownship",
        "Allies",
        "Blue");

    private readonly TextWriter _writer;
    private readonly bool _leaveOpen;
    private readonly string _temporaryPath;
    private readonly Dictionary<ulong, string[]> _lastTransforms =
        new Dictionary<ulong, string[]>();
    private readonly Dictionary<ulong, AircraftMetadata> _lastMetadata =
        new Dictionary<ulong, AircraftMetadata>();
    private readonly HashSet<ulong> _createdObjectIds = new HashSet<ulong>();
    private readonly HashSet<ulong> _deletedObjectIds = new HashSet<ulong>();
    private bool _disposed;

    public TacviewAcmiWriter(
        TextWriter writer,
        string missionName,
        DateTimeOffset referenceTimeUtc,
        bool leaveOpen = false)
        : this(writer, missionName, referenceTimeUtc, leaveOpen, null, null)
    {
    }

    private TacviewAcmiWriter(
        TextWriter writer,
        string missionName,
        DateTimeOffset referenceTimeUtc,
        bool leaveOpen,
        string temporaryPath,
        string finalPath)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _leaveOpen = leaveOpen;
        _temporaryPath = temporaryPath;
        FinalPath = finalPath;
        _writer.NewLine = "\n";

        WriteHeader(missionName ?? "Mission", referenceTimeUtc);
    }

    public string FinalPath { get; private set; }

    public static TacviewAcmiWriter CreateFile(
        string outputDirectory,
        string missionName,
        DateTimeOffset referenceTimeUtc)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("An output directory is required.", nameof(outputDirectory));
        }

        Directory.CreateDirectory(outputDirectory);

        string requestedPath = Path.Combine(
            outputDirectory,
            TacviewFileName.Build(referenceTimeUtc, missionName));
        string finalPath = GetAvailablePath(requestedPath);
        string temporaryPath = finalPath + ".tmp";

        FileStream stream = null;
        StreamWriter writer = null;

        try
        {
            stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            writer = new StreamWriter(stream, new UTF8Encoding(true), 4096)
            {
                AutoFlush = false
            };
            stream = null;

            return new TacviewAcmiWriter(
                writer,
                missionName,
                referenceTimeUtc,
                false,
                temporaryPath,
                finalPath);
        }
        catch
        {
            writer?.Dispose();
            stream?.Dispose();
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    public void WriteOwnshipFrame(double relativeTimeSeconds, AircraftTransform transform)
    {
        WriteFrame(
            relativeTimeSeconds,
            new[]
            {
                TacviewObjectUpdate.Upsert(OwnshipObjectId, transform, LegacyOwnshipMetadata)
            });
    }

    public void WriteFrame(double relativeTimeSeconds, IEnumerable<TacviewObjectUpdate> updates)
    {
        ThrowIfDisposed();

        if (updates == null)
        {
            throw new ArgumentNullException(nameof(updates));
        }

        var frameUpdates = new List<TacviewObjectUpdate>();
        foreach (TacviewObjectUpdate update in updates)
        {
            frameUpdates.Add(update);
        }
        if (frameUpdates.Count == 0)
        {
            return;
        }

        ValidateFrame(relativeTimeSeconds, frameUpdates);
        frameUpdates.Sort(CompareUpdates);

        var pendingTransforms = new Dictionary<ulong, string[]>();
        var pendingMetadata = new Dictionary<ulong, AircraftMetadata>();
        var pendingCreations = new List<ulong>();
        var pendingDeletes = new List<ulong>();

        _writer.Write('#');
        _writer.WriteLine(Format(relativeTimeSeconds, "0.000"));

        foreach (TacviewObjectUpdate update in frameUpdates)
        {
            if (update.IsDelete)
            {
                WriteDeletion(update);
                pendingDeletes.Add(update.ObjectId);
                continue;
            }

            string[] transform = BuildTransform(update.Transform);
            bool isCreated = _createdObjectIds.Contains(update.ObjectId);

            _writer.Write(update.ObjectId.ToString("X", Invariant));
            _writer.Write(",T=");
            WriteTransform(update.ObjectId, transform);

            if (!isCreated)
            {
                WriteMetadata(update.ObjectId, update.Metadata);
                pendingCreations.Add(update.ObjectId);
            }
            else
            {
                WriteMetadata(update.ObjectId, update.Metadata);
            }

            _writer.WriteLine();
            pendingTransforms.Add(update.ObjectId, transform);
            pendingMetadata.Add(update.ObjectId, update.Metadata);
        }

        _writer.Flush();
        CommitFrame(pendingTransforms, pendingMetadata, pendingCreations, pendingDeletes);
    }

    public void Flush()
    {
        if (!_disposed)
        {
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _writer.Flush();
        }
        finally
        {
            if (!_leaveOpen)
            {
                _writer.Dispose();
            }
        }

        if (_temporaryPath == null)
        {
            return;
        }

        string destinationPath = File.Exists(FinalPath)
            ? GetAvailablePath(FinalPath)
            : FinalPath;
        File.Move(_temporaryPath, destinationPath);
        FinalPath = destinationPath;
    }

    // A failed frame must never be promoted to a normal .acmi result. Keep
    // the temporary file in place for diagnostics and let the caller decide
    // whether it can be recovered.
    public void Abort()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            _writer.Dispose();
        }
    }

    private void WriteHeader(string missionName, DateTimeOffset referenceTimeUtc)
    {
        string utcTimestamp = referenceTimeUtc.ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Invariant);

        _writer.WriteLine("FileType=text/acmi/tacview");
        _writer.WriteLine("FileVersion=2.2");
        _writer.WriteLine($"0,ReferenceTime={utcTimestamp}");
        _writer.WriteLine($"0,RecordingTime={utcTimestamp}");
        _writer.WriteLine("0,DataSource=VTOL VR");
        _writer.WriteLine("0,DataRecorder=VTOL VR Tacview Singleplayer Recorder");
        _writer.WriteLine($"0,Title={EscapeText(missionName)}");
        _writer.WriteLine("0,Comments=Geodetic position from VTOL VR GPS; native U/V are floating-origin-corrected world X/Z meters.");
    }

    private void WriteTransform(ulong objectId, string[] transform)
    {
        _lastTransforms.TryGetValue(objectId, out string[] previous);

        for (int index = 0; index < transform.Length; index++)
        {
            if (index > 0)
            {
                _writer.Write('|');
            }

            if (previous == null ||
                !string.Equals(transform[index], previous[index], StringComparison.Ordinal))
            {
                _writer.Write(transform[index]);
            }
        }
    }

    private void WriteMetadata(ulong objectId, AircraftMetadata metadata)
    {
        _lastMetadata.TryGetValue(objectId, out AircraftMetadata previous);

        WriteMetadataValue("Type", metadata.Type, previous?.Type);
        WriteMetadataValue("Name", metadata.Name, previous?.Name);
        WriteMetadataValue("Coalition", metadata.Coalition, previous?.Coalition);
        WriteMetadataValue("Color", metadata.Color, previous?.Color);

        if (!string.IsNullOrWhiteSpace(metadata.CallSign) &&
            !string.Equals(metadata.CallSign, previous?.CallSign, StringComparison.Ordinal))
        {
            _writer.Write(",CallSign=");
            _writer.Write(EscapeText(metadata.CallSign));
        }

        if (!string.IsNullOrWhiteSpace(metadata.Pilot) &&
            !string.Equals(metadata.Pilot, previous?.Pilot, StringComparison.Ordinal))
        {
            _writer.Write(",Pilot=");
            _writer.Write(EscapeText(metadata.Pilot));
        }
    }

    private void WriteMetadataValue(string propertyName, string value, string previousValue)
    {
        if (string.Equals(value, previousValue, StringComparison.Ordinal))
        {
            return;
        }

        _writer.Write(',');
        _writer.Write(propertyName);
        _writer.Write('=');
        _writer.Write(EscapeText(value));
    }

    private void WriteDeletion(TacviewObjectUpdate update)
    {
        string eventName = update.RemovalReason == TacviewObjectRemovalReason.Destroyed
            ? "Destroyed"
            : "LeftArea";
        string objectId = update.ObjectId.ToString("X", Invariant);

        _writer.Write("0,Event=");
        _writer.Write(eventName);
        _writer.Write('|');
        _writer.Write(objectId);
        _writer.Write('|');
        string description = update.RemovalReason == TacviewObjectRemovalReason.Destroyed
            ? $"{update.RemovalLabel} destroyed"
            : $"{update.RemovalLabel} left area";
        _writer.Write(EscapeText(description));
        _writer.WriteLine();
        _writer.Write('-');
        _writer.WriteLine(objectId);
    }

    private void CommitFrame(
        Dictionary<ulong, string[]> pendingTransforms,
        Dictionary<ulong, AircraftMetadata> pendingMetadata,
        List<ulong> pendingCreations,
        List<ulong> pendingDeletes)
    {
        foreach (KeyValuePair<ulong, string[]> transform in pendingTransforms)
        {
            _lastTransforms[transform.Key] = transform.Value;
        }

        foreach (KeyValuePair<ulong, AircraftMetadata> metadata in pendingMetadata)
        {
            _lastMetadata[metadata.Key] = metadata.Value;
        }

        foreach (ulong objectId in pendingCreations)
        {
            _createdObjectIds.Add(objectId);
        }

        foreach (ulong objectId in pendingDeletes)
        {
            _lastTransforms.Remove(objectId);
            _lastMetadata.Remove(objectId);
            _deletedObjectIds.Add(objectId);
        }
    }

    private void ValidateFrame(double relativeTimeSeconds, List<TacviewObjectUpdate> updates)
    {
        ValidateFinite(relativeTimeSeconds, nameof(relativeTimeSeconds));
        var seenObjectIds = new HashSet<ulong>();

        foreach (TacviewObjectUpdate update in updates)
        {
            if (!seenObjectIds.Add(update.ObjectId))
            {
                throw new ArgumentException("An ACMI frame cannot contain an object ID more than once.", nameof(updates));
            }

            if (_deletedObjectIds.Contains(update.ObjectId))
            {
                throw new InvalidOperationException("A deleted ACMI object ID cannot be used again in the same session.");
            }

            if (update.IsDelete)
            {
                continue;
            }

            if (update.Metadata == null)
            {
                throw new ArgumentException("Aircraft metadata is required for an ACMI transform update.", nameof(updates));
            }

            ValidateTransform(update.Transform);
        }
    }

    private static void ValidateTransform(AircraftTransform transform)
    {
        ValidateFinite(transform.LongitudeDegrees, nameof(transform.LongitudeDegrees));
        ValidateFinite(transform.LatitudeDegrees, nameof(transform.LatitudeDegrees));
        ValidateFinite(transform.AltitudeMeters, nameof(transform.AltitudeMeters));
        ValidateFinite(transform.RollDegrees, nameof(transform.RollDegrees));
        ValidateFinite(transform.PitchDegrees, nameof(transform.PitchDegrees));
        ValidateFinite(transform.YawDegrees, nameof(transform.YawDegrees));
        ValidateFinite(transform.NativeUPositionMeters, nameof(transform.NativeUPositionMeters));
        ValidateFinite(transform.NativeVPositionMeters, nameof(transform.NativeVPositionMeters));
        ValidateFinite(transform.NativeHeadingDegrees, nameof(transform.NativeHeadingDegrees));
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, "ACMI numeric values must be finite.");
        }
    }

    private static string[] BuildTransform(AircraftTransform transform)
    {
        return new[]
        {
            Format(transform.LongitudeDegrees, "0.0000000"),
            Format(transform.LatitudeDegrees, "0.0000000"),
            Format(transform.AltitudeMeters, "0.00"),
            Format(transform.RollDegrees, "0.00"),
            Format(transform.PitchDegrees, "0.00"),
            Format(transform.YawDegrees, "0.00"),
            Format(transform.NativeUPositionMeters, "0.00"),
            Format(transform.NativeVPositionMeters, "0.00"),
            Format(transform.NativeHeadingDegrees, "0.00")
        };
    }

    private static int CompareUpdates(TacviewObjectUpdate left, TacviewObjectUpdate right)
    {
        if (left.ObjectId == right.ObjectId)
        {
            return 0;
        }

        if (left.ObjectId == OwnshipObjectId)
        {
            return -1;
        }

        if (right.ObjectId == OwnshipObjectId)
        {
            return 1;
        }

        return left.ObjectId < right.ObjectId ? -1 : 1;
    }

    private static string GetAvailablePath(string requestedPath)
    {
        if (!File.Exists(requestedPath) && !File.Exists(requestedPath + ".tmp"))
        {
            return requestedPath;
        }

        string directory = Path.GetDirectoryName(requestedPath);
        string fileName = Path.GetFileNameWithoutExtension(requestedPath);
        string extension = Path.GetExtension(requestedPath);

        for (int suffix = 1; ; suffix++)
        {
            string candidate = Path.Combine(directory, $"{fileName}_{suffix}{extension}");
            if (!File.Exists(candidate) && !File.Exists(candidate + ".tmp"))
            {
                return candidate;
            }
        }
    }

    private static string EscapeText(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace(",", "\\,")
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private static string Format(double value, string format)
    {
        return value.ToString(format, Invariant);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TacviewAcmiWriter));
        }
    }
}
