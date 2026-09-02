# Design notes. Import: PDF, *Read the plan*, and storeys

> `PdfRaster`, the sketch-to-plan pipeline under `Assets/Scripts/ResidenceViz/Sketch/` (the one thing in
> ResidenceViz that touches the network), where the API key lives, and how the app became multi-storey.
> The rules are summarised in [`.claude/rules/import-and-sketch.md`](../../.claude/rules/import-and-sketch.md); the
> reasoning lives here.

## A floor plan usually arrives as a PDF: `PdfRaster`

`UnderlayTool` is the only route from a paper plan into the model, and it accepted PNG and JPG, because
`Texture2D.LoadImage` decodes those and nothing else. Real plans arrive as PDFs. Architect sets,
estate-agent listings, scanned blueprints, so every one of them had to be converted somewhere else
first, at the one step the whole workflow is gated on.

**PDF is not an image format, so something has to rasterize it.** `Assets/Plugins/x86_64/pdfium.dll`
(~7 MB, BSD-3-Clause, the renderer every browser's Save-as-PDF preview uses) behind a ~150-line
P/Invoke wrapper. The managed alternative was considered and rejected: a parse-only library like PdfPig
can pull an *embedded* image out of a scan but renders nothing, and a plan out of any CAD package is
vector, so roughly half of all real plans would have imported as a blank page, silently.

Note this is the **opposite** conclusion to `HtmlReportWriter.cs`, which declines to add a PDF *writer*
for the before/after report. Writing a PDF means laying out a document and embedding fonts, for
something a browser already does; reading one means rasterizing a page. Different problems, and the
answers do not have to match. The offline promise is untouched: a bundled decoder is not a service.

Four rules in that file, each of which is a real failure mode:

- **BGRA, and top-down.** PDFium hands back B,G,R,A with the top row first; `Texture2D.RGBA32` wants
  R,G,B,A with the bottom row first. Both are corrected in one pass. Skip the flip and every imported
  plan is upside down, which reads as a property of the source file rather than a bug in the reader.
- **Init once, destroy never.** A domain reload throws away the managed half while the native DLL stays
  loaded for the process, so pairing a `FPDF_DestroyLibrary` with it would leave the next init running
  over a torn-down library, which crashes the Editor rather than failing.
- **Every entry point catches `DllNotFoundException`.** `IsAvailable` is false and the rail says so.
  The import happens inside a `FileBrowser` callback, where a thrown exception surfaces nowhere.
- **ONE dpi for the whole document, taken from its largest page.** This is not a performance knob, it
  is the mechanism behind calibrating once for a whole plan set: `metersPerPixel` is meters per
  *rendered* pixel, so it is comparable between two pages only if both were rendered at the same
  resolution, and then it is comparable regardless of their paper sizes. `MaxRasterSide` (4096) caps
  it, because an ARCH D sheet at 150 dpi is 3600×5400 and RGBA at 4096 square is already 67 MB
  resident under the traced quad.

A single-page PDF imports exactly as an image does. More than one raises a **page grid**, in the shape
of the furniture catalog's tile grid, with each page drawn on its own tile. Rendered a couple per
frame, because forty pages in one `OnGUI` hitches. From there: one page onto the floor you are on, or
**all of them as storeys**.

**The PDF itself is not stored**, only the rendered PNG. That keeps `UnderlayDef.imageFileName` meaning
exactly what it always meant, leaves the load path untouched, and keeps the export zip at one file per
sketch: the alternative walks straight into the `underlay/` loop in `ResidenceStore.ImportResidence`.

### …and `.bmp` was in the filter the whole time and never worked

`LoadImage` cannot decode a BMP, so importing one copied the file into storage, wrote a valid
`UnderlayDef`, and then rendered nothing at all: `EnsureQuad` destroyed the quad and returned, with no
message anywhere. It is out of the filter, and a failed decode is now a visible `UITheme.Glyph` in the
rail rather than silence: the same treatment every fit refusal gets, because a warning behind a hover
is a warning nobody reads. The failure is also remembered by key, so a broken file is read once rather
than on every frame the tool is open.

## …and it can be read for you: `Assets/Scripts/ResidenceViz/Sketch/`

Importing a plan got you a picture to trace. Tracing it is the hardest step in the workflow and the
one everything else is gated on. It is why the six sample residences exist at all. **Read the plan** in
the Import rail sends the calibrated sketch to the Anthropic API and turns what comes back into
walls, rooms, doors, windows and catalog furniture on the current storey, in one undoable edit.

This is **the only thing in ResidenceViz that touches the network**, it happens only when a key is present
and the button is pressed, and it sends the sketch image and nothing else. CLAUDE.md's opening claim
is narrowed rather than dropped: no Python, no server, works offline, one opt-in exception.

The legacy Site stack did a version of this through a Flask server (`ModelRequester` →
`/api/layout/generate` → `layout_prompt.py` → `LayoutConverter`). **None of it is reused**. Different
schema, different app, and its whole reason for having a server was an API key that must not ship,
which is solved differently here. What carries over is the lesson in `LayoutConverter`'s header: the
image-to-world transform is where that pipeline went wrong, and it is the thing here most worth a test.

### The model emits rooms and relationships, never ids or coordinates

This is the decision everything else rests on. `PlanBuilder` exists because *"nothing downstream of
the schema complains about bad geometry"*, and an LLM asked for raw `WallDef`/`OpeningDef` produces
exactly those failures: `OpeningDef.offset` is metres along a *specific* wall **after** T-junction
splitting, a number that does not exist until the wall graph has been derived and therefore one
nothing can be asked for up front.

So the model fills in `PlanBuilder`'s authoring surface: `Room`, `DoorBetween`, `ExteriorDoor`,
`Window`, `Against`, `Free`, `Mount`, and the tested derivation does the error-prone half. Failures
land in `PlanBuilder.Warnings` instead of producing silently wrong geometry, exactly as for the samples.

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

**An L-shaped room is one room, and the schema has to say so.** `partOf` names the rectangle a piece
belongs to; pieces are addressed individually (a sofa goes against the alcove's own wall) and emit
**one** `RoomDef` with no wall between them. Getting this wrong was the pipeline's largest silent
failure. See *An L-shaped room used to be a room with a wall through it* in
[samples-and-planbuilder.md](samples-and-planbuilder.md).

**Every room states its size twice.** `widthMeters`/`depthMeters` are read off the drawing and
explicitly *not* converted from the 0-1000 numbers. That redundancy is the point: normalised
coordinates are internally consistent even when they are wrong at a *scale*: a plan traced into half
the range, a room read off the wrong dimension line, and nothing else downstream can see it. The two
channels disagreeing is the only available signal, and `SketchPlanValidator` compares them with
deliberately loose slack (0.5 m or 25%), because the failure worth catching is wrong by a factor and a
tight bound would spend the repair turn on honest estimates.

**Structured output carries the enums.** `SketchPlanSpec.JsonSchema()` builds `roomType` from
`RoomFinish.All` and `catalogId` from `SampleFurniture.All`, so the model *cannot* name a room type
or a catalog item that does not exist. That deletes a class of validation rather than implementing
it. Two constraints of that mode shape the rest: every object needs `additionalProperties: false`
with every property `required` (so unused fields carry `""`/`0` sentinels), and numeric ranges are
**not** expressible. Hence `0..1000` and `0..1` live in the prompt and in `SketchPlanValidator`.
The rooms-only, detail-only and whole schemas are built from **one set of fragments**, so the
pass that reads rooms and the pass that places furniture cannot drift on what a room is.

### Coordinates are normalised to the image, and the vertical flip happens once

0-1000, origin **top-left**, x right and y **down**: the frame the model actually sees. A model
estimates position relative to what is in front of it far better than in absolute metres, and
normalised coordinates are independent of calibration. Lengths that are *not* positions: an
opening's width, its sill. Are plain metres, because a door is 0.813 m wide whatever the scan
resolution was.

`SketchFrame.ToWorld` is the whole conversion, and `UnderlayTool.ApplyTransform` is the authority it
has to agree with: the quad sits with its **bottom-left** at `originMeters`, sized `texW·mpp` by
`texH·mpp`, rotated `Euler(90, rotationDeg, 0)` **about its own centre**. A Unity Quad's uv (0,0) is
its (−0.5, −0.5) corner and a `Texture2D`'s v = 0 is its *bottom* row, so the image's top edge is
local +0.5: one flip, in one place. It is checked against a matrix built from `Quaternion.Euler` directly rather than against a second copy
of the same algebra, so a check cannot pass by sharing a mistake with the code it verifies. Verified at 0°, ±90°, 180° and 270° to under a
micrometre.

**A rotation that is not a quarter turn is refused, with a sentence.** `PlanBuilder` takes
axis-aligned rectangles; a sketch pinned at 7° cannot produce axis-aligned world rooms, and a quietly
skewed plan would be worse than none.

**`alongFraction` is the trap.** `PlanBuilder.EdgeLine` lerps from the *minimum* coordinate to the
maximum. West→east on a north or south wall, and **south→north** on an east or west one. Nobody
reading a picture measures a vertical wall from the bottom up, so a model left to guess puts every
grab bar and every wardrobe at the wrong end of its wall: visible, but only if you look. It is stated
in the schema description in the same compass words the edge itself uses, so there is nothing to infer.

### Regularization is the part that decides whether this works

`PlanBuilder.BuildWalls` buckets wall lines on a 1 mm key and never compares buckets. Two rooms whose
shared edge disagrees by 3 cm therefore derive **two parallel walls with a sliver between them**,
and nothing says so. `Spans.TOL` (2 mm) cannot help; it is a *within-line* tolerance, and by then the
two lines are already separate objects. Neither existing epsilon can be widened to cover this: both
are claims about rendering precision, and what is needed is a claim about **buildings**.

`SketchRegularizer.Snap` clusters all the x coordinates, then all the z, in **metres after the
transform**, and rewrites each room's edges to its cluster's **mean**. The mean is what makes the
pass idempotent and order-independent, because adjacent clusters formed by gap alone are separated by
more than the tolerance, so their representatives are too. That proof has one hole, found when the
tests below were written: a cluster split by the **width cap** rather than by a gap can leave two
bands closer than the tolerance, and a second run merges them. Idempotence therefore holds for the
no-op case and for jitter well inside the tolerance, and `SketchRegularizerTests` pins it there.

Two numbers, and both are **measured**:

- **`DefaultTolerance` = 0.25 m.** The tightest genuine separation between two distinct wall lines
  anywhere in the six shipped plans is **0.400 m** (`house_5b4b`, x axis). Rooms are addressed on
  centerlines, so two boundaries closer than that would mean a void between rooms rather than a wall.
- **`MAX_SPREAD_FACTOR` = 1.4**, capping a cluster at 0.35 m. Under 0.40, and the guard against
  **chaining**: pure single linkage would merge a run of coordinates each 0.24 m from the last into
  one cluster spanning several metres, which is how a row of narrow closets becomes a single wall.

The envelope was measured rather than argued about, by knocking every coordinate of every sample plan
out of place independently (a per-*room* jitter would move both sides of a shared edge together and
prove nothing):

| jitter | result | | jitter | result |
|---|---|---|---|---|
| ±0.03 m | rebuilds identically | | ±0.15 m | rebuilt identically in the sampled runs |
| ±0.06 m | rebuilds identically | | ±0.20 m | walls start to double |

*Identically* is the strong form: the same wall **count** as the authored plan (13, 22, 39, 25, 34,
46), no unwelded T-junction, no overlapping pair, empty `PlanBuilder.Warnings`.

The ±0.15 row is the sampled figure, and writing `SketchRegularizerTests` showed it is not the
guaranteed one: uniform ±0.15 jitter lets two authored lines 0.40 m apart approach to 0.16 m, inside
the tolerance, where they can merge or land overlapped when the width cap splits the cluster, and
fixed seeds found exactly that in two samples. **±0.10 m is the envelope a reader may rely on**:
every adjacent-edge pair stays within one tolerance of itself and clear of every genuine separation.
That is still about 8 units of the 0-1000 range on a 12 m plan, and it is what the tests pin.

`SketchRegularizerTests.Snap_MovesNothingOnAnySamplePlan` is the analogue of *"`Relink` is a no-op on
all six samples"*: raise the tolerance past 0.40 and it fails immediately, naming the plan and the
room.

That 0.400 m is now published as **`SketchRegularizer.MinGenuineSeparation`**, because two files need
it for opposite reasons and stating it twice is how they drift. Here it is a **ceiling**: the cluster
width cap has to stay under it or a genuine narrow return is merged out of existence. In
`SketchPlanValidator` it is a **floor**: a gap narrower than that between two rooms that do not touch
is not a chase, it is two walls with a void between them, which snapping correctly declined to close,
and which nothing downstream reports. Above it, the gap might be something the drawing shows and this
code cannot see, so the check says nothing.

### It reads the plan twice: `SketchPlanGenerator`

Reading a drawing is two jobs, *where the rooms are* and *what is in them*, and asking for both in one
reply makes the model trade them off inside a single sampling pass. **The two are not equally
recoverable, which is the whole argument for splitting them:** every opening and every item is
addressed BY ROOM KEY, so a room read wrongly takes the rest of the plan down with it, while a missed
wardrobe costs a wardrobe. So the rooms are settled first, checked on their own, and quoted back as
fact for the second pass, which therefore cannot name a room key that does not exist.

The rooms are quoted back **in metres, as built**, not in the 0-1000 units they arrived in. They have
been through `SketchRegularizer` by then and are no longer quite what was sent; quoting the originals
would invite a door placed against an edge that has since moved.

**Both passes share one system prompt and one image**, byte for byte, and that is not incidental. It
is the stable prefix `ClaudeClient`'s cache breakpoint sits on. The system prompt therefore carries the
whole 35-item catalog even on the pass that cannot place furniture: after the first turn it is read
back at a tenth of the price, whereas two system prompts would be two cache entries, each written at a
premium and each read half as often. **The breakpoint is on the image block, not on the text after
it**: one block later and every turn would write a fresh entry instead of reading this one. That
placement is what pays for reading the plan twice.

`ClaudeClient.Call` reports `CacheReadTokens`/`CacheWriteTokens` for the same reason `PlanBuilder`
reports warnings: a breakpoint that has stopped working fails **silently**, the answers stay correct
and only the bill moves. A run whose later turns read zero has an invalidator in it.

### Two turns a pass, and the better one wins

`SketchPlanValidator` says what is wrong with the *request* before any geometry exists. Dangling room
keys, a door between rooms that do not touch, overlapping rooms, a room nothing opens into, an opening
through the ceiling. Those strings have two audiences and both need prose: they go back to the model
as the repair turn, and to the user in the rail when it does not help.

**One repair round per pass, deliberately.** What a second turn fixes is referential slips: a mistyped
key, a catalog id recalled rather than read off the list, and it fixes most of them. What it does not
fix is a sketch the model cannot read, and each turn is another wait and another charge against the
user's key.

**The repair does not automatically win.** A repair turn is another sample, and a model correcting
three named problems can drop something that was right, so both replies are scored the same way and
the better one is kept, ties going to the first. That makes the extra turn a one-way bet: it can
improve a plan, never spoil one that was already good. It is also best-of-2 and a repair turn for the
price of one, which is why there is no separate resample.

**The two passes are scored differently, because they are wrong differently.** Rooms go through
`SketchPlanValidator.CheckRooms`: the half of that file that needs no openings, split out for exactly
this. Running the *full* validator on a rooms-only reply would report that every room in the plan has
no way into it, which is pure noise for the repair turn to chase. The fittings are scored on the whole
compile, `PlanBuilder.Warnings` included, which is the closest thing available to *"what will this
actually build as"*.

Whatever survives is **shown, and the plan is installed anyway**: the `OpeningFit` convention applied
to a whole storey. The geometry `PlanBuilder` derived is valid by construction even where one
relationship could not be resolved, and eleven of twelve doors beats nothing.

**The same holds a whole pass at a time.** If pass two fails outright: the network drops, the reply is
not a plan: the rooms are still read and checked and worth having, so they are installed on their own
and the reason the rest is missing is shown beside them.

**A window is not a way into a room, and the reachability check used to think it was.** It counted
every opening regardless of kind, so a bedroom with a window and no door passed, while the sentence
it would have printed said *"no door, pass-through or cased opening"*. The code and its own message
disagreed. It now counts only what you can walk through, and it asks the question twice: a room with no
passable opening at all is an isolated box, and a room whose doors lead only to other rooms in the same
predicament is a wing nobody can reach from the front door. The second is invisible opening by opening.

When **nothing** opens to the outside the plan is not condemned: an upper storey has no exterior door
and stairs are not modelled, but the rooms must still form one connected group rather than two
islands. That distinction is what keeps the check from firing on every upstairs plan it is handed.

**The validator does not re-check what `PlanBuilder` already checks.** Whether an item hangs on a wall
or stands on the floor is not a judgement call: the catalog knows, and the compiler corrects a
mismatch on the way through. Reporting it as well would spend a repair turn asking the model to fix
something already fixed, and two computations of one truth is how this codebase gets its notches.

### Replace, never merge, and neither `Relink` nor `Sync` runs afterwards

`SketchInstall.Adopt` keeps the destination's `id`, `name` and `elevation`: a storey is a fact about
the *building*, and replaces its contents. Merging is the wrong answer twice over: a second
self-consistent wall graph dropped onto an existing one produces crossings only `WallLinker.Relink`
can resolve (minting new wall ids), and rooms that then disagree with the graph, which only
`RoomRegions.Sync` can resolve (minting fresh guids, so the next comparison reports removed + added,
the delete-plus-add failure the revert property exists to catch).

For the same reason **neither runs after a generation**. `PlanBuilder` already derives a welded, split
graph; `Relink` is a verified no-op on all six sample plans (see [walls-and-rooms.md](walls-and-rooms.md)), and the same holds for a
generated one *provided the regularizer did its job*, which is exactly what the noisy-recovery test
asserts. `Sync` would additionally rewrite `polygon` on rooms that already have stable ids.

**Every id is re-stemmed** (`SketchPlanCompiler.Reid`, `g<4 hex>_`). `PlanBuilder` authors `w_0`,
`r_bath`, `o_3` identically every time, which is right for the samples and wrong the moment two
storeys of one residence are both generated: `ResidenceRenderer.Mark` keeps a **single flat dictionary** across
every element type, so a second `w_0` would take the first one's place and selection would start
picking the wrong wall.

When the storey is not empty the button says so in its own label (`Read this plan) replaces 14
walls, 3 rooms`: the reset-to-sample precedent. One undo takes the whole thing back, including what
was discarded, because `RecordEdit` snapshots the document at apply time and not at request time. A
snapshot taken when the request was *sent* would be a minute stale and would take back whatever the
user did while waiting.

### A refusal that draws is a refusal nobody sees

**Pressing *Read this plan* on a locked variant used to do nothing at all.** No badge, no message, no
request: the button was live, the press was accepted, and the run silently never started.

`RefuseIfLocked()` is the shared helper every tool uses, and it *draws*: a `Read-only` badge and a
tooltip. That works wherever it is called from `OnGUI`. `BeginGeneration` and `ApplyGeneration` are
called from **`Tick`**, because everything the rail queues is deferred there, so the helper drew into
nothing, returned `true`, and the method returned. The one signal a user got was the absence of one.

It bit in the worst possible place. **Locked is the default state of every residence in the library**, all
six samples included, and the rest of the Import tab is deliberately *not* gated: an underlay hangs
off `ResidenceDoc` rather than off the variant, so importing a plan and calibrating it are not variant
edits and never needed an unlock. Only generating writes into the level. So the whole tab worked,
right up to the one button that mattered.

Three fixes, and the first is the shape of the rule:

- **The gate belongs in the drawing pass.** `DrawGenerate` refuses before the button is drawn, so the
  button is never live to be pressed, and the reason sits beside it with the mode band named as the
  place to fix it. The checks left in `BeginGeneration` and `ApplyGeneration` are belt to that pair of
  braces and use `Ctx.IsLocked` **directly**: never the drawing helper.
- **`ApplyGeneration`'s copy was the expensive one.** It ran *after* the call had been made and paid
  for, so re-locking the variant while a read was in flight threw the finished plan away in silence.
  It now says so, and says the plan can simply be read again.
- **A dead coroutine can no longer look like a working one.** An unhandled exception stops a coroutine
  where it stands. Unity logs it, the callback never fires, and the rail spins for ever behind a
  Cancel button with nothing to cancel. `RunGeneration` starts the reader as its own coroutine and
  yields on *that* rather than yielding its enumerator inline, which would put it on the same call
  stack and take the wrapper down with it. A run that ends without answering is reported as one.

And **a run that succeeds now says so in the rail, not only in a toast.** `_genOutcome` keeps the
counts (rooms, walls, openings, items, turns, seconds) until the next run starts. It is counted off
the **level** rather than off the model's reply, so it reports what was actually installed rather than
what was asked for; the two differ exactly when something failed to resolve, which is the case the
line exists for. Without it, a plan read onto a storey you are not looking at and a button that does
nothing are the same experience.

### Two hazards that only exist because it is asynchronous

- **The rail's control count changes when a call starts or finishes**, and a coroutine can flip that
  at any point in a frame. Including between IMGUI's layout pass and its repaint pass over the same
  `OnGUI`. That is the `Mismatched LayoutGroup` this codebase's whole deferral discipline exists to
  prevent. So the coroutine writes `_genPhase`/`_genRunning`, `Tick` latches them into
  `_railPhase`/`_railRunning` **once per frame**, and `DrawRail` reads only the latched pair.
- **A call outlives the storey it was for.** A minute is long enough to switch residence, switch floor or
  close the document, so the result carries the `residenceId` and `levelId` it was asked *for* and apply
  refuses anywhere else: the discipline `ModeBand` follows for a held `VariantDef`, at a slower
  timescale. `Exit()` aborts an in-flight call beside `ClosePdf()`, for the reason that line already
  gives: leaving the Import tab is the ordinary way somebody abandons a request.

The image is resampled on the **CPU** (`SketchImageResample`) rather than through `Graphics.Blit` +
`ReadPixels`, which would swap the active render target: `ReportCapture`'s first rule. A box filter,
not point sampling: a wall is one or two dark pixels wide on a scan, and point-sampling 4096 px down
to 2576 *drops* walls rather than blurring them. **PNG, falling back to JPEG only above 3.5 MB**,
deliberately the opposite conclusion to `HtmlReportWriter`'s, and for a reason about the content
rather than the format: that file is choosing between sixteen photographs of shaded geometry, where
PNG makes a report nobody can email, while this is line art, where JPEG ringing around a 1 px line is
precisely the detail the model has to read.

## …and it can be read on this device: `SketchPlanDetector`

*Read on device* is the offline sibling of *Read the plan*: a deterministic computer-vision pass in
`Assets/Scripts/Authoring/Interior/Sketch/SketchPlanDetector.cs` (its stages beside it in
`SketchMaskCleanup`, `SketchWallSegments`, `SketchWallGraph`, `SketchCellMap` and
`SketchOpeningReader`) that turns the underlay's pixels
into a `SketchPlanSpec` with no key and no network. Everything downstream is the API path, reused
byte for byte: `SketchFrame` → `SketchPlanCompiler.Compile` (regularizer, validator, `PlanBuilder`)
→ `SketchInstall.Adopt`. The detector lives in `CXRAuthoring` for the same reason the compiler does:
`SketchPlanDetectorTests` drives it with plans drawn in code (`SketchTestImages`), so every stage has
a fixture nothing has to scan in.

**Graph first, cells second.** The first build of this detector worked at pixel granularity the
whole way down: flood-fill the binarised mask into regions, carve each region into rectangles
greedily, push the faces out to measured centerlines, then rescan the pixels between rectangle
pairs for openings. Its two worst failure modes on photographed hand sketches both came from
staying at pixel granularity too long. Its bridge stage demanded that a doorway gap and both flanks
share one exact pixel row or column, which photographed jambs never do, so offset jambs and corner
pen lifts leaked rooms into each other or into the outside, silently. And the greedy carve plus
per-face push manufactured overlapping boxes and slivers exactly where an L-shape's seam met a
ragged mask. The rework lifts to snapped wall centerlines *before* rooms exist. The pixels are read
once, into measured segments: each scanline perpendicular to a wall crosses it as one short dark
group (a solid stroke, or two thin lines around a light channel, which is how the double-line
window convention becomes a by-product of extraction rather than a second reader), and chaining
crossings across scanlines follows a wobbly stroke wherever it wanders, where any per-pixel run
threshold fails at the crest of the wobble. The lower-median centerline of a chain is immune to
local damage. Long segments establish wall lines by single-linkage clustering with a width cap
(`SketchRegularizer.Cluster`'s shape, for its reason: pure single linkage chains); short ones may
only join a line the long ones established, which keeps the short jamb beside a corner door without
letting a text dash mint a wall. Hough stays refused: its accumulator binning is a determinism
hazard, and nothing here needs it.

The chain drift cap used to reject a too-drifty chain whole, on the assumption that a chain either
wanders a little (wall) or drifts by its whole length (diagonal). A bifold zigzag drawn touching
its jamb broke that dichotomy: the crossings chain follows the connected ink from the wall into the
panel legs and out the other side, the combined drift blows the cap, and the rejection took both
real jamb pieces with it, erasing the closet's mouth wall entirely. So a chain past the cap is now
split at its largest single center step (first occurrence on a tie, so the split is deterministic)
and each side judged again, recursively. The straight wall pieces come out whole; the panel legs
keep failing the cap or fall under the minor minimum and vanish, and a uniform diagonal still emits
nothing because every fragment it peels stays diagonal.

**Repairing the graph IS the doorway detector.** A doorway is a gap between two collinear wall
segments; a corner pen lift is an endpoint that missed a perpendicular line; a door drawn hard
against a corner is an endpoint a door's width short of one. All three are repairs of the same
graph, so one pass owns them, and each records what it repaired: the doorway candidates come out of
this stage first-class, with widths and centers taken from the raw jamb endpoints rather than the
snapped grid, which is what keeps the scale anchor honest. The repair runs a tolerance ladder (one,
two, three strokes, each attempt from the pristine snapped input) and after each attempt the cells
are built and checked for leak signals: no bounded cell at all, a wall whose covered edges all
separate a room from itself (a partition that divided nothing means an unclosed corner elsewhere),
or a long real wall covering no cell edge it nearly reaches (the nearly matters: a dimension line
below the plan covers nothing and must not escalate the ladder). A clean rung is accepted; if none
is, the last one stands with a warning, so a bad corner degrades to a sentence instead of a merged
room. A candidate is still not an opening until the mask agrees: the slab across the gap must read
open and each jamb must carry real ink, because a phantom door costs a wall AND corrupts the scale
estimate. The cell map then names the two sides of every surviving gap, which retired the old
seven-probe majority test for "is this edge exterior".

**Door symbols are tolerated, not read** (`GapReadsOpen`, which replaced the flat one-tenth slab
test). Real plans draw swing arcs, bifold zigzags, and sometimes a route line straight through a
doorway, and all of that ink used to veto the gap while the graph's merged cover run stood, so a
closet came out as solid wall. The structural argument that makes tolerance safe: by the time a
candidate reaches the reader, any axis-aligned wall-like run of two strokes or more on the line
would already have become a segment, joined the line, and closed the gap in the collinear pass. So
the ink left in the slab is either a symbol crossing the band, which blocks only short bursts of
positions along the wall (an arc meets the wall perpendicularly; a zigzag leg is diagonal), or a
shattered wall or a label lying along the line, which blocks long stretches. The verifier accepts a
slab whose longest blocked stretch stays within three strokes and whose ink stays under 30 percent;
the clean one-tenth path is unchanged, byte for byte. The considered and refused alternative was
reading interior double-line (`dbl`) runs of doorway width as sliding or bifold doors drawn in the
wall plane: printed plans draw every wall double-line, so exactly the scanned inputs this change
targets would mint phantom doors along every interior wall. Interior double-lines stay a not-yet.

**A closet survives by its door.** The old rule dropped every room under 1.0 m2 as "a closet symbol
or noise", and with the room gone its verified door died too (`RoomKey` returns null), while the
walls around it stood: the worst of both. The rescue is gated on the one honest signal, a verified
opening: a room whose total area stays under `closetMaxAreaMeters` (1.5 m2, a reach-in closet up to
about 0.75 x 2.0 m) and whose door the mask verified is kept, typed `storage` (a closet is storage;
`floor_storage` already exists, so the floor palette's pair table is untouched) and named Closet by
its own counter in reading order. Doorless small cells stay dropped, so stair treads and furniture
outlines mint nothing. The per-side floor (0.60 m) is not relaxed because `SketchRegularizer.Snap`
drops thinner rooms downstream anyway. Short jambs (two to four strokes) used to be dropped as
annotation, but they are also the wall stub beside a closet door near a corner, so they are now
deferred instead: accepted only when the smaller adjacent rect is at most four door widths squared,
a scale-free test (a closet is at most about twice its door in each direction) that also marks the
gap `closet` for the scale anchor. And where a window run overlaps a verified doorway on one line
(`Dedup`), the doorway is emitted and the window dropped: the doorway passed the mask and named its
sides, the window is a line-pattern reading of the same ink, and emitting both stacks two openings
into one span for `OpeningFit` to collide.

**Cells make the centerline stage free.** Rooms are the bounded faces of the closed line
arrangement: a coarse grid whose cells are the intervals between consecutive wall lines, flooded
from outside, rooms labelled in first-cell scan order (which is what keeps "Room 1" the top-left
room). Cells sit on centerlines by construction, so adjacent rooms share their wall's line exactly
and the old face-pushing stage, whose majority-rule samples were the source of the sliver and
overlap failures, does not exist. A room's cells are cut into rectangles by a row-major sweep:
exact on any rectilinear shape, two rectangles for an L, three for a U, deterministic because the
sweep order is the scan order. The minimal-partition matching algorithm was considered and skipped:
`partOf` tolerates a non-minimal partition, and the matching's tie-breaking would be its own
determinism project. Thin bounded strips (a double-line wall's channel, a hatch band) fold back
into wall before naming, replacing the old thin-rectangle filter.

**Photographs are handled at the front.** The skew search runs coarse-to-fine (one-degree steps
across four degrees, then quarter-degree steps around the winner) and the correction rotates the
GRAY image once and thresholds again, because rotating the binary mask punches nearest-neighbour
holes into one-pixel strokes that read as pen lifts downstream. After a rotation the border ring is
blanked: the seam between the sheet and the rotation's paper fill binarises into long thin bands
that read as walls. Text, arrows, arcs and small symbols are removed by a component filter (no long
straight run and a small bounding box, which errs toward keeping anything that touches real wall),
and the stroke width is re-measured after that cleanup: grain votes the first estimate down, every
later threshold scales with the stroke, and one wrong number there once turned a six-pixel pen into
a two-pixel one and every door floor and weld cap with it.

**The doorway is the scale anchor.** The Claude button is gated on calibration; this one estimates
instead when the plan has none, because a standard door leaf (0.813 m) is the one dimension nearly
every plan carries. The lower median of the interior door gaps sets `metersPerPixel`, the estimate is
**written back as the calibration** on success (so the quad, tracing and the next read all agree, and
recalibrating remains the ordinary correction), sibling PDF pages inherit it through the existing
never-calibrated-only rule, and the outcome line says how many doorways it stood on. Uncalibrated and
doorless refuses with a sentence pointing at the scale wizard. A wrong assumption shows itself: the
Scale button wears the estimated width the moment the read lands. The anchor floor used to be the
single smallest gap, so one spurious break (an arrowhead, a cleanup hole) shrank the whole scale;
the floor is now the smallest gap another gap supports within 1.5x, with a lone gap still anchoring
alone (a one-door plan must keep working). Closet gaps are excluded while any other interior gap
stands, because a closet door (0.61 m) sits inside 1.5x of the assumed leaf (0.813 m) and would
drag the median.

**Synchronous, on purpose.** The detector is bounded by its 1200 px working resolution and finishes
well under a second, so `RunLocalGeneration` runs whole inside one `Tick` behind a single
`_pendingLocalGenerate` flag: no phase latch, no cancel, no staleness check, and nothing is written
until detection, the frame and the compile have all succeeded, so a refusal leaves the floor, the
calibration and the undo stack untouched. One `RecordEdit` covers the scale write-back and the
install because the undo snapshot is the whole document: one undo takes back both, verified live.
The detector's API is pure (`Color32[]` in, spec out), so if a machine ever proves slower the run can
move to a worker thread without touching the detector.

**Stated not-yets:** interior double-line conventions (pass-through counters and in-band sliding
doors; the extraction already marks them, the reader only trusts the exterior ones, and the refusal
to read them as doors is argued above), perspective de-skew (photograph plans face-on; the projection-profile
search straightens up to about four degrees of tilt), white-on-black plans (the binarize guard
refuses them with the line-work sentence), diagonal and curved walls (the chain drift cap rejects
them, deliberately: the spec is rectilinear), a plan drawn to the sheet's very edge (the border
ring is blanked after a skew rotation), and a closed furniture outline long enough to read as wall,
which bites its footprint out of its room rather than corrupting the plan around it.

### The key is not in `settings.json`

`ApiKeyStore` reads `ANTHROPIC_API_KEY` first, then `<RootDir>/anthropic.key`. **Not `ResidenceSettings`**:
that file is serialized wholesale, holds preferences rather than secrets, and is the first thing
anyone pastes into a bug report. Its own file, named for what it is, cannot be swept up by a
serializer that does not know one field is different from the others. It is not in `ResidenceDoc`, so not
in the undo snapshots, and `ExportResidence` zips `residence.json` plus the underlay images only, so it cannot
ride along in a `.riv` archive **by construction**.

When the key comes from the environment the app shows a badge and **no Forget button**: it must not
offer to delete something it did not write.

**The risk, plainly: what it writes is plaintext.** Anyone with the machine, or a backup of it, has
the key. Windows DPAPI would fix that and is perhaps twenty lines, but `ProtectedData` is Windows-only
and not guaranteed present in Unity's .NET profile, so it is a deliberate not-yet, and the rail says
so where the key is entered rather than leaving anyone to assume otherwise.

#### …and it is reachable before you need it

`DrawApiKey` was called from `DrawGenerate`, which sits behind **two** early returns: `DrawRail`
gives up as soon as the storey has no underlay, and `DrawGenerate` gives up while `metersPerPixel` is
unset. So the only route to the field was to import a plan **and** calibrate it first: the one control
somebody needs before their first import was behind the entire workflow it unlocks, and an empty
Import tab offered `Import plan…` and nothing else.

Entering a key is machine setup done once; calibrating is per-plan work. So the section is drawn
**whenever the Import tab is open**. Above the calibration gate in `DrawGenerate`, and again under
`Import plan…` in `DrawRail`'s empty branch, where the glyph explaining that a plan has to be imported
sits *after* the controls rather than in front of them. The `PickingPage` return is untouched: while a
multi-page PDF picker is open, the picker is the rail.

**The field is `UITheme.SecretRow`: it shows what you are entering, and hides on demand.** That is the
opposite of where this started, and a ~100-character key in a 250 px rail is what turned it round.

Masked-by-default was `GUI.PasswordField`, and it broke twice over. The editor computes its scroll
offset from the **real** string on the pass a paste lands, then repaint hands it a run of `•` of a
different glyph width, so the view window falls past the end of the dots and the box renders
**empty**. Dragging recomputes the offset, which is exactly what made it look like paste had failed.
And a field you cannot read is a field you cannot tell you are editing at all.

So the hidden state is a **short fixed placeholder**: a dozen dots at most, never the key's own
length, which cannot scroll, cannot go blank, and publishes no length. It is also **disabled** while
hidden: a box that silently swallows typing is a worse trap than a visible secret, so editing it means
clicking the eye, which is one gesture that says what it does.

What still holds is the part that matters: this field only ever holds a key **mid-entry**. `DrawApiKey`
clears it the moment `Save key` lands, and a stored key is never redisplayed: only summarised through
`ApiKeyStore.Masked`. Mechanically it is `TextRow` with the mask chosen per state: one reserved rect
and **one `GUI.TextField`, always**, so the control count never moves and the control id never changes
hash under a toggle. (Swapping to `GUI.PasswordField` did change it, which dropped keyboard focus every
time the eye was clicked.)

**The toggle's click is claimed BEFORE the field is drawn, and that ordering is the whole of it.** The
gutter sits *inside* the field's rect, and a text field claims the MouseDown for its whole rect the
moment it is drawn, so the first version, a `GUI.Button` drawn afterwards, never saw a press at all.
The click fell through to the field instead, which put the caret at the end of the value and scrolled
it: the toggle did nothing and the dots slid sideways for no reason a user could see. It is now an
explicit `gutter.Contains` + `Event.Use()` ahead of the field, which is the rule `DragNumber` already
states for its keys (*"handled BEFORE the field is drawn, or `GUI.TextField` has already consumed the
event"*) and `StateRow` for its row click. The glyph itself is then **painted** in the Repaint block
rather than being a control: the press is already spoken for, so a second control competing for the
same pixels is exactly the bug. Same gutter, style and tinting `PaintFieldChrome` gives `DragNumber`'s
`↔`, so a secret row and a number row line their affordances up on one edge.

**And the state is latched in `Tick`, like `_railPhase`.** `ApiKeyStore.Source` reads the disk, and it
decides both which branch `DrawApiKey` takes and whether `DrawGenerate` draws its button, so calling
`Set` or `Forget` from `OnGUI` changes the control count between the layout pass and the repaint pass,
which is the `Mismatched LayoutGroup` this file's whole deferral discipline exists to prevent. Hence
`_pendingKeySave` / `_pendingKeyForget`, applied in `Tick` *before* the latch so a save shows in the
frame it was asked for. `Enter` latches too: the default enum value is `None`, which is the *no key*
branch, so an unlatched first frame flashes the entry field on a machine that already has one. This
was survivable while the section was buried behind a calibration; a section drawn on every install is
not the place to leave it.

## Stories: `Stories.cs`

The schema has been multi-level since it was written: `LevelDef.elevation` is real and is threaded
correctly through walls, floors, ceilings, sensors, the furniture gizmo and the ground-plane raycast.
The *application* was single-level at exactly three choke points, and a multi-page plan set is the
thing that needs them opened.

- **`ResidenceEditController.Level` was `levels[0]`**, and every tool sees the level through it
  (`ResidenceToolContext.Level`). It is now indexed by `_levelIndex`. `ResidenceRenderer` has taken a level index
  since it was written, clamps it identically, and reads the *same* index out of the other variant for
  the compare ghost, but nothing ever passed one, so the app had two notions of "the current level"
  that agreed only because one of them was always zero. Changing that one property lights up the tools,
  the click plane, the camera framing and the ghost together.
- **`ResidenceDoc.underlay` was one sketch per RESIDENCE.** It is now `underlays`, one per storey, keyed by
  `UnderlayDef.levelId`. `ResidenceStore.Migrate` folds the old field in and clears it; the field stays
  *declared* because deleting it would make Newtonsoft silently drop the traced sketch of every residence
  already on disk. `Migrate` runs on every load and every import, so the fold must be idempotent: a sketch traced
  before storeys existed is stamped with the ground floor once and never duplicated.
- **A storey is a fact about the BUILDING**, so `Stories.Add` writes an empty level into **every
  variant, sharing one id**: the same split `ResidenceDoc.exteriorEnabled` already makes against
  `VariantDef.exterior`. That buys three things at once: level ids match across variants, which is what
  `VariantDiff.MatchLevel` pairs storeys by and what the underlay key needs; the new level is empty on
  both sides of every diff, so adding one **reports no change**; and because it asserts nothing about
  the residence it does **not** need the baseline unlocked. Drawing on the new floor still does, which is
  where the lock should bite.

It lives in `CXRAuthoring` for the reason `SampleRefresh` does, so it can be tested at all.
`ResidenceStore` is in `Assembly-CSharp`, which asmdefs cannot reference, and its static constructor
reaches for `Application.persistentDataPath`, so anything left there is reachable only by running the
Editor. `ResidenceStore` keeps the parts that genuinely need a disk and delegates the rest.

**The floor chip cycles rather than opening a menu.** A dropdown would need a rect of its own in the
`PointerOverUI` test and a deferred open/close for its control count: the machinery `ModeBand`'s
variant list needs: to choose between two or three items. It is **drawn only when the residence has more
than one storey**, the same conditional-visibility rule the Outdoors stage follows, and its width joins
`DrawTopBar`'s *measured* reserve: a control that is not in the reserve is a control that silently
pushes another one off the end, which is exactly how Undo and Redo disappeared once before. The switch
itself is deferred like every other request drawn in `OnGUI`, because it rebuilds every GameObject in
the residence. Picking, naming and removing a specific floor is in the Import rail's **Floors** section.

### Two bugs a second storey activates

Both were pre-existing, both were invisible while every residence had one floor, and both produce **silently
wrong output** rather than an error. They are the cost of entry, not optional cleanup.

- **`VariantRevert.Revert` reverted the wrong level.** Its comment said levels were matched "by id then
  by position, so a revert addresses the same level the change was reported from"; the code two lines
  down called a `FirstLevel` helper. Reverting an upstairs change found no element with that id on the
  ground floor and silently no-oped. Breaking the one property that file is *specified* by (revert every change in a diff and the diff
  comes back empty).
- **`VariantDiff.Change` carried no level identifier**, while everything that files a change by where
  it happened (`CompareTool`'s room grouping, the report's room sections) groups purely by XZ. Two
  storeys share an XZ by construction, so an upstairs change was filed under whatever ground-floor room
  sat beneath it. `Change` now carries `levelId` and `levelIndex`, stamped in `Compare`'s level loop
  rather than passed down through the eight `CompareX` methods, so a method added later cannot forget
  it. Occupants and the exterior keep `levelIndex = -1`. They belong to the variant, which is what
  puts them under "Elsewhere".

### What else had to stop answering for one floor

Occupants hang off the *variant* while rooms hang off a *level*, so `OccupancyModel` resolved an
upstairs bedroom to no room and reported the resident as **away from residence**, and `Validate` warned
that a correct schedule named "not a room here". `Pose.elsewhereInResidence` is the missing third answer:
`present` stays false (nothing to draw on this floor, and no sensor here can see them) but "not on this
floor" and "not in the building" are different things to tell a caregiver.

The sensing rollups were each named for the building and answered for one floor of it,
`SensorCoverage.WholeResidenceCoverage`, the ways-out count, `SensorCost.Of`. Each gained a `VariantDef`
overload, and the cost one walks every storey's devices in **one pass** rather than summing per-level
estimates: §5.4 prices a bundle as hub + sensors + **one** monthly fee, so two summed estimates would
sell a two-storey residence two subscriptions. Every one of these is a figure that would have flattered the
plan, in the rows a funder reads most closely.

The report photographs per storey: `ReportCapture.Shot` carries a level index and the capture loop
rebuilds once per **(variant, storey)** rather than once per variant. Still not once per shot: every
framing of a given storey comes from one rebuild, which is what guarantees a before/after pair shares a
camera pose exactly. On a single-storey residence that is two rebuilds, exactly as it always was.

**`SensorSim` needed nothing.** Its "nobody residence" logic is all room-scoped ("on, with nobody in the
room"), which is already correct per floor, and it resolves occupancy through `PoseAll`, which now
passes the variant down.


## Verification

**The PDF reader has no EditMode coverage, and cannot have any.** `PdfRaster` is P/Invoke into a native
plugin from `Assembly-CSharp`, so it is in the same category as `ResidenceStore` and the tools: verified by
running it. What it was verified against is a real 40-page PDF, rasterized and inspected. Page count,
per-page size in points, the derived document dpi, and the rendered image checked to be the right way
up and the right colour, which is what proves the BGRA swizzle and the vertical flip together.

## The rail is in the order the work happens

It drew Floors first ("+ Add a floor" above "Import plan…" on every residence) then the plan, its
scale, Read the plan, and only then opacity, angle and lock, with Replace and Remove stacked under
those. Four piles, none of which said which came first. Now it reads Plan → Scale → Read the plan →
Floors: the display tweaks fold under the plan they adjust (set once, rarely touched), Replace and
Remove share a row, and Floors (a fact about the building rather than a step in tracing) closes
the rail under its own header even when it is one button.

