using System;
using System.Globalization;
using System.Text;

namespace VTOLVRTacviewSPRecorder.Core;

public static class TacviewFileName
{
    private const int MaximumMissionNameLength = 80;
    private const string InvalidWindowsFileNameCharacters = "<>:\"/\\|?*";

    public static string Build(DateTimeOffset utcTime, string missionName)
    {
        return utcTime.ToUniversalTime().ToString("yyyyMMdd_HHmmss_fff'Z'_", CultureInfo.InvariantCulture)
            + SanitizeMissionName(missionName)
            + ".acmi";
    }

    public static string SanitizeMissionName(string missionName)
    {
        if (string.IsNullOrWhiteSpace(missionName))
        {
            return "Mission";
        }

        var result = new StringBuilder(Math.Min(missionName.Length, MaximumMissionNameLength));
        bool previousWasReplacement = false;
        bool hasValidCharacter = false;

        foreach (char character in missionName.Trim())
        {
            if (result.Length >= MaximumMissionNameLength)
            {
                break;
            }

            bool invalid = char.IsControl(character) ||
                InvalidWindowsFileNameCharacters.IndexOf(character) >= 0;

            if (invalid)
            {
                if (!previousWasReplacement)
                {
                    result.Append('_');
                    previousWasReplacement = true;
                }

                continue;
            }

            result.Append(character);
            previousWasReplacement = false;
            hasValidCharacter = true;
        }

        string sanitized = result.ToString().Trim(' ', '.');
        return !hasValidCharacter || string.IsNullOrWhiteSpace(sanitized) ? "Mission" : sanitized;
    }
}
