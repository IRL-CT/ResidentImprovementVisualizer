---
paths:
  - "Assets/Scripts/**/Sensor*.cs"
  - "Assets/Scripts/ResidenceViz/Tools/MonitorTool.cs"
  - "Assets/Resources/SensorCatalog.asset"
  - "Assets/Resources/SmartHomeTechnology/**"
  - "docs/SMARTHOME.md"
---

# Smart living

> Loaded when a file under the paths above is read. Rules only: the reasoning is in the design note linked at the end. Edit this file when a rule changes; update CLAUDE.md only if something every session needs moves.

## Smart living

Every **sensing** figure (device, threshold, cost, coverage) comes from
`Assets/Resources/SmartHomeTechnology/SmartHomeReport.pdf` (Cornell Tech / Center for Family Support,
July 2025), mapped section by section in [`docs/SMARTHOME.md`](../../docs/SMARTHOME.md). An unattributable
figure is worse than none.

- **Catalog (`SensorCatalog.asset`): 25 items = 16 sensing devices + 9 everyday aids.** The stage is
  `ResidenceStage.SmartLiving`, the tool is Equipment, but **`LevelDef.sensors`, `SensorDef`, the asset,
  the tool id `"sensor"` and the `Sensor*.cs` files keep their names** (renaming drops the layer out
  of saved residences / moves a GUID / breaks `StageOf`). An aid is a `SensorDef` with `coverageRadius = 0`
  and no `DefaultRules` ⇒ `SensorOverlay`, `SensorSim`, `SensorRules`, `SensorCoverage` ignore it;
  **no figure about what the residence can see moves when an aid is installed**. `provenance` and
  `speculative` stay as **data only**: the rail/console ⚠ glyphs and the report's per-row notes were
  removed on request, and no user-facing surface prints them; `SensorPrivacy.None` ("Not connected")
  ranks 0 tied with `Passive`. `SensorTool.RaisesAlerts` scopes the duplicate guard to devices that
  alert. The rail's cost row prints `Entry.CostLine` (whole dollars, one line); exact figures live in
  the tooltip. The coverage row is labeled **Range**, and there is no "Installs on"/"Kept in" row,
  the where-to-click instruction is the grid tile's tooltip.
- **A device hosts on an element, never on a coordinate** (`SensorHost`: opening, furniture item,
  occupant, `Room`, `Point`). `SensorPose.Resolve(level)` derives the pose; an **occupant** host has
  **no pose** (`resolved = false`, shown against the person). Checks read `sensor.hostKind`, never
  `entry.IsWorn`. **Nothing renders floating**: hub, thermostat, fall radar and motion sensor are
  `Wall` hosts; dispenser, air monitor, speaker and measuring set sit ON a `SensorFit.Surfaces` item,
  a `Furniture` host whose `position` is the spot in the HOST'S own unrotated frame, posed at the
  item's top plus half the device height (`position` null = the pre-surface centre pose, which is what
  every older instance on disk has). A personal item defaults to **Nobody** → put down on the nearest
  surface (`KeptHeight` is gone). An `Opening` host sits proud of the wall face in `hostSide`, and
  wall-aimed clicks go through `MountPlacement.WallCursor` (the pick ray, not the floor projection).
  **`SensorDef.facingYaw` is a DELTA from the host's own facing** (wall normal, item rotation); only
  Room/Point store world yaw. Every cascade that removes a host removes its devices, in
  `SelectTool.DeleteSelected` and `VariantRevert` (one host deeper: sensors on the openings of a
  reverted wall).
- **`SensorFit` refuses rather than slides**; clicking **on** a window refuses even with a door nearby;
  `door_sensor` may go on a window, lock and doorbell may not.
- **Grab bars via a Fixtures chip** write `WallMountDef` through `MountPlacement` (shared with the
  Furniture tool); no price; `threshold_ramp` is absent (it is a floor item).
- **`SensorSim`** walks all 1,440 minutes of `OccupancyModel.PoseAll` into events; `SensorRules` turns
  events into alerts. **Deterministic, nothing stored.** `Mode.Routine` (the household's day) **must
  raise zero alerts on all six samples**; `Mode.Eventful` injects seven scenarios at fixed times, one
  incident in one place. The false-alarm floor: the stove rule measures the hob on **with nobody in
  the kitchen** (the `Unattended` event stream); a fall needs someone **awake** in the room; …whom no
  other sensor in that room can see.
- **`SensorCoverage`** clips envelopes to the room (no occlusion within a room, deliberately);
  `Envelope.Of` resolves once. **Motion sensors go in a corner**, two at opposite ends when the room is
  longer than 0.8 × radius.
- **`SensorCost`**: the monthly system fee lives on `central_hub` once per building; only the pendant
  and the dispenser carry their own monthly. **§4.1's vendor savings claims appear nowhere**; the only
  offset is §5.2.2's $20-40 per remotely answered incident × `AssumedIncidentsPerWeek` (3), printed
  beside the result.
- **`MonitorTool`** filters by role through `SensorPrivacy` (`video_doorbell` is the only Video
  device); responses are console state, never document state.
- `ResidenceRenderer` draws each device as a labeled box with a material per device (`UpdateSensorStates`
  tints idle / active / alerting; `ReportCapture` freezes them idle). `TimelineBar` has an alert lane.
  `SensorOverlay` is controller-owned.
- **The package tiers are gone** (`SensorPackages.Missing`/`Label`/`Describe` deleted); **`Recommend`
  and `Tier` stay**. They build the two care samples' shipped proposal, and `Recommend` is
  deliberately not extended to everyday aids.

→ [`docs/design/smart-living.md`](../../docs/design/smart-living.md)
