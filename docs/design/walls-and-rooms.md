# Design notes. Walls, rooms and openings

> `WallLinker` (walls divide and join themselves), `RoomRegions` (an enclosed area is a room),
> `RoomFinish` (floor finish follows room type), the corner-overlap rule in `WallMeshBuilder`, the
> deleted `structural` and `swing` fields, and how an opening is reached and sized in the inspector.
> The rules are summarised in [`.claude/rules/walls-and-rooms.md`](../../.claude/rules/walls-and-rooms.md); the reasoning lives here.

## Walls drawn by hand divide and join themselves: `WallLinker`

`PlanBuilder` derives a clean wall graph for the samples: shared edges collapse, T-junctions split, every
endpoint coincides. **Nothing did that for a wall a user drew.** `WallTool.CommitSegment` appended a
`WallDef` and stopped. Walls that crossed stayed crossed, and `WallSnapping.Result.targetWallId` was
computed for every Endpoint/OnWall hit and consumed by nobody. That matters because
`WallMeshBuilder.ComputeExtensions` closes a corner *only* where two endpoints coincide within ~1 mm, so
a mere crossing renders as a notch with no warning anywhere: the same silence that `PlanBuilder` exists
to break for the authored plans.

`WallLinker` is `FenceLinker` (`Assets/Scripts/Authoring/FenceLinker.cs`) re-expressed for walls, and it
keeps every rule that file earned: a junction is welded onto a nearby vertex of **both** sides; a
junction on a wall's own end is a shared corner, not a cut; cuts leaving less than `MinSeg` are skipped;
**parallel or collinear contact is never a junction** (drawing along a wall must not chop it up); and
**piece 0 keeps the original id**, so `VariantDiff` reads a split as "wall shortened" rather than
"deleted + added".

Two epsilons, doing different jobs. Conflating them is how the notch gets in. `ContactEps` (0.02 m) is
*detection*: is this a junction? It is tighter than `FenceLinker`'s 0.05 because half a wall thickness is
0.057. `WeldEps` (1 mm) is the *output ceiling* and must never exceed `WallMeshBuilder.Near`. But the real
guarantee is neither: each junction is computed **once** as a single `Vector2` and that same value is
written into every wall meeting there, so endpoints are bit-identical rather than merely close. The contract is `1e-6`, not `1e-3`: a regression that recomputes one side independently must be
caught immediately rather than shipping a notch.

Three things fences have no analogue for:

- **A wall carries things.** `OpeningDef.offset` and `WallMountDef.offset` are absolute meters along
  `a→b`, so every split re-homes them onto the right piece and re-runs `OpeningFit`. `a→b` direction is
  preserved on every piece, which is what keeps `materialLeft`/`Right` and `WallMountDef.side`
  meaning what they meant.
- **A cut may land in a doorway.** Refused outright, with a warning: the wall T-joins at the jamb rather
  than bisecting the door. Ten lines, and it is what keeps the re-homing edge cases rare.
- **`Uncovered`** answers "which part of this run is not already walled", over `Spans.Subtract`. Drawing
  along an existing wall adds nothing; drawing past its end adds only the overhang. This is also how the
  room stamp will share a neighbour's wall instead of doubling it.

`Relink` restores the invariant after a drag and **is idempotent**. It runs on every drag-release, and a
version that churned ids would wreck `VariantDiff`. The rule that matters most: `Relink` is a **no-op on all six sample plans**: 179 walls that `PlanBuilder` derived by a completely different
(axis-aligned, span-union) route, agreeing with the arbitrary-angle linker. That is the cheapest possible
guarantee this cannot mangle a home someone already has.

`Spans` (`Union`/`Split`/`Subtract`) was moved verbatim out of `PlanBuilder`, which is authoring-time
only, because the linker and the stamp need the same interval algebra. `PlanBuilder.TOL` is now tied to
`Spans.TOL` so the two cannot drift.

Shift keeps meaning one thing: **draw free**: no snapping *and* no linking.

## An area closed off by walls is a room: `RoomRegions`

