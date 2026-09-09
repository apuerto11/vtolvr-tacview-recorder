using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// A horizontal point expressed in VTOL VR global X/Z metres. It deliberately
/// contains no Unity type so static-terrain layout decisions are testable away
/// from the game runtime.
/// </summary>
public readonly struct TacviewTerrainPlanarPoint
{
    public TacviewTerrainPlanarPoint(double x, double z)
    {
        X = x;
        Z = z;
    }

    public double X { get; }

    public double Z { get; }
}

/// <summary>
/// One authoritative terrain-data tile projected into the simulator's global
/// X/Z plane. The four supplied corners must form an axis-aligned rectangle;
/// arbitrary mesh terrain, tilted terrain, and ambiguous shapes are rejected
/// by the runtime before reaching this pure model.
/// </summary>
public sealed class TacviewTerrainTile
{
    internal const double CoordinateToleranceMeters = 0.05;

    public TacviewTerrainTile(
        string key,
        int sourceWidth,
        int sourceHeight,
        TacviewTerrainPlanarPoint corner0,
        TacviewTerrainPlanarPoint corner1,
        TacviewTerrainPlanarPoint corner2,
        TacviewTerrainPlanarPoint corner3)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A deterministic terrain tile key is required.", nameof(key));
        }

        if (sourceWidth < 2 || sourceHeight < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceWidth),
                "Terrain source dimensions must be at least 2x2.");
        }

        TacviewTerrainPlanarPoint[] corners =
        {
            corner0,
            corner1,
            corner2,
            corner3
        };
        for (int index = 0; index < corners.Length; index++)
        {
            if (!IsFinite(corners[index].X) || !IsFinite(corners[index].Z))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(corner0),
                    "Terrain tile corners must be finite global coordinates.");
            }
        }

        double minX = corners[0].X;
        double maxX = corners[0].X;
        double minZ = corners[0].Z;
        double maxZ = corners[0].Z;
        for (int index = 1; index < corners.Length; index++)
        {
            minX = Math.Min(minX, corners[index].X);
            maxX = Math.Max(maxX, corners[index].X);
            minZ = Math.Min(minZ, corners[index].Z);
            maxZ = Math.Max(maxZ, corners[index].Z);
        }

        if (maxX - minX <= CoordinateToleranceMeters ||
            maxZ - minZ <= CoordinateToleranceMeters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(corner0),
                "Terrain tile bounds must have positive horizontal extent.");
        }

        ValidateRectangleCorners(corners, minX, minZ, maxX, maxZ);

        Key = key.Trim();
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        MinX = minX;
        MinZ = minZ;
        MaxX = maxX;
        MaxZ = maxZ;
    }

    /// <summary>
    /// Stable identity supplied by the runtime. It is used only to associate
    /// this pure tile with its concrete Terrain/TerrainData source.
    /// </summary>
    public string Key { get; }

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    public double MinX { get; }

    public double MinZ { get; }

    public double MaxX { get; }

    public double MaxZ { get; }

    public double WidthMeters => MaxX - MinX;

    public double HeightMeters => MaxZ - MinZ;

    public bool Contains(double globalX, double globalZ)
    {
        return IsFinite(globalX) && IsFinite(globalZ) &&
            globalX >= MinX - CoordinateToleranceMeters &&
            globalX <= MaxX + CoordinateToleranceMeters &&
            globalZ >= MinZ - CoordinateToleranceMeters &&
            globalZ <= MaxZ + CoordinateToleranceMeters;
    }

    private static void ValidateRectangleCorners(
        IReadOnlyList<TacviewTerrainPlanarPoint> corners,
        double minX,
        double minZ,
        double maxX,
        double maxZ)
    {
        TacviewTerrainPlanarPoint[] expected =
        {
            new TacviewTerrainPlanarPoint(minX, minZ),
            new TacviewTerrainPlanarPoint(maxX, minZ),
            new TacviewTerrainPlanarPoint(maxX, maxZ),
            new TacviewTerrainPlanarPoint(minX, maxZ)
        };
        var consumed = new bool[expected.Length];

        for (int cornerIndex = 0; cornerIndex < corners.Count; cornerIndex++)
        {
            bool matched = false;
            for (int expectedIndex = 0; expectedIndex < expected.Length; expectedIndex++)
            {
                if (consumed[expectedIndex] ||
                    !NearlyEqual(corners[cornerIndex].X, expected[expectedIndex].X) ||
                    !NearlyEqual(corners[cornerIndex].Z, expected[expectedIndex].Z))
                {
                    continue;
                }

                consumed[expectedIndex] = true;
                matched = true;
                break;
            }

            if (!matched)
            {
                throw new ArgumentException(
                    "Terrain tile corners must describe one axis-aligned rectangle.",
                    nameof(corners));
            }
        }
    }

    internal static bool NearlyEqual(double left, double right)
    {
        return Math.Abs(left - right) <= CoordinateToleranceMeters;
    }

    internal static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}

