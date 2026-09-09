using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// One point on a terrain mesh expressed in VTOL VR global coordinates.  The
/// altitude is already in the simulator's GPS altitude reference, rather than
/// Unity local Y, so this model remains independent of Unity and floating
/// origin state.
/// </summary>
public readonly struct TacviewMeshTerrainPoint
{
    public TacviewMeshTerrainPoint(double x, double z, double altitude)
    {
        if (!IsFinite(x) || !IsFinite(z) || !IsFinite(altitude))
        {
            throw new ArgumentOutOfRangeException(
                nameof(x),
                "Terrain mesh points must contain finite global coordinates and altitude.");
        }

        X = x;
        Z = z;
        Altitude = altitude;
    }

    public double X { get; }

    public double Z { get; }

    public double Altitude { get; }

    internal static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}

/// <summary>
/// A single transformed MeshCollider triangle.  Its X/Z projection must have
/// area: vertical faces cannot represent a terrain heightfield and are
/// rejected before any ACMI header can reference this terrain.
/// </summary>
public readonly struct TacviewMeshTerrainTriangle
{
    internal const double ProjectedAreaTolerance = 1e-7;
    internal const double CoordinateTolerance = 1e-7;

    public TacviewMeshTerrainTriangle(
        TacviewMeshTerrainPoint first,
        TacviewMeshTerrainPoint second,
        TacviewMeshTerrainPoint third)
    {
        First = first;
        Second = second;
        Third = third;

        double signedTwiceArea =
            ((second.X - first.X) * (third.Z - first.Z)) -
            ((third.X - first.X) * (second.Z - first.Z));
        if (Math.Abs(signedTwiceArea) <= ProjectedAreaTolerance)
        {
            throw new ArgumentException(
                "A terrain mesh triangle projects to zero X/Z area (vertical or degenerate face).",
                nameof(third));
        }

        SignedTwiceArea = signedTwiceArea;
        MinX = Math.Min(first.X, Math.Min(second.X, third.X));
        MinZ = Math.Min(first.Z, Math.Min(second.Z, third.Z));
        MaxX = Math.Max(first.X, Math.Max(second.X, third.X));
        MaxZ = Math.Max(first.Z, Math.Max(second.Z, third.Z));
    }

    public TacviewMeshTerrainPoint First { get; }

    public TacviewMeshTerrainPoint Second { get; }

    public TacviewMeshTerrainPoint Third { get; }

    public double MinX { get; }

    public double MinZ { get; }

    public double MaxX { get; }

    public double MaxZ { get; }

    internal double SignedTwiceArea { get; }

    internal bool TryInterpolate(
        double globalX,
        double globalZ,
        out double altitude,
        out bool strictlyInside)
    {
        altitude = 0.0;
        strictlyInside = false;

        if (globalX < MinX - CoordinateTolerance ||
            globalX > MaxX + CoordinateTolerance ||
            globalZ < MinZ - CoordinateTolerance ||
            globalZ > MaxZ + CoordinateTolerance)
        {
            return false;
        }

        double firstWeight =
            (((Second.X - globalX) * (Third.Z - globalZ)) -
             ((Third.X - globalX) * (Second.Z - globalZ))) /
            SignedTwiceArea;
        double secondWeight =
            (((Third.X - globalX) * (First.Z - globalZ)) -
             ((First.X - globalX) * (Third.Z - globalZ))) /
            SignedTwiceArea;
        double thirdWeight = 1.0 - firstWeight - secondWeight;

        if (firstWeight < -CoordinateTolerance ||
            secondWeight < -CoordinateTolerance ||
            thirdWeight < -CoordinateTolerance ||
            firstWeight > 1.0 + CoordinateTolerance ||
            secondWeight > 1.0 + CoordinateTolerance ||
            thirdWeight > 1.0 + CoordinateTolerance)
        {
            return false;
        }

        altitude =
            (firstWeight * First.Altitude) +
            (secondWeight * Second.Altitude) +
            (thirdWeight * Third.Altitude);
        strictlyInside = firstWeight > CoordinateTolerance &&
            secondWeight > CoordinateTolerance &&
            thirdWeight > CoordinateTolerance;
        return TacviewMeshTerrainPoint.IsFinite(altitude);
    }
}

