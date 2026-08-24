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
