using System;
using System.IO;

namespace VTOLVRTacviewSPRecorder.Core;

public enum TacviewAircraftDatabaseStatus
{
    Installed,
    Updated,
    Current
}

/// <summary>Installs only the recorder-owned database file from the DLL resource.</summary>
public static class TacviewAircraftDatabase
{
    public const string FileName = "Tacview.VTOLVR.PlayerAircraft.xml";
    public const string ResourceName = "VTOLVRTacviewSPRecorder.Tacview.PlayerAircraft.xml";

    public static TacviewAircraftDatabaseStatus EnsureInstalled(string applicationDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(applicationDataDirectory) ||
            !Path.IsPathRooted(applicationDataDirectory))
        {
            throw new ArgumentException("An absolute application-data directory is required.",
                nameof(applicationDataDirectory));
        }

        byte[] expected;
        using (Stream resource = typeof(TacviewAircraftDatabase).Assembly.GetManifestResourceStream(ResourceName))
        {
            if (resource == null)
                throw new InvalidOperationException("The bundled Tacview aircraft database is missing.");
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            expected = buffer.ToArray();
        }

        string directory = Path.Combine(applicationDataDirectory, "Tacview", "Data", "Database", "Default Properties");
        string target = Path.Combine(directory, FileName);
        bool exists = File.Exists(target);
        if (exists && BytesEqual(File.ReadAllBytes(target), expected))
            return TacviewAircraftDatabaseStatus.Current;

        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, expected);
            // The replacement is atomic and never edits Tacview's stock or other custom definitions.
            if (exists)
                File.Replace(temporary, target, null);
            else
                File.Move(temporary, target);

            if (!BytesEqual(File.ReadAllBytes(target), expected))
                throw new IOException("Tacview aircraft database verification failed.");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        return exists ? TacviewAircraftDatabaseStatus.Updated : TacviewAircraftDatabaseStatus.Installed;
    }

    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
            if (left[index] != right[index]) return false;
        return true;
    }
}