/// <summary>
/// A bounded, deterministic spatial index over transformed terrain triangles.
/// It is deliberately a pure Core model: Unity meshes are snapshotted on the
/// main thread before this class is constructed, and no Unity data is touched
/// when a sample is interpolated.
/// </summary>
public sealed class TacviewMeshTerrainGrid
{
    private const double BoundsToleranceMeters = 0.01;
    private const double SeamAltitudeToleranceMeters = 0.05;

    private readonly IReadOnlyList<TacviewMeshTerrainTriangle> _triangles;
    // A fixed grid is denser and much smaller than a Dictionary<List<int>> for
    // the 1.15M-triangle Akutan source. Each cell owns a contiguous range in
    // _triangleIndexes; no duplicate per-cell list/array is retained.
    private readonly int[] _cellStarts;
    private readonly int[] _triangleIndexes;

    internal TacviewMeshTerrainGrid(
        IReadOnlyList<TacviewMeshTerrainTriangle> triangles,
        int[] cellStarts,
        int[] triangleIndexes,
        double minX,
        double minZ,
        double maxX,
        double maxZ,
        int sourceWidth,
        int sourceHeight,
        int exportWidth,
        int exportHeight,
        int indexColumns,
        int indexRows)
    {
        _triangles = triangles;
        _cellStarts = cellStarts;
        _triangleIndexes = triangleIndexes;
        MinX = minX;
        MinZ = minZ;
        MaxX = maxX;
        MaxZ = maxZ;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        ExportWidth = exportWidth;
        ExportHeight = exportHeight;
        IndexColumns = indexColumns;
        IndexRows = indexRows;
    }

    public IReadOnlyList<TacviewMeshTerrainTriangle> Triangles => _triangles;

    public double MinX { get; }

    public double MinZ { get; }

    public double MaxX { get; }

    public double MaxZ { get; }

    public double WidthMeters => MaxX - MinX;

    public double HeightMeters => MaxZ - MinZ;

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    public int ExportWidth { get; }

    public int ExportHeight { get; }

    public int IndexColumns { get; }

    public int IndexRows { get; }

    public int TriangleReferenceCount { get; internal set; }

    /// <summary>
    /// Samples a bounded rectangular probe lattice. This is used only during
    /// source preparation to detect a hole or multiple terrain surface before
    /// an export raster is allocated. The final export coordinates are still
    /// validated by <see cref="ValidateExportCoverage"/>.
    /// </summary>
    public void ValidateProbeCoverage(int samplesPerAxis)
    {
        if (samplesPerAxis < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(samplesPerAxis),
                "At least a 2x2 mesh terrain coverage probe is required.");
        }

