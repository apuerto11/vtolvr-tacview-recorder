using System;
using UnityEngine;
using VTOLVRTacviewSPRecorder.Core;

namespace VTOLVRTacviewSPRecorder.Main;

internal static class VtolActorClassifier
{
    public static bool TryClassify(
        Actor actor,
        bool isOwnship,
        out EntityKind kind,
        out SamplingClass samplingClass)
    {
        kind = default;
        samplingClass = default;

        if (actor == null || !actor.alive || !actor.isActiveAndEnabled || actor.parentActor != null)
        {
            return false;
        }

        Actor.OverrideReplayEntityTypes replayType = actor.overrideReplayEntityType;
        if (replayType == Actor.OverrideReplayEntityTypes.Air ||
            replayType == Actor.OverrideReplayEntityTypes.Chopper)
        {
            kind = EntityKind.Aircraft;
            samplingClass = isOwnship ? SamplingClass.Ownship : SamplingClass.Aircraft;
            return true;
        }

        if (replayType == Actor.OverrideReplayEntityTypes.Ground)
        {
            kind = EntityKind.Ground;
            samplingClass = SamplingClass.Surface;
            return true;
        }

        if (replayType == Actor.OverrideReplayEntityTypes.Sea)
        {
            kind = EntityKind.Sea;
            samplingClass = SamplingClass.Surface;
            return true;
        }

        if (replayType != Actor.OverrideReplayEntityTypes.None)
        {
            return false;
        }

        Actor.Roles role = actor.role;
        bool isAir = HasRole(role, Actor.Roles.Air);
        bool isGround = HasRole(role, Actor.Roles.Ground) ||
            HasRole(role, Actor.Roles.GroundArmor);
        bool isSea = HasRole(role, Actor.Roles.Ship);

        if (isAir && !isGround && !isSea)
        {
            kind = EntityKind.Aircraft;
            samplingClass = isOwnship ? SamplingClass.Ownship : SamplingClass.Aircraft;
            return true;
        }

        if (isGround && !isAir && !isSea)
        {
            kind = EntityKind.Ground;
            samplingClass = SamplingClass.Surface;
            return true;
        }

        if (isSea && !isAir && !isGround)
        {
            kind = EntityKind.Sea;
            samplingClass = SamplingClass.Surface;
            return true;
        }

        return false;
    }

    public static EntityMetadata BuildMetadata(Actor actor, EntityKind kind, bool isOwnship)
    {
        if (actor == null)
        {
            throw new ArgumentNullException(nameof(actor));
        }

        UnitSpawn unitSpawn = actor.unitSpawn;
        string displayName = GetDisplayName(actor, unitSpawn);
        string platformName = kind == EntityKind.Aircraft
            ? GetAircraftPlatformName(actor, unitSpawn)
            : kind == EntityKind.Ground
                ? GetGroundPlatformName(actor, unitSpawn)
                : kind == EntityKind.Sea
                    ? GetSeaPlatformName(actor, unitSpawn)
                    : null;
        string fallbackName = kind == EntityKind.Ground
            ? "Ground Unit"
            : kind == EntityKind.Sea ? "Sea Unit" : "Aircraft";
        string name = kind == EntityKind.Aircraft
            ? FirstNonEmpty(platformName, displayName, fallbackName)
            : FirstNonEmpty(displayName, platformName, fallbackName);
        string callSign = !string.IsNullOrWhiteSpace(displayName) &&
            !string.Equals(name, displayName, StringComparison.Ordinal)
            ? displayName
            : null;
        string shape = null;
        if (kind == EntityKind.Aircraft)
        {
            VtolAircraftShapeCatalog.TryGetShape(name, out shape);
        }
        else if (kind == EntityKind.Ground)
        {
            VtolGroundShapeCatalog.TryGetShape(platformName, out shape);
        }
        else if (kind == EntityKind.Sea)
        {
            VtolSeaShapeCatalog.TryGetShape(
                platformName,
                IsExactStockCarrierSpawn(unitSpawn),
                out shape);
        }

        return new EntityMetadata(
            GetTacviewType(actor, kind),
            name,
            GetCoalition(actor.team),
            GetColor(actor.team),
            callSign,
            GetPilotName(isOwnship),
            shape: shape);
    }

    public static string GetCoalition(Teams team)
    {
        return TacviewCoalitionPalette.GetCoalition(GetSide(team));
    }

    public static string GetColor(Teams team)
    {
        return TacviewCoalitionPalette.GetColor(GetSide(team));
    }

    public static string GetActorLabel(Actor actor, EntityKind kind)
    {
        try
        {
            EntityMetadata metadata = BuildMetadata(actor, kind, actor != null && actor.isPlayer);
            return FirstNonEmpty(metadata.CallSign, metadata.Name, kind.ToString());
        }
        catch
        {
            return actor == null
                ? kind.ToString()
                : FirstNonEmpty(actor.actorName, actor.name, kind.ToString());
        }
    }

