using System;
using UnityEngine;
using VTOLVRTacviewSPRecorder.Core;

namespace VTOLVRTacviewSPRecorder.Main;

internal sealed class VtolEntitySource : IEntityTransformSource
{
    private readonly Transform _transform;
    private readonly VTMapManager _mapManager;
    private readonly Func<bool> _isAvailable;

    public VtolEntitySource(
        Transform transform,
        VTMapManager mapManager,
        Func<bool> isAvailable = null)
    {
        _transform = transform;
        _mapManager = mapManager ?? throw new ArgumentNullException(nameof(mapManager));
        _isAvailable = isAvailable;
    }

    public bool IsAvailable =>
        _transform != null && (_isAvailable == null || _isAvailable());

    public bool TryGetTransform(out EntityTransform transform)
    {
        transform = default;

        if (!IsAvailable)
        {
            return false;
        }

        return TryGetCurrentTransform(out transform);
    }

    public bool TryGetCurrentTransform(out EntityTransform transform)
    {
        transform = default;

        if (_transform == null)
        {
            return false;
        }

        Vector3 position = _transform.position;
        Vector3 eulerAngles = _transform.eulerAngles;
        Vector3D globalPosition = VTMapManager.WorldToGlobalPoint(position);
        Vector3D gpsPosition = _mapManager.WorldPositionToGPSCoords(position);
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
        return true;
    }
}
