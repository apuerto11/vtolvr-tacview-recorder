# VTOL VR aircraft labels for Tacview

This bundled database companion supplies the original names for all six
flyable VTOL VR aircraft and retains the recorder's model proxies. It fixes
EF-24G recordings labelled as F-4 despite an explicit F-14 Shape.

## Automatic installation

Starting with 0.5.1-beta.1, the definitions are embedded in the recorder
DLL. When the mod loads, it installs or updates only this file:

```text
%APPDATA%\Tacview\Data\Database\Default Properties\Tacview.VTOLVR.PlayerAircraft.xml
```

Both the manual/GitHub package and the DLL-only Workshop package include the
resource. No separate XML installation is needed on a PC running the mod.
Restart Tacview after the mod logs that the labels were installed or updated.
An identical file is not rewritten. A write failure logs a warning and does not
prevent recording; the installer tries again the next time the mod loads.

## Manual fallback for older recorder versions

1. Close Tacview.
2. In File Explorer, open `%APPDATA%\Tacview\Data\Database\Default Properties\`.
   Create these folders if necessary.
3. Copy `Tacview.VTOLVR.PlayerAircraft.xml` into that folder.
4. Restart Tacview and reopen the recording.

This works with existing recordings without administrator rights, new models,
or edits to the ACMI file. The definitions are not embedded in recordings.
The XML is managed by the mod: local edits to that file are replaced on the next
mod load. Keep personal definitions in a separate XML file. After disabling or
uninstalling the mod, remove this one XML file and restart Tacview to uninstall
the labels. Disabling the mod alone leaves labels available for old recordings.

## Scope

All six flyable aircraft have distinct `VTOLVR.*` database IDs, generic bases,
and type filters. The fixed-wing T-55 entry does not target ground vehicles.
Shapes match the recorder catalog. No real-aircraft performance data is inherited.
AI-only aircraft are outside this companion's scope. Tacview performs best-match
name resolution; arbitrary modded names are not guaranteed to match exactly.

Tacview recommends defining ShortName, LongName and FullName in its database,
rather than in telemetry. The XML contains original metadata and references to
models supplied by Tacview; it does not contain any model assets.

- [Database documentation](https://raia-software-inc.gitbook.io/tacview/customization/database)
- [ACMI properties](https://raia-software-inc.gitbook.io/tacview/technical-documentation/acmi-telemetry-file-format#object-properties)

## Validation

On 2026-09-13, Tacview Starter 1.9.5.201 displayed the unchanged reporter
recordings (Free Flight 2 and Target Practice) as EF-24G Mischief after installing
the companion and restarting. Target Practice also showed EF-24G in the 3D label.
The original recording hashes were preserved. All six definitions were checked
against the recorder catalog for matching shapes, distinct IDs, and type filters;
the other five aircraft have not yet been visually checked in Tacview.
