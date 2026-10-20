using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// Real-Time Telemetry options, read from a small <c>key=value</c> file next
/// to the mod DLL (<c>realtime.cfg</c>). Unknown keys and invalid values are
/// reported and ignored, never fatal.
/// </summary>
public sealed class TacviewRealtimeSettings
{
    public const string FileName = "realtime.cfg";

    /// <summary>Starts the real-time host. Recording to file is unaffected either way.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Interface to listen on. Default 127.0.0.1: only programs on this PC can
    /// connect. Use 0.0.0.0 to accept other machines on the network.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = TacviewRealtimeServer.DefaultPort;

    /// <summary>Empty: no password.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Name announced to clients during the handshake.</summary>
    public string HostName { get; set; } = "VTOL VR";

    public int MaxClients { get; set; } = 4;

    /// <summary>Frames buffered per client before a slow client is dropped (~40 frames per second).</summary>
    public int MaxQueuedFrames { get; set; } = 2000;

    /// <summary>
    /// Aircraft flight data (IAS, TAS, Mach, AOA, AOS, AGL, Throttle,
    /// FuelVolume). Applies to the .acmi file and the live stream alike.
    /// </summary>
    public bool ExportFlightData { get; set; } = true;

    /// <summary>Radar locks (LockedTarget/LockedTargetMode) and in-flight missile retargeting.</summary>
    public bool ExportEngagement { get; set; } = true;

    /// <summary>RadarMode, RadarRange, EngagementRange, VerticalEngagementRange of ground units and ships.</summary>
    public bool ExportSensors { get; set; } = true;

    /// <summary>Sampling rate of the properties above (0.1 to 10 Hz). Values are written only when changed.</summary>
    public double FlightDataRateHz { get; set; } = 2.0;

    public static string DefaultFileContent =>
        "# Tacview Real-Time Telemetry (VTOL VR Tacview recorder)\n" +
        "# Tacview, or a bridge such as tacv-acmi-bridge, can follow the mission live.\n" +
        "# Recording to .acmi files works the same whether this is enabled or not.\n" +
        "enabled=true\n" +
        "# 127.0.0.1 = this PC only; 0.0.0.0 = any machine on the network (open the port in the firewall).\n" +
        "bind=127.0.0.1\n" +
        "port=42674\n" +
        "# Leave empty for no password.\n" +
        "password=\n" +
        "max_clients=4\n" +
        "# Extra per-object properties, in the .acmi file and the live stream (true/false).\n" +
        "export_flight_data=true\n" +
        "export_engagement=true\n" +
        "export_sensors=true\n" +
        "# Sampling rate of these properties; a value is written only when it changed.\n" +
        "flight_data_rate_hz=2\n";

    /// <summary>
    /// Reads <paramref name="path"/>; creates it with defaults when missing.
    /// <paramref name="warnings"/> lists ignored lines.
    /// </summary>
    public static TacviewRealtimeSettings LoadOrCreate(string path, List<string> warnings)
    {
        if (!File.Exists(path))
        {
            try
            {
                File.WriteAllText(path, DefaultFileContent, new UTF8Encoding(false));
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                warnings?.Add($"could not create {path}: {exception.Message}");
            }

            return new TacviewRealtimeSettings();
        }

        return Parse(File.ReadAllText(path), warnings);
    }

    public static TacviewRealtimeSettings Parse(string text, List<string> warnings)
    {
        var settings = new TacviewRealtimeSettings();
        string[] lines = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                warnings?.Add($"line {index + 1} ignored: expected key=value");
                continue;
            }

            string key = line.Substring(0, equals).Trim().ToLowerInvariant();
            string value = line.Substring(equals + 1).Trim();
            switch (key)
            {
                case "enabled":
                    if (TryParseBool(value, out bool enabled))
                    {
                        settings.Enabled = enabled;
                    }
                    else
                    {
                        warnings?.Add($"line {index + 1} ignored: enabled must be true or false");
                    }

                    break;
                case "bind":
                    settings.BindAddress = value;
                    break;
                case "port":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) && port >= 0 && port <= 65535)
                    {
                        settings.Port = port;
                    }
                    else
                    {
                        warnings?.Add($"line {index + 1} ignored: port must be between 0 and 65535");
                    }

                    break;
                case "password":
                    settings.Password = value;
                    break;
                case "host_name":
                    settings.HostName = value;
                    break;
                case "max_clients":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxClients) && maxClients >= 1)
                    {
                        settings.MaxClients = maxClients;
                    }
                    else
                    {
                        warnings?.Add($"line {index + 1} ignored: max_clients must be at least 1");
                    }

                    break;
                case "export_flight_data":
                case "exportflightdata":
                    settings.ExportFlightData = ParseBoolSetting(value, settings.ExportFlightData, index, key, warnings);
                    break;
                case "export_engagement":
                case "exportengagement":
                    settings.ExportEngagement = ParseBoolSetting(value, settings.ExportEngagement, index, key, warnings);
                    break;
                case "export_sensors":
                case "exportsensors":
                    settings.ExportSensors = ParseBoolSetting(value, settings.ExportSensors, index, key, warnings);
                    break;
                case "flight_data_rate_hz":
                case "flightdataratehz":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate) &&
                        rate >= 0.1 && rate <= 10.0)
                    {
                        settings.FlightDataRateHz = rate;
                    }
                    else
                    {
                        warnings?.Add($"line {index + 1} ignored: {key} must be between 0.1 and 10");
                    }

                    break;
                default:
                    warnings?.Add($"line {index + 1} ignored: unknown key '{key}'");
                    break;
            }
        }

        return settings;
    }

    private static bool ParseBoolSetting(string value, bool current, int index, string key, List<string> warnings)
    {
        if (TryParseBool(value, out bool parsed))
        {
            return parsed;
        }

        warnings?.Add($"line {index + 1} ignored: {key} must be true or false");
        return current;
    }

    private static bool TryParseBool(string value, out bool result)
    {
        switch (value.ToLowerInvariant())
        {
            case "true":
            case "yes":
            case "on":
            case "1":
                result = true;
                return true;
            case "false":
            case "no":
            case "off":
            case "0":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
