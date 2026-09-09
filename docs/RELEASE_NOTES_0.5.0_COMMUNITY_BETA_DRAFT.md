# Tacview Singleplayer Recorder — 0.5.0-beta.1

First community beta for recording VTOL VR **single-player** flights as
Tacview ACMI 2.2 for post-flight review.

- Player/AI aircraft, missiles, bombs, rockets, ground and sea units
- Flares, chaff and sampled real gun-bullet trajectories
- Locally generated terrain heightfields and automatic map switching
- Visual proxy models for supported aircraft and surface units

## Install

Install VTOL VR Mod Loader and enable VTOLAPI. With the game and loader closed,
extract the ZIP's `VTOLVRTacviewSPRecorder` folder into the game's
`@Mod Loader/Mods/` directory. Do not install duplicate manual/Workshop copies.
Start a single-player mission, fly, then leave normally. Open the resulting
`.acmi` in `Documents/Tacview/VTOL VR/` using Tacview 1.9.5 or newer.
Restart Tacview after new terrain is generated and enable Auto Terrain Switch.

## Beta limitations

Single-player only. Terrain has elevation but no original textures or scenery.
Aircraft/ship models are approximations. Gun samples are limited and are not
a fired-round count or a hit report. Akutan alignment/startup performance,
the second cannon family and early-exit/multiplayer edge cases need more live
testing. The latest checked flight was a 20-minute F/A-26B Target Practice
session on VTOL VR v1.12.6m1; see TESTING.md for its evidence and open coverage.

Report bugs through [GitHub Issues](https://github.com/kerimala/vtolvr-tacview-recorder/issues).
Include versions, mission, aircraft and a description; redact private data
before sharing logs or recordings. Never upload generated terrain caches.
This unofficial project has no automatic telemetry or uploads.
