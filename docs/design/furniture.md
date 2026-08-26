# Design notes. Furniture: fit, handles, picker and art

> `FurnitureFit` (the opening rule for anything placed by hand), the transform handles that made
> furniture movable, the footprint-tile picker, and how the two furniture packs are bound to catalog
> ids through generated wrapper prefabs. The rules are summarised in
> [`.claude/rules/furniture.md`](../../.claude/rules/furniture.md); the reasoning lives here.

## The same rule, for everything placed by hand: `FurnitureFit`

All of the builder's fit logic (see [samples-and-planbuilder.md](samples-and-planbuilder.md)) only ever
ran for the authored samples. Anything a *user* placed had no fit logic at
all: `OpeningFit` guarded doors while `FurnitureTool.Place` wrote the cursor position straight into the
level, so clicking in a doorway put furniture in a doorway, silently. `FurnitureFit` is that rule
re-expressed over the **emitted** `WallDef`/`OpeningDef` rather than `PlanBuilder`'s pending records,
and it follows `OpeningFit`'s contract. Slide to the nearest legal spot, refuse only when nothing is
legal, and return a `reason` written to be shown verbatim in the rail.

The two rules that carry over unchanged are the ones that make it usable rather than annoying: only
openings the item is **tall enough to reach** block it (a sofa belongs under a window sill), and only
walls the item is actually **against** are considered (an approach strip in front of a door is the
thing that was tried and reverted). `FitMount` is the bounded form for wall-mounted items, because a
grab bar hanging off the end of its own wall is not a placement.

Two things differ from `ResidenceMetrics.FootprintOf` on purpose. `FurnitureFit.Footprint` bounds the
**truly rotated** rectangle instead of snapping to a quarter turn: the tool hands out 15° steps and a
continuous slider, and at 45° the snap understates the extent by most of a diagonal. And the placement
ghost now draws at the **fitted** position, rotated the way `Quaternion.Euler` actually rotates: it had
the sign of `sin` flipped, so the preview was a mirror image of what spawned at every angle except the
quarter turns, which is why nobody noticed.

Still deliberately unguarded, in the builder only: `PlanBuilder.Free` does no opening check (its
handful of conflicts are placed explicitly, with comments), and wall mounts do not check each other.

## Placing was the only thing you could do to a piece of furniture

`FurnitureTool.Place` wrote an `ObjectInstance` into the level and that was the last time anything
touched it. `SelectTool` offered one rotation slider; **nothing anywhere wrote `position` or
`boxSizeMeters` after placement**, so moving a chair 30 cm meant deleting it and clicking again.
`ResidenceToolBase.LeftHeld`/`LeftReleased` had existed, unused, the whole time: nothing in ResidenceViz
dragged anything.

The Site tool has had the answer since long before: pick from a searchable grid, drop it, then grab /
rotate / scale it with handles. **`TransformGizmo` is reused literally**, not re-expressed the way
`WallLinker` re-expresses `FenceLinker`. It takes GameObjects and a Camera and emits deltas, with
nothing site-shaped in it, and literal reuse is what makes the two editors feel like one tool instead
of two similar ones. It joins `WorldRenderer`, `PrefabRegistry` and `EnvironmentScale` as
shared-by-design. Two knobs were added there, both defaulting to Site's behaviour: `minHandleSize`
(the stock 2 m floor on the handle radius is right for a tree and draws a 0.51 m toilet's gizmo four
times the size of the toilet) and `Tick(acceptInput)`.

**Shift snaps here, and means draw free in the drawing tools.** That inversion is Site's and it is
deliberate: drawing wants snapping by default, transforming wants free by default.

Four things are ResidenceViz's rather than Site's:

- **Scale means resize in REAL UNITS.** `boxSizeMeters` is the item's true size: what `ResidenceRenderer`
  draws, what `FurnitureFit` tests against a doorway, what the occupancy checks stand people clear of.
  A free 0.1-5× multiplier, the way Site scales a tree, would leave a 1.4×-scaled toilet reporting
  clearances for a toilet that does not exist. So the gizmo's additive scale delta is applied as a
  proportional factor to the real dimensions, the rail shows the user's chosen units, and **Reset to catalog
  size** is always one click away. (`ObjectInstance.scale` is *not* the field: ResidenceViz's render path
  has never read it.)
