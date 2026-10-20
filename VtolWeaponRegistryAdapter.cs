using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using VTOLVRTacviewSPRecorder.Core;

namespace VTOLVRTacviewSPRecorder.Main;

internal sealed class VtolWeaponRegistryAdapter : IDisposable
{
    private const int ActiveWeaponCap = 64;
    private const int FiredListScanCap = 512;
    private const double FiredListResyncIntervalSeconds = 0.25;
    private const double OwnerComponentResyncIntervalSeconds = 5.0;
    private const double MissingWeaponGraceSeconds = 0.25;
    private const double CapWarningIntervalSeconds = 10.0;

    private readonly VTMapManager _mapManager;
    private readonly EntityRegistry _registry;
    private readonly Dictionary<object, TrackedWeapon> _weapons =
        new Dictionary<object, TrackedWeapon>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Actor, OwnerSubscriptions> _owners =
        new Dictionary<Actor, OwnerSubscriptions>();
    private readonly Dictionary<Missile, UnityAction> _pendingMissileFireHandlers =
        new Dictionary<Missile, UnityAction>();
    private readonly Dictionary<object, MissileDescriptor> _missileDescriptors =
        new Dictionary<object, MissileDescriptor>(ReferenceEqualityComparer.Instance);
    private readonly List<PendingRemoval> _pendingRemovals = new List<PendingRemoval>();
    private readonly List<TacviewEvent> _pendingEvents = new List<TacviewEvent>();
    private readonly HashSet<Actor> _activeOwners = new HashSet<Actor>();
    private readonly List<Actor> _ownersToRemove = new List<Actor>();
    private readonly List<object> _weaponsToRemove = new List<object>();
    private readonly List<object> _descriptorKeysToRemove = new List<object>();
    private double _nextFiredListResyncTime;
    private double _nextOwnerComponentResyncTime;
    private double _nextCapWarningTime;
    private double _nextInternalWarningTime;
    private bool _disposed;

