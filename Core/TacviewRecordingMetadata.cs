namespace VTOLVRTacviewSPRecorder.Core;

public sealed class TacviewRecordingMetadata
{
    public TacviewRecordingMetadata(
        string theater = null,
        string category = null,
        string briefing = null,
        string mapId = null)
    {
        Theater = Clean(theater);
        Category = Clean(category);
        Briefing = Clean(briefing);
        MapId = Clean(mapId);
    }

    public string Theater { get; }

    public string Category { get; }

    public string Briefing { get; }

    public string MapId { get; }

    private static string Clean(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