        for (int row = 0; row < samplesPerAxis; row++)
        {
            double globalZ = Lerp(
                MinZ,
                MaxZ,
                TacviewTerrainRaster.NormalizedZForRawRow(row, samplesPerAxis));
            for (int column = 0; column < samplesPerAxis; column++)
            {
                double globalX = Lerp(
                    MinX,
                    MaxX,
                    TacviewTerrainRaster.NormalizedXForColumn(column, samplesPerAxis));
                if (!TrySample(globalX, globalZ, out _))
                {
                    throw new InvalidOperationException(
                        "The verified terrain mesh has a gap or ambiguous overlap in the bounded coverage probe at " +
                        "global X=" + globalX.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        ", Z=" + globalZ.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ".");
                }
            }
        }
    }

    /// <summary>
    /// Classifies a terrain sample at a global X/Z point. Shared triangle
    /// edges are resolved when their independently interpolated altitudes
    /// agree; overlapping surfaces and mismatched seams are ambiguous.
    /// </summary>
    public TacviewMeshTerrainSampleKind ClassifySample(
        double globalX,
        double globalZ,
        out double altitude)
    {
        altitude = 0.0;
        if (!TacviewMeshTerrainPoint.IsFinite(globalX) ||
            !TacviewMeshTerrainPoint.IsFinite(globalZ) ||
            globalX < MinX - BoundsToleranceMeters ||
            globalX > MaxX + BoundsToleranceMeters ||
            globalZ < MinZ - BoundsToleranceMeters ||
            globalZ > MaxZ + BoundsToleranceMeters)
        {
            return TacviewMeshTerrainSampleKind.Uncovered;
        }

        double clampedX = Clamp(globalX, MinX, MaxX);
        double clampedZ = Clamp(globalZ, MinZ, MaxZ);
        int column = CellFor(clampedX, MinX, MaxX, IndexColumns);
        int row = CellFor(clampedZ, MinZ, MaxZ, IndexRows);
        int cellIndex = checked((row * IndexColumns) + column);
        int candidateStart = _cellStarts[cellIndex];
        int candidateEnd = _cellStarts[cellIndex + 1];
        if (candidateStart >= candidateEnd)
        {
            return TacviewMeshTerrainSampleKind.Uncovered;
        }

        bool found = false;
        bool foundStrictInterior = false;
        double resolvedAltitude = 0.0;
        for (int candidateOffset = candidateStart; candidateOffset < candidateEnd; candidateOffset++)
        {
            TacviewMeshTerrainTriangle triangle = _triangles[_triangleIndexes[candidateOffset]];
            if (!triangle.TryInterpolate(clampedX, clampedZ, out double candidateAltitude, out bool strictlyInside))
            {
                continue;
            }

            if (!found)
            {
                found = true;
                foundStrictInterior = strictlyInside;
                resolvedAltitude = candidateAltitude;
                continue;
            }

            // More than one triangle with interior coverage is an overlapping
            // surface, even if a particular LOD happens to have equal heights.
            if (foundStrictInterior || strictlyInside ||
                Math.Abs(resolvedAltitude - candidateAltitude) > SeamAltitudeToleranceMeters)
            {
                return TacviewMeshTerrainSampleKind.Ambiguous;
            }
        }

        if (!found || !TacviewMeshTerrainPoint.IsFinite(resolvedAltitude))
        {
            return TacviewMeshTerrainSampleKind.Uncovered;
        }

        altitude = resolvedAltitude;
        return TacviewMeshTerrainSampleKind.Resolved;
    }

    /// <summary>
    /// Resolves exactly one terrain surface at a global X/Z point. Gaps,
    /// overhangs and LOD overlaps are all rejected rather than guessed.
    /// </summary>
    public bool TrySample(double globalX, double globalZ, out double altitude)
    {
        return ClassifySample(globalX, globalZ, out altitude) ==
            TacviewMeshTerrainSampleKind.Resolved;
    }

    /// <summary>
    /// Proves that every point which will be emitted to the fixed RAW raster
    /// resolves before a MapId can be created.  The caller supplies a strict
    /// cap to prevent an unexpectedly dense scene mesh from extending mission
    /// startup without bound.
    /// </summary>
    public void ValidateExportCoverage(int maximumValidationSamples)
    {
        if (maximumValidationSamples < 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumValidationSamples),
                "At least a 2x2 terrain validation budget is required.");
        }

        long sampleCount = (long)ExportWidth * ExportHeight;
        if (sampleCount > maximumValidationSamples)
        {
            throw new InvalidOperationException(
                "The mesh terrain export raster exceeds its validated startup sample cap.");
        }

        for (int row = 0; row < ExportHeight; row++)
        {
            double globalZ = GlobalZForRawRow(row);
            for (int column = 0; column < ExportWidth; column++)
            {
                double globalX = GlobalXForColumn(column);
                if (!TrySample(globalX, globalZ, out _))
                {
                    throw new InvalidOperationException(
                        "The verified terrain mesh leaves a gap or ambiguous overlap in the export raster at " +
                        "global X=" + globalX.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        ", Z=" + globalZ.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ".");
                }
            }
        }
    }

    public double GlobalXForColumn(int column)
    {
        return Lerp(MinX, MaxX, TacviewTerrainRaster.NormalizedXForColumn(column, ExportWidth));
    }

    public double GlobalZForRawRow(int row)
    {
        return Lerp(MinZ, MaxZ, TacviewTerrainRaster.NormalizedZForRawRow(row, ExportHeight));
    }

    internal static int CellFor(double value, double minimum, double maximum, int count)
    {
        if (count < 1 || maximum <= minimum)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        double normalized = (value - minimum) / (maximum - minimum);
        int cell = (int)Math.Floor(normalized * count);
        if (cell < 0)
        {
            return 0;
        }

        return cell >= count ? count - 1 : cell;
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return value < minimum ? minimum : value > maximum ? maximum : value;
    }

    private static double Lerp(double minimum, double maximum, double normalized)
    {
        return minimum + ((maximum - minimum) * normalized);
    }
}

