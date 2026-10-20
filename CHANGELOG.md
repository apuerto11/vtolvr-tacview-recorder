# Changelog

This file records intended community-visible changes. It does not mean that a
version has been published.

## Unreleased — Flight data (fork, untested in game)

- New per-object ACMI properties: `IAS`, `TAS`, `Mach`, `AOA`, `AOS`, `AGL`,
  `Throttle`, `FuelVolume` for aircraft; `LockedTarget`/`LockedTargetMode`
  for radar locks of aircraft, ground units and ships; `RadarMode`,
  `RadarRange`, `EngagementRange`, `VerticalEngagementRange` for ground units
  and ships. Missile `LockedTarget` is now refreshed during flight.
- Sampled at 2 Hz by default and written only when changed beyond a
  per-property threshold.
- `realtime.cfg`: `export_flight_data`, `export_engagement`, `export_sensors`,
  `flight_data_rate_hz`. They apply to file recording too.
- Mach and IAS are standard-atmosphere estimates. Several VTOL VR members are
  read by name through reflection and are unverified (see README); a missing
  member only disables that property.
- Validation: 235 automated tests (15 new: encoding, thresholds, lock
  release, merging, rate gate, atmosphere, settings); Core builds for
  net472; the new adapter type-checks against API stubs. Not yet compiled
  against the game assemblies nor run in game.

## Unreleased — Real-Time Telemetry (fork)

- Tacview Real-Time Telemetry host: Tacview or a bridge can follow the mission
  live while the `.acmi` file is recorded as before. Handshake and password
  hash (CRC-64, legacy CRC-32 accepted) follow the Tacview protocol.
- Late clients receive a complete snapshot of the current mission state, then
  incremental frames. Slow clients are disconnected instead of slowing the
  game; a network failure never interrupts the file recording.
- `realtime.cfg` next to the DLL: `enabled`, `bind` (127.0.0.1 by default),
  `port` (42674), `password`, `max_clients`.
- Validation: 220 automated tests (19 new: hashes, settings, snapshot mirror,
  file/stream parity, TCP handshake and refusal); Core builds for net472.
  Interoperability checked with tacv-acmi-bridge. Validated in game by the
  fork owner: the mod builds against the game and Mod Loader assemblies, the
  host listens on load, and tacv-acmi-bridge follows the mission live.

## Community beta — 0.5.1-beta.1 (2026-09-13)

- Embedded aircraft-label definitions in the recorder DLL, with automatic
  installation/update on mod load for the six flyable aircraft. This corrects
  EF-24G being labelled F-4 while retaining the existing model proxies.
- Only the recorder-owned XML in the current user's Tacview database directory
  is updated. Current files are left untouched; failed installs log a warning
  and recording continues. Restart Tacview after an installation or update.
- Existing recordings work without modification. Both GitHub/manual and
  Workshop distributions carry the definitions inside the same DLL.

- Validation: 201 automated tests; label definitions checked in Tacview 1.9.5.201
  using two existing EF-24G recordings. Fresh EF-24G gameplay and mod-load
  integration remain community testing targets.

## Community beta — 0.5.0-beta.1 (2026-09-09)

### Added

- Optional local Tacview 1.9.5 terrain companions with versioned `MapId`
  switching.
- Fail-closed Akutan MeshCollider terrain preparation with a resolved 508 ×
  508 heightfield lattice and boundary-connected sea-level handling.
- Reviewed exact Campaign aircraft aliases and stock Carrier/Escort Cruiser
  visual proxy mappings.
- Live `Projectile+Bullet` samples from real VTOL VR Bullet instances for M61
  and `VTOLCannon`/GAU-8 fire, with eventless lifecycle deletes.
- Owner-approved original Workshop Candidate D at
  `assets/workshop/thumbnail-512.png` plus repository legal/privacy notices.

### Changed

- Terrain XML writes Tacview's case-sensitive `Id` attribute correctly.
- A terrain map is activated in ACMI only after the matching local cache is
  verified; an interrupted/failed terrain export leaves the reservation as an
  ACMI comment instead.

### Known limitations

- Single-player only; multiplayer sessions are intentionally not recorded.
- Akutan Campaign terrain alignment, Auto Terrain Switch, and startup
  performance remain open beta validation targets.
- Aircraft and ship shapes are visual proxies. Gun bullets are sampled from the
  real pool, not a round-by-round record, and do not invent hit events.
- GitHub Issues are the primary feedback path; Reddit is for discovery and
  announcements. No public publication or automatic feedback upload is implied.
