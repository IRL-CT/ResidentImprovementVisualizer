# Design notes: the UI

> Stage naming, the Select tab, the measured command bar, `UITheme.ContentWidth`, `ModeBand`, the
> no-prose rule and `UITooltip`, `SelectionOverlay`, why figures stopped being the headline,
> `DragNumber`, the units chip, and text drawn over the 3D scene. The rules are summarised in
> [`.claude/rules/ui.md`](../../.claude/rules/ui.md) and [`.claude/rules/workflow-and-tools.md`](../../.claude/rules/workflow-and-tools.md); the reasoning lives here.

## Two of those tabs were called something else

**Sketch is now Import**, and **Draw is now Structure.** Both were named after a gesture, and in both
cases the gesture had stopped being the whole story.

A plan arrives as a photo, a scan or (most often) a **PDF**; "Sketch" named the least common of the
three, and the tab's own first button already said `Import plan…`. The word survives everywhere it is
still accurate: `UnderlayDef`, the `Sketch/` pipeline that reads a sketch IMAGE, and the prose in that
rail. It is the *stage* that is named for what you do with the tab, not for what the file happens
to be.

"Draw" was the same mistake one step further along, because since *Read the plan* landed, drawing is no
longer the only way walls, doors and rooms get made: `PlanBuilder` derives the identical graph from a
generated plan with no click at all. **Structure** names what the stage produces, the way `Furnish` and
`Smart living` do. Note the one thing it must not be read as: it does **not** reinstate `WallDef.structural`,
which was deleted precisely because nothing here can check a claim about the building. The tab is the
shell of the dwelling, not a load-bearing assertion about it.

`ResidenceWorkflow.Label` is `stage.ToString()`, so the enum member IS the tab label. Renaming the member
is the whole change, and there is no second table of display names to fall out of step with it.
`Structure` is 9 characters, the longest label in the bar, so it is the first one `UITheme.FitAll`
shortens below about 1100 px; `StageTips` leads a shortened tab's tooltip with the full name, which is
the mechanism that already existed for exactly this.

## Select is a tab, not a chip in every other tab

`Select` used to lead every stage's tool array. It is the pointer, not a phase of work, but as a chip
it was drawn six times, took digit `1` away from every stage, and still put the inspector off to one
side of whatever else was on screen. It is now `ResidenceStage.Select`, first in the enum and first in the
bar, and `PrimaryToolId` collapses to `ToolIdsFor(stage)[0]`. **Review therefore opens on Compare**,
not on the pointer: the exception that rule used to carry now has a tab of its own.

Two consequences worth knowing:

- **Four of the seven stages now hold exactly one tool**, so `DrawStageTools` returns early rather than
  drawing a lone permanently-active chip that cannot do anything. Draw and Review keep a chip row. Combined
  with dropping the stage header and tool header the rail used to reprint: both already highlighted in
  the command bar: the rail for most stages now opens directly on the tool's own controls.
- **`ResidenceEditController.TryAutoSelect`** carries you to the tab: clicking a piece of furniture, a wall
  mount or a resident from any other tab selects it and switches. It runs *before* the active tool and
  sets `ResidenceToolContext.ClickConsumed`, which `ResidenceToolBase.LeftClicked` reads: one line, so all nine
  tools respect it without knowing it exists. `IResidenceTool.ClaimsClicks` opts a tool out (always for
  Opening/Furniture/People/Outdoor, only mid-run for Wall/Room, only mid-wizard for Underlay).

  **The whitelist is load-bearing.** `RoomMeshBuilder` gives every floor a `ResidenceElementMarker`, so
  admitting `Floor`/`Room`/`Ceiling` would make every click inside an existing room select the floor
  instead of placing a wall corner: a bug that would read as "the wall tool stopped working". Walls
  and openings are excluded too, because clicking a wall is what the doors and windows tool is *for*.

**Esc is a two-rung ladder**: the first press deselects (what it has always meant, and a gesture used
constantly), and only with nothing selected does a second press leave Select for `_stageBefore`, the
tab you were working in.

Below the active tool the rail ends in one collapsed foldout, **Outdoors** (just the exterior on/off
toggle). The **Design options** foldout that used to sit beside it is gone: the variant list moved up
into the mode band and the change list became `CompareTool`. See *Which of the two jobs you are
doing*, below.

## The command bar spans the whole window, and the rails start beneath it

`_topRect` used to be the **gap between the rails**, and that is a budget that does not balance. At
1280 the gap is `1280 − 250 − 310 = 720`, and `UITheme.Inset` takes 28 more, leaving 692 px. Against
seven tabs wanting 448 and a right-hand cluster (the view-mode control, the eye-height chip, Undo and
Redo) wanting about 500. `GUILayout.BeginArea` **clips rather than scrolls**, so the overrun was silent
and it ate the right-hand end: Undo and Redo simply were not there.

The adaptive width that used to paper over this (`Mathf.Clamp((_topRect.width - 330) / n, 64, 92)`) 
was wrong twice over. It reserved a flat 330 for a cluster that wants ~500, and it divided
`_topRect.width` rather than the **inset** width, spending the same 28 px twice.

So the bar is now `new Rect(0, 0, w, topBarHeight)` and the rails start at `topBarHeight`. That is the
shape `UITheme.RailTop` has always existed for and the one the Site tool uses, so this restores a
shared convention rather than inventing one, and it takes the bar from 692 px to **1252 px** at the
same window size, which is what lets seven tabs render at full width with room over.

The reserve is now **measured** (`UITheme.MeasureBar` + `Measure`) instead of guessed, the view-mode
control is sized from its own labels rather than a hardcoded width (a guessed total clipped
"Walkthrough" at every window size), and the eye-height chip is pinned to the wider of its two labels
so toggling it stops shoving everything to its right. `UITheme.FitAll` then shortens tab labels as a
last resort below about 1100 px wide, with `StageTips` leading a shortened tab's tooltip with the full
stage name.

