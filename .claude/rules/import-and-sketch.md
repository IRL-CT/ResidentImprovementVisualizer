---
paths:
  - "Assets/Scripts/ResidenceViz/Sketch/**"
  - "Assets/Scripts/Authoring/Interior/Sketch/**"
  - "Assets/Scripts/ResidenceViz/PdfRaster.cs"
  - "Assets/Scripts/ResidenceViz/Tools/UnderlayTool.cs"
  - "Assets/Scripts/Authoring/Interior/Stories.cs"
  - "Assets/Plugins/x86_64/**"
---

# Import, PDF, Read the plan, and storeys

> Loaded when a file under the paths above is read. Rules only: the reasoning is in the design note linked at the end. Edit this file when a rule changes; update CLAUDE.md only if something every session needs moves.

## Import, PDF and Read the plan

**`PdfRaster`**: `Assets/Plugins/x86_64/pdfium.dll` (BSD-3) behind a ~150-line P/Invoke wrapper.
- **BGRA and top-down** from PDFium → `Texture2D.RGBA32` bottom-up, both corrected in one pass.
- **Init once, destroy never** (a domain reload keeps the native DLL loaded; a paired destroy crashes
  the Editor).
- **Every entry point catches `DllNotFoundException`**; `IsAvailable` false and the rail says so.
- **One dpi for the whole document, from its largest page**, capped by `MaxRasterSide` (4096): this
  is what makes `metersPerPixel` comparable between pages, so calibrating one page scales the rest.
- Only the rendered PNG is stored, never the PDF. `.bmp` is out of the import filter (`LoadImage`
  cannot decode it); a failed decode is a visible `UITheme.Glyph`, remembered by key.

**Read the plan** (`Assets/Scripts/ResidenceViz/Sketch/`) is the only network call in ResidenceViz; it sends the
sketch image and nothing else.

```
                 ┌─ pass 1: the rooms ──────────────────────────┐
sketch → Claude →│ regularize → validate ─┐                     │→ the room list, settled
                 │      ↑_ repair turn ___|  (best of the two)  │
                 └──────────────────────────────────────────────┘
                                   │ quoted back as fact, in metres
                 ┌─ pass 2: openings + furniture ───────────────┐
        Claude → │ validate → PlanBuilder ┐                     │→ LevelDef → the storey
                 │      ↑_ repair turn ___|  (best of the two)  │
                 └──────────────────────────────────────────────┘
```

- **The model emits rooms and relationships, never ids or coordinates**. It fills `PlanBuilder`'s
  authoring surface (`Room`, `partOf`, `DoorBetween`, `ExteriorDoor`, `Window`, `Against`, `Free`,
  `Mount`) and the tested derivation does the rest. `SketchPlanSpec.JsonSchema()` builds `roomType`
  from `RoomFinish.All` and `catalogId` from `SampleFurniture.All`; structured output needs
  `additionalProperties: false` with every property `required` (sentinels `""`/`0`); numeric ranges
  live in the prompt and `SketchPlanValidator`. The rooms-only, detail-only and whole schemas are built
  from one set of fragments. **Every room states its size twice** (`widthMeters`/`depthMeters` beside
  0-1000 coordinates); the validator compares them with 0.5 m / 25% slack.
- **Coordinates** are 0-1000, origin top-left, y down; lengths that are not positions are metres.
  `SketchFrame.ToWorld` is the whole conversion and must agree with `UnderlayTool.ApplyTransform`
  (quad bottom-left at `originMeters`, rotated about its centre); **one vertical flip, in one place**.
  A rotation that is not a quarter turn is refused. **`alongFraction` runs min → max** (west→east,
  **south→north**). Stated in the schema in compass words.
- **`SketchRegularizer.Snap`** clusters x then z in metres after the transform and rewrites each edge
  to the cluster mean (idempotent). `DefaultTolerance` 0.25 m, `MAX_SPREAD_FACTOR` 1.4 (cap 0.35 m,
  the guard against chaining), `MinGenuineSeparation` 0.400 m: the tightest real wall separation in
  the six samples; a **ceiling** here and a **floor** in the validator's sliver check. It must move
  nothing in any sample plan, and recover every sample from ±0.15 m per-coordinate jitter with the
  same wall count, no unwelded junction, no overlap, no warnings.
- **Two passes, one system prompt, one image, byte for byte**: the cache breakpoint sits **on the
  image block**; `ClaudeClient.Call` reports `CacheReadTokens`/`CacheWriteTokens` because a broken
  breakpoint fails silently. Rooms are quoted back to pass 2 **in metres, as built**.
- **One repair turn per pass; the better reply wins, ties to the first.** Rooms are scored by
  `SketchPlanValidator.CheckRooms`, fittings by the whole compile including `PlanBuilder.Warnings`.
  Whatever survives is **shown and the plan installed anyway**; if pass 2 fails outright the rooms are
  installed alone. The reachability check counts only what you can walk through, asks twice (isolated
  room; unreachable wing), and when nothing opens outside only demands one connected group. The
  validator does not re-check what `PlanBuilder` already checks.
