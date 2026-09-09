using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using VTOLVRTacviewSPRecorder.Core;

namespace VTOLVRTacviewSPRecorder.Main;

/// <summary>
/// Main-thread-only terrain sampling boundary. Implementations may retain
/// Unity/VTOL objects, but never cross into TacviewTerrainBackground tasks.
/// </summary>
internal interface IVtolTerrainSource
{
    string Description { get; }

    string SourceSignature { get; }

    int SourceWidth { get; }

    int SourceHeight { get; }

    int ExportWidth { get; }

    int ExportHeight { get; }

    double MinGlobalX { get; }

    double MinGlobalZ { get; }

    double MaxGlobalX { get; }

    double MaxGlobalZ { get; }

    bool IsAvailable { get; }

    float SampleAltitude(double globalX, double globalZ);
}

/// <summary>
/// Finds one real gameplay terrain source. Procedural VTMapGenerator data is
/// preferred where available. Static Unity Terrain/TerrainData is a supported
/// secondary path; Akutan instead uses its separately verified MeshCollider
/// terrain source because it exposes neither hmBdt nor Unity Terrain tiles.
/// </summary>
internal static class VtolTerrainSourceFactory
{
    // Mesh terrain needs a bounded, full pre-header geometry snapshot. Keep the
    // successful snapshot for the same map manager so lifecycle readiness and
    // TryPrepare do not build the same index twice on Unity's main thread.
    private static VTMapManager _cachedMeshMapManager;
    private static VtolMeshTerrainSource _cachedMeshSource;
    private static string _cachedMeshReadinessFingerprint;
    // A failed Akutan geometry preparation can be expensive: it snapshots and
    // indexes over one million triangles on Unity's main thread.  Readiness
    // can legitimately change while GridPlatoon is starting, so do not cache
    // an incomplete family.  Once the complete reviewed family is stable,
    // however, memoize a failure against its cheap structural fingerprint and
    // never repeat that snapshot until something observable has changed.
    private static VTMapManager _failedMeshMapManager;
    private static string _failedMeshReadinessFingerprint;
    private static string _failedMeshError;

    /// <summary>
    /// Releases all game-owned Unity references and the large Akutan snapshot.
    /// Lifecycle boundaries call this even when no recorder session reached
    /// writer creation, so readiness probes cannot retain a previous mission.
    /// </summary>
    internal static void ClearMeshCache()
    {
        _cachedMeshMapManager = null;
        _cachedMeshSource = null;
        _cachedMeshReadinessFingerprint = null;
        _failedMeshMapManager = null;
        _failedMeshReadinessFingerprint = null;
        _failedMeshError = null;
    }

    public static bool IsSourceAvailable(VTMapManager mapManager, out string error)
    {
        return TryCreate(mapManager, out _, out error);
    }

    public static bool TryCreate(
        VTMapManager mapManager,
        out IVtolTerrainSource source,
        out string error)
    {
        source = null;
        error = null;

        if (mapManager == null)
        {
            error = "VTMapManager.fetch is unavailable.";
            return false;
        }

        try
        {
            VTMapGenerator generator = VTMapGenerator.fetch;
            if (generator != null && generator.hmBdt != null)
            {
                source = new VtolGeneratedTerrainSource(generator, mapManager.map);
                return true;
            }
        }
        catch (Exception ex)
        {
            error = "VTMapGenerator source inspection failed: " +
                ex.GetType().Name + ": " + ex.Message;
        }

        try
        {
            if (VtolStaticTerrainSource.TryCreate(mapManager, out VtolStaticTerrainSource staticSource, out string staticError))
            {
                source = staticSource;
                return true;
            }

            error = CombineErrors(error, staticError);
        }
        catch (Exception ex)
        {
            error = CombineErrors(
                error,
                "Unity Terrain source inspection failed: " + ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            if (!VtolMeshTerrainSource.TryGetReadinessFingerprint(
                    mapManager,
                    out string readinessFingerprint,
                    out string readinessError))
            {
                _cachedMeshMapManager = null;
                _cachedMeshSource = null;
                _cachedMeshReadinessFingerprint = null;
                error = CombineErrors(error, readinessError);
            }
            else if (_cachedMeshMapManager == mapManager &&
                _cachedMeshSource != null &&
                _cachedMeshSource.IsAvailable &&
                string.Equals(
                    _cachedMeshReadinessFingerprint,
                    readinessFingerprint,
                    StringComparison.Ordinal))
            {
                source = _cachedMeshSource;
                return true;
            }
            else if (ClearStalePositiveCacheAndMatchesFailure(
                    mapManager,
                    readinessFingerprint))
            {
                error = CombineErrors(
                    error,
                    "The complete verified Akutan MeshCollider family is unchanged since its " +
                    "last rejected preparation: " + _failedMeshError);
            }
            else if (VtolMeshTerrainSource.TryCreate(
                    mapManager,
                    out VtolMeshTerrainSource meshSource,
                    out string meshError))
            {
                _failedMeshMapManager = null;
                _failedMeshReadinessFingerprint = null;
                _failedMeshError = null;
                _cachedMeshMapManager = mapManager;
                _cachedMeshSource = meshSource;
                _cachedMeshReadinessFingerprint = readinessFingerprint;
                source = meshSource;
                return true;
            }
            else
            {
                _cachedMeshMapManager = null;
                _cachedMeshSource = null;
                _cachedMeshReadinessFingerprint = null;
                _failedMeshMapManager = mapManager;
                _failedMeshReadinessFingerprint = readinessFingerprint;
                _failedMeshError = string.IsNullOrWhiteSpace(meshError)
                    ? "The MeshCollider terrain preparation did not produce a usable heightfield."
                    : meshError;
                error = CombineErrors(error, _failedMeshError);
            }
        }
        catch (Exception ex)
        {
            error = CombineErrors(
                error,
                "MeshCollider terrain source inspection failed: " + ex.GetType().Name + ": " + ex.Message);
        }

        if (string.IsNullOrWhiteSpace(error))
        {
            error = "Neither VTMapGenerator height data, an active Unity Terrain source, nor a verified MeshCollider terrain source is available.";
        }

        return false;
    }

    private static bool ClearStalePositiveCacheAndMatchesFailure(
        VTMapManager mapManager,
        string readinessFingerprint)
    {
        // Reaching this branch proves the positive-cache identity, live state,
        // or invariant geometry fingerprint changed. Drop the large snapshot
        // and Unity references before consulting/rebuilding the negative cache.
        _cachedMeshMapManager = null;
        _cachedMeshSource = null;
        _cachedMeshReadinessFingerprint = null;
        return _failedMeshMapManager == mapManager &&
            string.Equals(
                _failedMeshReadinessFingerprint,
                readinessFingerprint,
                StringComparison.Ordinal);
    }

    private static string CombineErrors(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return second;
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        return first + " " + second;
    }
}

internal sealed class VtolGeneratedTerrainSource : IVtolTerrainSource
{
    private readonly VTMapGenerator _generator;

    public VtolGeneratedTerrainSource(VTMapGenerator generator, VTMap map)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        if (_generator.hmBdt == null)
        {
            throw new InvalidOperationException("VTMapGenerator height data is unavailable.");
        }

        double totalSize = _generator.totalSize;
        if (!TacviewTerrainDefinition.IsFinite(totalSize) || totalSize <= 0.0)
        {
            throw new InvalidOperationException("VTOL reported an invalid generated terrain size.");
        }

        int width = _generator.hmBdt.width;
        int height = _generator.hmBdt.height;
        if (width < 2 || height < 2)
        {
            throw new InvalidOperationException("VTOL reported an invalid generated heightmap resolution.");
        }

        SourceWidth = width;
        SourceHeight = height;
        ExportWidth = Math.Min(width, VtolTerrainExporter.MaximumExportDimension);
        ExportHeight = Math.Min(height, VtolTerrainExporter.MaximumExportDimension);
        MinGlobalX = 0.0;
        MinGlobalZ = 0.0;
        MaxGlobalX = totalSize;
        MaxGlobalZ = totalSize;
        SourceSignature = VtolTerrainSourceSignatures.BuildGenerated(_generator, map);
    }

    public string SourceSignature { get; }