**Measured did not mean complete, and the gap was margins.** `Measure` reports a style's *padding* and
not its *margin*, and every control on this bar is drawn at an explicit width with its margin outside
that: six pixels a chip, six for the stage toolbar. The reserve carried a flat `+ 8` of slack to
stand for all of them together and was short by twenty to thirty, so the row overran the panel and
`BeginArea` clipped the right-hand end again. `UITheme.MarginW` publishes the number and the reserve
counts one per control; the `+ 12` already sitting on Undo/Redo was exactly this, done by hand for two
of them.

**And the tab width has no floor any more.** `Mathf.Clamp(…, 50f, 92f)` promised that seven tabs would
always be at least 350 px wide, and the only way to keep that promise in a window too narrow to hold
them is to push the right-hand cluster off the end, which is the failure the reserve exists to
prevent, reintroduced by the thing meant to prevent it. The **cluster** gives way first: the eye-height
chip is the widest thing on it and the only one with a shorter honest form, so it says `Seated` rather
than `Seated (wheelchair)`, and its tooltip is unchanged. Past that the tabs give up label text through
`FitAll`, which is a trim with the full name on hover: never a control that is simply not there.

## …and the CEILING is measured too, which took two guesses out at once

`92` was a guess made when every stage name was one short word, and it survived as a hard cap on tab
width. `Smart living` measures **101.5 px**, so `FitAll` cut it to `Smart li…` at *every* window size,
including one with a hundred pixels of bar standing empty to the right of Redo. A label trimmed to fit
a box that did not have to be that small is the same mistake as a reserve that was guessed at 330, and
this file has now made it three times.

The ceiling is `max(TabIdeal, widest measured label + TabBreath)`. It cannot eat the reserve: the
`Min` against `(inner − reserve) / n` still governs, so the extra width comes out of space that was
going to be blank. `TabIdeal` (92) is no longer a cap but the size tabs settle at when nothing needs
more, so a bar of short names looks exactly as it did.

**`TabTight` (56 px) is gone** with it. It was the threshold at which the cluster started giving way,
and it fired long *after* the tabs had begun ellipsising, so at 1280 the bar spent ~80 px printing
"(wheelchair)", a word already on that chip's own tooltip, and paid for it by cutting a stage name.
The chip now shortens when it would otherwise cost a whole label, compared against the widest label
rather than against a threshold: it buys a name when there is one to buy and stays long when there is
not. Deleted rather than left declared, on the `WallDef.structural` precedent: a constant nothing
reads is a threshold somebody later assumes is enforced.

Measured over the real styles: **full labels at 1280 and above** (the chip short at 1280, full from
1366), degrading through `FitAll` below that exactly as before, and strictly better at every width,
because the chip now gives way earlier than `TabTight` ever let it.

The **collapsible left rail** (`_leftCollapsed`, folding the library to a 52 px strip) is still there
and still useful, but it is no longer what makes the bar fit.

That 52, and `TimelineBar.CollapsedHeight`, both have to account for `UITheme.Inset` taking `Pad` (14)
off **both** sides: a panel sized to its content without that lands 28 px short and clips it.

## Nothing measured a string before drawing it: `UITheme.ContentWidth`

Every "it does not fit" bug here had one root: IMGUI cannot tell a helper how wide its container is
during the Layout pass, so nothing ever asked. But **we own every container**: each is a fixed-width
rail wrapped in `BeginArea(Inset(rect))`, so the width was always knowable, it was just never
published. `UITheme` now keeps a small stack of it, maintained by `BeginPanel`/`BeginRegion`/
`BeginScroll`, and read by everything that has to fit.

- **`BeginScroll` can never grow a horizontal scrollbar.** It passes `GUIStyle.none` as the horizontal
  scrollbar: the idiom the legacy Site rail and `FurnitureTool` already used, which ResidenceViz's own two
  rails never adopted: *and* reserves `ScrollbarW` (16). Both halves are needed: without the reserve,
  content laid out to the full view width overflows by exactly the vertical bar's width the moment
  that bar appears, which is a horizontal scrollbar appearing from nowhere.
- **`StateRow` wraps.** `_rowTitle` was explicitly `wordWrap = false`, and it draws every list title in
  the app. Residence names, sample names, occupant names, proposal names, `VariantDiff` change labels, all
  of them data. The two five-bedroom samples ship 37-character names in a box that holds 28, so they
  were cut mid-glyph with no ellipsis and no tooltip. It now wraps, and `StateRow` passes an explicit
  width, which is the other half: a word-wrapped label with no width still asks for its full natural
  width and the *row* grows sideways instead of the text moving to a second line. `reserveRight` is
  for the rows that share their line with a ✕.

  **`UITheme.HasWidth` is why this does not reach the Site tool.** `LibraryBrowser` calls `StateRow`
  too and draws its own `BeginArea`, so it publishes nothing; with no width on the stack `StateRow`
  takes its old path exactly. Natural width, no wrap, clipped at the panel edge. Fitting to a
  *guessed* width inside a panel this code cannot measure would be worse than not trying, so every
  fitting helper checks `HasWidth` rather than trusting `ContentWidth`'s default.
- **`UITheme.ChipRow`** replaces the hand-rolled "wrap every N" chip rows. N was picked by eye and four
  of the five rows never wrapped at all, while a four-chip row measures ~280 px in a 282 px rail,
  so one more chip would have pushed a control clean off the panel. It measures instead.
