using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VTOLVRTacviewSPRecorder.Core;

internal interface IAtomicFileMover
{
    void Move(string sourcePath, string destinationPath);
}

internal sealed class AtomicFileMover : IAtomicFileMover
{
    public void Move(string sourcePath, string destinationPath)
    {
        File.Move(sourcePath, destinationPath);
    }
}

internal interface IUnconfirmedMapIdSuppressor
{
    void Suppress(string path, string escapedMapId);
}

internal sealed class UnconfirmedMapIdSuppressor : IUnconfirmedMapIdSuppressor
{
    public void Suppress(string path, string escapedMapId)
    {
        byte[] expectedLine = Encoding.UTF8.GetBytes("0,MapId=" + escapedMapId);
        using (var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan))
        {
            long lineStart = 0;
            int lineLength = 0;
            bool lineMatches = true;
            bool firstLine = true;

            while (true)
            {
                int value = stream.ReadByte();
                if (firstLine && lineLength == 0 && value == 0xef)
                {
                    if (stream.ReadByte() != 0xbb || stream.ReadByte() != 0xbf)
                    {
                        throw new IOException("The ACMI header begins with an invalid UTF-8 byte-order mark.");
                    }

                    lineStart = stream.Position;
                    firstLine = false;
                    continue;
                }

                if (value < 0 || value == '\n')
                {
                    if (lineMatches && lineLength == expectedLine.Length)
                    {
                        stream.Position = lineStart;
                        stream.WriteByte((byte)'/');
                        stream.WriteByte((byte)'/');
                        stream.Flush(true);
                        return;
                    }

                    if (value < 0)
                    {
                        break;
                    }

                    lineStart = stream.Position;
                    lineLength = 0;
                    lineMatches = true;
                    firstLine = false;
                    continue;
                }

                // MapId is a header property. Stop before scanning potentially
                // large entity telemetry if the expected reservation is corrupt.
                if (lineLength == 0 && value == '#')
                {
                    break;
                }

                if (lineLength >= expectedLine.Length || value != expectedLine[lineLength])
                {
                    lineMatches = false;
                }

                lineLength++;
            }
        }

        throw new IOException("The unconfirmed terrain MapId header could not be safely suppressed.");
    }
}

public sealed class TacviewEntityWriter : IDisposable
{
    public const ulong OwnshipObjectId = 0x100;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly TextWriter _writer;
    private readonly bool _leaveOpen;
    private readonly string _temporaryPath;
    private readonly IAtomicFileMover _fileMover;
    private readonly IUnconfirmedMapIdSuppressor _mapIdSuppressor;
    private readonly string _pendingMapId;
    private readonly Dictionary<ulong, EntitySnapshot> _lastSnapshots =
        new Dictionary<ulong, EntitySnapshot>();
    private readonly HashSet<ulong> _materializedObjectIds = new HashSet<ulong>();
    private readonly HashSet<ulong> _deletedObjectIds = new HashSet<ulong>();
    private readonly HashSet<ulong> _seenFrameObjectIds = new HashSet<ulong>();
    private readonly HashSet<ulong> _frameUpsertObjectIds = new HashSet<ulong>();
    private readonly List<PendingCommit> _pendingCommits = new List<PendingCommit>();
    private readonly StringBuilder _frame = new StringBuilder(4096);
    private bool _mapIdConfirmed;
    private bool _disposed;

    public TacviewEntityWriter(
        TextWriter writer,
        string missionName,
        DateTimeOffset referenceTimeUtc,
        TacviewRecordingMetadata recordingMetadata = null,
        bool leaveOpen = false)
        : this(
            writer,
            missionName,
            referenceTimeUtc,
            recordingMetadata,
            leaveOpen,
            null,
            null,
            new AtomicFileMover(),
            new UnconfirmedMapIdSuppressor())
    {
    }

