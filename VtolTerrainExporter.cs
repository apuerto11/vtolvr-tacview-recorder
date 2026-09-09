using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using VTOLVRTacviewSPRecorder.Core;

namespace VTOLVRTacviewSPRecorder.Main;

internal sealed class VtolTerrainExporter : IDisposable
{
    private const int FingerprintSamplesPerAxis = 65;
    internal const int MaximumExportDimension = 1025;
    private const int RowsPerFrame = 4;

    private readonly MonoBehaviour _coroutineHost;
    private readonly IVtolTerrainSource _source;
    private readonly TacviewTerrainCache _cache;
    private readonly TacviewTerrainDefinition _definition;
    private IEnumerator _routine;
    private Task<TacviewTerrainCacheProbe> _cacheCheckTask;
    private Task<TacviewTerrainInstallResult> _installTask;
    private bool _terrainReady;
    private bool _disposed;

    private VtolTerrainExporter(
        MonoBehaviour coroutineHost,
        IVtolTerrainSource source,
        TacviewTerrainCache cache,
        TacviewTerrainDefinition definition)
    {
        _coroutineHost = coroutineHost;
        _source = source;
        _cache = cache;
        _definition = definition;
    }

    public string MapId => _definition.TerrainId;

    public string SourceDescription => _source.Description;

    public bool VerifyTerrainReadyOnDisk()
    {
        try
        {
            return _cache.IsCurrent(_definition);
        }
        catch
        {
            return false;
        }
    }

    public bool IsTerrainReady
    {
        get
        {
            if (_terrainReady)
            {
                return true;
            }

            // Observe already completed Core-only tasks without waiting. This closes
            // the narrow gap where the file worker has published terrain but Unity
            // has not resumed this coroutine before the mission ends.
            Task<TacviewTerrainInstallResult> installTask = _installTask;
            if (installTask != null &&
                installTask.Status == TaskStatus.RanToCompletion &&
                IsVerifiedInstallResult(installTask.Result))
            {
                return true;
            }

            Task<TacviewTerrainCacheProbe> cacheCheckTask = _cacheCheckTask;
            if (cacheCheckTask == null ||
                cacheCheckTask.Status != TaskStatus.RanToCompletion ||
                !cacheCheckTask.Result.IsCurrent)
            {
                return false;
            }

            try
            {
                return _cache.IsCurrent(_definition);
            }
            catch
            {
                return false;
            }
        }
    }

