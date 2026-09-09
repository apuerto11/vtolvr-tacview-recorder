# Deterministic VTOL VR ground-vehicle shapes

## Scope and contract

This catalog supplies an ACMI `Shape` only for ground-vehicle families that
were observed in the 2026-08-11 Target Practice recording.  It is separate
from the in-game `Name`; for example:

~~~~text
Name=MAD-4 Launcher,Shape=AntiAircraft.SAM.SA-6.2P25.obj
~~~~

The original ground-unit name, coalition, and any other metadata must remain
unchanged. `Shape` is only a Tacview rendering hint. The catalog is pure .NET,
uses a normalized exact dictionary key, and never performs a substring,
prefix, suffix, fuzzy, or heuristic model lookup.

Normalization trims leading/trailing whitespace, collapses internal
whitespace, and uses invariant case. It does not remove role words, rewrite
punctuation, or otherwise generalize a name. Every alternate name below is an
explicit alias. An unknown vehicle, including every airbase, receives no
shape.

## Shape evidence

The filenames are present in Tacview's official default-properties database:

- [Core database](https://raw.githubusercontent.com/Vyrtuoz/Tacview/master/Database/Default%20Properties/Tacview.DefaultProperties.Core.xml)
- [Ground vehicle military database](https://raw.githubusercontent.com/Vyrtuoz/Tacview/master/Database/Default%20Properties/Tacview.DefaultProperties.GroundVehicle.Military.xml)
- [Anti-aircraft radar database](https://raw.githubusercontent.com/Vyrtuoz/Tacview/master/Database/Default%20Properties/Tacview.DefaultProperties.AntiAircraft.Radar.xml)
- [Anti-aircraft SAM database](https://raw.githubusercontent.com/Vyrtuoz/Tacview/master/Database/Default%20Properties/Tacview.DefaultProperties.AntiAircraft.SAM.xml)
- [Tacview database documentation](https://www.tacview.net/documentation/database/en/)
- [Tacview 3D-model documentation](https://www.tacview.net/documentation/3dobjects/en/)

The recorder packages no Tacview assets. Each mapping is an explicit visual
proxy for a fictional VTOL VR unit, not an assertion of real-world identity.

## Mapping inventory

| Observed VTOL VR name(s) | Explicit Shape | Confidence | Mapping rationale |
| --- | --- | --- | --- |
| Artillery; `MPA-155` | `Vehicle.Artillery.M109.obj` | High | The MPA-155 is a self-propelled howitzer; the M109 is a close visual-role proxy, not an identity claim. |
| `MBT2-E Tank`; `MBT2-E` | `Vehicle.Tank.T-62A.obj` | Medium | Generic main-battle-tank proxy for the fictional MBT2-E; its visible proportions differ. |
| `MAD-4 Launcher` | `AntiAircraft.SAM.SA-6.2P25.obj` | Medium | Mobile SAM-launcher proxy for the fictional launcher. |
| `MAD-4 Radar` | `AntiAircraft.SAM.SA-6.1S91.obj` | Medium | Mobile combined search/fire-control-radar proxy for the fictional radar vehicle. |
| `IR APC` | `Vehicle.APC.BTR-80.obj` | Low | Generic APC proxy only; the VTOL VR configuration remains distinct. |
| `SAM Radar`; `SAM S/A Radar` | `AntiAircraft.SAM.SA-3.SNR-125.obj` | Medium | Stationary SAM-radar proxy for the observed radar family. |

The last recording also contained Barton, Danboro, and Winchester airbases.
They intentionally remain unmapped: an airbase is not a ground-vehicle shape
candidate, so it keeps its normal Tacview aerodrome presentation.

## Integration rule

A ground metadata adapter should call
`VtolGroundShapeCatalog.TryGetShape(platformName, out shape)` and pass the
result only as the optional ACMI `Shape` metadata field. It must retain the
source platform name rather than replacing it with the proxy model name.

Examples of deliberate non-matches:

~~~~text
Enemy Artillery       -> no Shape
Artillery Battery     -> no Shape
MAD-4 Launcher X      -> no Shape
SAM Radar v2          -> no Shape
Barton Airbase        -> no Shape
~~~~
