using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// Rates the suitability of a deliberately selected Tacview watercraft visual
/// proxy. It does not claim that a VTOL VR sea unit is the corresponding
/// real-world vessel.
/// </summary>
public enum VtolSeaShapeConfidence
{
    Low,
    Medium,
    High
}

/// <summary>
/// Describes the relationship between a VTOL VR sea-unit family and the
/// selected Tacview shape.
/// </summary>
public enum VtolSeaShapeMappingKind
{
    /// <summary>
    /// A consciously selected Tacview model used only as a visual
    /// approximation for a fictional VTOL VR sea unit.
    /// </summary>
    VisualProxy
}

/// <summary>
/// An immutable, documented shape decision for a VTOL VR sea-unit family.
/// </summary>
public sealed class VtolSeaShapeMapping
{
    internal VtolSeaShapeMapping(
        string vesselName,
        string shape,
        VtolSeaShapeConfidence confidence,
        string rationale)
    {
        VesselName = RequireValue(vesselName, nameof(vesselName));
        Shape = RequireValue(shape, nameof(shape));
        Confidence = confidence;
        Rationale = RequireValue(rationale, nameof(rationale));
    }

    /// <summary>
    /// The canonical VTOL VR sea-unit name. This is lookup metadata; callers
    /// must continue to write the original in-game text as ACMI Name.
    /// </summary>
    public string VesselName { get; }

    /// <summary>
    /// The official Tacview OBJ filename to write in ACMI Shape.
    /// </summary>
    public string Shape { get; }

    public VtolSeaShapeConfidence Confidence { get; }

    public VtolSeaShapeMappingKind Kind => VtolSeaShapeMappingKind.VisualProxy;

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
/// Exact-match catalog for the current VTOL VR carrier group sea-unit
/// families.
///
/// The catalog deliberately has no substring, prefix, suffix, or fuzzy
/// lookup. It normalizes only case and whitespace. A host adapter can provide
/// the explicit carrier-spawn type signal, because that is a stable gameplay
/// classification rather than an inference from a decorated display name.
/// Unknown sea units yield no Shape so Tacview retains its generic rendering.
/// </summary>
public static class VtolSeaShapeCatalog
{
    private const string CarrierShape = "Watercraft.CVN-68.obj";

    private static readonly VtolSeaShapeMapping[] CatalogEntries =
    {
        Mapping(
            "Carrier",
            CarrierShape,
            VtolSeaShapeConfidence.Medium,
            "Nimitz-class carrier visual proxy for VTOL VR's explicit carrier spawn and exact Carrier unit name."),
        Mapping(
            "Escort Cruiser",
            "Watercraft.CG-47.obj",
            VtolSeaShapeConfidence.Medium,
            "Ticonderoga-class cruiser visual proxy for the explicit VTOL VR Escort Cruiser unit name.")
    };

    private static readonly IReadOnlyList<VtolSeaShapeMapping> ReadOnlyEntries =
        Array.AsReadOnly(CatalogEntries);

    private static readonly IReadOnlyDictionary<string, VtolSeaShapeMapping> MappingsByName =
        new ReadOnlyDictionary<string, VtolSeaShapeMapping>(BuildMappingsByName());

    /// <summary>
    /// Gets the catalog decisions once each. Explicit aliases are intentionally
    /// not repeated here.
    /// </summary>
    public static IReadOnlyList<VtolSeaShapeMapping> Entries => ReadOnlyEntries;

    /// <summary>
    /// Gets a catalog entry by normalized exact sea-unit name. Returns false
    /// for unknown names; callers must not invent a Shape in that case.
    /// </summary>
    public static bool TryGet(string vesselName, out VtolSeaShapeMapping mapping)
    {
        string normalizedName = NormalizeName(vesselName);
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
    public static bool TryGetShape(string vesselName, out string shape)
    {
        if (TryGet(vesselName, out VtolSeaShapeMapping mapping))
        {
            shape = mapping.Shape;
            return true;
        }

        shape = null;
        return false;
    }

    /// <summary>
    /// Resolves a normal exact-name catalog entry. The Carrier entry requires
    /// an exact observed stock AICarrierSpawn classification in addition to
    /// its canonical exact name; that signal cannot substitute for the name.
    /// The caller must set
    /// <paramref name="isExactStockCarrierSpawn"/> only after comparing the
    /// concrete in-game type (not an assignable subclass); this method never
    /// infers a carrier from partial or decorated text.
    /// </summary>
    public static bool TryGetShape(
        string vesselName,
        bool isExactStockCarrierSpawn,
        out string shape)
    {
        if (!TryGet(vesselName, out VtolSeaShapeMapping mapping))
        {
            shape = null;
            return false;
        }

        bool isCarrierEntry = string.Equals(
            mapping.VesselName,
            "Carrier",
            StringComparison.Ordinal);

        // The Carrier proxy requires both pieces of stock evidence: the
        // canonical exact name and the exact runtime AICarrierSpawn type. This
        // prevents a custom type which happens to call itself Carrier from
        // inheriting the stock proxy. Conversely, the type signal alone can
        // never make an unknown/null name fail open.
        if (isCarrierEntry != isExactStockCarrierSpawn)
        {
            shape = null;
            return false;
        }

        shape = mapping.Shape;
        return true;
    }

    /// <summary>
    /// Normalizes permitted presentation variation without changing the lookup
    /// into a fuzzy search. The return value is an internal key, not a name to
    /// write to ACMI.
    /// </summary>
    public static string NormalizeName(string vesselName)
    {
        if (string.IsNullOrWhiteSpace(vesselName))
        {
            return null;
        }

        var normalized = new StringBuilder(vesselName.Length);
        bool pendingSpace = false;

        foreach (char character in vesselName.Trim())
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

    private static VtolSeaShapeMapping Mapping(
        string vesselName,
        string shape,
        VtolSeaShapeConfidence confidence,
        string rationale)
    {
        return new VtolSeaShapeMapping(vesselName, shape, confidence, rationale);
    }

    private static Dictionary<string, VtolSeaShapeMapping> BuildMappingsByName()
    {
        var mappings = new Dictionary<string, VtolSeaShapeMapping>(StringComparer.Ordinal);

        Add(mappings, CatalogEntries[0], "Carrier");
        Add(mappings, CatalogEntries[1], "Escort Cruiser");

        return mappings;
    }

    private static void Add(
        IDictionary<string, VtolSeaShapeMapping> mappings,
        VtolSeaShapeMapping mapping,
        params string[] exactNames)
    {
        for (int index = 0; index < exactNames.Length; index++)
        {
            string normalizedName = NormalizeName(exactNames[index]);
            if (normalizedName == null)
            {
                throw new InvalidOperationException("A catalog alias cannot be empty.");
            }

            if (mappings.TryGetValue(normalizedName, out VtolSeaShapeMapping existing))
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