`WallLinker` makes the wall graph clean. **Nothing then read it.** Rooms were traced by hand, corner by
corner, over walls that were already there, so every room was drawn twice, and nothing anywhere checked
that the two agreed. A room polygon that had drifted off its walls, and a walled area with no room in it
at all, were both completely silent: the exact class of failure `PlanBuilder` and `WallLinker` exist to
break for walls. The schema said so on purpose (*"deriving enclosed regions from a wall graph is fragile
while a plan is still half-traced… a deferred convenience, not a prerequisite"*), and that comment is now
gone from `InteriorTypes.cs`, `RoomTool.cs` and `PlanBuilder`'s header together.

`RoomRegions` is the inverse of `PlanBuilder`: that one derives walls from room rectangles, this one
derives rooms from the wall graph. The one sentence everything follows from:

> **An enclosed area is a room. Rooms stay first-class, stored, id-bearing, diffable records; derivation
> is an EDITING-TIME rewrite of one field (`polygon`), never a render-time computation.**

That second clause is not style. `VariantDiff` matches rooms **purely by id**, a `SensorDef` hosts on a
room id, every occupant's day addresses rooms by `ActivityDef.roomId`, `OccupancyModel`'s clear-spot memo
keys on one, and `ReportBuilder` sections by room. Derive rooms at render time and a **locked baseline**,
which has no editing session at all. Has no stable id for any of them to point at. So `Sync` writes
`polygon` and *nothing else*: `id`, `name`, `roomType` and `ceilingHeight` survive every re-derivation.

`Find` is a planar face walk, and four steps of it are each load-bearing:

- **One epsilon for welding, a bounded one for splitting.** `RoomRegions.WeldEps` is `WallLinker.WeldEps`,
  which is *verified equal* to `WallMeshBuilder.Near`: 1 mm, the distance at which `ComputeExtensions`
  actually closes a corner. Tying them makes "this area is closed" mean the same thing to the face finder
  and to what is on screen: **a gap you can see is a gap that makes no room.** A blanket `ContactEps`
  (0.02) would report a room through a visible hole. `Segments.Canonical`/`CanonicalIndex` were lifted out
  of `WallLinker` so the two files cannot drift on what counts as one junction. Interior *splitting*
  (step C) is the one place a wider bound is correct. See the post-mortem below.
- **Split at vertices, THEN de-duplicate edges**: in that order, which is the whole answer to collinear
  and overlapping walls. A wall drawn twice collapses to one edge; a partial overlap splits into matching
  halves that then de-dupe. Splitting is also not optional, for a reason that already ships:
  `WallLinker.SurvivingCuts` **refuses a cut inside a doorway**, near a wall's end, or near another
  junction, so a real T-junction can exist with no vertex on the through-wall, and the areas either side
  of it are genuinely enclosed.

  *Post-mortem (2026-08-23): the room that would not split.* Walling off part of a room sometimes left it
  one room. `WallLinker.Collect` welds a T-junction onto the nearest existing vertex within `ContactEps`
usually the **drawn endpoint**, up to 20 mm off the through-wall's centerline (`Segments.Intersect`'s
  clamped near-miss lands there too). When the cut *survives*, `SplitWall` bends the through-wall through
  that same point and the graph closes. When the cut is **refused**, the through-wall stays whole, and
  step C, documented as the rescue for exactly this, only inserted a vertex within `WeldEps` (1 mm), 20×
  tighter than what the linker guarantees. The partition endpoint had degree 1, `Prune` deleted the whole
  partition edge, and one region came back. Silently. The fix is **derivation-time and bounded per wall**:
  step C accepts a vertex within `max(WeldEps, min(ContactEps, half this wall's thickness))` of the
  segment. Half-thickness keeps the visible-gap rationale literally true: the vertex must sit inside the
  wall's rendered body, so a Shift free-drawn wall stopping visibly short still closes nothing. Welding
  (step B) stays at 1 mm, so distinct junctions stay distinct, and step C only *reuses* canonical
  vertices, so the bare-X property holds by construction. Derivation-time rather than commit-time because
  it repairs homes already saved with an off-centerline refused junction on their next `Sync`/Detect,
  where a commit-time weld-order change would help only future draws and would touch the bit-identical
  junction invariant. The "identical angles cannot occur after D" claim survives: exact ties still force
  split-then-dedupe, and near-ties at worst walk a ≤20 mm sliver face that `MinArea` swallows (sub-0.35 m²
  below 17.5 m of length). The refusals themselves now warn when they land at a genuine T (`s` and
  `len - s` both beyond `ContactEps`), where before only the doorway case did.
