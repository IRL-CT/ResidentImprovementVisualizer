---
paths:
  - "Assets/Editor/CatalogArtBinder.cs"
  - "Assets/Scripts/HomeViz/CatalogArtFit.cs"
  - "Assets/Scripts/HomeViz/FurnitureCatalog.cs"
  - "Assets/Scripts/HomeViz/MountPlacement.cs"
  - "Assets/Scripts/HomeViz/HomeRenderer.cs"
  - "Assets/Scripts/HomeViz/Tools/FurnitureTool.cs"
  - "Assets/Scripts/Authoring/Interior/FurnitureFit.cs"
  - "Assets/Scripts/TransformGizmo.cs"
  - "Assets/Prefabs/HomeViz/Catalog/**"
  - "Assets/Resources/FurnitureCatalog.asset"
  - "Assets/Resources/HomeCatalogRegistry.asset"
---

# Furniture catalog and transform handles

> Loaded when a file under the paths above is read. Rules only: the reasoning is in the design note linked at the end. Edit this file when a rule changes; update CLAUDE.md only if something every session needs moves.

## Furniture catalog

`Assets/Resources/FurnitureCatalog.asset` holds 35 items with real dimensions. `HomeRenderer` resolves each id against **`HomeCatalogRegistry.asset`**
(owned by the binder: never `Assets/Resources/PrefabRegistry.asset`, which the Site tool's Place and
Paint palettes render): a prefab if one exists, otherwise a correctly sized labeled box. **21 of the
35 have art**; 14 stay boxes on purpose (see *Deliberate decisions* in CLAUDE.md).

- **`Assets/Editor/CatalogArtBinder.cs`** (Tools → HomeViz → Catalog Art Binder) generates one wrapper
  per bound id at `Assets/Prefabs/HomeViz/Catalog/<id>.prefab`: unscaled floor-pivoted root carrying
  `CatalogArtFit`, an `Art` child holding the baked fit scale and pivot offset, the pack prefab nested
  inside with a quarter-turn yaw (both packs face −Z; `rotationY = 0` looks down +Z). **Yaw sits
  inside the scaled node** and must be a multiple of 90°. **The `Rows` table is the source of truth**;
  wrappers are derived and regenerating overwrites them (`CatalogArtFit.handTuned` is the one-off
  escape). The fit stretches each axis independently to the catalog size; `squash` (1.00 =
  undistorted) picks donors; `GasStove` is a cooktop so `range` is a `KitchenOven`; `wall_cabinet`
  uses `PivotZ.Back` because `MountPose` puts the origin on the wall face.
- **Real art has the placeholder's shape**: `Item` root, stretch on the `Art` child, a `Label` on every
  item. `FitCollider` runs on every re-pose. `PoseGO` has three branches (`Box`, `CatalogArtFit`, raw
  root-scale); `PoseMount` applies the fit too.
- Transform handles: `TransformGizmo` is reused literally from Site (`minHandleSize`, `Tick(acceptInput)`
  added, both defaulting to Site's behaviour). **Shift snaps here** (and means draw-free in the drawing
  tools). **Scale means resize in real units** (`boxSizeMeters`; `ObjectInstance.scale` is not read;
  `Reset to catalog size` in the rail). **Yaw only, no Y.** **The re-fit runs on release**, not per
  frame; `HomeRenderer.PoseFurnitureGO` re-poses the live GameObject during a drag and only the release
  calls `RebuildFurniture()`. **Placing selects what you placed.** Wall mounts get no gizmo. Rail
  controls plus a drag that re-hosts via `HomeMetrics.NearestWall`.

→ [`docs/design/furniture.md`](../../docs/design/furniture.md)
