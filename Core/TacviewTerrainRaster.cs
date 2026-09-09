using System;

namespace VTOLVRTacviewSPRecorder.Core;

public static class TacviewTerrainRaster
{
    // Tacview consumes image-style rows: west-to-east columns, north-to-south rows.
    // VTOL global +X is east and +Z is north, so row zero samples max Z.
    public static double NormalizedXForColumn(int column, int width)
    {
        ValidateIndex(column, width, nameof(column), nameof(width));
        return (double)column / (width - 1);
    }

    public static double NormalizedZForRawRow(int row, int height)
    {
        ValidateIndex(row, height, nameof(row), nameof(height));
        return 1.0 - ((double)row / (height - 1));
    }

    public static short QuantizeMeters(double heightMeters)
    {
        if (!TacviewTerrainDefinition.IsFinite(heightMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(heightMeters), "Terrain height must be finite.");
        }

        double rounded = Math.Round(heightMeters, MidpointRounding.AwayFromZero);
        if (rounded < short.MinValue || rounded > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heightMeters),
                "Terrain height does not fit Tacview's signed 16-bit meter range.");
        }

        return (short)rounded;
    }

    public static byte[] EncodeLittleEndian(short[] northToSouthRows, int width, int height)
    {
        if (northToSouthRows == null)
        {
            throw new ArgumentNullException(nameof(northToSouthRows));
        }

        int sampleCount = checked(width * height);
        if (width < 2 || height < 2 || northToSouthRows.Length != sampleCount)
        {
            throw new ArgumentException("The raster must contain exactly width * height samples.", nameof(northToSouthRows));
        }

        var result = new byte[checked(sampleCount * sizeof(short))];
        for (int index = 0; index < northToSouthRows.Length; index++)
        {
            ushort value = unchecked((ushort)northToSouthRows[index]);
            int byteIndex = index * 2;
            result[byteIndex] = (byte)(value & 0xff);
            result[byteIndex + 1] = (byte)(value >> 8);
        }

        return result;
    }

    private static void ValidateIndex(int index, int length, string indexName, string lengthName)
    {
        if (length < 2)
        {
            throw new ArgumentOutOfRangeException(lengthName, "Raster dimensions must be at least two samples.");
        }

        if (index < 0 || index >= length)
        {
            throw new ArgumentOutOfRangeException(indexName);
        }
    }
}
