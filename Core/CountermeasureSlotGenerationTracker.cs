using System;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// Detects new generations in a fixed-size pool or ring without treating the
/// backing slot itself as the entity identity.
/// </summary>
public sealed class CountermeasureSlotGenerationTracker
{
    private const double DefaultGenerationTimeToleranceSeconds = 0.01;

    private readonly bool[] _activeSlots;
    private readonly double[] _generationStartTimes;
    private readonly double _generationTimeToleranceSeconds;

    public CountermeasureSlotGenerationTracker(
        int slotCount,
        double generationTimeToleranceSeconds =
            DefaultGenerationTimeToleranceSeconds)
    {
        if (slotCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotCount));
        }

        if (!IsFinite(generationTimeToleranceSeconds) ||
            generationTimeToleranceSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generationTimeToleranceSeconds));
        }

        _activeSlots = new bool[slotCount];
        _generationStartTimes = new double[slotCount];
        _generationTimeToleranceSeconds = generationTimeToleranceSeconds;
    }

    public int SlotCount => _activeSlots.Length;

    /// <summary>
    /// Updates one slot and returns true only when it now contains a new active
    /// generation. Reusing a previously inactive slot is always a new
    /// generation, even if its timestamp happens to match an older one.
    /// </summary>
    public bool Observe(
        int slotIndex,
        bool isActive,
        double generationStartTimeSeconds)
    {
        if (slotIndex < 0 || slotIndex >= _activeSlots.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        }

        if (!isActive || !IsFinite(generationStartTimeSeconds))
        {
            _activeSlots[slotIndex] = false;
            _generationStartTimes[slotIndex] = 0.0;
            return false;
        }

        bool isNewGeneration = !_activeSlots[slotIndex] ||
            Math.Abs(
                _generationStartTimes[slotIndex] -
                generationStartTimeSeconds) >
            _generationTimeToleranceSeconds;

        _activeSlots[slotIndex] = true;
        _generationStartTimes[slotIndex] = generationStartTimeSeconds;
        return isNewGeneration;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
