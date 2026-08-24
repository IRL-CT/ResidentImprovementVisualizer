---
paths:
  - "Assets/Scripts/UITheme.cs"
  - "Assets/Scripts/UITooltip.cs"
  - "Assets/Scripts/HomeViz/ModeBand.cs"
  - "Assets/Scripts/HomeViz/TimelineBar.cs"
  - "Assets/Scripts/HomeViz/MeasureUI.cs"
  - "Assets/Scripts/HomeViz/HomeEditController.cs"
  - "Assets/Scripts/HomeViz/Tools/**"
  - "Assets/Scripts/Authoring/Interior/ScrubMath.cs"
---

# UI rules

> Loaded when a file under the paths above is read. Rules only: the reasoning is in the design note linked at the end. Edit this file when a rule changes; update CLAUDE.md only if something every session needs moves.

## UI rules

**House style: US English, no dashes, nothing said by contrast.**
- **US spellings in everything a user reads** (`meters`, `color`, `center`, `story`, `canceled`,
  `program`). The floor chip and every string about one say **floor**, or **story** where `floor`
  would collide with the surface underfoot.
- **No em dash (—) and no en dash (–) in a display string**, in the HTML report, or in the Anthropic
  prompt. A connector becomes a full stop, a colon or a comma; a composite name takes a comma or a
  `·`; a numeric range takes `-` in a compact readout (money, a clock range, a table cell) and the
  word `to` inside a sentence.
- **`"None"` is the empty-value placeholder** in a report table and a rail readout, never a bare `—`.
- **Say what is, not what it is not.** No "X, not Y", no "rather than", no "instead of" in anything
  on screen. State the rule positively and let the negative go unsaid.
- The historical display names in `SampleHomeInstaller.LegacyNames` are the one exception: they are
  keys matching homes already on disk, and they keep their em dashes forever.

**No prose on screen; a control names itself; sentences live on hover.**
- Nothing in HomeViz prints a sentence. Captions, hints and explanations are tooltips. Exceptions:
  content the panel exists to show (change list, a sample's blurb, a person's note, a proposal's
  description).
- Every editable control carries **one or two words inside itself, on the left** (`Thickness`,
  `Sill`, `Along wall`); read-only readouts use `UITheme.Value(label, value, tooltip)`. **The label is
  a name, never an explanation**, if it wants a verb or a clause it belongs in the tooltip. Said
  once: where an inline label says what a `UITheme.Header` said, the header went; the chip row's
  leading `Tool` label went too.
- **`UITooltip`** (`Assets/Scripts/UITooltip.cs`) is a hover tracker, not `GUI.tooltip`. It carries
  a **string, never a rect** (`Hover()` runs area-local, `Draw()` runs top-level at the end of
  `OnGUI`). `UITheme.Tip(text)` hangs a tooltip on the control just drawn; `Segmented` and
  `CommandBar` take a `string[] tooltips` overload. Delay `UITooltip.Delay` (0.45 s); suppressed while
  `GUIUtility.hotControl != 0`. `OverlayDraw.Tip` paints them with `Readout`'s chip painter.
- Where prose was the only thing in a panel it is replaced by a hoverable control, never a blank:
  fit refusals are `UITheme.Glyph("⚠", reason, Danger)` (a warning behind a hover is one nobody reads);
  `RefuseIfLocked` is **`UITheme.LockBadge`** (amber `⚠ Read-only` pill, full width, no button: the
  one switch is on the mode band; `SelectTool` draws the same badge above its read-only inspector); the calibration wizard uses
  `UITheme.Step(n, 3, …)` plus the prompt mirrored at the cursor; empty states are the action button;
  a destructive button states its price in its own label (`Reset (discards 3 proposals`)) or,
  where the label must stay short (Read this plan), in a `⚠` glyph tooltip beside it, and a
  two-click confirm's DangerButton always names the price in its label; scene-wiring
  faults are `Debug.LogWarning`.
