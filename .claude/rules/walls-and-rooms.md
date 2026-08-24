---
paths:
  - "Assets/Scripts/Authoring/Interior/Wall*.cs"
  - "Assets/Scripts/Authoring/Interior/RoomRegions.cs"
  - "Assets/Scripts/Authoring/Interior/RoomFinish.cs"
  - "Assets/Scripts/Authoring/Interior/RoomMeshBuilder.cs"
  - "Assets/Scripts/Authoring/Interior/Spans.cs"
  - "Assets/Scripts/Authoring/Interior/Segments.cs"
  - "Assets/Scripts/Authoring/Interior/OpeningFit.cs"
  - "Assets/Scripts/Authoring/Interior/FurnitureFit.cs"
  - "Assets/Scripts/Authoring/Interior/SensorFit.cs"
  - "Assets/Scripts/Authoring/Interior/HomeMetrics.cs"
  - "Assets/Scripts/Authoring/Interior/PlanBuilder.cs"
  - "Assets/Scripts/HomeViz/Tools/WallTool.cs"
  - "Assets/Scripts/HomeViz/Tools/OpeningTool.cs"
  - "Assets/Scripts/HomeViz/Tools/RoomTool.cs"
  - "Assets/Scripts/HomeViz/Tools/SelectTool.cs"
  - "Assets/Materials/Interior/**"
  - "Assets/Tests/EditMode/WallLayoutTests.cs"
  - "Assets/Tests/EditMode/WallMeshBuilderTests.cs"
  - "Assets/Tests/EditMode/WallSnappingTests.cs"
  - "Assets/Tests/EditMode/OpeningFitTests.cs"
  - "Assets/Tests/EditMode/HomeMetricsTests.cs"
  - "Assets/Tests/EditMode/RoomMeshBuilderTests.cs"
---

# Geometry rules. Walls, openings, rooms, floor finish, fits

> Loaded when a file under the paths above is read. Rules only: the reasoning is in the design note linked at the end. Edit this file when a rule changes; update CLAUDE.md only if something every session needs moves.

## Geometry rules

Nothing downstream of the schema complains about bad geometry: `WallLayout` clamps, `WallMeshBuilder`
leaves a notch, `HomeRenderer` skips, so these rules are what keeps it right.

**Walls and openings**
- `WallLayout.Build` emits solid boxes only *between* openings. Panels, a header above each, a sill
  below a window. **No CSG**; an opening is a gap the box list skips, so an item centred on a door
  renders as a box floating in the hole with no warning.
- `WallMeshBuilder` closes corners by **overlap, not mitering**: each wall extends half a
  neighbour-thickness past a shared endpoint, stopping **`JunctionBias` (1 mm) short** of the
  neighbour's far face (landing exactly on it z-fights the cap against the face). A **collinear**
  neighbour (`WallLinker.MinJunctionSin`) extends by nothing. Corners close only where endpoints
  coincide within `WallMeshBuilder.Near` (1 mm). Known limitation: cutaways or exports would need real
  mitering.
- **`WallLinker`** runs on every `WallTool.CommitSegment` and `Relink` on every drag-release: walls
  divide each other where they cross, a junction is welded onto a nearby vertex of both sides, a
  junction on a wall's own end is a shared corner not a cut, cuts shorter than `MinSeg` are skipped,
  **parallel/collinear contact is never a junction**, a cut landing in a doorway is refused (T-joins at
  the jamb), **piece 0 keeps the original id**, `a→b` direction is preserved, openings and mounts are
  re-homed onto the right piece and re-run through `OpeningFit`. `ContactEps` 0.02 m is detection,
  `WeldEps` 1 mm = `WallMeshBuilder.Near` is the output ceiling, and each junction is computed **once**
  as one `Vector2` written into every wall meeting there (bit-identical, `1e-6`, not merely close).
  `Relink` is idempotent and a no-op on all six sample plans. `Uncovered` (over `Spans.Subtract`) is
  what stops drawing along an existing wall from doubling it. **Shift = draw free**: no snapping, no
  linking.
- `Spans` (`Union`/`Split`/`Subtract`) is the shared interval algebra; `PlanBuilder.TOL` is tied to
  `Spans.TOL`. `Segments.Canonical`/`CanonicalIndex` are shared by `WallLinker` and `RoomRegions`.

**Rooms: `RoomRegions`**
- **An enclosed area is a room.** Rooms stay first-class, stored, id-bearing, diffable records;
  derivation is an *editing-time* rewrite of one field (`polygon`), never a render-time computation
  (`VariantDiff`, sensors, schedules, `OccupancyModel` and `ReportBuilder` all key on room id).
  `Sync` writes `polygon` and nothing else.
