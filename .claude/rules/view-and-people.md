---
paths:
  - "Assets/Scripts/HomeViz/ViewController.cs"
  - "Assets/Scripts/HomeViz/HomeRenderer.cs"
  - "Assets/Scripts/HomeViz/OccupancyClock.cs"
  - "Assets/Scripts/HomeViz/TimelineBar.cs"
  - "Assets/Scripts/HomeViz/LabelBillboard.cs"
  - "Assets/Scripts/HomeViz/InteriorMaterialPalette.cs"
  - "Assets/Scripts/Authoring/Interior/OccupancyModel.cs"
  - "Assets/Scripts/Authoring/Interior/OccupantTypes.cs"
  - "Assets/Scripts/Authoring/Interior/Clock.cs"
  - "Assets/Scripts/HomeViz/Tools/PeopleTool.cs"
  - "Assets/Materials/Interior/**"
  - "Assets/Resources/InteriorMaterialPalette.asset"
---

# Scene exposure, view modes, and people

> Loaded when a file under the paths above is read. Rules only: the reasoning is in the design note linked at the end. Edit this file when a rule changes; update CLAUDE.md only if something every session needs moves.

## Scene exposure and palette

There is **no tonemapping**, so anything over 1.0 clips per channel. Ambient is tuned so an up-facing
surface totals about **0.83×** (sun 52°, N·L 0.788): **do not raise the sun back to 1.1**; the
headroom is what the palette spends. Every material here is HomeViz-only (referenced by
`InteriorMaterialPalette.asset` and the scene's `GroundPad`).

| Surface | Albedo | Renders about | L\* |
|---|---|---|---|
| `Ground_Pad` | 0.20 | 0.18 | 19 |
| `Wall_Edge` (wall tops, end caps, opening reveals) | 0.38 | 0.35 | 37 |
| `Paint_Warm` (wall face) | 0.50 / 0.45 / 0.38 | 0.46 / 0.41 / 0.35 | 45 |
| `Tile_Bath` (wall face) | 0.42 / 0.48 / 0.50 | 0.39 / 0.44 / 0.46 | 45 |
| `Paint_White` (wall face) | 0.50 / 0.49 / 0.46 | 0.46 / 0.45 / 0.42 | 48 |
| `Floor_Carpet` | 0.68 / 0.50 / 0.50 | 0.62 / 0.45 / 0.42 | 52 |
| `Floor_Oak` | 0.72 / 0.50 / 0.28 | 0.66 / 0.45 / 0.23 | 53 |
| `Ceiling_White` | 0.55 | 0.51 | 53 |
| `Floor_Vinyl` | 0.74 / 0.70 / 0.44 | 0.68 / 0.63 / 0.37 | 66 |
| camera background | -- | 0.94 | 98 |

Three constraints, all binding: nothing clips (floors ≤ 0.74 per channel); **nothing in the dwelling
is white** (walls 0.50, ceilings 0.55, so the model has a silhouette against the 0.94 background);
and **light label text must read on a wall face** (L\* 48 against 0.98 glyphs). `Wall_Edge` is the
cap (from overhead the entire visible surface of a wall) and is the tone every floor is measured
against (ΔE contract in `.claude/rules/walls-and-rooms.md`); it does not move.

→ [`docs/design/view-and-people.md`](../../docs/design/view-and-people.md)

## View modes

| Mode | Behaviour |
|---|---|
| **Overview** *(default; `Mode.Overview` leads the enum)* | Perspective free-look, ceilings hidden. Right-drag turns the camera in place with the cursor captured; middle-drag pans (captured); scroll dollies; WASD relative to facing; **Q/E** lower and raise. |
| **Walkthrough** | First person with collision. **Standing 1.60 m / Seated 1.19 m** toggle (`EYE_HEIGHT_SEATED`, shared with the seated marker). **R** returns to a clear spot. |

- There is no Plan mode any more (free-look pitches to 89°, and the report's plan shot is the
  orthographic reading). `ViewController` never sets `cam.orthographic`; `ReportCapture.Plan` owns
  its own orthographic camera.
- **`FreeLook` rotates about the eye** as a round trip, keeping `_pivot`/`_yaw`/`_pitch`/`_distance`
  canonical (`eye = _pivot + Euler(pitch, yaw, 0) * (0, 0, -distance)`; apply delta;
  `_pivot = eye + forward * distance`), so `ApplyOrbit`, `FocusOn`, `FrameContent`, `KeyPan`,
  `ClampLift` are untouched. Pitch `[-85°, 89°]`. **Sensitivity is degrees per pixel, never per
  second** (`Mouse.delta` already accumulates the frame).
- **Cursor capture**: lock + hide on press, restore and warp back to the press point on release; a
  drag ends on `isPressed` false *or* `lockState` no longer `Locked`; `SetMode`, `OnDisable`,
  `OnApplicationFocus(false)` end drags; `SyncCapture` is the single place that releases; the first
  frame of a capture swallows its delta.
- **Drags latch**: a drag only begins in the 3D view (`PointerOverUI`) and then continues anywhere;
  the gizmo tick is deliberately not gated. `TypingInUI` guards `KeyPan`, `KeyLift` and the
  walkthrough keys.
- **`ClampLift` bounds the camera, not the pivot**, re-run every frame; the upper cap moves onto the
  eye when looking up.
- `FocusOn(point, closeUp)` only ever tightens (5 m). `HomeEditController.FocusElement` is the single
  entry point (roster rows, **F**); clicking a marker in the plan does not focus.
- **Only the shell is solid**: `HomeRenderer.PickOnly` marks every pick-only subtree (opening
  handles, furniture, occupants, devices) `isTrigger`; `Physics.RaycastAll` still hits them.
  `EnterWalkthrough` sets `skinWidth` to a tenth of the radius, `minMoveDistance` 0, and spawns at
  `StandableStart` (centre of the room's largest inscribed circle).

→ [`docs/design/view-and-people.md`](../../docs/design/view-and-people.md)

## People

- **`OccupantDef` hangs off `VariantDef`**, not `LevelDef`, who sleeps where is a thing a proposal
  changes, and `NewProposalFrom` preserves ids so the diff reports a move, not delete + add.
- **`ActivityDef` carries a kind and a `roomId`**; the kind only colours the block and suggests a room,
  the `roomId` places the person; null = away. Times are **minutes from midnight, 0-1439**; `end` before
  `start` wraps; equal = all day. `Clock` is the only place a time becomes text and follows
  `Units.Display` for 12/24-hour.
- **Positions are derived, never stored**: `OccupancyModel`. Posture comes from the anchored **item**
  (`PostureFor(prefabType)`: `On` / `At` / `InFrontOf`), not the activity kind. `IsClear(p, radius, …)`
  requires the disc inside the room **net of half the wall thickness** and clear of every other item
  taller than 0.15 m; `TryFindClearSpot` grids from the inscribed centre and **relaxes rather than
  refusing** (smaller radius, then zero). `WheelchairRadius` is 0.45. Several people in one room fan
  onto a ring; co-anchored people spread along the item. The grid search is memoised per `LevelDef`
  (`InvalidateCache` from `HomeRenderer` on rebuild).
- **`OccupancyClock` is a plain class held by `HomeRenderer`**, ticked from its `Update`. Not in a
  tool (gated on `!PointerOverUI`) and not in the document (undo/dirty). `Advance` returns true only
  on a whole-minute change; `UpdateOccupantPoses` writes transforms on cached markers, no teardown.
- Markers are tinted capsules with name + activity; `usesWheelchair` gives a seated marker over a
  wheelchair-sized pad. No walking, no animation.
- **`TimelineBar`** is a permanent full-width strip along the bottom (plain class, drawn from
  `HomeEditController.OnGUI`, everything derived). Collapsed `CollapsedHeight` (114 px: hour ruler, a
  3 px lane per person up to four, the alert lane, the transport: each a named constant, summed);
  expanded it is **`ExpandedHeight(n)`**: exactly one 46 px row per person, capped at 0.55 × the
  window, past which the rows scroll. **Its rect is in `PointerOverUI`**; the toggle is deferred
  (`_pendingTimelineToggle`); the scrollbar is accounted for once before the ruler, now-line and rows.
  **One clock, not three**: the transport lives here only; `SetStage(People)` expands it and
  **leaving People collapses it** (the chevron still works from any stage). **The clock is a
  `MeasureUI.Time` field** (drag or type; no slider); a click on the hour ruler scrubs to that time;
  the speed chip cycles round through `OccupancyClock.Speeds`. Clicking an alert diamond scrubs and
  selects via `_pendingAlert`. **Every activity block hovers** (`UITooltip.Hover`: span, activity ·
  room, in the strip too); the now-line is 3 px, runs through the ruler and carries a time chip on
  it; an hour grid (hairline each hour, stronger every third) is drawn over every track; the ruler,
  roster name and state use `TimelineBar`'s own zero-padding styles: never the 13 px skin label in a
  hand-sized rect.

→ [`docs/design/view-and-people.md`](../../docs/design/view-and-people.md)