- **Yaw only, no Y.** Every footprint in the app: `FurnitureFit`, `ResidenceMetrics`, `SelectionOverlay`,
  the occupancy checks. Is computed from `rotationY` alone and furniture stands on `Level.elevation`,
  so an X/Z tilt or a lifted item would show on screen and in none of the numbers.
- **The re-fit runs on RELEASE, not per frame.** `FurnitureFit` slides rather than refuses, and an
  item that jumped aside mid-drag would be fighting the cursor still holding it. Growing or turning an
  item reaches into a doorway exactly as moving it does, so all three paths end at the same re-fit.
  The re-fit obeys the placement rules: a sofa stretched wider still passes under a window sill and
  must not be shoved along the wall for growing, while raising it past the sill makes it block the
  window.
- **A wall mount gets no gizmo.** It is parameterised by `(wallId, offset, side, mountHeight)`,
  there is no direction it can travel that is not along a wall, so it gets rail controls plus a drag
  that re-hosts it onto the nearest wall. `ResidenceMetrics.NearestWall` is that answer, lifted out of
  `FurnitureTool` so placing and re-hosting cannot disagree about which side of a wall the cursor is on.

`ResidenceRenderer.PoseFurnitureGO` is what makes a drag affordable: a drag writes the def **and** re-poses
the live GameObject, and only the release rebuilds. The obvious alternative (mutate and `Rebuild()`) 
destroys and respawns every GameObject in the residence each frame, and `BuildPlaceholderBox` does a
`Shader.Find` and a `new Material` per item, so a drag over a furnished plan would allocate hundreds
of materials a second. Worse, it would destroy the very object the gizmo is holding. The long-dead
`RebuildFurniture()` is what the release calls. Spawn and re-pose go through one method, so an item a
drag resized looks identical either way; it also closes an old hole, that a prefab was instantiated at
its authored size and ignored a resize entirely (art now scales *relative* to the catalog size, so an
un-resized item renders exactly as authored).

**Placing now selects what you placed.** One line, and it is what joins "place" to "manipulate",
before it, the handles never appeared on the thing you had just put down.

## The picker is a grid, and the tiles are floor plans

Search plus an **All** chip plus a 3-column `UITheme.Thumb` grid, the shape of Site's Place rail, with
`ThumbnailCache` giving a real preview the day art lands under a catalog key.

Until then a tile is **not** the entry's swatch. The catalog colours by *category*, so a grid of flat
swatches makes every mobility item the same blue and every bedroom item the same purple: the tile
would restate the chip you just clicked and nothing else. Each tile instead draws the item's true
footprint against one fixed 2.3 m reference (the longest thing in the catalog is a 2.13 m hospital
bed), **not** normalised per tile, because the entire point is that a double bed and a nightstand are
not the same size. A bed fills its tile, a nightstand is a dot, a grab bar is a sliver. That is what
the old text rows carried, in the form this catalog exists to be honest about.


## The art is bound through generated wrapper prefabs: `CatalogArtBinder`

Two furniture packs were already in the project and referenced by nothing:
`Assets/Prefabs/Furniture/Cute_Furniture_Free/` (67 toon prefabs, two materials for the whole pack)
and `Assets/Prefabs/Furniture 2/`: the "Furniture Mega Pack", 511 prefabs. Neither can be registered
directly, for three reasons that are each fatal on their own:

- **Neither pack is at catalog scale.** The Mega Pack is roughly 2× oversized at the prefab root: a
  bed measures 5.28 m long, a bathtub 2.91 m. `PoseGO` scales real art *relative* to the catalog size
  and deliberately does not normalise against the prefab's own bounds, so a raw donor renders at
  whatever size it was authored.
- **`PoseMount` applies no scale at all**, so a wall-mounted donor has no correction whatsoever.
- **Both packs are Blender exports, so every model faces −Z**, while `PlanBuilder.YawFacingInto` is
  explicit that *"rotationY = 0 looks down +Z"*.

So `Assets/Editor/CatalogArtBinder.cs` (**Tools → ResidenceViz → Catalog Art Binder**) generates one
wrapper per bound id at `Assets/Prefabs/ResidenceViz/Catalog/<id>.prefab`: an unscaled, floor-pivoted root
carrying `CatalogArtFit`, an `Art` child holding the baked fit scale and pivot offset, and the pack
prefab nested inside that with a quarter-turn yaw. **Yaw sits inside the scaled node**, because Unity
applies `localScale` before `localRotation` on one transform and a non-quarter turn under a non-uniform
parent scale is a shear: the binder refuses any yaw that is not a multiple of 90°.

