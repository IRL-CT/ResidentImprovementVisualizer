# Residence Improvement Visualizer. Developer Notes

**This repository is the Unity project.** Its root is the project root: `Assets/`, `Packages/`,
`ProjectSettings/` are directly here, and every path below is repo-relative. Open this folder in
Unity **6000.3.10f1**.

A stand-alone desktop tool for visualising proposed improvements to a residence or apartment. Widening a
doorway, adding grab bars, removing a threshold, rearranging a bedroom so a wheelchair can turn, and
holding "how it is now" next to "what we're proposing" so residents, families and care staff can
compare them in a meeting.

**It requires no Python and no server, and it works offline.** The one exception is opt-in and
per-press: *Read the plan* in the Import rail sends that one sketch image to the Anthropic API and
gets a floor plan back: only when a key has been entered and the button pressed. Everything else,
including every residence you have, stays on the machine. Residences are files under
`Application.persistentDataPath/ResidenceImprovementVisualizer/`.

> **Legacy. CXRSite.** This project also still contains the original outdoor site-visioning
> tool: the `BasicModel` / `VRViewer` scenes, `EditController`, `WorldRenderer`, `TileBuildingEditor`,
> `LibraryClient`, `SyncClient`, `ModelRequester`. Nothing was deleted; it all still compiles and
> builds via **Build → Legacy. Site**. Its developer notes are in [`docs/SITE.md`](docs/SITE.md), and
> **its Python backend now lives outside this repository** at `../CXRLayoutGen/` (Flask server on
> :5002, LLM layout generation, the JSON data store, sketch inputs, and the RI pilot study). Some of
> its code is genuinely shared: `WorldRenderer`, `PrefabRegistry`, `SiteDef`/`PathDef`/`FenceDef`,
> `EnvironmentScale`, `TransformGizmo`, because ResidenceViz renders through it. Do not "clean up" those.

## Working in this repo

Three layers, each with one job. **CLAUDE.md** (this file) is what every session needs: what the repo
is, how to run and test it, the data model, the decisions that must not be re-opened, where
everything else lives. **`.claude/rules/<area>.md`** holds the rules for one area: each file carries
`paths:` frontmatter and Claude Code loads it only when a file matching those globs is read, so the
rules bind where they apply without sitting in every session. **`docs/design/<area>.md`** holds the
reasoning: rationale, history, bug post-mortems.

- **When you change a rule, change it in its rules file** (one or two lines, in place). Edit
  CLAUDE.md only when something every session needs has moved. Rationale goes in the design note.
- **Not a changelog.** Do not quote test counts, line counts or other figures that go stale.
- **Sizes:** keep CLAUDE.md under ~200 lines; keep a rules file under ~8 KB, if it grows past that,
  split it by `paths`, do not let it sprawl. (Official guidance: a long CLAUDE.md is one Claude skims.)
- A new area gets a new rules file (frontmatter + the rules) and a row in the table below.

| Area | Rules (path-scoped) | Reasoning |
|---|---|---|
| Sample residences, `PlanBuilder` | [`.claude/rules/samples-and-planbuilder.md`](.claude/rules/samples-and-planbuilder.md) | [`docs/design/samples-and-planbuilder.md`](docs/design/samples-and-planbuilder.md) |
| Walls, openings, rooms, floor finish, the fits | [`.claude/rules/walls-and-rooms.md`](.claude/rules/walls-and-rooms.md) | [`docs/design/walls-and-rooms.md`](docs/design/walls-and-rooms.md) |
| Workflow stages, the tools table | [`.claude/rules/workflow-and-tools.md`](.claude/rules/workflow-and-tools.md) | [`docs/design/ui.md`](docs/design/ui.md) |
| UI rules (no prose, the fields, buttons, spacing scale, layout) | [`.claude/rules/ui.md`](.claude/rules/ui.md) | [`docs/design/ui.md`](docs/design/ui.md) |
| UI chrome (`ModeBand`, overlays, text over the scene, units chip) | [`.claude/rules/ui-chrome.md`](.claude/rules/ui-chrome.md) | [`docs/design/ui.md`](docs/design/ui.md) |
| Import, PDF, Read the plan, storeys | [`.claude/rules/import-and-sketch.md`](.claude/rules/import-and-sketch.md) | [`docs/design/import-and-sketch.md`](docs/design/import-and-sketch.md) |
| Variants: compare, revert, ghost, report | [`.claude/rules/variants-and-report.md`](.claude/rules/variants-and-report.md) | [`docs/design/variants-and-report.md`](docs/design/variants-and-report.md) |
| Scene exposure & palette, view modes, people | [`.claude/rules/view-and-people.md`](.claude/rules/view-and-people.md) | [`docs/design/view-and-people.md`](docs/design/view-and-people.md) |
| Furniture catalog, art binder, transform handles | [`.claude/rules/furniture.md`](.claude/rules/furniture.md) | [`docs/design/furniture.md`](docs/design/furniture.md) |
| Optional exterior layer | [`.claude/rules/exterior.md`](.claude/rules/exterior.md) |: |
| Smart living | [`.claude/rules/smart-living.md`](.claude/rules/smart-living.md) | [`docs/design/smart-living.md`](docs/design/smart-living.md) · [`docs/SMARTHOME.md`](docs/SMARTHOME.md) |