- **`next(e)` is the outgoing half-edge immediately CLOCKWISE from `twin(e)`**. Arriving at a vertex,
  take the leftmost turn. Then **the outer face is exactly the cycle whose `SignedArea` is ≤ 0**, which is
  a theorem of planar subdivisions rather than a heuristic: it needs no bounding-box test and it is correct
  for **disconnected** components, where two detached loops give two negative cycles and both interiors are
  kept.
- **Collinear vertices are stripped.** The walk emits a vertex wherever *any* wall meets the boundary, so a
  room picks one up mid-edge everywhere a neighbour's partition T-joins it. Those are corners of the wall
  graph, not of this room, and keeping them would make `Sync` rewrite every sample's stored polygon on
  first run for no change in shape.

Two things `Find` deliberately does **not** do. It invents no vertex at a **bare X crossing** (two walls
crossing with no shared endpoint: only reachable via Shift free-draw): `WallMeshBuilder` renders that as a
notch, so a room there would be one the plan does not draw, and the fix is one gesture away. And it never
calls `WallLinker.Relink`, which would mint new wall ids as a side effect of a *rooms* gesture.

**The property that matters most**: `Find` must reproduce the rooms of all six sample plans **exactly**,
same count, every authored corner at `1e-6`, no extra vertices, areas to `1e-4` m² (asserted by
`RoomRegionsTests`). That is the analogue of `Relink` being a no-op on all six samples, and it is worth more than any
synthetic case: `PlanBuilder` derives those 179 walls from room rectangles by a completely different
(axis-aligned, span-union) route, so an arbitrary-angle face walk agreeing corner for corner is the cheapest
possible guarantee this cannot mangle a home someone already has.

Note the tolerance is **two** numbers, not one, and collapsing them breaks something either way: a corner is
compared *directly* and is exact at `1e-6`, while an extra vertex is compared by *projecting* it onto an
authored edge, which costs ~1 ULP of that edge's length: 1.5e-6 across a 12.5 m hall, over the corner
tolerance before the geometry is even wrong. Hence `1e-5` there, still 100× tighter than the millimetre at
which the bug worth catching appears.

### What `Sync` does when the answer is ambiguous

Matching is on **`LargestInscribedCircle.center`**, not the centroid: an L- or U-shaped room's centroid can
fall outside it, while the inscribed centre is by construction furthest from any wall, which is why
`OccupancyModel` and `ViewController.StandableStart` already use it. Three cheap passes (inscribed centre →
centroid → most corners contained), no polygon boolean: the decision only has to be **stable**, not measured.

| | |
|---|---|
| Two rooms → one region | A wall was deleted. The **larger** keeps its identity, so removing the wall between a living room and a nook leaves the living room. |
| One room → two regions | A wall was drawn across it. Falls out of the same rule with no extra code: the larger keeps id/name/type, the other is owned by nobody and becomes an `Untyped` newcomer. |
| Region claims nothing | A new room, `Untyped`, unnamed, and **real from that instant**: floor, id, area, holds furniture, places people, picked up by sensor coverage. |
| Room claimed by nothing | Removed, with a warning. This is also what fires on the first wall edit in a home whose rooms were traced by hand under the old tool. Destructive, and undoable only because Sync runs inside the caller's `RecordEdit`. |

**Surviving rooms keep their existing order.** Rebuilding the list in region order was the obvious thing and
it is wrong: it reshuffles `level.rooms` on any wall edit anywhere, rewriting the stored document for no
visible change. Order is not identity.

`RoomRegions.RemoveRoom` is now the **one** room-removal cascade: `SelectTool.DeleteSelected` and
`VariantRevert.RevertRoom` both call it, where the notes previously warned about "two places that must not
disagree" and Sync would have made a third. A `SensorHost.Point` water sensor still survives its room.

### Where `Sync` is called, and why never from `VariantRevert`