    public string Description => "VTMapGenerator gameplay heightfield";

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    public int ExportWidth { get; }

    public int ExportHeight { get; }

    public double MinGlobalX { get; }

    public double MinGlobalZ { get; }

    public double MaxGlobalX { get; }

    public double MaxGlobalZ { get; }

    public bool IsAvailable => _generator != null && _generator.hmBdt != null;

    public float SampleAltitude(double globalX, double globalZ)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("VTMapGenerator height data became unavailable.");
        }

        return _generator.GetHeightmapAltitude(new Vector3D(globalX, 0.0, globalZ));
    }
}

/// <summary>
/// Concrete source for built-in campaign terrain. It uses public active
/// UnityEngine.Terrain/TerrainData APIs only on the Unity thread and converts
/// both bounds and sampled heights through VTOL's public global/GPS helpers.
/// </summary>
internal sealed class VtolStaticTerrainSource : IVtolTerrainSource
{
    private const double HorizontalPlaneToleranceMeters = 0.05;
    private const float LocalCoordinateTolerance = 0.001f;

    private readonly VTMapManager _mapManager;
    private readonly TacviewTerrainGrid _grid;
    private readonly Dictionary<string, RuntimeTile> _tilesByKey;

    private VtolStaticTerrainSource(
        VTMapManager mapManager,
        TacviewTerrainGrid grid,
        Dictionary<string, RuntimeTile> tilesByKey)
    {
        _mapManager = mapManager;
        _grid = grid;
        _tilesByKey = tilesByKey;
        SourceSignature = VtolTerrainSourceSignatures.BuildStatic(grid, tilesByKey);
    }

    public string SourceSignature { get; }

    public string Description => "active Unity Terrain/TerrainData tile grid";

    public int SourceWidth => _grid.SourceWidth;

    public int SourceHeight => _grid.SourceHeight;

    public int ExportWidth => _grid.ExportWidth;

    public int ExportHeight => _grid.ExportHeight;

    public double MinGlobalX => _grid.MinX;

    public double MinGlobalZ => _grid.MinZ;

    public double MaxGlobalX => _grid.MaxX;

    public double MaxGlobalZ => _grid.MaxZ;

