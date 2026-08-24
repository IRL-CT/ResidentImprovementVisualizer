# Design notes: the Smart living layer

> Everything about sensing devices and everyday aids: naming, the picker, hosting on elements, the
> simulated day, coverage, cost, the monitor console, `SensorFit`, and the two samples that ship a
> proposal. The figures themselves are mapped to the Cornell Tech report in
> [SMARTHOME.md](../SMARTHOME.md). The rules are summarised in
> [`.claude/rules/smart-living.md`](../../.claude/rules/smart-living.md); the reasoning lives here.

## The Smart living layer: `SensorTypes.cs` and friends

Every **sensing** device, threshold, cost and coverage figure here is derived from
`Assets/Resources/SmartHomeTechnology/SmartHomeReport.pdf` (Cornell Tech / Center for Family Support,
July 2025). [`docs/SMARTHOME.md`](../SMARTHOME.md) is the map back, section by section, and it is
not decoration: these numbers are read off the screen in a funding meeting, so an unattributable
figure is worse than no figure.

### The stage is not called Sense, and nothing on disk was renamed

The catalog holds **25** things, not 16. Nine of them are **Everyday living**: a rocker knife, a sock
aid, a key turner, a touch-free bin, a smart bulb, and the report does not mention one of them. They
are here because what decides whether someone lives independently is often not sensing at all, which
is the same argument the report makes about a stove sensor reached from the other end.

So the stage is **Smart living** and the tool is **Equipment**. `HomeStage.Sense` became
`HomeStage.SmartLiving`, and `HomeWorkflow.Label` now splits the enum member at each capital rather
than gaining a second table of display names: *the enum member IS the tab label* survives, and every
other member is one word so nothing else moves. (It is 12 characters against `Structure`'s 9, so it is
now the first label `UITheme.FitAll` shortens; `StageTips` already leads a shortened tab's tooltip
with the full name.)

**`LevelDef.sensors`, `SensorDef`, `SensorCatalog.asset`, the tool id `"sensor"` and the thirteen
`Sensor*.cs` files all keep their names**, on the Sketch → Import precedent: *the word survives
everywhere it is still accurate*. Renaming the field would make Newtonsoft drop the whole layer out of
every home already saved, renaming the asset moves a GUID reference in `HomeViz.unity`, and renaming
the tool id breaks `HomeWorkflow.StageOf` and every `RequestTool` call. None of it for anything a user
can see. What `SensorDef` has to be right about is the SHAPE of the record: host, cost, privacy,
coverage, rules. **An everyday aid is that record with no envelope and no rules**, and that is the
whole of why nine new rows cost almost nothing.

Being inert is not luck, it is the two zeroes. `coverageRadius = 0` and no `DefaultRules` case means
`SensorOverlay` skips them (it returns on a zero radius), `SensorSim`'s per-device dispatch emits
nothing, `SensorRules` sees no events and `SensorCoverage` counts nothing, so **`SensorSim`,
`SensorRules`, `SensorCoverage` and `SensorPose` needed no change at all**, and no figure this app
prints about what a home can see moves when an aid is installed. Two invariants hold over all six samples: an everyday item changes no coverage figure and raises no
gap, and added to a full package it still raises nothing.

Three things that were NOT free, each because they had quietly assumed every device senses:

- **`speculative` needed `provenance` beside it.** That flag meant *"the report names this class of
  device without pricing a product"*, which is true of the Emerging four and false of these: the
  report does not name them at all. Both need a warning and they need different sentences, so the
  sentence is data. Empty means *from the report*, and `SensorCatalog.Entry.Attribution` is the one
  place that decides, which also stops the picker printing the literal `Report §.` for an entry with
  no section number, as it did the moment the catalog outgrew its own tooltip.