`WallTool.CommitSegment` (**both** paths, Shift means no snapping and no dividing, not no rooms),
`SelectTool.DeleteSelected` for `Kind.Wall`, and the Rooms rail's **Detect rooms** button. Each inside the
caller's existing `RecordEdit`, so one undo takes the wall *and* the rooms back. Not on wall thickness or
height. Centerlines do not move. Not from `PlanBuilder`, which authors stable `r_<key>` ids that occupant
schedules reference by name. **Not on load or in `Migrate`**, which would rewrite every stored polygon on
every open.

And **not from `VariantRevert`**, for three reasons in order of force:

1. It breaks the property that file is *specified* by. Reverting a deleted wall would have Sync re-split the
   merged region and mint a **fresh guid**, so the diff returns Removed + Added: the nastiest failure the revert property guards against: *"a delete-plus-add leaves the diff empty now and lies on
   the next comparison."*
2. It is unnecessary. `RevertRoom` already restores the whole `RoomDef` including its polygon via `Copy`, and
   `RevertWall` restores the wall, so the diff is empty **by construction**. Two computations of one truth is
   how this codebase gets its 57 mm notches.
3. A locked baseline has no editing session in which to derive anything.

Corollary: **`HomeRenderer` keeps rendering `level.rooms`**, never `Find`.

## The floor finish was the room type wearing a picker: `RoomFinish`

