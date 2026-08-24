---
paths:
  - "Assets/Scripts/HomeViz/ExteriorBridge.cs"
  - "Assets/Scripts/HomeViz/Tools/OutdoorTool.cs"
  - "Assets/Scripts/WorldRenderer.cs"
---

# Optional exterior layer

> Loaded when a file under the paths above is read. Rules only: the reasoning is in the design note linked at the end. Edit this file when a rule changes; update CLAUDE.md only if something every session needs moves.

## Optional exterior layer (off by default)

`HomeDoc.exteriorEnabled` is `false` and `VariantDef.exterior` is `null` until the **Outdoors**
foldout switches it on, which adds the Outdoors stage. `ExteriorBridge` wraps the variant's `SiteDef`
as an `EnvironmentDef` for the existing `WorldRenderer`; the scene's `Exterior` subtree (60 × 60 m
Terrain + `WorldRenderer`) is wired but `SetActive(false)`. Rendering needs **both** the opt-in and
something drawn (`ExteriorBridge.HasContent`), otherwise the tidy ground pad would be swapped for a
bare terrain; `HomeRenderer` keeps exactly one of `GroundPad` / `Terrain` active. `WorldRenderer`'s
tile-building fields are deliberately unassigned in `HomeViz.unity` (`buildingInstances` is always
empty); `pathMaterialPalette`, `fencePalette`, `terrainRegistry`, `prefabRegistry` must stay wired,
`OutdoorTool` reads the first two. Ground painting, scatter and lot editing remain Site-only.