## Running it

Open `Assets/Scenes/ResidenceViz.unity` and press Play. **The first launch seeds six sample residences.**

Build with **Build → ResidenceViz (PC, Windows)** (`Ctrl+Shift+H`) →
`Builds/ResidenceViz/ResidenceImprovementVisualizer.exe`. That build ships **only** the ResidenceViz
scene; the Site stack is absent from it.

## Concepts

- **Residence** (`ResidenceDoc`): one dwelling. The unit of save/load, of the library list, and of export.
- **Variant** (`VariantDef`): one design option. Every residence has a **baseline** ("Existing", locked
  everywhere but on a residence created from scratch) plus any number of named proposals. Switching is
  a re-render. Comparing two variants gives
  a plain-English change list, any line of which can be reverted on its own. See `ModeBand`.
- **Level** (`LevelDef`): one storey. **One is edited and rendered at a time**, chosen by the floor
  chip in the top bar (drawn only when the residence has more than one).
- **Underlay** (`UnderlayDef`): the imported floor-plan sketch, calibrated so tracing is at true scale.
  One per storey, keyed by `levelId`.
- **Occupant** (`OccupantDef`): someone who lives here, with a 24-hour timeline. Their position is
  **derived** from the schedule and the clock, never stored.

## Data model: `Assets/Scripts/Authoring/Interior/InteriorTypes.cs`

```
ResidenceDoc
 ├ underlays: [ UnderlayDef ]       one traced sketch PER STOREY, keyed by levelId
 ├ customItems: [ CustomItemDef ]   furniture this household owns; shared by every variant
 └ variants: [ VariantDef ]
      ├ levels: [ LevelDef ]
      │    ├ walls:       [ WallDef ]        centerline a→b + thickness + height
      │    ├ openings:    [ OpeningDef ]     offset along a wall, width, sill, threshold
      │    ├ rooms:       [ RoomDef ]        floor polygon, DERIVED from the walls
      │    ├ furniture:   [ ObjectInstance ] ← REUSED from AuthoringTypes.cs
      │    ├ wallMounted: [ WallMountDef ]   grab bars, cabinets; uses the DecorAlignment fields
      │    └ sensors:     [ SensorDef ]      smart home devices; each hosts on what it watches
      ├ exterior:        SiteDef             ← REUSED; null by default (see `.claude/rules/exterior.md`)
      ├ exteriorObjects: [ ObjectInstance ]
      └ occupants:      [ OccupantDef ]      the household (see `.claude/rules/view-and-people.md`)
           └ schedule:  [ ActivityDef ]      kind + start/end minutes + roomId (+ optional anchor)
```