**The `Rows` table in that file is the source of truth, not the prefabs.** A wrapper is a derived
artifact and regenerating overwrites it; `CatalogArtFit.handTuned` is the escape hatch for a one-off.

**The fit stretches each axis independently** to the exact catalog size, matching what the placeholder
box does, so the picture keeps agreeing with the numbers `FurnitureFit` and `ResidenceMetrics` report. The
cost is distortion when a donor's proportions differ, which the binder reports as a **squash** figure
(1.00 = undistorted) and which is what picks the donors: `Measure Family` ranks a whole folder on it,
turning 50 candidates into a dozen before any screenshot is taken. Donors were chosen best-fit-first
with the Cute pack preferred wherever it lands within 1.30, which is the seven ids its own art
actually covers well.

**Squash cannot settle yaw**, because a footprint is identical under a half turn. That is the one
thing here decided by looking rather than measuring, and skipping it is not cosmetic: the sample
apartment's sofa stood against the west wall with its cushions pressed into the wall and its backrest
facing the living room, and nothing anywhere complained.

Two selection traps the ranking walked into and the table now pins: `GasStove` is a **cooktop**, 0.3 m
tall, so `range` must be a `KitchenOven`; and ranking on aspect alone offered a kitchen extractor hood
for `island` and a sink for `wall_cabinet`.

