# Smart living: where every number comes from

The **Smart living** stage holds twenty-five things that can be installed in a residence. Sixteen of them
sense, and every one of those is derived, device for device and figure for figure, from
**`Assets/Resources/SmartHomeTechnology/SmartHomeReport.pdf`**: *Smart Home Technology Solutions for
Individuals with Intellectual and Developmental Disabilities*, Olzhas Yessenbayev, Cornell Tech /
Center for Family Support, New York, July 2025.

The other nine are **Everyday living** (a rocker knife, a sock aid, a touch-free bin) and the report
does not mention any of them. They are in the catalog because the things that decide whether someone
can live independently are often not sensing at all, and they are **marked**, not mixed in: see
[Everyday living](#everyday-living--not-from-the-report), below.

This file is the map back. It exists because the costs and coverage figures are read off the screen in
a funding meeting, and **an unattributable number is worse than no number**. Everything in the sensing
half can be checked against a section of the PDF; everything in the everyday half says on its own row,
in the rail and in the report, that it cannot be. The rule did not get looser: a figure still has to
say where it came from. What changed is that *"typical retail, check it"* is now one of the answers it
is allowed to give.

> **The stage used to be called Sense**, and the catalog was these sixteen devices alone. Nothing was
> renamed on disk: `LevelDef.sensors`, `SensorDef`, `SensorCatalog.asset` and the thirteen `Sensor*.cs`
> files keep their names, because renaming the field would silently drop the whole layer out of every
> residence already saved. This is the precedent Sketch → Import set: *the word survives everywhere it is
> still accurate*, and `SensorDef` is still accurate about the SHAPE of the record. An everyday aid is
> that record with no envelope and no rules.

Two sources of truth carry the data, and they are deliberately duplicated:

| | Where | Read by |
|---|---|---|
| `SensorCatalog.asset` | `Assets/Resources/` | the picker, the rail, the renderer |
| `SensorDevices.cs` | `Assets/Scripts/Authoring/Interior/` | `SensorFit`, `SensorSim`, `SensorPackages`, the tests |

`SensorCatalog` is a ScriptableObject in `Assembly-CSharp` and asmdefs cannot reach it, so the pure
geometry and simulation code cannot read the asset: the identical constraint `SampleFurniture`
carries against `FurnitureCatalog`, handled the identical way. `SampleResidenceInstaller
.VerifyAgainstCatalog(SensorCatalog)` compares the two on every seed and warns on drift in size,
coverage, cost, host kind or privacy tier.

---

## The architecture (§3.1)

The report's system is three parts, and ResidenceViz models each one:

| Report | ResidenceViz |
|---|---|
| **Peripheral sensors** (§3.1.2) | `SensorDef` on `LevelDef`, hosted on the element each watches |
| **Central hub** (§3.1.3) | the `central_hub` device, and where the system's monthly fee lives |
| **Remote care access** (§3.1.4) | `MonitorTool`: the DSP console, with §5.3.3's three roles |

---

## The 16 devices

**Cost is the report's own range.** Where it prices a device across several vendors, the low figure is
the cheapest named and the high figure the dearest, which is why `smart_lock` spans $150 to $849 (the
OlideSmart is an ADA powered opener, and the report keeps it in the same row).

### Safety: §4.4

| id | §  | Purchase | Monthly | Coverage | Privacy | Vendors named |
|---|---|---|---|---|---|---|
| `door_sensor` | 4.4.1 | $18-$100 |: | its doorway | passive | Aqara $18, YoLink $20, Simply Home $50-100 |
| `stove_sensor` | 4.4.2 | $149.99-$495 |: | its range | passive | FireAvert $149.99, Simply Home $150-250, Innohome $399, iGuardStove $495 |
| `water_sensor` | 4.4.3 | $18-$100 |: | 0.3 m | passive | YoLink $18, Aqara $19.99, Kidde $39.99, Honeywell Lyric $79 |
| `smart_lock` | 4.4.4 | $149.99-$849 |: | its doorway | passive | Sesame $149.99+$99, Schlage Encode $224, August $229.99, OlideSmart $849 |
| `smart_switch` | 4.4.5 | $29.95-$399 |: | what it powers | passive | Enabling Devices $29.95, TP-Link Kasa $44, Leviton $49.99, Brilliant $399 |

**`smart_switch` is displayed as "Smart plug / switch", and there is deliberately no separate
`smart_plug`.** §4.4.5 prices a plug-in adapter and an in-wall switch as one range across one vendor
list, so a second row would split one attributable figure in two and count it twice in `SensorCost`.
The word *smart plug* is in the entry's `detects` text, which the picker's search already reads.

### Mobility: §4.3

| id | § | Purchase | Monthly | Coverage | Privacy | Vendors named |
|---|---|---|---|---|---|---|
| `motion_sensor` | 4.3.1 | $20-$80 |: | 9.1 m / 110° | presence | TP-Link Kasa $20, Aqara P1 $25, SmartThings $25, Simply Home $40-80 |
| `bed_chair_pad` | 4.3.2 | $30-$200 |: | its bed | presence | Smart Caregiver $30, VitalBase $120, Rest Assured $150+, Simply Home $100-200 |

`motion_sensor`'s envelope is the report's own: *"up to 30-40 feet with a 90-120 degree field of
view"*, mounted *"at heights of 6-8 feet"*. 9.1 m is 30 ft (the conservative end) and 110° is the
middle of the stated arc.

### Health: §4.2

| id | § | Purchase | Monthly | Coverage | Privacy | Vendors named |
|---|---|---|---|---|---|---|
| `smart_thermostat` | 4.2.1 | $100-$250 |: | the residence | passive | Sensi Touch 2 $149, Honeywell T9 $169, Nest $249, Ecobee Premium $250 |
| `med_dispenser` | 4.2.2 | $219.95-$299.95 | $24.95-$29.95 | its tray | passive | Livi $199+$19.99/mo, Hero $99+$29.99/mo, MedMinder $49.99/mo, e-pill $299 |

### Staff communication: §4.5

| id | § | Purchase | Monthly | Coverage | Privacy | Vendors named |
|---|---|---|---|---|---|---|
| `panic_pendant` | 4.5.1 | $50-$284.95 | $27.95-$51.95 | worn | audio | ATS $119.99 (no fee), Bay Alarm $29.95/mo, Medical Guardian $124.95+$39.95/mo, Life Alert $197+$49.95/mo |
| `video_doorbell` | 4.5.2 | $49.99-$329.95 |: | 5 m / 160° | **video** | Ring $49.99-$149.99, Arlo $79.99, Eufy $99.99+, Nest $179.99 |

**`video_doorbell` is the only Video-tier device in the catalog, and that is the report's position
rather than an omission.** §5.3.3 records SimplyHome's own practice: *"no constant cameras; optional
entry-way only"*. The console's Family role hides it entirely.

### Hub: §3.1.3, priced in §5.4

| id | Purchase | Monthly | Privacy |
|---|---|---|---|
| `central_hub` | $849.95-$1,549.90 | **$79.95-$149.95** | audio |

§5.4 prices a bundle as *hub + 3-5 custom sensors + one monthly fee*, and every sensor row in §4.1
says *"Monthly: Part of system fee"*. **`SensorCost` is the only place these are added up**, and it
puts the system fee on the hub alone: a per-device monthly would count it five times over on a care
residence. Only the pendant and the dispenser, which §4.5.1 and §4.2.2 price separately, carry a monthly
of their own.

### Emerging: §2.2.4 and §3.1.2, **not specified by the report**

| id | Where the report gestures at it | Purchase (indicative) |
|---|---|---|
| `health_wearable` | §2.2.4: *"assistive wearables (e.g., health-tracking watches)"* | $99-$299 |
| `air_quality_monitor` | §3.1.2: *"environmental monitors for temperature/humidity"* | $79-$229 |
| `voice_prompt_speaker` | §3.1.2, §4.1: the *"verbal prompt"* every scenario ends in | $49-$129 |
| `fall_radar` | §2.2.4: *"computer vision AI"*, §4.3 fall detection | $199-$449 |

These four are flagged `speculative` in the catalog, and every surface that prints their cost says
so: **the report names the class of device without evaluating a product, so nothing here should be
mistaken for a figure it stands behind.**

---

## Everyday living: NOT from the report

Nine items the report does not mention at all. They are here because the sensing layer answers *would
anyone know if something happened*, and that is only half of living somewhere: a rocker knife, a sock
aid and a button hook decide whether a resident eats and dresses without waiting for someone. That is
the same argument the report makes about a stove sensor, reached by a different route.

**None of them senses, reports, connects to the hub or raises anything**, and that is what makes them
almost free to carry: `coverageRadius` 0 and no rule means `SensorOverlay` skips them, `SensorSim`
emits nothing for them, `SensorRules` sees no events and `SensorCoverage` counts nothing, so **not one
existing figure in the app moves when they are installed.** That holds as two invariants: an everyday item changes no coverage figure and raises no gap, and
added to a full package it still raises nothing, the second over all six samples.

What they *do* get, for nothing, is everything a device already had: a host to hang off, a price in the
total, a line in the change list, a revert, and a row in the report.

**Every price below is typical US retail**: `speculative` is set and `provenance` carries the
sentence saying so. Both are **data only** now: the rail and console ⚠ glyphs and the report's
per-row notes were removed on request, so no user-facing surface prints them. The distinction is
still recorded because there are two reasons a price is unquoted: the Emerging four are devices the
report *names* without pricing, these are devices it does not name.

### Personal. Hosted on a resident

`SensorHost.Occupant`, which is the pendant's path: **no pose at all**, so nothing is ever drawn in the
plan. That is what lets a 2 cm zipper pull be in this catalog honestly. See *Why these are not
furniture*, below. Left **unassigned**, the same item is put down on the nearest counter, table or
nightstand instead (a `Furniture` host on a `SensorFit.Surfaces` item), never floating in mid-air.

| id | Purchase | Privacy | What it is |
|---|---|---|---|
| `stability_utensils` | $30-$200 | none | Weighted, contoured or self-levelling cutlery. The range spans an off-the-shelf weighted set and a powered stabilising handle. |
| `rocker_knife` | $10-$35 | none | A curved blade that cuts by rocking. One hand does the whole job. |
| `key_turner` | $10-$30 | none | A lever arm on a key. It gives the whole hand something to hold. |
| `sock_aid` | $10-$35 | none | Puts a sock on without bending to the foot. |
| `button_hook` | $8-$25 | none | Fastens a button one-handed. |
| `zipper_pull` | $6-$18 | none | A ring that replaces a zip tab. A hooked finger is enough. |

`button_hook` and `zipper_pull` are separate rows although they are usually sold as one combined tool,
so a plan can say a resident needs only one of the two.

### Kept in the residence

Drawn as a small labeled box: at or above the scale of a `motion_sensor` (0.06 × 0.06 × 0.08). The
measuring set sits on a counter (`SensorHost.Furniture`, on a `SensorFit.Surfaces` item); the bin
stands on the floor and the bulb hangs from the ceiling (`SensorHost.Room`, floor-clamped and
ceiling-flush in `SensorPose.FreeHeight`).

| id | Purchase | Privacy | What it is |
|---|---|---|---|
| `large_print_measures` | $12-$45 | none | High-contrast, large-figure measuring cups and jugs. Shared kitchen equipment, kept on the counter. |
| `auto_trash_can` | $40-$130 | none | Opens on a wave. No free hand and no foot pedal needed. |
| `smart_bulb` | $10-$50 | **passive** | Light by voice, app or schedule. Nobody crosses a dark room to a switch. |

Two privacy calls worth reading twice, because both look inconsistent and are not:

- **`auto_trash_can` has an infrared sensor in its lid and is still `none`.** The tier answers *what
  reaches a caregiver*, not *what contains a sensor*, and nothing this notices leaves the bin.
- **`smart_bulb` is `passive` rather than `none`** because it genuinely is on the network, even though
  nothing it does is reported anywhere.

`SensorPrivacy.None` is new, ranks with `Passive` as least intrusive, and reads **"Not connected"**.
The tie is deliberate: every console role filter compares with `<=`, so an unpowered aid is shown to a
family member exactly as a water sensor is, which is right, because neither reports anything about a
person. What separates the two tiers is what the badge SAYS, not who may see it.

### Why these are not furniture

`FurnitureCatalog` is where a grab bar lives, and it is the obvious residence for a sock aid too. It is the
wrong one, and the reasons are mechanical rather than aesthetic:

- A 2 cm entry **renders as a 2 cm speck under a ~0.35 m label floating seven times its own height
  above it**: `AddLabel` has no minimum, so the label would be the item on screen.
- `ResidenceEditController.MIN_ITEM_SIZE` (0.05) **silently inflates it 2.5×** the moment anyone touches the
  resize gizmo, and `SelectTool`'s dimension slider cannot display its true size at all.
- `FurnitureFit` never conflicts with anything at that scale, so the doorway rule that justifies the
  whole fit pass does nothing.
- The furniture path still has no "sits on a counter": `FurnitureCatalog.MountType.Counter` is
  declared and referenced nowhere. The DEVICE path models it: `SensorFit.Surfaces` plus the
  `Furniture`-host top-face pose in `SensorPose.OnFurniture`, which is where the measuring jug gets
  its counter.

The device path has none of these, because an assigned personal aid is **never drawn**, and one put
down in the residence sits on a surface at a scale this renderer has always handled.

### Grab bars, which stayed furniture

`grab_bar_24`, `grab_bar_36` and `handrail` are offered in the Smart living rail under a **Fixtures**
chip, and **their data did not move**: placing one still writes a `WallMountDef` through
`MountPlacement`, exactly as the Furnish rail does. Stored residences, all six samples, `VariantDiff`,
`VariantRevert` and the wall-mount inspector are untouched by construction. It is a second door into
the same room, not a migration.

They carry **no price**, and that is honest rather than unfinished: `FurnitureCatalog` has no cost
field at all, and a bar is a change to the *building*, reported in the plan and in the report's room
sections rather than in a technology total a funder reads as equipment. Printing `$0` beside a device
that costs $18 would be worse than saying nothing.

`threshold_ramp` is deliberately absent from that chip despite being just as much an accessibility
fixture: it is a floor item, and a floor item needs a rotation, which needs a control, a ghost that
turns and a pair of keys: the Furnish rail, in full, a second time. It stays one tab away rather than
half-built here.

---

## The eight rules

Each is a sentence from the report with a number in it, expressed as a `SensorRuleDef` so a residence can
move the number without moving it for every other residence.

| Rule | Default | Window | Severity | Report |
|---|---|---|---|---|
| `unattended_cooktop` | 45 min | all day | urgent | §3.1: *"left unattended for 45 minutes … beyond the usual 15-20 minute sessions"* |
| `night_exit` | immediate | 21:00-06:00 | warning | §4.1: *"If front door opens after 9 PM, alert caregiver and play verbal prompt"* |
| `bed_exit` | 10 min | 00:00-05:00 | warning | §4.3.2: *"alerts for prolonged absence (e.g., after 10-30 minutes)"* |
| `possible_fall` | 10 min | all day | urgent | §4.1: *"no motion for 10 min triggers alert to caregiver's phone"* |
| `missed_medication` | 30 min | all day | warning | §4.2.2 |
| `water_leak` | immediate | all day | urgent | §4.4.3: *"reacts instantly upon contact with water"* |
| `panic` | immediate | all day | urgent | §4.5.1 |
| `temperature` | 30 min | all day | warning | §4.2.1: *"alerts if temperatures deviate from safe ranges"* |

**Fourteen of the twenty-five have no rule at all, and that is correct rather than unfinished.** Five
of the sensing devices: a lock, a switch, a speaker and a hub *act* rather than notice, and a
doorbell's motion is a notification rather than an alarm (§4.5.2: *"screen visitors"*). And all nine
Everyday living items, which notice nothing in the first place.

That emptiness is load-bearing rather than incidental: `SensorRules` reaches for
`SensorDevices.EffectiveRules`, so a device with no rule is silently a no-op through the whole
simulation. It is why nine new rows needed no change to `SensorSim` or `SensorRules` at all.

### Two rules read more carefully than the report states them

- **`unattended_cooktop` measures the hob being on WITH NOBODY IN THE KITCHEN**, not the hob being on.
  The report's wording is *"left **unattended** for 45 minutes"* against sessions that normally run 15
  to 20, and measuring the hob alone raised an alert during an ordinary meal on all six samples.
  `SensorSim` emits a second event stream marked `Unattended` for exactly this.
- **`possible_fall` requires someone awake still in the room, whom no other sensor can see.** Without
  the awake clause every sleeping resident raises a fall alert every night; without the other-sensor
  clause, two sensors covering one room raise one every time somebody walks between their cones.

Both are the false-alarm floor (an ordinary day raises nothing on every sample), and
§4 names reliability and false alarms as an evaluation criterion for precisely this reason.

---

## The two figures that are NOT quoted from the report

**Coverage** is computed, not claimed: `SensorCoverage` grids each room at 0.15 m: the same step
`OccupancyModel` stands people on, and clips each sensor to its own room, because a 9.1 m radius is
longer than most residences and an unclipped disc would report one sensor in a hall as covering five
bedrooms through their walls.

**The labour offset** is quoted at a stated assumption rather than derived from the demonstration day.
§5.2.2 gives one mechanical figure (*"saving $20-40/hour in labor costs per incident"*) and
`SensorCost.AssumedIncidentsPerWeek` (3) is multiplied by it, with the assumption printed beside the
result everywhere it appears. §4.1's per-device claims (*"saving $500/month per resident"*, *"cuts
nursing visits by 50%"*) are **deliberately not reproduced anywhere in the app**: they are vendor
figures with no method behind them, and reprinting them in a tool used to argue for funding would
launder a marketing claim into an estimate.

The demonstration day is used qualitatively instead (*which* scenarios this package would catch) 
because it acts out seven of the report's incidents at once and treating that as typical would inflate
the offset roughly fivefold.

---

## The packages

`SensorPackages.Recommend` is the report's placement guidance made mechanical, and it is what builds
the two shipped sample proposals, so what ships is derived from the plan rather than listed by hand.

**There is no package UI.** The Sensors rail's tier bar and its *Add the N missing devices* button
were removed, along with `SensorPackages.Missing` / `Label` / `Describe`, which existed only for them;
a user installs devices one at a time through `SensorFit`. The tiers below are what the authored
samples select between, not a control anybody presses.

| Tier | What it adds |
|---|---|
| Essential | hub, every exterior door, the range, water at every wet fixture |
| Standard | + movement sensing through the residence, a doorbell, a thermostat |
| Care home | + a pad under every bed, a pendant per resident, spoken prompts, medication |

**Motion sensors go in a corner, not the middle**, and a room longer than 0.8 × the sensor's radius
gets two at opposite ends. A 110° cone at a rectangular room's centre covers about half its floor; the
same cone in a corner covers effectively all of it, because the room subtends only 90° from there. In
the care homes' corridors that single decision took coverage from 49% to 93%.

---

## Where the code lives

| File | What it is |
|---|---|
| `Authoring/Interior/SensorTypes.cs` | the schema: `SensorDef`, `SensorRuleDef`, hosts, privacy tiers, events, alerts |
| `Authoring/Interior/SensorDevices.cs` | the 25 items, their default rules and their provenance (the CXRAuthoring mirror) |
| `Authoring/Interior/SensorPose.cs` | where a device is, derived from the element it hosts on |
| `Authoring/Interior/SensorFit.cs` | which element a click installs on, and why not |
| `Authoring/Interior/SensorCoverage.cs` | what is watched, what is not, and the gaps |
| `Authoring/Interior/SensorCost.cs` | the only place costs are added up |
| `Authoring/Interior/SensorSim.cs` | a day, derived from the household's schedule |
| `Authoring/Interior/SensorRules.cs` | events → alerts |
| `Authoring/Interior/SensorPackages.cs` | a whole package, derived from the plan |
| `ResidenceViz/SensorCatalog.cs` | the ScriptableObject, with vendors and the report's own prose |
| `ResidenceViz/SensorOverlay.cs` | coverage drawn over the plan |
| `ResidenceViz/Tools/SensorTool.cs` | placing devices, everyday aids and fixtures: the Equipment rail |
| `ResidenceViz/MountPlacement.cs` | putting a wall-mounted fixture on a wall; shared with `FurnitureTool` |
| `ResidenceViz/Tools/MonitorTool.cs` | the DSP console: §3.1.4 |
