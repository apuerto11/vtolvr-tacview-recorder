using System;
using System.Collections.Generic;

namespace VTOLVRTacviewSPRecorder.Core;

public enum WarningKind
{
    SourceUnavailable,
    SourceFault,
    NonFiniteTransform,
    Metadata,
    Refresh
}

public sealed class WarningThrottle
{
    private readonly double _minimumIntervalSeconds;
    private readonly Dictionary<WarningKey, double> _lastWarningTimes =
        new Dictionary<WarningKey, double>();
    private readonly List<WarningKey> _keysToRemove = new List<WarningKey>();

    public WarningThrottle(double minimumIntervalSeconds = 10.0)
    {
        if (double.IsNaN(minimumIntervalSeconds) ||
            double.IsInfinity(minimumIntervalSeconds) ||
            minimumIntervalSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumIntervalSeconds),
                "Warning throttle interval must be finite and non-negative.");
        }

        _minimumIntervalSeconds = minimumIntervalSeconds;
    }

    public bool ShouldLog(ulong objectId, WarningKind kind, double nowSeconds)
    {
        if (double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(nowSeconds), "Warning time must be finite.");
        }

        var key = new WarningKey(objectId, kind);
        if (_lastWarningTimes.TryGetValue(key, out double lastWarningTime) &&
            nowSeconds - lastWarningTime < _minimumIntervalSeconds)
        {
            return false;
        }

        _lastWarningTimes[key] = nowSeconds;
        return true;
    }

    public void Forget(ulong objectId)
    {
        if (_lastWarningTimes.Count == 0)
        {
            return;
        }

        _keysToRemove.Clear();
        foreach (WarningKey key in _lastWarningTimes.Keys)
        {
            if (key.ObjectId == objectId)
            {
                _keysToRemove.Add(key);
            }
        }

        for (int index = 0; index < _keysToRemove.Count; index++)
        {
            _lastWarningTimes.Remove(_keysToRemove[index]);
        }

        _keysToRemove.Clear();
    }

    public void Clear()
    {
        _lastWarningTimes.Clear();
        _keysToRemove.Clear();
    }

    private readonly struct WarningKey : IEquatable<WarningKey>
    {
        public WarningKey(ulong objectId, WarningKind kind)
        {
            ObjectId = objectId;
            Kind = kind;
        }

        public ulong ObjectId { get; }

        public WarningKind Kind { get; }

        public bool Equals(WarningKey other)
        {
            return ObjectId == other.ObjectId && Kind == other.Kind;
        }

        public override bool Equals(object obj)
        {
            return obj is WarningKey && Equals((WarningKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (ObjectId.GetHashCode() * 397) ^ (int)Kind;
            }
        }
    }
}