/// <summary>
/// A validated rectangular union of one or more static terrain-data tiles.
/// Its sample dimensions preserve the densest source resolution until the
/// recorder's explicit export cap applies.
/// </summary>
public sealed class TacviewTerrainGrid
{
    private readonly IReadOnlyList<TacviewTerrainTile> _tiles;

    internal TacviewTerrainGrid(
        IReadOnlyList<TacviewTerrainTile> tiles,
        double minX,
        double minZ,
        double maxX,
        double maxZ,
        int sourceWidth,
        int sourceHeight,
        int exportWidth,
        int exportHeight)
    {
        _tiles = tiles;
        MinX = minX;
        MinZ = minZ;
        MaxX = maxX;
        MaxZ = maxZ;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        ExportWidth = exportWidth;
        ExportHeight = exportHeight;
    }

    public IReadOnlyList<TacviewTerrainTile> Tiles => _tiles;

    public double MinX { get; }

    public double MinZ { get; }

    public double MaxX { get; }

    public double MaxZ { get; }

    public double WidthMeters => MaxX - MinX;

    public double HeightMeters => MaxZ - MinZ;

    /// <summary>
    /// The uncapped source-equivalent samples spanning the complete grid.
    /// These are fingerprint metadata, not an allocation request.
    /// </summary>
    public int SourceWidth { get; }

    public int SourceHeight { get; }

    public int ExportWidth { get; }

    public int ExportHeight { get; }

    /// <summary>
    /// Selects one tile deterministically. Interior overlap and gaps are
    /// rejected by the builder, while shared edge samples use sorted tile order.
    /// </summary>
    public bool TryGetTile(double globalX, double globalZ, out TacviewTerrainTile tile)
    {
        tile = null;
        if (!TacviewTerrainTile.IsFinite(globalX) || !TacviewTerrainTile.IsFinite(globalZ) ||
            globalX < MinX - TacviewTerrainTile.CoordinateToleranceMeters ||
            globalX > MaxX + TacviewTerrainTile.CoordinateToleranceMeters ||
            globalZ < MinZ - TacviewTerrainTile.CoordinateToleranceMeters ||
            globalZ > MaxZ + TacviewTerrainTile.CoordinateToleranceMeters)
        {
            return false;
        }

        for (int index = 0; index < _tiles.Count; index++)
        {
            TacviewTerrainTile candidate = _tiles[index];
            if (!candidate.Contains(globalX, globalZ))
            {
                continue;
            }

            tile = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the global X coordinate for a west-to-east RAW column.
    /// </summary>
    public double GlobalXForColumn(int column)
    {
        return Lerp(MinX, MaxX, TacviewTerrainRaster.NormalizedXForColumn(column, ExportWidth));
    }

    /// <summary>
    /// Returns the global Z coordinate for a north-to-south RAW row.
    /// </summary>
    public double GlobalZForRawRow(int row)
    {
        return Lerp(MinZ, MaxZ, TacviewTerrainRaster.NormalizedZForRawRow(row, ExportHeight));
    }

    private static double Lerp(double minimum, double maximum, double normalized)
    {
        return minimum + ((maximum - minimum) * normalized);
    }
}

/// <summary>
/// Builds a fail-closed union from static Unity Terrain bounds. Every cell in
/// the resulting rectangular envelope must be covered by exactly one tile.
/// </summary>
public static class TacviewTerrainGridBuilder
{
    public static TacviewTerrainGrid Build(
        IEnumerable<TacviewTerrainTile> tiles,
        int maximumExportDimension)
    {
        if (tiles == null)
        {
            throw new ArgumentNullException(nameof(tiles));
        }

        if (maximumExportDimension < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumExportDimension),
                "The maximum terrain export dimension must be at least two.");
        }

        var ordered = new List<TacviewTerrainTile>();
        foreach (TacviewTerrainTile tile in tiles)
        {
            if (tile == null)
            {
                throw new ArgumentException("Terrain tile collections cannot contain null.", nameof(tiles));
            }

            ordered.Add(tile);
        }

        if (ordered.Count == 0)
        {
            throw new ArgumentException("At least one terrain tile is required.", nameof(tiles));
        }

        ordered.Sort(CompareTiles);
        for (int index = 1; index < ordered.Count; index++)
        {
            if (string.Equals(ordered[index - 1].Key, ordered[index].Key, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Terrain tile keys must be unique for deterministic sampling.",
                    nameof(tiles));
            }
        }

        double minX = ordered[0].MinX;
        double minZ = ordered[0].MinZ;
        double maxX = ordered[0].MaxX;
        double maxZ = ordered[0].MaxZ;
        double maximumSamplesPerMeterX = 0.0;
        double maximumSamplesPerMeterZ = 0.0;
        var xBoundaries = new List<double>(ordered.Count * 2);
        var zBoundaries = new List<double>(ordered.Count * 2);

        for (int index = 0; index < ordered.Count; index++)
        {
            TacviewTerrainTile tile = ordered[index];
            minX = Math.Min(minX, tile.MinX);
            minZ = Math.Min(minZ, tile.MinZ);
            maxX = Math.Max(maxX, tile.MaxX);
            maxZ = Math.Max(maxZ, tile.MaxZ);
            maximumSamplesPerMeterX = Math.Max(
                maximumSamplesPerMeterX,
                (tile.SourceWidth - 1.0) / tile.WidthMeters);
            maximumSamplesPerMeterZ = Math.Max(
                maximumSamplesPerMeterZ,
                (tile.SourceHeight - 1.0) / tile.HeightMeters);
            AddBoundary(xBoundaries, tile.MinX);
            AddBoundary(xBoundaries, tile.MaxX);
            AddBoundary(zBoundaries, tile.MinZ);
            AddBoundary(zBoundaries, tile.MaxZ);
        }

        xBoundaries.Sort();
        zBoundaries.Sort();
        ValidateRectangularCoverage(ordered, xBoundaries, zBoundaries);

        int sourceWidth = CalculateSampleCount(maxX - minX, maximumSamplesPerMeterX);
        int sourceHeight = CalculateSampleCount(maxZ - minZ, maximumSamplesPerMeterZ);
        int exportWidth = Math.Min(sourceWidth, maximumExportDimension);
        int exportHeight = Math.Min(sourceHeight, maximumExportDimension);

        return new TacviewTerrainGrid(
            new ReadOnlyCollection<TacviewTerrainTile>(ordered),
            minX,
            minZ,
            maxX,
            maxZ,
            sourceWidth,
            sourceHeight,
            exportWidth,
            exportHeight);
    }

