global using static VTOLVRTacviewSPRecorder.Main.Logger;
using System.IO;
using System;
using System.Reflection;
using ModLoader.Framework;
using ModLoader.Framework.Attributes;
using VTOLVRTacviewSPRecorder.Core;

namespace VTOLVRTacviewSPRecorder.Main;

[ItemId("de.kalag.vtolvr.tacviewsprecorder")] // Harmony ID for your mod, make sure this is unique
public class Main : VtolMod
{
    public string ModFolder;

    private RecorderLifecycle _recorderLifecycle;

    private void Awake()
    {
        ModFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        Log($"Awake at {ModFolder}");

        try
        {
            TacviewAircraftDatabaseStatus status = TacviewAircraftDatabase.EnsureInstalled(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            Log(status == TacviewAircraftDatabaseStatus.Current
                ? "Tacview aircraft labels are current."
                : $"Tacview aircraft labels {status.ToString().ToLowerInvariant()}. Restart Tacview to load them.");
        }
        catch (Exception exception)
        {
            // A viewer configuration failure must not prevent flight recording.
            LogWarn($"Could not install Tacview aircraft labels; recording will continue: {exception.Message}");
        }

        _recorderLifecycle = new RecorderLifecycle(this);
        _recorderLifecycle.StartListening();
    }

    public override void UnLoad()
    {
        _recorderLifecycle?.Dispose();
        _recorderLifecycle = null;
    }
}
