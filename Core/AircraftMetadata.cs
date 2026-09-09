using System;

namespace VTOLVRTacviewSPRecorder.Core;

public sealed class AircraftMetadata
{
    public AircraftMetadata(
        string type,
        string name,
        string coalition,
        string color,
        string callSign = null,
        string pilot = null)
    {
        Type = RequireValue(type, nameof(type));
        Name = RequireValue(name, nameof(name));
        Coalition = RequireValue(coalition, nameof(coalition));
        Color = RequireValue(color, nameof(color));
        CallSign = string.IsNullOrWhiteSpace(callSign) ? null : callSign.Trim();
        Pilot = string.IsNullOrWhiteSpace(pilot) ? null : pilot.Trim();
    }

    public string Type { get; }

    public string Name { get; }

    public string Coalition { get; }

    public string Color { get; }

    public string CallSign { get; }

    public string Pilot { get; }

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty metadata value is required.", parameterName);
        }

        return value.Trim();
    }
}