    public static string FirstNonEmpty(params string[] values)
    {
        for (int index = 0; index < values.Length; index++)
        {
            string value = values[index];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static bool HasRole(Actor.Roles roles, Actor.Roles role)
    {
        return (roles & role) == role;
    }

    private static TacviewCoalitionSide GetSide(Teams team)
    {
        return team == Teams.Allied
            ? TacviewCoalitionSide.Allied
            : team == Teams.Enemy
                ? TacviewCoalitionSide.Enemy
                : TacviewCoalitionSide.Neutral;
    }

    private static string GetTacviewType(Actor actor, EntityKind kind)
    {
        if (kind == EntityKind.Ground)
        {
            return "Ground+Vehicle";
        }

        if (kind == EntityKind.Sea)
        {
            return "Sea+Watercraft";
        }

        VehicleMaster vehicleMaster = actor.GetComponent<VehicleMaster>();
        if (vehicleMaster != null)
        {
            return vehicleMaster.isHelicopter ? "Air+Rotorcraft" : "Air+FixedWing";
        }

        return actor.GetComponent<KinematicPlane>() != null ? "Air+FixedWing" : "Air";
    }

    private static string GetAircraftPlatformName(Actor actor, UnitSpawn unitSpawn)
    {
        VehicleMaster vehicleMaster = actor.GetComponent<VehicleMaster>();
        if (vehicleMaster != null && vehicleMaster.playerVehicle != null &&
            !string.IsNullOrWhiteSpace(vehicleMaster.playerVehicle.vehicleName))
        {
            return vehicleMaster.playerVehicle.vehicleName;
        }

        if (unitSpawn is AIAircraftSpawn aiAircraftSpawn &&
            !string.IsNullOrWhiteSpace(aiAircraftSpawn.vehicleName))
        {
            return aiAircraftSpawn.vehicleName;
        }

        ExternalVehicleInfo externalVehicleInfo = actor.GetComponent<ExternalVehicleInfo>();
        return externalVehicleInfo != null
            ? FirstNonEmpty(externalVehicleInfo.vehicleName)
            : null;
    }

    private static string GetGroundPlatformName(Actor actor, UnitSpawn unitSpawn)
    {
        string spawnedUnitDisplayName = unitSpawn?.unitSpawner != null
            ? unitSpawn.unitSpawner.GetUIDisplayName()
            : null;
        return FirstNonEmpty(
            actor.actorName,
            spawnedUnitDisplayName,
            unitSpawn?.unitName);
    }

    private static string GetSeaPlatformName(Actor actor, UnitSpawn unitSpawn)
    {
        string spawnedUnitDisplayName = unitSpawn?.unitSpawner != null
            ? unitSpawn.unitSpawner.GetUIDisplayName()
            : null;

        // Runtime actors are decorated with scenario designations such as
        // "Escort Cruiser 11" and "Carrier 61". Shape selection must use
        // the original prefab unit label when AISeaUnitSpawn makes that real
        // source available; it remains a strict exact catalog key.
        string canonicalPrefabActorName = GetSeaPrefabActorName(unitSpawn);
        return VtolSeaPlatformNameResolver.Resolve(
            canonicalPrefabActorName,
            actor?.actorName,
            spawnedUnitDisplayName,
            unitSpawn?.unitName);
    }

    private static string GetSeaPrefabActorName(UnitSpawn unitSpawn)
    {
        if (!(unitSpawn is AISeaUnitSpawn seaUnitSpawn) ||
            seaUnitSpawn.unitSpawner == null ||
            seaUnitSpawn.unitSpawner.prefabUnitSpawn == null ||
            seaUnitSpawn.unitSpawner.prefabUnitSpawn.actor == null)
        {
            return null;
        }

        return seaUnitSpawn.unitSpawner.prefabUnitSpawn.actor.actorName;
    }

    private static bool IsExactStockCarrierSpawn(UnitSpawn unitSpawn)
    {
        // AICarrierSpawn is the observed stock carrier path. Do not classify
        // subclasses/custom variants merely because they inherit it: unknown
        // content remains generic unless it supplies the canonical exact name.
        return unitSpawn != null && VtolSeaSpawnTypePolicy.IsExactStockType(
            unitSpawn.GetType(),
            typeof(AICarrierSpawn));
    }

    private static string GetDisplayName(Actor actor, UnitSpawn unitSpawn)
    {
        string designation = actor.designation.num1 > 0
            ? actor.designation.ShortString()
            : null;
        string spawnedUnitDisplayName = unitSpawn?.unitSpawner != null
            ? unitSpawn.unitSpawner.GetUIDisplayName()
            : null;
        string actorName = FirstNonEmpty(
            actor.actorName,
            spawnedUnitDisplayName,
            unitSpawn?.unitName);

        if (!string.IsNullOrWhiteSpace(designation) && !string.IsNullOrWhiteSpace(actorName))
        {
            return $"{designation} {actorName}";
        }

        return FirstNonEmpty(designation, actorName);
    }

    private static string GetPilotName(bool isOwnship)
    {
        if (!isOwnship || PilotSaveManager.current == null)
        {
            return null;
        }

        return FirstNonEmpty(PilotSaveManager.current.pilotName);
    }
}