- **`UITheme.Segmented` fits itself**, which is what keeps the Smart living rail inside its panel. A
  `GUILayout.Toolbar`'s minimum width is the **sum** of its cells' natural widths, so a bar whose
  labels are wider than the rail does not shrink. It overruns and is clipped, silently. `Day ·
  Typical day / Day with incidents` wants ~225 px of a 221 px row, so it ran off the right-hand edge
  of a 310 px rail (so did the sensor package tier bar, at ~237 of 195, before that control was
  removed. See *The package tiers are gone* in [smart-living.md](smart-living.md)). `CompareTool` had
  already measured its own proposal names by hand; that job now lives in the control, which takes the
  row label's share off first (**including its margin**: the label is drawn at `LabelBarWidth` and
  occupies that plus margin), ellipsises each option into the cell it will actually land in, and leads
  a trimmed option's tooltip with its full name. Two callers keep the old path by the `HasWidth` rule:
  one that pins its own width (the command bar's view-mode picker, measured into that bar's reserve)
  and one whose container publishes none (the Site tool).

  **Padding gives way before letters do.** A segmented cell carries the button style's 10 px either
  side: 20 px a cell that says nothing, and 60 px across a three-way bar, which is most of a word. So
  the bar walks `SegmentPads` (10 → 7 → 5 → 3) and takes the roomiest padding at which every label
  still fits, and only ellipsises when even the tightest will not do. Graduated rather than one step
  so a bar gives up exactly as much air as it needs: the monitor's `Viewing as` clears `Resident` at
  5 px where it would not at 10, and no other bar in the app moves at all. Measured against the real
  `PublicSans` metrics, **nothing in the Smart living rail is trimmed**: `Day` keeps the full 10 px,
  `Viewing as` takes 5. The one ellipsis left anywhere is Compare's proposal picker on a
  long user-typed name, which is unbounded data and the case `Fit` exists for.
- **`UITheme.Fit`** is the ellipsis, and it is used **only** where a box is geometrically fixed and
  cannot grow: a Toolbar cell, the timeline's hand-computed 18 px roster rects, and a control's own
  inline label (`LabelColumn`, capped a little over half the box so the value it names can never be
  pushed off the right edge). Everywhere a row can get taller, the answer is wrap, so that no name is
  ever hidden. Where `Fit` does trim, the caller puts the full string in the tooltip: the same rule
  that lets every *explanation* in this app live on hover.

The timeline had a related bug of its own: `trackX`/`trackW` were computed from `inner` **outside** the
scroll view while the rows were drawn **inside** it, and the hour ruler and now-line used the full
width. With five or more occupants the vertical scrollbar appeared and the gantt ran ~15 px wider than
its visible track, drifting out of alignment with both and losing late evening under the bar. The
scrollbar is now accounted for once, before any of the three is drawn.


## Which of the two jobs you are doing: `ModeBand`

ResidenceViz is two editors wearing one face. **Correcting the record**. Making the model match the residence
that actually exists, and **proposing a change** to it are different acts with different
consequences, and the app used to look identical in both.

The state was never missing. `VariantDef.isBaseline` and `VariantDef.locked` spell all four
combinations, and the old variant panel composed them into a string. That string lived in a foldout
that was collapsed by default, at the bottom of the right rail, opened automatically only in Review.
The only ambient signal was a tool refusing to work because the variant was locked, which reads as a
malfunction rather than as an answer. Switching variants meant: go to Review, scroll, expand, click.

`ModeBand` is a permanent colour-coded strip between the command bar and the scene:

| `isBaseline` | `locked` | Reads | Actions |
|---|---|---|---|
| yes | yes | `EXISTING RESIDENCE · LOCKED`, slate | `Correct the record`, `Propose a change` |
| yes | **no** | `RECORDING THE EXISTING RESIDENCE`, **amber** | `Done. Lock the record` |
| no | no | `PROPOSAL A ▾ · 11 CHANGES`, accent | `Compare`, `Report` |
| no | yes | as above plus a `Locked` badge | `Unlock`, then as above |

**Amber is for editing reality**, and that is the whole point of the file. Working in a proposal is
the routine case and gets the accent; unlocking the baseline is rare and consequential, because an
accidental edit there silently corrupts the record every proposal is measured against and nothing
downstream would ever complain. It is not an *error*, so Danger would be a lie. Hence `UITheme.Warn`,
a new token sitting between `Ok` and `Danger`.

**`isBaseline` and `locked` are read separately, never collapsed into one "mode" field.** They are
orthogonal and all four rows above are reachable: a locked proposal is a normal thing to want, and an
unlocked baseline is the reason the band exists.

**The verbs do the clarifying work.** `Correct the record` and `Propose a change` name the two jobs;
"Design options" named neither, which is why that label is gone.

Mechanically it follows `TimelineBar` and `SelectionOverlay` exactly: a plain class the controller
owns and draws from `OnGUI`, everything re-derived per frame (undo restores the whole `ResidenceDoc`
without notifying anyone, so a held `VariantDef` would describe a variant that no longer exists). Two
things it must keep:

- **`_modeRect` is in the `PointerOverUI` test.** A panel that test does not know about is a panel
  every click falls through. Here, pressing `Propose a change` would also place a wall.
- **`_pendingVariantMenu`** joins `_pendingStage` / `_pendingLeftToggle` / `_pendingViewMode` /
  `_pendingTimelineToggle`. Expanding the band into its variant list changes its height *and* its
  control count, which mid-`OnGUI` is the `Mismatched LayoutGroup` this file's whole deferral
  discipline exists to prevent. `RequestVariant` and `RequestReport` are deferred for the same reason
a variant switch rebuilds every GameObject in the residence, and a report renders a camera.

It draws **no subtitle**, deliberately. Per the no-prose rule below, the explanation is the title's
tooltip; a band printing "how the residence actually is today" under its own heading would be exactly the
paragraph the rest of the UI had just removed.


## A control names itself; only sentences live on hover: `UITooltip`

**Nothing in ResidenceViz prints a sentence any more.** Every caption, every hint, every paragraph is a
hover tooltip. The one exception is content the panel exists to show: the variant change list (what
gets read aloud in a meeting), a sample's blurb, a library row's version, a person's own note.

**But a control's NAME is not a sentence, and it is not on hover.** That distinction is new, and it
is the correction to a rule that had been taken one step too far. The first version of this section
removed *every* caption, and for a read-only figure that was right: a rail of numbers under headers
reads fine, and the revert switch below was left in place in case it did not. For an *editable*
control it was wrong, and measurably so rather than as a matter of taste:

> `SelectTool.DrawResizeControls` drew **three identical boxes** (width, depth and height) with
> nothing on screen distinguishing them. `OutdoorTool` drew a ramp's width directly above its rise the
> same way. `PeopleTool` drew a name field and a note field in one panel and a start/end pair in
> another. A tooltip cannot fix any of those, because the question is not *"what does this one do"*
> but *"which of these three is which"*, and answering it on hover means hovering all three and
> remembering.