- **`SketchInstall.Adopt` replaces, never merges**. Keeps the storey's `id`, `name`, `elevation`.
  `ResidenceStore.Create` installs the starter room through it too. **`IsEmpty` counts an untouched
  `StarterRoom` as empty**, so Read this plan stays the plain PrimaryButton on a new residence; the
  instant anything is done to that room the ⚠ names the price again. `ContentSummary` is unaffected.
  **Neither `Relink` nor `Sync` runs afterwards.** Every id is re-stemmed (`SketchPlanCompiler.Reid`,
  `g<4 hex>_`) because `ResidenceRenderer.Mark` keeps one flat dictionary. Read this plan keeps a short
  label; the `⚠` glyph beside it names what it replaces; one undo takes it all back (`RecordEdit`
  snapshots at apply time).
- **The rail leads with Floors, merged with Plan**: each floor is **one row**: an inline name
  field (editing renames, clicking selects; the active floor's field wears the tint), the plan's
  filename (`Fit`, full name on hover), a round `↻` replace glyph (`UITheme.RoundGlyphButton`), and
  a ✕ that removes the floor **and its plan** behind a two-click confirm whose DangerButton names
  the price. The last floor's ✕ removes only the plan (absent with no plan); Import sits in the
  filename's place when a floor has none; imports target the row's `levelId`, not the active floor.
  Add floor is one button plus a name field prefilled with `Stories.DefaultName`, editable before
  pressing. Then, for the active floor's plan: Scale (one self-labelled
  button: `Scale · <image width>` when calibrated, `Set scale…` when not; no header, no badge) →
  the `Display` foldout (opacity · angle · lock) → the Read the plan controls, headerless. The
  page picker still replaces the whole rail while a multi-page PDF is open.
- **The lock gate is in the drawing pass** (`DrawGenerate` refuses before drawing the button);
  `BeginGeneration`/`ApplyGeneration` check `Ctx.IsLocked` directly, never the drawing helper.
  `RunGeneration` runs the reader as its own coroutine; a run that ends without answering is
  reported. `_genOutcome` keeps the installed counts (off the **level**) in the rail.
- The coroutine writes `_genPhase`/`_genRunning`; `Tick` latches them once per frame; `DrawRail`
  reads only the latch. The result carries the `residenceId`/`levelId` it was asked for and apply refuses
  anywhere else; `Exit()` aborts an in-flight call.
- The image is resampled on the **CPU** (`SketchImageResample`, box filter, point-sampling drops
  1 px walls); **PNG, JPEG only above 3.5 MB**.
- **The key**: `ApiKeyStore`: `ANTHROPIC_API_KEY` first, then `<RootDir>/anthropic.key`. Not in
  `ResidenceSettings`, not in `ResidenceDoc`, not in the export zip. From the environment: badge, no Forget
  button. Written **plaintext** (DPAPI is a deliberate not-yet; the rail says so). The field is
  `UITheme.SecretRow`: visible while typing, a short fixed placeholder and disabled when hidden, one
  `GUI.TextField` always, the eye toggle's click claimed **before** the field is drawn; drawn whenever
  the Import tab is open; save/forget deferred via `_pendingKeySave`/`_pendingKeyForget` and the
  `Source` latched in `Tick`.

**Read on device** (`SketchPlanDetector`, in `CXRAuthoring` beside the compiler) is the offline
sibling of Read the plan: same seam (`SketchPlanSpec` → `Compile` → `Adopt`), no key, no network.

- **Deterministic, by contract**: same pixels and calibration give the same spec byte for byte.
  Row-major scans, lower medians, index tie-breaks, no Dictionary iteration, no parallelism, no
  randomness, no Hough. `SketchPlanDetectorTests` pins it with plans drawn in code
  (`SketchTestImages`); `SketchWallStageTests` pins the stages one at a time.
- It emits rooms and relationships through the spec, never wall ids or world coordinates. **Graph
  first, cells second**: the mask is read once into measured wall segments (`SketchWallSegments`,
  scanline crossings chained along the span, so hand wobble and the double-line convention are
  handled structurally; majors establish wall lines, minors only join them). A chain whose center
  drift exceeds the diagonal cap is split at its largest center step and each side judged again,
  so a panel line drawn touching its jamb cannot drag the wall's chain into rejection. Repairing the snapped
  line graph (`SketchWallGraph`, a 1 to 3 stroke tolerance ladder driven by leak signals) **is** the
  doorway detector: a doorway is a gap between collinear segments, a corner pen lift is an endpoint
  that missed a perpendicular line, and both tolerate the few-pixel offsets a photographed sketch
  always has. Rooms are the bounded cells of the closed arrangement (`SketchCellMap`), on
  centerlines **by construction**; the row sweep cuts a cell into at most 4 rectangles (root plus
  parts), bounding box plus warning past that. Openings are believed only after the mask verifies
  the gap and the cell map names its two sides (`SketchOpeningReader`); windows are the double-line
  marks the extraction already measured, trusted only with the outside on one side. Text, arrows
  and small symbols are removed first (`SketchMaskCleanup`: no long straight run and a small box);
  the stroke is **re-measured after that cleanup**, because every threshold scales with it. After a
  skew rotation the border ring is blanked: the seam against the paper fill binarises into bands
  that read as walls.
