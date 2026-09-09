global using static VTOLVRTacviewSPRecorder.Main.Logger;
using System.IO;
using System.Reflection;
using ModLoader.Framework;
using ModLoader.Framework.Attributes;

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

        _recorderLifecycle = new RecorderLifecycle(this);
        _recorderLifecycle.StartListening();
    }

    public override void UnLoad()
    {
        _recorderLifecycle?.Dispose();
        _recorderLifecycle = null;
    }
}