Two reuses are load-bearing and must not be "cleaned up" into new types: **`ObjectInstance` is the
furniture type** (`boxSizeMeters` = the item's true size), and **`SiteDef` is the exterior layer**
(`PathDef` a ramp or walkway, `FenceDef` a railing, `SurfaceStrokeDef` a patio).

Deleted fields, on purpose and not deprecated (Newtonsoft's `MissingMemberHandling` defaults to
`Ignore`, so residences on disk simply drop the keys): `RoomDef.floorMaterial` / `ceilingMaterial` (floor
finish is now `RoomFinish.FloorMaterial(roomType)`), `WallDef.structural`, `OpeningDef.swing`
(`ResidenceMetrics.ClearWidth`: a door loses its leaf and stop; a measured `OpeningDef.clearWidth` still
wins). `ResidenceDoc.underlay` stays *declared* because `ResidenceStore.Migrate` folds it into `underlays`.

## Deliberate decisions. Do not re-open

Each of these was argued out; the reasoning is in the linked notes.

- **US English, no dashes, nothing said by contrast** in every display string, the report and the
  Anthropic prompt. `"None"` is the empty placeholder. `SampleResidenceInstaller.LegacyNames` keeps its
  em dashes because those names key residences already on disk. → ui
- **No PDF writer** for the report. Self-contained HTML that prints to PDF. (The PDF *reader* is the
  opposite call: rasterizing is a different problem.) → variants-and-report, import-and-sketch
- **The API key is plaintext** on disk; DPAPI is a stated not-yet. → import-and-sketch
- **A residence created from scratch opens on `StarterRoom`**: 3 x 3 m on centerlines, "Living room",
  no opening, and its baseline **unlocked**, alone in the project (it has no record to protect yet).
  `SketchInstall.IsEmpty` counts an untouched one as empty; `NewResidence` lands on Structure.
  → samples-and-planbuilder
- **A custom item ("Make your own") is created and deleted, never edited**, and its id is `custom:` plus
  a slug of its name, because `VariantDiff` and the placeholder box both recover the label from the key
  alone. Deleting drops the definition and **leaves every placement standing**. → furniture
- **The ghost draws no openings.** → variants-and-report
- **`SensorPackages.Recommend` is not extended** to everyday aids; **package tiers are gone**. → smart-living
- **`Assets/Resources/PrefabRegistry.asset` is never touched**; ResidenceViz uses
  `ResidenceCatalogRegistry.asset`. → furniture
- **`Tile_Bath` is never a floor**; `Wall_Edge` does not move; the sun stays at its exposure. → walls-and-rooms, view-and-people
- **14 catalog items stay labeled boxes**: `wheelchair`, `walker`, `hospital_bed`, `transfer_bench`,
  `patient_lift`, `shower_seat`, `roll_in_shower`, `grab_bar_24`, `grab_bar_36`, `handrail`,
  `light_switch`, `outlet`, `thermostat`, `threshold_ramp`. No `shower_seat` in any sample. → furniture
- **Occlusion within a room is not modelled** in coverage; **openings in perpendicular walls are not
  considered** by the fits; `PlanBuilder.Free` is unguarded; wall mounts do not check each other. → smart-living, samples-and-planbuilder
- **Deleted, not deprecated**: `Mode.Plan`, `WallDef.structural`, `OpeningDef.swing`,
  `RoomDef.floorMaterial`/`ceilingMaterial`, `TabTight`, `MeasureField`/`DrawMoveControls`, the inch
  preset chips, the sensor tier bar. `ResidenceDoc.underlay` stays **declared** for migration. → walls-and-rooms, ui, view-and-people
- **`SampleResidences.Generation` was not bumped** for the RoomFinish and swing schema changes (nothing
  visible moved). → walls-and-rooms
- **Neither `Relink` nor `Sync` runs after a generated plan**, on load, in `Migrate`, or from
  `VariantRevert`. → import-and-sketch, walls-and-rooms
- **Turning circles** are reported only in `MeasureTool`; `ResidenceMetrics.LargestInscribedCircle` stays
  (used by `OccupancyModel`, `StandableStart`, `RoomRegions.Sync`). → ui
- **Not ported from the Site server**: content-hash dedup, multi-client sync. **Not in ResidenceViz**:
  ground painting, scatter, lot editing.
- **`ReportCapture` keeps its own orthographic plan camera** although Plan view mode is gone. → view-and-people
- **Clock follows the units chip** (metric ⇒ 24-hour). → ui

## Rules-ready, but no rules

`ClearanceRules.Registry` ships **empty**. The data a rule would need exists: `OpeningDef.clearWidth`,
`OpeningDef.thresholdHeight`, `FurnitureCatalog.Entry.clearanceFront/Side`, `ObjectInstance.boxSizeMeters`,
`ResidenceMetrics.LargestInscribedCircle`.

## Storage

```
<persistentDataPath>/ResidenceImprovementVisualizer/
    residences/<id>.json            one ResidenceDoc per file
    residences/_archive/<id>.json   soft-deleted; nothing is ever destroyed
    underlays/<id>/<image>          the traced sketches, one per storey
    reports/<residence> - <proposal> - <date>.html
    settings.json
    anthropic.key                   the API key, if one was entered; NOT in settings.json
```

Writes are atomic (temp file + `File.Replace`). Sharing is **Export/Import**: a `.riv` zip of
`residence.json` plus every storey's underlay, and, for people without the app, the HTML report.
`persistentDataPath` derives from `productName`, which has changed twice (`CXRBrownfield`, then
`CXRHomeViz`), so `ResidenceStore.LegacyRoots` is an ordered **chain** of previous roots, newest
first, each pairing a product folder with the folder that sat inside it. `ResidenceStore`'s **static
constructor** runs `MigrateLegacyRoot` once: it moves the first root it finds and renames the inner
`homes/` to `residences/` (a failed move only warns). `ResidenceStore.Migrate` runs on
every load and import. Ported from the Site server: atomic write, version bump, name uniquing,
soft-delete.

## Units

Storage is **always metres**; display follows the units chip (default metres). Every conversion goes
through `Units.Format` / `Units.Parse`: an inline `* 3.28` is a bug, and so is a control printing its
own unit suffix instead of going through `MeasureUI`. The parser is forgiving (`12' 6"`, `12'6"`,
`6 1/2"`, `3.8m`, `380cm`). Every `DragNumber` is a parse site via `MeasureUI` with
`BareUnit.FollowDisplay`; `WallTool`'s run length and `UnderlayTool`'s calibration call `Units.TryParse`
directly.

## Tests

`Assets/Tests/EditMode/` (assembly `EditModeTests`), run through the Unity Test Runner or the MCP
`tests-run` tool. **`CXRAuthoring`** (`Assets/Scripts/Authoring/`) is the dependency-free, testable
assembly: that is why `SampleRefresh`, `Stories`, `ScrubMath`, `RoomFinish`, `RoomRegions` and the
`Sketch/` compiler live there. **`Assembly-CSharp`** (`ResidenceStore`, the tools, `ResidenceRenderer`,
`PdfRaster`) cannot be referenced by an asmdef and is verified by running the Editor.

Fixtures: `BrushGeometryTests`, `CustomItemsTests`, `DecorAlignmentTests`, `DecorPlacementTests`, `FenceBuilderTests`,
`FenceLinkerTests`, `ResidenceMetricsTests`, `LayoutConverterTests`, `OpeningFitTests`, `PathGeometryTests`,
`PathMeshTests`, `PlanBuilderTests`, `PolygonTriangulatorTests`, `RoomMeshBuilderTests`,
`SampleResidencesTests`, `StarterRoomTests`, `TileDeformTests`, `UnitsTests`, `VariantDiffTests`,
`WallLayoutTests`, `WallMeshBuilderTests`, `WallSnappingTests`. `SampleResidencesTests` runs every structural, placement and
occupancy check over all six samples, because the samples are data and data has no compiler.

## Repository layout

```
CLAUDE.md                 every-session notes: run, data model, decisions, where the rest lives
.claude/rules/            per-area rules, path-scoped; loaded when a matching file is read
README.md                 repo entry point
ApplicationSummary.md     the product one-pager
docs/SITE.md              legacy CXRSite developer notes
docs/SMARTHOME.md         every sensing device, cost and threshold, mapped to its report section
docs/design/              the design notes: the reasoning behind the rules, per area
Assets/                   the Unity project
    Editor/BuildMenu.cs   Build menu (ResidenceViz + Legacy Site)
    Editor/CatalogArtBinder.cs   catalog id → furniture-pack prefab; generates the wrappers
    Plugins/x86_64/pdfium.dll    the PDF rasterizer (BSD-3); see PdfRaster.cs
    Scripts/ResidenceViz/      ResidenceViz controllers, renderer, store, tools
    Scripts/Authoring/    CXRAuthoring asmdef: dependency-free geometry
    Scripts/              legacy Site runtime
    Scenes/ResidenceViz.unity  the app
    Tests/EditMode/       the EditMode tests
Packages/  ProjectSettings/
.claude/  .mcp.json       Claude Code config, rules, Unity MCP skills
```

Everything ignored by `.gitignore` (`Library/`, `Temp/`, `obj/`, `UserSettings/`, `*.csproj`, `*.sln`)
is regenerated by Unity and safe to delete.