So every control that can be edited now carries **one or two words, inside itself, on the left**,
`Thickness`, `Sill`, `Along wall`: with the sentence still on hover, unchanged. Read-only readouts
took the same treatment in the same pass (`UITheme.Value(label, value, tooltip)`), because labelling
only the editable half leaves a rail alternating a named box with an anonymous number, which reads as
though the app forgot to finish the second one.

**The label is a name, never an explanation.** `Width`, not `How wide this is`; `Facing`, not `Which
way this faces. Drag the box, or type an exact bearing.`: that second string is still the tooltip.
If a label wants a verb or a clause, it is prose and belongs on hover.

**And it is said once.** Wherever an inline label now says what a `UITheme.Header` said, the header
went: `Room type`, `Railing type`, `Surface`, `Snapping`, `Placement`, `Drawing`. Headers
that group several differently-named controls stayed.

**Which is also where the rule stops.** **`Tool`**, the leading label on the stage's tool chip row, was
naming something already named and charging real width for it in a 266 px rail. Every chip on that row
IS a tool's name, so the label said only that a row of tool names is a row of tool names: for ~45 px.
Structure's three chips (`Walls`, `Doors & windows`, `Rooms`) measure 245 px without it and 290 with, so
removing it is also what takes that row from two lines to one. (The sensor tier bar's `Package` label
was the second case of this, and is gone with the bar itself.)

The test is not "does this control have a name" but "does anything on screen already say it". A
`Thickness` field beside a `Height` field does not, and keeps its label.

Two mechanisms, and one deliberate constraint.

