# CXRHomeViz — Developer Notes

**This repository is the Unity project.** Its root is the project root: `Assets/`, `Packages/`,
`ProjectSettings/` are directly here, and every path below is repo-relative. Open this folder in
Unity **6000.3.10f1**.

A stand-alone desktop tool for visualising proposed improvements to a home or apartment — widening a
doorway, adding grab bars, removing a threshold, rearranging a bedroom so a wheelchair can turn — and
holding "how it is now" next to "what we're proposing" so residents, families and care staff can
compare them in a meeting.

**It requires no Python, no server, and no network.** Homes are files under
`Application.persistentDataPath/CXRHomeViz/`.

> **Legacy — CXRBrownfield.** This project also still contains the original outdoor site-visioning
> tool: the `BasicModel` / `VRViewer` scenes, `EditController`, `WorldRenderer`, `TileBuildingEditor`,
> `LibraryClient`, `SyncClient`, `ModelRequester`. Nothing was deleted; it all still compiles and
> builds via **Build → Legacy — Brownfield**. Its developer notes are in
> [`docs/BROWNFIELD.md`](docs/BROWNFIELD.md), and **its Python backend now lives outside this
> repository** at `../CXRLayoutGen/` (Flask server on :5002, LLM layout generation, the JSON data
> store, sketch inputs, and the RI pilot study). Some of its code is genuinely shared —
> `WorldRenderer`, `PrefabRegistry`, `SiteDef`/`PathDef`/`FenceDef`, `EnvironmentScale` — because
> HomeViz's optional exterior layer renders through it. Do not "clean up" those.

## Running it

Open `Assets/Scenes/HomeViz.unity` and press Play. That is the whole setup. **The first launch seeds
six sample homes**, so it opens on something you can walk through rather than an empty library.

Build with **Build → HomeViz (PC, Windows)** (`Ctrl+Shift+H`) → `Builds/HomeViz/CXRHomeViz.exe`. That
build ships **only** the HomeViz scene, so the Brownfield stack is absent from it entirely.

## Sample homes

`SampleHomes` ships six complete, furnished, single-storey dwellings. They exist because the library
used to start empty and the only way in was the hardest step of the workflow — import a plan, calibrate
it, trace it — so nobody could see what the tool does without first doing that.

| | Apartments | Houses |
|---|---|---|
| Small | Studio, 38 m², 1 person | 2 bed / 1 bath, 90 m², 2 people |
| Medium | 2 bed / 1 bath, 74 m², 2–3 people | 3 bed / 2 bath, 125 m², 4 people |
| Large | 5 bed / 4 bath, 165 m², group home | 5 bed / 4 bath, 210 m², assisted living |

The two five-bedroom plans are the care settings the tool is aimed at: **every door 36" and
step-free**, roll-in showers, grab bars in every bathroom, handrails down a 1.6 m corridor (wide enough
for two wheelchairs to pass), and a fully accessible bedroom 1 with a hospital bed, lift and
wheelchair. Every bedroom and bathroom in those two clears a 1.5 m turning circle.

Each sample ships with **only the locked "Existing" baseline** — branching is what *Design options →
New proposal* is for, and *Unlock* is one click.

Two entry points: seeded on first run (guarded by `HomeSettings.samplesSeeded`, so archiving a sample
keeps it archived), and a **Sample homes** picker in the left rail that adds a fresh copy at any time.
Each install gets a new GUID and a uniqued name, so pulling the same sample twice is fine.

### Why there is a builder — `PlanBuilder`

**Nothing downstream of the schema complains about bad geometry.** `WallLayout` silently *clamps* an
opening that hangs off its wall, `WallMeshBuilder` leaves a ~57 mm notch wherever two wall endpoints
miss each other by more than 1 mm, and `HomeRenderer` skips an opening whose `wallId` does not resolve.
Six plans of raw coordinate literals would be thousands of unreviewable lines, wrong in ways nobody
would notice. So the plans are authored as **room rectangles** and everything error-prone is derived:

- **Walls** come from the room rects, grouped by line, then *unioned and re-split at every significant
  point*. One pass handles all three hazards: a shared edge collapses to **one** `WallDef`; rooms of
  different depth sharing a line resolve without overlap; and every T-junction and crossing is split so
  all endpoints coincide exactly, which is what `WallMeshBuilder.ComputeExtensions` needs to weld.
- **Openings** are placed by relationship — `DoorBetween("hall", "bed1", …)` finds the shared edge,
  centres the opening in it, resolves the post-split host wall, and asserts `OpeningFit.IsValid`.
