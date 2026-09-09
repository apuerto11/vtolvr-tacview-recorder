using VTOLVRTacviewSPRecorder.Core;
using Xunit;

namespace VTOLVRTacviewSPRecorder.Core.Tests;

public sealed class GroundShapeCatalogTests
{
    [Theory]
    [InlineData("Artillery", "Vehicle.Artillery.M109.obj")]
    [InlineData("MPA-155", "Vehicle.Artillery.M109.obj")]
    [InlineData("MBT2-E Tank", "Vehicle.Tank.T-62A.obj")]
    [InlineData("MBT2-E", "Vehicle.Tank.T-62A.obj")]
    [InlineData("MAD-4 Launcher", "AntiAircraft.SAM.SA-6.2P25.obj")]
    [InlineData("MAD-4 Radar", "AntiAircraft.SAM.SA-6.1S91.obj")]
    [InlineData("IR APC", "Vehicle.APC.BTR-80.obj")]
    [InlineData("SAM Radar", "AntiAircraft.SAM.SA-3.SNR-125.obj")]
    [InlineData("SAM S/A Radar", "AntiAircraft.SAM.SA-3.SNR-125.obj")]
    public void ObservedGroundFamiliesUseOnlyTheirExplicitlyConfiguredShapes(
        string vehicleName,
        string expectedShape)
    {
        Assert.True(VtolGroundShapeCatalog.TryGet(vehicleName, out VtolGroundShapeMapping mapping));
        Assert.Equal(expectedShape, mapping.Shape);
        Assert.Equal(VtolGroundShapeMappingKind.VisualProxy, mapping.Kind);
    }

    [Fact]
    public void NormalizationHandlesOnlyCaseAndWhitespace()
    {
        const string sourceName = "  mad-4    launcher  ";

        Assert.True(VtolGroundShapeCatalog.TryGetShape(sourceName, out string shape));

        Assert.Equal("MAD-4 LAUNCHER", VtolGroundShapeCatalog.NormalizeName(sourceName));
        Assert.Equal("AntiAircraft.SAM.SA-6.2P25.obj", shape);
        Assert.Equal("  mad-4    launcher  ", sourceName);
    }

    [Fact]
    public void AirbasesAndUnknownGroundVehiclesDoNotReceiveInventedShapes()
    {
        Assert.False(VtolGroundShapeCatalog.TryGetShape("Barton Airbase", out string airbaseShape));
        Assert.Null(airbaseShape);
        Assert.False(VtolGroundShapeCatalog.TryGetShape("Unknown Tank", out string unknownShape));
        Assert.Null(unknownShape);
    }

    [Theory]
    [InlineData("Enemy Artillery")]
    [InlineData("Artillery Battery")]
    [InlineData("MBT2-E Tank Mk II")]
    [InlineData("MAD-4 Launcher X")]
    [InlineData("SAM Radar v2")]
    [InlineData("Some BTR-80-like vehicle")]
    public void LookupNeverFallsBackToSubstringOrSuffixMatching(string vehicleName)
    {
        Assert.False(VtolGroundShapeCatalog.TryGet(vehicleName, out _));
    }

    [Fact]
    public void ConfidenceReflectsTheDocumentedProxyStrength()
    {
        Assert.True(VtolGroundShapeCatalog.TryGet("MPA-155", out VtolGroundShapeMapping artillery));
        Assert.True(VtolGroundShapeCatalog.TryGet("MBT2-E", out VtolGroundShapeMapping tank));
        Assert.True(VtolGroundShapeCatalog.TryGet("IR APC", out VtolGroundShapeMapping apc));

        Assert.Equal(VtolGroundShapeConfidence.High, artillery.Confidence);
        Assert.Equal(VtolGroundShapeConfidence.Medium, tank.Confidence);
        Assert.Equal(VtolGroundShapeConfidence.Low, apc.Confidence);
    }
}