    private bool IsVerifiedInstallResult(TacviewTerrainInstallResult result)
    {
        if (result == null || result.Status == TacviewTerrainInstallStatus.Failed)
        {
            return false;
        }

        try
        {
            return File.Exists(result.RawPath) &&
                File.Exists(result.XmlPath) &&
                File.Exists(result.ChecksumPath) &&
                new FileInfo(result.RawPath).Length == _definition.ExpectedRawLength &&
                _cache.IsCurrent(_definition);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryPrepare(
        MonoBehaviour coroutineHost,
        VTMapManager mapManager,
        VTScenario scenario,
        out VtolTerrainExporter exporter,
        out string mapId,
        out string error)
    {
        exporter = null;
        error = null;
        mapId = null;

        try
        {
            if (coroutineHost == null)
            {
                throw new ArgumentNullException(nameof(coroutineHost));
            }

            if (mapManager == null)
            {
                throw new ArgumentNullException(nameof(mapManager));
            }

            string mapIdentity = GetMapIdentity(mapManager, scenario);

            if (!VtolTerrainSourceFactory.TryCreate(mapManager, out IVtolTerrainSource source, out string sourceError))
            {
                throw new InvalidOperationException(sourceError);
            }

            double widthMeters = source.MaxGlobalX - source.MinGlobalX;
            double heightMeters = source.MaxGlobalZ - source.MinGlobalZ;
            double worldSizeMeters = Math.Max(widthMeters, heightMeters);
            if (!TacviewTerrainDefinition.IsFinite(widthMeters) ||
                !TacviewTerrainDefinition.IsFinite(heightMeters) ||
                widthMeters <= 0.0 || heightMeters <= 0.0)
            {
                throw new InvalidOperationException("VTOL terrain source reported invalid global bounds.");
            }

            TacviewTerrainGeoPoint bottomLeft = ToGeoPoint(
                mapManager,
                source.MinGlobalX,
                source.MinGlobalZ);
            TacviewTerrainGeoPoint bottomRight = ToGeoPoint(
                mapManager,
                source.MaxGlobalX,
                source.MinGlobalZ);
            TacviewTerrainGeoPoint topRight = ToGeoPoint(
                mapManager,
                source.MaxGlobalX,
                source.MaxGlobalZ);
            TacviewTerrainGeoPoint topLeft = ToGeoPoint(
                mapManager,
                source.MinGlobalX,
                source.MaxGlobalZ);
            double[] sparseSamples = SampleFingerprint(source);
            GetHeightRange(sparseSamples, out double minimumHeight, out double maximumHeight);

            string fingerprint = TacviewTerrainId.ComputeFingerprint(
                new TacviewTerrainFingerprintInput
                {
                    MapIdentity = mapIdentity,
                    SourceSignature = source.SourceSignature,
                    WorldSizeMeters = worldSizeMeters,
                    SourceWidth = source.SourceWidth,
                    SourceHeight = source.SourceHeight,
                    ExportWidth = source.ExportWidth,
                    ExportHeight = source.ExportHeight,
                    MinimumHeightMeters = minimumHeight,
                    MaximumHeightMeters = maximumHeight,
                    BottomLeft = bottomLeft,
                    BottomRight = bottomRight,
                    TopRight = topRight,
                    TopLeft = topLeft,
                    SparseHeightSamples = sparseSamples
                });

            mapId = TacviewTerrainId.BuildVersioned(mapIdentity, fingerprint);
            var definition = new TacviewTerrainDefinition(
                mapId,
                source.ExportWidth,
                source.ExportHeight,
                bottomLeft,
                bottomRight,
                topRight,
                topLeft);
            string applicationData = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(applicationData) || !Path.IsPathRooted(applicationData))
            {
                throw new InvalidOperationException("Windows did not provide a safe per-user AppData path.");
            }

            string terrainDirectory = Path.Combine(
                applicationData,
                "Tacview",
                "Data",
                "Terrain",
                "Custom");

            exporter = new VtolTerrainExporter(
                coroutineHost,
                source,
                new TacviewTerrainCache(terrainDirectory),
                definition);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Used only by RecorderLifecycle before the ACMI writer exists. It makes
    /// bounded readiness retries aware of Campaign static terrain without
    /// making an unsupported terrain source block recording indefinitely.
    /// </summary>
    public static bool IsTerrainSourceAvailable(VTMapManager mapManager, out string error)
    {
        return VtolTerrainSourceFactory.IsSourceAvailable(mapManager, out error);
    }

    public void Start()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VtolTerrainExporter));
        }

        if (_routine != null)
        {
            return;
        }