- **`SensorPrivacy.None`**: "Not connected". `Passive` means *notices a state*, and a sock aid notices
  nothing; saying "Senses a condition" on its badge would be a small lie in the one place the console's
  dignity argument is actually made. It ranks 0 **tied with `Passive`**, deliberately: the role filters
  compare with `<=`, so an unpowered aid is shown to a family member exactly as a water sensor is. The
  tiers differ in what the badge SAYS, not in who may see it.
- **The duplicate guard is scoped to alerts.** Two door sensors on one door double every alert that
  door raises; two sock aids for one resident are two sock aids. `SensorTool.RaisesAlerts` asks the
  catalog's own coverage and rules rather than a category string, so an aid later given a threshold
  starts being treated as a device with no second place to update.

### The picker's tile had one mode and needed three

`SensorTool.Reach` drew a coverage disc, arguing that *"every device in this catalog is a small grey
box, so a grid of footprints would be sixteen identical dots: what distinguishes them is reach"*. The
first half was true; the second was true of **five** of the sixteen. The other eleven fell through to
the same 5×5 dot the argument was made against: `FurnitureTool`'s own remarks block exists to prevent
exactly that, and it had been sitting in this file unnoticed. Nine everyday rows would have made it
twenty.

So: **reach** at the 9.6 m span for the five that have any; **footprint** at a 0.6 m span for anything
else installed (the biggest is a 0.51 m bed pad, and at 9.6 m every one of them rounds to a dot);
**the entry's own colour** for anything worn, because they differ from each other by a centimetre or
two (a 0.01 m zipper pull against a 0.30 m sock aid) so against one span every one of them rounds to
the same handful of pixels, which is the identical-dot failure the three modes exist to avoid. Each
everyday entry therefore carries its own swatch, which the sensor catalog allows and
`FurnitureCatalog`'s per-category colouring would not. (The original argument was that a worn item is
never drawn in the plan at all; half of that stopped being true when a personal item became placeable
without a person, and the half above is the half that decides.)

### A personal item no longer needs a person

A pendant belongs to a resident and a key turner lives in a pocket, so both hang off `OccupantDef`,
and while that was the **only** answer, eight of the twenty-five entries were unplaceable in a home
with an empty roster. The rail refused them with a warning whose only advice was to go and use a
different tab, in a tool used to lay a home out *before* deciding who moves into it. Every other tile
in that grid answers one gesture (click, and something appears in the plan) and these answered none.

The rail's `Worn by` / `Belongs to` row now leads with **Nobody**, and that is the default because it
is the answer that always works. `SensorFit.Personal` reads it as *put this down in the room that was
clicked*: a **`SensorHost.Room`** host at `SensorFit.KeptHeight` (0.91 m, the nightstand or counter the
report already specifies the medication dispenser on), drawn as a labeled box exactly like the hub
beside it. Naming a resident still does what it always did.

Three things make this cost almost nothing, and each is the reason a whole subsystem needed no line:

- **`Room` is the hub's host and the dispenser's**, so `HomeRenderer` already draws it,
  `RoomRegions.RemoveRoom` already cascades it, `VariantRevert` already restores it and `VariantDiff`
  already reports it.
- **No figure about what the home can see moves**, because every worn entry has a zero envelope and no
  rules: the two invariants stated above (no coverage figure moves, nothing is raised). A pendant kept in
  a room is not worn by anybody, so `SensorSim`'s panic case (`d.def.hostId == who`) correctly never
  fires for it; the one on a resident is unchanged.
- **`KeptHeight` has to be stated at all** only because the catalog's mount height for anything worn is
  **zero**: right while nothing drew one, and wrong the moment something does: `HomeRenderer` centres
  a device's box on its pose, so zero is half a zipper pull below the floor plane. The fit returns it
  and `SensorTool.Place` writes it onto the instance, so the inspector can move it like any other
  height.

