# CXRHomeViz. Application Summary

## What it is

A desktop application for **visualizing proposed changes to a home or apartment at true scale, and
holding "how it is now" next to "what we're proposing"** so the two can be compared in a meeting.

## Who it is for

Residents, families, and care staff in **shared homes and assisted living**, together with the
occupational therapists, care coordinators, and designers who work with them. The tool is built for
the conversation that happens *before* anyone commits to a renovation: someone needs to see whether
the change actually solves the problem.

It is not a CAD tool and not for contractors. It trades construction-grade precision for being
usable, in a room, by people who are not designers.

## The question it answers

A floor plan cannot tell you:

- How much **clear width** a doorway really has once the door is hung and open, and whether that was
  measured or estimated from the leaf size.
- Whether a **60" turning circle** actually fits beside the bed, or in the bathroom, or in the space
  between the counter and the island.
- Whether a threshold makes an otherwise fine doorway **not step-free**.
- What someone **seated in a wheelchair** can see over a counter or through a window: the
  walkthrough has a standing 1.60 m / seated 1.19 m eye-height toggle for exactly this.
- Whether a **smart home package would actually cover this home**: which way out nothing is watching,
  which bedroom would notice a fall, what the whole thing costs, and what a caregiver's phone would
  have shown at three in the morning.
- What the **rest of living there** costs and who it is for: the grab bar, the rocker knife, the sock
  aid. Held in the same list, priced in the same total, and marked as retail figures rather than
  report ones.

## How it works

1. **Import and calibrate.** Bring in a photo or scan of a floor plan, click two points, and type the
   real distance between them. Everything traced afterwards is at true scale.
2. **Trace.** Walls with a live length and angle readout (and typed lengths like `12' 6"`), doors and
   windows in the inch presets doors are actually specified in, rooms as floor polygons.
3. **Furnish.** A 35-item catalog at real dimensions, plus wall-mounted items (grab bars, cabinets) 
   that snap to the wall face you point at.
4. **Smart living.** A 25-item catalog of what gets installed rather than furnished, in three parts.
   **Sixteen sensing devices**. Door and stove sensors, pressure pads, motion sensors, pendants, a
   hub: every one taken, with its real cost and vendors, from the Center for Family Support's
   technology assessment (`docs/SMARTHOME.md` maps each back to its section). **Nine everyday aids**
   the report does not cover: a rocker knife, a sock aid, a button hook, a key turner, stabilising
   cutlery, a high-contrast measuring set, a touch-free bin, a smart bulb. Priced at typical retail
   and saying so wherever the figure appears, because what decides whether someone lives independently
   is often not sensing at all. And **grab bars and handrails**, offered here as well as in Furnish.
   Everything installs **on the element it belongs to**: an opening, a bed, a room, a wall, or a
   resident, so widening a doorway later carries its sensor with it, and a sock aid follows the person
   rather than sitting somewhere in the plan. The plan draws what each device can see, and calls out
   the way out that nothing is watching.
5. **Watch the day.** The household's schedule already places everyone minute by minute, so the
   sensors are simulated rather than scripted: play the day and watch devices light up, alerts land on
   the timeline, and a mock caregiver console fill with the report's own wording: *"Bernard left bed
   at 3:10 AM and has not returned."* Switching the console's role to **Family** or **Resident** shows
   exactly what each person would and would not be able to see.
6. **Branch and compare.** Every home has a locked **"Existing"** baseline recording how the home
   actually is. Design options branch off it. Comparing two produces a plain-English change list:

   > • Changed Bathroom door: width 2' 8" → 3', clear width 2' 5 5/8" → 2' 9 5/8", threshold removed (step-free)
   > • Added Grab bar 24: at 2' 9 1/8" AFF
   > • Added Bed / chair pad. Twin bed in Bedroom 2: $30-$200

7. **Walk it.** Overview (perspective free-look, drawing, measuring, and looking straight down for
   a plan) and Walkthrough (first person, with collision and the seated/standing toggle).
8. **Hand something over.** A before/after report: a self-contained HTML file that prints to PDF from
   any browser: with each changed room photographed twice from one camera pose, the measured claims
   side by side, and a technology section carrying the devices, the coverage, the cost and the
   scenarios the package would have caught.

## Running and shipping it

**No Python, no server, and it works offline**: with one opt-in exception, *Read the plan*, which
sends a single sketch image to the Anthropic API and returns a floor plan. Open the repository folder in Unity 6000.3.10f1, open
`Assets/Scenes/HomeViz.unity`, and press Play. First launch seeds six complete sample homes so the
tool opens on something walkable rather than an empty library. **The two five-bedroom care homes ship
a costed smart home proposal beside their baseline**, so Compare tells that story without anyone
having to build it first.

Ship with **Build → HomeViz (PC, Windows)** → `Builds/HomeViz/CXRHomeViz.exe`.

Homes are plain JSON files under `Application.persistentDataPath/CXRHomeViz/`, written atomically and
soft-deleted rather than destroyed. Sharing a home between machines is Export/Import of a single
`.homeviz` archive holding the home plus its underlay image.

## Deliberately out of scope

- **No server and no accounts.** Storage is local files; sharing is a file you send someone.
- **No layout generation.** Homes are traced by a person over a calibrated plan, not inferred by a
  model. (The sketch→layout pipeline belongs to the legacy Site tool and now lives outside
  this repository.)
- **No VR.** The HomeViz build never initializes XR.
- **No accessibility rules engine, yet.** `ClearanceRules.Registry` ships empty *on purpose*. Every
  input a rule would need is already measured and stored: clear width, threshold height, per-item
  front and side clearances, true footprints, and the largest inscribed circle per room. Adding a
  code check later is writing a comparison, not inventing the geometry.
- **No construction-grade geometry.** Wall corners are closed by overlapping the boxes rather than
  mitering them. Invisible on opaque walls, but cutaway views or geometry exported for a contractor
  would show it.
- **No live sensor data, and no claim to predict a household.** The smart home layer models what
  *would* be installed and simulates a day from the household's own schedule; nothing talks to a real
  device. The demonstration day acts out the report's scenarios at once to show what a package
  catches, and the app labels it as such: the cost offset is quoted at a stated incidents-per-week
  assumption rather than derived from it, and the report's per-device vendor savings claims are
  reproduced nowhere.

## Also in this repository

The legacy **CXRSite** outdoor site-visioning tool shares this Unity project. It is not part of
the HomeViz build. See [docs/SITE.md](docs/SITE.md); its Python backend lives outside the
repository at `../CXRLayoutGen/`.

`Assets/Resources/SmartHomeTechnology/SmartHomeReport.pdf` is the source for the sensing half of that
layer: *Smart Home Technology Solutions for Individuals with Intellectual and Developmental
Disabilities*, Olzhas Yessenbayev, Cornell Tech / Center for Family Support, July 2025.
[docs/SMARTHOME.md](docs/SMARTHOME.md) maps every device, cost and threshold back to its section.