/// <summary>
/// Creates a finite triangle heightfield index.  The limits are deliberately
/// explicit: a corrupted or unrelated MeshCollider cannot allocate an
/// unbounded index or make source discovery scan arbitrary geometry.
/// </summary>
public static class TacviewMeshTerrainGridBuilder
{
    // The installed Akutan MeshTerrain currently contains 1,146,879 triangles
    // across its 35 verified readable MeshCollider bindings. Keep a small explicit
    // margin for a compatible authored tile, but reject arbitrary scene meshes
    // before their snapshot can grow without bound.
    public const int DefaultMaximumTriangleCount = 1200000;
    public const int DefaultMaximumSpatialCellCount = 65536;
    public const int DefaultMaximumSpatialReferenceCount = 4000000;
    public const int MinimumExportDimension = 65;

    public static TacviewMeshTerrainGrid Build(
        IEnumerable<TacviewMeshTerrainTriangle> triangles,
        int maximumExportDimension,
        int maximumTriangleCount = DefaultMaximumTriangleCount,
        int maximumSpatialCellCount = DefaultMaximumSpatialCellCount,
        int maximumSpatialReferenceCount = DefaultMaximumSpatialReferenceCount)
    {
        if (triangles == null)
        {
            throw new ArgumentNullException(nameof(triangles));
        }

        var owned = new List<TacviewMeshTerrainTriangle>();
        foreach (TacviewMeshTerrainTriangle triangle in triangles)
        {
            if (owned.Count >= maximumTriangleCount)
            {
                throw new InvalidOperationException(
                    "The verified terrain mesh exceeds the triangle snapshot cap.");
            }

            owned.Add(triangle);
        }

        return BuildOwned(
            owned,
            maximumExportDimension,
            maximumTriangleCount,
            maximumSpatialCellCount,
            maximumSpatialReferenceCount);
    }

