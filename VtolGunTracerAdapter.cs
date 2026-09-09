using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VTOLVRTacviewSPRecorder.Core;

namespace VTOLVRTacviewSPRecorder.Main;

/// <summary>
/// Captures the real VTOL VR <see cref="Bullet"/> pool lifecycle.
///
/// The recorder deliberately hooks the game's Bullet spawn and teardown
/// boundaries instead of re-creating rounds from ammo counters or ballistic
/// guesses. A tracked ACMI object therefore follows the actual pooled Bullet
/// transform until Bullet.KillBullet or Bullet.OnDisable supplies the end of
/// that generation.
/// </summary>
internal sealed class VtolGunTracerAdapter : IDisposable
{
    private const string HarmonyId =
        "de.kalag.vtolvr.tacviewsprecorder.gun-bullets.0.5.0";
    private const double OwnerComponentResyncIntervalSeconds = 5.0;
    private const double WarningIntervalSeconds = 10.0;

    private static readonly object InstanceGate = new object();
    private static VtolGunTracerAdapter _activeInstance;

    // Bullet.Fire invokes Gun.OnFiredBullet synchronously. The callback gives
    // us the factual Gun identity/name for cadence accounting; the Fire
    // postfix then binds that context to the actual pooled Bullet instance.
    [ThreadStatic]
    private static Gun _pendingGun;

    [ThreadStatic]
    private static VTOLCannon _pendingCannon;

    [ThreadStatic]
    private static Actor _pendingEmitterOwner;

