# Steam Workshop listing draft

> Not uploaded. This is a copy-ready community-beta draft for the official
> VTOL VR Mod Uploader after the remaining gates in `RELEASE_CHECKLIST.md` pass.

## Uploader fields

| Field | Draft value / decision |
| --- | --- |
| Title | `Tacview Singleplayer Recorder` |
| Upload folder | `artifacts\workshop-staging\VTOLVRTacviewSPRecorder` |
| Contents | Exactly `VTOLVRTacviewSPRecorder.dll`; no PDB, private `item.json`, game assembly, Tacview model, generated terrain, or recording. |
| DLL name | `VTOLVRTacviewSPRecorder.dll` |
| Allow Load On Start | `true` |
| Show On Main Menu | `true` |
| Tag | `Client Side` only. Do **not** select `Multiplayer Compatible` or `Host Enforced`. |
| Dependency | VTOLAPI Workshop item `3265689427`; add it in the Workshop page's **Add/Remove Required Items** UI after the first upload. |
| Thumbnail | **Owner-approved Candidate D.** Use `assets/workshop/thumbnail-512.png` (512 x 512 PNG, SHA-256 `9A09197C535BFD4E916F608181D2DB7178B395E2F4868A738940B946EB4152E2`). It is original and contains no Mod Loader, VTOL VR, Tacview, or Boundless Dynamics logo. |
| First visibility | `Hidden` (the uploader maps this to Steam private). |
| Beta visibility | `Unlisted` only for invited testers after the private Workshop download/load test passes. |
| Public visibility | Owner-approved; after the Hidden download/load check. |

## Workshop description

```text
[h1]Tacview Singleplayer Recorder — 0.5.0-beta.1 Community Beta[/h1]

Records supported VTOL VR single-player missions as Tacview ACMI 2.2 files for post-flight review.

[h2]Features[/h2]
[list]
[*]Aircraft, weapons, ground/sea units, mission context, flares, and chaff
[*]Local Tacview terrain companion with matching MapId switching
[*]Experimental Akutan Campaign terrain path; broader live validation is still requested
[*]Reviewed aircraft and stock Carrier/Escort Cruiser visual proxy shapes
[*]Live `Projectile+Bullet` samples from real VTOL VR Bullet instances for observed cannon fire
[/list]

[h2]Requirements[/h2]
[list]
[*]VTOL VR Mod Loader
[*]VTOLAPI (required item; Steam will prompt you to install it)
[*]Tacview 1.9.5 or newer
[/list]

[h2]Important[/h2]
[list]
[*]Single-player only. Multiplayer sessions are intentionally not recorded.
[*]Terrain is a locally generated heightfield, not VTOL VR textures or game assets.
[*]Aircraft and ship shapes are visual proxies.
[*]Gun bullets follow the real pooled Bullet transform until its lifecycle teardown; they are not an exact round count, hit log, or ballistic replay.
[*]After a newly generated terrain cache, restart Tacview before opening the finalized ACMI and keep Auto Terrain Switch enabled.
[/list]

[h2]Beta feedback[/h2]
Please use the [url=https://github.com/kerimala/vtolvr-tacview-recorder/issues/new?template=bug_report.yml]bug report form[/url] or [url=https://github.com/kerimala/vtolvr-tacview-recorder/issues/new?template=feature_request.yml]feature request form[/url]. Include the recorder, VTOL VR, Mod Loader, and Tacview versions; mission/map; reproduction or use case; and sanitized Player.log, finalized ACMI, and Tacview screenshot evidence where applicable. Read the [url=https://github.com/kerimala/vtolvr-tacview-recorder/blob/main/SUPPORT.md]support and privacy guide[/url] first. Do not attach generated terrain RAW/XML files, extracted game assets, keys, credentials, or recordings containing other players' data.
```

## Upload runbook

1. Owner publication/risk approval was recorded on 2026-09-09. Verify the clean public source and release artifacts.
2. Run `dotnet msbuild VTOLVRTacviewSPRecorder.csproj -t:CreateWorkshopStaging -p:Configuration=Release -p:DeployMod=false`.
3. Verify the folder contains only the reviewed Release DLL and compare its
   SHA-256 to the build record.
4. Open **VTOL VR Mod Uploader** from Steam. Select the staging folder and
   `assets/workshop/thumbnail-512.png`, then enter the fields above.
5. Upload as **Hidden**, accept the Steam Workshop Legal Agreement if Steam
   requests it, and record the resulting Workshop ID.
6. Subscribe/download that hidden item on a clean/separate loader profile,
   then complete the live test matrix. Add VTOLAPI in the Workshop web UI as a
   required item and re-test a fresh subscriber flow.
7. Use **Unlisted** for a controlled beta. Do not treat it as private: people
   with the link can access it.
8. Switch to **Public** only with explicit approval and after all release
   checklist gates are evidenced.

See [LEGAL_AND_PRIVACY.md](../LEGAL_AND_PRIVACY.md) for the independent-project,
no-endorsement, asset, EULA, and privacy boundaries.