        Log(
            $"Tacview terrain source prepared: {_source.Description} " +
            $"({_definition.Width}x{_definition.Height}, MapId '{_definition.TerrainId}').");
        _routine = ExportRoutine();
        _coroutineHost.StartCoroutine(_routine);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IEnumerator routine = _routine;
        _routine = null;
        if (routine != null)
        {
            _coroutineHost.StopCoroutine(routine);
        }
    }

    private IEnumerator ExportRoutine()
    {
        IEnumerator coreRoutine = ExportRoutineCore();
        try
        {
            while (true)
            {
                bool movedNext = false;
                bool failed = false;
                object current = null;
                try
                {
                    movedNext = coreRoutine.MoveNext();
                    if (movedNext)
                    {
                        current = coreRoutine.Current;
                    }
                }
                catch (Exception ex)
                {
                    failed = true;
                    LogWarn(
                        $"Tacview terrain export failed for '{_definition.TerrainId}': {ex}. " +
                        "ACMI recording continues.");
                }

                if (failed || !movedNext)
                {
                    break;
                }

                yield return current;
            }
        }
        finally
        {
            (coreRoutine as IDisposable)?.Dispose();
            _routine = null;
        }
    }

    private IEnumerator ExportRoutineCore()
    {
        // Let entity recording start first. Cache hashing and file installation run on
        // worker threads; only the public VTOL height API is sampled on Unity's thread.
        yield return null;

        // The actual Task.Run lives in the Unity-free Core assembly boundary.
        // No Unity/VTOL object can be passed into that API.
        Task<TacviewTerrainCacheProbe> cacheCheck =
            TacviewTerrainBackground.CheckCacheAsync(_cache, _definition);
        _cacheCheckTask = cacheCheck;
        while (!cacheCheck.IsCompleted)
        {
            yield return null;
        }

        TacviewTerrainCacheProbe cacheProbe = cacheCheck.GetAwaiter().GetResult();
        if (cacheProbe.IsCurrent)
        {
            if (_cache.IsCurrent(_definition))
            {
                _terrainReady = true;
                Log($"Tacview terrain cache is current: {_definition.TerrainId}");
                yield break;
            }

            LogWarn(
                $"Tacview terrain cache changed after verification for '{_definition.TerrainId}'. " +
                "The companion will attempt a safe rebuild.");
        }

        if (!string.IsNullOrWhiteSpace(cacheProbe.Error))
        {
            LogWarn(
                $"Tacview terrain cache check could not be completed for '{_definition.TerrainId}': " +
                $"{cacheProbe.Error}. The companion will attempt a safe rebuild.");
        }

        var heights = new short[checked(_definition.Width * _definition.Height)];
        VtolMeshTerrainSource meshTerrainSource = _source as VtolMeshTerrainSource;
        for (int row = 0; row < _definition.Height; row++)
        {
            if (_disposed || !_source.IsAvailable)
            {
                yield break;
            }

            double globalZ = Lerp(
                _source.MinGlobalZ,
                _source.MaxGlobalZ,
                TacviewTerrainRaster.NormalizedZForRawRow(row, _definition.Height));
            int rowOffset = row * _definition.Width;
            for (int column = 0; column < _definition.Width; column++)
            {
                double globalX = Lerp(
                    _source.MinGlobalX,
                    _source.MaxGlobalX,
                    TacviewTerrainRaster.NormalizedXForColumn(column, _definition.Width));
                float altitude;
                try
                {
                    // Akutan was fully resolved before MapId preparation. Use
                    // the exact same immutable lattice here; generated and
                    // Unity Terrain sources retain their public API sampling.
                    altitude = meshTerrainSource != null
                        ? meshTerrainSource.SampleResolvedRasterAltitude(row, column)
                        : _source.SampleAltitude(globalX, globalZ);
                }
                catch (InvalidOperationException) when (meshTerrainSource != null)
                {
                    throw new InvalidOperationException(
                        "The MeshCollider terrain source failed exact export coverage; its MapId will remain inactive.");
                }
                heights[rowOffset + column] = TacviewTerrainRaster.QuantizeMeters(altitude);
            }

            if ((row + 1) % RowsPerFrame == 0)
            {
                yield return null;
            }
        }

        byte[] rawBytes = TacviewTerrainRaster.EncodeLittleEndian(
            heights,
            _definition.Width,
            _definition.Height);
        // The payload copies the completed byte buffer before crossing into the
        // Core-only background worker. No Unity/VTOL object is captured.
        var payload = new TacviewTerrainPayload(_definition, rawBytes);
        Task<TacviewTerrainInstallResult> installTask =
            TacviewTerrainBackground.InstallAsync(_cache, payload);
        _installTask = installTask;
        while (!installTask.IsCompleted)
        {
            yield return null;
        }

        TacviewTerrainInstallResult result = installTask.GetAwaiter().GetResult();
        if (result.Status == TacviewTerrainInstallStatus.Failed)
        {
            LogWarn(
                $"Tacview terrain export failed for '{_definition.TerrainId}': {result.Error}. " +
                "ACMI recording continues without changing Tacview's installation.");
            yield break;
        }
        if (!_cache.IsCurrent(_definition))
        {
            LogWarn(
                $"Tacview terrain export for '{_definition.TerrainId}' did not pass its final cache verification. " +
                "ACMI recording continues and the MapId will not be activated.");
            yield break;
        }

        _terrainReady = true;

        Log(
            $"Tacview terrain {(result.Status == TacviewTerrainInstallStatus.Installed ? "installed" : "cache verified")}: " +
            $"{_definition.TerrainId} ({_definition.Width}x{_definition.Height}) at '{result.XmlPath}'. " +
            "Restart Tacview before opening this recording.");
    }

    private static double[] SampleFingerprint(IVtolTerrainSource source)
    {
        if (source is VtolMeshTerrainSource meshTerrainSource)
        {
            // Select exact points from the already resolved 508 lattice. This
            // cannot reintroduce raw-mesh gaps or coordinate-rounding drift.
            return meshTerrainSource.SampleResolvedFingerprint(FingerprintSamplesPerAxis);
        }

        var samples = new double[FingerprintSamplesPerAxis * FingerprintSamplesPerAxis];
        int sampleIndex = 0;
        for (int row = 0; row < FingerprintSamplesPerAxis; row++)
        {
            double normalizedZ = TacviewTerrainRaster.NormalizedZForRawRow(
                row,
                FingerprintSamplesPerAxis);
            for (int column = 0; column < FingerprintSamplesPerAxis; column++)
            {
                double normalizedX = TacviewTerrainRaster.NormalizedXForColumn(
                    column,
                    FingerprintSamplesPerAxis);
                samples[sampleIndex++] = source.SampleAltitude(
                    Lerp(source.MinGlobalX, source.MaxGlobalX, normalizedX),
                    Lerp(source.MinGlobalZ, source.MaxGlobalZ, normalizedZ));
            }
        }

        return samples;
    }

    private static void GetHeightRange(double[] samples, out double minimumHeight, out double maximumHeight)
    {
        if (samples == null || samples.Length == 0)
        {
            throw new ArgumentException("At least one terrain sample is required.", nameof(samples));
        }

        minimumHeight = samples[0];
        maximumHeight = samples[0];
        for (int index = 1; index < samples.Length; index++)
        {
            minimumHeight = Math.Min(minimumHeight, samples[index]);
            maximumHeight = Math.Max(maximumHeight, samples[index]);
        }
    }

    private static double Lerp(double minimum, double maximum, double normalized)
    {
        return minimum + ((maximum - minimum) * normalized);
    }

    private static TacviewTerrainGeoPoint ToGeoPoint(
        VTMapManager mapManager,
        double globalX,
        double globalZ)
    {
        Vector3 worldPoint = VTMapManager.GlobalToWorldPoint(
            new Vector3D(globalX, 0.0, globalZ));
        Vector3D gpsPoint = mapManager.WorldPositionToGPSCoords(worldPoint);
        return new TacviewTerrainGeoPoint(gpsPoint.y, gpsPoint.x);
    }

    private static string GetMapIdentity(VTMapManager mapManager, VTScenario scenario)
    {
        VTMap map = mapManager?.map;
        return map == null
            ? VtolActorClassifier.FirstNonEmpty(scenario?.mapID, "map")
            : VtolActorClassifier.FirstNonEmpty(
                map.mapID,
                scenario?.mapID,
                map.mapName,
                map.sceneName,
                "map");
    }

}
