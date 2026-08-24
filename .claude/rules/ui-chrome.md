---
paths:
  - "Assets/Scripts/ResidenceViz/ModeBand.cs"
  - "Assets/Scripts/ResidenceViz/OverlayDraw.cs"
  - "Assets/Scripts/ResidenceViz/SelectionOverlay.cs"
  - "Assets/Scripts/ResidenceViz/SensorOverlay.cs"
  - "Assets/Scripts/ResidenceViz/LabelBillboard.cs"
  - "Assets/Scripts/ResidenceViz/LabelOutline.cs"
  - "Assets/Scripts/ResidenceViz/TimelineBar.cs"
  - "Assets/Scripts/ResidenceViz/ResidenceEditController.cs"
---

# UI chrome rules. Mode band, overlays, text over the scene

> Loaded when a file under the paths above is read. Rules only: the reasoning is in the design note linked at the end. The control, field, spacing and layout rules are in [`ui.md`](ui.md). Edit this file when a rule changes; update CLAUDE.md only if something every session needs moves.

**`ModeBand`**: a permanent colour-coded strip between the command bar and the scene; `isBaseline`
and `locked` are read separately, never collapsed into one field:

| `isBaseline` | `locked` | Reads | Actions |
|---|---|---|---|
| yes | yes | `BASE ENVIRONMENT · READ-ONLY`, slate stripe, title in `Ink` (the rails show the amber `LockBadge`) | `Modify base environment`, `New proposal` |
| yes | **no** | `EDITING BASE ENVIRONMENT`, **amber** (`UITheme.Warn`) | `Done` |
| no | no | `PROPOSAL 08/23/2026 · 11 CHANGES ▾`, accent | `Compare`, `Report` |

A new proposal is named by `ResidenceStore.NewProposalName`: `Proposal MM/DD/YYYY`, then `Proposal n
MM/DD/YYYY` with n the residence's proposal ordinal, bumped past any name already taken.

A plain class the controller draws from `OnGUI`, re-derived per frame (undo restores the whole
`ResidenceDoc` without notifying anyone). No subtitle; the explanation is the title's tooltip. **Actions
are `UITheme.BandButton`** (white field + rim: the band's washes equal `Btn`, so a `SecondaryButton`
vanished on them) and `ActionsWidth` measures with `BandButtonStyle`. **The title is a pill only when
`switchable`** (more than one variant), otherwise a plain label; `TitleInk` is `Ink` in Base (never
the caption colour `Ink2`), the stripe keeps the slate. `_modeRect` is in `PointerOverUI`; the menu
opens via `_pendingVariantMenu`.

**Text over the scene carries its own contrast.** `OverlayDraw.Readout` owns a `GUIStyle` with an
explicit light `textColor` and the mono face, clamped into the window (`GUI.color` *tints*, so the
ambient `Ink` would otherwise bleed through). World labels are light text with a dark stroke
(`LabelOutline`: four `TextMesh` copies offset by a fraction of `characterSize`, sharing the font
material, `sortingOrder = -1`). `LabelBillboard` hides a label shorter than `minPixelHeight` (11 px)
on screen with 15% hysteresis (**apparent size, not distance**) toggling renderers, never the
GameObject; the stroke is built lazily so the child count is watched too.

**Units chip** in the top bar switches `Units.Display` for the whole app (and the clock: metric ⇒
24-hour). Display defaults to **metres** (dragged numbers read better); `ResidenceStore.MigrateSettings`
carries `unitsDefaultVersion` to flip old installs once. `Units.BareUnit.FollowDisplay` is what typed
fields pass: `WallTool`'s typed run length and `UnderlayTool`'s calibration included.

**Overlays** (`SelectionOverlay`, `SensorOverlay`, `OccupancyClock`) are plain classes the controller
owns and draws **outside** the `!PointerOverUI` guard that gates `IResidenceTool.DrawOverlay` /
`HandleInput`: the cursor is on the rail precisely while reading what it describes. Everything they
draw is re-derived per frame.

→ [`docs/design/ui.md`](../../docs/design/ui.md)
