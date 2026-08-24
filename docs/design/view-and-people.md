# Design notes: the scene, the camera, and the people in it

> Why the scene is exposed the way it is (no tonemapping, the value ladder), the two view modes and
> the free-look camera, why only the shell is solid, and the occupant model. Placement, the clock,
> and the timeline bar. The rules are summarised in [`.claude/rules/view-and-people.md`](../../.claude/rules/view-and-people.md);
> the reasoning lives here.

## The scene has to be exposed, or every surface is the same white

There is **no tonemapping** (`DefaultVolumeProfile`'s `Tonemapping` mode is `None`) so a shaded value
over 1.0 is not rolled off, it is clipped. The scene was lighting an up-facing surface with
`ambientSkyColor` (0.62, linear 0.342) *plus* a 1.1-intensity sun at 52 deg (N.L 0.788), i.e. **1.21x**,
which puts every albedo above about 0.70 past 1.0. That was most of the interior palette: walls (0.93),
ceilings (0.96), wall caps (0.80) and the ground pad (0.86) all clipped to the **same pure white**, and
what survived was crammed into the top of the range: the pad at 0.93 against a 0.94 camera background
and 0.87 wall caps. Looking straight down is the worst case by construction: the ground and the wall
tops are horizontal surfaces at an identical angle to the sun and there is **no shading difference
between them at all**. Albedo is the only thing telling a wall from the ground.

So the ambient is retuned to put the total near **0.83x**. **Lowering it is not a style preference and
raising the sun back to 1.1 reintroduces the bug**: the headroom is what the palette below spends.

The palette is then a value ladder with real gaps in it, so each surface is told apart by its own tone
rather than by a 7% difference near white:

| Surface | Albedo | Renders about | L\* |
|---|---|---|---|
| `Ground_Pad` | 0.20 | 0.18 | 19 |
| `Wall_Edge` (wall tops, end caps, opening reveals) | 0.38 | 0.35 | 37 |
| `Paint_Warm` (wall face) | 0.50 / 0.45 / 0.38 | 0.46 / 0.41 / 0.35 | 45 |
| `Tile_Bath` (wall face) | 0.42 / 0.48 / 0.50 | 0.39 / 0.44 / 0.46 | 45 |
| `Paint_White` (wall face) | 0.50 / 0.49 / 0.46 | 0.46 / 0.45 / 0.42 | 48 |
| `Floor_Carpet` | 0.68 / 0.50 / 0.50 | 0.62 / 0.45 / 0.42 | 52 |
| `Floor_Oak` | 0.72 / 0.50 / 0.28 | 0.66 / 0.45 / 0.23 | 53 |
| `Ceiling_White` | 0.55 | 0.51 | 53 |
| `Floor_Vinyl` | 0.74 / 0.70 / 0.44 | 0.68 / 0.63 / 0.37 | 66 |
| camera background | -- | 0.94 | 98 |

Three of those carry the weight. **The ground pad is dark**, which is what the rest of this file has
always assumed when it talks about text and outlines reading "against the dark ground pad", and it is
what makes the dwelling a lit object standing on a backdrop instead of a pale shape on a pale field.
**`Wall_Edge` is a mid-dark grey**, because from overhead the cap is the *entire* visible surface of a wall, so
it has to sit clearly between the pad below it and the floors beside it; at its original 0.80 it was a
near-white on a near-white and the walls simply were not there.

And **nothing in the dwelling is white any more.** Exposing the scene stopped the palette *clipping* to
white but left it *reading* as white: `Paint_White` at 0.93 and `Ceiling_White` at 0.96 still rendered at
L\* 85 and 88 against a 0.94 camera background, so the walls, the ceilings and the page behind them were
three shades of one near-white and the model had no silhouette. This is a second, independent constraint
from the clipping one: the headroom argument above is satisfied at 0.96, and 0.96 was still wrong.

That pass took them to 0.72 and 0.76, and a **third** constraint took them the rest of the way down to
**0.50 and 0.55**: *the text drawn over a wall has to be readable on it*. `ResidenceRenderer.AddLabel` names
every item, device and resident in near-white (0.98) with `LabelOutline`'s dark stroke, and the most
common thing behind a label is the wall directly behind the furniture it names, which is the whole
reason that label is light rather than ink in the first place. At L\* 66 the paint was near enough to
the glyphs that only the stroke was doing any work, and a stroke is a fraction of a character wide. At
L\* 48 the label sits ~50 points of lightness above its background and reads on its own. `Paint_Warm`
and `Tile_Bath` came down with it, in the same proportion, because they are wall faces too and leaving
either bright just moves the unreadable case into the warm rooms and the bathrooms.

**`Wall_Edge` deliberately did not move.** It is the *cap*, and from overhead the cap is the entire
visible surface of a wall, so it is the tone every floor is measured against, and the ΔE contract in [walls-and-rooms.md](walls-and-rooms.md) is
stated against it. Lowering the faces to 0.50 leaves ~11 points of L\* between face and cap, which is
still a clear step, and squeezing the cap down after them would run it into the 0.20 ground pad.

Every material in that table is ResidenceViz's alone: `Ground_Pad` is used only by the scene's `GroundPad`,
and `Wall_Edge`, `Paint_White`, `Paint_Warm`, `Tile_Bath` and `Ceiling_White` are referenced by nothing
but `InteriorMaterialPalette.asset`, so the legacy Site scenes are untouched by construction, the same
split `MeasureUI` keeps for units.


## View modes

There are **two**, and the app opens in the first.

| Mode | Behaviour |
|---|---|
| **Overview** *(default)* | Perspective free-look, ceilings hidden. The drawing, measuring and "show the family the layout" view. **Right-drag turns the camera in place with the cursor captured**. Hidden and locked for the duration, restored to the pixel it was pressed at on release. Middle-drag pan (captured too), scroll dolly, WASD relative to facing, and **Q/E to lower and raise**. |
| **Walkthrough** | First person with collision. **Standing 1.60 m / Seated 1.19 m toggle**: the seated setting shows a wheelchair user's actual sightline over counters and through windows. **R** returns you to a clear spot. |

### There used to be a third, and free-look is why there is not

**Plan** was an orthographic top-down camera that could pan and zoom and **nothing else**: no turning,
no height, no perspective, and it was the mode the app opened in. What it added over the overview was
the orthographic projection itself: parallel walls stay parallel, so a wall never leans and nothing is
foreshortened.

That is a real difference and it is a small one, because the overview already reaches the same viewpoint.
`FreeLook` opens pitch to **89°**, so looking straight down at the plan is a *gesture* there rather than a
mode: one you can enter and leave without changing what the camera is, and one that keeps the turning,
the height and the WASD travel the drawing tools are used through.

Against that, being a mode cost: a segmented cell in a command bar that is already tight at 1280 px; a
second set of camera rules (`UpdatePlan`, `_orthoSize`, `FOCUS_ORTHO`, the orthographic branches of
`FrameContent` and `FocusOn`) to keep in step with the orbit path forever; and: the one that decided it
a **default state in which the camera cannot be turned at all**, which is the first thing anybody
tries. Someone who wants the true orthographic reading of a plan has one that is better than a live
camera: the report's plan shot, which is orthographic, framed on the whole residence, and printable.

So `Mode.Plan` is **deleted rather than deprecated**, on the `WallDef.structural` precedent, and
`Mode.Overview` leads the enum, which is what makes it the default: `Start` calls `SetMode(Mode.Overview)`,
and `ResidenceViz.unity`'s camera is serialized perspective so the first frame is not orthographic either.
`ViewController` no longer sets `cam.orthographic` to true anywhere.

**`ReportCapture` still takes an orthographic plan shot, and must.** It owns its own hidden camera
(`ReportCapture.Plan`), which was never `ViewController`'s mode: the report leads on a top-down image of
the whole residence, and that is a framing decision about a document rather than a way of looking around one.

`_pivot` therefore carries a live Y, and every path now reads it.

### The overview was a turntable, and a turntable is not a camera

Right-drag used to *orbit*: `_yaw`/`_pitch` spun around `_pivot`, so the house rotated in place while
the viewer stood still. That was inherited wholesale from `EditController` on the grounds that anyone
who had used the Site tool already knew it, but Site looks at a site from **outside**, where a
turntable is the right gesture, and here you are looking **into** a dwelling. Being unable to turn
your head, with a visible cursor that runs off the window edge and takes the drag with it, reads as
unnatural next to any game.

`FreeLook` rotates about the **eye** instead, and the whole trick is that it does so as a round trip
rather than by changing what the state means:

```
eye    = _pivot + Euler(_pitch, _yaw, 0) * (0, 0, -_distance)     // == cam.transform.position
…apply the delta to _yaw / _pitch…
_pivot = eye + Euler(_pitch, _yaw, 0) * (0, 0,  _distance)        // eye + forward * distance
```

`_pivot` / `_yaw` / `_pitch` / `_distance` stay canonical and `ApplyOrbit` is untouched, which is why
`FocusOn`, `FrameContent`, `KeyPan` and `ClampLift` all keep working with no changes at all, and why scroll still dollies along the view axis, since the eye sits at `pivot − forward × distance`.
Pitch opens up to **`[-85°, 89°]`**: the old floor of 5° meant you could never look level, let alone
up, which was half of why the gesture felt wrong. Q/E still own vertical, and that is what keeps W/S
horizontal rather than burrowing into the floor whenever you look down.

**Sensitivity is degrees per PIXEL, never per second.** `Mouse.delta` already accumulates a frame's
travel, so a `Time.deltaTime` in there is what would make it framerate-dependent: the inversion of
the usual rule, and easy to "fix" wrongly.

#### The cursor capture, and why the release path is the paranoid one

Pressing right locks the cursor (`CursorLockMode.Locked`) and hides it; releasing restores it and
**warps it back to where it was pressed**, because Unity re-centres the pointer when a lock drops and
a cursor that teleports to the middle of the screen is its own kind of wrong. Middle-drag pan captures
too: no drag should be cut short by the edge of the screen.

A capture that outlives its gesture is an invisible cursor with no way back, so `StillDragging` ends a
drag on `isPressed` going false rather than on `wasReleasedThisFrame` (a release while the window is
unfocused never fires the edge event) **and** on `Cursor.lockState` no longer being `Locked` (Esc in
the Editor drops Unity's lock behind our back). `SetMode`, `OnDisable` and `OnApplicationFocus(false)`
all end drags as well. Right and middle can be held at once, so `SyncCapture` is the single place that
decides to release: an end-of-drag that released on its own would drop the cursor mid-way through the
other gesture. And the first frame of a capture **swallows its delta**: locking warps the OS cursor to
the centre, and that warp arrives as one large delta that would snap the view a quarter turn.

#### Drags latch, which is what lets the camera be gated at all

`ViewController` used to poll `isPressed`, so a press landing on a rail also drove the camera, and
scrolling a list zoomed. It now takes `EditController`'s rule: a drag only **begins** in the 3D view,
and once begun keeps going wherever the cursor travels. That distinction is load-bearing,
`ResidenceEditController`'s gizmo tick is deliberately *not* gated outright, because the handles must keep
up with a look already in flight. Reading `PointerOverUI` needs a `ResidenceEditController` reference,
found in `Awake` the way `residenceRenderer` is, so no scene wiring changed.

`TypingInUI` (`GUIUtility.keyboardControl != 0`, `EditController`'s idiom) now guards `KeyPan`,
`KeyLift` and the walkthrough's keys. ResidenceViz had no equivalent, so typing `wardrobe` into any field
flew the camera, which stopped being a curiosity the moment WASD flew rather than nudged.

### The descent limit is on the camera, not the pivot

`ClampLift`. The camera sits `sin(pitch) × distance` above its target, so a pivot stopped at floor
level still leaves the camera metres over the roof and holding Q appears to stop while you are looking
down at the building from outside. Deriving the pivot's lower bound from where it puts the *camera*
means Q descends all the way to standing height, and means the pivot itself is free to go below the
floor, which is exactly what circling a target across the room at eye level requires. It re-runs
every frame, because looking around or zooming changes the offset and a bound computed once would be
stale the moment either did.

**Its upper bound switches on which of the two is higher, and that is free-look's doing.** Capping
`_pivot.y` at `elevation + 25 m` is right while the camera is above the pivot. Looking down, the
clamp is bit-identical to what it always was, so zooming out at a long distance still lifts the camera
freely. But looking **up** puts the pivot above the eye by construction, and capping it there would
drag the camera down out of a gesture that must not translate it at all. So above level the cap moves
onto the eye instead, which is what stops E from flying to space while you look at the ceiling. A
plain eye cap in both directions is the obvious simplification and it is wrong: it fights zooming out.

**Focus.** `FocusOn(point, closeUp)` centres the overview camera and, with `closeUp`, pulls in to
5 m. It only ever *tightens*: someone already studying a bathroom must not be yanked backwards by
asking where a resident is. `ResidenceEditController.FocusElement` is the single entry point: the People
view's roster row, the People tool's rail roster, and **F** (frame the current selection) all go
through it, and it reports why nothing happened: a resident who is out has no marker to point at,
and the walkthrough camera is a body standing in a room, not a viewpoint that may be teleported.
Clicking a person's *marker in the plan* deliberately does **not** focus: you are already looking at
them, and closing in would throw away the framing you clicked from.

### Only the shell is solid

The walkthrough was unusable and it was not a tuning problem. **Most colliders in a ResidenceViz scene are
not physics at all**: an opening has no geometry, furniture renders as a labeled massing box, an
occupant is a tinted capsule; each carries a collider purely so `ResidenceToolContext.PickElement`'s
raycast can find it. The opening handle fills its void *exactly*, so left solid it makes **every
doorway a wall** and you cannot leave the room you spawn in.

`ResidenceRenderer.PickOnly` marks those subtrees `isTrigger`. A `CharacterController` ignores triggers,
and `Physics.RaycastAll` still hits them (`m_QueriesHitTriggers: 1`, and `PickElement` is the only
physics query in ResidenceViz), so everything stays clickable. What stays solid is walls, floors and
ceilings: the shell, and the only thing that should ever stop you.

Three further things, all in `EnterWalkthrough`: `skinWidth` is set to a tenth of the body radius
(Unity's default 0.08 against a 0.2 m radius is what jitters and snags) and `minMoveDistance` to 0
(the default silently discards a frame's movement); and the body no longer spawns at `_pivot`, which
is the framing centre and as likely to be a wall as a room: `StandableStart` takes the centre of the
room's largest inscribed circle, which is by construction the point furthest from any wall.
`LargestInscribedCircle` ignoring furniture is exactly right now that furniture does not block.


## People, who the residence is for

A plan with nobody in it cannot make the argument the tool exists to make. Every accessibility claim
here: two wheelchairs passing in a corridor, one roll-in shower serving five residents at half past
seven, the accessible bedroom being at the far end from the accessible bathroom. Is a claim about
people moving through the plan over a day.

- **`OccupantDef` hangs off `VariantDef`**, not `LevelDef`, for the same reason `exterior` does: who
  sleeps in which bedroom is a thing a proposal *changes*. `NewProposalFrom` preserves element ids, so
  `VariantDiff` reports *"Alice: sleeps in Bedroom 1 (was Bedroom 3)"* rather than a delete plus an add.
- **`ActivityDef` carries both a kind and a `roomId`.** The kind colours the timeline block and
  suggests a room when a block is created; the `roomId` is what actually places the person. A
  five-bathroom care home has no single right answer to "which bathroom", so the guess never wins.
  `roomId` null means away from residence and the marker hides.
- **Times are minutes from midnight, 0-1439.** `end` before `start` wraps past midnight, which is how
  sleep is expressed; equal ends mean an all-day block. `Clock` is the only place a time becomes text
  (the `Units` rule, applied to time), and it follows `Units.Display` for 12- vs 24-hour.
- **Positions are derived, never stored**: `OccupancyModel`. A stored coordinate would leave people
  inside walls the moment a proposal moved one. Several people in one room are **fanned onto a ring** so
  three residents at dinner are three markers, not one box. A schedule *gap* falls back to the most
  recently started block rather than making someone vanish.

### Placement is furniture-aware, and that is the whole point

The first version tested one thing (`PointInPolygon(spot, roomPolygon)`) and it was not enough. An
unanchored activity fell back to `ResidenceMetrics.LargestInscribedCircle(room).center`, which is computed on
the **bare** room (it says so at `ResidenceMetrics.cs:146`), so it happily stood Maya inside the studio's
armchair for four hours of her day. Three rules now, all in `OccupancyModel`:

- **Posture comes from the ITEM, not the activity kind.** `PostureFor(prefabType)` sorts catalog ids
  into `On` (beds, seating, toilet, tub (you occupy it, so it is not its own obstacle), `At` (tables) 
  the edge, facing in), and `InFrontOf` (appliances and fixtures; the default, and what everything did
  before). A `relax` block anchored to a range should still stand at the range, which is why the kind is
  the wrong handle.
- **`IsClear(p, radius, level, poly, ignore)` is the acceptance test**, and it checks two things the old
  code checked neither of: that the marker's *disc* fits inside the room net of **half the wall
  thickness**. Room polygons run along wall centerlines, so a bare point test leaves people standing in
  the plaster, and that it clears every *other* item taller than 0.15 m. Anything shorter is something
  you stand on, like a roll-in shower.
- **It relaxes rather than refusing.** `TryFindClearSpot` grids the room for the free point nearest the
  inscribed centre; failing that it retries at a smaller radius, then at zero. A 1.8 × 2.0 m care
  bathroom holding a tub, a toilet and a basin has no 0.52 m clear circle anywhere in it, and hiding a
  marker would be a worse answer than a tight fit.

Two consequences worth knowing. `WheelchairRadius` is 0.45, not the 0.61 half-length of the chair pad:
asking for 0.61 in every direction demands a 1.22 m clear circle and pushes every wheelchair user back
out to the room centre. And co-anchored people now spread along the item's width, so a couple reads as
two markers on the bed rather than one capsule inside the other: the fan-out used to sit *after* the
anchored early return and never ran for them.

The grid search is **memoised** (`InvalidateCache`, called from `ResidenceRenderer` on rebuild, and scoped to
the `LevelDef` because room ids repeat across residences). Not an optimisation: `PoseAll` runs every simulated
minute *and* from `CurrentPoses()` on the `OnGUI` path, several times a frame.

### The clock is on `ResidenceRenderer`, and that is not an accident

`OccupancyClock` is a **plain class**, not a MonoBehaviour, held by `ResidenceRenderer` and ticked from its
`Update`. Two places it looks like it should live and cannot:

- **Not in a tool.** `ResidenceEditController` gates `IResidenceTool.HandleInput` on `!PointerOverUI`, so a clock
  ticking there stops dead whenever the cursor rests on a rail, which is where the cursor is while
  someone watches the timeline.
- **Not in the document.** Undo snapshots the whole `ResidenceDoc`, so a clock stored there would make every
  tick an undo entry and every playback an unsaved change. Scrubbing must never dirty the file.

`Advance` returns true only when the whole minute changes, and `UpdateOccupantPoses` writes transforms
on cached markers: no teardown. A full `Rebuild()` per frame is what this exists to avoid. Being a
plain class also means **the whole feature needed no scene edit**: `ResidenceRenderer` was already wired.

Markers are deliberately low fidelity, matching the labeled-box convention: a tinted capsule of the
right height with the person's name and current activity floating over it. `usesWheelchair` gives a
seated marker topped out at `EYE_HEIGHT_SEATED` over a wheelchair-sized pad: the same number the
walkthrough Seated toggle uses, so the two views agree about how tall a wheelchair user is. There is no
walking and no animation; the value is in seeing five people want one bathroom, not in watching anyone
traverse a corridor.

### The timeline lives along the bottom: `TimelineBar`

This was `PeopleDashboard`, a panel floating over the middle of the scene that had to be dismissed to
see the plan it was describing, which is backwards. The whole argument the timeline makes is *about*
the plan: five residents wanting one bathroom at half past seven is a claim you have to be looking at
the bathroom to feel. So it is a permanent strip along the bottom, and the plan is never covered.

Still a plain class like `UITheme`, drawn from `ResidenceEditController.OnGUI`, everything derived and
nothing stored. **Its rect must stay in the `PointerOverUI` test**. Without that, scrubbing the clock
also starts a camera drag and every click falls through to the scene.

- **Full width, not the gap between the rails.** At 1280 that gap gives a 24-hour chart 30 px an hour;
  the whole window gives 53. The chart is the content.
- **Collapsed is 92 px**. Hour ruler, one 3 px lane per person (`DrawStrip`, up to four), and the
  transport. **Expanded** it grows upward into the per-person gantt the panel used to be. The rails
  always stop at the *collapsed* height, so expanding draws over their lower ends rather than
  re-laying-out everything else on screen.
- **The toggle is deferred** (`_pendingTimelineToggle`, applied in `Update` beside `_pendingStage`).
  Flipping it inside `OnGUI` changes both the bar's height and its control count between the layout and
  repaint passes: the `Mismatched LayoutGroup` this codebase's deferral discipline exists to prevent.
  `_pendingLeftToggle` and `_pendingViewMode` were added for the same reason.

**One clock, not three.** The time used to be on screen three times and the transport twice: the
People rail, the dashboard header, the dashboard footer. The footer is the survivor; `PeopleTool`'s
`DrawClock` and its open/close button are gone, along with the top bar's "People view" chip (the bar is
permanent and carries its own chevron, and the command bar needed the width). `ShowPeopleView` is now
`TimelineExpanded`, and `SetStage(People)` still expands it.

The rail edits one person; this reads all of them: 310 px is a fine width for a name and a list of
blocks and a hopeless one for a 24-hour chart.

#### It folds when you leave, it is as tall as its roster, and every block says what it is

`SetStage(People)` expanded the bar and nothing ever collapsed it, so after one visit a 260 px gantt
lay over the bottom of every other stage until somebody found the chevron. Leaving People collapses
it now; the chevron still works from anywhere. Expanded, it is `ExpandedHeight(n)`. Ruler, one row
per person, alert lane, transport. Instead of `max(260, …)`, because a household of one was
getting a third of a panel of nothing. The blocks were coloured bars with no name; each hovers with
its span, activity and room now, in the strip too, the same `UITooltip.Hover` the alert marks had.
The now-line was a 1.5 px hairline that stopped under the ruler; it is 3 px, runs through the ruler,
and carries the time on a chip where the eye already is, and an hour grid sits over every track so a
block's edge can be read against the ruler without a glance across the bar.

