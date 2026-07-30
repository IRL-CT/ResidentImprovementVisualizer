# CXRHomeViz — Application Summary

## What it is

A desktop application for **visualizing proposed changes to a home or apartment at true scale, and
holding "how it is now" next to "what we're proposing"** so the two can be compared in a meeting.

## Who it is for

Residents, families, and care staff in **group homes and assisted living**, together with the
occupational therapists, care coordinators, and designers who work with them. The tool is built for
the conversation that happens *before* anyone commits to a renovation: someone needs to see whether
the change actually solves the problem.

It is not a CAD tool and not for contractors. It trades construction-grade precision for being
usable, in a room, by people who are not designers.

## The question it answers

A floor plan cannot tell you:

- How much **clear width** a doorway really has once the door is hung and open — and whether that was
  measured or estimated from the leaf size.
- Whether a **60" turning circle** actually fits beside the bed, or in the bathroom, or in the space
  between the counter and the island.
- Whether a threshold makes an otherwise fine doorway **not step-free**.
- What someone **seated in a wheelchair** can see over a counter or through a window — the
  walkthrough has a standing 1.60 m / seated 1.19 m eye-height toggle for exactly this.

## How it works

1. **Import and calibrate.** Bring in a photo or scan of a floor plan, click two points, and type the
   real distance between them. Everything traced afterwards is at true scale.
2. **Trace.** Walls with a live length and angle readout (and typed lengths like `12' 6"`), doors and
   windows in the inch presets doors are actually specified in, rooms as floor polygons.
3. **Furnish.** A 35-item catalog at real dimensions, plus wall-mounted items — grab bars, cabinets —
   that snap to the wall face you point at.
4. **Branch and compare.** Every home has a locked **"Existing"** baseline recording how the home
   actually is. Design options branch off it. Comparing two produces a plain-English change list:

   > • Changed Bathroom door: width 2' 8" → 3', clear width 2' 5 5/8" → 2' 9 5/8", threshold removed (step-free)
   > • Added Grab bar 24: at 2' 9 1/8" AFF

5. **Walk it.** Plan (orthographic, for tracing and measuring), Dollhouse (perspective orbit), and
   Walkthrough (first person, with collision and the seated/standing toggle).

## Running and shipping it

**No Python, no server, no network.** Open the repository folder in Unity 6000.3.10f1, open
`Assets/Scenes/HomeViz.unity`, and press Play. First launch seeds six complete sample homes so the
tool opens on something walkable rather than an empty library.

Ship with **Build → HomeViz (PC, Windows)** → `Builds/HomeViz/CXRHomeViz.exe`.

Homes are plain JSON files under `Application.persistentDataPath/CXRHomeViz/`, written atomically and
soft-deleted rather than destroyed. Sharing a home between machines is Export/Import of a single
`.homeviz` archive holding the home plus its underlay image.

## Deliberately out of scope

- **No server and no accounts.** Storage is local files; sharing is a file you send someone.
- **No layout generation.** Homes are traced by a person over a calibrated plan, not inferred by a
  model. (The sketch→layout pipeline belongs to the legacy Brownfield tool and now lives outside
  this repository.)
- **No VR.** The HomeViz build never initializes XR.
- **No accessibility rules engine — yet.** `ClearanceRules.Registry` ships empty *on purpose*. Every
  input a rule would need is already measured and stored: clear width, threshold height, per-item
  front and side clearances, true footprints, and the largest inscribed circle per room. Adding a
  code check later is writing a comparison, not inventing the geometry.
- **No construction-grade geometry.** Wall corners are closed by overlapping the boxes rather than
  mitering them — invisible on opaque walls, but cutaway views or geometry exported for a contractor
  would show it.

## Also in this repository

The legacy **CXRBrownfield** outdoor site-visioning tool shares this Unity project. It is not part of
the HomeViz build. See [docs/BROWNFIELD.md](docs/BROWNFIELD.md); its Python backend lives outside the
repository at `../CXRLayoutGen/`.