**`UITooltip`** (`Assets/Scripts/UITooltip.cs`) is a hover tracker, **not** built on `GUI.tooltip`.
That mechanism only fires for controls drawn from a `GUIContent` carrying a tooltip, and every
`UITheme` helper takes a bare `string`, so adopting it would have meant changing all ~150 signatures
(the legacy Site tool's call sites included) and would still have handed back only a string, never the
rect. The manual pattern below is the one `UITheme.StateRow` already proves works in this exact nesting
of `BeginArea` inside `BeginArea` inside `BeginScrollView`.

The whole design turns on one thing: **the tracker carries a string, never a rect.** `Hover()` runs
inside a layout area, where `Event.current.mousePosition` is area-local and the caller's rect is in the
same space. `Draw()` runs at the very end of `OnGUI`, after every `EndArea`, where it is top-level.
Both are correct, and there is no coordinate conversion anywhere in the file.

`UITheme.Tip(text)` hangs a tooltip on the control just drawn, via `GUILayoutUtility.GetLastRect()`.
Being a separate call rather than a parameter is what let the whole app gain tooltips with **zero
changes to any existing `UITheme` signature**, so the Site tool is untouched by construction. It works
after raw `GUILayout.Toggle`/`TextField` too. Two things need more: `Segmented` and `CommandBar` draw N
controls inside ONE layout rect, so both gained a `string[] tooltips` overload that slices that rect
back apart (a `Toolbar` divides evenly, so it is exact).

Tips wait `UITooltip.Delay` (0.45 s), re-arm when the cursor moves to a different control rather than
swapping text instantly, and are suppressed while `GUIUtility.hotControl != 0`: a tip popping up
mid-slider-drag sits under the cursor obscuring the value being dragged.

`OverlayDraw.Tip` paints them, sharing `Readout`'s extracted chip painter: same shadow, chip, hairline
and window clamp, and crucially the same **explicit light `textColor`**, which per that file's own
header is the only thing stopping `UITheme`'s near-black `Ink` tinting through onto a dark chip. It
differs from `Readout` in wrapping at a max width and using the sans face: a readout is `3' 6" · 90°`
and a tooltip is a sentence, and `CalcSize` on a sentence produces a chip wider than the screen.
`DrawStatus`'s toast now goes through the same renderer, which fixes a second-order bug. It was a bare
`GUI.Label` inheriting `Ink` straight onto the 3D scene, i.e. invisible against a wall.

**`UITheme.Value(value, tooltip)` is still the revert switch**, and still worth keeping: every *unlabelled*
readout funnels through it with `Note(tooltip)` commented out inside, so a rail that turns out to want its
sentences back is one line away from having them. What it no longer has to carry alone is the naming
job: `Value(label, value, tooltip)` is the three-argument overload the readouts moved to, and the
two-argument form survives for the rows whose value names itself (a filename, a person's day block, an
alert's own title) and for the legacy Site tool, which is why it was never changed rather than replaced.

**The geometry is shared, so everything lines up.** `UITheme.FieldInset` (10 px), `LabelGap` (8) and
`GlyphGutter` (18) are what a labelled field, a labelled text row, a labelled readout, a labelled
segmented control and a labelled chip row all measure from: a name that started three pixels off its
neighbour's would undo most of the point. `LabelColumn` caps a label a little over half the box and
ellipsises through `Fit` to get there, so a long one can never push the number off the right edge;
per `Fit`'s own rule the full string is in the tooltip, which every one of these call sites already
had. **The `↔` drag arrow moved from the left of a number field to a gutter on its right**, which is
the space the label took.

`UITheme.MutedLine` is the compact measurement summary at the foot of an inspector: `_numSmall`'s
tertiary ink, left-aligned and **word-wrapped against an explicit `ContentWidth`**, guarded by
`HasWidth` exactly as `StateRow` is. Both halves are needed for the same reason they are there: a
word-wrapped label with no width asks for its full natural width, so the row grows sideways instead of
the text moving to a second line.

**Where prose was the only thing in a panel, it is replaced by a hoverable control, never by a blank
rectangle.** A blank panel reads as a bug, and a message nobody can reach is worse than no message:

- Fit refusals and the narrow-walkway check are `UITheme.Glyph("⚠", reason, Danger)`: the glyph is a
  signal, not a sentence, and stays visible. A warning behind a hover is a warning nobody reads.
- `RefuseIfLocked` is a `Locked` badge **plus an Unlock button right there**. The sentence was only ever
  telling you to go and do something elsewhere; the refusal is now actionable.
- The calibration wizard gets `UITheme.Step(n, 3, instruction)` (a compact `1 / 3` indicator) **and**
  the prompt mirrored at the cursor over the image via `OverlayDraw.Readout`, which is where you are
  actually looking while the app waits for a click. Stage 1 previously had nothing on the image at all.
- Empty states are the action button already in the panel ("New residence", "Start from a floor plan",
  "+ Add person"), carrying the sentence as its tooltip.
- The reset confirmation puts its cost in the **button's own label** (`Reset, discards 3 proposals`).
  A count is not a subtitle, and a destructive button that states its price beats one that does not.
- The two "no palette is wired" messages are now `Debug.LogWarning`. They are scene-wiring faults,
  nobody using the app can act on them, and whoever can is reading the console.

## Seeing what is selected: `SelectionOverlay`

Selecting something used to change the **rail and nothing else**. On a plan with forty walls, clicking
one and reading *"Wall: 12' 4""* in the inspector left no way to tell *which* wall you had short of
deleting it and undoing. `SelectionOverlay` draws the selection in the scene: a haloed outline, vertex
handles, and its **name**: a wall at its **true thickness including the junction extensions** (so the
highlight traces the box actually on screen, shared-corner overlap and all), a room's polygon, an
opening's span across its host wall, furniture's **truly rotated** footprint.

The chips used to carry a figure as well: the wall's length and thickness, the room's area and
turning-circle diameter with a ring drawn around it, the item's footprint. Every one of those was a
number the rail was *already* printing, drawn a second time over the plan. The outline is what answers
"which one"; the figures now live in the rail, once. The geometry is untouched, and that matters: the
outline is the whole reason this file exists.

Two placement decisions, both load-bearing and both the same shape as the `OccupancyClock` one:

- **Not in a tool.** `ResidenceEditController` gates `IResidenceTool.DrawOverlay` on `!PointerOverUI`, so an
  overlay drawn from `SelectTool` blanks the instant the cursor reaches the rail, which is exactly
  where the cursor goes to read the inspector describing what was just selected. It is drawn from the
  controller, outside that guard and *before* the tool overlay so a live preview stays on top.
- **A plain class**, like `TimelineBar` and `UITheme`. Selection already lives on the controller so
  the rail can describe it whatever tool is active; the highlight follows the **selection**, not the
  tool, and needs no scene wiring to do it.

Everything is re-derived from the schema every frame rather than cached, because undo restores the whole
`ResidenceDoc` without notifying anyone: a cached `WallDef` would be a stale object outlining a wall that no
longer exists. The handle layer will have to hold ids for the same reason.

`OverlayDraw` grew `Polyline`, `Haloed` and `Handle` for this. `Haloed` draws the outline twice, a wide
dark pass then the colour, because one pass is invisible against a floor finish of similar tone and the
highlight has to read on oak, on white vinyl and against the dark ground pad.

## Numbers stopped being the headline

The app reported a figure for everything, everywhere, permanently. Clicking a wall led with its length
and thickness; a room led with its floor area *and* the diameter of the largest turning circle that
fits in it, with that circle drawn as a ring in the plan; a chair led with its footprint. Each was then
drawn a **second** time as a readout chip over the scene. Below that, the inspector offered typed
exact-entry fields (`12' 6"`) for X/Z position and for W/D/H, on top of the sliders that already set
the same three dimensions.

That is an *audit* reading of the tool. The work it is actually for is arranging a residence so a wheelchair
fits through it, and that is done by looking at the plan. So the order inverted: **name first, controls
second, figures last**, as one muted `UITheme.MutedLine` above Delete. Nothing was lost: every number
is still exact and still on screen. It simply stopped being the first thing said.

Note what this does **not** mean, because the first pass got it wrong: demoting a figure is not the same
as anonymising a control. The summary line at the foot is still the right place for a wall's length, and
the controls above it now each carry their own one-word name inline. See *A control names itself*,
above. The two rules point the same way. A rail that opens on `Thickness` and `Height` and closes on a
muted `12' 4" · 4 1/2"` is saying the figures are the footnote; a rail of three unnamed boxes was saying
nothing at all.

Four things this needs that the old arrangement did not:

- **The summary is RETURNED by each `Draw*`, not drawn by it.** Four of the six return early when the
  variant is locked, and locked is the **default** state of every residence in the library: a line emitted
  inside them would have been invisible in the common case. `DrawRail` collects the `(line, tip)` pair
  and emits it after the switch, which is also what keeps it below the controls rather than above them.
- **The step-free badge stays a badge.** It is a verdict, not a figure, and it is the one thing about a
  doorway worth seeing without reading. Folding it into the summary line would have buried the single
  most consequential fact the inspector knows.
- **The turning circle is gone from the rail, the scene and the report**, and survives only in
  `MeasureTool`. `ResidenceMetrics.LargestInscribedCircle` is *not* dead and must not be removed:
  `OccupancyModel` stands people with it and `ViewController.StandableStart` spawns the walkthrough
  with it. Only the display call sites went.
- **`MeasureField` and `DrawMoveControls` are deleted.** Typing a raw X/Z coordinate was never how
  anyone positioned a chair, and the Move mode's rail is now empty on purpose: the segmented control
  above it carries the gesture in its own tooltip and the gizmo in the plan is the mover. That is not a
  blank panel in the sense the no-prose rule forbids; there is still a control there.

## One number control: `UITheme.DragNumber`

Editing a number used to take one of **five** widgets. A `-/+` stepper (14 sites), a bare slider (2),
a slider with a hardcoded unit suffix (5), a hand-rolled clock stepper (2), and: in exactly two
places: a text field you could actually type into. So the two gestures anybody wants, *type the value
I know* and *push it and watch it move*, lived in different controls and you mostly got one of them.

`DragNumber` is both at once: a field you type into, that scrubs when you drag across it. Click and
drag to push the value, click to type, Enter or click-away to commit, Esc to cancel, up/down to nudge.
**Shift is finer and Ctrl/Alt coarser**: the `SelectTool` arrow-nudge convention, not the gizmo's
Shift-snaps, because this is a rail control. The active field is unmistakable: an accent wash, a
two-pixel accent border, and the live value pinned at the cursor through `UITooltip.Pin`, which is
where the eye is during a drag.

**It takes a label as well as a tooltip**, and draws the label inside itself on the left while the value
stays mono and right-aligned. The label is measured against the rect layout actually handed back, so it
is computed *after* `GetRect`, which is safe only because that rect comes from an explicit height, an
expanding width and `GUIContent.none`, none of which the style's padding can move. `padding.left` is then
rewritten on `_dragBox` / `_dragBoxOn` / `_dragEdit` per call; they are shared statics consumed
immediately after, so a per-call assignment cannot leak into another field.

**The label survives every state, including typing**, which is the one place it parts company with the
`↔` arrow beside it. The arrow is an affordance and the caret replaces it, so it hides; the label is the
field's identity, and a box that forgets its own name the moment you click into it is the bug the whole
thing exists to fix. `_dragEdit`'s caret therefore starts *after* the label rather than under it.

**No control was added to do any of it.** The label is a `GUI.Label` and a `GUI.DrawTexture` inside the
existing `Repaint` branch, so the Layout and Repaint passes still agree on one `GetControlID` and one
`GetRect`: the invariant the next bullet is about.

Four things in that file are load-bearing:

- **One reserved rect, one `GUI.TextField`, always.** The obvious build swaps a painted label for a
  TextField when the field gains focus, and that changes the control count between the layout pass and
  the repaint pass: the `Mismatched LayoutGroup` the whole `_pendingStage` deferral discipline exists
  to prevent. The rect is reserved once and the TextField drawn into it in *both* modes; only the
  string and the styling differ, so the two passes agree by construction and no `_pending` flag is
  needed.
- **Keys are handled BEFORE the field is drawn.** Draw first and `GUI.TextField` has already consumed
  the event and flipped `e.type` to `Used`, so Enter, Escape and the nudge keys all silently do
  nothing. Taking the four we own up front leaves every other keystroke to the editor.
- **Focus is re-requested until it ARRIVES.** `GUI.FocusControl` does not take effect within the pass
  that calls it, so clearing the request immediately lets the commit-on-blur test fire on the same
  frame. Focus the field, observe it is not focused yet, commit. Clicking into a box would do nothing.
- **It takes `hotControl`**: the first `GUIUtility.GetControlID` control in the project, where every
  other custom hit-test is `StateRow`'s `GetLastRect` + `Contains` + `Use`. It earns the exception:
  `UITooltip.Draw` already bails while `hotControl != 0`, so a scrub gets tooltip suppression free.

**The cursor is deliberately not captured.** Locking it would give infinite travel, but
`ViewController.SyncCapture` is the sole arbiter of `Cursor.lockState` and releases whenever neither of
*its* two drags is live. Dropping the lock mid-scrub and warping the pointer to a position this
control never recorded. A window's width of travel is enough.

`ScrubMath` (`Assets/Scripts/Authoring/Interior/ScrubMath.cs`) is the arithmetic, in `CXRAuthoring` for
the reason `SampleRefresh` and `Stories` are: so it can be tested at all. **The accumulator carries a
value, not a pixel count**. Summing pixels and converting at the end looks equivalent and is not,
because the rate changes the instant Shift goes down and the whole total would be re-scaled
retroactively, yanking the value backwards under a stationary cursor. Quantisation is applied on the
way *out* for the mirror reason: rounding on the way in makes every sub-step motion vanish, so a fine
drag under a coarse step travels nowhere however far the pointer goes.

**The quantum follows the display, and the caller does not choose it.** Every step in the app was
picked while the display was imperial (0.003 is an eighth of an inch, 0.012 a half, 0.025 an inch) so
in metric a scrub ticked 3 mm, 12 mm, 25 mm. Arithmetically fine and visibly wrong, because the point of
a quantum is that it lands on numbers you would say out loud. `MeasureUI.DisplayStep` takes the *size*
of step a call site asks for and snaps it to the roundest value of about that size in the unit actually
on screen, **nearest by ratio rather than by difference** (the candidates span 3 mm to a metre, and an
absolute comparison snaps every small step to the smallest one in the table). Only the drag and the
arrow nudge are affected: a typed value is never quantised (`Commit` settles with step 0) so `32"`
still lands on exactly 0.8128 whichever system is showing.

**Every field now has a min and a max.** None of the 14 stepper sites had either, so pressing the minus
button enough times drove wall thickness, opening height and walkway width negative with nothing
anywhere complaining. `Settle` clamps, or *wraps* for the two circular ranges: an angle and a time of
day: where 359 degrees to 1 is two degrees of travel rather than a 358-degree spring back.

Two consequences worth knowing. **`ResidenceEditController.TypingInUI` had to exist**: `HandleGlobalKeys`
and `SelectTool.HandleTransformKeys` read digits, Esc, F, arrows and Z/X unconditionally, which was a
latent bug (typing `z` into a room name rotated the selected chair) and becomes a constant one with
~25 focusable fields in the rail. `ViewController` and the legacy `EditController` have each declared
that one-line property since they were written. And **an opening finally has a width control**: the
28/30/32/34/36" chips were the only way to set one, so no non-preset width was reachable from the UI
at all. Those chips are now **gone**, in both rails: a measured field had been added directly beneath
them, so one dimension was offered twice in two idioms, and the five sizes people ask for are not the
sizes buildings have. 32" and 36" are ADA spec *names* rather than measurements and that is still
true. It just belongs in the tooltip and the clear-passage readout, not in a control that cannot
express a 34 1/2" doorway measured off a real residence.

## The units chip, and why the default is metres

`ResidenceSettings.metricUnits` has existed since the store was written and is wired to `Units.Display`
through `ResidenceStore.ApplySettings`. **It had no UI**, so the only way to switch was to hand-edit
`settings.json`.

Worse, the app was showing *both* systems at once. The old `UITheme.ValueStepper` and `ValueSlider`
took a literal unit suffix and never consulted `Units.Display`, so every caller passed `" m"` and
printed raw metres, while every read-only value beside them went through `Units.Format` and printed
feet and inches. In the resize panel that put three sliders reading `0.61 m` directly above three
fields reading `2' 0"`, for the same three dimensions. Both are gone; `MeasureUI` is now the only way
a number reaches the screen, and it renders every one through `Units` or `Clock`.

**Display defaults to metres, and that is a change.** It defaulted to feet-and-inches because the
audience is US shared homes and assisted living, where every dimension that matters: a 32" clear
doorway, a 60" turning circle. Is spoken in inches. Those figures have not stopped mattering and the
chip is one click away. What changed is that numbers are now *dragged*, and a value scrubbing from
`3' 11 5/8"` to `4' 0 1/8"` changes four glyphs at once where `1.21 m` to `1.22 m` changes one digit.
A unit you can read while it moves beats one you can quote afterwards.

A plain default only reaches new installs: every `settings.json` already on disk records the old
imperial default explicitly, so `ResidenceStore.MigrateSettings` carries `unitsDefaultVersion` and flips
those over once. It is the one place here that overwrites a stored preference, and it is a move of the
DEFAULT rather than of the user's choice: the chip still switches back, and the version guard means it
never fires twice.

**`Units.BareUnit.FollowDisplay` is now what the typed fields pass.** `WallTool`'s run length and
`UnderlayTool`'s calibration both hard-coded `BareUnit.Feet`, so typing a bare `3` meant three *feet*
even in metric. `Units` already had the right answer. It was simply never asked for. Every length
field routes through `MeasureUI.Length`, so this is fixed everywhere at once rather than per call site.

The chip sits in the top bar beside the eye-height chip, **pinned to the wider of its two labels** for
the identical reason that one is: an unpinned chip that changes label shoves Undo and Redo sideways
every time it is clicked. `unitsW` is added to `DrawTopBar`'s measured `reserve`. It needs no deferral
unlike the stage and view-mode pickers it changes neither the bar's height nor its control count.

**It switches the clock too, and that is deliberate.** `Clock` reads `Units.Display` for 12- versus
24-hour, which that file's header and the *Units* section of CLAUDE.md both describe as the same preference
applied to time. Metric therefore implies a 24-hour timeline, which the new default now makes the
out-of-the-box state. The chip's tooltip says so rather than letting it surprise anyone. Decoupling them means giving `Clock` its own flag.

## Buttons stopped looking like text, and the rails got one spacing scale

Three complaints arrived together: *buttons look like text*, *things are weirdly spaced*, *the
value controls are not the same everywhere*, and they had one root each.

**A borderless button is a caption to anyone who has not hovered it.** `GhostButton` was `Ink2` text
with no background, which is exactly the look of a hint, and it drew Export, Star, every Cancel,
Undo and Redo. `DangerButton` was the same in bold red: a warning label, not a control. And
`SecondaryButton`'s fill (`#F1F1EE`) was two hex points off the mode band's `Tile` wash and four off
the card, behind a 14 % hairline; in the default Base mode the band's two actions were essentially
invisible as buttons, and the band's title (the one control that switches variants) was a label
style painted in `Ink2` with a trailing `▾` as its only tell. So: `Btn` darkened, a `BtnLine` rim at
22 % on everything pressable, ghost and danger became **outline** buttons (the rim is the
affordance; the clear fill keeps them below Primary and Secondary), the band got `BandButton` (a
white field, because white is the one surface none of its three washes share) and its title is a
pill only while it can be clicked. Unlit chips went from `Ink2` to `Ink` for the same reason.

**The spacing was nine numbers picked by eye, stacked on margins nobody counted.** 78 `Space()` calls
across 2/4/6/8/10/12/14/16/18, a `Space(8|10)` on top of a `Header` that already carried 10 px, and
button rows that touched (the left rail's Save/Export/Reset/Archive, Compare's Generate + Take back,
Import's Replace + Remove, the PDF picker's three). The scale is now `GapTight`/`Gap`/`Section`
(4/8/14), `Header` and `Foldout` own the space above them so no caller adds any, and the command
bar's gaps are one `BarGap` that the reserve arithmetic and the draw code both read. They used to be
literals kept in step by hand. Every `✕` is `GlyphW` wide (they were 24, 26 and 28 beside reserves of
32 and 34), and the three thumbnail grids share one height.

**Booleans had three widgets, and the day had a slider.** Six settings were Unity's default grey
checkbox, two were a lit chip and one a chip whose label changed with its state. `UITheme.Toggle` is
one labelled row in the fields' own chrome (name left, `●`/`○` in the gutter, tint wash when on) 
so a setting lines up with the number above it. The timeline's scrubber was the last
`HorizontalSlider` in ResidenceViz, beside a read-only time: the one number you could not type. It is a
`MeasureUI.Time` field now (six pixels a quarter-hour makes the row's width most of a day; Ctrl is
coarser), the ruler jumps to a click (that used to expand the bar, which is the chevron's job) and
the speed chip wraps instead of sticking at the top after four presses. The two typed-length paths
that still parsed `BareUnit.Feet` (exactly the bug `MeasureUI`'s header said was fixed) now follow
the display. The proposal description, the one unstyled control left, wears `UITheme.TextArea`.

## Text over the scene has to carry its own contrast

Everything the app draws *into the 3D view* sits on a background the app itself picks and the user
cannot change. Wall grey, an oak or white-vinyl floor finish, the dark ground pad, a catalog swatch.
Both kinds of text there were failing on it, for different reasons:

- **`OverlayDraw.Readout`** (the measurement chip every tool uses) drew near-black text on its own
  near-black chip. `GUI.color` **tints** a style's colour rather than replacing it, so setting it to
  white left the ambient style's `textColor` (`UITheme.Ink`, because every rail is light paper)
  near-black. The chip now owns a `GUIStyle` with an explicit light `textColor` and the mono face, and
  is clamped into the window: the readout that runs off the right edge is the one you most want to
  read, because that is where you are drawing to.
- **The world labels on placeholder boxes and occupant markers** were near-black `TextMesh`, i.e.
  invisible against the wall directly behind the furniture they name. They are now light text with a
  dark stroke: the subtitle convention, which reads on any backdrop because it brings both ends of the
  contrast range with it.

`LabelOutline` is that stroke: four copies of the same `TextMesh`, offset by a fraction of
`characterSize` (**not** meters: the label over a grab bar and the one over a bedroom are the same
size in pixels and different sizes in world units). It keeps the **shared font material**, so the
stroke can never separate from the glyphs: same ZTest, same queue, and: the reason a copied material
with a lower `renderQueue` was rejected: the built-in font is *dynamic*, so its atlas is replaced as
new glyphs are requested and a copy would keep drawing from the old one. Order is forced with
`sortingOrder = -1` instead. It re-syncs on text change only, because occupant labels rewrite
themselves every time the clock moves someone and assigning `TextMesh.text` regenerates its mesh.

### …and a label nobody can read is not drawn at all

Every item, every device and every resident carries one, so a five-bedroom residence draws well over a
hundred names at once. Pulled back far enough to see the whole dwelling they overlap into a band of
grey mush that hides the plan they are annotating and cannot be read anyway. So `LabelBillboard` hides
a label once it would draw shorter than `minPixelHeight` (11 px) on screen, and re-shows it on the way
back in, with 15% of hysteresis so one sitting exactly on the threshold does not flicker every frame
the camera breathes.

**Apparent size, not distance**: this is the whole reason the test is not a `Vector3.Distance`. What
decides legibility is how big a label lands on screen, and a distance cull answers that only by
accident: pulled back far enough to see a whole dwelling, a hundred labels overlap into grey mush while
every one of them is still "near". Screen height in pixels is the one question the overview and the
walkthrough both answer, so there is no separate threshold per view mode, and it is what keeps the
rule honest under an orthographic camera, where distance says nothing at all and the zoom says
everything. Height comes off the **generated
mesh** rather than from `characterSize` × `fontSize`, whose mapping is an undocumented internal
constant, and the bounds are honest about a label that wrapped onto two lines.

`lockUpright` and the flat-on-the-ground branch it gated went out with **Plan**: they only ever fired
for an orthographic `Camera.main` looking straight down, and there is no longer one. Under free-look the
plain billboard already lays a label flat as the camera comes overhead, which is the same picture by a
shorter route. The orthographic branch of `PixelsPerMeter` stays: that is the *legibility* rule, and
it is what makes the file independent of which projection is on screen.

Two things follow from the shape of a label rather than from the rule:

- **Renderers are toggled, never the GameObject.** A deactivated label gets no `LateUpdate`, so it
  could never decide to come back: the same reason `OccupancyClock` cannot live in a tool.
- **The stroke is built lazily by `LabelOutline`, on ITS first `LateUpdate`**, which may land after the
  billboard's. So the child count is watched alongside the visibility state; without that, a label
  hidden before its stroke existed renders as a dark smudge with no text in it.

## Typing walked the camera: `TypingInUI` is latched in `OnGUI`

Every key the app reads was already gated on `GUIUtility.keyboardControl != 0`, and it still
happened: a name typed into the People rail panned the overview. The guard was read from `Update`,
and in the Editor that static is only dependably the game view's focus state *inside* an OnGUI pass,
read between passes it can answer for whichever IMGUI context last ran. So `ResidenceEditController`
latches it once per `OnGUI` (exactly the arrangement `PointerOverUI` has always had), and
`ViewController` and `ResidenceToolBase.KeyDown` read the latch. The tools gained the gate at the same
time: their `HandleInput` runs only with the cursor off the rails, but a field keeps focus wherever
the cursor goes, so Z/X, Delete and the wall tool's typed digits fired under a focused field.

## Read-only is a badge, not a caption: `UITheme.LockBadge`

`RefuseIfLocked` drew `StatusBadge("Read-only", false)`: 11 px, hint grey, a hollow dot: the visual
grammar of "server not connected", sitting where the controls should have been, and read as a
malfunction rather than an answer. It is now a full-width amber pill with the ⚠ glyph: the rim says
badge, the amber says "this is consequential", and the band above says `BASE ENVIRONMENT · READ-ONLY`
in the same words. Amber here does not contradict "amber is for editing the base": the badge shows
only while the base is locked, the amber band only while it is open. They never share a screen, and
both mean "attention: the base environment". `SelectTool` draws the same badge over its read-only
inspector, which until now simply lost its controls and said nothing.

## The tool picker is segmented, the "Show" labels are gone, and a list row is a surface

Structure's Walls / Doors & windows / Rooms chips were the same accent pill, in the same column, eight
pixels above the Door / Window / Opening chips: "which tool" and "what am I placing" as one
undifferentiated row. The picker is `UITheme.Segmented` now (the sub-tab look Move / Rotate / Scale
already had) and the opening row is named `Add`, so it reads "Add · Door". The Rooms rail lists the
floor's rooms as `StateRow`s instead of a lone glyph that said "click inside a room" only on hover.
Furnish's and Smart living's `Show` was the last leading chip-row label, the pattern `DrawStageTools`
had already dropped for `Tool`. And the unselected `StateRow`: every resident, room, proposal and
residence in the app. Was clickable borderless text; it carries the white-field + rim surface now, with
the People rail adding a `›` so a row visibly opens something.

## Heights have to include the margin

The command bar, the mode band and the timeline's transport were each sized to their control's
height and not to its style's vertical margin, so the control was laid out 3 px down and `BeginArea`
clipped its bottom edge: the "slightly cut off" tabs. `TopBarHeight` is computed from the theme
now, the band's `VPad` leaves the margin's room, and the timeline's constants are summed rather than
written down. The hour numbers and roster text were a different clip: the 13 px skin label with 3 px
of padding painted into 16 and 18 px rects; the bar has its own zero-padding styles for those.

