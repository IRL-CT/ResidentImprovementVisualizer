# CXRHomeViz — home & apartment interior visioning

A stand-alone desktop tool for helping residents, families, and care staff in **group homes and
assisted living** visualize proposed improvements to a home: widening a doorway, adding grab bars,
removing a threshold, rearranging a bedroom so a wheelchair can turn.

Import a photo of a floor plan, calibrate it against one known dimension, and trace it — walls,
doors, rooms, furniture, all at true size. Then create named proposals off the as-built baseline and
compare them, in 3D and as a plain-English change list:

> • Changed Bathroom door: width 2' 8" → 3', clear width 2' 5 5/8" → 2' 9 5/8", threshold removed (step-free)
> • Added Grab bar 24: at 2' 9 1/8" AFF

It answers questions a plan view cannot: how much clear width that doorway really has once the door
is open, whether a 60" turning circle fits beside the bed, and — via the seated eye-height toggle in
the walkthrough — what someone in a wheelchair actually sees over the counter.

## Quick start

**This repository is the Unity project.** There is nothing to install, no server to start, and no
network required. Homes are local files under `Application.persistentDataPath/CXRHomeViz/`.

1. Open this folder in Unity **6000.3.10f1**.
2. Open `Assets/Scenes/HomeViz.unity` and press **Play**.

First launch seeds **six sample homes** — a studio, two- and three-bedroom apartments and houses, and
two five-bedroom care settings (a group home and an assisted-living unit) — so you land on something
walkable instead of an empty library.

To ship a build: **Build → HomeViz (PC, Windows)** (`Ctrl+Shift+H`) → `Builds/HomeViz/CXRHomeViz.exe`.

See [ApplicationSummary.md](ApplicationSummary.md) for the product-level overview, and
[CLAUDE.md](CLAUDE.md) for the full developer notes.

## What it does

| | |
|---|---|
| **Sketch** | Import a plan photo and calibrate it — click two points, type the real distance. Everything traced afterwards is at true scale. |
| **Draw** | Walls with a live length + angle readout and typed lengths (`12' 6"`). Doors and windows in inch presets, showing the resulting clear passage as you choose. Rooms as floor polygons with a live area readout. |
| **Furnish** | A 35-item catalog at real dimensions — wheelchair 0.66 × 1.22 m, twin bed 0.99 × 2.03 m, toilet 0.51 × 0.71 m — plus wall-mounted grab bars and cabinets that snap to the wall face you hover. |
| **Review** | Measure point-to-point, read the largest turning circle that fits in each room, and diff any two design options into a change list in feet and inches. |
| **Outdoors** *(optional)* | Off by default. Switch it on to draw entry ramps (with a live 1:12 slope check), walkways, railings, and patios around the home. |

Three view modes: **Plan** (orthographic, for tracing and measuring), **Dollhouse** (perspective
orbit), and **Walkthrough** (first person, with a **standing 1.60 m / seated 1.19 m** eye-height
toggle).

Every home carries a locked **"Existing"** baseline — the record of how the home actually is — plus
any number of named proposals branched off it. Switching between them is a re-render, so it is
instant. Sharing is Export/Import of a single `.homeviz` archive holding the home and its underlay.

## Repository layout

```
CLAUDE.md                 developer notes
ApplicationSummary.md     product one-pager
docs/BROWNFIELD.md        legacy CXRBrownfield notes
Assets/
    Scenes/HomeViz.unity  the app
    Scripts/HomeViz/      controllers, renderer, store, tools
    Scripts/Authoring/    CXRAuthoring asmdef — dependency-free geometry
    Scripts/              legacy Brownfield runtime
    Tests/EditMode/       305 tests
Packages/  ProjectSettings/
```

## Legacy — CXRBrownfield

This Unity project also still contains the original **outdoor site-visioning** tool: turn a rough,
hand-drawn, top-down site sketch into an editable, walkable 3D environment. Upload an image of a site
plan; a large language model interprets it into a structured scene description; Unity renders that as
terrain, paths, buildings, and props; then you edit, refine, save, and "bake" the result into an
optimized scene for VR or lightweight playback.

Nothing was deleted — the `BasicModel` and `VRViewer` scenes, `EditController`, `WorldRenderer`,
`TileBuildingEditor`, `LibraryClient` and `SyncClient` all still compile and build via
**Build → Legacy — Brownfield**.

**Its Python backend now lives outside this repository**, at [`../CXRLayoutGen/`](../CXRLayoutGen/) —
the Flask server on :5002, the LLM layout generation, the JSON data store, the sketch inputs, and the
RI pilot study. To run Brownfield: start `python server/server.py` from there, then open
`Assets/Scenes/BasicModel.unity`.

Developer notes: [docs/BROWNFIELD.md](docs/BROWNFIELD.md). Backend setup:
`../CXRLayoutGen/README.md`.

## Key technologies

- **Unity 6000.3.10f1 (C#)**, Input System, Newtonsoft JSON
- `CXRAuthoring` — a dependency-free assembly holding all the geometry (wall layout, mesh building,
  triangulation, snapping, opening fit, metrics, unit parsing), so it is unit-testable without Unity
- **OpenXR + XR Interaction Toolkit** — the legacy VR viewer only
