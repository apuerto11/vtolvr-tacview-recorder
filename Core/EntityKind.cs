using System;

namespace VTOLVRTacviewSPRecorder.Core;

public enum EntityKind
{
    Aircraft,
    Missile,
    Rocket,
    Bomb,
    Projectile,
    Flare,
    Chaff,
    Ground,
    Sea,
    Static
}

public enum SamplingClass
{
    Ownship,
    Aircraft,
    FastWeapon,
    BallisticWeapon,
    Decoy,
    Surface,
    Static
}

internal static class EntitySamplingClassResolver
{
    public static SamplingClass Resolve(EntityKind kind, bool isOwnship)
    {
        if (isOwnship)
        {
            return SamplingClass.Ownship;
        }

        switch (kind)
        {
            case EntityKind.Aircraft:
                return SamplingClass.Aircraft;
            case EntityKind.Missile:
            case EntityKind.Rocket:
                return SamplingClass.FastWeapon;
            case EntityKind.Bomb:
            case EntityKind.Projectile:
                return SamplingClass.BallisticWeapon;
            case EntityKind.Flare:
            case EntityKind.Chaff:
                return SamplingClass.Decoy;
            case EntityKind.Ground:
            case EntityKind.Sea:
                return SamplingClass.Surface;
            case EntityKind.Static:
                return SamplingClass.Static;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported entity kind.");
        }
    }
}