    private readonly VTMapManager _mapManager;
    private readonly EntityRegistry _entityRegistry;
    private readonly GunTracerRegistry _tracers;
    private readonly Harmony _harmony;
    private readonly FieldInfo _hitPointField;
    private readonly Dictionary<Actor, OwnerSubscriptions> _owners =
        new Dictionary<Actor, OwnerSubscriptions>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Bullet, TrackedBullet> _bullets =
        new Dictionary<Bullet, TrackedBullet>(ReferenceEqualityComparer.Instance);
    private readonly List<Bullet> _bulletsToRemove = new List<Bullet>();
    private readonly Dictionary<VTOLCannon, Actor> _cannonOwners =
        new Dictionary<VTOLCannon, Actor>(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Actor> _activeOwners =
        new HashSet<Actor>(ReferenceEqualityComparer.Instance);
    private readonly List<Actor> _ownersToRemove = new List<Actor>();
    private double _nextOwnerComponentResyncTime;
    private double _nextWarningTime;
    private bool _disposed;

    public VtolGunTracerAdapter(VTMapManager mapManager, EntityRegistry entityRegistry)
    {
        _mapManager = mapManager ?? throw new ArgumentNullException(nameof(mapManager));
        _entityRegistry = entityRegistry ?? throw new ArgumentNullException(nameof(entityRegistry));
        _tracers = new GunTracerRegistry(_entityRegistry);
        _harmony = new Harmony(HarmonyId);
        _hitPointField = AccessTools.Field(typeof(Bullet), "hitPoint") ??
            throw new MissingFieldException(typeof(Bullet).FullName, "hitPoint");

        InstallPatches();
    }

    public int ActiveTracerCount => _tracers.ActiveTracerCount;

    public bool NeedsOwnerReconciliation(double nowSeconds)
    {
        return !_disposed && nowSeconds >= _nextOwnerComponentResyncTime;
    }

    public void TrackOwner(Actor owner)
    {
        if (_disposed)
        {
            return;
        }

        Actor root = GetRootActor(owner);
        if (root == null || !ReferenceEquals(root, owner))
        {
            return;
        }

        try
        {
            if (_owners.TryGetValue(root, out OwnerSubscriptions existing))
            {
                existing.Reconcile(root, this);
                return;
            }

            var subscriptions = new OwnerSubscriptions(root);
            subscriptions.Reconcile(root, this);
            _owners.Add(root, subscriptions);
            if (_nextOwnerComponentResyncTime <= 0.0)
            {
                _nextOwnerComponentResyncTime =
                    Time.realtimeSinceStartupAsDouble + OwnerComponentResyncIntervalSeconds;
            }
        }
        catch (Exception ex)
        {
            WarnThrottled($"Gun tracer owner registration skipped: {ex.Message}");
        }
    }

    public void UntrackOwner(Actor owner)
    {
        if (ReferenceEquals(owner, null))
        {
            return;
        }

        // Close every live Bullet generation before the owner is removed from
        // the entity registry. The registry captures the Bullet's last real
        // transform and emits only its normal final snapshot/Delete; no hit or
        // timeout event is invented for owner teardown.
        TerminateOwnerBullets(owner);

        if (_owners.TryGetValue(owner, out OwnerSubscriptions subscriptions))
        {
            subscriptions.Dispose(this);
            _owners.Remove(owner);
        }
    }

    public void Refresh(
        double nowSeconds,
        List<Actor> activeActors,
        bool ownerSnapshotComplete)
    {
        if (_disposed)
        {
            return;
        }

        if (activeActors == null)
        {
            throw new ArgumentNullException(nameof(activeActors));
        }

        _tracers.BeginFrame();
        if (ownerSnapshotComplete)
        {
            ReconcileOwnerSubscriptions(nowSeconds, activeActors);
        }

        _tracers.Refresh(nowSeconds);
    }

    public void DrainFrameDataTo(List<TacviewEntityUpdate> destination)
    {
        _tracers.DrainFrameDataTo(
            destination ?? throw new ArgumentNullException(nameof(destination)));
    }

    public void PrepareFinalDrain()
    {
        if (_disposed)
        {
            return;
        }

        _tracers.BeginFrame();
        _tracers.PrepareFinalDrain();
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

        foreach (OwnerSubscriptions subscriptions in _owners.Values)
        {
            subscriptions.Dispose(this);
        }

        _owners.Clear();
        _activeOwners.Clear();
        _ownersToRemove.Clear();
        _bullets.Clear();
        _bulletsToRemove.Clear();
        _cannonOwners.Clear();
        ClearPendingContext();

        try
        {
            _harmony.UnpatchSelf();
        }
        catch (Exception ex)
        {
            LogWarn($"Gun tracer Harmony cleanup failed: {ex}");
        }

        _tracers.Clear();
    }

    private void ReconcileOwnerSubscriptions(double nowSeconds, List<Actor> activeActors)
    {
        if (nowSeconds < _nextOwnerComponentResyncTime)
        {
            return;
        }

        _nextOwnerComponentResyncTime = nowSeconds + OwnerComponentResyncIntervalSeconds;
        _activeOwners.Clear();
        for (int index = 0; index < activeActors.Count; index++)
        {
            Actor root = GetRootActor(activeActors[index]);
            if (root == null || !_activeOwners.Add(root))
            {
                continue;
            }

            if (_owners.TryGetValue(root, out OwnerSubscriptions existing))
            {
                existing.Reconcile(root, this);
            }
            else
            {
                TrackOwner(root);
            }
        }

        _ownersToRemove.Clear();
        foreach (Actor owner in _owners.Keys)
        {
            if (!_activeOwners.Contains(owner))
            {
                _ownersToRemove.Add(owner);
            }
        }

        for (int index = 0; index < _ownersToRemove.Count; index++)
        {
            UntrackOwner(_ownersToRemove[index]);
        }

        _ownersToRemove.Clear();
        _activeOwners.Clear();
    }

    private void OnGunFiredBullet(Gun gun, Actor owner, Ray ray, float speed)
    {
        if (_disposed || gun == null)
        {
            return;
        }

        // The callback is inside Bullet.Fire. Do not create a record here: the
        // matching Harmony postfix supplies the actual pooled Bullet.
        _pendingGun = gun;
        _pendingCannon = null;
        _pendingEmitterOwner = owner;
    }

    private void BeginCannonFire(VTOLCannon cannon)
    {
        if (_disposed || cannon == null)
        {
            return;
        }

        _pendingGun = null;
        _pendingCannon = cannon;
        _pendingEmitterOwner = FindCannonOwner(cannon);
    }

    private void EndCannonFire(VTOLCannon cannon)
    {
        if (ReferenceEquals(_pendingCannon, cannon))
        {
            _pendingCannon = null;
            _pendingEmitterOwner = null;
        }
    }

    private void OnBulletFired(Bullet bullet, Actor sourceActor)
    {
        Gun gun = _pendingGun;
        VTOLCannon cannon = _pendingCannon;
        Actor ownerHint = sourceActor ?? _pendingEmitterOwner;
        object emitter = (object)gun ?? (object)cannon ?? (object)ownerHint;
        ClearPendingContext();

        if (_disposed || bullet == null || emitter == null)
        {
            return;
        }

        Actor owner = GetRootActor(ownerHint);
        if (owner == null || !_owners.ContainsKey(owner) ||
            !_entityRegistry.TryGet(owner, out TrackedEntity parent))
        {
            // A real Bullet with no currently tracked parent is intentionally
            // skipped. This also makes callbacks arriving after owner teardown
            // no-op instead of creating an orphaned generation.
            return;
        }

        // Pool reuse must never inherit an earlier ACMI generation. Normally
        // OnDisable/OnEnable closes it first; this final guard handles a game
        // teardown ordering where Fire arrives before either callback.
        if (_bullets.TryGetValue(bullet, out TrackedBullet previous))
        {
            _tracers.Terminate(previous.Handle);
            _bullets.Remove(bullet);
        }

        string name = gun != null
            ? VtolActorClassifier.FirstNonEmpty(gun.gunName, "Gun")
            : cannon != null
                ? VtolActorClassifier.FirstNonEmpty(
                    cannon.gameObject == null ? null : cannon.gameObject.name,
                    "VTOLCannon")
                : "Bullet";
        var source = new VtolBulletTransformSource(_mapManager, bullet);
        if (!source.TryGetTransform(out EntityTransform spawnTransform) ||
            !spawnTransform.HasFiniteValues)
        {
            return;
        }

        var request = new GunTracerSpawnRequest(
            emitter,
            name,
            VtolActorClassifier.GetCoalition(owner.team),
            VtolActorClassifier.GetColor(owner.team),
            spawnTransform,
            source,
            parent.ObjectId);
        if (_tracers.TrySpawn(
            request,
            Time.realtimeSinceStartupAsDouble,
            out GunTracerHandle handle))
        {
            _bullets[bullet] = new TrackedBullet(owner, handle);
        }
    }

    private void OnBulletKilled(Bullet bullet)
    {
        if (_disposed || bullet == null ||
            !_bullets.TryGetValue(bullet, out TrackedBullet tracked))
        {
            return;
        }

        EntityTransform? finalTransform = null;
        try
        {
            Vector3 hitPoint = (Vector3)_hitPointField.GetValue(bullet);
            if (TryMapBulletPoint(bullet, hitPoint, out EntityTransform mapped))
            {
                finalTransform = mapped;
            }
        }
        catch
        {
            // The real Bullet teardown still has an authoritative transform
            // source; the registry will use its last live sample below.
        }

        _tracers.Terminate(tracked.Handle, finalTransform);
        _bullets.Remove(bullet);
    }

    private void OnBulletDisabled(Bullet bullet)
    {
        if (_disposed || bullet == null ||
            !_bullets.TryGetValue(bullet, out TrackedBullet tracked))
        {
            return;
        }

        // OnDisable is the factual pool/teardown boundary when KillBullet was
        // not observed. It is not a timeout and carries no invented event.
        _tracers.Terminate(tracked.Handle);
        _bullets.Remove(bullet);
    }

    private void OnBulletEnabled(Bullet bullet)
    {
        if (_disposed || bullet == null ||
            !_bullets.TryGetValue(bullet, out TrackedBullet tracked))
        {
            return;
        }

        // Defensive generation split for unusual pool ordering. A subsequent
        // Fire always receives a new ACMI object id.
        _tracers.Terminate(tracked.Handle);
        _bullets.Remove(bullet);
    }

    private void TerminateOwnerBullets(Actor owner)
    {
        _bulletsToRemove.Clear();
        foreach (KeyValuePair<Bullet, TrackedBullet> pair in _bullets)
        {
            if (!ReferenceEquals(pair.Value.Owner, owner))
            {
                continue;
            }

            // A null endpoint makes the registry sample the actual live
            // Bullet transform, falling back to its latest real sample when
            // Unity has already torn the pooled component down.
            _tracers.Terminate(pair.Value.Handle);
            _bulletsToRemove.Add(pair.Key);
        }

        for (int index = 0; index < _bulletsToRemove.Count; index++)
        {
            _bullets.Remove(_bulletsToRemove[index]);
        }

        _bulletsToRemove.Clear();
    }

    private Actor FindCannonOwner(VTOLCannon cannon)
    {
        if (cannon != null && _cannonOwners.TryGetValue(cannon, out Actor owner))
        {
            return owner;
        }

        return GetRootActor(cannon == null ? null : cannon.GetComponentInParent<Actor>());
    }

    private bool TryMapBulletPoint(Bullet bullet, Vector3 worldPoint, out EntityTransform transform)
    {
        transform = default;
        if (!IsFinite(worldPoint))
        {
            return false;
        }

        Vector3 velocity;
        try
        {
            velocity = bullet.velocity;
            if (!IsFinite(velocity) || velocity.sqrMagnitude < 0.0001f)
            {
                velocity = bullet.transform.forward;
            }
        }
        catch
        {
            return false;
        }

        return VtolBulletTransformSource.TryMap(
            _mapManager,
            worldPoint,
            velocity,
            out transform);
    }

    private void InstallPatches()
    {
        lock (InstanceGate)
        {
            if (_activeInstance != null)
            {
                throw new InvalidOperationException(
                    "Only one gun Bullet recorder adapter can be active.");
            }

            _activeInstance = this;
        }

        try
        {
            PatchRequired(typeof(Bullet), "Fire", nameof(BulletFirePrefix), nameof(BulletFirePostfix));
            PatchRequired(
                typeof(Bullet),
                "FireWithInitial",
                nameof(BulletFirePrefix),
                nameof(BulletFireWithInitialPostfix));
            PatchRequired(typeof(Bullet), "KillBullet", nameof(BulletKillPrefix), null);
            PatchRequired(typeof(Bullet), "OnDisable", nameof(BulletDisablePrefix), null);
            PatchRequired(typeof(Bullet), "OnEnable", null, nameof(BulletEnablePostfix));
            PatchRequired(
                typeof(VTOLCannon),
                "Fire",
                nameof(CannonFirePrefix),
                nameof(CannonFirePostfix));
            Log("Gun Bullet lifecycle Harmony hooks installed (M61 and VTOLCannon).");
        }
        catch
        {
            lock (InstanceGate)
            {
                if (ReferenceEquals(_activeInstance, this))
                {
                    _activeInstance = null;
                }
            }

            try
            {
                _harmony.UnpatchSelf();
            }
            catch
            {
                // Preserve the original hook failure for the caller.
            }

            throw;
        }
    }

    private void PatchRequired(
        Type declaringType,
        string methodName,
        string prefixName,
        string postfixName)
    {
        MethodInfo[] targets = declaringType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method => method.DeclaringType == declaringType && method.Name == methodName)
            .ToArray();
        if (targets.Length != 1)
        {
            throw new MissingMethodException(
                declaringType.FullName,
                $"{methodName} (expected exactly one method, found {targets.Length})");
        }

        try
        {
            _harmony.Patch(
                targets[0],
                GetHarmonyMethod(prefixName),
                GetHarmonyMethod(postfixName));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Harmony could not patch {declaringType.FullName}.{methodName}.",
                ex);
        }
    }

    private static HarmonyMethod GetHarmonyMethod(string methodName)
    {
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return null;
        }

        MethodInfo patch = typeof(VtolGunTracerAdapter).GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.NonPublic);
        if (patch == null)
        {
            throw new MissingMethodException(typeof(VtolGunTracerAdapter).FullName, methodName);
        }

        return new HarmonyMethod(patch);
    }

    private static void BulletFirePrefix()
    {
        // A Gun callback from a prior Fire call cannot bleed into this new
        // method invocation. Cannon context is owned by Cannon.Fire and is
        // intentionally retained until that method's postfix.
        _pendingGun = null;
        if (_pendingCannon == null)
        {
            _pendingEmitterOwner = null;
        }
    }

    private static void BulletFirePostfix(
        Bullet __instance,
        [HarmonyArgument(11)] Actor sourceActor)
    {
        _activeInstance?.OnBulletFired(__instance, sourceActor);
    }

    private static void BulletFireWithInitialPostfix(
        Bullet __instance,
        [HarmonyArgument(11)] Actor sourceActor)
    {
        _activeInstance?.OnBulletFired(__instance, sourceActor);
    }

    private static void BulletKillPrefix(Bullet __instance)
    {
        _activeInstance?.OnBulletKilled(__instance);
    }

    private static void BulletDisablePrefix(Bullet __instance)
    {
        _activeInstance?.OnBulletDisabled(__instance);
    }

    private static void BulletEnablePostfix(Bullet __instance)
    {
        _activeInstance?.OnBulletEnabled(__instance);
    }

    private static void CannonFirePrefix(VTOLCannon __instance)
    {
        _activeInstance?.BeginCannonFire(__instance);
    }

    private static void CannonFirePostfix(VTOLCannon __instance)
    {
        _activeInstance?.EndCannonFire(__instance);
    }

    private static void ClearPendingContext()
    {
        _pendingGun = null;
        _pendingCannon = null;
        _pendingEmitterOwner = null;
    }

    private static Actor GetRootActor(Actor actor)
    {
        if (actor == null)
        {
            return null;
        }

        Actor current = actor;
        while (current.parentActor != null && !ReferenceEquals(current.parentActor, current))
        {
            current = current.parentActor;
        }

        return current;
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }

    private sealed class TrackedBullet
    {
        public TrackedBullet(Actor owner, GunTracerHandle handle)
        {
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Handle = handle ?? throw new ArgumentNullException(nameof(handle));
        }

        public Actor Owner { get; }

        public GunTracerHandle Handle { get; }
    }

    private void WarnThrottled(string message)
    {
        double nowSeconds = Time.realtimeSinceStartupAsDouble;
        if (nowSeconds < _nextWarningTime)
        {
            return;
        }

        _nextWarningTime = nowSeconds + WarningIntervalSeconds;
        LogWarn(message);
    }

    private sealed class OwnerSubscriptions : IDisposable
    {
        private readonly Actor _owner;
        private readonly Dictionary<Gun, Bullet.BulletFiredDelegate> _gunHandlers =
            new Dictionary<Gun, Bullet.BulletFiredDelegate>(ReferenceEqualityComparer.Instance);
        private readonly HashSet<VTOLCannon> _cannons =
            new HashSet<VTOLCannon>(ReferenceEqualityComparer.Instance);
        private readonly List<Gun> _gunsToRemove = new List<Gun>();
        private readonly List<VTOLCannon> _cannonsToRemove = new List<VTOLCannon>();

        public OwnerSubscriptions(Actor owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public bool ContainsCannon(VTOLCannon cannon)
        {
            return cannon != null && _cannons.Contains(cannon);
        }

        public void Reconcile(Actor owner, VtolGunTracerAdapter adapter)
        {
            if (owner == null || adapter == null)
            {
                return;
            }

            var foundGuns = new HashSet<Gun>(ReferenceEqualityComparer.Instance);
            Gun[] guns = owner.GetComponentsInChildren<Gun>(true);
            for (int index = 0; index < guns.Length; index++)
            {
                Gun gun = guns[index];
                if (gun == null || !foundGuns.Add(gun) || _gunHandlers.ContainsKey(gun))
                {
                    continue;
                }

                try
                {
                    Bullet.BulletFiredDelegate handler = (ray, speed) =>
                        adapter.OnGunFiredBullet(gun, _owner, ray, speed);
                    gun.OnFiredBullet += handler;
                    _gunHandlers.Add(gun, handler);
                }
                catch (Exception ex)
                {
                    adapter.WarnThrottled($"Gun Bullet callback subscription skipped: {ex.Message}");
                }
            }

            _gunsToRemove.Clear();
            foreach (Gun gun in _gunHandlers.Keys)
            {
                if (gun == null || !foundGuns.Contains(gun))
                {
                    _gunsToRemove.Add(gun);
                }
            }

            for (int index = 0; index < _gunsToRemove.Count; index++)
            {
                RemoveGun(_gunsToRemove[index], adapter);
            }

            _gunsToRemove.Clear();

            var foundCannons = new HashSet<VTOLCannon>(ReferenceEqualityComparer.Instance);
            VTOLCannon[] cannons = owner.GetComponentsInChildren<VTOLCannon>(true);
            for (int index = 0; index < cannons.Length; index++)
            {
                VTOLCannon cannon = cannons[index];
                if (cannon == null || !foundCannons.Add(cannon))
                {
                    continue;
                }

                _cannons.Add(cannon);
                adapter._cannonOwners[cannon] = _owner;
            }

            _cannonsToRemove.Clear();
            foreach (VTOLCannon cannon in _cannons)
            {
                if (cannon == null || !foundCannons.Contains(cannon))
                {
                    _cannonsToRemove.Add(cannon);
                }
            }

            for (int index = 0; index < _cannonsToRemove.Count; index++)
            {
                VTOLCannon cannon = _cannonsToRemove[index];
                adapter._tracers.ForgetEmitter(cannon);
                _cannons.Remove(cannon);
                adapter._cannonOwners.Remove(cannon);
            }

            _cannonsToRemove.Clear();
        }

        public void Dispose(VtolGunTracerAdapter adapter)
        {
            foreach (KeyValuePair<Gun, Bullet.BulletFiredDelegate> pair in _gunHandlers)
            {
                adapter?._tracers.ForgetEmitter(pair.Key);
                try
                {
                    if (pair.Key != null)
                    {
                        pair.Key.OnFiredBullet -= pair.Value;
                    }
                }
                catch
                {
                    // Unity teardown must not prevent later subscriptions from
                    // being released.
                }
            }

            if (adapter != null)
            {
                foreach (VTOLCannon cannon in _cannons)
                {
                    adapter._tracers.ForgetEmitter(cannon);
                    adapter._cannonOwners.Remove(cannon);
                }
            }

            _gunHandlers.Clear();
            _cannons.Clear();
            _gunsToRemove.Clear();
            _cannonsToRemove.Clear();
        }

        void IDisposable.Dispose()
        {
            Dispose(null);
        }

        private void RemoveGun(Gun gun, VtolGunTracerAdapter adapter)
        {
            adapter?._tracers.ForgetEmitter(gun);
            if (gun == null || !_gunHandlers.TryGetValue(gun, out Bullet.BulletFiredDelegate handler))
            {
                _gunHandlers.Remove(gun);
                return;
            }

            try
            {
                gun.OnFiredBullet -= handler;
            }
            catch
            {
                // Removing a destroyed Unity component is best effort.
            }

            _gunHandlers.Remove(gun);
        }
    }
}

