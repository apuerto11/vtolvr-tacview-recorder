using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using VTOLVRTacviewSPRecorder.Core;

namespace VTOLVRTacviewSPRecorder.Main;

/// <summary>
/// Connects the installed VTOL VR countermeasure lifecycle to the pure .NET
/// countermeasure registry. Harmony is limited to the verified physical spawn
/// boundaries; recurring work visits active recorder entries only.
/// </summary>
internal sealed class VtolCountermeasureRegistryAdapter :
    IDisposable,
    ICountermeasureWarningSink
{
    private const string FlareHarmonyId =
        "de.kalag.vtolvr.tacviewsprecorder.countermeasures.0.4.2.flare";
    private const string ParticleChaffHarmonyId =
        "de.kalag.vtolvr.tacviewsprecorder.countermeasures.0.4.2.chaff.particle";
    private const int MaximumNewChaffPerSecond = 64;
    private const double FlareSafetyTailSeconds = 30.0;
    private const double MaximumFlareSafetyLifetimeSeconds = 90.0;
    private const double AdvancedChaffSafetyLifetimeSeconds = 30.0;
    private const double MaximumParticleChaffLifetimeSeconds = 15.0;
    private const double ParticleStartGraceSeconds = 0.05;
    private const double ParticleLivenessIntervalSeconds = 0.2;
    private const double InternalWarningIntervalSeconds = 10.0;

    private static readonly object InstanceGate = new object();
    private static VtolCountermeasureRegistryAdapter _activeInstance;

    [ThreadStatic]
    private static FlareFireContext _currentFlareContext;

    private readonly VTMapManager _mapManager;
    private readonly EntityRegistry _entityRegistry;
    private readonly CountermeasureRegistry _countermeasures;
    private readonly Harmony _flareHarmony;
    private readonly Harmony _particleChaffHarmony;
    private readonly Dictionary<Actor, OwnerRegistration> _owners =
        new Dictionary<Actor, OwnerRegistration>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FlareCountermeasure, Actor> _flareOwners =
        new Dictionary<FlareCountermeasure, Actor>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ChaffCountermeasure, AdvancedChaffEventRegistration>
        _advancedChaffEvents =
            new Dictionary<ChaffCountermeasure, AdvancedChaffEventRegistration>(
                ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ChaffLauncher, LauncherRegistration> _launchers =
        new Dictionary<ChaffLauncher, LauncherRegistration>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CMFlare, FlareGeneration> _flareGenerations =
        new Dictionary<CMFlare, FlareGeneration>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<AdvancedSlotKey, AdvancedChaffGeneration>
        _advancedChaffGenerations =
            new Dictionary<AdvancedSlotKey, AdvancedChaffGeneration>();
    private readonly List<ParticleChaffGeneration> _particleChaffGenerations =
        new List<ParticleChaffGeneration>();
    private readonly List<CMFlare> _flaresToRemove = new List<CMFlare>();
    private readonly List<AdvancedSlotKey> _advancedSlotsToRemove =
        new List<AdvancedSlotKey>();
    private double _chaffWindowStartSeconds = double.NegativeInfinity;
    private int _chaffReleasesInWindow;
    private double _nextInternalWarningTime;
    private bool _flarePatchesInstalled;
    private bool _particleChaffPatchesInstalled;
    private bool _advancedChaffCaptureObserved;
    private bool _disposed;

    public VtolCountermeasureRegistryAdapter(
        VTMapManager mapManager,
        EntityRegistry entityRegistry)
    {
        _mapManager = mapManager ?? throw new ArgumentNullException(nameof(mapManager));
        _entityRegistry = entityRegistry ?? throw new ArgumentNullException(nameof(entityRegistry));
        _countermeasures = new CountermeasureRegistry(
            entityRegistry,
            warningThrottle: new WarningThrottle(InternalWarningIntervalSeconds),
            warningSink: this);
        _flareHarmony = new Harmony(FlareHarmonyId);
        _particleChaffHarmony = new Harmony(ParticleChaffHarmonyId);
        InstallPatches();
    }

    public int ActiveCountermeasureCount => _countermeasures.ActiveCountermeasureCount;

    public bool HasPendingLifecycleData => _countermeasures.HasPendingLifecycleData;

    public void TrackOwner(Actor owner)
    {
        if (_disposed || ReferenceEquals(owner, null) || owner == null)
        {
            return;
        }

        try
        {
            UntrackOwner(owner);

            FlareCountermeasure[] flares = _flarePatchesInstalled
                ? owner.GetComponentsInChildren<FlareCountermeasure>(true)
                : Array.Empty<FlareCountermeasure>();
            ChaffCountermeasure[] chaffManagers =
                owner.GetComponentsInChildren<ChaffCountermeasure>(true);
            ChaffLauncher[] chaffLaunchers = _particleChaffPatchesInstalled
                ? owner.GetComponentsInChildren<ChaffLauncher>(true)
                : Array.Empty<ChaffLauncher>();
            var registration = new OwnerRegistration(
                owner,
                flares,
                chaffManagers,
                chaffLaunchers);
            _owners.Add(owner, registration);

            for (int index = 0; index < flares.Length; index++)
            {
                FlareCountermeasure flare = flares[index];
                if (flare != null)
                {
                    _flareOwners[flare] = owner;
                }
            }

            for (int index = 0; index < chaffManagers.Length; index++)
            {
                ChaffCountermeasure manager = chaffManagers[index];
                if (manager != null)
                {
                    RegisterAdvancedChaffEvent(owner, manager);
                }
            }

            for (int index = 0; index < chaffLaunchers.Length; index++)
            {
                ChaffLauncher launcher = chaffLaunchers[index];
                if (launcher == null)
                {
                    continue;
                }

                ParticleSystem[] particles =
                    launcher.GetComponentsInChildren<ParticleSystem>();
                _launchers[launcher] = new LauncherRegistration(
                    owner,
                    particles,
                    GetParticleSafetyLifetime(particles));
            }
        }
        catch (Exception ex)
        {
            WarnInternal($"Countermeasure owner registration failed for " +
                $"'{SafeActorName(owner)}': {ex.Message}");
        }
    }

    public void UntrackOwner(Actor owner)
    {
        if (ReferenceEquals(owner, null) || !_owners.ContainsKey(owner))
        {
            return;
        }

        _owners.Remove(owner);
        RemoveAllOwnedMappings(_flareOwners, owner);
        RemoveAllOwnedAdvancedChaffEvents(owner);
        RemoveAllOwnedLauncherMappings(owner);
    }

    public void Refresh(double nowSeconds)
    {
        if (_disposed)
        {
            return;
        }

        RefreshFlareLifecycle(nowSeconds);
        RefreshAdvancedChaffLifecycle(nowSeconds);
        RefreshParticleChaffLifecycle(nowSeconds);
        _countermeasures.Refresh(nowSeconds);
    }

    public void DrainFrameDataTo(List<TacviewEntityUpdate> destination)
    {
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (_disposed)
        {
            return;
        }

        _countermeasures.BeginFrame();
        _countermeasures.DrainFrameDataTo(destination);
    }

    public void PrepareFinalDrain(double nowSeconds)
    {
        if (_disposed)
        {
            return;
        }

        _flaresToRemove.Clear();
        foreach (CMFlare flare in _flareGenerations.Keys)
        {
            _flaresToRemove.Add(flare);
        }

        for (int index = 0; index < _flaresToRemove.Count; index++)
        {
            FinalizeFlare(_flaresToRemove[index], nowSeconds);
        }

        _flaresToRemove.Clear();
        _advancedSlotsToRemove.Clear();
        foreach (AdvancedSlotKey slot in _advancedChaffGenerations.Keys)
        {
            _advancedSlotsToRemove.Add(slot);
        }

        for (int index = 0; index < _advancedSlotsToRemove.Count; index++)
        {
            FinalizeAdvancedChaff(_advancedSlotsToRemove[index], nowSeconds);
        }

        _advancedSlotsToRemove.Clear();
        for (int index = _particleChaffGenerations.Count - 1; index >= 0; index--)
        {
            ParticleChaffGeneration generation = _particleChaffGenerations[index];
            _countermeasures.MarkForRemoval(generation.Handle, nowSeconds);
            _particleChaffGenerations.RemoveAt(index);
        }

        _countermeasures.PrepareFinalDrain(nowSeconds);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (InstanceGate)
        {
            if (ReferenceEquals(_activeInstance, this))
            {
                _activeInstance = null;
            }
        }

        if (ReferenceEquals(_currentFlareContext?.Adapter, this))
        {
            _currentFlareContext = null;
        }

        foreach (FlareGeneration generation in _flareGenerations.Values)
        {
            generation.Unsubscribe();
        }

        foreach (AdvancedChaffEventRegistration registration in
            _advancedChaffEvents.Values)
        {
            registration.Unsubscribe();
        }

        UnpatchFeature(_flareHarmony, "flare");
        UnpatchFeature(_particleChaffHarmony, "particle chaff");

        _flareGenerations.Clear();
        _advancedChaffGenerations.Clear();
        _particleChaffGenerations.Clear();
        _owners.Clear();
        _flareOwners.Clear();
        _advancedChaffEvents.Clear();
        _launchers.Clear();
        _flaresToRemove.Clear();
        _advancedSlotsToRemove.Clear();
        _countermeasures.Clear();
    }

    public void Warn(CountermeasureWarning warning)
    {
        LogWarn($"{warning.Kind}: {warning.Message}");
    }

    private void InstallPatches()
    {
        lock (InstanceGate)
        {
            if (_activeInstance != null)
            {
                throw new InvalidOperationException(
                    "Only one countermeasure recorder adapter can be active.");
            }

            _activeInstance = this;
        }

        _flarePatchesInstalled = TryInstallPatchGroup(
            "flare",
            _flareHarmony,
            () =>
            {
                PatchRequired(
                    _flareHarmony,
                    typeof(FlareCountermeasure),
                    "FireFlare",
                    new[] { typeof(Vector3), typeof(Vector3), typeof(float) },
                    prefixName: nameof(FlareFirePrefix),
                    postfixName: nameof(FlareFirePostfix));
                PatchRequired(
                    _flareHarmony,
                    typeof(CMFlare),
                    "OnEnable",
                    Type.EmptyTypes,
                    postfixName: nameof(FlareEnabledPostfix));
            });
        _particleChaffPatchesInstalled = TryInstallPatchGroup(
            "particle chaff",
            _particleChaffHarmony,
            () => PatchRequired(
                _particleChaffHarmony,
                typeof(ChaffLauncher),
                "FinalChaff",
                Type.EmptyTypes,
                postfixName: nameof(FinalChaffPostfix)));

        Log(
            "Countermeasure capture readiness: " +
            $"flare={_flarePatchesInstalled}, " +
            "advanced-chaff=True (typed event), " +
            $"particle-chaff={_particleChaffPatchesInstalled}.");
    }

    private void PatchRequired(
        Harmony harmony,
        Type declaringType,
        string methodName,
        Type[] argumentTypes,
        string prefixName = null,
        string postfixName = null)
    {
        MethodInfo target = AccessTools.Method(declaringType, methodName, argumentTypes);
        if (target == null)
        {
            throw new MissingMethodException(declaringType.FullName, methodName);
        }

        try
        {
            harmony.Patch(
                target,
                GetHarmonyMethod(prefixName),
                GetHarmonyMethod(postfixName));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Harmony could not patch {declaringType.FullName}.{methodName} " +
                $"with prefix '{prefixName ?? "<none>"}' and postfix " +
                $"'{postfixName ?? "<none>"}'.",
                ex);
        }
    }

    private static bool TryInstallPatchGroup(
        string featureName,
        Harmony harmony,
        Action install)
    {
        try
        {
            install();
            Log($"Countermeasure {featureName} Harmony hooks installed.");
            return true;
        }
        catch (Exception ex)
        {
            var featureFailure = new InvalidOperationException(
                $"Countermeasure {featureName} Harmony hook installation failed.",
                ex);
            try
            {
                harmony.UnpatchSelf();
            }
            catch (Exception cleanupException)
            {
                LogWarn(
                    $"Countermeasure {featureName} partial-patch cleanup failed: " +
                    cleanupException);
            }

            LogWarn(
                $"Countermeasure {featureName} capture is unavailable; " +
                $"other countermeasure features will continue: {featureFailure}");
            return false;
        }
    }

    private static void UnpatchFeature(Harmony harmony, string featureName)
    {
        try
        {
            harmony.UnpatchSelf();
        }
        catch (Exception ex)
        {
            LogWarn($"Countermeasure {featureName} Harmony cleanup failed: {ex}");
        }
    }

    private static HarmonyMethod GetHarmonyMethod(string methodName)
    {
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return null;
        }

        MethodInfo patch = typeof(VtolCountermeasureRegistryAdapter).GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.NonPublic);
        if (patch == null)
        {
            throw new MissingMethodException(
                typeof(VtolCountermeasureRegistryAdapter).FullName,
                methodName);
        }

        return new HarmonyMethod(patch);
    }

    private static void FlareFirePrefix(
        FlareCountermeasure __instance,
        Vector3 position,
        Vector3 velocity,
        float lifeTime)
    {
        try
        {
            VtolCountermeasureRegistryAdapter adapter = _activeInstance;
            _currentFlareContext = adapter?.CreateFlareContext(
                __instance,
                position,
                lifeTime);
        }
        catch (Exception ex)
        {
            _currentFlareContext = null;
            _activeInstance?.WarnInternal($"Flare firing context failed: {ex.Message}");
        }
    }

    private static void FlareFirePostfix()
    {
        _currentFlareContext = null;
    }

    private static void FlareEnabledPostfix(CMFlare __instance)
    {
        FlareFireContext context = _currentFlareContext;
        _currentFlareContext = null;
        if (context == null || !ReferenceEquals(context.Adapter, _activeInstance))
        {
            return;
        }

        try
        {
            context.Adapter.OnFlareEnabled(__instance, context);
        }
        catch (Exception ex)
        {
            context.Adapter.WarnInternal($"Flare spawn capture failed: {ex.Message}");
        }
    }

    private static void FinalChaffPostfix(ChaffLauncher __instance)
    {
        try
        {
            VtolCountermeasureRegistryAdapter adapter = _activeInstance;
            if (adapter != null && !Radar.ADV_RADAR)
            {
                adapter.OnParticleChaffFired(__instance);
            }
        }
        catch (Exception ex)
        {
            _activeInstance?.WarnInternal($"Particle chaff spawn capture failed: {ex.Message}");
        }
    }

    private FlareFireContext CreateFlareContext(
        FlareCountermeasure countermeasure,
        Vector3 position,
        float lifeTime)
    {
        if (_disposed || countermeasure == null)
        {
            return null;
        }

        Actor owner = ResolveFlareOwner(countermeasure);
        return owner == null
            ? null
            : new FlareFireContext(this, owner, position, lifeTime);
    }

    private void OnFlareEnabled(CMFlare flare, FlareFireContext context)
    {
        if (_disposed || flare == null)
        {
            return;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        if (_flareGenerations.TryGetValue(flare, out FlareGeneration previous))
        {
            EntityTransform? previousFinal = CaptureCurrentTransform(previous.Source);
            _countermeasures.MarkForRemoval(previous.Handle, now, previousFinal);
            previous.Unsubscribe();
            _flareGenerations.Remove(flare);
        }

        var source = new VtolEntitySource(
            flare.transform,
            _mapManager,
            () => flare != null && flare.gameObject.activeInHierarchy);
        EntityTransform? spawnTransform = CaptureCurrentTransform(source);
        if (!spawnTransform.HasValue)
        {
            WarnInternal("Flare spawn skipped because its real transform was unavailable.");
            return;
        }

        OwnerMetadata owner = GetOwnerMetadata(context.Owner);
        var request = new CountermeasureSpawnRequest(
            CountermeasureKind.Flare,
            owner.Coalition,
            owner.Color,
            owner.ParentObjectId,
            source,
            spawnTransform);
        if (!_countermeasures.TrySpawn(
            request,
            now,
            out CountermeasureHandle handle))
        {
            return;
        }

        double stockLifetime = Math.Max(context.LifeTime, flare.flareLife);
        double safetyLifetime = Math.Min(
            Math.Max(1.0, stockLifetime + FlareSafetyTailSeconds),
            MaximumFlareSafetyLifetimeSeconds);
        var generation = new FlareGeneration(
            flare,
            source,
            handle,
            now + safetyLifetime);
        Action<CMFlare> decayHandler = decayed => OnFlareDecayed(decayed, generation);
        generation.SetDecayHandler(decayHandler);
        flare.OnDecayed += decayHandler;
        _flareGenerations[flare] = generation;
    }

    private void OnFlareDecayed(CMFlare flare, FlareGeneration expectedGeneration)
    {
        if (_disposed || ReferenceEquals(flare, null) ||
            !_flareGenerations.TryGetValue(flare, out FlareGeneration current) ||
            !ReferenceEquals(current, expectedGeneration))
        {
            return;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        EntityTransform? finalTransform = CaptureCurrentTransform(current.Source);
        _countermeasures.MarkForRemoval(current.Handle, now, finalTransform);
        current.Unsubscribe();
        _flareGenerations.Remove(flare);
    }

    private void RegisterAdvancedChaffEvent(
        Actor owner,
        ChaffCountermeasure countermeasure)
    {
        if (_advancedChaffEvents.TryGetValue(
            countermeasure,
            out AdvancedChaffEventRegistration previous))
        {
            previous.Unsubscribe();
            _advancedChaffEvents.Remove(countermeasure);
        }

        Action firedHandler = null;
        try
        {
            ChaffCountermeasure.Chaff[] chaffs = countermeasure.chaffs;
            int slotCount = chaffs?.Length ?? 0;
            if (slotCount == 0)
            {
                WarnInternal("Advanced chaff event registration skipped because its slot ring is unavailable.");
                return;
            }

            var slotTracker = new CountermeasureSlotGenerationTracker(slotCount);
            CaptureAdvancedChaffSlotBaseline(chaffs, slotTracker);
            firedHandler = () => OnAdvancedChaffFired(countermeasure);
            countermeasure.OnFiredCM += firedHandler;
            _advancedChaffEvents[countermeasure] =
                new AdvancedChaffEventRegistration(
                    owner,
                    countermeasure,
                    slotTracker,
                    firedHandler);
        }
        catch (Exception ex)
        {
            if (firedHandler != null && countermeasure != null)
            {
                try
                {
                    countermeasure.OnFiredCM -= firedHandler;
                }
                catch
                {
                    // A destroyed component cannot retain a usable callback.
                }
            }

            WarnInternal($"Advanced chaff typed-event registration failed: {ex.Message}");
        }
    }

    private static void CaptureAdvancedChaffSlotBaseline(
        ChaffCountermeasure.Chaff[] chaffs,
        CountermeasureSlotGenerationTracker slotTracker)
    {
        float observationTime = Time.time;
        int count = Math.Min(chaffs.Length, slotTracker.SlotCount);
        for (int index = 0; index < count; index++)
        {
            ChaffCountermeasure.Chaff chaff = chaffs[index];
            bool isActive = !chaff.decayed && chaff.effectValue > 0f;
            double generationStartTime = observationTime - chaff.elapsedTime;
            slotTracker.Observe(index, isActive, generationStartTime);
        }
    }

    private void OnAdvancedChaffFired(ChaffCountermeasure countermeasure)
    {
        if (_disposed || countermeasure == null || !Radar.ADV_RADAR)
        {
            return;
        }

        try
        {
            if (!_advancedChaffEvents.TryGetValue(
                countermeasure,
                out AdvancedChaffEventRegistration registration))
            {
                return;
            }

            ChaffCountermeasure.Chaff[] chaffs = countermeasure.chaffs;
            if (chaffs == null ||
                chaffs.Length != registration.SlotTracker.SlotCount)
            {
                WarnInternal("Advanced chaff release skipped because its slot ring changed unexpectedly.");
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            float observationTime = Time.time;
            for (int index = 0; index < chaffs.Length; index++)
            {
                ChaffCountermeasure.Chaff chaff = chaffs[index];
                bool isActive = !chaff.decayed && chaff.effectValue > 0f;
                double generationStartTime = observationTime - chaff.elapsedTime;
                if (registration.SlotTracker.Observe(
                    index,
                    isActive,
                    generationStartTime))
                {
                    try
                    {
                        SpawnAdvancedChaff(
                            registration.Owner,
                            countermeasure,
                            index,
                            now);
                    }
                    catch (Exception ex)
                    {
                        WarnInternal(
                            $"Advanced chaff slot {index} capture failed: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            WarnInternal($"Advanced chaff typed-event capture failed: {ex.Message}");
        }
    }

    private void SpawnAdvancedChaff(
        Actor owner,
        ChaffCountermeasure countermeasure,
        int slotIndex,
        double now)
    {
        var key = new AdvancedSlotKey(countermeasure, slotIndex);
        if (_advancedChaffGenerations.TryGetValue(
            key,
            out AdvancedChaffGeneration previous))
        {
            EntityTransform? previousFinal = previous.Source.TryGetCurrentTransform();
            _countermeasures.MarkForRemoval(
                previous.Handle,
                now,
                previousFinal);
            _advancedChaffGenerations.Remove(key);
        }

        if (!TryConsumeChaffBudget(now) || owner == null)
        {
            return;
        }

        var source = new AdvancedChaffSource(
            _mapManager,
            countermeasure,
            slotIndex);
        EntityTransform? spawnTransform = source.TryGetCurrentTransform();
        if (!spawnTransform.HasValue)
        {
            WarnInternal("Advanced chaff spawn skipped because its simulated position was unavailable.");
            return;
        }

        OwnerMetadata ownerMetadata = GetOwnerMetadata(owner);
        if (!_countermeasures.TrySpawn(
            new CountermeasureSpawnRequest(
                CountermeasureKind.Chaff,
                ownerMetadata.Coalition,
                ownerMetadata.Color,
                ownerMetadata.ParentObjectId,
                source,
                spawnTransform),
            now,
            out CountermeasureHandle handle))
        {
            return;
        }

        _advancedChaffGenerations[key] = new AdvancedChaffGeneration(
            source,
            handle,
            now + AdvancedChaffSafetyLifetimeSeconds);

        if (!_advancedChaffCaptureObserved)
        {
            _advancedChaffCaptureObserved = true;
            Log("Countermeasure advanced chaff typed-event capture received its first release.");
        }
    }

    private void OnParticleChaffFired(ChaffLauncher launcher)
    {
        if (_disposed || launcher == null)
        {
            return;
        }

        if (!_launchers.TryGetValue(launcher, out LauncherRegistration registration))
        {
            Actor resolvedOwner = launcher.GetComponentInParent<Actor>();
            if (resolvedOwner == null)
            {
                WarnInternal("Chaff launcher owner could not be resolved.");
                return;
            }

            ParticleSystem[] particles = launcher.GetComponentsInChildren<ParticleSystem>();
            registration = new LauncherRegistration(
                resolvedOwner,
                particles,
                GetParticleSafetyLifetime(particles));
            _launchers[launcher] = registration;
        }

        if (!registration.HasUsableParticles)
        {
            WarnInternal("Visible chaff skipped because the launcher has no particle-system position lifecycle.");
            return;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        if (!TryConsumeChaffBudget(now))
        {
            return;
        }

        var launcherSource = new VtolEntitySource(launcher.transform, _mapManager);
        EntityTransform? spawnTransform = CaptureCurrentTransform(launcherSource);
        if (!spawnTransform.HasValue)
        {
            WarnInternal("Visible chaff skipped because the real launcher transform was unavailable.");
            return;
        }

        OwnerMetadata owner = GetOwnerMetadata(registration.Owner);
        if (!_countermeasures.TrySpawn(
            new CountermeasureSpawnRequest(
                CountermeasureKind.Chaff,
                owner.Coalition,
                owner.Color,
                owner.ParentObjectId,
                source: null,
                spawnTransform: spawnTransform),
            now,
            out CountermeasureHandle handle))
        {
            return;
        }

        _particleChaffGenerations.Add(new ParticleChaffGeneration(
            handle,
            registration.Particles,
            now + ParticleStartGraceSeconds,
            now + registration.SafetyLifetimeSeconds));
    }

    private void RefreshFlareLifecycle(double nowSeconds)
    {
        _flaresToRemove.Clear();
        foreach (KeyValuePair<CMFlare, FlareGeneration> pair in _flareGenerations)
        {
            if (nowSeconds >= pair.Value.ExpiresAtSeconds ||
                pair.Key == null ||
                !pair.Key.gameObject.activeInHierarchy)
            {
                _flaresToRemove.Add(pair.Key);
            }
        }

        for (int index = 0; index < _flaresToRemove.Count; index++)
        {
            FinalizeFlare(_flaresToRemove[index], nowSeconds);
        }

        _flaresToRemove.Clear();
    }

    private void FinalizeFlare(CMFlare flare, double nowSeconds)
    {
        if (ReferenceEquals(flare, null) ||
            !_flareGenerations.TryGetValue(flare, out FlareGeneration generation))
        {
            return;
        }

        EntityTransform? finalTransform = CaptureCurrentTransform(generation.Source);
        _countermeasures.MarkForRemoval(generation.Handle, nowSeconds, finalTransform);
        generation.Unsubscribe();
        _flareGenerations.Remove(flare);
    }

    private void RefreshAdvancedChaffLifecycle(double nowSeconds)
    {
        _advancedSlotsToRemove.Clear();
        foreach (KeyValuePair<AdvancedSlotKey, AdvancedChaffGeneration> pair in
            _advancedChaffGenerations)
        {
            if (nowSeconds >= pair.Value.ExpiresAtSeconds || pair.Value.Source.IsDecayed)
            {
                _advancedSlotsToRemove.Add(pair.Key);
            }
        }

        for (int index = 0; index < _advancedSlotsToRemove.Count; index++)
        {
            FinalizeAdvancedChaff(_advancedSlotsToRemove[index], nowSeconds);
        }

        _advancedSlotsToRemove.Clear();
    }

    private void FinalizeAdvancedChaff(AdvancedSlotKey key, double nowSeconds)
    {
        if (!_advancedChaffGenerations.TryGetValue(
            key,
            out AdvancedChaffGeneration generation))
        {
            return;
        }

        EntityTransform? finalTransform = generation.Source.TryGetCurrentTransform();
        _countermeasures.MarkForRemoval(generation.Handle, nowSeconds, finalTransform);
        _advancedChaffGenerations.Remove(key);
    }

    private void RefreshParticleChaffLifecycle(double nowSeconds)
    {
        for (int index = _particleChaffGenerations.Count - 1; index >= 0; index--)
        {
            ParticleChaffGeneration generation = _particleChaffGenerations[index];
            bool expired = nowSeconds >= generation.ExpiresAtSeconds;
            bool checkDue = nowSeconds >= generation.NextCheckAtSeconds;
            bool finished = checkDue && !AreParticlesAlive(generation.Particles);
            if (!expired && !finished)
            {
                if (checkDue)
                {
                    generation.NextCheckAtSeconds =
                        nowSeconds + ParticleLivenessIntervalSeconds;
                }

                continue;
            }

            _countermeasures.MarkForRemoval(generation.Handle, nowSeconds);
            _particleChaffGenerations.RemoveAt(index);
        }
    }

    private bool TryConsumeChaffBudget(double nowSeconds)
    {
        if (nowSeconds - _chaffWindowStartSeconds >= 1.0 ||
            nowSeconds < _chaffWindowStartSeconds)
        {
            _chaffWindowStartSeconds = nowSeconds;
            _chaffReleasesInWindow = 0;
        }

        if (_chaffReleasesInWindow >= MaximumNewChaffPerSecond)
        {
            WarnInternal($"Chaff release capture is limited to " +
                $"{MaximumNewChaffPerSecond} new objects per second.");
            return false;
        }

        _chaffReleasesInWindow++;
        return true;
    }

    private Actor ResolveFlareOwner(FlareCountermeasure countermeasure)
    {
        if (_flareOwners.TryGetValue(countermeasure, out Actor owner) && owner != null)
        {
            return owner;
        }

        owner = countermeasure.GetComponentInParent<Actor>();
        if (owner != null)
        {
            _flareOwners[countermeasure] = owner;
        }

        return owner;
    }

    private OwnerMetadata GetOwnerMetadata(Actor owner)
    {
        if (owner == null)
        {
            throw new InvalidOperationException("The firing aircraft owner is unavailable.");
        }

        if (_entityRegistry.TryGet(owner, out TrackedEntity tracked) &&
            tracked.Kind == EntityKind.Aircraft)
        {
            return new OwnerMetadata(
                tracked.Metadata.Coalition,
                tracked.Metadata.Color,
                tracked.ObjectId);
        }

        return new OwnerMetadata(
            VtolActorClassifier.GetCoalition(owner.team),
            VtolActorClassifier.GetColor(owner.team),
            null);
    }

    private static EntityTransform? CaptureCurrentTransform(VtolEntitySource source)
    {
        if (source == null)
        {
            return null;
        }

        try
        {
            return source.TryGetCurrentTransform(out EntityTransform transform) &&
                transform.HasFiniteValues
                ? transform
                : (EntityTransform?)null;
        }
        catch
        {
            return null;
        }
    }

    private static bool AreParticlesAlive(ParticleSystem[] particles)
    {
        for (int index = 0; index < particles.Length; index++)
        {
            ParticleSystem particle = particles[index];
            if (particle == null)
            {
                continue;
            }

            try
            {
                if (particle.IsAlive(true))
                {
                    return true;
                }
            }
            catch
            {
                // A destroyed particle system is simply no longer alive.
            }
        }

        return false;
    }

    private static double GetParticleSafetyLifetime(ParticleSystem[] particles)
    {
        double lifetime = 0.5;
        for (int index = 0; index < particles.Length; index++)
        {
            ParticleSystem particle = particles[index];
            if (particle == null)
            {
                continue;
            }

            try
            {
                ParticleSystem.MainModule main = particle.main;
                lifetime = Math.Max(
                    lifetime,
                    main.duration + main.startLifetime.constantMax + 0.5);
            }
            catch
            {
                // Keep the conservative bounded fallback.
            }
        }

        return Math.Min(lifetime, MaximumParticleChaffLifetimeSeconds);
    }

    private static void RemoveAllOwnedMappings<TComponent>(
        Dictionary<TComponent, Actor> mappings,
        Actor owner)
        where TComponent : UnityEngine.Object
    {
        var componentsToRemove = new List<TComponent>();
        foreach (KeyValuePair<TComponent, Actor> pair in mappings)
        {
            if (ReferenceEquals(pair.Value, owner))
            {
                componentsToRemove.Add(pair.Key);
            }
        }

        for (int index = 0; index < componentsToRemove.Count; index++)
        {
            mappings.Remove(componentsToRemove[index]);
        }
    }

    private void RemoveAllOwnedLauncherMappings(Actor owner)
    {
        var launchersToRemove = new List<ChaffLauncher>();
        foreach (KeyValuePair<ChaffLauncher, LauncherRegistration> pair in _launchers)
        {
            if (ReferenceEquals(pair.Value.Owner, owner))
            {
                launchersToRemove.Add(pair.Key);
            }
        }

        for (int index = 0; index < launchersToRemove.Count; index++)
        {
            _launchers.Remove(launchersToRemove[index]);
        }
    }

    private void RemoveAllOwnedAdvancedChaffEvents(Actor owner)
    {
        var countermeasuresToRemove = new List<ChaffCountermeasure>();
        foreach (KeyValuePair<ChaffCountermeasure, AdvancedChaffEventRegistration>
            pair in _advancedChaffEvents)
        {
            if (ReferenceEquals(pair.Value.Owner, owner))
            {
                pair.Value.Unsubscribe();
                countermeasuresToRemove.Add(pair.Key);
            }
        }

        for (int index = 0; index < countermeasuresToRemove.Count; index++)
        {
            _advancedChaffEvents.Remove(countermeasuresToRemove[index]);
        }
    }

    private static string SafeActorName(Actor actor)
    {
        return actor == null
            ? "<destroyed>"
            : VtolActorClassifier.FirstNonEmpty(
                actor.actorName,
                actor.name,
                "<unnamed>");
    }

    private void WarnInternal(string message)
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (now < _nextInternalWarningTime)
        {
            return;
        }

        _nextInternalWarningTime = now + InternalWarningIntervalSeconds;
        LogWarn(message);
    }

    private sealed class FlareFireContext
    {
        public FlareFireContext(
            VtolCountermeasureRegistryAdapter adapter,
            Actor owner,
            Vector3 position,
            float lifeTime)
        {
            Adapter = adapter;
            Owner = owner;
            Position = position;
            LifeTime = lifeTime;
        }

        public VtolCountermeasureRegistryAdapter Adapter { get; }

        public Actor Owner { get; }

        public Vector3 Position { get; }

        public float LifeTime { get; }
    }

    private sealed class FlareGeneration
    {
        private Action<CMFlare> _decayHandler;

        public FlareGeneration(
            CMFlare flare,
            VtolEntitySource source,
            CountermeasureHandle handle,
            double expiresAtSeconds)
        {
            Flare = flare;
            Source = source;
            Handle = handle;
            ExpiresAtSeconds = expiresAtSeconds;
        }

        public CMFlare Flare { get; }

        public VtolEntitySource Source { get; }

        public CountermeasureHandle Handle { get; }

        public double ExpiresAtSeconds { get; }

        public void SetDecayHandler(Action<CMFlare> decayHandler)
        {
            _decayHandler = decayHandler;
        }

        public void Unsubscribe()
        {
            Action<CMFlare> handler = _decayHandler;
            _decayHandler = null;
            if (handler != null && Flare != null)
            {
                Flare.OnDecayed -= handler;
            }
        }
    }

    private sealed class AdvancedChaffEventRegistration
    {
        private Action _firedHandler;

        public AdvancedChaffEventRegistration(
            Actor owner,
            ChaffCountermeasure countermeasure,
            CountermeasureSlotGenerationTracker slotTracker,
            Action firedHandler)
        {
            Owner = owner;
            Countermeasure = countermeasure;
            SlotTracker = slotTracker;
            _firedHandler = firedHandler;
        }

        public Actor Owner { get; }

        public ChaffCountermeasure Countermeasure { get; }

        public CountermeasureSlotGenerationTracker SlotTracker { get; }

        public void Unsubscribe()
        {
            Action handler = _firedHandler;
            _firedHandler = null;
            if (handler != null && Countermeasure != null)
            {
                Countermeasure.OnFiredCM -= handler;
            }
        }
    }

    private sealed class AdvancedChaffGeneration
    {
        public AdvancedChaffGeneration(
            AdvancedChaffSource source,
            CountermeasureHandle handle,
            double expiresAtSeconds)
        {
            Source = source;
            Handle = handle;
            ExpiresAtSeconds = expiresAtSeconds;
        }

        public AdvancedChaffSource Source { get; }

        public CountermeasureHandle Handle { get; }

        public double ExpiresAtSeconds { get; }
    }

    private sealed class AdvancedChaffSource : IEntityTransformSource
    {
        private readonly VTMapManager _mapManager;
        private readonly ChaffCountermeasure _countermeasure;
        private readonly int _slotIndex;

        public AdvancedChaffSource(
            VTMapManager mapManager,
            ChaffCountermeasure countermeasure,
            int slotIndex)
        {
            _mapManager = mapManager;
            _countermeasure = countermeasure;
            _slotIndex = slotIndex;
        }

        public bool IsDecayed
        {
            get
            {
                return !TryGetChaff(out ChaffCountermeasure.Chaff chaff) || chaff.decayed;
            }
        }

        public bool TryGetTransform(out EntityTransform transform)
        {
            transform = default;
            if (!TryGetChaff(out ChaffCountermeasure.Chaff chaff) || chaff.decayed)
            {
                return false;
            }

            return TryMapPosition(chaff.position, out transform);
        }

        public EntityTransform? TryGetCurrentTransform()
        {
            if (!TryGetChaff(out ChaffCountermeasure.Chaff chaff))
            {
                return null;
            }

            return TryMapPosition(chaff.position, out EntityTransform transform) &&
                transform.HasFiniteValues
                ? transform
                : (EntityTransform?)null;
        }

        private bool TryGetChaff(out ChaffCountermeasure.Chaff chaff)
        {
            chaff = default;
            if (_countermeasure == null || _countermeasure.chaffs == null ||
                _slotIndex < 0 || _slotIndex >= _countermeasure.chaffs.Length)
            {
                return false;
            }

            chaff = _countermeasure.chaffs[_slotIndex];
            return true;
        }

        private bool TryMapPosition(Vector3 position, out EntityTransform transform)
        {
            transform = default;
            try
            {
                Vector3D globalPosition = VTMapManager.WorldToGlobalPoint(position);
                Vector3D gpsPosition = _mapManager.WorldPositionToGPSCoords(position);
                transform = VtolTransformMapper.MapEntity(
                    gpsPosition.x,
                    gpsPosition.y,
                    gpsPosition.z,
                    globalPosition.x,
                    globalPosition.z,
                    new Attitude(0.0, 0.0, 0.0));
                return transform.HasFiniteValues;
            }
            catch
            {
                return false;
            }
        }
    }

    private sealed class ParticleChaffGeneration
    {
        public ParticleChaffGeneration(
            CountermeasureHandle handle,
            ParticleSystem[] particles,
            double checkAfterSeconds,
            double expiresAtSeconds)
        {
            Handle = handle;
            Particles = particles;
            NextCheckAtSeconds = checkAfterSeconds;
            ExpiresAtSeconds = expiresAtSeconds;
        }

        public CountermeasureHandle Handle { get; }

        public ParticleSystem[] Particles { get; }

        public double NextCheckAtSeconds { get; set; }

        public double ExpiresAtSeconds { get; }
    }

    private sealed class OwnerRegistration
    {
        public OwnerRegistration(
            Actor owner,
            FlareCountermeasure[] flares,
            ChaffCountermeasure[] chaffManagers,
            ChaffLauncher[] chaffLaunchers)
        {
            Owner = owner;
            Flares = flares ?? Array.Empty<FlareCountermeasure>();
            ChaffManagers = chaffManagers ?? Array.Empty<ChaffCountermeasure>();
            ChaffLaunchers = chaffLaunchers ?? Array.Empty<ChaffLauncher>();
        }

        public Actor Owner { get; }

        public FlareCountermeasure[] Flares { get; }

        public ChaffCountermeasure[] ChaffManagers { get; }

        public ChaffLauncher[] ChaffLaunchers { get; }
    }

    private sealed class LauncherRegistration
    {
        public LauncherRegistration(
            Actor owner,
            ParticleSystem[] particles,
            double safetyLifetimeSeconds)
        {
            Owner = owner;
            Particles = particles ?? Array.Empty<ParticleSystem>();
            SafetyLifetimeSeconds = safetyLifetimeSeconds;
        }

        public Actor Owner { get; }

        public ParticleSystem[] Particles { get; }

        public double SafetyLifetimeSeconds { get; }

        public bool HasUsableParticles
        {
            get
            {
                for (int index = 0; index < Particles.Length; index++)
                {
                    if (Particles[index] != null)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    private readonly struct OwnerMetadata
    {
        public OwnerMetadata(string coalition, string color, ulong? parentObjectId)
        {
            Coalition = coalition;
            Color = color;
            ParentObjectId = parentObjectId;
        }

        public string Coalition { get; }

        public string Color { get; }

        public ulong? ParentObjectId { get; }
    }

    private readonly struct AdvancedSlotKey : IEquatable<AdvancedSlotKey>
    {
        public AdvancedSlotKey(ChaffCountermeasure countermeasure, int slotIndex)
        {
            Countermeasure = countermeasure;
            SlotIndex = slotIndex;
        }

        public ChaffCountermeasure Countermeasure { get; }

        public int SlotIndex { get; }

        public bool Equals(AdvancedSlotKey other)
        {
            return ReferenceEquals(Countermeasure, other.Countermeasure) &&
                SlotIndex == other.SlotIndex;
        }

        public override bool Equals(object obj)
        {
            return obj is AdvancedSlotKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (RuntimeHelpers.GetHashCode(Countermeasure) * 397) ^ SlotIndex;
            }
        }
    }
}
