using System;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// Chooses the catalog lookup name for a sea actor without making text-based
/// vessel guesses. AISeaUnitSpawn exposes the original prefab actor name;
/// runtime actor labels may carry scenario designations such as "Carrier 61"
/// and must therefore be a lower-priority fallback only.
/// </summary>
public static class VtolSeaPlatformNameResolver
{
    public static string Resolve(
        string canonicalPrefabActorName,
        string runtimeActorName,
        string spawnedUnitDisplayName,
        string unitName)
    {
        return FirstNonEmpty(
            canonicalPrefabActorName,
            runtimeActorName,
            spawnedUnitDisplayName,
            unitName);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        for (int index = 0; index < values.Length; index++)
        {
            string value = values[index];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