/// <summary>
/// Transform source for one real pooled Bullet. It samples the game's actual
/// Transform/velocity and maps the position through the current floating
/// origin; it never extrapolates a lifetime-limited ballistic substitute.
/// </summary>
internal sealed class VtolBulletTransformSource : IEntityTransformSource
{
    private readonly VTMapManager _mapManager;
    private readonly Bullet _bullet;

    public VtolBulletTransformSource(VTMapManager mapManager, Bullet bullet)
    {
        _mapManager = mapManager ?? throw new ArgumentNullException(nameof(mapManager));
        _bullet = bullet ?? throw new ArgumentNullException(nameof(bullet));
    }

    public bool TryGetTransform(out EntityTransform transform)
    {
        transform = default;
        try
        {
            if (_bullet == null)
            {
                return false;
            }

            Vector3 velocity = _bullet.velocity;
            if (!IsFinite(velocity) || velocity.sqrMagnitude < 0.0001f)
            {
                velocity = _bullet.transform.forward;
            }

            return TryMap(_mapManager, _bullet.transform.position, velocity, out transform);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryMap(
        VTMapManager mapManager,
        Vector3 worldPosition,
        Vector3 velocity,
        out EntityTransform transform)
    {
        transform = default;
        if (mapManager == null || !IsFinite(worldPosition) || !IsFinite(velocity) ||
            velocity.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        try
        {
            Vector3D globalPosition = VTMapManager.WorldToGlobalPoint(worldPosition);
            Vector3D gpsPosition = mapManager.WorldPositionToGPSCoords(worldPosition);
            Quaternion rotation = Quaternion.LookRotation(velocity.normalized, Vector3.up);
            Vector3 eulerAngles = rotation.eulerAngles;
            Attitude attitude = Attitude.FromUnityEuler(
                eulerAngles.x,
                eulerAngles.y,
                eulerAngles.z);
            transform = VtolTransformMapper.MapEntity(
                gpsPosition.x,
                gpsPosition.y,
                gpsPosition.z,
                globalPosition.x,
                globalPosition.z,
                attitude);
            return transform.HasFiniteValues;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
