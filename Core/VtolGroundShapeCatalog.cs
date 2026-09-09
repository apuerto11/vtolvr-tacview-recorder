using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// Rates the suitability of a deliberately chosen Tacview ground-vehicle
/// visual proxy. It does not claim that the fictional VTOL VR vehicle is the
/// corresponding real-world vehicle.
/// </summary>
public enum VtolGroundShapeConfidence
{
    Low,
    Medium,
    High
}

/// <summary>
/// Describes the relationship between a VTOL VR ground vehicle and the
/// selected Tacview shape.
/// </summary>
public enum VtolGroundShapeMappingKind
{
    /// <summary>
    /// A consciously selected Tacview model used only as a visual
    /// approximation for a fictional VTOL VR ground vehicle.
    /// </summary>
    VisualProxy
}

/// <summary>
/// An immutable, documented shape decision for a VTOL VR ground-vehicle
/// family.
/// </summary>
public sealed class VtolGroundShapeMapping
{
    internal VtolGroundShapeMapping(
        string vehicleName,
        string shape,
        VtolGroundShapeConfidence confidence,
        string rationale)
    {
        VehicleName = RequireValue(vehicleName, nameof(vehicleName));
        Shape = RequireValue(shape, nameof(shape));
        Confidence = confidence;
        Rationale = RequireValue(rationale, nameof(rationale));
    }

    /// <summary>
    /// The canonical in-game ground-vehicle family. This is lookup metadata;
    /// callers must continue to write the original in-game text as ACMI Name.
    /// </summary>
    public string VehicleName { get; }

    /// <summary>
    /// The official Tacview OBJ filename to write in ACMI Shape.
    /// </summary>
    public string Shape { get; }

    public VtolGroundShapeConfidence Confidence { get; }

    public VtolGroundShapeMappingKind Kind => VtolGroundShapeMappingKind.VisualProxy;

    /// <summary>
    /// Concise explanation retained with the pure data table for code review.
    /// </summary>
    public string Rationale { get; }

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty catalog value is required.", parameterName);
        }

        return value.Trim();
    }
}

/// <summary>
/// Exact-match catalog for the ground-vehicle families observed in the
/// 2026-08-11 VTOL VR recording.
///
/// The catalog intentionally has no substring, prefix, suffix, or fuzzy
/// lookup. It normalizes only case and whitespace. Every alternate name is an
/// explicit alias in the table; unknown names, including airbases, yield no
/// Shape so Tacview can use its normal generic presentation.
/// </summary>
public static class VtolGroundShapeCatalog
{
    private static readonly VtolGroundShapeMapping[] CatalogEntries =
    {
        Mapping(
            "Artillery",
            "Vehicle.Artillery.M109.obj",
            VtolGroundShapeConfidence.High,
            "MPA-155 self-propelled-howitzer visual proxy; it does not assert an M109 identity."),
        Mapping(
            "MBT2-E Tank",
            "Vehicle.Tank.T-62A.obj",
            VtolGroundShapeConfidence.Medium,
            "Generic main-battle-tank visual proxy for the fictional MBT2-E."),
        Mapping(
            "MAD-4 Launcher",
            "AntiAircraft.SAM.SA-6.2P25.obj",
            VtolGroundShapeConfidence.Medium,
            "Mobile SAM-launcher visual proxy for the fictional MAD-4 launcher."),
        Mapping(
            "MAD-4 Radar",
            "AntiAircraft.SAM.SA-6.1S91.obj",
            VtolGroundShapeConfidence.Medium,
            "Mobile combined search/fire-control-radar visual proxy for the fictional MAD-4 radar."),
        Mapping(
            "IR APC",
            "Vehicle.APC.BTR-80.obj",
            VtolGroundShapeConfidence.Low,
            "Generic armored-personnel-carrier visual proxy; the IR APC is fictional."),
        Mapping(
            "SAM Radar",
            "AntiAircraft.SAM.SA-3.SNR-125.obj",
            VtolGroundShapeConfidence.Medium,
            "Stationary SAM-radar visual proxy for the fictional SAM radar family.")
    };