- **Door symbols are tolerated, not read** (`GapReadsOpen`): a gap passes clean at a tenth of the
  slab inked, and inkier gaps still pass when no blocked stretch along the wall exceeds `3*stroke`
  and the slab stays under 30 percent ink. Swing arcs, bifold zigzags and a route line walked
  through a doorway all pass; a shattered wall, hatching or a label lying along the line still veto.
- **Closets survive by their door**: a room under `closetMaxAreaMeters` (1.5 m2) with a verified
  door is kept, typed `storage` and named Closet (its own counter, reading order). A doorless room
  still needs `minRoomAreaMeters`; every room still needs `SketchRegularizer.MinRoomSide` per side.
  Short jambs (down to `2*stroke`) are accepted only when the smaller adjacent rect is at most four
  door widths squared, and that same test marks the gap `closet`.
- **The scale floor needs support**: the anchor floor is the smallest gap another gap backs within
  1.5x (a lone gap still anchors alone), interior non-closet gaps are preferred, and closet gaps
  stay out of the anchor set while any other interior gap stands.
- **One opening per span** (`SketchOpeningReader.Dedup`): where a window run overlaps a verified
  doorway on one wall line, the doorway is emitted and the window dropped.
- The one flip from `GetPixels32`'s bottom-up rows to the spec's top-down `y` happens once, in
  `ToGrayTopDown`. `alongFraction` pays the south→north flip in `Along`, with a vertical-wall test.
- **Not gated on calibration.** Uncalibrated, the scale is estimated from the lower median interior
  doorway at 0.813 m, **written back** as the calibration on success (sibling pages inherit via the
  never-calibrated-only rule), and the outcome line names the doorway count. Uncalibrated and
  doorless refuses and points at the scale wizard. The measured wall thickness cross-checks the
  estimate: walls past 0.35 m at the estimated scale add a warning, never a refusal (stroke width
  is a property of the pen).
- **Synchronous inside one `Tick`** behind `_pendingLocalGenerate`; `RunLocalGeneration` checks
  `Ctx.IsLocked` directly (the drawing-pass gate in `DrawGenerate` covers both buttons). Nothing is
  written until detect, frame and compile have all succeeded; then ONE `RecordEdit` covers the scale
  write-back and the install, so one undo takes back both.
- The regularizer envelope the detector may lean on is **±0.10 m** per coordinate
  (`SketchRegularizerTests`); the design note explains why the older ±0.15 figure is not guaranteed.
- Stated not-yets: interior double-lines (a doorway-width `dbl` run is NOT a sliding door: printed
  plans draw every wall double-line, so reading it would mint phantom doors), perspective de-skew,
  white-on-black plans, diagonal and curved walls, a plan drawn to the sheet's very edge, and a
  closed furniture outline long enough to read as wall, which bites its footprint out of its room.

**Stories: `Stories.cs`** (in `CXRAuthoring`, testable; `ResidenceStore` delegates to it).
- `ResidenceEditController.Level` is indexed by `_levelIndex` (the same index `ResidenceRenderer` already took).
- `ResidenceDoc.underlays` is one sketch per storey keyed by `levelId`; `Migrate` folds the old `underlay`
  in, idempotently.
- **A storey is a fact about the building**: `Stories.Add` writes an empty level into **every
  variant, sharing one id** (pairs storeys in `VariantDiff.MatchLevel`, keys the underlay, reports no
  change, needs no unlock).
- `VariantRevert` addresses the level a change was reported from; `VariantDiff.Change` carries
  `levelId`/`levelIndex` (stamped in `Compare`'s level loop; occupants and exterior are `-1`).
- `OccupancyModel.Pose.elsewhereInResidence` is the third answer beside present and away. The sensing
  rollups (`SensorCoverage.WholeResidenceCoverage`, ways out, `SensorCost.Of`) have `VariantDef` overloads
  that walk every storey in one pass (the system fee is charged **once** per building).
  `ReportCapture.Shot` carries a level index; one rebuild per (variant, storey).
- The floor chip cycles (no menu), is drawn only with more than one storey, is in the measured
  reserve, and switches via a deferred request.

→ [`docs/design/import-and-sketch.md`](../../docs/design/import-and-sketch.md)