- **Figures are the footnote**: a rail opens on the name, then controls, and closes on one muted
  `UITheme.MutedLine` of figures above Delete: *returned* by each `Draw*` and emitted by `DrawRail`
  after the switch, because four of them return early when the variant is locked. Readout chips over
  the scene no longer repeat figures; `SelectionOverlay` draws the outline + name only.

**Fields: `DragNumber`, `TextRow`, `Toggle`, `TextArea` share one chrome.** One reserved rect
(`RowH + 4`), the name painted inside on the left (`FieldInset`), the affordance in the right
`GlyphGutter` (`↔` / eye / `●○`), `BtnLine` rim, `Accent` edge and `Tint` wash when hot.
- **`DragNumber` is the one number control.** A field you type into that scrubs when dragged. Shift
  finer, Ctrl/Alt coarser; up/down nudge; Enter/click-away commit; Esc cancels; live value pinned at
  the cursor via `UITooltip.Pin`. **The timeline's clock is a `MeasureUI.Time` field** (the day
  scrubber); a click on the hour ruler jumps to that time; the ▲/▼ chip is the only expand control.
- **`UITheme.Toggle(label, value, tip)` is the one on/off control** in a rail: never
  `GUILayout.Toggle`, never a chip whose label or lit state carries a boolean. No `GetControlID`
  (the `StateRow` hit-test idiom). Chips remain for the command bar and the transport.
