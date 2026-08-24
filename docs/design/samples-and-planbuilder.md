# Design notes. Sample residences and `PlanBuilder`

> Why the six sample residences exist, how they stay fresh on disk, and why plans are authored as room
> rectangles with everything error-prone derived. The rules are summarised in
> [`.claude/rules/samples-and-planbuilder.md`](../../.claude/rules/samples-and-planbuilder.md); the reasoning lives here.

## Sample residences

`SampleResidences` ships six complete, furnished, single-storey dwellings. They exist because the library
used to start empty and the only way in was the hardest step of the workflow. Import a plan, calibrate
it, trace it, so nobody could see what the tool does without first doing that.

| | Apartments | Houses |
|---|---|---|
| Small | Studio, 38 m², 1 person | 2 bed / 1 bath, 90 m², 2 people |
| Medium | 2 bed / 1 bath, 74 m², 2-3 people | 3 bed / 2 bath, 125 m², 4 people |
| Large | 5 bed / 4 bath, 165 m², shared home | 5 bed / 4 bath, 210 m², assisted living |

The two five-bedroom plans are the care settings the tool is aimed at: **every door 36" and
step-free**, roll-in showers, grab bars in every bathroom, handrails down a 1.6 m corridor (wide enough
for two wheelchairs to pass), and a fully accessible bedroom 1 with a hospital bed, lift and
wheelchair. Every bedroom and bathroom in those two clears a 1.5 m turning circle.

Four of the six ship with **only the locked "Existing" baseline**. Branching is what *Propose a
change* is for, and *Unlock* is one click. **The two five-bedroom care settings ship a second locked
variant, "Smart home package"**, so opening one and pressing Compare tells the whole sensing-layer
story without anyone having to build it first; see *Two samples ship a proposal* in [smart-living.md](smart-living.md). Both open on
the baseline regardless: a sample shows the residence as it is, and the proposal is a click away in the
mode band.

Two entry points: seeded on first run (guarded by `ResidenceSettings.samplesSeeded`, so archiving a sample
keeps it archived), and a **Sample residences** picker in the left rail that adds a fresh copy at any time.
Each install gets a new GUID and a uniqued name, so pulling the same sample twice is fine.

`SampleResidenceInstaller.BackfillOccupants` (guarded by its own `ResidenceSettings.occupantsBackfilled`) exists
because that seeding guard cuts both ways: occupants postdate the samples, so on any install that
predates them all six sit in the library with nobody in them and the timeline opens empty. Re-seeding
would resurrect archived samples and duplicate kept ones, so the roster is filled in place instead,
only for a residence tagged `sample`, only when its roster is empty, and only when its room ids still match
the sample it is named after. That last condition is the real guard: schedules address rooms by id, so
a reworked plan is left alone rather than having a household dropped into rooms that moved.

### The samples on disk go stale: `SampleResidences.Generation`

That backfill fixed **one** instance of a problem the seeding guard creates every time: a sample is
written to disk once and never rewritten, so *every* later improvement to a plan is invisible on any
machine that has already launched the app. It is not hypothetical. The opening-avoidance work below
landed after the first seed, and long after `SampleResidencesTests` went green the six residences in the library
still had a wardrobe across a cased opening, a bath across a bathroom door and a dresser across a
bedroom door. About fifty blocking items across five of the six, none of which any test could see,
because the tests build the samples fresh and the app does not.

So each installed residence carries `ResidenceDoc.sampleKey` and `ResidenceDoc.sampleGeneration`, and
`SampleResidenceInstaller.RefreshStaleSamples` (called from `Start`, after the backfill) re-installs any
that has fallen behind. **Bump `SampleResidences.Generation` whenever a plan changes**: that is the entire
contract, and forgetting it is the one way to reintroduce this.

`SampleRefresh.Evaluate` decides *whether*, and lives in `CXRAuthoring` so it can be tested at all,
the installer is in `Assembly-CSharp`, which asmdefs cannot reach. Its bar is deliberately high,
because a refresh replaces the whole document rather than merging: one variant, still the locked
baseline, no traced underlay. Branch a proposal, unlock the baseline or import a plan and the residence
stops being a sample and starts being yours: the automatic path gives up, and the rail's **Reset to
the latest sample** (confirming, and saying how many proposals it discards) is the only way in.

Unlike the backfill this does **not** compare geometry. It does not need to: the backfill wrote a
roster addressed by room id into a plan it could not be sure still had those rooms, whereas a refresh
replaces the plan too, so the only question worth asking is whether anything would be lost.

Display names are not a stable key ("Group home apartment" became "Shared home apartment") so
`LegacyNames` maps every retired name to its key. Without it the one sample with the worst geometry
would have been the one that could never be matched, and therefore never fixed. A residence still carrying
a retired name verbatim is renamed as part of the refresh; anything else a user typed is left alone.

### Why there is a builder: `PlanBuilder`

