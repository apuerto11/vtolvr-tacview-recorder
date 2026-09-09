# Changelog

This file records intended community-visible changes. It does not mean that a
version has been published.

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
