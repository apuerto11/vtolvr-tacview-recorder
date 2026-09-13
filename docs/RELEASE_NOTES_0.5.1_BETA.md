# Tacview Singleplayer Recorder — 0.5.1-beta.1

Fixes Tacview showing an EF-24G as “F-4” in aircraft labels even when the F-14
proxy model is correct. The mod now installs its bundled Tacview label database
automatically for EF-24G, F/A-26B, F-45A, T-55, AV-42C and AH-94.

- Definitions are embedded in the DLL for both Workshop and manual installs.
- Only the recorder-owned XML is updated; other custom definitions are untouched.
- Existing recordings work without edits. The model proxy mappings are unchanged.
- If label installation fails, the mod logs a warning and recording continues.

## Update and use

Update the mod, load it once through VTOL VR Mod Loader, then **restart Tacview**.
No separate XML download is needed. Tacview 1.9.5 or newer and VTOLAPI are required.

[Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3798566504):
update the existing subscription. For manual installs, close the game and loader,
then replace the `VTOLVRTacviewSPRecorder` folder in `@Mod Loader/Mods/` with the
ZIP's folder. Enable only one recorder copy and keep VTOLAPI enabled.

## Validation and remaining coverage

201 tests pass in both Debug and Release; both mod builds have no warnings or
errors. The built DLL's installer was checked under Windows .NET CLR 4.
The label definitions were checked in Tacview 1.9.5.201 using two unchanged
EF-24G recordings. A fresh EF-24G flight and game mod-load integration remain
community testing targets; the maintainer does not own that DLC. Other aircraft
have automated definition/catalog checks, not a claimed visual playtest.

Single-player only; existing beta limitations remain in [TESTING.md](https://github.com/kerimala/vtolvr-tacview-recorder/blob/main/TESTING.md).
Please report versions and results through [GitHub Issues](https://github.com/kerimala/vtolvr-tacview-recorder/issues).
Sanitize recordings and logs before sharing; do not upload generated terrain caches.
