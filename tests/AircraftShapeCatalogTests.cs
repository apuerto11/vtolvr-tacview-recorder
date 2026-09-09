using VTOLVRTacviewSPRecorder.Core;
using Xunit;

namespace VTOLVRTacviewSPRecorder.Core.Tests;

public sealed class AircraftShapeCatalogTests
{
    [Theory]
    [InlineData("AEW-50 Bulwark", "FixedWing.E-3.obj")]
    [InlineData("AH-94", "Rotorcraft.AH-64.obj")]
    [InlineData("ASF-30", "FixedWing.F-18C.obj")]
    [InlineData("ASF-33", "FixedWing.F-22.obj")]
    [InlineData("ASF-58", "FixedWing.Su-57.obj")]
    [InlineData("AV-42C Kestrel", "FixedWing.AV-8B.obj")]
    [InlineData("B-11 Bomber", "Core.FixedWing.obj")]
    [InlineData("E-4 Overlord", "FixedWing.E-3.obj")]
    [InlineData("EF-24G Mischief", "FixedWing.F-14.obj")]
    [InlineData("F-45A Ghost", "FixedWing.F-35.obj")]
    [InlineData("F/A-26B Wasp", "FixedWing.F-18E.obj")]
    [InlineData("GAV-25 Bullshark", "FixedWing.AV-8B.obj")]
    [InlineData("HB-106 Bomber", "FixedWing.B-1.obj")]
    [InlineData("KC-49 Tanker", "FixedWing.KC-135C.obj")]
    [InlineData("Manta UCAV", "FixedWing.MQ-28.obj")]
    [InlineData("MQ-31", "FixedWing.MQ-9.obj")]
    [InlineData("T-55 Tyro", "FixedWing.T-38.obj")]
    public void KnownAircraftUseOnlyTheirExplicitlyConfiguredShapes(
        string aircraftName,
        string expectedShape)
    {
        Assert.True(VtolAircraftShapeCatalog.TryGet(aircraftName, out VtolAircraftShapeMapping mapping));
        Assert.Equal(expectedShape, mapping.Shape);
    }

    [Fact]
    public void Asf58DoesNotReceiveTheF5ShapeFromItsEmbeddedText()
    {
        Assert.True(VtolAircraftShapeCatalog.TryGetShape("ASF-58", out string shape));

        Assert.Equal("FixedWing.Su-57.obj", shape);
        Assert.NotEqual("FixedWing.F-5.obj", shape);
        Assert.False(VtolAircraftShapeCatalog.TryGet("F-5", out _));
    }

    [Fact]
    public void Hb106GetsOnlyTheExplicitB1Proxy()
    {
        Assert.True(VtolAircraftShapeCatalog.TryGet("HB-106", out VtolAircraftShapeMapping mapping));

        Assert.Equal("FixedWing.B-1.obj", mapping.Shape);
        Assert.Equal(VtolAircraftShapeMappingKind.VisualProxy, mapping.Kind);
        Assert.NotEqual("FixedWing.B-2.obj", mapping.Shape);
        Assert.NotEqual("FixedWing.B-21.obj", mapping.Shape);
    }

    [Fact]
    public void Fa26bRetainsTheFictionalInputNameWhileProvidingAShape()
    {
        const string originalName = "F/A-26B";

        Assert.True(VtolAircraftShapeCatalog.TryGet(originalName, out VtolAircraftShapeMapping mapping));

        Assert.Equal("F/A-26B", originalName);
        Assert.Equal("F/A-26B", mapping.AircraftName);
        Assert.Equal("FixedWing.F-18E.obj", mapping.Shape);
    }

    [Theory]
    [InlineData("FA-26B", "FixedWing.F-18E.obj")]
    [InlineData("MANTA", "FixedWing.MQ-28.obj")]
    public void CampaignRuntimeAliasesResolveOnlyTheirExplicitCatalogEntries(
        string runtimePlatformName,
        string expectedShape)
    {
        Assert.True(VtolAircraftShapeCatalog.TryGet(runtimePlatformName, out VtolAircraftShapeMapping mapping));
        Assert.Equal(expectedShape, mapping.Shape);
    }

    [Theory]
    [InlineData("FA-26B-EX")]
    [InlineData("XFA-26B")]
    [InlineData("MANTA Drone")]
    [InlineData("Super MANTA")]
    public void CampaignAliasesDoNotBroadenIntoFuzzyMatching(string platformName)
    {
        Assert.False(VtolAircraftShapeCatalog.TryGetShape(platformName, out string shape));
        Assert.Null(shape);
    }

    [Fact]
    public void UnknownAircraftDoesNotReceiveAnInventedShape()
    {
        Assert.False(VtolAircraftShapeCatalog.TryGetShape("Experimental X-999", out string shape));
        Assert.Null(shape);
    }

    [Fact]
    public void NormalizationHandlesWhitespaceCaseAndKnownRoleSuffixes()
    {
        Assert.True(
            VtolAircraftShapeCatalog.TryGet(
                "  hb-106     BOMBER  ",
                out VtolAircraftShapeMapping mapping));

        Assert.Equal("HB-106", VtolAircraftShapeCatalog.NormalizeName("  hb-106     BOMBER  "));
        Assert.Equal("FixedWing.B-1.obj", mapping.Shape);
    }

    [Theory]
    [InlineData("XASF-58")]
    [InlineData("ASF-580")]
    [InlineData("HB-106X")]
    [InlineData("Some F-5-ish aircraft")]
    [InlineData("Some B-1-ish aircraft")]
    public void LookupNeverFallsBackToSubstringMatching(string aircraftName)
    {
        Assert.False(VtolAircraftShapeCatalog.TryGet(aircraftName, out _));
    }

    [Fact]
    public void B11UsesAnExplicitOfficialGenericInsteadOfABomberProxy()
    {
        Assert.True(VtolAircraftShapeCatalog.TryGet("B-11 Bomber", out VtolAircraftShapeMapping mapping));

        Assert.Equal("Core.FixedWing.obj", mapping.Shape);
        Assert.Equal(VtolAircraftShapeMappingKind.DeterministicGeneric, mapping.Kind);
    }
}