    public bool IsAvailable
    {
        get
        {
            if (_mapManager == null)
            {
                return false;
            }

            foreach (RuntimeTile tile in _tilesByKey.Values)
            {
                if (!tile.IsAvailable)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public static bool TryCreate(
        VTMapManager mapManager,
        out VtolStaticTerrainSource source,
        out string error)
    {
        source = null;
        error = null;

        if (mapManager == null)
        {
            error = "VTMapManager.fetch is unavailable for static Terrain coordinate conversion.";
            return false;
        }

        Terrain[] activeTerrains = Terrain.activeTerrains;
        if (activeTerrains == null || activeTerrains.Length == 0)
        {
            error = "No active Unity Terrain/TerrainData tiles are loaded.";
            return false;
        }

        var runtimeTiles = new List<RuntimeTile>(activeTerrains.Length);
        var coreTiles = new List<TacviewTerrainTile>(activeTerrains.Length);
        var tilesByKey = new Dictionary<string, RuntimeTile>(StringComparer.Ordinal);
        try
        {
            for (int index = 0; index < activeTerrains.Length; index++)
            {
                Terrain terrain = activeTerrains[index];
                if (terrain == null || !terrain.isActiveAndEnabled || terrain.terrainData == null)
                {
                    continue;
                }

                RuntimeTile runtimeTile = RuntimeTile.Create(terrain);
                if (tilesByKey.ContainsKey(runtimeTile.Key))
                {
                    throw new InvalidOperationException(
                        "Active Terrain tiles do not have unique deterministic hierarchy keys.");
                }

                tilesByKey.Add(runtimeTile.Key, runtimeTile);

                runtimeTiles.Add(runtimeTile);
                coreTiles.Add(runtimeTile.CoreTile);
            }

            if (coreTiles.Count == 0)
            {
                error = "No enabled Unity Terrain component exposed usable TerrainData.";
                return false;
            }

            TacviewTerrainGrid grid = TacviewTerrainGridBuilder.Build(
                coreTiles,
                VtolTerrainExporter.MaximumExportDimension);
            source = new VtolStaticTerrainSource(mapManager, grid, tilesByKey);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public float SampleAltitude(double globalX, double globalZ)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("The static Unity Terrain source became unavailable.");
        }

        if (!_grid.TryGetTile(globalX, globalZ, out TacviewTerrainTile tile) ||
            !_tilesByKey.TryGetValue(tile.Key, out RuntimeTile runtimeTile))
        {
            throw new InvalidOperationException(
                "The requested terrain sample lies outside the verified Unity Terrain tile grid.");
        }

        return runtimeTile.SampleAltitude(_mapManager, globalX, globalZ);
    }

    internal sealed class RuntimeTile
    {
        private readonly Terrain _terrain;
        private readonly TerrainData _terrainData;

        private RuntimeTile(Terrain terrain, TerrainData terrainData, string key, TacviewTerrainTile coreTile)
        {
            _terrain = terrain;
            _terrainData = terrainData;
            Key = key;
            CoreTile = coreTile;
        }

        public string Key { get; }

        public TacviewTerrainTile CoreTile { get; }

        public TerrainData TerrainData => _terrainData;

        public bool IsAvailable =>
            _terrain != null &&
            _terrain.isActiveAndEnabled &&
            _terrain.terrainData == _terrainData;

        public static RuntimeTile Create(Terrain terrain)
        {
            if (terrain == null || terrain.transform == null || terrain.terrainData == null)
            {
                throw new ArgumentException("A live Terrain with TerrainData and Transform is required.", nameof(terrain));
            }

            TerrainData terrainData = terrain.terrainData;
            Vector3 size = terrainData.size;
            if (!IsFinite(size.x) || !IsFinite(size.y) || !IsFinite(size.z) ||
                size.x <= 0.0f || size.z <= 0.0f)
            {
                throw new InvalidOperationException("Unity TerrainData reported invalid horizontal bounds.");
            }

            int sourceResolution = terrainData.heightmapResolution;
            if (sourceResolution < 2)
            {
                throw new InvalidOperationException("Unity TerrainData reported an invalid heightmap resolution.");
            }

            Transform transform = terrain.transform;
            Vector3D global0 = ToGlobal(transform.TransformPoint(Vector3.zero));
            Vector3D globalX = ToGlobal(transform.TransformPoint(new Vector3(size.x, 0.0f, 0.0f)));
            Vector3D globalXZ = ToGlobal(transform.TransformPoint(new Vector3(size.x, 0.0f, size.z)));
            Vector3D globalZ = ToGlobal(transform.TransformPoint(new Vector3(0.0f, 0.0f, size.z)));
            ValidateHorizontalPlane(global0, globalX, globalXZ, globalZ);

            string key = BuildHierarchyKey(transform);
            var coreTile = new TacviewTerrainTile(
                key,
                sourceResolution,
                sourceResolution,
                ToPlanarPoint(global0),
                ToPlanarPoint(globalX),
                ToPlanarPoint(globalXZ),
                ToPlanarPoint(globalZ));
            return new RuntimeTile(terrain, terrainData, key, coreTile);
        }

        public float SampleAltitude(VTMapManager mapManager, double globalX, double globalZ)
        {
            if (!IsAvailable || mapManager == null)
            {
                throw new InvalidOperationException("The selected Unity Terrain tile is unavailable.");
            }

            Vector3 worldPoint = VTMapManager.GlobalToWorldPoint(
                new Vector3D(globalX, 0.0, globalZ));
            Vector3 localPoint = _terrain.transform.InverseTransformPoint(worldPoint);
            Vector3 size = _terrainData.size;
            float normalizedX = localPoint.x / size.x;
            float normalizedZ = localPoint.z / size.z;
            if (normalizedX < -LocalCoordinateTolerance ||
                normalizedX > 1.0f + LocalCoordinateTolerance ||
                normalizedZ < -LocalCoordinateTolerance ||
                normalizedZ > 1.0f + LocalCoordinateTolerance)
            {
                throw new InvalidOperationException(
                    "Global terrain-grid selection did not map into the selected TerrainData tile.");
            }

            float terrainLocalHeight = _terrainData.GetInterpolatedHeight(
                Mathf.Clamp01(normalizedX),
                Mathf.Clamp01(normalizedZ));
            Vector3 terrainWorldPoint = _terrain.transform.TransformPoint(
                new Vector3(localPoint.x, terrainLocalHeight, localPoint.z));
            Vector3D gps = mapManager.WorldPositionToGPSCoords(terrainWorldPoint);
            if (!TacviewTerrainDefinition.IsFinite(gps.z))
            {
                throw new InvalidOperationException("VTOL GPS conversion returned a non-finite terrain altitude.");
            }

            return (float)gps.z;
        }

        private static Vector3D ToGlobal(Vector3 worldPoint)
        {
            return VTMapManager.WorldToGlobalPoint(worldPoint);
        }

        private static TacviewTerrainPlanarPoint ToPlanarPoint(Vector3D globalPoint)
        {
            if (!TacviewTerrainDefinition.IsFinite(globalPoint.x) ||
                !TacviewTerrainDefinition.IsFinite(globalPoint.z))
            {
                throw new InvalidOperationException("VTOL global conversion returned invalid terrain coordinates.");
            }

            return new TacviewTerrainPlanarPoint(globalPoint.x, globalPoint.z);
        }

        private static void ValidateHorizontalPlane(
            Vector3D corner0,
            Vector3D corner1,
            Vector3D corner2,
            Vector3D corner3)
        {
            if (!TacviewTerrainDefinition.IsFinite(corner0.y) ||
                !TacviewTerrainDefinition.IsFinite(corner1.y) ||
                !TacviewTerrainDefinition.IsFinite(corner2.y) ||
                !TacviewTerrainDefinition.IsFinite(corner3.y))
            {
                throw new InvalidOperationException("VTOL global conversion returned invalid terrain elevation data.");
            }

            double referenceHeight = corner0.y;
            if (Math.Abs(corner1.y - referenceHeight) > HorizontalPlaneToleranceMeters ||
                Math.Abs(corner2.y - referenceHeight) > HorizontalPlaneToleranceMeters ||
                Math.Abs(corner3.y - referenceHeight) > HorizontalPlaneToleranceMeters)
            {
                throw new InvalidOperationException(
                    "Unity Terrain is tilted relative to VTOL global coordinates and cannot be exported as a heightfield.");
            }
        }

        private static string BuildHierarchyKey(Transform transform)
        {
            var segments = new List<string>();
            for (Transform current = transform; current != null; current = current.parent)
            {
                segments.Add((current.name ?? string.Empty) + "#" +
                    current.GetSiblingIndex().ToString(CultureInfo.InvariantCulture));
            }

            segments.Reverse();
            return transform.gameObject.scene.name + "/" + string.Join("/", segments);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}

/// <summary>
/// Snapshot source for the built-in Akutan mesh terrain.  The game scene uses
/// no Unity Terrain/TerrainData there; instead it loads read-only, non-convex
/// MeshCollider surfaces named multiRes_terrain.* under the authored
/// akutan2/Platoons hierarchy.  This adapter intentionally accepts only that
/// verified family and never performs a global Physics query or samples
/// unrelated scene geometry.
/// </summary>
internal sealed class VtolMeshTerrainSource : IVtolTerrainSource
{
    // This is a closed evidence-backed family, not a prefix match. In the
    // installed build `_0` is an unbound two-triangle asset and is intentionally
    // absent; the actual Map Terrain has these 35 collider/renderer bindings.
    private static readonly VerifiedMeshDefinition[] VerifiedAkutanMeshes =
    {
        new VerifiedMeshDefinition("multiRes_terrain.004", "akutan2Colored_1", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.005", "akutan2Colored_2", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.006", "akutan2Colored_3", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.011", "akutan2Colored_4", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.012", "akutan2Colored_5", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.013", "akutan2Colored_6", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.014", "akutan2Colored_7", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.015", "akutan2Colored_8", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.016", "akutan2Colored_9", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.019", "akutan2Colored_10", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.020", "akutan2Colored_11", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.021", "akutan2Colored_12", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.022", "akutan2Colored_13", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.023", "akutan2Colored_14", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.024", "akutan2Colored_15", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.025", "akutan2Colored_16", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.026", "akutan2Colored_17", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.027", "akutan2Colored_18", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.028", "akutan2Colored_19", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.029", "akutan2Colored_20", 16644, 98310),
        new VerifiedMeshDefinition("multiRes_terrain.030", "akutan2Colored_21", 18317, 98292),
        new VerifiedMeshDefinition("multiRes_terrain.031", "akutan2Colored_22", 17471, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.032", "akutan2Colored_23", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.036", "akutan2Colored_24", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.037", "akutan2Colored_25", 16643, 98307),
        new VerifiedMeshDefinition("multiRes_terrain.038", "akutan2Colored_26", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.039", "akutan2Colored_27", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.040", "akutan2Colored_28", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.045", "akutan2Colored_29", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.047", "akutan2Colored_30", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.048", "akutan2Colored_31", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.049", "akutan2Colored_32", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.050", "akutan2Colored_33", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.051", "akutan2Colored_34", 16641, 98304),
        new VerifiedMeshDefinition("multiRes_terrain.052", "akutan2Colored_35", 16641, 98304)
    };

    private const string TerrainRootName = "akutan2";
    private const string TerrainContainerName = "Platoons";
    private const string AkutanMapId = "map_akutan";
    private const float GridPlatoonPositionToleranceMeters = 0.05f;
    private const int MaximumMeshExportDimension =
        TacviewAkutanRasterResolver.ProductionDimension;
    private const int ExpectedAkutanGridPlatoonCount = 34;

    private readonly VTMapManager _mapManager;
    private readonly TacviewMeshTerrainGrid _grid;
    private readonly TacviewAkutanRaster _resolvedRaster;
    private readonly IReadOnlyList<RuntimeMeshCollider> _runtimeMeshes;

    internal readonly struct VerifiedMeshDefinition
    {
        public VerifiedMeshDefinition(
            string objectName,
            string meshName,
            int vertexCount,
            int triangleIndexCount)
        {
            ObjectName = objectName;
            MeshName = meshName;
            VertexCount = vertexCount;
            TriangleIndexCount = triangleIndexCount;
        }

        public string ObjectName { get; }

        public string MeshName { get; }

        public int VertexCount { get; }

        public int TriangleIndexCount { get; }
    }

    private VtolMeshTerrainSource(
        VTMapManager mapManager,
        TacviewMeshTerrainGrid grid,
        TacviewAkutanRaster resolvedRaster,
        IReadOnlyList<RuntimeMeshCollider> runtimeMeshes)
    {
        _mapManager = mapManager;
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        _resolvedRaster = resolvedRaster ?? throw new ArgumentNullException(nameof(resolvedRaster));
        _runtimeMeshes = runtimeMeshes;
        SourceSignature = VtolTerrainSourceSignatures.BuildMesh(grid, runtimeMeshes);
    }

    public string SourceSignature { get; }

    public string Description =>
        "verified Akutan multi-resolution MeshCollider heightfield";

    public int SourceWidth => _grid.SourceWidth;

    public int SourceHeight => _grid.SourceHeight;

    public int ExportWidth => _grid.ExportWidth;

    public int ExportHeight => _grid.ExportHeight;

    public double MinGlobalX => _grid.MinX;

    public double MinGlobalZ => _grid.MinZ;

    public double MaxGlobalX => _grid.MaxX;

    public double MaxGlobalZ => _grid.MaxZ;

    public bool IsAvailable
    {
        get
        {
            if (_mapManager == null || VTMapManager.fetch != _mapManager ||
                !IsVerifiedAkutanMap(_mapManager))
            {
                return false;
            }

            for (int index = 0; index < _runtimeMeshes.Count; index++)
            {
                if (!_runtimeMeshes[index].IsStillUsable)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Inspects only the closed stock family metadata and authored
    /// GridPlatoon state. This deliberately avoids vertex/index snapshots so
    /// the lifecycle can memoize an unchanged failed geometry preparation.
    /// </summary>
    internal static bool TryGetReadinessFingerprint(
        VTMapManager mapManager,
        out string fingerprint,
        out string error)
    {
        fingerprint = null;
        error = null;
        if (!IsVerifiedAkutanMap(mapManager))
        {
            error = "The closed MeshCollider terrain source supports only the stock map_akutan runtime.";
            return false;
        }

        try
        {
            List<RuntimeMeshCollider> discovered = DiscoverVerifiedTerrainMeshes(out string discoveryError);
            if (!HasEachVerifiedMeshExactlyOnce(discovered))
            {
                error = discoveryError ??
                    "The complete closed Akutan terrain family is not ready for metadata fingerprinting.";
                return false;
            }

            if (!TrySelectVerifiedTerrainFamily(
                    discovered,
                    out List<RuntimeMeshCollider> selected,
                    out string selectionError))
            {
                error = selectionError;
                return false;
            }

            fingerprint = BuildReadinessFingerprint(mapManager, selected);
            return true;
        }
        catch (Exception ex)
        {
            error = "MeshCollider terrain readiness inspection failed: " +
                ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public static bool TryCreate(
        VTMapManager mapManager,
        out VtolMeshTerrainSource source,
        out string error)
    {
        source = null;
        error = null;
        if (mapManager == null)
        {
            error = "VTMapManager.fetch is unavailable for MeshCollider coordinate conversion.";
            return false;
        }

        if (!IsVerifiedAkutanMap(mapManager))
        {
            error = "The closed MeshCollider terrain source supports only the stock map_akutan runtime.";
            return false;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            long managedBefore = GC.GetTotalMemory(false);
            List<RuntimeMeshCollider> discovered = DiscoverVerifiedTerrainMeshes(out string discoveryError);
            if (discovered.Count == 0)
            {
                error = discoveryError ??
                    "No active verified Akutan MeshCollider terrain surface is loaded.";
                return false;
            }

            if (!HasEachVerifiedMeshExactlyOnce(discovered))
            {
                error = discoveryError ??
                    "The loaded Akutan MeshCollider terrain family is not the complete reviewed 35-mesh set.";
                return false;
            }

            if (!TrySelectVerifiedTerrainFamily(
                    discovered,
                    out List<RuntimeMeshCollider> selected,
                    out string selectionError))
            {
                error = selectionError;
                return false;
            }

            long managedAfterDiscovery = GC.GetTotalMemory(false);
            selected.Sort(RuntimeMeshCollider.CompareByKey);
            int expectedTriangleCount = 0;
            for (int index = 0; index < selected.Count; index++)
            {
                expectedTriangleCount = checked(
                    expectedTriangleCount + (selected[index].TriangleIndexCount / 3));
            }

            if (expectedTriangleCount > TacviewMeshTerrainGridBuilder.DefaultMaximumTriangleCount)
            {
                throw new InvalidOperationException("The verified terrain mesh exceeds the triangle snapshot cap.");
            }

            var triangles = new List<TacviewMeshTerrainTriangle>(expectedTriangleCount);
            for (int index = 0; index < selected.Count; index++)
            {
                AppendSnapshotTriangles(
                    mapManager,
                    selected[index],
                    triangles,
                    TacviewMeshTerrainGridBuilder.DefaultMaximumTriangleCount);
            }

            long managedAfterSnapshot = GC.GetTotalMemory(false);
            TacviewMeshTerrainGrid grid = TacviewMeshTerrainGridBuilder.BuildOwned(
                triangles,
                MaximumMeshExportDimension);
            // MapId preparation is gated on the complete, reviewed 508x508
            // Akutan topology. Boundary-connected no-hit components are ocean;
            // ambiguity and enclosed holes fail before the source is cached.
            TacviewAkutanRaster resolvedRaster =
                TacviewAkutanRasterResolver.ResolveProduction(grid);
            long managedAfterIndex = GC.GetTotalMemory(false);

            source = new VtolMeshTerrainSource(
                mapManager,
                grid,
                resolvedRaster,
                new ReadOnlyCollection<RuntimeMeshCollider>(selected));
            stopwatch.Stop();
            Log(
                "Tacview Akutan MeshCollider terrain prepared: bindings=" +
                selected.Count.ToString(CultureInfo.InvariantCulture) +
                ", triangles=" + grid.Triangles.Count.ToString(CultureInfo.InvariantCulture) +
                ", index=" + grid.IndexColumns.ToString(CultureInfo.InvariantCulture) + "x" +
                grid.IndexRows.ToString(CultureInfo.InvariantCulture) +
                ", references=" + grid.TriangleReferenceCount.ToString(CultureInfo.InvariantCulture) +
                ", resolvedRaster=" + resolvedRaster.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                resolvedRaster.Height.ToString(CultureInfo.InvariantCulture) +
                ", mainThreadMs=" + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                ", managedApproxDelta(discovery/snapshot/index)=" +
                (managedAfterDiscovery - managedBefore).ToString(CultureInfo.InvariantCulture) + "/" +
                (managedAfterSnapshot - managedBefore).ToString(CultureInfo.InvariantCulture) + "/" +
                (managedAfterIndex - managedBefore).ToString(CultureInfo.InvariantCulture) + ".");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static bool IsVerifiedAkutanMap(VTMapManager mapManager)
    {
        return mapManager != null && mapManager.map != null &&
            string.Equals(mapManager.map.mapID, AkutanMapId, StringComparison.Ordinal);
    }

    public float SampleAltitude(double globalX, double globalZ)
    {
        // VtolTerrainExporter revalidates IsAvailable once per emitted raster
        // row. Do not repeat the 35-GridPlatoon current-placement check for
        // every pixel in that row; the source was fully checked immediately
        // before sampling and this keeps the fixed 508x508 export bounded.
        if (_mapManager == null || VTMapManager.fetch != _mapManager ||
            !IsVerifiedAkutanMap(_mapManager))
        {
            throw new InvalidOperationException("The MeshCollider terrain source became unavailable.");
        }

        if (!TacviewTerrainDefinition.IsFinite(globalX) ||
            !TacviewTerrainDefinition.IsFinite(globalZ) ||
            globalX < _grid.MinX || globalX > _grid.MaxX ||
            globalZ < _grid.MinZ || globalZ > _grid.MaxZ)
        {
            throw new InvalidOperationException(
                "The requested Akutan terrain sample lies outside the resolved raster.");
        }

        double columnPosition = ((globalX - _grid.MinX) / _grid.WidthMeters) *
            (_resolvedRaster.Width - 1);
        // RAW row zero is north/max-Z, hence the inverted Z normalization.
        double rowPosition = ((_grid.MaxZ - globalZ) / _grid.HeightMeters) *
            (_resolvedRaster.Height - 1);
        int left = Math.Max(0, Math.Min(_resolvedRaster.Width - 1, (int)Math.Floor(columnPosition)));
        int top = Math.Max(0, Math.Min(_resolvedRaster.Height - 1, (int)Math.Floor(rowPosition)));
        int right = Math.Min(_resolvedRaster.Width - 1, left + 1);
        int bottom = Math.Min(_resolvedRaster.Height - 1, top + 1);
        double xFraction = columnPosition - left;
        double rowFraction = rowPosition - top;
        double topAltitude = Lerp(
            _resolvedRaster.GetAltitude(top, left),
            _resolvedRaster.GetAltitude(top, right),
            xFraction);
        double bottomAltitude = Lerp(
            _resolvedRaster.GetAltitude(bottom, left),
            _resolvedRaster.GetAltitude(bottom, right),
            xFraction);
        return (float)Lerp(topAltitude, bottomAltitude, rowFraction);
    }

    internal float SampleResolvedRasterAltitude(int row, int column)
    {
        return (float)_resolvedRaster.GetAltitude(row, column);
    }

    internal double[] SampleResolvedFingerprint(int samplesPerAxis)
    {
        if (samplesPerAxis < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(samplesPerAxis));
        }

        var samples = new double[checked(samplesPerAxis * samplesPerAxis)];
        int sampleIndex = 0;
        for (int row = 0; row < samplesPerAxis; row++)
        {
            double globalZ = _grid.MaxZ -
                (_grid.HeightMeters * row / (samplesPerAxis - 1.0));
            for (int column = 0; column < samplesPerAxis; column++)
            {
                double globalX = _grid.MinX +
                    (_grid.WidthMeters * column / (samplesPerAxis - 1.0));
                samples[sampleIndex++] = SampleAltitude(globalX, globalZ);
            }
        }

        return samples;
    }

    private static double Lerp(double minimum, double maximum, double normalized)
    {
        return minimum + ((maximum - minimum) * normalized);
    }

    private static List<RuntimeMeshCollider> DiscoverVerifiedTerrainMeshes(out string error)
    {
        error = null;
        MeshCollider[] colliders = Resources.FindObjectsOfTypeAll<MeshCollider>();
        var candidates = new List<RuntimeMeshCollider>();
        int matchingFamilyCount = 0;
        int inactiveOrUnusableCount = 0;

        for (int index = 0; index < colliders.Length; index++)
        {
            MeshCollider collider = colliders[index];
            if (!TryGetVerifiedMeshDefinition(
                    collider,
                    out int definitionIndex,
                    out VerifiedMeshDefinition definition))
            {
                continue;
            }

            matchingFamilyCount++;
            if (!RuntimeMeshCollider.TryCreate(
                    collider,
                    definitionIndex,
                    definition,
                    out RuntimeMeshCollider candidate))
            {
                inactiveOrUnusableCount++;
                continue;
            }

            candidates.Add(candidate);
        }

        if (!HasEachVerifiedMeshExactlyOnce(candidates))
        {
            error = matchingFamilyCount == 0
                ? "No MeshCollider matches the installed closed Akutan terrain family."
                : "The loaded Akutan terrain family is incomplete or unusable: expected " +
                    VerifiedAkutanMeshes.Length.ToString(CultureInfo.InvariantCulture) +
                    " exact collider/mesh bindings, found " +
                    candidates.Count.ToString(CultureInfo.InvariantCulture) +
                    " usable and " + inactiveOrUnusableCount.ToString(CultureInfo.InvariantCulture) +
                    " unusable binding(s).";
        }

        return candidates;
    }

    private static bool HasEachVerifiedMeshExactlyOnce(
        IReadOnlyList<RuntimeMeshCollider> candidates)
    {
        if (candidates.Count != VerifiedAkutanMeshes.Length)
        {
            return false;
        }

        var found = new bool[VerifiedAkutanMeshes.Length];
        for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            int definitionIndex = candidates[candidateIndex].DefinitionIndex;
            if (definitionIndex < 0 || definitionIndex >= found.Length || found[definitionIndex])
            {
                return false;
            }

            found[definitionIndex] = true;
        }

        for (int definitionIndex = 0; definitionIndex < found.Length; definitionIndex++)
        {
            if (!found[definitionIndex])
            {
                return false;
            }
        }

        return true;
    }

    private static bool TrySelectVerifiedTerrainFamily(
        IReadOnlyList<RuntimeMeshCollider> discovered,
        out List<RuntimeMeshCollider> selected,
        out string error)
    {
        selected = new List<RuntimeMeshCollider>(discovered.Count);
        error = null;
        var firstMeshByGridPlatoon = new Dictionary<int, RuntimeMeshCollider>();
        var firstMeshByGridCoordinate = new Dictionary<string, RuntimeMeshCollider>(StringComparer.Ordinal);

        for (int index = 0; index < discovered.Count; index++)
        {
            RuntimeMeshCollider candidate = discovered[index];
            if (candidate.NearestLodGroup != null)
            {
                error = "A closed Akutan terrain binding is under an LODGroup; its full-map selection is not proven.";
                return false;
            }

            if (!candidate.IsRuntimeSelected)
            {
                error = "A closed Akutan terrain binding is not active after GridPlatoon startup.";
                return false;
            }

            GridPlatoon gridPlatoon = candidate.GridPlatoon;
            if (gridPlatoon == null || !gridPlatoon.remainActive || gridPlatoon.releaseOnSpawn)
            {
                error = "A closed Akutan terrain binding does not retain its reviewed GridPlatoon configuration.";
                return false;
            }

            int gridPlatoonId = gridPlatoon.GetInstanceID();
            if (firstMeshByGridPlatoon.TryGetValue(gridPlatoonId, out RuntimeMeshCollider firstAtParent))
            {
                if (!IsApprovedSharedGridPlatoonPair(firstAtParent.DefinitionIndex, candidate.DefinitionIndex))
                {
                    error = "The closed Akutan terrain family has an unreviewed shared GridPlatoon parent.";
                    return false;
                }
            }
            else
            {
                firstMeshByGridPlatoon.Add(gridPlatoonId, candidate);
            }

            string gridCoordinate = gridPlatoon.spawnInGrid.x.ToString(CultureInfo.InvariantCulture) + "," +
                gridPlatoon.spawnInGrid.y.ToString(CultureInfo.InvariantCulture);
            if (firstMeshByGridCoordinate.TryGetValue(gridCoordinate, out RuntimeMeshCollider firstAtCoordinate))
            {
                if (firstAtCoordinate.GridPlatoon != gridPlatoon ||
                    !IsApprovedSharedGridPlatoonPair(firstAtCoordinate.DefinitionIndex, candidate.DefinitionIndex))
                {
                    error = "The closed Akutan terrain family has an unreviewed duplicate GridPlatoon world coordinate.";
                    return false;
                }
            }
            else
            {
                firstMeshByGridCoordinate.Add(gridCoordinate, candidate);
            }

            // All 35 reviewed Akutan tiles use remainActive=true.  Requiring
            // their active collider/renderer state proves the full map family
            // is live before TransformPoint snapshots its game-owned position.
            selected.Add(candidate);
        }

        if (selected.Count != VerifiedAkutanMeshes.Length)
        {
            error = "The complete closed Akutan terrain family is not ready for snapshotting.";
            return false;
        }

        if (firstMeshByGridPlatoon.Count != ExpectedAkutanGridPlatoonCount ||
            firstMeshByGridCoordinate.Count != ExpectedAkutanGridPlatoonCount)
        {
            error = "The closed Akutan terrain family does not retain the reviewed 35-mesh/34-GridPlatoon layout.";
            return false;
        }

        return true;
    }

    private static bool IsApprovedSharedGridPlatoonPair(int firstDefinitionIndex, int secondDefinitionIndex)
    {
        // Installed stock Akutan: the nested multiRes_terrain.037 / asset 25
        // is a child of multiRes_terrain.029 / asset 20 under the same
        // AutoGridPlatoon (69,69). It is a real disjoint terrain tile, not a
        // generic hierarchy/LOD exception.
        return (firstDefinitionIndex == 19 && secondDefinitionIndex == 24) ||
            (firstDefinitionIndex == 24 && secondDefinitionIndex == 19);
    }

    private static string BuildReadinessFingerprint(
        VTMapManager mapManager,
        List<RuntimeMeshCollider> selected)
    {
        if (!IsVerifiedAkutanMap(mapManager) || selected == null ||
            selected.Count != VerifiedAkutanMeshes.Length)
        {
            throw new InvalidOperationException(
                "Cannot fingerprint an incomplete or non-Akutan MeshCollider family.");
        }

        selected.Sort(RuntimeMeshCollider.CompareByDefinitionThenKey);
        var builder = new StringBuilder(8192);
        builder.Append("akutan-mesh-readiness-v2");
        AppendReadinessPart(builder, mapManager.map.mapID);
        AppendReadinessPart(builder, selected.Count.ToString(CultureInfo.InvariantCulture));

        var emittedGridPlatoons = new HashSet<int>();
        int gridPlatoonCount = 0;
        for (int index = 0; index < selected.Count; index++)
        {
            RuntimeMeshCollider mesh = selected[index];
            GridPlatoon gridPlatoon = mesh.GridPlatoon;
            if (gridPlatoon == null)
            {
                throw new InvalidOperationException(
                    "A selected MeshCollider terrain binding lost its GridPlatoon.");
            }

            AppendReadinessPart(builder, mesh.DefinitionIndex.ToString(CultureInfo.InvariantCulture));
            AppendReadinessPart(builder, mesh.Collider.GetInstanceID().ToString(CultureInfo.InvariantCulture));
            AppendReadinessPart(builder, mesh.Mesh.GetInstanceID().ToString(CultureInfo.InvariantCulture));
            AppendReadinessPart(builder, mesh.VertexCount.ToString(CultureInfo.InvariantCulture));
            AppendReadinessPart(builder, mesh.SubMeshCount.ToString(CultureInfo.InvariantCulture));
            AppendReadinessPart(builder, mesh.TriangleIndexCount.ToString(CultureInfo.InvariantCulture));
            AppendReadinessPart(builder, mesh.Collider.enabled ? "1" : "0");
            AppendReadinessPart(builder, mesh.Renderer.enabled ? "1" : "0");
            AppendReadinessPart(builder, mesh.Collider.gameObject.activeInHierarchy ? "1" : "0");

            AppendReadinessPart(builder, gridPlatoon.GetInstanceID().ToString(CultureInfo.InvariantCulture));
            AppendReadinessPart(builder, gridPlatoon.spawnInGrid.x.ToString(CultureInfo.InvariantCulture));
            AppendReadinessPart(builder, gridPlatoon.spawnInGrid.y.ToString(CultureInfo.InvariantCulture));
            AppendReadinessPart(builder, gridPlatoon.remainActive ? "1" : "0");
            AppendReadinessPart(builder, gridPlatoon.releaseOnSpawn ? "1" : "0");
            AppendRelativeGridPlatoonPosition(builder, gridPlatoon);
            AppendReadinessPart(builder, mesh.GridOrientationSignature);
            AppendReadinessPart(builder, mesh.TransformSignature);

            if (emittedGridPlatoons.Add(gridPlatoon.GetInstanceID()))
            {
                gridPlatoonCount++;
            }
        }

        if (gridPlatoonCount != ExpectedAkutanGridPlatoonCount)
        {
            throw new InvalidOperationException(
                "The selected MeshCollider family has an unexpected GridPlatoon count.");
        }

        AppendReadinessPart(builder, gridPlatoonCount.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static void AppendRelativeGridPlatoonPosition(StringBuilder builder, GridPlatoon gridPlatoon)
    {
        LevelBuilder levelBuilder = LevelBuilder.fetch;
        if (gridPlatoon == null || levelBuilder == null)
        {
            throw new InvalidOperationException(
                "The GridPlatoon position cannot be fingerprinted before LevelBuilder is ready.");
        }

        Vector3 offset = gridPlatoon.transform.position -
            levelBuilder.GridToPosition(gridPlatoon.spawnInGrid);
        if (offset.sqrMagnitude >
            GridPlatoonPositionToleranceMeters * GridPlatoonPositionToleranceMeters)
        {
            throw new InvalidOperationException(
                "A GridPlatoon no longer matches its reviewed authored placement.");
        }

        // Do not encode raw world-space floats: both the authored placement
        // and GridToPosition move under Floating Origin. The invariant fact is
        // that their relative placement remains inside the reviewed tolerance.
        AppendReadinessPart(builder, "confirmed");
    }

    private static void AppendReadinessPart(StringBuilder builder, string value)
    {
        string normalized = value ?? string.Empty;
        builder.Append('|');
        builder.Append(normalized.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(normalized);
    }

    private static void AppendSnapshotTriangles(
        VTMapManager mapManager,
        RuntimeMeshCollider runtimeMesh,
        ICollection<TacviewMeshTerrainTriangle> destination,
        int maximumTriangleCount)
    {
        Mesh mesh = runtimeMesh.Mesh;
        if (mesh == null || !mesh.isReadable)
        {
            throw new InvalidOperationException("A selected MeshCollider terrain mesh is no longer readable.");
        }

        Vector3[] localVertices = mesh.vertices;
        int[] indexes = mesh.triangles;
        if (localVertices == null || localVertices.Length < 3 ||
            indexes == null || indexes.Length < 3 || indexes.Length % 3 != 0)
        {
            throw new InvalidOperationException("A selected MeshCollider terrain mesh has invalid vertex or triangle data.");
        }

        if (indexes.Length != runtimeMesh.TriangleIndexCount)
        {
            throw new InvalidOperationException("A selected MeshCollider terrain mesh changed its triangle metadata during snapshotting.");
        }

        var transformed = new TacviewMeshTerrainPoint[localVertices.Length];
        Transform transform = runtimeMesh.Collider.transform;
        for (int vertexIndex = 0; vertexIndex < localVertices.Length; vertexIndex++)
        {
            Vector3 worldPoint = transform.TransformPoint(localVertices[vertexIndex]);
            Vector3D globalPoint = VTMapManager.WorldToGlobalPoint(worldPoint);
            Vector3D gpsPoint = mapManager.WorldPositionToGPSCoords(worldPoint);
            transformed[vertexIndex] = new TacviewMeshTerrainPoint(
                globalPoint.x,
                globalPoint.z,
                gpsPoint.z);
        }

        for (int index = 0; index < indexes.Length; index += 3)
        {
            if (destination.Count >= maximumTriangleCount)
            {
                throw new InvalidOperationException("The verified terrain mesh exceeds the triangle snapshot cap.");
            }

            int first = indexes[index];
            int second = indexes[index + 1];
            int third = indexes[index + 2];
            if (first < 0 || first >= transformed.Length ||
                second < 0 || second >= transformed.Length ||
                third < 0 || third >= transformed.Length)
            {
                throw new InvalidOperationException("A selected MeshCollider terrain mesh has an out-of-range triangle index.");
            }

            destination.Add(new TacviewMeshTerrainTriangle(
                transformed[first],
                transformed[second],
                transformed[third]));
        }
    }

    private static bool TryGetVerifiedMeshDefinition(
        MeshCollider collider,
        out int definitionIndex,
        out VerifiedMeshDefinition definition)
    {
        definitionIndex = -1;
        definition = default;
        if (collider == null || collider.gameObject == null ||
            !collider.gameObject.scene.IsValid() || !collider.gameObject.scene.isLoaded)
        {
            return false;
        }

        Mesh mesh = collider.sharedMesh;
        if (mesh == null || !HasVerifiedTerrainAncestors(collider.transform))
        {
            return false;
        }

        for (int index = 0; index < VerifiedAkutanMeshes.Length; index++)
        {
            VerifiedMeshDefinition candidate = VerifiedAkutanMeshes[index];
            if (string.Equals(collider.gameObject.name, candidate.ObjectName, StringComparison.Ordinal) &&
                string.Equals(mesh.name, candidate.MeshName, StringComparison.Ordinal))
            {
                definitionIndex = index;
                definition = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool HasVerifiedTerrainAncestors(Transform transform)
    {
        bool foundPlatoons = false;
        for (Transform current = transform; current != null; current = current.parent)
        {
            if (string.Equals(current.name, TerrainContainerName, StringComparison.Ordinal))
            {
                foundPlatoons = true;
                continue;
            }

            if (foundPlatoons &&
                string.Equals(current.name, TerrainRootName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal sealed class RuntimeMeshCollider
    {
        private readonly int _definitionIndex;
        private readonly int _vertexCount;
        private readonly int _subMeshCount;
        private readonly int _triangleIndexCount;
        private readonly Bounds _meshBounds;

        private RuntimeMeshCollider(
            MeshCollider collider,
            MeshFilter meshFilter,
            MeshRenderer renderer,
            Mesh mesh,
            string key,
            LODGroup nearestLodGroup,
            GridPlatoon gridPlatoon,
            int definitionIndex,
            int triangleIndexCount)
        {
            Collider = collider;
            MeshFilter = meshFilter;
            Renderer = renderer;
            Mesh = mesh;
            Key = key;
            NearestLodGroup = nearestLodGroup;
            GridPlatoon = gridPlatoon;
            _definitionIndex = definitionIndex;
            _vertexCount = mesh.vertexCount;
            _subMeshCount = mesh.subMeshCount;
            _triangleIndexCount = triangleIndexCount;
            _meshBounds = mesh.bounds;
            TransformSignature = BuildRelativeGridPlatoonTransformSignature(
                collider.transform,
                gridPlatoon.transform);
            GridOrientationSignature = BuildGridPlatoonOrientationSignature(
                gridPlatoon.transform);
        }

        public MeshCollider Collider { get; }

        public MeshFilter MeshFilter { get; }

        public MeshRenderer Renderer { get; }

        public Mesh Mesh { get; }

        public string Key { get; }

        public LODGroup NearestLodGroup { get; }

        public GridPlatoon GridPlatoon { get; }

        public int DefinitionIndex => _definitionIndex;

        public string TransformSignature { get; }

        public string GridOrientationSignature { get; }

        public int VertexCount => _vertexCount;

        public int SubMeshCount => _subMeshCount;

        public int TriangleIndexCount => _triangleIndexCount;

        public Bounds MeshBounds => _meshBounds;

        public bool IsRuntimeSelected =>
            Collider != null && Collider.enabled && Collider.gameObject.activeInHierarchy &&
            Renderer != null && Renderer.enabled && Renderer.gameObject.activeInHierarchy;

        // The source stores global/GPS coordinates, so a normal floating-origin
        // move does not invalidate its snapshot.  It must nevertheless verify
        // the current authored GridPlatoon placement through LevelBuilder: a
        // cached source from before GridPlatoon.Start (or a later map change)
        // would otherwise retain stale transformed vertices.
        public bool IsStillUsable
        {
            get
            {
                if (Collider == null || MeshFilter == null || Renderer == null || Mesh == null ||
                    GridPlatoon == null || Collider.convex ||
                    !GridPlatoon.remainActive || GridPlatoon.releaseOnSpawn ||
                    Collider.sharedMesh != Mesh || MeshFilter.sharedMesh != Mesh ||
                    !Mesh.isReadable || Mesh.vertexCount != _vertexCount ||
                    Mesh.subMeshCount != _subMeshCount ||
                    !Collider.gameObject.scene.IsValid() || !Collider.gameObject.scene.isLoaded ||
                    !IsRuntimeSelected || Collider.GetComponentInParent<LODGroup>() != null ||
                    !TryGetTriangleIndexCount(Mesh, out int triangleIndexCount) ||
                    triangleIndexCount != _triangleIndexCount ||
                    !TryGetOnlyGridPlatoon(Collider.transform, out GridPlatoon currentGridPlatoon) ||
                    currentGridPlatoon != GridPlatoon ||
                    !IsGridPlatoonPositionConfirmed(GridPlatoon) ||
                    !string.Equals(
                        TransformSignature,
                        BuildRelativeGridPlatoonTransformSignature(
                            Collider.transform,
                            GridPlatoon.transform),
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        GridOrientationSignature,
                        BuildGridPlatoonOrientationSignature(GridPlatoon.transform),
                        StringComparison.Ordinal))
                {
                    return false;
                }

                return true;
            }
        }

        internal static bool TryCreate(
            MeshCollider collider,
            int definitionIndex,
            VerifiedMeshDefinition definition,
            out RuntimeMeshCollider candidate)
        {
            candidate = null;
            if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy ||
                collider.convex)
            {
                return false;
            }

            Mesh mesh = collider.sharedMesh;
            MeshFilter meshFilter = collider.GetComponent<MeshFilter>();
            MeshRenderer renderer = collider.GetComponent<MeshRenderer>();
            if (mesh == null || !mesh.isReadable || meshFilter == null ||
                meshFilter.sharedMesh != mesh || renderer == null)
            {
                return false;
            }

            if (mesh.vertexCount != definition.VertexCount ||
                !TryGetTriangleIndexCount(mesh, out int triangleIndexCount) ||
                triangleIndexCount != definition.TriangleIndexCount ||
                !TryGetOnlyGridPlatoon(collider.transform, out GridPlatoon gridPlatoon) ||
                gridPlatoon.releaseOnSpawn || !gridPlatoon.remainActive ||
                !IsGridPlatoonPositionConfirmed(gridPlatoon))
            {
                return false;
            }

            candidate = new RuntimeMeshCollider(
                collider,
                meshFilter,
                renderer,
                mesh,
                BuildHierarchyKey(collider.transform),
                collider.GetComponentInParent<LODGroup>(),
                gridPlatoon,
                definitionIndex,
                triangleIndexCount);
            return true;
        }

        private static bool TryGetTriangleIndexCount(Mesh mesh, out int triangleIndexCount)
        {
            triangleIndexCount = 0;
            if (mesh == null || mesh.subMeshCount < 1)
            {
                return false;
            }

            long total = 0;
            for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
            {
                if (mesh.GetTopology(subMeshIndex) != MeshTopology.Triangles)
                {
                    return false;
                }

                total += mesh.GetIndexCount(subMeshIndex);
                if (total > int.MaxValue || total % 3 != 0)
                {
                    return false;
                }
            }

            triangleIndexCount = (int)total;
            return triangleIndexCount >= 3;
        }

        private static bool TryGetOnlyGridPlatoon(
            Transform transform,
            out GridPlatoon gridPlatoon)
        {
            gridPlatoon = null;
            for (Transform current = transform; current != null; current = current.parent)
            {
                GridPlatoon candidate = current.GetComponent<GridPlatoon>();
                if (candidate == null)
                {
                    continue;
                }

                if (gridPlatoon != null)
                {
                    return false;
                }

                gridPlatoon = candidate;
            }

            return gridPlatoon != null;
        }

        private static bool IsGridPlatoonPositionConfirmed(GridPlatoon gridPlatoon)
        {
            LevelBuilder levelBuilder = LevelBuilder.fetch;
            if (gridPlatoon == null || levelBuilder == null)
            {
                return false;
            }

            Vector3 expectedPosition = levelBuilder.GridToPosition(gridPlatoon.spawnInGrid);
            Vector3 offset = gridPlatoon.transform.position - expectedPosition;
            return offset.sqrMagnitude <=
                GridPlatoonPositionToleranceMeters * GridPlatoonPositionToleranceMeters;
        }

        public static int CompareByKey(RuntimeMeshCollider left, RuntimeMeshCollider right)
        {
            return string.Compare(left.Key, right.Key, StringComparison.Ordinal);
        }

        public static int CompareByDefinitionThenKey(RuntimeMeshCollider left, RuntimeMeshCollider right)
        {
            int comparison = left.DefinitionIndex.CompareTo(right.DefinitionIndex);
            return comparison != 0 ? comparison : CompareByKey(left, right);
        }

        private static string BuildHierarchyKey(Transform transform)
        {
            var segments = new List<string>();
            for (Transform current = transform; current != null; current = current.parent)
            {
                segments.Add((current.name ?? string.Empty) + "#" +
                    current.GetSiblingIndex().ToString(CultureInfo.InvariantCulture));
            }

            segments.Reverse();
            return transform.gameObject.scene.name + "/" + string.Join("/", segments);
        }

        private static string BuildRelativeGridPlatoonTransformSignature(
            Transform transform,
            Transform gridPlatoonTransform)
        {
            if (transform == null || gridPlatoonTransform == null)
            {
                throw new InvalidOperationException(
                    "A terrain binding lost its collider-to-GridPlatoon transform chain.");
            }

            var builder = new StringBuilder(384);
            Transform current = transform;
            int depth = 0;
            while (current != gridPlatoonTransform)
            {
                if (current == null || depth++ > 64)
                {
                    throw new InvalidOperationException(
                        "A terrain binding is no longer below its reviewed GridPlatoon.");
                }

                Vector3 position = current.localPosition;
                Quaternion rotation = current.localRotation;
                Vector3 scale = current.localScale;
                builder.Append(current.name ?? string.Empty).Append('#')
                    .Append(current.GetSiblingIndex().ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(position.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(position.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(position.z.ToString("R", CultureInfo.InvariantCulture)).Append(';')
                    .Append(rotation.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(rotation.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(rotation.z.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(rotation.w.ToString("R", CultureInfo.InvariantCulture)).Append(';')
                    .Append(scale.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(scale.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(scale.z.ToString("R", CultureInfo.InvariantCulture)).Append('|');
                current = current.parent;
            }

            return builder.Length == 0 ? "identity" : builder.ToString();
        }

        private static string BuildGridPlatoonOrientationSignature(Transform transform)
        {
            if (transform == null)
            {
                throw new InvalidOperationException("A terrain GridPlatoon lost its Transform.");
            }

            // Floating Origin changes translation only. World rotation and
            // effective scale are therefore safe invariants for the remaining
            // part of TransformPoint that GridToPosition does not validate.
            Quaternion rotation = transform.rotation;
            Vector3 scale = transform.lossyScale;
            return rotation.x.ToString("R", CultureInfo.InvariantCulture) + "," +
                rotation.y.ToString("R", CultureInfo.InvariantCulture) + "," +
                rotation.z.ToString("R", CultureInfo.InvariantCulture) + "," +
                rotation.w.ToString("R", CultureInfo.InvariantCulture) + ";" +
                scale.x.ToString("R", CultureInfo.InvariantCulture) + "," +
                scale.y.ToString("R", CultureInfo.InvariantCulture) + "," +
                scale.z.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}

internal static class VtolTerrainSourceSignatures
{
    public static string BuildGenerated(VTMapGenerator generator, VTMap map)
    {
        var signature = new StringBuilder(512);
        signature.Append("generated-v1");
        AppendPart(signature, generator.mapType.ToString());
        AppendPart(signature, generator.edgeMode.ToString());
        AppendPart(signature, generator.coastSide.ToString());
        AppendPart(signature, generator.gridSize.ToString(CultureInfo.InvariantCulture));
        AppendPart(signature, generator.chunkSize.ToString("R", CultureInfo.InvariantCulture));
        AppendPart(signature, generator.noiseSeed.ToString(CultureInfo.InvariantCulture));

        if (!(map is VTMapCustom customMap))
        {
            return signature.ToString();
        }

        AppendPart(signature, "custom");
        AppendPart(signature, customMap.seed ?? string.Empty);
        AppendPart(signature, customMap.isBuiltin ? "1" : "0");
        AppendPart(signature, customMap.isSteamWorkshopMap ? "1" : "0");
        AppendFileMetadata(signature, customMap.heightmapPath);

        try
        {
            if (!string.IsNullOrWhiteSpace(customMap.mapDir) && Directory.Exists(customMap.mapDir))
            {
                string[] terrainFiles = Directory.GetFiles(
                    customMap.mapDir,
                    "height*",
                    SearchOption.TopDirectoryOnly);
                Array.Sort(terrainFiles, StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < terrainFiles.Length; index++)
                {
                    AppendFileMetadata(signature, terrainFiles[index]);
                }
            }
        }
        catch (Exception ex)
        {
            AppendPart(signature, "metadata-unavailable:" + ex.GetType().Name);
        }

        return signature.ToString();
    }

    public static string BuildStatic(
        TacviewTerrainGrid grid,
        IReadOnlyDictionary<string, VtolStaticTerrainSource.RuntimeTile> tilesByKey)
    {
        var signature = new StringBuilder(1024);
        signature.Append("unity-terrain-v1");
        AppendPart(signature, grid.MinX.ToString("R", CultureInfo.InvariantCulture));
        AppendPart(signature, grid.MinZ.ToString("R", CultureInfo.InvariantCulture));
        AppendPart(signature, grid.MaxX.ToString("R", CultureInfo.InvariantCulture));
        AppendPart(signature, grid.MaxZ.ToString("R", CultureInfo.InvariantCulture));

        for (int index = 0; index < grid.Tiles.Count; index++)
        {
            TacviewTerrainTile tile = grid.Tiles[index];
            VtolStaticTerrainSource.RuntimeTile runtimeTile = tilesByKey[tile.Key];
            TerrainData data = runtimeTile.TerrainData;
            AppendPart(signature, tile.Key);
            AppendPart(signature, data.name ?? string.Empty);
            AppendPart(signature, tile.MinX.ToString("R", CultureInfo.InvariantCulture));
            AppendPart(signature, tile.MinZ.ToString("R", CultureInfo.InvariantCulture));
            AppendPart(signature, tile.MaxX.ToString("R", CultureInfo.InvariantCulture));
            AppendPart(signature, tile.MaxZ.ToString("R", CultureInfo.InvariantCulture));
            AppendPart(signature, tile.SourceWidth.ToString(CultureInfo.InvariantCulture));
            AppendPart(signature, tile.SourceHeight.ToString(CultureInfo.InvariantCulture));
            AppendPart(signature, data.size.y.ToString("R", CultureInfo.InvariantCulture));
        }

        return signature.ToString();
    }

    public static string BuildMesh(
        TacviewMeshTerrainGrid grid,
        IReadOnlyList<VtolMeshTerrainSource.RuntimeMeshCollider> runtimeMeshes)
    {
        if (grid == null)
        {
            throw new ArgumentNullException(nameof(grid));
        }

        if (runtimeMeshes == null || runtimeMeshes.Count == 0)
        {
            throw new ArgumentException("At least one verified terrain mesh is required.", nameof(runtimeMeshes));
        }

        var signature = new StringBuilder(2048);
        signature.Append("mesh-terrain-v1");
        AppendPart(signature, grid.MinX.ToString("R", CultureInfo.InvariantCulture));
        AppendPart(signature, grid.MinZ.ToString("R", CultureInfo.InvariantCulture));
        AppendPart(signature, grid.MaxX.ToString("R", CultureInfo.InvariantCulture));
        AppendPart(signature, grid.MaxZ.ToString("R", CultureInfo.InvariantCulture));
        AppendPart(signature, grid.SourceWidth.ToString(CultureInfo.InvariantCulture));
        AppendPart(signature, grid.SourceHeight.ToString(CultureInfo.InvariantCulture));
        AppendPart(signature, grid.ExportWidth.ToString(CultureInfo.InvariantCulture));
        AppendPart(signature, grid.ExportHeight.ToString(CultureInfo.InvariantCulture));
        AppendPart(signature, grid.Triangles.Count.ToString(CultureInfo.InvariantCulture));
        AppendPart(signature, grid.IndexColumns.ToString(CultureInfo.InvariantCulture));
        AppendPart(signature, grid.IndexRows.ToString(CultureInfo.InvariantCulture));
        AppendPart(signature, grid.TriangleReferenceCount.ToString(CultureInfo.InvariantCulture));

        for (int index = 0; index < runtimeMeshes.Count; index++)
        {
            VtolMeshTerrainSource.RuntimeMeshCollider mesh = runtimeMeshes[index];
            Bounds bounds = mesh.MeshBounds;
            AppendPart(signature, mesh.Key);
            AppendPart(signature, mesh.Mesh.name ?? string.Empty);
            AppendPart(signature, mesh.VertexCount.ToString(CultureInfo.InvariantCulture));
            AppendPart(signature, mesh.SubMeshCount.ToString(CultureInfo.InvariantCulture));
            AppendPart(signature, mesh.TriangleIndexCount.ToString(CultureInfo.InvariantCulture));
            AppendPart(signature, bounds.center.x.ToString("R", CultureInfo.InvariantCulture));
            AppendPart(signature, bounds.center.y.ToString("R", CultureInfo.InvariantCulture));
            AppendPart(signature, bounds.center.z.ToString("R", CultureInfo.InvariantCulture));
            AppendPart(signature, bounds.extents.x.ToString("R", CultureInfo.InvariantCulture));
            AppendPart(signature, bounds.extents.y.ToString("R", CultureInfo.InvariantCulture));
            AppendPart(signature, bounds.extents.z.ToString("R", CultureInfo.InvariantCulture));
            AppendPart(signature, mesh.TransformSignature);
        }

        return signature.ToString();
    }

    private static void AppendFileMetadata(StringBuilder signature, string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return;
            }

            var info = new FileInfo(filePath);
            AppendPart(signature, "file");
            AppendPart(signature, info.Name.ToLowerInvariant());
            AppendPart(signature, info.Length.ToString(CultureInfo.InvariantCulture));
            AppendPart(signature, info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            AppendPart(signature, "file-metadata-unavailable:" + ex.GetType().Name);
        }
    }

    private static void AppendPart(StringBuilder signature, string value)
    {
        string normalized = value ?? string.Empty;
        signature.Append('|');
        signature.Append(normalized.Length.ToString(CultureInfo.InvariantCulture));
        signature.Append(':');
        signature.Append(normalized);
    }
}
