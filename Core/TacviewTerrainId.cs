using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VTOLVRTacviewSPRecorder.Core;

public static class TacviewTerrainId
{
    public const string ManagedPrefix = "VTOLVR.";
    private const int MaxMapSegmentLength = 48;
    private const int FingerprintLength = 16;

    public static string BuildBase(string mapIdentity)
    {
        return ManagedPrefix + SanitizeMapSegment(mapIdentity);
    }

    public static string BuildVersioned(string mapIdentity, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new ArgumentException("A terrain fingerprint is required.", nameof(fingerprint));
        }

        string normalizedFingerprint = fingerprint.Trim().ToLowerInvariant();
        for (int index = 0; index < normalizedFingerprint.Length; index++)
        {
            char character = normalizedFingerprint[index];
            if (!((character >= '0' && character <= '9') ||
                (character >= 'a' && character <= 'f')))
            {
                throw new ArgumentException("The terrain fingerprint must be hexadecimal.", nameof(fingerprint));
            }
        }

        if (normalizedFingerprint.Length < FingerprintLength)
        {
            throw new ArgumentException(
                $"The terrain fingerprint must contain at least {FingerprintLength} hexadecimal characters.",
                nameof(fingerprint));
        }

        return BuildBase(mapIdentity) + "." +
            normalizedFingerprint.Substring(0, FingerprintLength);
    }

    public static string SanitizeMapSegment(string value)
    {
        string normalized = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(Math.Min(normalized.Length, MaxMapSegmentLength));
        bool pendingSeparator = false;

        for (int index = 0; index < normalized.Length; index++)
        {
            char character = normalized[index];
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark ||
                category == UnicodeCategory.SpacingCombiningMark ||
                category == UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            bool isAsciiLetter =
                (character >= 'a' && character <= 'z') ||
                (character >= 'A' && character <= 'Z');
            bool isDigit = character >= '0' && character <= '9';
            bool isSafePunctuation = character == '-' || character == '_' || character == '.';

            if (isAsciiLetter || isDigit || isSafePunctuation)
            {
                if (pendingSeparator && builder.Length > 0 &&
                    builder.Length < MaxMapSegmentLength && !EndsWithSeparator(builder))
                {
                    builder.Append('-');
                }

                pendingSeparator = false;
                if (builder.Length < MaxMapSegmentLength)
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
            }
            else
            {
                pendingSeparator = true;
            }

            if (builder.Length >= MaxMapSegmentLength)
            {
                break;
            }
        }

        string result = builder.ToString().Trim('.', '-', '_');
        while (result.Contains(".."))
        {
            result = result.Replace("..", ".");
        }

        return string.IsNullOrEmpty(result) ? "map" : result;
    }

    public static bool IsManaged(string terrainId)
    {
        if (string.IsNullOrEmpty(terrainId) ||
            !terrainId.StartsWith(ManagedPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (int index = ManagedPrefix.Length; index < terrainId.Length; index++)
        {
            char character = terrainId[index];
            if (!((character >= 'a' && character <= 'z') ||
                (character >= '0' && character <= '9') ||
                character == '-' || character == '_' || character == '.'))
            {
                return false;
            }
        }

        return terrainId.Length > ManagedPrefix.Length &&
            terrainId.IndexOf("..", StringComparison.Ordinal) < 0;
    }

    public static string ComputeFingerprint(TacviewTerrainFingerprintInput input)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        input.Validate();
        var canonical = new StringBuilder(4096);
        AppendCanonical(canonical, "schema", "1");
        AppendCanonical(canonical, "map", input.MapIdentity?.Trim() ?? string.Empty);
        AppendCanonical(canonical, "source", input.SourceSignature?.Trim() ?? string.Empty);
        AppendCanonical(canonical, "world-size", Format(input.WorldSizeMeters));
        AppendCanonical(canonical, "source-width", input.SourceWidth.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(canonical, "source-height", input.SourceHeight.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(canonical, "export-width", input.ExportWidth.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(canonical, "export-height", input.ExportHeight.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(canonical, "min-height", Format(input.MinimumHeightMeters));
        AppendCanonical(canonical, "max-height", Format(input.MaximumHeightMeters));
        AppendPoint(canonical, "bottom-left", input.BottomLeft);
        AppendPoint(canonical, "bottom-right", input.BottomRight);
        AppendPoint(canonical, "top-right", input.TopRight);
        AppendPoint(canonical, "top-left", input.TopLeft);

        for (int index = 0; index < input.SparseHeightSamples.Length; index++)
        {
            AppendCanonical(
                canonical,
                "sample-" + index.ToString(CultureInfo.InvariantCulture),
                Format(input.SparseHeightSamples[index]));
        }

        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] bytes = Encoding.UTF8.GetBytes(canonical.ToString());
            return ToLowerHex(sha256.ComputeHash(bytes));
        }
    }

    internal static string ToLowerHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        for (int index = 0; index < bytes.Length; index++)
        {
            builder.Append(bytes[index].ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static bool EndsWithSeparator(StringBuilder builder)
    {
        char character = builder[builder.Length - 1];
        return character == '-' || character == '_' || character == '.';
    }

    private static void AppendCanonical(StringBuilder builder, string key, string value)
    {
        builder.Append(key);
        builder.Append('=');
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append('\n');
    }

    private static void AppendPoint(StringBuilder builder, string key, TacviewTerrainGeoPoint point)
    {
        AppendCanonical(builder, key + "-longitude", Format(point.Longitude));
        AppendCanonical(builder, key + "-latitude", Format(point.Latitude));
    }

    private static string Format(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}

public sealed class TacviewTerrainFingerprintInput
{
    public string MapIdentity { get; set; }

    public string SourceSignature { get; set; }

    public double WorldSizeMeters { get; set; }

    public int SourceWidth { get; set; }

    public int SourceHeight { get; set; }

    public int ExportWidth { get; set; }

    public int ExportHeight { get; set; }

    public double MinimumHeightMeters { get; set; }

    public double MaximumHeightMeters { get; set; }

    public TacviewTerrainGeoPoint BottomLeft { get; set; }

    public TacviewTerrainGeoPoint BottomRight { get; set; }

    public TacviewTerrainGeoPoint TopRight { get; set; }

    public TacviewTerrainGeoPoint TopLeft { get; set; }

    public double[] SparseHeightSamples { get; set; } = Array.Empty<double>();

    internal void Validate()
    {
        if (!TacviewTerrainDefinition.IsFinite(WorldSizeMeters) || WorldSizeMeters <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(WorldSizeMeters));
        }

        if (SourceWidth < 2 || SourceHeight < 2 || ExportWidth < 2 || ExportHeight < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(SourceWidth), "Terrain dimensions must be at least 2x2.");
        }

        if (!TacviewTerrainDefinition.IsFinite(MinimumHeightMeters) ||
            !TacviewTerrainDefinition.IsFinite(MaximumHeightMeters) ||
            MaximumHeightMeters < MinimumHeightMeters)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumHeightMeters));
        }

        ValidatePoint(BottomLeft, nameof(BottomLeft));
        ValidatePoint(BottomRight, nameof(BottomRight));
        ValidatePoint(TopRight, nameof(TopRight));
        ValidatePoint(TopLeft, nameof(TopLeft));

        if (SparseHeightSamples == null || SparseHeightSamples.Length == 0)
        {
            throw new ArgumentException("At least one sparse terrain sample is required.", nameof(SparseHeightSamples));
        }

        for (int index = 0; index < SparseHeightSamples.Length; index++)
        {
            if (!TacviewTerrainDefinition.IsFinite(SparseHeightSamples[index]))
            {
                throw new ArgumentException("Terrain samples must be finite.", nameof(SparseHeightSamples));
            }
        }
    }

    private static void ValidatePoint(TacviewTerrainGeoPoint point, string name)
    {
        if (!TacviewTerrainDefinition.IsValidPoint(point))
        {
            throw new ArgumentOutOfRangeException(name, "Terrain coordinates must be finite WGS-84 longitude/latitude values.");
        }
    }
}