    private static void ValidateRectangularCoverage(
        IReadOnlyList<TacviewTerrainTile> tiles,
        IReadOnlyList<double> xBoundaries,
        IReadOnlyList<double> zBoundaries)
    {
        for (int zIndex = 0; zIndex < zBoundaries.Count - 1; zIndex++)
        {
            double minZ = zBoundaries[zIndex];
            double maxZ = zBoundaries[zIndex + 1];
            if (maxZ - minZ <= TacviewTerrainTile.CoordinateToleranceMeters)
            {
                continue;
            }

            double midpointZ = minZ + ((maxZ - minZ) * 0.5);
            for (int xIndex = 0; xIndex < xBoundaries.Count - 1; xIndex++)
            {
                double minX = xBoundaries[xIndex];
                double maxX = xBoundaries[xIndex + 1];
                if (maxX - minX <= TacviewTerrainTile.CoordinateToleranceMeters)
                {
                    continue;
                }

                double midpointX = minX + ((maxX - minX) * 0.5);
                int coveredBy = 0;
                for (int tileIndex = 0; tileIndex < tiles.Count; tileIndex++)
                {
                    TacviewTerrainTile tile = tiles[tileIndex];
                    if (midpointX > tile.MinX + TacviewTerrainTile.CoordinateToleranceMeters &&
                        midpointX < tile.MaxX - TacviewTerrainTile.CoordinateToleranceMeters &&
                        midpointZ > tile.MinZ + TacviewTerrainTile.CoordinateToleranceMeters &&
                        midpointZ < tile.MaxZ - TacviewTerrainTile.CoordinateToleranceMeters)
                    {
                        coveredBy++;
                    }
                }

                if (coveredBy != 1)
                {
                    throw new ArgumentException(
                        coveredBy == 0
                            ? "Static terrain tiles leave a gap inside their rectangular union."
                            : "Static terrain tiles overlap inside their rectangular union.",
                        nameof(tiles));
                }
            }
        }
    }

    private static int CalculateSampleCount(double extentMeters, double samplesPerMeter)
    {
        if (!TacviewTerrainTile.IsFinite(extentMeters) || extentMeters <= 0.0 ||
            !TacviewTerrainTile.IsFinite(samplesPerMeter) || samplesPerMeter <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(extentMeters));
        }

        double requested = Math.Ceiling((extentMeters * samplesPerMeter) - 1e-9) + 1.0;
        if (requested < 2.0 || requested > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(extentMeters),
                "Static terrain source resolution is outside the supported range.");
        }

        return (int)requested;
    }

    private static void AddBoundary(ICollection<double> boundaries, double candidate)
    {
        foreach (double existing in boundaries)
        {
            if (TacviewTerrainTile.NearlyEqual(existing, candidate))
            {
                return;
            }
        }

        boundaries.Add(candidate);
    }

    private static int CompareTiles(TacviewTerrainTile left, TacviewTerrainTile right)
    {
        int comparison = left.MinZ.CompareTo(right.MinZ);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.MinX.CompareTo(right.MinX);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.MaxZ.CompareTo(right.MaxZ);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.MaxX.CompareTo(right.MaxX);
        if (comparison != 0)
        {
            return comparison;
        }

        return string.Compare(left.Key, right.Key, StringComparison.Ordinal);
    }
}
