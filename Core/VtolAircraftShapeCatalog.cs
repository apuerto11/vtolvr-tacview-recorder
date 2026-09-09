using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// The confidence of a deliberately selected Tacview shape. This rates the
/// suitability of the mapping decision, not whether a fictional VTOL VR
/// aircraft is an exact real-world aircraft.
/// </summary>
public enum VtolAircraftShapeConfidence
{
    Low,
    Medium,
    High
}

/// <summary>
/// Describes why a catalog entry supplies a Shape value.
/// </summary>
public enum VtolAircraftShapeMappingKind
{
    /// <summary>
    /// A consciously selected real-aircraft model used only as a visual
    /// approximation for a fictional VTOL VR aircraft.
    /// </summary>
    VisualProxy,

    /// <summary>
    /// Tacview's official fixed-wing fallback, chosen explicitly to prevent
    /// Tacview's own name resolution from selecting an unintended model.
    /// </summary>
    DeterministicGeneric
}

/// <summary>
/// An immutable, documented shape decision for one VTOL VR aircraft family.
/// </summary>
public sealed class VtolAircraftShapeMapping
{
    internal VtolAircraftShapeMapping(
        string aircraftName,
        string shape,
        VtolAircraftShapeConfidence confidence,
        VtolAircraftShapeMappingKind kind,
        string rationale)
    {
        AircraftName = RequireValue(aircraftName, nameof(aircraftName));
        Shape = RequireValue(shape, nameof(shape));
        Confidence = confidence;
        Kind = kind;
        Rationale = RequireValue(rationale, nameof(rationale));
    }

    /// <summary>
    /// The canonical fictional VTOL VR aircraft name. This is lookup
    /// metadata; callers must continue to write the in-game name as Name.
    /// </summary>
    public string AircraftName { get; }

    /// <summary>
    /// The official Tacview OBJ filename to write in ACMI Shape.
    /// </summary>
    public string Shape { get; }

    public VtolAircraftShapeConfidence Confidence { get; }

    public VtolAircraftShapeMappingKind Kind { get; }

    /// <summary>
    /// Concise rationale retained with the pure data table for code review.
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
/// Exact-match catalog for known current VTOL VR aircraft.
///
/// The catalog deliberately has no substring, prefix, suffix, or fuzzy lookup.
/// It normalizes only case, whitespace, and complete trailing role suffixes
/// (currently Bomber and Tanker). An unlisted aircraft yields no Shape so that
/// Tacview's normal generic fallback remains in control.
/// </summary>
public static class VtolAircraftShapeCatalog
{
    private static readonly HashSet<string> KnownRoleSuffixes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "BOMBER",
            "TANKER"
        };