- **Furniture** is placed with `Against(item, room, edge, fraction)`, which computes the flush position
  and the yaw facing into the room. `alongWall: true` gives the quarter turn a tub or shower needs —
  the catalog models both 0.76 × 1.52 (narrow front, deep), but both are installed as an alcove, and
  it is the only way either fits a 1.8 m bathroom.

Anything unresolved lands in `PlanBuilder.Warnings` rather than throwing, and the tests assert that
list is empty — which is what turns a silent geometry bug into a failing test.

**`SampleFurniture` mirrors the 35 `FurnitureCatalog` ids** because `FurnitureCatalog` is a
ScriptableObject in `Assembly-CSharp` and `CXRAuthoring` cannot reach it. That is the one unavoidable
duplication here; `SampleHomeInstaller.VerifyAgainstCatalog` compares the two on seed and warns on
drift. There is deliberately no `shower_seat` in any sample — these render as massing boxes, and a seat
inside a shower would be one box buried in another.

## Concepts

- **Home** (`HomeDoc`) — one dwelling. The unit of save/load, of the library list, and of export.
- **Variant** (`VariantDef`) — one design option. Every home has a **baseline** ("Existing", the record
  of how the home actually is, **locked by default**) plus any number of named proposals. Switching is
  a re-render, so it is instant. Comparing two variants produces a plain-English change list.
- **Level** (`LevelDef`) — one storey. Multi-storey is representable; this pass edits one at a time.
- **Underlay** (`UnderlayDef`) — the imported floor-plan sketch, **calibrated** so tracing is at true scale.

## Data model — `Assets/Scripts/Authoring/Interior/InteriorTypes.cs`

```
HomeDoc
 ├ underlay: UnderlayDef            the traced sketch + its metersPerPixel
 └ variants: [ VariantDef ]
      ├ levels: [ LevelDef ]
      │    ├ walls:       [ WallDef ]        centerline a→b + thickness + height
      │    ├ openings:    [ OpeningDef ]     offset along a wall, width, sill, swing, threshold
      │    ├ rooms:       [ RoomDef ]        floor polygon + finishes
      │    ├ furniture:   [ ObjectInstance ] ← REUSED from AuthoringTypes.cs
      │    └ wallMounted: [ WallMountDef ]   grab bars, cabinets; uses the DecorAlignment fields
      ├ exterior:        SiteDef             ← REUSED; null by default (see Exterior layer)
      └ exteriorObjects: [ ObjectInstance ]
```

Two reuses are load-bearing and should not be "cleaned up" into new types:

- **`ObjectInstance` is the furniture type.** Its `boxSizeMeters` field already means "this item's true
  size" — `LayoutConverter` has used it that way for generated massing boxes all along.
- **`SiteDef` is the exterior layer.** `PathDef` is a ramp or walkway, `FenceDef` a railing,
  `SurfaceStrokeDef` a patio. `WorldRenderer` already draws all of it.

## Walls, openings, and the two deliberate simplifications

`WallLayout.Build` decomposes a wall into the solid boxes that remain once its openings are removed:
full-height panels between them, a header above each, a sill below each window. **Openings are never
subtracted with CSG** — an opening is simply a gap the box list skips.

`WallMeshBuilder` closes corners by **extending each wall half a neighbour-thickness past any shared
endpoint, so the boxes overlap**, rather than mitering them. Invisible on opaque solids and robust at
every angle and valence. *Known limitation:* cutaway views, transparent walls, or geometry exported for
a contractor would reveal the overlap and would need real mitering.

## Workflow stages — `Assets/Scripts/HomeViz/HomeWorkflow.cs`

The command bar across the top selects a **stage**, and the right rail shows only that stage's tools.
This is HomeViz's answer to Brownfield's `UIShell` / `UIMode`, minus the machinery: Brownfield needs a
MonoBehaviour and a `Changed` event because five panels each read the mode from their own `OnGUI`,
whereas `HomeEditController` owns the whole UI, so the stage is a field on it and `HomeWorkflow` is
just the table.

| Stage | Tools | |
|---|---|---|
| **Sketch** | Select, Sketch | Import a plan and calibrate it. Nothing else can be at true scale until this is done. |
| **Draw** | Select, Walls, Doors & windows, Rooms | The shell of the dwelling. |
| **Furnish** | Select, Furniture | Including wall-mounted grab bars and cabinets. |
| **Review** | Select, Measure | Opens with the **Design options** panel expanded — comparing is the work here. |
| **Outdoors** | Select, Outdoors | **Absent from the command bar unless `HomeDoc.exteriorEnabled`.** |

**Select is in every stage** — it is the pointer, not a phase. Every stage opens on the tool it exists
for, except Review, which opens on Select.

Below the active tool the rail ends in two foldouts, both collapsed: **Design options** (the variant
list + change list, auto-opened in Review) and **Outdoors (optional)** (just the exterior on/off
toggle). Everything not belonging to the current stage is one click away, not on screen.