    /// <summary>
    /// Builds the index while taking ownership of a completed triangle
    /// snapshot. This avoids a second 100+ MB triangle copy for the verified
    /// Akutan runtime source. Callers must not mutate the list afterwards.
    /// </summary>
    internal static TacviewMeshTerrainGrid BuildOwned(
        List<TacviewMeshTerrainTriangle> triangles,
        int maximumExportDimension,
        int maximumTriangleCount = DefaultMaximumTriangleCount,
        int maximumSpatialCellCount = DefaultMaximumSpatialCellCount,
        int maximumSpatialReferenceCount = DefaultMaximumSpatialReferenceCount)
    {
        if (triangles == null)
        {
            throw new ArgumentNullException(nameof(triangles));
        }

        if (maximumExportDimension < 2 ||
            maximumTriangleCount < 1 ||
            maximumSpatialCellCount < 1 ||
            maximumSpatialReferenceCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumExportDimension),
                "Mesh terrain index limits must all be positive and the export dimension at least two.");
        }

        if (triangles.Count == 0)
        {
            throw new ArgumentException("At least one terrain mesh triangle is required.", nameof(triangles));
        }

        if (triangles.Count > maximumTriangleCount)
        {
            throw new InvalidOperationException(
                "The verified terrain mesh exceeds the triangle snapshot cap.");
        }

        triangles.Sort(CompareTriangles);
        double minX = triangles[0].MinX;
        double minZ = triangles[0].MinZ;
        double maxX = triangles[0].MaxX;
        double maxZ = triangles[0].MaxZ;
        for (int index = 1; index < triangles.Count; index++)
        {
            TacviewMeshTerrainTriangle triangle = triangles[index];
            minX = Math.Min(minX, triangle.MinX);
            minZ = Math.Min(minZ, triangle.MinZ);
            maxX = Math.Max(maxX, triangle.MaxX);
            maxZ = Math.Max(maxZ, triangle.MaxZ);
        }

        if (!TacviewMeshTerrainPoint.IsFinite(minX) ||
            !TacviewMeshTerrainPoint.IsFinite(minZ) ||
            !TacviewMeshTerrainPoint.IsFinite(maxX) ||
            !TacviewMeshTerrainPoint.IsFinite(maxZ) ||
            maxX - minX <= TacviewMeshTerrainTriangle.CoordinateTolerance ||
            maxZ - minZ <= TacviewMeshTerrainTriangle.CoordinateTolerance)
        {
            throw new ArgumentException(
                "Terrain mesh bounds must form a finite non-zero global X/Z rectangle.",
                nameof(triangles));
        }

        CalculateSpatialDimensions(
            triangles.Count,
            maxX - minX,
            maxZ - minZ,
            maximumSpatialCellCount,
            out int indexColumns,
            out int indexRows);
        MeshSpatialIndex spatialIndex = BuildSpatialIndex(
            triangles,
            minX,
            minZ,
            maxX,
            maxZ,
            indexColumns,
            indexRows,
            maximumSpatialReferenceCount,
            out int referenceCount);

        CalculateRasterDimensions(
            triangles.Count,
            maxX - minX,
            maxZ - minZ,
            maximumExportDimension,
            out int sourceWidth,
            out int sourceHeight,
            out int exportWidth,
            out int exportHeight);

        var grid = new TacviewMeshTerrainGrid(
            new ReadOnlyCollection<TacviewMeshTerrainTriangle>(triangles),
            spatialIndex.CellStarts,
            spatialIndex.TriangleIndexes,
            minX,
            minZ,
            maxX,
            maxZ,
            sourceWidth,
            sourceHeight,
            exportWidth,
            exportHeight,
            indexColumns,
            indexRows)
        {
            TriangleReferenceCount = referenceCount
        };
        return grid;
    }

    private static MeshSpatialIndex BuildSpatialIndex(
        IReadOnlyList<TacviewMeshTerrainTriangle> triangles,
        double minX,
        double minZ,
        double maxX,
        double maxZ,
        int columns,
        int rows,
        int maximumReferences,
        out int referenceCount)
    {
        int cellCount = checked(columns * rows);
        var counts = new int[cellCount];
        long references = CountSpatialReferences(
            triangles,
            minX,
            minZ,
            maxX,
            maxZ,
            columns,
            rows,
            counts,
            maximumReferences);
        var starts = new int[cellCount + 1];
        for (int cellIndex = 0; cellIndex < counts.Length; cellIndex++)
        {
            starts[cellIndex + 1] = checked(starts[cellIndex] + counts[cellIndex]);
        }

        var writePositions = new int[cellCount];
        Array.Copy(starts, writePositions, cellCount);
        var triangleIndexes = new int[checked((int)references)];
        for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
        {
            TacviewMeshTerrainTriangle triangle = triangles[triangleIndex];
            int minColumn = TacviewMeshTerrainGrid.CellFor(triangle.MinX, minX, maxX, columns);
            int maxColumn = TacviewMeshTerrainGrid.CellFor(triangle.MaxX, minX, maxX, columns);
            int minRow = TacviewMeshTerrainGrid.CellFor(triangle.MinZ, minZ, maxZ, rows);
            int maxRow = TacviewMeshTerrainGrid.CellFor(triangle.MaxZ, minZ, maxZ, rows);

            for (int row = minRow; row <= maxRow; row++)
            {
                for (int column = minColumn; column <= maxColumn; column++)
                {
                    int cellIndex = checked((row * columns) + column);
                    triangleIndexes[writePositions[cellIndex]++] = triangleIndex;
                }
            }
        }

        if (references > int.MaxValue)
        {
            throw new InvalidOperationException("The terrain mesh spatial index exceeds supported bookkeeping limits.");
        }

        referenceCount = (int)references;
        return new MeshSpatialIndex(starts, triangleIndexes);
    }

    private static long CountSpatialReferences(
        IReadOnlyList<TacviewMeshTerrainTriangle> triangles,
        double minX,
        double minZ,
        double maxX,
        double maxZ,
        int columns,
        int rows,
        int[] counts,
        int maximumReferences)
    {
        long references = 0;
        for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
        {
            TacviewMeshTerrainTriangle triangle = triangles[triangleIndex];
            int minColumn = TacviewMeshTerrainGrid.CellFor(triangle.MinX, minX, maxX, columns);
            int maxColumn = TacviewMeshTerrainGrid.CellFor(triangle.MaxX, minX, maxX, columns);
            int minRow = TacviewMeshTerrainGrid.CellFor(triangle.MinZ, minZ, maxZ, rows);
            int maxRow = TacviewMeshTerrainGrid.CellFor(triangle.MaxZ, minZ, maxZ, rows);

            for (int row = minRow; row <= maxRow; row++)
            {
                for (int column = minColumn; column <= maxColumn; column++)
                {
                    references++;
                    if (references > maximumReferences || references > int.MaxValue)
                    {
                        throw new InvalidOperationException(
                            "The terrain mesh spatial index exceeds its triangle-reference cap.");
                    }

                    int cellIndex = checked((row * columns) + column);
                    counts[cellIndex] = checked(counts[cellIndex] + 1);
                }
            }
        }

        return references;
    }

    private readonly struct MeshSpatialIndex
    {
        public MeshSpatialIndex(int[] cellStarts, int[] triangleIndexes)
        {
            CellStarts = cellStarts;
            TriangleIndexes = triangleIndexes;
        }

        public int[] CellStarts { get; }

        public int[] TriangleIndexes { get; }
    }

    private static void CalculateSpatialDimensions(
        int triangleCount,
        double width,
        double height,
        int maximumCellCount,
        out int columns,
        out int rows)
    {
        // Aim for roughly eight triangles per cell. This keeps point queries
        // short without creating an enormous dictionary for dense meshes.
        int requestedCells = Math.Max(1, (triangleCount + 7) / 8);
        requestedCells = Math.Min(requestedCells, maximumCellCount);
        double aspect = width / height;
        double root = Math.Sqrt(requestedCells);
        columns = Math.Max(1, (int)Math.Ceiling(root * Math.Sqrt(aspect)));
        rows = Math.Max(1, (int)Math.Ceiling(root / Math.Sqrt(aspect)));

        while ((long)columns * rows > maximumCellCount)
        {
            if (columns >= rows && columns > 1)
            {
                columns--;
            }
            else if (rows > 1)
            {
                rows--;
            }
            else
            {
                break;
            }
        }
    }

    private static void CalculateRasterDimensions(
        int triangleCount,
        double width,
        double height,
        int maximumExportDimension,
        out int sourceWidth,
        out int sourceHeight,
        out int exportWidth,
        out int exportHeight)
    {
        double aspect = width / height;
        double samplesPerAxis = Math.Sqrt(Math.Max(1.0, triangleCount / 2.0));
        sourceWidth = BoundedDimension(samplesPerAxis * Math.Sqrt(aspect));
        sourceHeight = BoundedDimension(samplesPerAxis / Math.Sqrt(aspect));
        exportWidth = Math.Min(sourceWidth, maximumExportDimension);
        exportHeight = Math.Min(sourceHeight, maximumExportDimension);
    }

    private static int BoundedDimension(double cellsPerAxis)
    {
        double requested = Math.Ceiling(cellsPerAxis) + 1.0;
        requested = Math.Max(MinimumExportDimension, requested);
        requested = Math.Min(4097.0, requested);
        return (int)requested;
    }

    private static int CompareTriangles(
        TacviewMeshTerrainTriangle left,
        TacviewMeshTerrainTriangle right)
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

        comparison = left.First.Altitude.CompareTo(right.First.Altitude);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Second.Altitude.CompareTo(right.Second.Altitude);
        return comparison != 0
            ? comparison
            : left.Third.Altitude.CompareTo(right.Third.Altitude);
    }
}
public enum TacviewMeshTerrainSampleKind
{
    Resolved,
    Uncovered,
    Ambiguous
}