    private static readonly VtolAircraftShapeMapping[] CatalogEntries =
    {
        Mapping(
            "AEW-50",
            "FixedWing.E-3.obj",
            VtolAircraftShapeConfidence.Medium,
            VtolAircraftShapeMappingKind.VisualProxy,
            "Airborne early-warning proxy; the AEW-50 is the E-4 counterpart, not an asserted E-3 identity."),
        Mapping(
            "AH-94",
            "Rotorcraft.AH-64.obj",
            VtolAircraftShapeConfidence.High,
            VtolAircraftShapeMappingKind.VisualProxy,
            "Twin-turboshaft tandem attack-helicopter visual approximation."),
        Mapping(
            "ASF-30",
            "FixedWing.F-18C.obj",
            VtolAircraftShapeConfidence.Low,
            VtolAircraftShapeMappingKind.VisualProxy,
            "Carrier multirole-fighter proxy; selected deliberately instead of an unsafe numeric-name inference."),
        Mapping(
            "ASF-33",
            "FixedWing.F-22.obj",
            VtolAircraftShapeConfidence.Low,
            VtolAircraftShapeMappingKind.VisualProxy,
            "Stealth fighter visual approximation; the fictional aircraft has no claimed real counterpart."),
        Mapping(
            "ASF-58",
            "FixedWing.Su-57.obj",
            VtolAircraftShapeConfidence.Medium,
            VtolAircraftShapeMappingKind.VisualProxy,
            "Twin-engine thrust-vectoring stealth-fighter proxy selected explicitly, never by the F-5 substring."),
        Mapping(
            "AV-42C",
            "FixedWing.AV-8B.obj",
            VtolAircraftShapeConfidence.Medium,
            VtolAircraftShapeMappingKind.VisualProxy,
            "Vectored-thrust VTOL attack-aircraft proxy; the transport configuration remains fictional."),
        Mapping(
            "B-11",
            "Core.FixedWing.obj",
            VtolAircraftShapeConfidence.High,
            VtolAircraftShapeMappingKind.DeterministicGeneric,
            "Deliberate official generic shape: no defensible visual proxy for the delta-wing strategic bomber."),
        Mapping(
            "E-4",
            "FixedWing.E-3.obj",
            VtolAircraftShapeConfidence.Medium,
            VtolAircraftShapeMappingKind.VisualProxy,
            "Airborne early-warning visual proxy, not a replacement for the E-4 name."),
        Mapping(
            "EF-24G",
            "FixedWing.F-14.obj",
            VtolAircraftShapeConfidence.High,
            VtolAircraftShapeMappingKind.VisualProxy,
            "Twin-seat, twin-engine variable-sweep fighter visual approximation."),
        Mapping(
            "F-45A",
            "FixedWing.F-35.obj",
            VtolAircraftShapeConfidence.High,
            VtolAircraftShapeMappingKind.VisualProxy,
            "Single-engine stealth STOVL fighter visual approximation."),
        Mapping(
            "F/A-26B",
            "FixedWing.F-18E.obj",
            VtolAircraftShapeConfidence.High,
            VtolAircraftShapeMappingKind.VisualProxy,
            "Twin-engine carrier multirole-fighter visual approximation."),
        Mapping(
            "GAV-25",
            "FixedWing.AV-8B.obj",
            VtolAircraftShapeConfidence.Medium,
            VtolAircraftShapeMappingKind.VisualProxy,
            "STOVL ground-attack visual approximation; deliberately not selected from the Su-25 name fragment."),
        Mapping(
            "HB-106",
            "FixedWing.B-1.obj",
            VtolAircraftShapeConfidence.Low,
            VtolAircraftShapeMappingKind.VisualProxy,
            "Supersonic heavy-bomber approximation selected by exact HB-106 key, never by the B-1 substring."),
        Mapping(
            "KC-49",
            "FixedWing.KC-135C.obj",
            VtolAircraftShapeConfidence.Medium,
            VtolAircraftShapeMappingKind.VisualProxy,
            "Large aerial-tanker visual approximation."),
        Mapping(
            "Manta UCAV",
            "FixedWing.MQ-28.obj",
            VtolAircraftShapeConfidence.Medium,
            VtolAircraftShapeMappingKind.VisualProxy,
            "Small expendable combat-drone visual approximation."),
        Mapping(
            "MQ-31",
            "FixedWing.MQ-9.obj",
            VtolAircraftShapeConfidence.Low,
            VtolAircraftShapeMappingKind.VisualProxy,
            "Generic unmanned-aircraft visual approximation; no MQ-25-shaped asset is asserted."),
        Mapping(
            "T-55",
            "FixedWing.T-38.obj",
            VtolAircraftShapeConfidence.Medium,
            VtolAircraftShapeMappingKind.VisualProxy,
            "Two-seat supersonic trainer visual approximation.")
    };

    private static readonly IReadOnlyList<VtolAircraftShapeMapping> ReadOnlyEntries =
        Array.AsReadOnly(CatalogEntries);

    private static readonly IReadOnlyDictionary<string, VtolAircraftShapeMapping> MappingsByName =
        new ReadOnlyDictionary<string, VtolAircraftShapeMapping>(BuildMappingsByName());

    /// <summary>
    /// Gets the catalog decisions once each. Aliases are intentionally not
    /// repeated here.
    /// </summary>
    public static IReadOnlyList<VtolAircraftShapeMapping> Entries => ReadOnlyEntries;

