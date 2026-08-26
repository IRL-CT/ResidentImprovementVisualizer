---
paths:
  - "Assets/Scripts/ResidenceViz/Tools/**"
  - "Assets/Scripts/ResidenceViz/ResidenceWorkflow.cs"
  - "Assets/Scripts/ResidenceViz/ResidenceEditController.cs"
  - "Assets/Scripts/ResidenceViz/ResidenceToolContext.cs"
---

# Workflow stages and tools

> Loaded when a file under the paths above is read. Rules only: the reasoning is in the design note linked at the end. Edit this file when a rule changes; update CLAUDE.md only if something every session needs moves.

## Workflow stages: `Assets/Scripts/ResidenceViz/ResidenceWorkflow.cs`

The command bar selects a **stage**; the right rail shows that stage's tools. The stage is a field on
`ResidenceEditController`; `ResidenceWorkflow` is just the table. `ResidenceWorkflow.Label` is the enum member split
at each capital: **the enum member IS the tab label**, there is no second table of names.

| Stage | Tools | |
|---|---|---|
| **Select** | Select | The pointer and the inspector. Its own tab, first in the bar. |
| **Import** | Import | Import a plan (image or PDF) calibrate it, add or name floors. |
| **Structure** | Walls, Openings, Rooms | The shell. Walls that close off an area make a room by themselves. |
| **Furnish** | Furniture | Including wall-mounted grab bars and cabinets. |
| **Smart living** | Equipment, Monitor | Sensing devices, everyday aids, and the grab bars that go with them. After Furnish because a device hosts on an element. |
| **People** | People | Who lives here and what their day is. Expands the timeline bar; leaving collapses it. |
| **Review** | Compare, Measure | Opens on **Compare**. |
| **Outdoors** | Outdoors | **Absent unless `ResidenceDoc.exteriorEnabled`.** |

- **Select is a tab, not a chip.** `PrimaryToolId` is `ToolIdsFor(stage)[0]`; the rail's tool picker
  is a `UITheme.Segmented` control (deferred via `RequestTool`), and stages with one tool draw none. `ResidenceEditController.TryAutoSelect` carries a click on furniture, a wall mount or a
  resident to the Select tab from any other tab; it runs before the active tool and sets
  `ResidenceToolContext.ClickConsumed`, which `ResidenceToolBase.LeftClicked` reads. `IResidenceTool.ClaimsClicks` opts
  a tool out (always for Opening/Furniture/People/Outdoor, mid-run for Wall/Room, mid-wizard for
  Underlay). **The whitelist is load-bearing**: admitting `Floor`/`Room`/`Ceiling` would make every
  click inside a room select the floor instead of placing a wall corner; walls and openings are
  excluded because clicking a wall is what the openings tool is for.
- **Esc is a two-rung ladder**: first press deselects; with nothing selected a second press returns to
  `_stageBefore`.
- **`NewResidence` lands on Structure**: it arrives with the `StarterRoom` already on the floor, so the
  next move is Walls / Openings / Rooms. The library's empty-state **Start from a floor plan** still
  asks for Import, and wins by requesting after `NewResidence` returns.
- **Digits 1-N pick a tool within the stage; Ctrl+1-8 switch stage.** The count is however many
  stages `VisibleStages` returns (eight with the exterior on). A ladder one rung short leaves the last
  stage unreachable.
- Below the active tool the rail ends in one collapsed **Outdoors** foldout (the exterior toggle).

→ [`docs/design/ui.md`](../../docs/design/ui.md)

## Tools: `Assets/Scripts/ResidenceViz/Tools/`

Each tool is one file implementing `IResidenceTool`, registered in `ResidenceEditController.Awake` and placed in
a stage by `ResidenceWorkflow`. Each carries a **`Hint`**: the how-to sentence, surfaced as the tooltip of
its tab (`ResidenceEditController.StageTips`) and its chip.

