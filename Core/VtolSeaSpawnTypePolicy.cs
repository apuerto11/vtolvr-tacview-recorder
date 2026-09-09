using System;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// Narrow type policy for the stock AICarrierSpawn visual fallback. Assignable
/// subclasses are deliberately rejected: a mod/custom carrier can inherit the
/// stock type while representing a different hull, so it must resolve through
/// an exact canonical catalog name or remain generic.
/// </summary>
public static class VtolSeaSpawnTypePolicy
{
    public static bool IsExactStockType(Type runtimeSpawnType, Type stockCarrierSpawnType)
    {
        return runtimeSpawnType != null &&
            stockCarrierSpawnType != null &&
            runtimeSpawnType == stockCarrierSpawnType;
    }
}