**Nothing downstream of the schema complains about bad geometry.** `WallLayout` silently *clamps* an
opening that hangs off its wall, `WallMeshBuilder` leaves a ~57 mm notch wherever two wall endpoints
miss each other by more than 1 mm, and `ResidenceRenderer` skips an opening whose `wallId` does not resolve.
Six plans of raw coordinate literals would be thousands of unreviewable lines, wrong in ways nobody
would notice. So the plans are authored as **room rectangles** and everything error-prone is derived:

- **Walls** come from the room rects, grouped by line, then *unioned and re-split at every significant
  point*. One pass handles all three hazards: a shared edge collapses to **one** `WallDef`; rooms of
  different depth sharing a line resolve without overlap; and every T-junction and crossing is split so
  all endpoints coincide exactly, which is what `WallMeshBuilder.ComputeExtensions` needs to weld.
- **Openings** are placed by relationship: `DoorBetween("hall", "bed1", …)` finds the shared edge,
  centres the opening in it, resolves the post-split host wall, and asserts `OpeningFit.IsValid`.
- **Furniture** is placed with `Against(item, room, edge, fraction)`, which computes the flush position
  and the yaw facing into the room. `alongWall: true` gives the quarter turn a tub or shower needs,
  the catalog models both 0.76 × 1.52 (narrow front, deep), but both are installed as an alcove, and
  it is the only way either fits a 1.8 m bathroom.

Anything unresolved lands in `PlanBuilder.Warnings` rather than throwing, and the tests assert that
list is empty, which is what turns a silent geometry bug into a failing test.

Each plan is also **occupied**, by the headcount its blurb advertises: `Person` / `Does` on the
builder, resolved by `BuildOccupants(level)` after `Build()` because rooms and anchors only have ids
once the geometry exists. `Does` takes a room *key* (`"bath1"`), not the emitted `r_bath1`, and an
anchor is a catalog *type* (`"range"`) resolved to the first such item inside that room. Authors never
see the `f_n` ids. Schedules are written out per person rather than generated from a template, because
the interesting part is where they *do not* line up: which two bedrooms share a bathroom, and therefore
who is queueing at half past seven.

**`SampleFurniture` mirrors the 35 `FurnitureCatalog` ids** because `FurnitureCatalog` is a
ScriptableObject in `Assembly-CSharp` and `CXRAuthoring` cannot reach it. That is the one unavoidable
duplication here; `SampleResidenceInstaller.VerifyAgainstCatalog` compares the two on seed and warns on
drift. There is deliberately no `shower_seat` in any sample: these render as massing boxes, and a seat
inside a shower would be one box buried in another.

### An L-shaped room used to be a room with a wall through it: `RoomPart`

`Room()` takes a rectangle, and **every rectangle contributes all four of its edges to the wall
derivation.** That is exactly right for rooms that merely touch, and it is why an L-shaped room could
not be said at all: two rectangles with the same *name* are two rooms, so the derivation dutifully
built a full-height wall along the edge between them: a wall that renders, encloses, and is reported
by nothing, in the middle of a room the drawing shows as open. It is the same class of silence this
file exists to break, reintroduced by the one shape the authoring surface could not express.

The sketch pipeline made it live rather than theoretical: its prompt *instructed* the model to split an
irregular area into rectangles sharing a name, so every L-shaped living room came back as a bisected
one, and `SketchPlanValidator` then demanded a door into the half nothing opened into, spending the
repair turn on a wall that should not have existed.

`RoomPart(key, partOf, x, z, w, d)` says it properly. Three things follow, and each is load-bearing:

- **The wall between the pieces is dropped, and it is a filter rather than a special case.** A shared
  span's endpoints are rectangle coordinates, and every rectangle coordinate on a line is already a
  break point on that line, so by the time union-and-re-split has run, every emitted piece lies wholly
  inside a shared span or wholly outside one. There is nothing to cut. And a shared span can never be a
  wall the plan needs: both rectangles are one room, one lies either side of the line, and rooms may not
  overlap, so no third room can reach it.
- **One `RoomDef`, with the union as its floor.** `RectilinearOutline` works on the CELLS of the grid the
  rectangles induce, not on the rectangles: each inside cell contributes its four edges wound CCW, an
  edge shared by two inside cells cancels against its own reverse, and what survives is the boundary
  already pointing the right way round. It returns **false rather than something plausible** when the
  survivors are not one simple loop: two rectangles meeting only at a corner give a vertex with two ways
  out, disjoint ones give two loops, and the room falls back to its declared rectangle with a warning,
  so the missing piece is visible rather than invented.
- **A piece is still addressed by its own key.** `Against("living_nook", East, …)` puts a sofa along the
  alcove's own wall, which is the only way to say that at all, but the *placed-footprint* bookkeeping
  keys on the room, so a chair in the alcove and a table in the main span still check against each
  other. Occupant schedules resolve through the same map, so a day that names a piece still lands on the
  room's `r_` id, which is what `OccupancyModel` and every sensor host address.

**Every rectangle in all six sample plans is a whole room**, so `DropInteriorEdges` returns early and
the derivation is provably untouched, which is what keeps three properties true together: `SampleResidencesTests` passes, `WallLinker.Relink`
is a no-op on all six plans, and `RoomRegions.Find` reproduces the sample rooms exactly.