    public VtolWeaponRegistryAdapter(VTMapManager mapManager, EntityRegistry registry)
    {
        _mapManager = mapManager ?? throw new ArgumentNullException(nameof(mapManager));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public int ActiveWeaponCount => _weapons.Count;

    public bool NeedsOwnerReconciliation(double nowSeconds)
    {
        return !_disposed && nowSeconds >= _nextOwnerComponentResyncTime;
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

        foreach (TrackedWeapon weapon in _weapons.Values)
        {
            weapon.BeginFrame();
        }

        if (ownerSnapshotComplete)
        {
            ReconcileOwnerSubscriptions(nowSeconds, activeActors);
        }

        if (nowSeconds >= _nextFiredListResyncTime)
        {
            _nextFiredListResyncTime = nowSeconds + FiredListResyncIntervalSeconds;
            ResyncFiredWeapons();
        }

        ProcessWeaponLifecycle(nowSeconds);
    }

    public void PrepareFinalDrain(double nowSeconds, bool settleUnavailable)
    {
        if (_disposed)
        {
            return;
        }

        foreach (TrackedWeapon weapon in _weapons.Values)
        {
            weapon.BeginFrame();
            if (settleUnavailable && !weapon.HasPendingRemoval && !weapon.Source.IsAvailable)
            {
                weapon.MarkForRemoval(
                    TacviewEntityRemovalReason.Deleted,
                    CaptureFinalTransform(weapon));
            }
        }

        ProcessWeaponLifecycle(nowSeconds);
    }

    /// <summary>
    /// Re-resolves each live missile's guidance target (same game members as
    /// at launch) and updates its LockedTarget when it changed, e.g. a
    /// missile that acquires or switches targets after launch. A lost lock
    /// keeps the last target: ACMI relationship clearing is not written.
    /// </summary>
    public void RefreshMissileTargets()
    {
        if (_disposed)
        {
            return;
        }

        foreach (TrackedWeapon weapon in _weapons.Values)
        {
            Missile missile = weapon.MissileInstance;
            if (missile == null || weapon.HasPendingRemoval)
            {
                continue;
            }

            try
            {
                ulong? targetId = ResolveObjectId(ResolveMissileTarget(missile));
                if (!targetId.HasValue || targetId == weapon.TargetObjectId)
                {
                    continue;
                }

                // Never re-register an identity the registry already dropped.
                if (!_registry.TryGet(missile, out TrackedEntity entity) ||
                    !ReferenceEquals(entity, weapon.Entity))
                {
                    continue;
                }

                EntityMetadata current = entity.Metadata;
                _registry.Register(
                    missile,
                    entity.Kind,
                    false,
                    current.WithRelationships(current.ParentObjectId, targetId, current.NextObjectId));
                weapon.TargetObjectId = targetId;
            }
            catch (Exception ex)
            {
                WarnThrottled($"Missile target refresh skipped: {ex.Message}");
            }
        }
    }

    public void AppendSamplingTargetsTo(List<EntitySamplingTarget> destination)
    {
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        foreach (TrackedWeapon weapon in _weapons.Values)
        {
            if (weapon.CanSample)
            {
                destination.Add(new EntitySamplingTarget(weapon.Entity, weapon.Source));
            }
        }
    }

    public void TrackOwner(Actor owner)
    {
        if (_disposed || owner == null || _owners.ContainsKey(owner))
        {
            return;
        }

        try
        {
            _owners.Add(owner, AttachOwner(owner));
            if (_nextOwnerComponentResyncTime <= 0.0)
            {
                _nextOwnerComponentResyncTime =
                    Time.realtimeSinceStartupAsDouble + OwnerComponentResyncIntervalSeconds;
            }
        }
        catch (Exception ex)
        {
            WarnThrottled($"Weapon event subscription skipped for '{SafeActorName(owner)}': {ex.Message}");
        }
    }

    public void UntrackOwner(Actor owner)
    {
        if (ReferenceEquals(owner, null) || !_owners.TryGetValue(owner, out OwnerSubscriptions subscriptions))
        {
            return;
        }

        subscriptions.Dispose();
        _owners.Remove(owner);
        RemoveMissileDescriptorsForOwner(owner);
    }

    public void DrainFrameDataTo(
        List<TacviewEntityUpdate> updates,
        List<TacviewEvent> events)
    {
        if (updates == null)
        {
            throw new ArgumentNullException(nameof(updates));
        }

        if (events == null)
        {
            throw new ArgumentNullException(nameof(events));
        }

        foreach (TrackedWeapon weapon in _weapons.Values)
        {
            if (weapon.TryQueueMandatorySnapshot(out EntityTransform transform))
            {
                updates.Add(TacviewEntityUpdate.Upsert(
                    weapon.Entity.ObjectId,
                    transform,
                    weapon.Entity.Metadata));
            }
        }

        for (int index = 0; index < _pendingEvents.Count; index++)
        {
            events.Add(_pendingEvents[index]);
        }

        for (int index = 0; index < _pendingRemovals.Count; index++)
        {
            PendingRemoval removal = _pendingRemovals[index];
            updates.Add(TacviewEntityUpdate.Delete(
                removal.Entity.ObjectId,
                removal.Reason,
                removal.Label));
        }

        _pendingEvents.Clear();
        _pendingRemovals.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (OwnerSubscriptions subscriptions in _owners.Values)
        {
            subscriptions.Dispose();
        }

        foreach (KeyValuePair<Missile, UnityAction> pending in _pendingMissileFireHandlers)
        {
            if (pending.Key != null)
            {
                pending.Key.OnFired -= pending.Value;
            }
        }

        foreach (TrackedWeapon weapon in _weapons.Values)
        {
            weapon.Dispose();
        }

        _owners.Clear();
        _pendingMissileFireHandlers.Clear();
        _missileDescriptors.Clear();
        _weapons.Clear();
        _pendingRemovals.Clear();
        _pendingEvents.Clear();
        _activeOwners.Clear();
        _ownersToRemove.Clear();
        _weaponsToRemove.Clear();
        _descriptorKeysToRemove.Clear();
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
            Actor actor = activeActors[index];
            if (actor == null || !_activeOwners.Add(actor))
            {
                continue;
            }

            if (_owners.TryGetValue(actor, out OwnerSubscriptions existing))
            {
                existing.Dispose();
                _owners.Remove(actor);
                RemoveMissileDescriptorsForOwner(actor);
            }

            TrackOwner(actor);
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

    private void RemoveMissileDescriptorsForOwner(Actor owner)
    {
        _descriptorKeysToRemove.Clear();
        foreach (KeyValuePair<object, MissileDescriptor> descriptor in _missileDescriptors)
        {
            if (ReferenceEquals(descriptor.Value.Owner, owner))
            {
                _descriptorKeysToRemove.Add(descriptor.Key);
            }
        }

        for (int index = 0; index < _descriptorKeysToRemove.Count; index++)
        {
            _missileDescriptors.Remove(_descriptorKeysToRemove[index]);
        }

        _descriptorKeysToRemove.Clear();
    }

    private OwnerSubscriptions AttachOwner(Actor owner)
    {
        var subscriptions = new OwnerSubscriptions();

        try
        {

        WeaponManager[] weaponManagers = owner.GetComponentsInChildren<WeaponManager>(true);
        for (int index = 0; index < weaponManagers.Length; index++)
        {
            WeaponManager weaponManager = weaponManagers[index];
            if (weaponManager == null)
            {
                continue;
            }

            Action<Missile> handler = missile => OnMissileAnnounced(missile, owner);
            weaponManager.OnFiredMissile += handler;
            subscriptions.Add(new DelegateSubscription(
                () =>
                {
                    if (weaponManager != null)
                    {
                        weaponManager.OnFiredMissile -= handler;
                    }
                }));
        }

        var attachedMissileLaunchers = new HashSet<MissileLauncher>();
        HPEquipMissileLauncher[] missileEquips =
            owner.GetComponentsInChildren<HPEquipMissileLauncher>(true);
        for (int index = 0; index < missileEquips.Length; index++)
        {
            HPEquipMissileLauncher equip = missileEquips[index];
            MissileLauncher launcher = equip?.ml;
            if (launcher == null || !attachedMissileLaunchers.Add(launcher))
            {
                continue;
            }

            EntityKind? forcedKind = IsBombEquip(equip) ? EntityKind.Bomb : (EntityKind?)null;
            string label = VtolActorClassifier.FirstNonEmpty(equip.shortName, equip.fullName);
            RememberLoadedMissiles(launcher, owner, forcedKind, label);
            Action<Missile> loadHandler = missile =>
                RememberMissileDescriptor(missile, owner, forcedKind, label);
            launcher.OnLoadMissile += loadHandler;
            subscriptions.Add(new DelegateSubscription(
                () =>
                {
                    if (launcher != null)
                    {
                        launcher.OnLoadMissile -= loadHandler;
                    }
                }));
            Action<int> handler = firedIndex =>
                OnMissileLauncherFired(launcher, firedIndex, owner, forcedKind, label);
            launcher.OnFiredMissileIdx += handler;
            subscriptions.Add(new DelegateSubscription(
                () =>
                {
                    if (launcher != null)
                    {
                        launcher.OnFiredMissileIdx -= handler;
                    }
                }));
        }

        MissileLauncher[] directMissileLaunchers =
            owner.GetComponentsInChildren<MissileLauncher>(true);
        for (int index = 0; index < directMissileLaunchers.Length; index++)
        {
            MissileLauncher launcher = directMissileLaunchers[index];
            if (launcher == null || !attachedMissileLaunchers.Add(launcher))
            {
                continue;
            }

            string label = launcher.missilePrefab == null
                ? null
                : launcher.missilePrefab.name;
            Action<int> handler = firedIndex =>
                OnMissileLauncherFired(launcher, firedIndex, owner, null, label);
            launcher.OnFiredMissileIdx += handler;
            subscriptions.Add(new DelegateSubscription(
                () =>
                {
                    if (launcher != null)
                    {
                        launcher.OnFiredMissileIdx -= handler;
                    }
                }));
        }

        RocketLauncher[] rocketLaunchers = owner.GetComponentsInChildren<RocketLauncher>(true);
        for (int index = 0; index < rocketLaunchers.Length; index++)
        {
            RocketLauncher launcher = rocketLaunchers[index];
            if (launcher == null)
            {
                continue;
            }

            Action<Rocket> handler = rocket => RegisterRocket(
                rocket,
                owner,
                VtolActorClassifier.FirstNonEmpty(launcher.shortName, launcher.fullName));
            launcher.OnFiredRocket += handler;
            subscriptions.Add(new DelegateSubscription(
                () =>
                {
                    if (launcher != null)
                    {
                        launcher.OnFiredRocket -= handler;
                    }
                }));
        }

            return subscriptions;
        }
        catch
        {
            subscriptions.Dispose();
            throw;
        }
    }

    private void OnMissileAnnounced(Missile missile, Actor owner)
    {
        if (_disposed || missile == null || _weapons.ContainsKey(missile) ||
            _pendingMissileFireHandlers.ContainsKey(missile))
        {
            return;
        }

        UnityAction fireHandler = null;
        fireHandler = () =>
        {
            if (missile != null)
            {
                missile.OnFired -= fireHandler;
            }

            _pendingMissileFireHandlers.Remove(missile);
            RegisterMissile(missile, owner, null, null);
        };
        _pendingMissileFireHandlers.Add(missile, fireHandler);
        missile.OnFired += fireHandler;
    }

    private void OnMissileLauncherFired(
        MissileLauncher launcher,
        int firedIndex,
        Actor owner,
        EntityKind? forcedKind,
        string label)
    {
        try
        {
            Missile[] missiles = launcher?.missiles;
            if (missiles == null || firedIndex < 0 || firedIndex >= missiles.Length)
            {
                return;
            }

            RegisterMissile(missiles[firedIndex], owner, forcedKind, label);
        }
        catch (Exception ex)
        {
            WarnThrottled($"Missile launch event was not recorded for '{SafeActorName(owner)}': {ex.Message}");
        }
    }

    private void RememberLoadedMissiles(
        MissileLauncher launcher,
        Actor owner,
        EntityKind? forcedKind,
        string label)
    {
        Missile[] missiles = launcher.missiles;
        if (missiles == null)
        {
            return;
        }

        for (int index = 0; index < missiles.Length; index++)
        {
            RememberMissileDescriptor(missiles[index], owner, forcedKind, label);
        }
    }

    private void RememberMissileDescriptor(
        Missile missile,
        Actor owner,
        EntityKind? forcedKind,
        string label)
    {
        if (missile == null)
        {
            return;
        }

        _missileDescriptors[missile] = new MissileDescriptor(
            owner,
            forcedKind,
            label);
    }

    private void ResyncFiredWeapons()
    {
        try
        {
            List<Missile> missiles = Missile.allFiredMissiles;
            if (missiles != null)
            {
                int startIndex = Math.Max(0, missiles.Count - FiredListScanCap);
                if (startIndex > 0)
                {
                    WarnThrottled($"Missile safety resync limited to the newest {FiredListScanCap} entries.");
                }

                for (int index = startIndex; index < missiles.Count; index++)
                {
                    Missile missile = missiles[index];
                    RegisterMissile(missile, missile == null ? null : missile.launchedByActor, null, null);
                }
            }
        }
        catch (Exception ex)
        {
            WarnThrottled($"Missile safety resync failed: {ex.Message}");
        }

        try
        {
            List<Rocket> rockets = Rocket.allFiredRockets;
            if (rockets != null)
            {
                int startIndex = Math.Max(0, rockets.Count - FiredListScanCap);
                if (startIndex > 0)
                {
                    WarnThrottled($"Rocket safety resync limited to the newest {FiredListScanCap} entries.");
                }

                for (int index = startIndex; index < rockets.Count; index++)
                {
                    RegisterRocket(rockets[index], null, null);
                }
            }
        }
        catch (Exception ex)
        {
            WarnThrottled($"Rocket safety resync failed: {ex.Message}");
        }
    }

    private void RegisterMissile(
        Missile missile,
        Actor ownerHint,
        EntityKind? forcedKind,
        string labelHint)
    {
        if (_disposed || missile == null || _weapons.ContainsKey(missile))
        {
            return;
        }

        if (!TryReserveWeaponSlot("missile"))
        {
            return;
        }

        UnityAction<Missile> detonationHandler = null;
        bool registered = false;
        try
        {
            if (_missileDescriptors.TryGetValue(missile, out MissileDescriptor descriptor))
            {
                ownerHint = ownerHint ?? descriptor.Owner;
                forcedKind = forcedKind ?? descriptor.ForcedKind;
                labelHint = VtolActorClassifier.FirstNonEmpty(labelHint, descriptor.Label);
            }

            Actor owner = GetRootActor(missile.launchedByActor ?? ownerHint);
            EntityKind kind = forcedKind ?? ClassifyMissile(missile);
            ulong? parentId = ResolveObjectId(owner);
            ulong? targetId = ResolveObjectId(ResolveMissileTarget(missile));
            string name = VtolActorClassifier.FirstNonEmpty(
                labelHint,
                missile.gameObject == null ? null : missile.gameObject.name,
                kind == EntityKind.Bomb ? "Bomb" : "Missile");
            EntityMetadata metadata = BuildWeaponMetadata(
                kind == EntityKind.Bomb ? "Weapon+Bomb" : "Weapon+Missile",
                name,
                owner,
                parentId,
                targetId);
            TrackedEntity entity = _registry.Register(missile, kind, false, metadata);
            registered = true;
            detonationHandler = detonated => OnMissileDetonated(detonated);
            missile.OnMissileDetonated += detonationHandler;
            var source = new VtolEntitySource(
                missile.transform,
                _mapManager,
                () => missile != null && !missile.hasDetonated);
            EntityTransform? initialTransform = CaptureCurrentTransform(source);
            _weapons.Add(missile, TrackedWeapon.ForMissile(
                entity,
                missile,
                source,
                detonationHandler,
                parentId,
                targetId,
                name,
                initialTransform));
            _missileDescriptors.Remove(missile);
            Log($"{kind} registered: {entity.ObjectId:X} '{name}'" +
                (parentId.HasValue ? $" parent {parentId.Value:X}." : "."));
        }
        catch (Exception ex)
        {
            if (missile != null && detonationHandler != null)
            {
                missile.OnMissileDetonated -= detonationHandler;
            }

            if (registered)
            {
                _registry.Remove(missile, out TrackedEntity _);
            }

            WarnThrottled($"Missile registration failed: {ex.Message}");
        }
    }

    private void RegisterRocket(Rocket rocket, Actor ownerHint, string labelHint)
    {
        if (_disposed || rocket == null || _weapons.ContainsKey(rocket))
        {
            return;
        }

        if (!TryReserveWeaponSlot("rocket"))
        {
            return;
        }

        UnityAction<Rocket> detonationHandler = null;
        bool registered = false;
        try
        {
            Actor owner = GetRootActor(ownerHint);
            ulong? parentId = ResolveObjectId(owner);
            string name = VtolActorClassifier.FirstNonEmpty(
                labelHint,
                rocket.gameObject == null ? null : rocket.gameObject.name,
                "Rocket");
            EntityMetadata metadata = BuildWeaponMetadata(
                "Weapon+Rocket",
                name,
                owner,
                parentId,
                null);
            TrackedEntity entity = _registry.Register(
                rocket,
                EntityKind.Rocket,
                false,
                metadata);
            registered = true;
            detonationHandler = detonated => OnRocketDetonated(detonated);
            rocket.OnDetonated += detonationHandler;
            var source = new VtolEntitySource(
                rocket.transform,
                _mapManager,
                () => rocket != null && rocket.gameObject.activeInHierarchy);
            EntityTransform? initialTransform = CaptureCurrentTransform(source);
            _weapons.Add(rocket, TrackedWeapon.ForRocket(
                entity,
                rocket,
                source,
                detonationHandler,
                parentId,
                name,
                initialTransform));
            Log($"Rocket registered: {entity.ObjectId:X} '{name}'" +
                (parentId.HasValue ? $" parent {parentId.Value:X}." : "."));
        }
        catch (Exception ex)
        {
            if (rocket != null && detonationHandler != null)
            {
                rocket.OnDetonated -= detonationHandler;
            }

            if (registered)
            {
                _registry.Remove(rocket, out TrackedEntity _);
            }

            WarnThrottled($"Rocket registration failed: {ex.Message}");
        }
    }

    private void OnMissileDetonated(Missile missile)
    {
        if (_disposed || missile == null || !_weapons.TryGetValue(missile, out TrackedWeapon weapon))
        {
            return;
        }

        if (missile.missed && weapon.ParentObjectId.HasValue)
        {
            weapon.MarkForRemoval(
                TacviewEntityRemovalReason.Deleted,
                CaptureFinalTransform(weapon),
                TacviewEvent.Timeout(
                weapon.ParentObjectId.Value,
                weapon.Label,
                weapon.TargetObjectId,
                "Miss"));
        }
        else
        {
            weapon.MarkForRemoval(
                TacviewEntityRemovalReason.Destroyed,
                CaptureFinalTransform(weapon));
        }
    }

    private void OnRocketDetonated(Rocket rocket)
    {
        if (_disposed || rocket == null || !_weapons.TryGetValue(rocket, out TrackedWeapon weapon))
        {
            return;
        }

        weapon.MarkForRemoval(
            TacviewEntityRemovalReason.Destroyed,
            CaptureFinalTransform(weapon));
    }

    private static EntityTransform? CaptureFinalTransform(TrackedWeapon weapon)
    {
        return CaptureCurrentTransform(weapon.Source);
    }

    private static EntityTransform? CaptureCurrentTransform(VtolEntitySource source)
    {
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

    private void ProcessWeaponLifecycle(double nowSeconds)
    {
        _weaponsToRemove.Clear();
        foreach (KeyValuePair<object, TrackedWeapon> pair in _weapons)
        {
            TrackedWeapon weapon = pair.Value;
            if (weapon.HasPendingRemoval)
            {
                if (weapon.CanFinalizeRemoval)
                {
                    _weaponsToRemove.Add(pair.Key);
                }

                continue;
            }

            if (weapon.Source.IsAvailable)
            {
                weapon.ClearMissingSince();
                continue;
            }

            if (!weapon.MissingSinceSeconds.HasValue)
            {
                weapon.MarkMissing(nowSeconds);
            }
            else if (weapon.HasMissingTimedOut(nowSeconds, MissingWeaponGraceSeconds))
            {
                weapon.MarkForRemoval(
                    TacviewEntityRemovalReason.Deleted,
                    CaptureFinalTransform(weapon));
            }
        }

        for (int index = 0; index < _weaponsToRemove.Count; index++)
        {
            RemoveWeapon(_weaponsToRemove[index]);
        }

        _weaponsToRemove.Clear();
    }

    private void RemoveWeapon(object identity)
    {
        if (!_weapons.TryGetValue(identity, out TrackedWeapon weapon))
        {
            return;
        }

        weapon.Dispose();
        _weapons.Remove(identity);
        if (_registry.Remove(identity, out TrackedEntity removed))
        {
            if (!weapon.InitialSnapshotEmitted)
            {
                WarnThrottled($"{removed.Kind} {removed.ObjectId:X} ended before a valid ACMI snapshot; its delete is suppressed.");
                return;
            }

            if (weapon.TimeoutEvent.HasValue)
            {
                _pendingEvents.Add(weapon.TimeoutEvent.Value);
            }

            _pendingRemovals.Add(new PendingRemoval(
                removed,
                weapon.RemovalReason,
                weapon.Label));
            Log($"{removed.Kind} removed: {removed.ObjectId:X} '{weapon.Label}' " +
                $"({weapon.RemovalReason}).");
        }
    }

    private bool TryReserveWeaponSlot(string kind)
    {
        if (_weapons.Count < ActiveWeaponCap)
        {
            return true;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        if (now >= _nextCapWarningTime)
        {
            _nextCapWarningTime = now + CapWarningIntervalSeconds;
            LogWarn($"Active weapon capture cap ({ActiveWeaponCap}) reached; additional {kind}s are skipped.");
        }

        return false;
    }

    private void WarnThrottled(string message)
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (now < _nextInternalWarningTime)
        {
            return;
        }

        _nextInternalWarningTime = now + CapWarningIntervalSeconds;
        LogWarn(message);
    }

    private EntityMetadata BuildWeaponMetadata(
        string type,
        string name,
        Actor owner,
        ulong? parentId,
        ulong? targetId)
    {
        return new EntityMetadata(
            type,
            name,
            owner == null ? "Neutral" : VtolActorClassifier.GetCoalition(owner.team),
            owner == null ? "White" : VtolActorClassifier.GetColor(owner.team),
            parentObjectId: parentId,
            lockedTargetObjectId: targetId);
    }

    private ulong? ResolveObjectId(Actor actor)
    {
        Actor root = GetRootActor(actor);
        return root != null && _registry.TryGet(root, out TrackedEntity entity)
            ? entity.ObjectId
            : (ulong?)null;
    }

    private static Actor ResolveMissileTarget(Missile missile)
    {
        if (missile.radarLock != null && missile.radarLock.actor != null)
        {
            return missile.radarLock.actor;
        }

        return missile.opticalTargetActor != null
            ? missile.opticalTargetActor
            : missile.antiRadTargetActor != null
                ? missile.antiRadTargetActor
                : null;
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

    private static EntityKind ClassifyMissile(Missile missile)
    {
        return missile.guidanceMode == Missile.GuidanceModes.Bomb
            ? EntityKind.Bomb
            : EntityKind.Missile;
    }

    private static bool IsBombEquip(HPEquipMissileLauncher equip)
    {
        return equip is HPEquipBombRack ||
            equip is HPEquipBombDragRack ||
            equip is HPEquipGPSBombRack ||
            equip is HPEquipLaserBombRack ||
            equip is HPEquipSFBombRack;
    }

    private static string SafeActorName(Actor actor)
    {
        return actor == null
            ? "<destroyed>"
            : VtolActorClassifier.FirstNonEmpty(actor.actorName, actor.name, "<unnamed>");
    }

    private sealed class TrackedWeapon : IDisposable
    {
        private readonly Missile _missile;
        private readonly Rocket _rocket;
        private readonly UnityAction<Missile> _missileDetonationHandler;
        private readonly UnityAction<Rocket> _rocketDetonationHandler;
        private readonly SampledEntityLifecycleState _lifecycle;

        private TrackedWeapon(
            TrackedEntity entity,
            VtolEntitySource source,
            Missile missile,
            Rocket rocket,
            UnityAction<Missile> missileDetonationHandler,
            UnityAction<Rocket> rocketDetonationHandler,
            ulong? parentObjectId,
            ulong? targetObjectId,
            string label,
            EntityTransform? initialTransform)
        {
            Entity = entity;
            Source = source;
            _missile = missile;
            _rocket = rocket;
            _missileDetonationHandler = missileDetonationHandler;
            _rocketDetonationHandler = rocketDetonationHandler;
            ParentObjectId = parentObjectId;
            TargetObjectId = targetObjectId;
            Label = label;
            _lifecycle = new SampledEntityLifecycleState(initialTransform);
        }

        public TrackedEntity Entity { get; }

        public VtolEntitySource Source { get; }

        public ulong? ParentObjectId { get; }

        public ulong? TargetObjectId { get; set; }

        public Missile MissileInstance => _missile;

        public string Label { get; }

        public bool HasPendingRemoval => _lifecycle.HasPendingRemoval;

        public bool CanSample => _lifecycle.CanSample;

        public TacviewEntityRemovalReason RemovalReason => _lifecycle.RemovalReason;

        public double? MissingSinceSeconds => _lifecycle.MissingSinceSeconds;

        public EntityTransform? FinalTransform => _lifecycle.FinalTransform;

        public bool FinalSnapshotEmitted => _lifecycle.FinalSnapshotEmitted;

        public TacviewEvent? TimeoutEvent { get; private set; }

        public EntityTransform? InitialTransform => _lifecycle.InitialTransform;

        public bool InitialSnapshotEmitted => _lifecycle.InitialSnapshotEmitted;

        public bool CanFinalizeRemoval => _lifecycle.CanFinalizeRemoval;

        public static TrackedWeapon ForMissile(
            TrackedEntity entity,
            Missile missile,
            VtolEntitySource source,
            UnityAction<Missile> detonationHandler,
            ulong? parentObjectId,
            ulong? targetObjectId,
            string label,
            EntityTransform? initialTransform)
        {
            return new TrackedWeapon(
                entity,
                source,
                missile,
                null,
                detonationHandler,
                null,
                parentObjectId,
                targetObjectId,
                label,
                initialTransform);
        }

        public static TrackedWeapon ForRocket(
            TrackedEntity entity,
            Rocket rocket,
            VtolEntitySource source,
            UnityAction<Rocket> detonationHandler,
            ulong? parentObjectId,
            string label,
            EntityTransform? initialTransform)
        {
            return new TrackedWeapon(
                entity,
                source,
                null,
                rocket,
                null,
                detonationHandler,
                parentObjectId,
                null,
                label,
                initialTransform);
        }

        public void MarkMissing(double nowSeconds)
        {
            _lifecycle.MarkMissing(nowSeconds);
        }

        public void ClearMissingSince()
        {
            _lifecycle.ClearMissingSince();
        }

        public bool HasMissingTimedOut(double nowSeconds, double graceSeconds)
        {
            return _lifecycle.HasMissingTimedOut(nowSeconds, graceSeconds);
        }

        public void BeginFrame()
        {
            _lifecycle.BeginFrame();
        }

        public bool TryQueueMandatorySnapshot(out EntityTransform transform)
        {
            if (_lifecycle.NeedsInitialCapture)
            {
                try
                {
                    if (Source.TryGetCurrentTransform(out EntityTransform current) &&
                        current.HasFiniteValues)
                    {
                        _lifecycle.TrySetInitialTransform(current);
                    }
                }
                catch
                {
                    // The next batch can retry while the weapon remains active.
                }
            }

            return _lifecycle.TryQueueMandatorySnapshot(out transform);
        }

        public void MarkForRemoval(
            TacviewEntityRemovalReason reason,
            EntityTransform? finalTransform,
            TacviewEvent? timeoutEvent = null)
        {
            if (_lifecycle.MarkForRemoval(reason, finalTransform))
            {
                TimeoutEvent = timeoutEvent;
            }
        }

        public void Dispose()
        {
            if (_missile != null && _missileDetonationHandler != null)
            {
                _missile.OnMissileDetonated -= _missileDetonationHandler;
            }

            if (_rocket != null && _rocketDetonationHandler != null)
            {
                _rocket.OnDetonated -= _rocketDetonationHandler;
            }
        }
    }

    private sealed class OwnerSubscriptions : IDisposable
    {
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        public void Add(IDisposable subscription)
        {
            _subscriptions.Add(subscription);
        }

        public void Dispose()
        {
            for (int index = _subscriptions.Count - 1; index >= 0; index--)
            {
                _subscriptions[index].Dispose();
            }

            _subscriptions.Clear();
        }
    }

    private sealed class DelegateSubscription : IDisposable
    {
        private Action _unsubscribe;

        public DelegateSubscription(Action unsubscribe)
        {
            _unsubscribe = unsubscribe;
        }

        public void Dispose()
        {
            Action unsubscribe = _unsubscribe;
            _unsubscribe = null;
            unsubscribe?.Invoke();
        }
    }

    private readonly struct PendingRemoval
    {
        public PendingRemoval(
            TrackedEntity entity,
            TacviewEntityRemovalReason reason,
            string label)
        {
            Entity = entity;
            Reason = reason;
            Label = label;
        }

        public TrackedEntity Entity { get; }

        public TacviewEntityRemovalReason Reason { get; }

        public string Label { get; }
    }

    private readonly struct MissileDescriptor
    {
        public MissileDescriptor(
            Actor owner,
            EntityKind? forcedKind,
            string label)
        {
            Owner = owner;
            ForcedKind = forcedKind;
            Label = label;
        }

        public Actor Owner { get; }

        public EntityKind? ForcedKind { get; }

        public string Label { get; }
    }
}
