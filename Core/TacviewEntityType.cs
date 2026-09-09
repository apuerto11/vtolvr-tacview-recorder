using System;

namespace VTOLVRTacviewSPRecorder.Core;

public static class TacviewEntityType
{
    public static string GetDefault(EntityKind kind)
    {
        switch (kind)
        {
            case EntityKind.Aircraft:
                return "Air";
            case EntityKind.Missile:
                return "Weapon+Missile";
            case EntityKind.Rocket:
                return "Weapon+Rocket";
            case EntityKind.Bomb:
                return "Weapon+Bomb";
            case EntityKind.Projectile:
                return "Weapon+Projectile";
            case EntityKind.Ground:
                return "Ground+Vehicle";
            case EntityKind.Sea:
                return "Sea+Watercraft";
            case EntityKind.Static:
                return "Ground+Static";
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported entity kind.");
        }
    }
}