| Tool | What it does |
|---|---|
| **Select** | Pick a wall / room / item: **not a door**, which is reached through its host wall (a click on an opening handle redirects to `marker.parentId`; the wall's rail lists its openings, also on a locked baseline, and `DrawOpening` draws the same list; hovering a row haloes it via `HoverOpeningId`, cleared at the top of `OnGUI`). Rail leads with the **name**, then controls, then one muted line of figures above Delete; a doorway keeps its step-free badge. Furniture gets the **transform handles** (Move/Rotate/Scale segmented control driving the scene gizmo and the rail; arrows nudge, Shift finer, **Z/X** quarter-turn, Shift snaps 15°). A wall mount gets offset/side controls and drags onto the nearest wall. A device gets no gizmo but a privacy badge, a *reports to staff* toggle and its thresholds. |
| **Walls** | Click a chain of corners. Live length + angle readout; type a length (`12' 6"`) mid-run. Snaps endpoints → square onto a crossed wall (a crossing within `MinSeg` of the wall's end welds to its corner) → centerlines → level with a parallel wall's end (dashed guide to the endpoint) → 45° → grid. Every commit runs through `WallLinker`; **Shift = draw free**. |
| **Openings** | Width, height and sill are free numbers (drag or type); live **clear passage** readout, doors only (no leaf ⇒ clear = rough, in the rail and the overlay). Every placement runs through `OpeningFit`; what you place comes up selected. The width field is deliberately *not* bounded by `MaxWidth` here (the hovered wall changes every frame): the preview turns red instead. |
| **Rooms** | Says what each area **is**: the rail leads with the twelve type rows, always visible. Arming one makes every click in the plan assign that type (and select the room, staying armed); the armed row clicked again disarms. The floor's rooms fold behind one dropdown `StateRowLine` header naming the picked room (or `Rooms (N)`); the open list closes on pick (deferred via `_pendingListToggle`), and the picked room's name, type chips (folded behind the current type) and area sit below the closed header. Untyped rooms draw dashed; the carve's bridge edges are never outlined. **Detect rooms** appears only when stored rooms and the wall graph disagree. |
| **Furniture** | Searchable catalog grid; each tile draws the item's true footprint against one fixed 2.3 m reference (not normalised per tile). **Z/X** rotate 90°, Shift+scroll 15° (not Q/E: those lift the camera and are gated only on `TypingInUI`). Wall-mounted items snap to the hovered face via `MountPlacement`. Every placement runs through `FurnitureFit`; the ghost previews the fitted spot; what you place comes up selected. |
| **Equipment** | Grid over the 25-item Smart living catalog plus a **Fixtures** chip (grab bars, handrail → `WallMountDef`, no price). Tiles draw **reach** for the five things that sense at a distance, **footprint** for anything else installed, the entry's own colour for anything worn. Click the doorway/bed/range/room it goes on (`SensorFit`), pick a resident for anything personal, or leave it as **Nobody** (default) and put it down in a room. No package button. Coverage overlay while open. |
| **Monitor** | The DSP console: live alert cards with the response beside each (Prompt · Call · Check · Dispatch), roster and positions, coverage, gaps and cost. **Role switch DSP / Family / Resident** filters by `SensorPrivacy`. Answering an alert never dirties the file. |
| **People** | Roster, and one person's day: name, wheelchair, colour, activity blocks (15-minute steps; room set by clicking the plan). The clock is in the timeline bar, not here. |
| **Compare** | What a proposal changes, against any other variant. Rows grouped by room (trailing "Elsewhere"); clicking one selects and focuses **without leaving the tab** (`reveal: false`); markers in the plan (green added, red removed, accent changed). **✕ reverts that one change** via `VariantRevert`. Also the proposal's `description`, the ghost toggle, and **Generate report**. |
| **Measure** | Point-to-point with running totals, plus turning space per room: **the only place the turning circle is reported**. |
| **Import** | **Floors** leads, merged with the plan list: one row per floor: an inline name field (edit renames, click selects), the plan's filename, a round ↻ replace glyph, and a ✕ removing floor+plan behind a two-click confirm (the last floor's ✕ removes only the plan); an add-floor button + prefilled name field closes the section. Then, for the active floor's plan: **Scale**: one self-labelled button (`Scale · <image width>` / `Set scale…`) opening the two-point calibration: a folded *Display* section (opacity · angle · lock), and the headerless **Read the plan** controls (short label; the ⚠ beside it names what it replaces). A multi-page PDF offers a page grid and one storey per page. |
| **Outdoors** | Draw a **walkway** or **entry ramp** (`PathDef`) or a **railing** (`FenceDef`) with the wall tool's gesture; live 1:12 slope check. |

→ [`docs/design/ui.md`](../../docs/design/ui.md) · [`docs/design/walls-and-rooms.md`](../../docs/design/walls-and-rooms.md) · [`docs/design/furniture.md`](../../docs/design/furniture.md)
