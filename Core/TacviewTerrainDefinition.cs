using System;

namespace VTOLVRTacviewSPRecorder.Core;

public readonly struct TacviewTerrainGeoPoint : IEquatable<TacviewTerrainGeoPoint>
{
    public TacviewTerrainGeoPoint(double longitude, double latitude)
    {
        Longitude = longitude;
        Latitude = latitude;
    }

    public double Longitude { get; }

    public double Latitude { get; }

    public bool Equals(TacviewTerrainGeoPoint other)
    {
        return Longitude.Equals(other.Longitude) && Latitude.Equals(other.Latitude);
    }

    public override bool Equals(object obj)
    {
        return obj is TacviewTerrainGeoPoint other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return (Longitude.GetHashCode() * 397) ^ Latitude.GetHashCode();
        }
    }
}

public sealed class TacviewTerrainDefinition
{
    public TacviewTerrainDefinition(
        string terrainId,
        int width,
        int height,
        TacviewTerrainGeoPoint bottomLeft,
        TacviewTerrainGeoPoint bottomRight,
        TacviewTerrainGeoPoint topRight,
        TacviewTerrainGeoPoint topLeft)
    {
        if (!TacviewTerrainId.IsManaged(terrainId))
        {
            throw new ArgumentException("Terrain IDs must be safe recorder-managed VTOLVR IDs.", nameof(terrainId));
        }

        if (width < 2 || height < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Terrain dimensions must be at least 2x2.");
        }

        ValidatePoint(bottomLeft, nameof(bottomLeft));
        ValidatePoint(bottomRight, nameof(bottomRight));
        ValidatePoint(topRight, nameof(topRight));
        ValidatePoint(topLeft, nameof(topLeft));

        TerrainId = terrainId;
        Width = width;
        Height = height;
        BottomLeft = bottomLeft;
        BottomRight = bottomRight;
        TopRight = topRight;
        TopLeft = topLeft;
    }

    public string TerrainId { get; }

    public int Width { get; }

    public int Height { get; }

    public TacviewTerrainGeoPoint BottomLeft { get; }

    public TacviewTerrainGeoPoint BottomRight { get; }

    public TacviewTerrainGeoPoint TopRight { get; }

    public TacviewTerrainGeoPoint TopLeft { get; }

    public string RawFileName => TerrainId + ".raw";

    public string XmlFileName => TerrainId + ".xml";

    public string ChecksumFileName => TerrainId + ".sha256";

    public long ExpectedRawLength => checked((long)Width * Height * sizeof(short));

    internal static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    internal static bool IsValidPoint(TacviewTerrainGeoPoint point)
    {
        return IsFinite(point.Longitude) && IsFinite(point.Latitude) &&
            point.Longitude >= -180.0 && point.Longitude <= 180.0 &&
            point.Latitude >= -90.0 && point.Latitude <= 90.0;
    }

    private static void ValidatePoint(TacviewTerrainGeoPoint point, string parameterName)
    {
        if (!IsValidPoint(point))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Terrain coordinates must be finite WGS-84 longitude/latitude values.");
        }
    }
}
