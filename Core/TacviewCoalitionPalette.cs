using System;

namespace VTOLVRTacviewSPRecorder.Core;

public enum TacviewCoalitionSide
{
    Allied,
    Enemy,
    Neutral
}

public static class TacviewCoalitionPalette
{
    public static string GetCoalition(TacviewCoalitionSide side)
    {
        switch (side)
        {
            case TacviewCoalitionSide.Allied:
                return "Allies";
            case TacviewCoalitionSide.Enemy:
                return "Enemies";
            case TacviewCoalitionSide.Neutral:
                return "Neutral";
            default:
                throw new ArgumentOutOfRangeException(nameof(side), side, "Unsupported coalition side.");
        }
    }

    public static string GetColor(TacviewCoalitionSide side)
    {
        switch (side)
        {
            case TacviewCoalitionSide.Allied:
                return "Blue";
            case TacviewCoalitionSide.Enemy:
                return "Red";
            case TacviewCoalitionSide.Neutral:
                return "White";
            default:
                throw new ArgumentOutOfRangeException(nameof(side), side, "Unsupported coalition side.");
        }
    }
}