### Openings are the other thing nothing downstream complains about

`WallLayout` emits solid boxes only **between** openings, so an item centred on a door renders as a box
floating in the hole: no warning, no error, just a grab bar hanging in a doorway. `PlanBuilder.Find`
originally asked only whether a mount's *centre* landed on a wall segment, and `Against`/`Free` clamped
to the room rectangle and knew nothing about openings at all. That produced, across the six samples, a
bath across the only way into a bathroom, wardrobes over windows, and a grab bar 0.155 m past the end of
its wall. Three things fix it, and the tests pin all three:

- **`OpeningSpans(vertical, coord, itemBottom, itemTop)`** returns the blocked runs on a wall line, but
  only for openings the item's own height actually reaches. A sofa in front of a window is not a mistake
the sill is 0.914 m and the sofa 0.84 m, so it passes underneath, and so does a kitchen run. Doors
  have a sill of 0, so everything conflicts with a door.
- **`TrySlideClear`** moves the item to the nearest legal spot instead of refusing (the `OpeningFit`
  convention), and `BuildFurniture` feeds it the footprints already placed in that room, so sliding one
  item clear of a door cannot shove it into its neighbour. Including round a corner, which is why the
  bookkeeping is 2-D rects rather than along-wall spans.
- **`BestEdgeFor(room, width, height, …preference)`** lets a recipe ask for a wall that can take the
  item. `Bathroom()` and `Bedroom()` used to name compass directions, which is how a bath ended up
  across the door in three plans and a dresser across one in two more.

Openings in *perpendicular* walls are deliberately **not** considered. Reserving an approach strip in
front of them was tried and reverted, because a kitchen run is supposed to reach the corner beside a
cased opening and the rule pushed correct layouts apart. The two items that genuinely reached into a
neighbouring doorway are placed explicitly instead.

This is also why **openings must be declared before furniture** in every plan: `ClearRunOn` reads
`_openings`, and a recipe that runs first would see an empty list.


## What the tests pin

`PlanBuilderTests` pins the three wall derivations (shared edges collapse, partial overlaps resolve,
T-junctions split). `SampleResidencesTests` runs every check over all six samples, because the samples are
data and data has no compiler: no builder warnings, ids unique across **all** element types including
occupants and activities (`ResidenceRenderer.Mark` uses one flat dictionary, so a wall colliding with a chair
breaks selection), every opening `IsValid` on a resolvable wall, no surviving T-junction or wall overlap,
rooms tiling the footprint and matching the advertised bedroom/bathroom counts, every furniture footprint
inside its room and clear of every other, and the accessibility floor for the two care plans.

Three of these exist because the placement bugs were invisible to every other check:
`NothingStandsInADoorOrWindow` (nothing may block an opening it is tall enough to reach: the sill rule
above), `EveryWallMount_FitsItsWallAndItsRoom` (no overhanging a corner, and the vertical span sits
inside the ceiling: the check that catches `mountHeight` being read as a bottom rather than a centre),
and `NobodyEverStandsInsideTheFurniture`.

The occupancy checks are in the same file and the same spirit: the roster matches the headcount each
blurb advertises, every day covers all 1440 minutes with no overlap, every activity names a real room or
is explicitly `out`, **everyone stands inside their own room's polygon at every ten-minute step of the
day**, **nobody's centre is ever inside a furniture footprint** at any of those steps, and: for the two
care plans: no shared bathroom is ever double-occupied in the morning. That last one is an assertion
about the *argument* the sample makes, so an edit that breaks it has to say so.

The furniture check asserts centres, not full clearance, and that is deliberate: the care bathrooms are
genuinely too tight for a 0.52 m clear circle, so demanding one would fail on plans that are correct.


`PlanBuilderTests` pins multi-rectangle rooms against a **control**, which is the half that makes it
mean anything: the same two rectangles declared as two rooms must keep 4 m of wall on the line they
share, and declared as a room and a part must keep 2 m: the stretch that is still a wall to the
outside: with the shared stretch gone. Without the control, "no wall between the pieces" would also
pass on a builder that had stopped deriving walls at all. The rest pins what the union is for: six
corners and the exact area for the L, a corner-only meeting refused with a warning rather than boxed
over, an item in one piece blocking an item in the other, and a window on the part's own wall resolving
to the part's own wall.


`ResidenceStore` and the tools live in `Assembly-CSharp`, which asmdefs cannot reference, so they are
verified by driving the real filesystem and the real renderer rather than by unit test. For the samples
that meant: rendering all six through the scene's real `ResidenceRenderer` (wall/floor/furniture/mount
counts and bounds all correct, everything a labeled box as expected), round-tripping two through
`ResidenceStore.Save`/`Load` (including the `float[][]` polygons that are the reason this schema uses
Newtonsoft rather than `JsonUtility`), and branching a proposal to confirm `VariantDiff` reports the
change list in feet and inches.