    private TacviewEntityWriter(
        TextWriter writer,
        string missionName,
        DateTimeOffset referenceTimeUtc,
        TacviewRecordingMetadata recordingMetadata,
        bool leaveOpen,
        string temporaryPath,
        string finalPath,
        IAtomicFileMover fileMover,
        IUnconfirmedMapIdSuppressor mapIdSuppressor)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _fileMover = fileMover ?? throw new ArgumentNullException(nameof(fileMover));
        _mapIdSuppressor = mapIdSuppressor ?? throw new ArgumentNullException(nameof(mapIdSuppressor));
        _leaveOpen = leaveOpen;
        _temporaryPath = temporaryPath;
        FinalPath = finalPath;
        _pendingMapId = recordingMetadata?.MapId;
        // In-memory/custom writers retain their historical immediate-header behavior.
        // A file recording, however, must not publish an active MapId until its
        // matching terrain has been verified on disk.
        _mapIdConfirmed = temporaryPath == null || string.IsNullOrWhiteSpace(_pendingMapId);
        _writer.NewLine = "\n";
        WriteHeader(missionName ?? "Mission", referenceTimeUtc, recordingMetadata);
    }

    public string FinalPath { get; private set; }

    public static TacviewEntityWriter CreateFile(
        string outputDirectory,
        string missionName,
        DateTimeOffset referenceTimeUtc,
        TacviewRecordingMetadata recordingMetadata = null)
    {
        return CreateFile(
            outputDirectory,
            missionName,
            referenceTimeUtc,
            recordingMetadata,
            new AtomicFileMover(),
            new UnconfirmedMapIdSuppressor());
    }

    /// <summary>
    /// Records to file and also hands every flushed frame (header included) to
    /// <paramref name="realtimeSink"/>, e.g. <see cref="TacviewRealtimeServer.Publish"/>.
    /// A sink failure is reported once through <paramref name="onRealtimeError"/>
    /// and never interrupts the file recording.
    /// </summary>
    public static TacviewEntityWriter CreateFile(
        string outputDirectory,
        string missionName,
        DateTimeOffset referenceTimeUtc,
        TacviewRecordingMetadata recordingMetadata,
        Action<string> realtimeSink,
        Action<Exception> onRealtimeError)
    {
        return CreateFile(
            outputDirectory,
            missionName,
            referenceTimeUtc,
            recordingMetadata,
            new AtomicFileMover(),
            new UnconfirmedMapIdSuppressor(),
            realtimeSink,
            onRealtimeError);
    }

    internal static TacviewEntityWriter CreateFile(
        string outputDirectory,
        string missionName,
        DateTimeOffset referenceTimeUtc,
        TacviewRecordingMetadata recordingMetadata,
        IAtomicFileMover fileMover)
    {
        return CreateFile(
            outputDirectory,
            missionName,
            referenceTimeUtc,
            recordingMetadata,
            fileMover,
            new UnconfirmedMapIdSuppressor());
    }

    internal static TacviewEntityWriter CreateFile(
        string outputDirectory,
        string missionName,
        DateTimeOffset referenceTimeUtc,
        TacviewRecordingMetadata recordingMetadata,
        IAtomicFileMover fileMover,
        IUnconfirmedMapIdSuppressor mapIdSuppressor,
        Action<string> realtimeSink = null,
        Action<Exception> onRealtimeError = null)
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
        TextWriter writer = null;

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
            if (realtimeSink != null)
            {
                writer = new TeeTextWriter(writer, realtimeSink, onRealtimeError);
            }

            return new TacviewEntityWriter(
                writer,
                missionName,
                referenceTimeUtc,
                recordingMetadata,
                false,
                temporaryPath,
                finalPath,
                fileMover,
                mapIdSuppressor);
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

    public void WriteFrame(
        double relativeTimeSeconds,
        List<TacviewEntityUpdate> updates,
        List<TacviewEvent> events)
    {
        ThrowIfDisposed();

        if (updates == null)
        {
            throw new ArgumentNullException(nameof(updates));
        }

        if (events == null)
        {
            throw new ArgumentNullException(nameof(events));
        }

        ValidateFinite(relativeTimeSeconds, nameof(relativeTimeSeconds));
        ValidateFrame(updates, events);
        updates.Sort(CompareUpdates);

        _frame.Clear();
        _pendingCommits.Clear();
        _frame.Append('#');
        _frame.Append(relativeTimeSeconds.ToString("0.000", Invariant));
        _frame.Append('\n');
        int contentStart = _frame.Length;

        for (int index = 0; index < updates.Count; index++)
        {
            TacviewEntityUpdate update = updates[index];
            if (!update.IsDelete)
            {
                AppendUpsert(update);
            }
        }

        for (int index = 0; index < events.Count; index++)
        {
            AppendEvent(events[index]);
        }

        for (int index = 0; index < updates.Count; index++)
        {
            TacviewEntityUpdate update = updates[index];
            if (update.IsDelete)
            {
                AppendDeletion(update);
            }
        }

        if (_frame.Length == contentStart)
        {
            CommitFrame();
            return;
        }

        _writer.Write(_frame.ToString());
        _writer.Flush();
        CommitFrame();
    }

    public void Flush()
    {
        if (!_disposed)
        {
            _writer.Flush();
        }
    }

    public void ConfirmMapId()
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(_pendingMapId))
        {
            throw new InvalidOperationException("This recording has no terrain MapId to confirm.");
        }

        _mapIdConfirmed = true;
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

        if (!_mapIdConfirmed)
        {
            _mapIdSuppressor.Suppress(_temporaryPath, EscapeText(_pendingMapId));
        }

        string destinationPath = File.Exists(FinalPath)
            ? GetAvailablePath(FinalPath)
            : FinalPath;
        _fileMover.Move(_temporaryPath, destinationPath);
        FinalPath = destinationPath;
    }

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

    private void WriteHeader(
        string missionName,
        DateTimeOffset referenceTimeUtc,
        TacviewRecordingMetadata recordingMetadata)
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

        if (!string.IsNullOrWhiteSpace(recordingMetadata?.Theater))
        {
            _writer.WriteLine($"0,Theater={EscapeText(recordingMetadata.Theater)}");
        }

        if (!string.IsNullOrWhiteSpace(recordingMetadata?.MapId))
        {
            _writer.WriteLine($"0,MapId={EscapeText(recordingMetadata.MapId)}");
        }

        if (!string.IsNullOrWhiteSpace(recordingMetadata?.Category))
        {
            _writer.WriteLine($"0,Category={EscapeText(recordingMetadata.Category)}");
        }

        if (!string.IsNullOrWhiteSpace(recordingMetadata?.Briefing))
        {
            _writer.WriteLine($"0,Briefing={EscapeText(recordingMetadata.Briefing)}");
        }

        _writer.WriteLine("0,Comments=Geodetic position from VTOL VR GPS; native U/V are floating-origin-corrected world X/Z meters.");
    }

    private void AppendUpsert(TacviewEntityUpdate update)
    {
        EntityMetadata metadata = FilterUnknownRelationships(update.Metadata);
        bool exists = _lastSnapshots.TryGetValue(update.ObjectId, out EntitySnapshot previous);
        bool transformChanged = !exists || !TransformsEquivalent(update.Transform, previous.Transform);
        bool metadataChanged = !exists || !MetadataEquals(metadata, previous.Metadata);

        if (!transformChanged && !metadataChanged)
        {
            return;
        }

        int lineStart = _frame.Length;
        string objectId = update.ObjectId.ToString("X", Invariant);
        _frame.Append(objectId);
        if (transformChanged)
        {
            _frame.Append(",T=");
            AppendTransform(update.Transform, exists ? previous.Transform : (EntityTransform?)null);
        }

        AppendMetadata(metadata, exists ? previous.Metadata : null);

        if (!transformChanged && _frame.Length == lineStart + objectId.Length)
        {
            // Relationship and optional-text clearing semantics are not
            // documented well enough to emit invented sentinel values.
            _frame.Length = lineStart;
            return;
        }

        _frame.Append('\n');
        _pendingCommits.Add(PendingCommit.Upsert(
            update.ObjectId,
            new EntitySnapshot(update.Transform, metadata)));
    }

    private EntityMetadata FilterUnknownRelationships(EntityMetadata metadata)
    {
        ulong? parent = FilterKnownObjectId(metadata.ParentObjectId);
        ulong? lockedTarget = FilterKnownObjectId(metadata.LockedTargetObjectId);
        ulong? next = FilterKnownObjectId(metadata.NextObjectId);
        if (parent == metadata.ParentObjectId &&
            lockedTarget == metadata.LockedTargetObjectId &&
            next == metadata.NextObjectId)
        {
            return metadata;
        }

        return metadata.WithRelationships(parent, lockedTarget, next);
    }

    private ulong? FilterKnownObjectId(ulong? objectId)
    {
        return objectId.HasValue && IsKnownOrCreatedThisFrame(objectId.Value)
            ? objectId
            : null;
    }

    private bool IsKnownOrCreatedThisFrame(ulong objectId)
    {
        return _materializedObjectIds.Contains(objectId) ||
            _frameUpsertObjectIds.Contains(objectId);
    }

    private void AppendTransform(EntityTransform current, EntityTransform? previous)
    {
        AppendTransformField(current.LongitudeDegrees, previous?.LongitudeDegrees, "0.0000000", 7, false);
        AppendTransformField(current.LatitudeDegrees, previous?.LatitudeDegrees, "0.0000000", 7, true);
        AppendTransformField(current.AltitudeMeters, previous?.AltitudeMeters, "0.00", 2, true);
        AppendTransformField(current.RollDegrees, previous?.RollDegrees, "0.00", 2, true);
        AppendTransformField(current.PitchDegrees, previous?.PitchDegrees, "0.00", 2, true);
        AppendTransformField(current.YawDegrees, previous?.YawDegrees, "0.00", 2, true);
        AppendTransformField(current.NativeUPositionMeters, previous?.NativeUPositionMeters, "0.00", 2, true);
        AppendTransformField(current.NativeVPositionMeters, previous?.NativeVPositionMeters, "0.00", 2, true);
        AppendTransformField(current.NativeHeadingDegrees, previous?.NativeHeadingDegrees, "0.00", 2, true);
    }

    private void AppendTransformField(
        double current,
        double? previous,
        string format,
        int decimalPlaces,
        bool prependSeparator)
    {
        if (prependSeparator)
        {
            _frame.Append('|');
        }

        if (!previous.HasValue || !RoundedEquals(current, previous.Value, decimalPlaces))
        {
            _frame.Append(current.ToString(format, Invariant));
        }
    }

    private static bool TransformsEquivalent(EntityTransform left, EntityTransform right)
    {
        return RoundedEquals(left.LongitudeDegrees, right.LongitudeDegrees, 7) &&
            RoundedEquals(left.LatitudeDegrees, right.LatitudeDegrees, 7) &&
            RoundedEquals(left.AltitudeMeters, right.AltitudeMeters, 2) &&
            RoundedEquals(left.RollDegrees, right.RollDegrees, 2) &&
            RoundedEquals(left.PitchDegrees, right.PitchDegrees, 2) &&
            RoundedEquals(left.YawDegrees, right.YawDegrees, 2) &&
            RoundedEquals(left.NativeUPositionMeters, right.NativeUPositionMeters, 2) &&
            RoundedEquals(left.NativeVPositionMeters, right.NativeVPositionMeters, 2) &&
            RoundedEquals(left.NativeHeadingDegrees, right.NativeHeadingDegrees, 2);
    }

    private static bool RoundedEquals(double left, double right, int decimalPlaces)
    {
        return Math.Round(left, decimalPlaces) == Math.Round(right, decimalPlaces);
    }

    private void AppendMetadata(EntityMetadata metadata, EntityMetadata previous)
    {
        AppendMetadataText("Type", metadata.Type, previous?.Type, required: true);
        AppendMetadataText("Name", metadata.Name, previous?.Name, required: true);
        AppendMetadataText("Shape", metadata.Shape, previous?.Shape, required: false);
        AppendMetadataText("Coalition", metadata.Coalition, previous?.Coalition, required: true);
        AppendMetadataText("Color", metadata.Color, previous?.Color, required: true);
        AppendMetadataText("CallSign", metadata.CallSign, previous?.CallSign, required: false);
        AppendMetadataText("Pilot", metadata.Pilot, previous?.Pilot, required: false);
        AppendMetadataId("Parent", metadata.ParentObjectId, previous?.ParentObjectId);
        AppendMetadataId("LockedTarget", metadata.LockedTargetObjectId, previous?.LockedTargetObjectId);
        AppendMetadataId("Next", metadata.NextObjectId, previous?.NextObjectId);
    }

    private void AppendMetadataText(
        string propertyName,
        string value,
        string previousValue,
        bool required)
    {
        if (string.Equals(value, previousValue, StringComparison.Ordinal) ||
            (!required && string.IsNullOrWhiteSpace(value)))
        {
            return;
        }

        _frame.Append(',');
        _frame.Append(propertyName);
        _frame.Append('=');
        _frame.Append(EscapeText(value));
    }

    private void AppendMetadataId(string propertyName, ulong? value, ulong? previousValue)
    {
        if (!value.HasValue || value == previousValue)
        {
            return;
        }

        _frame.Append(',');
        _frame.Append(propertyName);
        _frame.Append('=');
        _frame.Append(value.Value.ToString("X", Invariant));
    }

    private void AppendEvent(TacviewEvent frameEvent)
    {
        switch (frameEvent.Kind)
        {
            case TacviewEventKind.Timeout:
                if (!IsKnownOrCreatedThisFrame(frameEvent.SourceObjectId))
                {
                    return;
                }

                _frame.Append("0,Event=Timeout|SourceId:");
                _frame.Append(frameEvent.SourceObjectId.ToString("X", Invariant));
                _frame.Append("|AmmoType:");
                _frame.Append(EscapeEventValue(frameEvent.AmmoType));
                _frame.Append("|AmmoCount:");
                _frame.Append(frameEvent.AmmoCount.ToString(Invariant));
                if (frameEvent.TargetObjectId.HasValue &&
                    IsKnownOrCreatedThisFrame(frameEvent.TargetObjectId.Value))
                {
                    _frame.Append("|TargetId:");
                    _frame.Append(frameEvent.TargetObjectId.Value.ToString("X", Invariant));
                }

                if (!string.IsNullOrWhiteSpace(frameEvent.Outcome))
                {
                    _frame.Append("|Outcome:");
                    _frame.Append(EscapeEventValue(frameEvent.Outcome));
                }

                _frame.Append('\n');
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(frameEvent));
        }
    }

    private void AppendDeletion(TacviewEntityUpdate update)
    {
        if (!_lastSnapshots.ContainsKey(update.ObjectId))
        {
            _pendingCommits.Add(PendingCommit.Delete(update.ObjectId));
            return;
        }

        string objectId = update.ObjectId.ToString("X", Invariant);
        if (update.RemovalReason != TacviewEntityRemovalReason.Deleted)
        {
            bool destroyed = update.RemovalReason == TacviewEntityRemovalReason.Destroyed;
            _frame.Append("0,Event=");
            _frame.Append(destroyed ? "Destroyed" : "LeftArea");
            _frame.Append('|');
            _frame.Append(objectId);
            _frame.Append('|');
            _frame.Append(EscapeEventValue(update.RemovalLabel));
            _frame.Append(destroyed ? " destroyed" : " left area");
            _frame.Append('\n');
        }

        _frame.Append('-');
        _frame.Append(objectId);
        _frame.Append('\n');
        _pendingCommits.Add(PendingCommit.Delete(update.ObjectId));
    }

    private void CommitFrame()
    {
        for (int index = 0; index < _pendingCommits.Count; index++)
        {
            PendingCommit commit = _pendingCommits[index];
            if (commit.IsDelete)
            {
                _lastSnapshots.Remove(commit.ObjectId);
                _deletedObjectIds.Add(commit.ObjectId);
            }
            else
            {
                _lastSnapshots[commit.ObjectId] = commit.Snapshot;
                _materializedObjectIds.Add(commit.ObjectId);
            }
        }

        _pendingCommits.Clear();
        _frame.Clear();
    }

    private void ValidateFrame(List<TacviewEntityUpdate> updates, List<TacviewEvent> events)
    {
        _seenFrameObjectIds.Clear();
        _frameUpsertObjectIds.Clear();

        for (int index = 0; index < updates.Count; index++)
        {
            TacviewEntityUpdate update = updates[index];
            if (!_seenFrameObjectIds.Add(update.ObjectId))
            {
                throw new ArgumentException(
                    "An ACMI frame cannot contain an object ID more than once.",
                    nameof(updates));
            }

            if (_deletedObjectIds.Contains(update.ObjectId))
            {
                throw new InvalidOperationException(
                    "A deleted ACMI object ID cannot be used again in the same session.");
            }

            if (update.IsDelete)
            {
                continue;
            }

            _frameUpsertObjectIds.Add(update.ObjectId);

            if (update.Metadata == null)
            {
                throw new ArgumentException("Entity metadata is required.", nameof(updates));
            }

            ValidateTransform(update.Transform);
        }

        for (int index = 0; index < events.Count; index++)
        {
            TacviewEvent frameEvent = events[index];
            if (frameEvent.Kind != TacviewEventKind.Timeout ||
                frameEvent.SourceObjectId == 0 ||
                string.IsNullOrWhiteSpace(frameEvent.AmmoType) ||
                frameEvent.AmmoCount < 1)
            {
                throw new ArgumentException("The ACMI event is invalid.", nameof(events));
            }
        }
    }

    private static void ValidateTransform(EntityTransform transform)
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

    private static bool MetadataEquals(EntityMetadata left, EntityMetadata right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        return string.Equals(left.Type, right.Type, StringComparison.Ordinal) &&
            string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
            string.Equals(left.Coalition, right.Coalition, StringComparison.Ordinal) &&
            string.Equals(left.Color, right.Color, StringComparison.Ordinal) &&
            string.Equals(left.CallSign, right.CallSign, StringComparison.Ordinal) &&
            string.Equals(left.Pilot, right.Pilot, StringComparison.Ordinal) &&
            string.Equals(left.Shape, right.Shape, StringComparison.Ordinal) &&
            left.ParentObjectId == right.ParentObjectId &&
            left.LockedTargetObjectId == right.LockedTargetObjectId &&
            left.NextObjectId == right.NextObjectId;
    }

    private static int CompareUpdates(TacviewEntityUpdate left, TacviewEntityUpdate right)
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
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace(",", "\\,")
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private static string EscapeEventValue(string value)
    {
        return EscapeText(value).Replace('|', '/').Replace(':', '-');
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TacviewEntityWriter));
        }
    }

    private readonly struct EntitySnapshot
    {
        public EntitySnapshot(EntityTransform transform, EntityMetadata metadata)
        {
            Transform = transform;
            Metadata = metadata;
        }

        public EntityTransform Transform { get; }

        public EntityMetadata Metadata { get; }
    }

    private readonly struct PendingCommit
    {
        private PendingCommit(
            ulong objectId,
            EntitySnapshot snapshot,
            bool isDelete)
        {
            ObjectId = objectId;
            Snapshot = snapshot;
            IsDelete = isDelete;
        }

        public ulong ObjectId { get; }

        public EntitySnapshot Snapshot { get; }

        public bool IsDelete { get; }

        public static PendingCommit Upsert(ulong objectId, EntitySnapshot snapshot)
        {
            return new PendingCommit(objectId, snapshot, false);
        }

        public static PendingCommit Delete(ulong objectId)
        {
            return new PendingCommit(objectId, default, true);
        }
    }

}