    /// <summary>
    /// Gets a catalog entry by normalized exact name. Returns false for unknown
    /// names; callers must not invent a Shape in that case.
    /// </summary>
    public static bool TryGet(string aircraftName, out VtolAircraftShapeMapping mapping)
    {
        string normalizedName = NormalizeName(aircraftName);
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
    public static bool TryGetShape(string aircraftName, out string shape)
    {
        if (TryGet(aircraftName, out VtolAircraftShapeMapping mapping))
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
    public static string NormalizeName(string aircraftName)
    {
        if (string.IsNullOrWhiteSpace(aircraftName))
        {
            return null;
        }

        var normalized = new StringBuilder(aircraftName.Length);
        bool pendingSpace = false;

        foreach (char character in aircraftName.Trim())
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

        string value = normalized.ToString();
        while (true)
        {
            int separator = value.LastIndexOf(' ');
            if (separator < 1)
            {
                return value;
            }

            string lastWord = value.Substring(separator + 1);
            if (!KnownRoleSuffixes.Contains(lastWord))
            {
                return value;
            }

            value = value.Substring(0, separator);
        }
    }

    private static VtolAircraftShapeMapping Mapping(
        string aircraftName,
        string shape,
        VtolAircraftShapeConfidence confidence,
        VtolAircraftShapeMappingKind kind,
        string rationale)
    {
        return new VtolAircraftShapeMapping(aircraftName, shape, confidence, kind, rationale);
    }

    private static Dictionary<string, VtolAircraftShapeMapping> BuildMappingsByName()
    {
        var mappings = new Dictionary<string, VtolAircraftShapeMapping>(StringComparer.Ordinal);

        Add(
            mappings,
            CatalogEntries[0],
            "AEW-50",
            "AEW-50 Bulwark");
        Add(
            mappings,
            CatalogEntries[1],
            "AH-94",
            "AH-94 Dragonfly");
        Add(mappings, CatalogEntries[2], "ASF-30");
        Add(mappings, CatalogEntries[3], "ASF-33");
        Add(mappings, CatalogEntries[4], "ASF-58");
        Add(
            mappings,
            CatalogEntries[5],
            "AV-42C",
            "AV-42C Kestrel");
        Add(
            mappings,
            CatalogEntries[6],
            "B-11",
            "B-11 Bomber");
        Add(
            mappings,
            CatalogEntries[7],
            "E-4",
            "E-4 Overlord");
        Add(
            mappings,
            CatalogEntries[8],
            "EF-24G",
            "EF-24G Mischief");
        Add(
            mappings,
            CatalogEntries[9],
            "F-45A",
            "F-45A Ghost");
        Add(
            mappings,
            CatalogEntries[10],
            "F/A-26B",
            "F/A-26B Wasp",
            // Campaign runtime labels omit the slash. This remains a fully
            // explicit alias, not punctuation-insensitive/fuzzy matching.
            "FA-26B");
        Add(
            mappings,
            CatalogEntries[11],
            "GAV-25",
            "GAV-25 Bullshark");
        Add(
            mappings,
            CatalogEntries[12],
            "HB-106",
            "HB-106 Bomber");
        Add(
            mappings,
            CatalogEntries[13],
            "KC-49",
            "KC-49 Tanker");
        Add(
            mappings,
            CatalogEntries[14],
            "Manta UCAV",
            // Campaign AI runtime labels use the platform's all-caps short
            // form. It is intentionally an exact alias only.
            "MANTA");
        Add(mappings, CatalogEntries[15], "MQ-31");
        Add(
            mappings,
            CatalogEntries[16],
            "T-55",
            "T-55 Tyro");

        return mappings;
    }

    private static void Add(
        IDictionary<string, VtolAircraftShapeMapping> mappings,
        VtolAircraftShapeMapping mapping,
        params string[] exactNames)
    {
        for (int index = 0; index < exactNames.Length; index++)
        {
            string normalizedName = NormalizeName(exactNames[index]);
            if (normalizedName == null)
            {
                throw new InvalidOperationException("A catalog alias cannot be empty.");
            }

            if (mappings.TryGetValue(normalizedName, out VtolAircraftShapeMapping existing))
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
