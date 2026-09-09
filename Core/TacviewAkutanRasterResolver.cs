using System;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// Immutable north-to-south RAW raster resolved from the reviewed Akutan mesh
/// family. Only <see cref="TacviewAkutanRasterResolver"/> may create one.
/// </summary>
public sealed class TacviewAkutanRaster
{
    private readonly double[] _altitudes;

    internal TacviewAkutanRaster(int width, int height, double[] altitudes)
    {
        Width = width;
        Height = height;
        _altitudes = altitudes ?? throw new ArgumentNullException(nameof(altitudes));
    }

    public int Width { get; }

    public int Height { get; }

    public double GetAltitude(int row, int column)
    {
        if (row < 0 || row >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        if (column < 0 || column >= Width)
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }

        return _altitudes[checked((row * Width) + column)];
    }
}

/// <summary>
/// Applies the evidence-backed Akutan ocean policy to one fixed mesh lattice.
/// It is intentionally not a generic mesh fallback: every uncovered
/// four-neighbour component must reach an outer raster edge, and ambiguous
/// geometry always fails closed.
/// </summary>
public static class TacviewAkutanRasterResolver
{
    public const int ProductionDimension = 508;
    public const int ProductionMaximumSampleCount =
        ProductionDimension * ProductionDimension;

    public static TacviewAkutanRaster ResolveProduction(TacviewMeshTerrainGrid grid)
    {
        if (grid == null)
        {
            throw new ArgumentNullException(nameof(grid));
        }

        if (grid.ExportWidth != ProductionDimension ||
            grid.ExportHeight != ProductionDimension)
        {
            throw new InvalidOperationException(
                "The reviewed Akutan resolver requires an exact 508x508 export lattice.");
        }

        return Resolve(
            grid,
            ProductionDimension,
            ProductionDimension,
            ProductionMaximumSampleCount);
    }

    /// <summary>
    /// Bounded overload used by pure Core topology tests. Runtime Akutan code
    /// must call <see cref="ResolveProduction"/> so its dimension cannot drift.
    /// </summary>
    public static TacviewAkutanRaster Resolve(
        TacviewMeshTerrainGrid grid,
        int width,
        int height,
        int maximumSampleCount)
    {
        if (grid == null)
        {
            throw new ArgumentNullException(nameof(grid));
        }

        if (width < 2 || height < 2 || maximumSampleCount < 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "A bounded raster of at least 2x2 samples is required.");
        }

        long sampleCountLong = (long)width * height;
        if (sampleCountLong > maximumSampleCount ||
            sampleCountLong > ProductionMaximumSampleCount ||
            sampleCountLong > int.MaxValue)
        {
            throw new InvalidOperationException(
                "The Akutan terrain raster exceeds its caller or absolute 508x508 sample cap.");
        }

        int sampleCount = (int)sampleCountLong;
        var altitudes = new double[sampleCount];
        var uncovered = new bool[sampleCount];
        for (int row = 0; row < height; row++)
        {
            double globalZ = Lerp(
                grid.MinZ,
                grid.MaxZ,
                TacviewTerrainRaster.NormalizedZForRawRow(row, height));
            int rowOffset = checked(row * width);
            for (int column = 0; column < width; column++)
            {
                double globalX = Lerp(
                    grid.MinX,
                    grid.MaxX,
                    TacviewTerrainRaster.NormalizedXForColumn(column, width));
                int sampleIndex = rowOffset + column;
                TacviewMeshTerrainSampleKind kind =
                    grid.ClassifySample(globalX, globalZ, out double altitude);
                if (kind == TacviewMeshTerrainSampleKind.Ambiguous)
                {
                    throw new InvalidOperationException(
                        "The verified Akutan mesh is ambiguous at raster row " + row +
                        ", column " + column + ".");
                }

                if (kind == TacviewMeshTerrainSampleKind.Uncovered)
                {
                    uncovered[sampleIndex] = true;
                    continue;
                }

                if (!TacviewMeshTerrainPoint.IsFinite(altitude))
                {
                    throw new InvalidOperationException(
                        "The verified Akutan mesh produced a non-finite raster altitude.");
                }

                altitudes[sampleIndex] = altitude;
            }
        }

        ResolveBoundaryOceanComponents(uncovered, altitudes, width, height);
        return new TacviewAkutanRaster(width, height, altitudes);
    }

    private static void ResolveBoundaryOceanComponents(
        bool[] uncovered,
        double[] altitudes,
        int width,
        int height)
    {
        var visited = new bool[uncovered.Length];
        var queue = new int[uncovered.Length];
        for (int start = 0; start < uncovered.Length; start++)
        {
            if (!uncovered[start] || visited[start])
            {
                continue;
            }

            int head = 0;
            int tail = 0;
            bool touchesBoundary = false;
            visited[start] = true;
            queue[tail++] = start;
            while (head < tail)
            {
                int sampleIndex = queue[head++];
                int row = sampleIndex / width;
                int column = sampleIndex - (row * width);
                if (row == 0 || row == height - 1 ||
                    column == 0 || column == width - 1)
                {
                    touchesBoundary = true;
                }

                Enqueue(row - 1, column, width, height, uncovered, visited, queue, ref tail);
                Enqueue(row + 1, column, width, height, uncovered, visited, queue, ref tail);
                Enqueue(row, column - 1, width, height, uncovered, visited, queue, ref tail);
                Enqueue(row, column + 1, width, height, uncovered, visited, queue, ref tail);
            }

            if (!touchesBoundary)
            {
                throw new InvalidOperationException(
                    "The verified Akutan mesh contains an enclosed uncovered raster component.");
            }

            // VTMapManager GPS altitude is relative to WaterPhysics.height;
            // reviewed boundary-connected Akutan ocean is exactly 0 m MSL.
            for (int queueIndex = 0; queueIndex < tail; queueIndex++)
            {
                altitudes[queue[queueIndex]] = 0.0;
            }
        }
    }

    private static void Enqueue(
        int row,
        int column,
        int width,
        int height,
        bool[] uncovered,
        bool[] visited,
        int[] queue,
        ref int tail)
    {
        if (row < 0 || row >= height || column < 0 || column >= width)
        {
            return;
        }

        int sampleIndex = checked((row * width) + column);
        if (!uncovered[sampleIndex] || visited[sampleIndex])
        {
            return;
        }

        visited[sampleIndex] = true;
        queue[tail++] = sampleIndex;
    }

    private static double Lerp(double minimum, double maximum, double normalized)
    {
        return minimum + ((maximum - minimum) * normalized);
    }
}