    private static readonly IReadOnlyList<VtolGroundShapeMapping> ReadOnlyEntries =
        Array.AsReadOnly(CatalogEntries);

    private static readonly IReadOnlyDictionary<string, VtolGroundShapeMapping> MappingsByName =
        new ReadOnlyDictionary<string, VtolGroundShapeMapping>(BuildMappingsByName());

    /// <summary>
    /// Gets the catalog decisions once each. Explicit aliases are intentionally
    /// not repeated here.
    /// </summary>
    public static IReadOnlyList<VtolGroundShapeMapping> Entries => ReadOnlyEntries;

    /// <summary>
    /// Gets a catalog entry by normalized exact name. Returns false for unknown
    /// names; callers must not invent a Shape in that case.
    /// </summary>
    public static bool TryGet(string vehicleName, out VtolGroundShapeMapping mapping)
    {
        string normalizedName = NormalizeName(vehicleName);
        if (normalizedName == null)
        {
            mapping = null;
            return false;
        }

        return MappingsByName.TryGetValue(normalizedName, out mapping);
    }

    /// <summary>
    /// Gets only the Shape property needed by an ACMI metadata adapter.
    /// </summary>
    public static bool TryGetShape(string vehicleName, out string shape)
    {
        if (TryGet(vehicleName, out VtolGroundShapeMapping mapping))
        {
            shape = mapping.Shape;
            return true;
        }

        shape = null;
        return false;
    }

    /// <summary>
    /// Normalizes permitted presentation variation without changing the lookup
    /// into a fuzzy search. The return value is an internal key, not a name to
    /// write to ACMI.
    /// </summary>
    public static string NormalizeName(string vehicleName)
    {
        if (string.IsNullOrWhiteSpace(vehicleName))
        {
            return null;
        }

        var normalized = new StringBuilder(vehicleName.Length);
        bool pendingSpace = false;

        foreach (char character in vehicleName.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                normalized.Append(' ');
                pendingSpace = false;
            }

            normalized.Append(char.ToUpperInvariant(character));
        }

        return normalized.ToString();
    }

    private static VtolGroundShapeMapping Mapping(
        string vehicleName,
        string shape,
        VtolGroundShapeConfidence confidence,
        string rationale)
    {
        return new VtolGroundShapeMapping(vehicleName, shape, confidence, rationale);
    }

    private static Dictionary<string, VtolGroundShapeMapping> BuildMappingsByName()
    {
        var mappings = new Dictionary<string, VtolGroundShapeMapping>(StringComparer.Ordinal);

        Add(mappings, CatalogEntries[0], "Artillery", "MPA-155");
        Add(mappings, CatalogEntries[1], "MBT2-E Tank", "MBT2-E");
        Add(mappings, CatalogEntries[2], "MAD-4 Launcher");
        Add(mappings, CatalogEntries[3], "MAD-4 Radar");
        Add(mappings, CatalogEntries[4], "IR APC");
        Add(mappings, CatalogEntries[5], "SAM Radar", "SAM S/A Radar");

        return mappings;
    }

    private static void Add(
        IDictionary<string, VtolGroundShapeMapping> mappings,
        VtolGroundShapeMapping mapping,
        params string[] exactNames)
    {
        for (int index = 0; index < exactNames.Length; index++)
        {
            string normalizedName = NormalizeName(exactNames[index]);
            if (normalizedName == null)
            {
                throw new InvalidOperationException("A catalog alias cannot be empty.");
            }

            if (mappings.TryGetValue(normalizedName, out VtolGroundShapeMapping existing))
            {
                if (!ReferenceEquals(existing, mapping))
                {
                    throw new InvalidOperationException(
                        "Two catalog entries cannot use the same normalized exact name.");
                }

                continue;
            }

            mappings.Add(normalizedName, mapping);
        }
    }
}