**Deliberately left as boxes, with the reason, so it does not get re-litigated:** the five mobility
items (`wheelchair`, `walker`, `hospital_bed`, `transfer_bench`, `patient_lift`: neither pack has any
medical equipment, and these are what the tool's whole argument rests on), `shower_seat` and
`roll_in_shower`, the three 0.04 m rails (`grab_bar_24`, `grab_bar_36`, `handrail`), the three
sub-decimetre plates (`light_switch`, `outlet`, `thermostat`) and `threshold_ramp`. Anything stretched
into those reads worse than a labeled box.

## Real art now has the same shape as a placeholder

`BuildPlaceholderBox` has always returned a floor-pivoted `Item` root holding a `Box` and a `Label`,
while real art was the bare instantiated prefab whose **root** `PoseGO` scaled. Art now comes back in
the placeholder's shape (unscaled root, stretch on the `Art` child) and three things follow:

- **Everything is labeled**, not just boxes. A label parented to a scaled root would have its glyphs
  stretched by the fit; on an unscaled root it cannot be.
- **`FitCollider` replaces `AddFittedCollider` and runs on every re-pose, not once at spawn.** The old
  order added the collider and *then* let `PoseGO` write a scale onto the same transform, so a resized
  item with art got its pick box multiplied twice: a bed widened a fifth got one 44% too wide. It was
  dormant only because no catalog id resolved to art.
- **`PoseMount` applies the fit too.** It is the identity today (a mount's size is always the catalog
  size and its wrapper is baked to exactly that), and it runs anyway so a mount that ever gains a size
  override follows it instead of silently rendering wrong.

`PoseGO` therefore has three branches: `Box` → placeholder, `CatalogArtFit` → fitted wrapper, neither →
the original root-scale path, kept so registering a raw prefab by hand still behaves as it always did.

One thing the bake works around rather than fixes: `MountPose` puts the origin **on** the wall face and
the placeholder box straddles it, so half an item's depth is inside the wall. Invisible on a 0.09 m grab
bar, 165 mm on a 0.33 m wall cabinet. Hence `PivotZ.Back` on that row. The proper fix is to push the
placeholder, the ghost and `MountPose` out by half the depth together.

## The registry was split: `ResidenceCatalogRegistry.asset`

`Assets/Resources/PrefabRegistry.asset` is **not** touched, and must not be: besides `WorldRenderer`,
`EditController` renders its `entries` as the Site tool's **Place → Objects** thumbnail grid and its
**Paint Objects** brush list. Twenty-one interior rows there would put a sofa and a toilet in the site
editor's object palette. `ResidenceRenderer.prefabRegistry` in `ResidenceViz.unity` points at
`Assets/Resources/ResidenceCatalogRegistry.asset` instead, which the binder owns outright, which is also
what makes regeneration a safe wholesale rewrite.

Adding art for one of the remaining 14 is a row in `Rows` plus a regenerate. Nothing about the schema
or the data changes, because instances only ever store the key.


## Make your own: items the catalog does not ship

The catalog is a fixed 35. Every real residence has something outside it, and the only recourse was to
place the nearest item and resize it, which leaves the plan asserting that a chest freezer is a
wardrobe. The tool's whole argument rests on dimensional honesty, so an item that is the right size
under the wrong name undermines exactly the thing it is there to support.

A name and three dimensions is the entire schema. That is enough for the question being asked, which is
whether the wheelchair gets past it, and it is deliberately less than `FurnitureCatalog.Entry` carries:
no color, no clearances, no wall mounting. A wall mount needs a second set of decor rules that a name
and three numbers cannot describe, so custom items are floor-standing and the picker never offers
otherwise.

**The definition lives on `ResidenceDoc`**, beside `underlays` and for the same reason: what the
household owns is a record of the dwelling, not a design option, so every variant offers the same list
and one item can stand in both Existing and a proposal for the comparison to be about the room rather
than the furniture. It rides inside the `.riv` because the export is the whole document. `Migrate`
backfills the empty list; nothing else was needed, because the whole `ResidenceDoc` is already the undo
unit, so one `RecordEdit` covers a list that sits outside every variant.

### Why the id is a slug of the name

`ObjectInstance.prefabType` is the only durable link from something standing in a room back to what it
is, and **two readers of it cannot reach the definition list**. `VariantDiff` holds two `VariantDef`s
and no document, by design, so it can never look one up; and the renderer has nothing to look up at all
once a definition has been deleted. Both label furniture from the key alone.

So the key is `custom:` plus a slug of the name. `custom:reading_chair` reads back as "Reading chair"
in a Compare row, in the HTML report, and on the floating label over a box whose definition is gone. A
guid would have been the obvious choice and would have put a hex string in a document a family reads.
Names are never editable, which is what keeps the slug from going stale, and that is the actual reason
editing was left out rather than a scoping decision: an editable name means either a key that lies or a
migration over every placement that stores it.

The prefix is the other half. No catalog id and no `PrefabRegistry` key contains a colon, so
`FindPrefab` misses and `BuildPlaceholderBox` takes over, which is precisely the intended path: a custom
item is a labeled box at its true size, the same treatment the 14 deliberate placeholders get.
`CustomItemsTests.NoCatalogIdLooksCustom` pins the separation, because a catalog id that started with
the prefix would resolve as a custom item and quietly lose its art.

### One lookup, and what deleting means

A custom item reaches the app as a **synthesized `FurnitureCatalog.Entry`**. `Entry` is the currency of
the whole furnish path (`NewInstance`, `FurnitureFit`, the ghost, the tile painter, the Select rail), so
synthesizing one meant `FurnitureTool.Place`, `FitFloor`, `DrawOverlay` and `Report` needed no changes
at all: there is no second placement stack to keep in step with the first.

The cost is that `Catalog.Get` is no longer the right question. It finds the 35 shipped items and
reports a custom one as unknown, which reads downstream as a nameless box with no size and no
**Reset to catalog size**. `ResidenceRenderer.EntryFor` is now the single answer to "what is this
thing", and every call site in the renderer, the controller and `SelectTool` goes through it.

**Deleting drops the definition and leaves every placement standing.** Each instance carries its own
`boxSizeMeters` and its own name inside its key, so nothing moves, resizes or goes nameless; the item
simply stops being one you can place again. That made null a legitimate return from `EntryFor` rather
than an error, and the call sites already handled it: `SelectTool` hides **Reset to catalog size** when
the entry is null, which is exactly the right behavior with no code written for it. The alternative,
refusing to delete anything in use, would have made the list unclearable in the one case where you most
want to clear it, which is after a mistake.

### The rail

The chip is synthetic, drawn by hand after the loop over `Categories()`, the same shape `SensorTool`'s
Fixtures chip uses: the catalog asset has no such row and should not gain one. The form sits above the
grid so it reads as the thing that fills the shelf under it, and delete lives in the Selected block
rather than on a 76 px tile, where a ✕ is never more than a misclick from the thing it sits on.

Category switching is deferred through `Tick` alongside the add and the delete, because picking the chip
adds the whole form to the panel, and a control count that changes between the layout and repaint passes
is the `Mismatched LayoutGroup` the `_pending*` flags exist to prevent. The footprint tile cache gained
the item's dimensions in its key: a catalog id names one footprint forever, but a custom item can be
deleted and remade under the same slug at a different size, and an id-only key hands that one the old
item's picture.
