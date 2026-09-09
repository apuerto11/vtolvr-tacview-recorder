using System;
using System.Collections.Generic;
using System.IO;
using VTOLVRTacviewSPRecorder.Core;
using Xunit;

namespace VTOLVRTacviewSPRecorder.Core.Tests;

public sealed class SeaShapeCatalogTests
{
    [Theory]
    [InlineData("Carrier", "Watercraft.CVN-68.obj")]
    [InlineData("  carrier  ", "Watercraft.CVN-68.obj")]
    [InlineData("Escort Cruiser", "Watercraft.CG-47.obj")]
    [InlineData(" escort    cruiser ", "Watercraft.CG-47.obj")]
    public void KnownSeaFamiliesUseOnlyTheirExplicitlyConfiguredShapes(
        string vesselName,
        string expectedShape)
    {
        Assert.True(VtolSeaShapeCatalog.TryGet(vesselName, out VtolSeaShapeMapping mapping));

        Assert.Equal(expectedShape, mapping.Shape);
        Assert.Equal(VtolSeaShapeMappingKind.VisualProxy, mapping.Kind);
    }

    [Fact]
    public void ExplicitCarrierSpawnClassificationConfirmsOnlyTheCanonicalCarrierEntry()
    {
        Assert.True(
            VtolSeaShapeCatalog.TryGetShape(
                "Carrier",
                isExactStockCarrierSpawn: true,
                out string carrierShape));
        Assert.Equal("Watercraft.CVN-68.obj", carrierShape);

        Assert.False(
            VtolSeaShapeCatalog.TryGetShape(
                "1 Carrier Group",
                isExactStockCarrierSpawn: true,
                out string unknownStockCarrierShape));
        Assert.Null(unknownStockCarrierShape);

        Assert.False(
            VtolSeaShapeCatalog.TryGetShape(
                null,
                isExactStockCarrierSpawn: true,
                out string nullStockCarrierShape));
        Assert.Null(nullStockCarrierShape);
    }

    [Fact]
    public void CanonicalPrefabNameWinsOverDecoratedRuntimeEscortName()
    {
        string catalogName = VtolSeaPlatformNameResolver.Resolve(
            canonicalPrefabActorName: "Escort Cruiser",
            runtimeActorName: "Escort Cruiser 11",
            spawnedUnitDisplayName: "Escort Cruiser 11",
            unitName: "Escort Cruiser");

        Assert.Equal("Escort Cruiser", catalogName);
        Assert.True(VtolSeaShapeCatalog.TryGetShape(catalogName, out string shape));
        Assert.Equal("Watercraft.CG-47.obj", shape);
    }

    [Fact]
    public void DecoratedRuntimeNameDoesNotBecomeASubstringShapeMatchWithoutCanonicalPrefab()
    {
        string catalogName = VtolSeaPlatformNameResolver.Resolve(
            canonicalPrefabActorName: null,
            runtimeActorName: "Escort Cruiser 11",
            spawnedUnitDisplayName: null,
            unitName: null);

        Assert.Equal("Escort Cruiser 11", catalogName);
        Assert.False(VtolSeaShapeCatalog.TryGetShape(catalogName, out string shape));
        Assert.Null(shape);
    }

    [Fact]
    public void CustomCarrierSubclassNeverReceivesStockFallbackWithoutExactCanonicalName()
    {
        Assert.True(VtolSeaSpawnTypePolicy.IsExactStockType(
            typeof(StockCarrierSpawn),
            typeof(StockCarrierSpawn)));
        Assert.False(VtolSeaSpawnTypePolicy.IsExactStockType(
            typeof(CustomCarrierSubclass),
            typeof(StockCarrierSpawn)));

        Assert.False(VtolSeaShapeCatalog.TryGetShape(
            "Custom Carrier Variant",
            isExactStockCarrierSpawn: false,
            out string shape));
        Assert.Null(shape);

        Assert.False(VtolSeaShapeCatalog.TryGetShape(
            "Custom Carrier Variant",
            isExactStockCarrierSpawn: true,
            out string forcedStockSignalShape));
        Assert.Null(forcedStockSignalShape);
    }

    [Theory]
    [InlineData("Carrier Group")]
    [InlineData("Enemy Carrier")]
    [InlineData("Carrier Mk II")]
    [InlineData("Escort Cruiser Mk II")]
    [InlineData("Escort Frigate")]
    [InlineData("Watercraft.CVN-68.obj")]
    public void LookupNeverFallsBackToSubstringOrGenericWarshipMatching(string vesselName)
    {
        Assert.False(VtolSeaShapeCatalog.TryGetShape(vesselName, out string shape));
        Assert.Null(shape);
    }

    [Fact]
    public void WriterPreservesSeaIdentityAndEmitsTheExplicitCarrierShape()
    {
        Assert.True(VtolSeaShapeCatalog.TryGetShape("Carrier", out string shape));
        var metadata = new EntityMetadata(
            "Sea+Watercraft",
            "Carrier",
            "Allies",
            "Blue",
            shape: shape);
        var text = new StringWriter();

        using (var writer = new TacviewEntityWriter(
            text,
            "Sea shape test",
            DateTimeOffset.Parse("2026-08-13T00:00:00Z"),
            leaveOpen: true))
        {
            writer.WriteFrame(
                0.0,
                new List<TacviewEntityUpdate>
                {
                    TacviewEntityUpdate.Upsert(0x101, Transform(), metadata)
                },
                new List<TacviewEvent>());
        }

        string acmi = text.ToString();
        Assert.Contains(
            "101,T=7.0000000|51.0000000|100.00|0.00|0.00|0.00|0.00|0.00|90.00" +
            ",Type=Sea+Watercraft,Name=Carrier,Shape=Watercraft.CVN-68.obj",
            acmi);
        Assert.DoesNotContain("Name=CVN-68", acmi);
    }

    private static EntityTransform Transform()
    {
        return new EntityTransform(7.0, 51.0, 100.0, 0.0, 0.0, 0.0, 0.0, 0.0, 90.0);
    }

    private class StockCarrierSpawn
    {
    }

    private sealed class CustomCarrierSubclass : StockCarrierSpawn
    {
    }
}
