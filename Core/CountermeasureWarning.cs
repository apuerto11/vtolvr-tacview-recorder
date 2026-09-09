using System;

namespace VTOLVRTacviewSPRecorder.Core;

public enum CountermeasureWarningKind
{
    SpawnRejected,
    ActiveCapReached,
    SalvoCapReached,
    SourceUnavailable,
    SourceFault,
    NonFiniteTransform,
    LifecycleSuppressed
}

public readonly struct CountermeasureWarning
{
    public CountermeasureWarning(
        CountermeasureWarningKind kind,
        ulong objectId,
        string message)
    {
        Kind = kind;
        ObjectId = objectId;
        Message = message ?? string.Empty;
    }

    public CountermeasureWarningKind Kind { get; }

    /// <summary>
    /// Zero denotes a release-wide warning before an object id was allocated.
    /// </summary>
    public ulong ObjectId { get; }

    public string Message { get; }
}

/// <summary>
/// Testable boundary for host logging. Implementations must not throw into the
/// recorder; the core also protects itself if one does.
/// </summary>
public interface ICountermeasureWarningSink
{
    void Warn(CountermeasureWarning warning);
}

internal sealed class CountermeasureWarningReporter
{
    private readonly WarningThrottle _throttle;
    private readonly ICountermeasureWarningSink _sink;

    public CountermeasureWarningReporter(
        WarningThrottle throttle,
        ICountermeasureWarningSink sink)
    {
        _throttle = throttle;
        _sink = sink;
    }

    public void Warn(
        CountermeasureWarningKind kind,
        ulong objectId,
        double nowSeconds,
        string message)
    {
        if (_sink == null)
        {
            return;
        }

        if (_throttle != null && !_throttle.ShouldLog(
                objectId,
                ToWarningKind(kind),
                nowSeconds))
        {
            return;
        }

        try
        {
            _sink.Warn(new CountermeasureWarning(kind, objectId, message));
        }
        catch
        {
            // Diagnostics are never allowed to interrupt countermeasure capture.
        }
    }

    public void Forget(ulong objectId)
    {
        _throttle?.Forget(objectId);
    }

    private static WarningKind ToWarningKind(CountermeasureWarningKind kind)
    {
        switch (kind)
        {
            case CountermeasureWarningKind.SourceUnavailable:
                return WarningKind.SourceUnavailable;
            case CountermeasureWarningKind.SourceFault:
                return WarningKind.SourceFault;
            case CountermeasureWarningKind.NonFiniteTransform:
                return WarningKind.NonFiniteTransform;
            case CountermeasureWarningKind.SpawnRejected:
                return WarningKind.Metadata;
            case CountermeasureWarningKind.ActiveCapReached:
            case CountermeasureWarningKind.SalvoCapReached:
            case CountermeasureWarningKind.LifecycleSuppressed:
                return WarningKind.Refresh;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported countermeasure warning kind.");
        }
    }
}