`RoomDef.floorMaterial` and `ceilingMaterial` are **gone**. Not deprecated-but-declared: the rule that kept
`HomeDoc.underlay` declared is that deleting it drops data that cannot be recovered, and a floor finish can,
`RoomTool.SetType` overwrote the user's choice every time the type changed, `PlanBuilder.FloorFor` derived it
from `roomType` for all six samples with the comment *"Matches RoomTool's defaults, so a sample room is
indistinguishable from a drawn one"*, and the two never diverged. `ceilingMaterial` was a constant wearing a
field's clothes. Hard-coded `"ceiling_white"` at both write sites, never diffed. Leaving them declared would
have been an active trap: `CompareRooms` would go on reporting *"new floor finish"* for a field no UI can set.
(Newtonsoft's `MissingMemberHandling` defaults to `Ignore`, so homes already on disk simply drop the keys.)

`RoomFinish.FloorMaterial(roomType)` replaces both. It lives in `CXRAuthoring` rather than on the palette for
the split `MeasureUI`/`Units` already keeps. Domain knowledge that must be testable, while
`InteriorMaterialPalette` is a ScriptableObject in `Assembly-CSharp` the EditMode tests cannot reach. An
unknown type falls through to `floor_untyped`, which is the graceful degradation that is the whole stated
reason `RoomType` is string constants and not an enum. `SampleHomeInstaller.VerifyFloorFinishes` warns at seed
if any type maps to a material the palette lacks: the same cross-assembly problem `SampleFurniture` has,
solved the same documented way, because the failure is otherwise silent: `Get` falls back to `defaultFloor`
and the room just renders as vinyl.

**Hue is the separator, and the band is what makes room for it.** With no tonemapping, clipping is **per
channel**, so no floor channel may exceed **0.74**; floors must sit clearly above `Wall_Edge` (0.38, looking
down at the plan, the wall cap is the *entire* visible surface of a wall). `Tile_Bath` is a
**wall** finish and must never be used as a floor.

The floors used to be bounded above by `Paint_White` as well, and they are not any more: the wall paints
came down to 0.50 so that light label text reads on them (*Nothing in the dwelling is white any more* in [view-and-people.md](view-and-people.md)), which puts most floors **above** the wall face rather than below it. That inverts a ladder and
breaks no contract, because the contract below is stated against the wall **cap**, and the cap did not
move: from overhead the cap is the whole of a wall, so `Wall_Edge` is the only wall tone a floor is ever
actually seen beside.

The first version of this table said the same thing and then did not do it: every floor landed in
[0.52, 0.74] and **five of the twelve types stayed neutral greys**. Untyped, carpet, entry, storage,
office. That is exactly the arrangement that reads as *"the floor is the same colour as the wall"*, and it
is measurable rather than a matter of taste. Against the colours the scene's own lighting actually
produces, `floor_carpet` sat **ΔE 7** from the wall cap, `floor_storage` ΔE 8, `floor_entry` ΔE 9, and
`floor_entry` sat **ΔE 0.9** from `floor_storage`, which is the same colour twice.

So every floor now carries a real hue, and **the ladder is checked as a whole rather than row by row**:

> No floor is within **ΔE 20** of the rendered wall cap, and no two floors are within **ΔE 14** of each
> other. CIELAB, computed on the colour after the scene's lighting, not on the raw albedo.

Those two numbers are the contract. The worst pair is now hall/entry at ΔE 15 and the worst floor-to-cap is
untyped at ΔE 20, against 0.9 and 7 before. Change a row and re-check the whole pair table, because a floor
is only ever seen beside another floor and beside a wall.

| Type | materialId | Type | materialId |
|---|---|---|---|
| `untyped`, `other` | `floor_untyped` cool grey | `hall` | `floor_hall` warm sand |
| `bedroom` | `floor_carpet` dusty rose | `entry` | `floor_entry` clay brown |
| `bathroom` | `floor_bath` blue tile | `laundry` | `floor_laundry` mint green |
| `kitchen` | `floor_vinyl` pale yellow vinyl | `storage` | `floor_storage` muted violet |
| `living` | `floor_oak` oak | `office` | `floor_office` slate blue |
| `dining` | `floor_dining` walnut | | |

`floor_untyped` is the **only** floor left neutral, and deliberately so: it is the one that carries no claim
about the room, so it gets no hue either. It is separated by value alone, which is why it is also the one
sitting nearest the threshold.

`RoomType.Untyped` is new and leads the list. It is distinct from `Other`: `Other` is a deliberate "none of
these", `Untyped` means "nobody has said yet", which is why the Rooms tool draws it dashed.

**`SampleHomes.Generation` is deliberately NOT bumped.** The samples' walls, rooms, ids and polygons come out
byte-identical; the only data change is two fields nothing reads ceasing to be written, and the visible tint
is derived at render time from `roomType`, which all six already set on every room. Bumping would re-install
six homes on every machine for no visible difference. (The standing contract is "bump whenever a plan
changes": this is a schema change, not a plan change.)

### A room can no longer be deleted on its own

The walls still enclose the area, so the next `Sync` would put it straight back with a fresh guid, losing the
name and type and reporting remove+add in every open comparison. So `Kind.Room`/`Floor`/`Ceiling` are out of
`SelectTool.DeleteSelected`, the Delete button and the Delete key are gated off for them, and where the button
was there is now a `UITheme.Glyph` saying a room follows its walls. `SelectionOverlay.DrawRoom` loses its
per-vertex drag handles for the same reason: a handle that cannot be dragged is a promise the tool does not
keep, while keeping the haloed outline, which is what answers *"which one"*. `VariantRevert`'s removal path
stays: that is not a user gesture.



## Walls, openings, and the two deliberate simplifications

`WallLayout.Build` decomposes a wall into the solid boxes that remain once its openings are removed:
full-height panels between them, a header above each, a sill below each window. **Openings are never
subtracted with CSG**: an opening is simply a gap the box list skips.

`WallMeshBuilder` closes corners by **extending each wall half a neighbour-thickness past any shared
endpoint, so the boxes overlap**, rather than mitering them. Invisible on opaque solids and robust at
every angle and valence. *Known limitation:* cutaway views, transparent walls, or geometry exported for
a contractor would reveal the overlap and would need real mitering.


## …and the overlap has to be an overlap, not a tie

"Invisible on opaque solids" holds only while one box is genuinely *in front of* the other. Two coplanar
faces at the **same** depth are not hidden, they z-fight, and *"half a neighbour-thickness"* landed on
that tie twice, on all 358 wall ends across the six samples:

- **An extension that reaches the neighbour's far face exactly** puts the wall's end cap on the plane of
  that neighbour's face. The cap is `SUB_EDGE` (`Wall_Edge.mat`) and the face is `SUB_LEFT`/`SUB_RIGHT`
  (a paint), so every L corner and every T-stem drew **two different materials at one depth, over a
  full-height strip a wall thick**. Extensions now stop `WallMeshBuilder.JunctionBias` (1 mm) short, which
  puts the cap *inside* the neighbour, where it is simply occluded. Sub-mm is not slop here: the tie is
  the failure, and 1 mm is ~10× the worst depth resolution this scene's near/far can produce.
- **A collinear neighbour was treated as a corner.** It is not: the run continues and the two boxes
  already abut. Extending them only buried a wall's thickness of duplicate coplanar face in the
  neighbour: invisible while both pieces carry the same finish, a flickering band the moment they do
  not, which is one repaint away on any wall a room boundary splits. And splits are the *common* case,
  not the odd one: `PlanBuilder` derives walls that way and `WallLinker` enforces it, so **208 of those
  358 ends were collinear** and now extend by nothing at all.

Collinearity uses `WallLinker.MinJunctionSin`, the same threshold that file uses to decide a contact is
overlap rather than a junction, so the two cannot drift on what counts as one run.

What this costs is a **1 mm² notch at the tip of an outside corner**. Verified by gridding every
junction in all six plans at 0.5 mm and diffing coverage against the old rule: four such tips per
dwelling (its four outside corners), nothing else uncovered anywhere, nothing lost deeper than the bias.

## `WallDef.structural` is gone

A wall carried a `structural` flag. Informational only, set by a toggle in the wall tool and the
inspector, surfaced as a "Structural wall" label in the rail, the selection readout and the change
list. Nothing enforced it, nothing measured it and no rule read it, so it asserted something about
the building that the app could neither check nor act on, in a tool whose audience is care staff
rather than architects. It is **deleted rather than deprecated**, on the `RoomFinish` precedent:
leaving the field declared would keep `CompareWalls` reporting *"marked structural"* for a flag no UI
can set. Newtonsoft's `MissingMemberHandling` defaults to `Ignore`, so homes already on disk simply
drop the key.

## …and so is `OpeningDef.swing`

A door carried a swing, which side it is hinged on, whether it opens in or out, or slides. Four chips
in `OpeningTool`'s rail (`L in` / `R in` / `Slide` / `Pocket`) set it at placement, and **nothing could
change it afterwards**: the inspector never offered the control, so a door was frozen at whatever it was
drawn with, exactly as a window's sill was before `FitVertical` was wired up.

Nothing draws it either. An opening is a gap `WallLayout` skips. There is no leaf, no arc, no hinge
anywhere in the renderer, in `SelectionOverlay` or in the report, so the swing was invisible in every
view of the home, in a tool whose whole argument is made by looking at the plan. Its one consumer was
`HomeMetrics.ClearWidth`, and that is now the honest form of the same estimate: **a door loses its leaf
and stop, anything with no leaf in it loses nothing**. The stored `OpeningDef.clearWidth` is untouched
and still beats the estimate, which is the field to reach for when a real doorway was measured on site.

**Deleted rather than deprecated**, on the `WallDef.structural` and `RoomFinish` precedent: leaving it
declared would keep `CompareOpenings` reporting *"swing L in → Slide"* for a value no UI can set.

**`SampleHomes.Generation` is deliberately NOT bumped.** No sample ever passed a swing: all six took
`PlanBuilder`'s `LeftIn` default, whose clear width is `width − 0.060` and still is. The geometry, the
ids and every derived figure come out identical, so a refresh would re-install six homes for no change.

The one behaviour that does move: a door a **user** placed as `Slide` or `Pocket` had a wider derived
clear passage (half its width, or `width − 0.030`), and now reads as a swing door like every other. A
measured `clearWidth` on that door still wins, which is the route back.


## An opening is reached through its wall, and every dimension is a free number

Two things about doors and windows were backwards, and they turn out to be the same thing.

**An opening is not something you can point at.** It has no geometry. It is a gap `WallLayout`
skips, so `HomeRenderer.RenderOpeningHandles` fabricates an invisible `BoxCollider` filling the
void, built `0.02 m` proud of the wall on both faces *specifically so it beats its host wall in the
raycast*. Clicking a doorway therefore selected the hole rather than the wall the hole is in, and
there was no route from a wall to the openings it hosts: `DrawWall` printed a bare **count**, so it
told you a wall had three doors and gave you no way to reach any of them.

So `SelectTool` now **redirects** an opening hit to `marker.parentId`, the host wall id `Mark`
already stores, and the wall's rail carries a `UITheme.Foldout` list of its openings. Labelled by
`SensorPose.OpeningLabel` ("Bathroom door", "Bedroom 2 window"), ordered by
`WallLayout.OpeningsFor`, which already sorts by centerline offset. Hovering a row haloes that
opening in the plan, which is what answers *"which one"* when a wall has two doors into the same
room.

Five things this needs that a first pass would get wrong:

- **Redirect, do not ignore.** `PickElement` returns the **first** marker along the ray and stops.
  Skipping opening hits would fall through to nothing. Clicking a doorway would select *nothing*,
  which reads as the tool being broken rather than as a rule.
- **The handle GameObject stays.** `HomeEditController.FocusElement` resolves ids through
  `HomeRenderer.GetGO` → `_byId`, which only `Mark` populates, so deleting it would break **F** on a
  selected opening and `CompareTool`'s focus on every opening change row. `FocusPoint`'s
  degenerate-bounds fallback was written for this renderer. It is no longer a selection handle; it is
  what `_byId`, the redirect and the overlay all still need.
- **The list is drawn on a LOCKED baseline too**, above the `IsLocked` early return. Locked is the
  default state of every home in the library, and reading which doors a wall has is inspecting, not
  editing: with the scene click gone, gating it would make an opening unreachable in the common case.
- **The list is drawn by `DrawOpening` as well as by `DrawWall`.** Selecting an opening replaces the
  wall's rail with the opening's, so a list drawn only by `DrawWall` would vanish the instant you
  clicked a row, and flicking between the two doors in one wall would mean reselecting the wall each
  time. The row for the opening being edited draws as the active one.
- **`HoverOpeningId` is cleared at the top of `OnGUI`**, not by the list that sets it. Clearing it
  where it is set leaves a highlight burning when the selection changes to something that draws no
  list at all. It works with no frame of lag because `DrawRightRail` runs *before* the selection
  overlay in the same pass, and that overlay is deliberately outside the `PointerOverUI` guard: the
  cursor is on the rail precisely while someone reads the row it is describing.

**And every dimension is now a free number.** The five inch presets are gone from both rails. They
were defended on the grounds that this is how doors are specified, but a measured field had been
added directly beneath them, so the rail offered one dimension twice in two idioms, and no chip can
express the 34 1/2" doorway measured off a real home. Height and sill were worse: `OpeningTool`
offered both at placement and the inspector offered **neither**, so a window's sill was frozen at
whatever it was drawn with. Both now run through `OpeningFit.FitVertical`, which had existed since
the file was written and was called by nobody. Position along the wall joins them, because an
opening has no drag gesture anywhere and the rail is now the only way to nudge one.

### `OpeningFit.MaxWidth`: a control that cannot ask for the impossible

`Fit` **refuses** an over-wide request rather than clamping it, which is right for a placement and
wrong under a drag-scrubbed field: the number in the box climbs while the document silently declines
to follow, so the control and the model disagree with nothing on screen saying so. Softening `Fit`
would break the placement path, so the **control is bounded instead**: `MeasureUI.Length` already
takes a `max`.

`MaxWidth` and `Fit` are the same question read from opposite ends ("how wide may this be here?"
versus "where may something this wide sit?"), so the neighbour walk is extracted into one private
`FreeSpan` that both call. Two copies would be two chances for the control to offer a width the fit
then rejects, which is the single failure this arrangement exists to prevent, and it is what
`MaxWidth_IsAlwaysAcceptedByFit` pins: over a grid of offsets, neighbour sets and edge reserves, a
width `MaxWidth` allows is always a width `Fit` accepts. A 1 mm drift in either direction fails it.

`OpeningTool`'s own width field is deliberately **not** bounded this way: the hovered wall changes
every frame, so a max derived from it would move under a drag already in progress. There the live
refusal is already honest: the preview turns red and the glyph says why.


## What the tests pin

`WallMeshBuilderTests` pins the junction extension from both directions, because the two failure modes
are opposites and a plausible-looking "cleanup" hits one or the other. Too little and a corner keeps a
57 mm notch. Hence the asymmetry cases (the extension follows the *neighbour's* thickness, and the
thickest one at a three-way). Landing dead on and it z-fights. Hence a case asserting the extension is
strictly **less than** the far face it aims at, and the collinear / T / crossroads cases asserting a
continuing run extends by nothing.