## Tools — `Assets/Scripts/HomeViz/Tools/`

Each tool is one file implementing `IHomeTool`, registered with one line in
`HomeEditController.Awake` and placed in a stage by `HomeWorkflow`. **Digits 1–N pick a tool within
the active stage; Ctrl+1–5 switch stage.** (The old flat 1–6 indexed the registry directly, which
left the seventh tool unreachable from the keyboard.)

| Tool | What it does |
|---|---|
| **Select** | Pick a wall / door / room / item. The inspector shows the numbers that matter: a door's **clear width** (and whether that was measured or estimated), whether it is step-free, a room's area and the **largest turning circle that fits inside it**. |
| **Walls** | Click a chain of corners. Live **length + angle readout**, and you can **type a length** (`12' 6"`) mid-run to place an endpoint exactly. Snaps to endpoints → wall centerlines → 45° axes → grid, in that order. **Hold Shift to suspend all snapping** (the fence-tool convention). |
| **Doors & windows** | Inch presets (28/30/32/34/36") because that is how doors are specified. Shows the resulting **clear passage** live while you choose. Every placement runs through `OpeningFit`, which slides to the nearest legal spot rather than refusing. |
| **Rooms** | Draw a floor polygon with a live area readout. Ear-clipping triangulation handles concave (L-shaped) rooms. |
| **Furniture** | Catalog picker with true dimensions on every row. Q/E rotate 90°, Shift+scroll 15°. Wall-mounted items snap to the wall face you hover. |
| **Measure** | Point-to-point with per-leg and running totals, plus turning space per room. |
| **Sketch** | Import a plan photo, then **calibrate**: click two points, type the real distance. Everything traced afterwards is at true scale. |
| **Outdoors** | Draw a **walkway** or **entry ramp** (`PathDef`) or a **railing** (`FenceDef`) around the home, using the wall tool's gesture and snapping so a ramp lands on the corner it serves. The ramp sub-mode shows a live **1:12 slope check** against the rise you enter. Only reachable from the Outdoors stage — see the exterior layer below. |

## View modes

| Mode | Behaviour |
|---|---|
| **Plan** | Orthographic top-down, ceilings hidden. The tracing and measuring view. |
| **Dollhouse** | Perspective orbit, ceilings hidden. Right-drag orbit, middle-drag pan, scroll zoom, WASD — identical to the Brownfield camera. |
| **Walkthrough** | First person with collision. **Standing 1.60 m / Seated 1.19 m toggle** — the seated setting shows a wheelchair user's actual sightline over counters and through windows. |

## Furniture catalog — `Assets/Resources/FurnitureCatalog.asset`

35 items with real dimensions (wheelchair 0.66 × 1.22 m, twin bed 0.99 × 2.03 m, toilet 0.51 × 0.71 m…).

**There is no interior art in this project** — every existing asset pack is exterior. So the renderer
resolves each catalog id against `PrefabRegistry`: a prefab if one exists under that key, otherwise a
**correctly sized labeled box**. Adding real models later is a `PrefabRegistry` edit — no code change,
no schema change, no data migration, because instances only ever store the key.

## Optional exterior layer (off by default)

`HomeDoc.exteriorEnabled` is `false` and `VariantDef.exterior` is `null` until switched on in the
**Outdoors (optional)** foldout at the bottom of the right rail. That one toggle is the entire outdoor
surface area of the UI until it is on; turning it on adds the **Outdoors stage** to the command bar,
and turning it off removes every outdoor control again.

When on, `ExteriorBridge` wraps the variant's `SiteDef` as an `EnvironmentDef` and hands it to the
existing `WorldRenderer` — so ramps, walkways, railings and patios render through code that already
works. The `Exterior` subtree in the scene (a 60 × 60 m Terrain + a `WorldRenderer`) is wired but
`SetActive(false)`.

`OutdoorTool` is what authors that data — before it existed, nothing in HomeViz could write a
`PathDef` or `FenceDef`, so `ExteriorBridge.HasContent` was permanently false and the toggle rendered
nothing no matter what. Two conditions still gate the render, deliberately: the home must have opted
in **and** something must have been drawn, otherwise an enabled-but-empty exterior would swap the tidy
indoor ground pad for a bare 60 m terrain. `HomeRenderer` hands the ground over between `GroundPad`
and the exterior `Terrain` so exactly one is ever active.

`WorldRenderer`'s tile-building fields (`tileShapePalette`, `materialPalette`, `buildingGenerator`) are
**deliberately unassigned** in `HomeViz.unity`: `ExteriorBridge` always passes an empty
`buildingInstances` list — the house is `HomeRenderer`'s job — so that branch is unreachable here.
`pathMaterialPalette`, `fencePalette`, `terrainRegistry` and `prefabRegistry` must stay wired;
`OutdoorTool` reads the first two to offer only surfaces and railing types that will actually render.

The remaining Brownfield terrain tools (ground painting, object scatter, lot editing) are still only in
`EditController`. `IHomeTool` is the landing pad if they are ever wanted here.

## Rules-ready, but no rules

`ClearanceRules.Registry` ships **empty**. What exists is the data a rule would need:
`OpeningDef.clearWidth`, `OpeningDef.thresholdHeight`, `FurnitureCatalog.Entry.clearanceFront/Side`,
`ObjectInstance.boxSizeMeters`, and `HomeMetrics.LargestInscribedCircle` (turning space). Adding a rule
later is writing a comparison, not inventing geometry.

## Storage

```
<persistentDataPath>/CXRHomeViz/
    homes/<id>.json              one HomeDoc per file
    homes/_archive/<id>.json     soft-deleted; nothing is ever destroyed
    underlays/<id>/<image>       the traced sketch
    settings.json
```

Writes are atomic (temp file + `File.Replace`). Sharing is **Export/Import**: a single `.homeviz`
archive holding the home plus its underlay, which replaces the server as the way a home moves between
machines.

Ported from the Brownfield server (`../CXRLayoutGen/server/server.py`): atomic write, version bump,
name uniquing, soft-delete. **Deliberately not ported:** the content-hash dedup (~115 lines), which
existed only because layout generation re-POSTed identical `BuildingDef`s — with generation out of
scope there is no duplicate source. Also not ported: `/api/active` multi-client sync (VR is out of
scope for this pass).

## Units

Storage is **always meters**. Display defaults to **feet-and-inches**. Every conversion goes through
`Units.Format` / `Units.Parse` — an inline `* 3.28` anywhere else is a bug. The parser is deliberately
forgiving (`12' 6"`, `12'6"`, `12' 6`, `6 1/2"`, `3.8m`, `380cm`) because the calibration prompt is the
one field that gates the entire workflow.

## Tests

`Assets/Tests/EditMode/` — 305 EditMode tests pass, covering both apps. The HomeViz geometry lives in
the dependency-free `CXRAuthoring` assembly and is fully covered: `WallLayoutTests`,
`WallMeshBuilderTests`, `PolygonTriangulatorTests`, `RoomMeshBuilderTests`, `WallSnappingTests`,
`OpeningFitTests`, `HomeMetricsTests`, `VariantDiffTests`, `UnitsTests`.

`PlanBuilderTests` pins the three wall derivations (shared edges collapse, partial overlaps resolve,
T-junctions split). `SampleHomesTests` runs every check over all six samples, because the samples are
data and data has no compiler: no builder warnings, ids unique across **all** element types (
`HomeRenderer.Mark` uses one flat dictionary, so a wall colliding with a chair breaks selection), every
opening `IsValid` on a resolvable wall, no surviving T-junction or wall overlap, rooms tiling the
footprint and matching the advertised bedroom/bathroom counts, every furniture footprint inside its room
and clear of every other, and the accessibility floor for the two care plans.

`HomeStore` and the tools live in `Assembly-CSharp`, which asmdefs cannot reference, so they are
verified by driving the real filesystem and the real renderer rather than by unit test. For the samples
that meant: rendering all six through the scene's real `HomeRenderer` (wall/floor/furniture/mount
counts and bounds all correct, everything a labeled box as expected), round-tripping two through
`HomeStore.Save`/`Load` (including the `float[][]` polygons that are the reason this schema uses
Newtonsoft rather than `JsonUtility`), and branching a proposal to confirm `VariantDiff` reports the
change list in feet and inches.

## Repository layout

```
CLAUDE.md                 these notes
README.md                 repo entry point
ApplicationSummary.md     the product one-pager
docs/BROWNFIELD.md        legacy CXRBrownfield developer notes
Assets/                   the Unity project
    Editor/BuildMenu.cs   Build menu (HomeViz + Legacy — Brownfield)
    Scripts/HomeViz/      HomeViz controllers, renderer, store, tools
    Scripts/Authoring/    CXRAuthoring asmdef — dependency-free geometry
    Scripts/              legacy Brownfield runtime
    Scenes/HomeViz.unity  the app
    Tests/EditMode/       305 tests
Packages/  ProjectSettings/
.claude/  .mcp.json       Claude Code config + Unity MCP skills
```

Everything ignored by `.gitignore` (`Library/`, `Temp/`, `obj/`, `UserSettings/`, `*.csproj`, `*.sln`)
is regenerated by Unity and safe to delete.