- `Find` is a planar face walk: vertices weld at `WeldEps` (= `WallMeshBuilder.Near`: a gap you can
  see is a gap that makes no room), but interior *splitting* accepts a vertex within
  `min(ContactEps, half the wall's thickness)` of a wall: the bound under which `WallLinker` may have
  left a refused junction, and inside the wall's body so no visible gap closes. Split at vertices
  *then* de-duplicate edges, `next(e)` is the outgoing half-edge clockwise from `twin(e)`, the outer
  face is the cycle whose `SignedArea` ≤ 0, collinear vertices stripped. No vertex is invented at a
  bare X crossing, and it never calls `Relink`. A region wholly inside another is carved out of it
  (`CarveContainedRegions`): the containing ring is bridge-cut. Still one ring, two coincident
  bit-identical edges that cancel in every even-odd test, so regions never overlap: one room, one
  space. It must reproduce the rooms of all six samples exactly
  (`RoomRegionsTests`). **Detect rooms** gates on `RoomRegions.RoomsMatch`. Polygons, not counts.
- `Sync` matches on `LargestInscribedCircle.center` → centroid → most corners. Two rooms → one region:
  the **larger keeps its identity**. One room → two: larger keeps id/name/type, the other is a new
  `Untyped` room. Region claims nothing: a new `Untyped` room, real from that instant. Room claimed by
  nothing: removed with a warning. **Surviving rooms keep their existing order.**
- `Sync` is called from `WallTool.CommitSegment` (both paths), `SelectTool.DeleteSelected` for a
  wall, and the Rooms rail's **Detect rooms**: each inside the caller's `RecordEdit`. **Never** on
  thickness/height edits, from `PlanBuilder`, on load, in `Migrate`, or from `VariantRevert` (which
  restores polygons by `Copy`, so the diff is empty by construction). `HomeRenderer` renders
  `level.rooms`, never `Find`.
- `RoomRegions.RemoveRoom` is the **one** room-removal cascade (`SelectTool.DeleteSelected`,
  `VariantRevert.RevertRoom`). **A room cannot be deleted on its own**: the walls still enclose it
  and the next `Sync` would put it straight back with a fresh guid, so `Kind.Room/Floor/Ceiling` are
  out of `DeleteSelected`, the Delete button and key, and `SelectionOverlay.DrawRoom` has no drag
  handles.
- `RoomType.Untyped` ("nobody has said yet", drawn dashed) is distinct from `Other` ("none of these").

**Floor finish: `RoomFinish.FloorMaterial(roomType)`** (in `CXRAuthoring`; unknown type →
`floor_untyped`):

| Type | materialId | Type | materialId |
|---|---|---|---|
| `untyped`, `other` | `floor_untyped` cool grey | `hall` | `floor_hall` warm sand |
| `bedroom` | `floor_carpet` dusty rose | `entry` | `floor_entry` clay brown |
| `bathroom` | `floor_bath` blue tile | `laundry` | `floor_laundry` mint green |
| `kitchen` | `floor_vinyl` pale yellow vinyl | `storage` | `floor_storage` muted violet |
| `living` | `floor_oak` oak | `office` | `floor_office` slate blue |
| `dining` | `floor_dining` walnut | | |

Contract (CIELAB on the colour after scene lighting): **no floor within ΔE 20 of the rendered wall
cap (`Wall_Edge`), no two floors within ΔE 14 of each other, no floor channel above 0.74.** Change a
row and re-check the whole pair table. `Tile_Bath` is a wall finish, never a floor.

**Fits: one contract, three files**
- `OpeningFit`, `FurnitureFit`, `SensorFit`: **slide to the nearest legal spot, refuse only when
  nothing is legal, return a `reason` written to be shown verbatim** (`UITheme.Glyph("⚠", reason)`).
  `SensorFit` is the exception that refuses rather than slides (a device on the wrong host reports
  nothing forever).
- Only openings the item is **tall enough to reach** block it (a sofa passes under a sill; doors have
  sill 0), and only walls the item is actually **against** are considered. Openings in perpendicular
  walls are deliberately not (an approach strip was tried and reverted). `FurnitureFit.Footprint`
  bounds the truly rotated rectangle; `FitMount` is the bounded form for wall mounts.
- `OpeningFit.MaxWidth` and `Fit` share one private `FreeSpan`, so a width the control offers is
  always a width the fit accepts. `OpeningFit.FitVertical` bounds height and sill.
- Deliberately unguarded: `PlanBuilder.Free` does no opening check; wall mounts do not check each
  other.

→ [`docs/design/walls-and-rooms.md`](../../docs/design/walls-and-rooms.md) ·
[`docs/design/samples-and-planbuilder.md`](../../docs/design/samples-and-planbuilder.md)