The refusals that remain are the two that are still real, and both name the way round rather than
being a dead end: clicking outside every room (*"click inside a room to put this down, or choose the
resident it belongs to"*), and naming a resident who has since left the household.

### Grab bars are surfaced here, not migrated: `MountPlacement`

`grab_bar_24`, `grab_bar_36` and `handrail` appear under a **Fixtures** chip in the Equipment rail, and
**their data does not move**: placing one still writes a `WallMountDef`, so stored homes, all six
samples, `VariantDiff`, `VariantRevert` and the wall-mount inspector are untouched by construction.

Two rails placing wall mounts is two chances to disagree about which face of a wall a click came from,
so `MountPlacement` (`Assets/Scripts/HomeViz/MountPlacement.cs`) holds Hover / Fit / Place / Ghost and
**both** tools call it: the same call `HomeMetrics.NearestWall` already represents, made once more.

A fixture shows **no price**, and that is honest rather than unfinished: `FurnitureCatalog` has no cost
field at all, and a bar is a change to the *building*, reported in the plan and the report's room
sections rather than in a technology total a funder reads as equipment. `threshold_ramp` is absent from
the chip despite being just as much an accessibility fixture. It is a floor item, and a floor item
needs a rotation, a control, a turning ghost and a pair of keys, which is the Furnish rail in full a
second time.

### `SensorPackages.Recommend` is deliberately NOT extended

It is *"the report's placement guidance made mechanical"*, and the report has no guidance on who needs
a rocker knife. Auto-recommending one would have the tool invent a clinical judgement in the one place
it has no business doing so. It also means `SampleHomes.Generation` does not move and no home is
re-installed: the two care samples ship exactly the package they always did.

The report's system is three parts and each has a home here. Peripheral sensors (§3.1.2) are
`SensorDef` on `LevelDef`, the central hub (§3.1.3) is a device like any other, and remote care access
(§3.1.4) is `MonitorTool`, which authors nothing.

**Installing sensing IS a proposal, and that is why this cost so little to build.** HomeViz was
already a before/after machine, so putting sensors on the level meant `VariantDiff` → `CompareTool`
markers → `VariantRevert` → the ghost → the report all carried them with additions rather than new
subsystems. The five files that are genuinely new are the ones that answer questions nothing else
could: where a device installs, what it can see, what it costs, what happens over a day, and what a
caregiver is told about it.

### A device hosts on an element, never on a coordinate

A door sensor belongs to an `OpeningDef`, a pressure pad to the `ObjectInstance` of a bed, a stove
sensor to the range, a pendant to an `OccupantDef`. Storing `(x, z)` instead would repeat the mistake
`OccupantDef` exists to avoid: widen a doorway or move a bed in a proposal, and the device is left
describing geometry that moved. `SensorPose.Resolve` derives the pose, and takes an **explicit
level** for the reason `HomeRenderer.MountPose` does: the ghost has to place a REMOVED device from
the variant that still has its host.

It is also what makes the simulation possible at all. "Did anyone go through the front door" is a
question about an opening; "is the hob on" is a question about a `cook` activity anchored to a range.
Both are answerable because the device names the element and `OccupancyModel` already knows where
everyone is relative to it. A coordinate would have to be re-matched to an element every minute.

Only two hosts carry a point of their own: `Room` (a cone has to start somewhere, and the corner it
was put in IS the placement decision) and `Point` (a water sensor sits on a patch of floor belonging
to nothing). A device hosted on an **occupant** has **no pose at all**: `SensorPose.Resolve` returns
`resolved = false`, the renderer skips it, and the console shows it against a person. Returning the
wearer's live position would be wrong twice. It is not where the device is installed, and it would
make a pendant's marker skate around the plan every simulated minute. Note the claim is about the
**host kind**, not about the catalog entry: the same pendant left unassigned is a `Room` host and is
drawn like any other device, which is why every check for this reads `sensor.hostKind` and never
`entry.IsWorn`.

Every cascade that removes a host removes its devices, in **two places that must not disagree**:
`SelectTool.DeleteSelected` and `VariantRevert`. Reverting an added wall takes its openings, its
mounts *and* the sensors on those openings, which is one host deeper than the existing cascade went,
so the doomed opening ids are collected before the openings are removed.

### The day is derived from the household: `SensorSim`

`OccupancyModel.PoseAll(variant, level, minute)` already answers where everyone is at every minute.
`SensorSim` walks all 1,440 of them and turns that into events; `SensorRules` turns events into
alerts. Nothing is scripted per home: move a resident to a different bedroom in a proposal and a
different motion sensor is the one that goes quiet.

**Deterministic, and nothing is stored.** The timeline, the console, the report and the tests all
describe one day and would contradict each other otherwise, and a stored event log would be a second
copy of the timeline that a proposal could contradict, exactly as a stored occupant position would.

**Two modes, and the reason there are two.** A correct package on a normal day raises *nothing*,
that is the system working, and §4 names false alarms as an evaluation criterion because a package
that cries wolf is one staff learn to ignore. So `Mode.Routine` is the household's ordinary day and
it must produce **zero alerts on all six samples**. But a package that raises
nothing also *shows* nothing, and the report's argument is entirely about the exceptional day, so
`Mode.Eventful` injects seven of its own scenarios onto this home's real people and real devices, at
fixed times so every screenshot and assertion describes the same afternoon. The console labels the
two "Typical day" and "Day with incidents" rather than letting anyone mistake the second for a
forecast.

The false-alarm floor is three specific clauses, and each was found by that test failing:

- **The stove rule measures the hob being on WITH NOBODY IN THE KITCHEN.** The report says *"left
  **unattended** for 45 minutes"* against sessions of 15-20; measuring the hob alone raised an alert
  during an ordinary meal on all six samples. `SensorSim` emits a second event stream marked
  `Unattended`, `OnSpans` takes a detail filter to walk it, and `StateAt` skips it so the plan tint
  still follows the hob rather than the derived condition.
- **A fall needs someone AWAKE still in the room.** A sleeping resident produces no movement for
  hours; without this clause every bedroom raises a fall alert every night.
- **…whom no OTHER sensor in that room can see.** Two sensors covering one room would otherwise raise
  a fall every time somebody crossed from one cone into the other.

**One incident happens in one place.** Injecting a leak into every water sensor gave a four-bathroom
home four simultaneous floods, which demonstrates nothing except carelessness.

### Coverage is clipped to the room: `SensorCoverage`

The same shape as `HomeMetrics`, one step over: that file asks whether a wheelchair fits through a
doorway, this asks whether anyone would know if something happened. A PIR sensor's 9.1 m range
(§4.3.1) is longer than most homes, so an **unclipped disc would report one sensor in the hall as
covering five bedrooms through their walls**, and a figure that flatters a plan is worse than none,
because the entire point of the figure is to find the gap.

What is deliberately not modelled is occlusion *within* a room: a sensor does not lose the far corner
of an L-shaped living room to a returning wall. Same trade `FurnitureFit` makes about openings in
perpendicular walls: the extra precision changes almost no real plan, and it would make the number
move when a door was left open.

`Envelope.Of` resolves a sensor once and is then tested against thousands of points; resolving the
pose inside the per-point test made a five-bedroom sweep quadratic in the plan for no reason.

**Motion sensors go in a CORNER**, and a room longer than 0.8 × the radius gets two at opposite ends.
A 110° cone at a rectangular room's centre covers about half its floor while the same cone in a corner
covers effectively all of it, because the room subtends only 90° from there. In the care homes'
corridors that one decision took coverage from 49% to 93%, and below about 90%, a resident standing
in the unseen part for ten minutes raises a fall alert, so this is a false-alarm fix as much as a
coverage one.

### The money is added up in exactly one place: `SensorCost`

§5.4 prices a bundle as *hub + 3-5 sensors + **one** monthly fee of $79.95-$149.95*, and every sensor
row in §4.1 says "Monthly: Part of system fee". So the system fee lives on `central_hub`, only the
pendant and the dispenser carry a monthly of their own, and a second hub adds hardware rather than a
second subscription. A per-device monthly would quintuple a care home's running cost in a figure
somebody takes to a funder.

**§4.1's per-device savings claims are deliberately reproduced nowhere in the app.** "Saving
$500/month per resident", "cuts nursing visits by 50%" are vendor figures with no method behind them,
and printing them in a tool used to argue for funding would launder a marketing claim into an
estimate. What is used instead is §5.2.2's single mechanical figure: $20-40 of labour per incident
answered remotely rather than in person. Multiplied by a **stated assumption**
(`SensorCost.AssumedIncidentsPerWeek`, 3) that is printed beside the result everywhere it appears.
Deriving it from the demonstration day instead would claim seven incidents *every* day and inflate the
offset roughly fivefold, which would discredit the whole figure the moment anyone checked it.

### The console is the ethics section, made checkable: `MonitorTool`

§5.3.3 gives DSPs full intervention, family members trends and view-only access, and residents a
simplified surface; §5.5 asks for all of it "without compromising dignity or autonomy". Every version
of that argument is a paragraph, and a paragraph is what this app has no room for.

Switching the **role** shows it instead. As Family the camera disappears from the device list, the
alerts stop naming which room anyone is in: "Something needs attention in bedroom 3" rather than "No
movement in bedroom 3 for 10 minutes, and Alice is in there", and the resident's live position is not
reported at all. A care team can see what their sister will and will not be able to see, in one
click, before anyone signs anything. `SensorPrivacy` on each device is what the filter reads, and
`video_doorbell` is the only Video-tier device in the catalog because §5.3.3 records exactly that:
"no constant cameras; optional entry-way only".

**Responses are console state, never document state.** Acknowledging an alert must not dirty the file
or land in the undo stack: the same rule `OccupancyClock` lives by, for the same reason: scrubbing a
day and answering its alerts is a read.

### What the plan and the timeline show

`HomeRenderer` renders each device as a small labeled box at its true size, `PickOnly` like every
other non-shell collider, with a **material per device** so `UpdateSensorStates` can tint it idle /
active / alerting as the clock moves. That is what makes playing the day legible in the plan rather
than only on the timeline: the timeline says an alert lands at 03:20, and the plan says where.
`ReportCapture` freezes every device at idle for the duration and restores afterwards, for the reason
it hides the occupants: a sensor lit red in the "after" shot is the clock having moved, not the
proposal.

`TimelineBar` gains an alert lane under the person strip. Sensor traffic as hairlines, alerts as
diamonds coloured by severity, and `CollapsedHeight` grew 92 → 106 with it. Clicking a diamond
scrubs the clock to that minute and selects the device, deferred through `_pendingAlert` exactly like
every other request that bar makes, because scrubbing re-poses every marker and re-tints every device.

`SensorOverlay` is a plain class the controller owns and draws, like `SelectionOverlay` and for the
identical reason: a tool's `DrawOverlay` is gated on the pointer being off the rails, and the coverage
picture is what someone looks at *while* reading the coverage figure in the rail.

### The refusals are the interesting half of `SensorFit`

`OpeningFit`'s contract, over the question this fit actually answers: not "where" but "on what". Two
differences from the other two fits are deliberate:

- **A refusal refuses.** `FurnitureFit` slides and places anyway, because swallowing a click reads as
  the tool being broken. Here, installing a stove sensor on a wardrobe would be a device that reports
  nothing forever, which is worse than a click that says why not.
- **Clicking ON a window refuses even when a door is nearby.** The generous reach is there to pick
  *between* candidates, not to turn an aimed click into a different answer: a doorbell landing on the
  back door when someone pointed at the front window erodes trust in every other snap in the app.

`door_sensor` may go on a window and the lock and doorbell may not, because §3.1.2 says "door/window
contact sensors" and the other two need a leaf.

### The package tiers are gone

The Sensors rail opened with a three-way `Essential / Standard / Care home` segmented control and, under
it, **`Add the N missing devices`** (or `Package complete`): one press installing a whole package
through `SensorPackages.Missing`, which was `Recommend` filtered by `SensorFit.AlreadyInstalled` so a
second press could not double anything. All of that UI is **deleted**, along with the three functions
that existed only to serve it: `SensorPackages.Missing`, `Label` and `Describe`.

**`SensorPackages.Recommend` and `Tier` stay, and must.** They are what builds the two care-home
samples' shipped proposal (`SampleHomes`). What the removal costs is the *"one function, two callers"* argument that file's header used
to lead with: the rail and the samples can no longer disagree about what a complete package is, because
the rail no longer has an opinion. Devices are placed one at a time, through `SensorFit`, which is the
gesture the rest of the tool was always built around.

### Two samples ship a proposal, and `SampleRefresh` had to change

`apartment_5b4b` and `house_5b4b` each ship a locked **"Smart home package"** beside their baseline,
built by `SensorPackages.Recommend`, which since the package button was removed is the **only** thing
that calls it, so what ships is still derived from the plan rather than listed by hand. The other four
samples stay bare.

That broke `SampleRefresh.Evaluate`, which asked "is there exactly one variant". It was the same
question while every sample shipped only a baseline; the day two of them ship two, a home is born
tripping the count on its first launch and is **frozen at whatever generation installed it forever**,
precisely the staleness trap `SampleHomes.Generation` exists to close, reintroduced by the mechanism
meant to close it. `VariantDef.fromSample` asks it properly: refresh when every variant is one of ours
and still locked. It defaults to false, so homes already on disk take the old path unchanged with no
migration, and anything a user branches is false by construction.

### Nothing floats. Hosts tightened, faces respected (2026-08-24)

Three placement faults were fixed in one pass, on request:

- **Room-hosted devices floated.** The hub, thermostat, fall radar and motion sensor rendered at the
  raw click point at a fixed centre height, mid-air. They are `Wall` hosts now: the pose
  `SensorPose.OnWall` always gave the smart switch. The dispenser, air monitor, speaker and measuring
  set (and any personal item left as Nobody) sit ON a surface: `SensorFit.Surfaces` lists the
  furniture with a usable top, the click is clamped into the host's footprint, stored in the host's
  own unrotated frame, and posed at the item's top plus half the device height, so the device rides
  its host. The bin is floor-clamped and the bulb ceiling-flush (`SensorPose.FreeHeight`). Stored
  instances keep their stored hostKind and resolve exactly as before; the samples were deliberately
  not re-seeded.
- **The opposite-face bug.** `GroundPoint` projects the click to the floor plane, so a click on a
  wall's visible face landed behind the wall under the angled camera and `NearestWall` read the wrong
  side. Placement now goes through `MountPlacement.WallCursor`, which prefers the pick ray's hit on
  the wall itself: the same parallax the wall-mount drag fixed earlier, closed at the same shared
  spot so the two rails cannot disagree. Opening-hosted devices also store the clicked face in
  `hostSide` and sit proud of it; they used to render buried in the wall's body.
- **The yaw double-count.** Writers stored absolute world yaw in `facingYaw` while `SensorPose` added
  it to a host-derived base, so opening/furniture/wall cones pointed wrong. `facingYaw` is now a
  delta (zero from every element-host writer); `SensorFit.Result.coneYaw` carries the world yaw for
  the ghost. Room and Point hosts were already consistent and are unchanged.

The same pass reworded the rail: descriptions in `SensorCatalog.asset` lead with what the item does
and who uses it, the cost row is `Entry.CostLine` (whole dollars, one line), the coverage row is
labeled `Range`, and the "Installs on"/"Kept in" row, the ⚠ provenance glyphs and the report's
per-row notes are gone. `provenance` and `speculative` stay as data.

