# Residence Improvement Visualizer

Residence Improvement Visualizer is a Unity desktop application for drawing a residence to scale and testing changes to it
before anyone builds. You import a photo or scan of a floor plan, calibrate it by clicking two points
and typing the real distance between them, then trace walls, doors, windows and rooms on top of it.
From there you furnish the space from a catalog of real-dimension items and place smart home devices
on the elements they watch.

Every residence keeps a locked baseline of how it stands today. Design options branch off that baseline,
and comparing any two produces a plain-English list of what changed, down to clear widths and
threshold heights. It is built for residents, families and care staff in shared homes and assisted
living, along with the occupational therapists and coordinators who work with them.

Some things a plan view will not tell you. How much clear width a doorway really has once the door is
hung and open. Whether a wheelchair turning circle fits beside the bed. Whether a threshold stops an
otherwise fine doorway from being step-free. What someone seated can see over a counter.

## Quick start

Open this folder in Unity 6000.3.10f1, open `Assets/Scenes/ResidenceViz.unity`, and press Play. First
launch seeds sample residences, so there is something to walk through straight away. To ship a build, use
Build → ResidenceViz (PC, Windows) (`Ctrl+Shift+H`), which writes `Builds/ResidenceViz/ResidenceImprovementVisualizer.exe`. That
build carries the ResidenceViz scene alone.

There is nothing to install, no server to start, and no account. Residences stay on the machine. One
feature reaches the network and it is opt-in per press. "Read the plan", in the Import rail, sends a
single sketch image to the Anthropic API and gets a floor plan back, and it runs only after you have
entered your own API key and pressed the button.

## What it does

### Importing a plan

Bring in a photo, a scan, or a PDF. Calibrate it by clicking two points and typing the distance
between them; everything traced afterwards sits at true scale. A multi-page PDF can put one page on
each storey. Residences with more than one floor get a floor chip in the top bar, and one storey is edited
and rendered at a time.

### Tracing the shell

Walls go down as a chain of corners with a live length and angle readout, and you can type a run
length mid-draw. Endpoints snap square onto a crossed wall, then to centerlines, level with a
parallel wall's end, to 45°, and finally to the grid. Hold Shift to draw free. Doors and windows take
width, height and sill as free numbers, with the resulting clear passage shown as you drag. Rooms are
floor polygons derived from the wall graph. You say what each one is, and the floor finish follows
from the type.

Storage is always in metres. Display follows the units chip, and the parser is forgiving about how
you type a measurement (`12' 6"`, `6 1/2"`, `380cm`).

### Furnishing

The catalog holds beds, seating, kitchen and bathroom fixtures, and mobility equipment, each at its
real footprint. Tiles in the picker draw that footprint against one fixed reference, so a hospital
bed looks like a hospital bed sitting next to a nightstand. Grab bars, handrails and wall cabinets
snap to the wall face you point at. Placement runs through a fit pass that keeps items clear of
doorways and out of walls.

### Smart living

Sensing devices and everyday aids share one catalog: door and stove sensors, pressure pads, motion
sensors, pendants and a hub, alongside things like a rocker knife or a sock aid. Each device installs
on the element it belongs to. An opening, a bed, a room, a wall, a resident. Widen that doorway later
and its sensor comes with it; a worn device follows the person rather than sitting somewhere on the
plan. The plan view draws what each device can see and calls out any way out that nothing is
watching. The Monitor console plays the household's own schedule forward, lighting devices up as
people move and filing alerts in the wording a caregiver would read. Switch its role between staff,
family and resident to see what each of them has access to. Every device cost and threshold traces
back to its source in [docs/SMARTHOME.md](docs/SMARTHOME.md).

### Reviewing and comparing

Measure point to point with a running total, and read the largest turning circle that fits in each
room. Compare holds a proposal against any other variant and groups the differences by room, with
markers in the plan. Clicking a row selects and focuses that element. Each row also carries a revert,
which undoes that one change and leaves the rest of the proposal alone.

### Two ways to look at it

Overview is a perspective free-look with ceilings hidden. It is the drawing and measuring view, and
looking straight down gives you a plan. Walkthrough is first person with collision, and it carries
the eye-height toggle: 1.60 m standing, 1.19 m for a wheelchair user.

### Outdoors

Off by default. Switch it on to draw entry ramps with a live 1:12 slope check, walkways, railings and
patios around the residence.

### Handing something over

Generate report writes a self-contained HTML file that prints to PDF from any browser. Each changed
room is photographed twice from one camera pose, with the measured claims side by side and a section
covering the technology, its coverage and its cost.

## Storage and sharing

Residences are plain JSON files under `Application.persistentDataPath/ResidenceImprovementVisualizer/`, one file per residence,
written atomically. Deleting a residence moves it into an archive folder rather than destroying it.
Underlay images live beside them, one per storey, and reports land in their own folder. Sharing
between machines is Export and Import of a single `.riv` archive holding the residence and its
underlays. For anyone without the app, the HTML report is the thing you send.

---

Product overview in [ApplicationSummary.md](ApplicationSummary.md); developer notes in
[CLAUDE.md](CLAUDE.md), with the per-area rules in [`.claude/rules/`](.claude/rules/) and the
reasoning behind them in [docs/design/](docs/design/). The legacy CXRSite outdoor site-visioning tool
shares this Unity project, is absent from the ResidenceViz build, and is documented in
[docs/SITE.md](docs/SITE.md).