- **`UITheme.TextArea`** for the one multi-line field (a proposal's description). No raw
  `GUILayout.TextArea` / `TextField`.
- **One reserved rect, one `GUI.TextField`, in both modes** (swapping a label for a field changes the
  control count between layout and repaint). **Keys are handled before the field is drawn.** Focus is
  re-requested until it arrives. It is the one `GetControlID`/`hotControl` control in the project. The
  cursor is deliberately **not** captured (`ViewController.SyncCapture` is the sole arbiter of
  `Cursor.lockState`).
- `ScrubMath` (`CXRAuthoring`): the accumulator carries a value, not pixels; quantisation on the way
  out; **every field has a min and max**: a structure field's fixed bounds are the named
  `HomeConventions.MIN_*/MAX_*` constants, never inline numbers; angle and time-of-day wrap. Fast
  events accelerate superlinearly (`AccelPx`/`AccelMax`; never under Shift-fine) so a range wider
  than the screen is still draggable. `MeasureUI.DisplayStep` snaps the step to the roundest value
  in the unit on screen (nearest by ratio); a typed value is never quantised.
- `HomeEditController.TypingInUI` (`GUIUtility.keyboardControl != 0`) gates every global key.

**Layout discipline.**
- Anything that changes a panel's **height or control count** inside `OnGUI` is deferred to
  `Update`/`Tick` via a `_pending*` flag (`_pendingStage`, `_pendingLeftToggle`, `_pendingViewMode`,
  `_pendingTimelineToggle`, `_pendingVariantMenu`, `_pendingAlert`, `_pendingKeySave/Forget`, the
  generation `_genPhase` → `_railPhase` latch). Otherwise IMGUI throws `Mismatched LayoutGroup`.
- **Every panel rect is in the `PointerOverUI` test** (`_topRect`, `_modeRect`, the rails, the
  timeline). A panel that test does not know about is one every click falls through.
- **`UITheme.ContentWidth`** publishes the fixed width of the current container
  (`BeginPanel`/`BeginRegion`/`BeginScroll`); every fitting helper checks **`HasWidth`** first and
  takes the old natural-width path when none is published (the Site tool's `LibraryBrowser`). Never fit
  to a guessed width.
- `BeginScroll` passes `GUIStyle.none` for the horizontal bar **and** reserves `ScrollbarW` (16). It
  can never grow a horizontal scrollbar. `StateRow` wraps (explicit width; `reserveRight` for a ✕); `StateRowLine` is its one-line picker
  form (title fitted, full text in the tooltip, legal because the box is fixed).
  `UITheme.ChipRow` measures instead of wrapping every N. `UITheme.Segmented` fits itself: label share
  off first (plus margin), padding walks `SegmentPads` (10 → 7 → 5 → 3) before letters give way, then
  `Fit` with the full text in the tooltip. **`Fit` (ellipsis) only where a box is geometrically
  fixed**: a Toolbar cell, the timeline's roster rects, `LabelColumn`; wherever a row can grow, wrap.
- **The command bar spans the whole window** (`new Rect(0, 0, w, topBarHeight)`); rails start at
  `topBarHeight`. Its right-hand reserve is **measured** (`UITheme.MeasureBar`/`Measure` + one
  `UITheme.MarginW` per control); the view-mode control is sized from its labels; the eye-height and
  units chips are pinned to the wider of their two labels. Tab width ceiling =
  `max(TabIdeal 92, widest label + TabBreath)`, **no floor**: the eye-height chip shortens to
  `Seated` before a tab label is cut, then `UITheme.FitAll` trims labels with the full stage name led
  into the tooltip (`StageTips`). Full labels at 1280 and above.
- Shared geometry: `UITheme.FieldInset` 10, `LabelGap` 8, `GlyphGutter` 18. `LabelColumn` caps a
  label a little over half the box. The `↔` drag arrow sits in a gutter on the field's right.
- **The spacing scale is three steps and nothing else**: `UITheme.GapTight()` 4, `Gap()` 8,
  `Section()` 14: never `GUILayout.Space(n)`. **`Header` and `Foldout` own the space above them**
  (`SectionH` in their margin): no `Gap`/`Section` before either, including before a method that
  opens with one. `Divider` carries its own 7 px. Stacked button rows get one `Gap` between them.
  The command bar's gaps are `HomeEditController.BarGap` (= `SectionH`), counted once into the
  reserve and spent once in the draw.
- **Every glyph button (`✕`, `☰`, `‹`) is `UITheme.GlyphW` (26) wide**; a row sharing its line
  reserves `UITheme.GlyphReserve`. A row delete is `DangerButton`, everywhere. A small in-row action
  that is not a delete (the `↻` replace) is `UITheme.RoundGlyphButton`. GlyphW square on a circular
  face.
- **Three button surfaces, all visibly buttons**: `PrimaryButton` accent fill; `SecondaryButton`
  `Btn` fill + `BtnLine` rim; `GhostButton` an **outline** (clear fill, `BtnLine` rim, Ink text);
  `DangerButton` the same outline in `Danger`. Nothing clickable is borderless text: **an unselected
  `StateRow` carries the white-field + rim surface** (`_bandTex`), the active one the tint, a muted
  one the tile. On the mode band (whose washes equal `Btn`) actions are `UITheme.BandButton`
  (white field + rim) and the variant title is a pill only when switchable.
- **A stage's tool picker is `UITheme.Segmented`** (`DrawStageTools`, deferred via `RequestTool`),
  so "which tool" never wears the same accent chips as "what to place" under it; stages with one tool
  draw none. The opening-kind chip row is named `Add`.
- **Heights include the style's margin.** The command bar is `TopBarHeight` = `PrimaryH + Pad*2 +
  button margin` (computed, not serialized); the mode band's `VPad` leaves room for `BtnH` + margin;
  `TimelineBar.CollapsedHeight` / `ExpandedHeight(n)` are sums of its own row constants.
- Thumbnail grids share `UITheme.GridHeight` / `TileGap`.
- The collapsible left rail folds to 52 px; that and `TimelineBar.CollapsedHeight` account for
  `UITheme.Inset` taking `Pad` (14) off both sides.

The mode band, the overlays, text over the scene and the units chip are in
[`ui-chrome.md`](ui-chrome.md).

→ [`docs/design/ui.md`](../../docs/design/ui.md)
