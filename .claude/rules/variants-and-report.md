---
paths:
  - "Assets/Scripts/Authoring/Interior/VariantDiff.cs"
  - "Assets/Scripts/Authoring/Interior/VariantRevert.cs"
  - "Assets/Scripts/HomeViz/Report/**"
  - "Assets/Scripts/HomeViz/Tools/CompareTool.cs"
  - "Assets/Tests/EditMode/VariantDiffTests.cs"
---

# Variants. Compare, revert, ghost, report

> Loaded when a file under the paths above is read. Rules only: the reasoning is in the design note linked at the end. Edit this file when a rule changes; update CLAUDE.md only if something every session needs moves.

## Variants: compare, revert, ghost, report

- **`VariantDiff.Compare` takes any two variants.** `Change` carries `kind`, `id`, `worldPos`,
  `levelId`/`levelIndex`. `NewProposalFrom` deep-copies **preserving every element id**, so moving
  something reports as one `Modified`, not remove + add. It compares `boxSizeMeters`; `DetailWriter`
  is a struct and must be passed by `ref` (a day-only change was once reported as nothing).
- **`VariantRevert` is the exact inverse of `VariantDiff`: revert every change in a diff and the
  diff comes back empty**, for any edit, on every storey, on all six samples. Ids are preserved on
  every path; deep copies are written by hand (no shared `float[]`); the one refusal is restoring an
  opening or mount onto a wall the proposal removed (restore the wall first); reverting an added wall
  cascades to its openings, mounts and the sensors on those openings, mirroring
  `SelectTool.DeleteSelected`.
- **`CompareTool`** rows are grouped by room; click selects + focuses with `reveal: false`; ✕ reverts
  undoably; `VariantDef.description` is edited here and heads the report.
- **The ghost** (`HomeRenderer._ghostVariantId`/`_ghostOn`) is re-applied last in every `Rebuild`
  and after the targeted rebuilds. Red from the *other* variant where things **were**, green from
  this one where they **are**; `Modified` feeds both halves, gated by `GeometryDiffers`. **Two**
  translucent materials (`_Surface`, blend pair, **ZWrite off**), ghost meshes in `_ghostMeshes`,
  **no colliders**. The diff is against the variant being **rendered** (Compare switches the view to
  its After when the chip goes on). **On by default in Review**: `CompareTool.Enter` brings the view
  to After and turns it on; `HomeEditController.SetStage` turns it off on leaving the Review stage
  (Compare ↔ Measure keeps it); the toggle in the rail still overrides either way. Openings are
  deliberately absent from the ghost.
- **The report** (`Assets/Scripts/HomeViz/Report/`): self-contained HTML with a print stylesheet
  (`@page` is the PDF half); `ReportDoc` is the model. `ReportCapture`: a **hidden camera, never
  `ScreenCapture`**; **never from `OnGUI`**; **one rebuild per (variant, storey)**, every framing taken
  from it; **framed over the union of both variants' bounds**; occupants, ghost, selection and sensor
  states hidden/frozen and restored afterwards; **JPEG**. `ReportBuilder` supplies description,
  counted summary and before/after metrics (no turning circles). The **Technology section carries no
  photographs** and must stay last: `ReportCapture.Framings` is shorter than `report.sections` and
  the pairing loop stops at the shorter.

→ [`docs/design/variants-and-report.md`](../../docs/design/variants-and-report.md)
