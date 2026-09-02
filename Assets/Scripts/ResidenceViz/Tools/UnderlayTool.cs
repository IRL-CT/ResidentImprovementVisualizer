using System.Collections.Generic;
using System.IO;
using SimpleFileBrowser;
using UnityEngine;
using UnityEngine.InputSystem;

// Imports a floor-plan sketch and puts it on the ground plane at TRUE SCALE, to be traced over.
//
// This is the route from a paper plan into the model, and the calibration step is the single most
// important interaction in the application. Click two points on the image, type the real distance
// between them, and every wall traced afterwards is dimensionally correct.
//
// There are now three ways on from there. Trace it by hand; press Read this plan and have the sketch
// read by Claude (SketchPlanGenerator); or press Read on device and have it read locally
// (SketchPlanDetector), with no key and no network. Calibration gates tracing and the Claude read,
// for the reason this file has always given: a photo of a hand-drawn plan, calibrated against one
// known dimension, beats any estimate of the same sketch. The on-device read is the one exception:
// on an uncalibrated plan it estimates the scale from the doorways it finds, writes that back as the
// calibration, and says so, because a standard door is the one dimension nearly every plan carries.
// Tracing remains the accurate route; the readers are the fast ones.
//
// Until it is calibrated, the image has no scale and nothing traced over it means anything, so the
// rail says so plainly rather than letting someone trace a whole plan at the wrong size.
public class UnderlayTool : ResidenceToolBase
{
    public override string Id => "underlay";
    public override string DisplayName => "Import";

    public override string Hint =>
        "Import a photo or scan of the floor plan, then set its scale by clicking the two ends of "
        + "something you know the length of. Nothing traced measures correctly until that is done.";

    private GameObject _quad;
    private Texture2D _texture;
    private string _loadedFor;      // residence id + filename the current texture belongs to
    private string _loadError;      // why the sketch is not on screen; shown in the rail, not hidden

    private int _calibStage;        // 0 idle, 1 awaiting first point, 2 awaiting second

    // Only while the calibration wizard is waiting for a click on the image.
    public override bool ClaimsClicks => _calibStage > 0;
    private Vector2 _calibA, _calibB;
    private string _calibText = "";
    private string _calibError;

    // --- Reading the plan ---------------------------------------------------------------------
    //
    // A generation runs for a minute or more on a coroutine the CONTROLLER hosts, so it can finish
    // at any point in a frame. Including between IMGUI's layout pass and its repaint pass over this
    // same OnGUI. The rail draws a different number of controls while a call is in flight than while
    // it is idle, so a phase that changed between those two passes is the Mismatched LayoutGroup
    // this file's whole deferral discipline exists to prevent. The coroutine therefore writes only
    // the `_gen*` fields; Tick latches them into the `_rail*` fields once per frame; and DrawRail
    // reads only those. Same shape as _pendingStage, applied to a new source of asynchrony.
    private bool _pendingGenerate;
    private bool _pendingApply;

    // The on-device read needs none of the machinery above: it is synchronous and finishes inside
    // the Tick that starts it, so one deferral flag is its whole protocol. It reports through the
    // same _gen* fields, because the rail's report rows are about the last read, whoever read it.
    private bool _pendingLocalGenerate;
    private ClaudeClient.Call _genCall;
    private SketchGenerationResult _genResult;
    private bool _genRunning;
    private string _genPhase;
    private float _genStarted;

    private bool _railRunning;
    private string _railPhase;

    private string _genError;
    private string _genNotes;

    /// <summary>
    /// What the last run actually did, kept until the next one starts.
    ///
    /// The toast that used to be the only report of a SUCCESSFUL read fades, and a plan read onto a
    /// story you are not looking at changes nothing you can see, so a run that worked and a button
    /// that did nothing were the same experience. This is the line that tells them apart.
    /// </summary>
    private string _genOutcome;
    private readonly List<string> _genProblems = new List<string>();

    // The key row runs on the same latch-and-defer discipline as the generation phase above, and for
    // the identical reason. ApiKeyStore.Source reads the DISK, and it decides both which branch
    // DrawApiKey takes and whether DrawGenerate draws its button, so calling Set or Forget inside
    // OnGUI changes the control count between the layout pass and the repaint pass. That was only
    // ever reachable after somebody had imported AND calibrated a plan; now that the section is drawn
    // whenever the Import tab is open, it is reachable on every install.
    private string _keyTyped = "";
    private string _keyError;
    private bool _keyOpen;
    // Shown while it is being entered, hidden on demand: a key you cannot read is one you cannot
    // check a paste against, and this field only ever holds a key mid-entry: it is cleared the moment
    // Save key lands, and what is on disk is never redisplayed, only summarized.
    private bool _keyReveal = true;
    private ApiKeyStore.Origin _keySource;
    private bool _pendingKeySave;
    private bool _pendingKeyForget;

    public override void Enter(ResidenceToolContext ctx)
    {
        base.Enter(ctx);

        // Before the first OnGUI, not just before the first Tick: the default enum value is None,
        // which is the "no key yet" branch, so an unlatched first frame flashes the entry field on a
        // machine that already has a key.
        _keySource = ApiKeyStore.Source;
        EnsureQuad();
    }

    // Closing the PDF matters as much as resetting the wizard: an open PdfDocument holds the whole
    // file in unmanaged memory and its thumbnails are page-sized textures, and leaving the Import tab
    // is the ordinary way someone abandons a picker.
    public override void Exit()
    {
        _calibStage = 0;
        _confirmRemoveLevel = -1;
        _addName = null;
        ClosePdf();

        // Same argument as closing the PDF: leaving the Import tab is the ordinary way somebody
        // abandons a request, and an orphaned call holds a five-minute timeout with a callback on
        // the far end of it.
        _genCall?.Abort();
    }

    // Everything the rail queues is applied HERE, not in HandleInput: the controller skips HandleInput
    // while the pointer is over a rail, which is exactly where it is when a rail button is clicked, so
    // these would have fired at some arbitrary later moment when the cursor wandered over the scene.
    public override void Tick()
    {
        if (Ctx?.Doc == null) return;

        // Latched once per frame, before anything draws. See the field declarations.
        _railRunning = _genRunning;
        _railPhase = _genPhase;

        // Applied BEFORE the latch below, so a save or a forget is reflected in the same frame it was
        // asked for rather than leaving the rail a frame behind its own file.
        if (_pendingKeySave)
        {
            _pendingKeySave = false;
            if (ApiKeyStore.Set(_keyTyped, out string keyErr)) { _keyTyped = ""; _keyError = null; }
            else _keyError = keyErr;
        }
        if (_pendingKeyForget) { _pendingKeyForget = false; ApiKeyStore.Forget(); }

        _keySource = ApiKeyStore.Source;

        if (_pendingGenerate) { _pendingGenerate = false; BeginGeneration(); }
        if (_pendingApply) { _pendingApply = false; ApplyGeneration(); }
        if (_pendingLocalGenerate) { _pendingLocalGenerate = false; RunLocalGeneration(); }

        if (_pendingImportAll) { _pendingImportAll = false; ImportAllPages(); }
        if (_pendingAddLevel)
        {
            _pendingAddLevel = false;
            Ctx.Controller.AddLevel(_pendingAddLevelName);
            _pendingAddLevelName = null;
            _addName = null;
        }
        if (_pendingRemoveLevel >= 0)
        {
            int doomed = _pendingRemoveLevel;
            _pendingRemoveLevel = -1;
            Ctx.Controller.RemoveLevel(doomed);
        }
        if (_confirmRemoveLevel >= Ctx.Controller.LevelCount) _confirmRemoveLevel = -1;
        if (_pendingRemovePlan != null)
        {
            string cleared = _pendingRemovePlan;
            _pendingRemovePlan = null;
            Ctx.Controller.RecordDocEdit("Remove plan");
            ResidenceStore.SetUnderlay(Ctx.Doc, cleared, null);
            if (Ctx.Level?.id == cleared) DestroyQuad();
            Ctx.Controller.MarkDirty();
        }

        // Ungated too, and this is why it moved out of HandleInput: switching story from the floor
        // chip leaves the cursor on the top bar, so a gated refresh would keep the PREVIOUS floor's
        // sketch on screen until the pointer happened to cross into the 3D view. It is cheap: a hit
        // on the cache key returns after re-applying the transform.
        EnsureQuad();
    }

    public override void HandleInput()
    {
        if (Ctx?.Doc == null) return;

        if (_calibStage == 0 || !Ctx.GroundPoint(out Vector2 p)) return;

        if (LeftClicked())
        {
            if (_calibStage == 1) { _calibA = p; _calibStage = 2; }
            else if (_calibStage == 2) { _calibB = p; _calibStage = 3; }
        }

        if (KeyDown(Key.Escape)) _calibStage = 0;
    }

    // ---------------------------------------------------------------------------------------

    public override void DrawRail()
    {
        if (Ctx?.Doc == null) return;

        _thumbBudget = THUMBS_PER_FRAME;

        // While a multi-page PDF is open the picker IS the rail. See DrawPagePicker.
        if (PickingPage) { DrawPagePicker(); return; }

        // TOP TO BOTTOM: the floors and their plans, then everything about the ACTIVE floor's
        // plan: its scale, how it is shown, and reading it. Plan and Floors used to be separate
        // sections at opposite ends of the rail, but a story and its sketch are the same act, so
        // each floor row now carries its plan: the filename, Replace / Remove, or Import when it
        // has none.
        DrawFloors();

        var underlay = ResidenceStore.UnderlayFor(Ctx.Doc, Ctx.Level?.id);

        if (underlay == null || string.IsNullOrEmpty(underlay.imageFileName))
        {
            // The key belongs here even with nothing imported, and this is the case that matters
            // most: entering it is done once per machine, and an empty Import tab is where somebody
            // setting the app up actually is. The controls come before the glyph because the key is
            // the thing that can be acted on from here; the glyph is the standing explanation.
            UITheme.Section();
            DrawApiKey();
            UITheme.Glyph("⚠", "Import a plan first. This reads the sketch you import, and it is "
                             + "measured against the scale you calibrate it to.", UITheme.Warn);
            return;
        }

        // ---- Scale: one button that names its own state. See DrawCalibration's stage 0 ----
        UITheme.Gap();
        DrawCalibration(underlay);

        // How the sketch is shown. Folded, because once set they are rarely touched again.
        _displayOpen = UITheme.Foldout(_displayOpen, "Display");
        if (_displayOpen) DrawDisplay(underlay);

        // ---- Read the plan (headerless: the button and its gates say it all) ----
        UITheme.Section();
        DrawGenerate(underlay);
    }

    private bool _displayOpen;

    // Opacity, angle and lock: the three ways the sketch is shown rather than what it is.
    private void DrawDisplay(UnderlayDef underlay)
    {
        float opacity = MeasureUI.Number("Opacity", "How strongly the sketch shows through",
                                         underlay.opacity, 0.01f, 0.05f, 1f, "0.00");
        if (!Mathf.Approximately(opacity, underlay.opacity))
        {
            underlay.opacity = opacity;
            ApplyTransform(underlay);
            Ctx.Controller.MarkDirty();
        }

        // Not wrapped: -180 and 180 are the same picture, but a sketch nudged one degree past
        // square should stop there rather than flipping to the other end of the readout.
        float rot = MeasureUI.Angle("Angle", "Turn the sketch to square it up",
                                    underlay.rotationDeg, -180f, 180f, wrap: false);
        if (!Mathf.Approximately(rot, underlay.rotationDeg))
        {
            underlay.rotationDeg = rot;
            ApplyTransform(underlay);
            Ctx.Controller.MarkDirty();
        }

        bool locked = UITheme.Toggle("Lock in place", underlay.locked,
                                     "Stop the sketch moving while you trace over it");
        if (locked != underlay.locked) { underlay.locked = locked; Ctx.Controller.MarkDirty(); }
    }

    // ---------------------------------------------------------------------------------------
    // Reading the plan
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Both read buttons. Read this plan is the one control in ResidenceViz that reaches the
    /// network, and the only place that says so; Read on device is its offline sibling.
    ///
    /// The section sits directly under Scale because that is the honest order of operations: a
    /// generated plan is only as accurate as the calibration it is measured against, so the Claude
    /// button is not offered until there is one. The on-device read is the exception and says so in
    /// its tooltip: with no calibration it estimates the scale from the doorways it finds and writes
    /// that back. Tracing by hand is still the accurate route and is still what everything else in
    /// this rail is for: these are the fast ones.
    /// </summary>
    private void DrawGenerate(UnderlayDef underlay)
    {
        // ABOVE the calibration gate, not below it. Entering a key is machine setup done once, while
        // calibrating is per-plan work, so gating the field on the calibration put the one control
        // somebody needs before their first import behind the whole workflow it unlocks.
        DrawApiKey();

        // THE GATE GOES HERE, IN THE DRAWING PASS, and that is the whole point of it.
        //
        // BeginGeneration used to open with RefuseIfLocked(), which DRAWS, and it is called from
        // Tick, where drawing does nothing. So on a locked variant, which is the default state of
        // every residence in the library and of all six samples, pressing the button was silent: no
        // badge, no message, no request, nothing. Refusing where the buttons are drawn means neither
        // is ever live to be pressed, and the reason is on screen beside them.
        if (RefuseIfLocked())
        {
            UITheme.Glyph("⚠", "Reading a plan writes walls, rooms and furniture into this floor, "
                             + "so this design option has to be unlocked first. Use the mode band at "
                             + "the top: Correct the record, or Propose a change.", UITheme.Warn);
            DrawGenerationReport();
            return;
        }

        if (_railRunning)
        {
            float elapsed = Time.realtimeSinceStartup - _genStarted;
            UITheme.Value(_railPhase ?? "Working…", $"{elapsed:0} s",
                          "Claude is reading the sketch. This usually takes under a minute.");
            if (UITheme.GhostButton("Cancel")) _genCall?.Abort();
            return;
        }

        // The Claude read stays gated on calibration and a key; the on-device read needs neither,
        // which is why it draws below regardless. Everything either branch keys on is per-frame
        // stable, so the control count holds between layout and repaint.
        bool calibrated = underlay.metersPerPixel > 0f;
        bool apiReady = calibrated && _keySource != ApiKeyStore.Origin.None;
        bool empty = SketchInstall.IsEmpty(Ctx.Level);

        string apiTip = "The sketch image is sent to Anthropic and read by Claude, which returns the "
                      + "rooms, doors, windows and furniture it can see. Nothing else about this residence "
                      + "leaves your machine, and it only happens when you press this. One undo takes "
                      + "the whole plan back.";

        string localTip = "Reads the sketch on this machine. Nothing leaves it. Rooms, doorways and "
                        + "windows are found; furniture is drawn in afterwards, by you. One undo "
                        + "takes the whole plan back."
                        + (calibrated ? "" : " The scale is estimated from the doorways it finds "
                                           + "and saved as this plan's calibration.");

        if (apiReady)
        {
            if (DangerOrPrimary("Read this plan", apiTip, empty)) _pendingGenerate = true;
            UITheme.Gap();
        }
        else if (!calibrated)
        {
            // A missing key already explains itself in the key row above; a missing scale is
            // explained here, and only for the path it gates.
            UITheme.Glyph("⚠", "Set the scale to read this plan with Claude. A generated plan is "
                             + "measured against the calibration, so it is only ever as accurate as "
                             + "that. Read on device estimates a scale from the doorways it finds.",
                          UITheme.Warn);
        }

        if (DangerOrPrimary("Read on device", localTip, empty)) _pendingLocalGenerate = true;

        DrawGenerationReport();
    }

    /// <summary>
    /// One read button: plain on an empty floor, Danger with the price in the ⚠ beside it once the
    /// floor holds anything. The label stays short; the glyph's hover says what a press replaces.
    /// </summary>
    private bool DangerOrPrimary(string label, string tip, bool empty)
    {
        if (empty)
        {
            bool go = UITheme.PrimaryButton(label);
            UITheme.Tip(tip);
            return go;
        }

        GUILayout.BeginHorizontal();
        bool pressed = UITheme.DangerButton(label,
                                            GUILayout.Width(UITheme.ContentWidth - UITheme.GlyphReserve));
        UITheme.Tip(tip);
        UITheme.Glyph("⚠", "Replaces " + SketchInstall.ContentSummary(Ctx.Level)
                         + " already on this floor. One undo takes the whole plan back.",
                      UITheme.Danger);
        GUILayout.EndHorizontal();
        return pressed;
    }

    /// <summary>
    /// Everything the last run has to say for itself: the failure, the outcome, the problems, the
    /// notes. Drawn on the locked branch too, so a message from a run that already happened does not
    /// vanish the moment the variant is re-locked.
    /// </summary>
    private void DrawGenerationReport()
    {
        if (!string.IsNullOrEmpty(_genError))
            UITheme.Glyph("⚠", _genError, UITheme.Danger);

        if (!string.IsNullOrEmpty(_genOutcome))
            UITheme.Value("Last read", _genOutcome,
                          "What the last run put on this floor, and what it cost.");

        if (_genProblems.Count > 0)
        {
            UITheme.Glyph("⚠", Problems(), UITheme.Warn);
            UITheme.Value("Read with", _genProblems.Count + (_genProblems.Count == 1 ? " problem" : " problems"),
                          "What could not be resolved. The rest of the plan was kept.");
        }

        if (!string.IsNullOrEmpty(_genNotes))
            UITheme.Value("Notes", "…", _genNotes);
    }

    /// <summary>
    /// The key row, drawn whenever the Import tab is open: from both of DrawRail's branches, so it
    /// is reachable before the first plan is imported rather than only after one has been imported
    /// AND calibrated. Entering a key is setup done once per machine; calibrating is per-plan work.
    ///
    /// A badge and nothing else when the environment supplies the key: the app must not offer to
    /// forget something it did not write. Everything here reads the LATCHED source and defers its
    /// writes. See the field declarations.
    /// </summary>
    private void DrawApiKey()
    {
        var source = _keySource;      // latched in Tick: never read the disk from OnGUI

        if (source == ApiKeyStore.Origin.Environment)
        {
            UITheme.StatusBadge("Key from environment", true);
            UITheme.Tip("ANTHROPIC_API_KEY is set on this machine, so nothing is stored by the app.");
            return;
        }

        if (source == ApiKeyStore.Origin.File)
        {
            _keyOpen = UITheme.Foldout(_keyOpen, "API key");
            if (!_keyOpen) return;

            UITheme.StatusBadge("Key set", true);
            UITheme.Tip(ApiKeyStore.Masked + ". Stored on this machine only, in plain text, and "
                      + "never part of an exported residence.");
            if (UITheme.GhostButton("Forget key")) _pendingKeyForget = true;
            return;
        }

        UITheme.Glyph("⚠", "An Anthropic API key is needed to read a plan. It is stored on this "
                         + "machine only, in plain text, and is never part of an exported residence.",
                      UITheme.Warn);

        _keyTyped = UITheme.SecretRow("API key", _keyTyped, ref _keyReveal,
                                      "Paste your Anthropic API key. The eye on the right hides it if "
                                    + "somebody is looking; it is cleared the moment you save, and "
                                    + "what is stored is never shown again. Nothing is sent until you "
                                    + "press Read this plan.");

        if (UITheme.SecondaryButton("Save key")) _pendingKeySave = true;

        if (!string.IsNullOrEmpty(_keyError)) UITheme.Glyph("⚠", _keyError, UITheme.Danger);
    }

    private string Problems()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _genProblems.Count && i < 12; i++) sb.AppendLine("• " + _genProblems[i]);
        if (_genProblems.Count > 12) sb.Append("…and ").Append(_genProblems.Count - 12).Append(" more.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Starts the call. Only ever reached from Tick: never from OnGUI.</summary>
    private void BeginGeneration()
    {
        if (_genRunning) return;

        // NOT RefuseIfLocked(). That helper draws, and this runs from Tick, which is exactly how a
        // press on a locked variant used to disappear without a word. The rail refuses before the
        // button is live; this is the belt to that pair of braces, and it still has to say so.
        if (Ctx == null || Ctx.IsLocked)
        {
            _genError = "This design option is locked, so nothing can be written into it. Unlock it "
                      + "from the mode band at the top and press the button again.";
            _genOutcome = null;
            return;
        }

        var level0 = Ctx.Level;
        if (level0 == null)
        {
            _genError = "There is no floor to read this plan onto.";
            _genOutcome = null;
            return;
        }

        var underlay = ResidenceStore.UnderlayFor(Ctx.Doc, level0.id);
        if (underlay == null || _texture == null)
        {
            _genError = "The sketch image is not loaded.";
            _genOutcome = null;
            return;
        }

        var frame = SketchFrame.Build(underlay.originMeters, _texture.width, _texture.height,
                                      underlay.metersPerPixel, underlay.rotationDeg);
        if (!frame.valid) { _genError = frame.reason; _genOutcome = null; return; }

        _genError = null;
        _genOutcome = null;
        _genNotes = null;
        _genProblems.Clear();
        _genRunning = true;
        _genPhase = "Preparing the sketch…";
        _genStarted = Time.realtimeSinceStartup;
        _genCall = new ClaudeClient.Call();
        _genResult = null;

        Ctx.Controller.StartCoroutine(RunGeneration(frame, level0, Ctx.Doc.id, level0.id));
    }

    /// <summary>
    /// Runs the read, and guarantees the rail hears back one way or the other.
    ///
    /// An unhandled exception stops a coroutine where it stands: Unity logs it and the callback never
    /// fires, so _genRunning stays true and the rail spins for ever behind a Cancel button with
    /// nothing to cancel. That is the worst version of "nothing happened", because it looks like work
    /// in progress. Starting the reader as its own coroutine and yielding on THAT. Rather than
    /// yielding its enumerator inline, which would put it on this call stack and take this method
    /// down with it. Means a death over there still returns control here.
    /// </summary>
    private System.Collections.IEnumerator RunGeneration(SketchFrame frame, LevelDef level, string residenceId, string levelId)
    {
        bool answered = false;

        yield return Ctx.Controller.StartCoroutine(SketchPlanGenerator.Run(
            _texture, frame, level.ceilingHeight, level.wallThickness,
            residenceId, levelId, _genCall,
            phase => _genPhase = phase,
            result =>
            {
                answered = true;
                _genRunning = false;
                _genPhase = null;
                _genResult = result;

                // A null result is the abort path, and cancelling is the one outcome that needs no
                // explanation: only an acknowledgement, so the rail does not look stuck.
                if (result != null) _pendingApply = true;
                else _genOutcome = "Canceled.";
            }));

        if (answered) yield break;

        _genRunning = false;
        _genPhase = null;
        _genOutcome = null;
        _genError = "The plan reader stopped before it finished. The Unity console will say why. "
                  + "Nothing was written to this floor.";
    }

    /// <summary>
    /// Writes the plan into the story, deferred because it rebuilds every GameObject in the residence.
    /// </summary>
    private void ApplyGeneration()
    {
        var result = _genResult;
        _genResult = null;
        if (result == null) return;

        if (!result.Ok)
        {
            _genError = result.error ?? "That plan could not be read, and gave no reason.";
            _genOutcome = null;
            Ctx.Controller.Status("Could not read that plan.");
            return;
        }

        // A minute is long enough for somebody to switch residence or floor while it ran. Applying a plan
        // to the wrong story would be a silent, destructive mistake, so it simply refuses.
        if (Ctx.Doc == null || Ctx.Doc.id != result.forResidenceId || Ctx.Level?.id != result.forLevelId)
        {
            _genError = "That plan was read for a different floor. Go back to the floor you started "
                      + "it on and read it again.";
            _genOutcome = null;
            return;
        }

        // Again NOT RefuseIfLocked(): this also runs from Tick. Worse than the button's version,
        // because by here the call has been made and paid for: locking the variant while it ran used
        // to throw the whole plan away without a word.
        if (Ctx.IsLocked)
        {
            _genError = "The plan was read, but this design option was locked before it could be "
                      + "written. Unlock it and read the plan again.";
            _genOutcome = null;
            return;
        }

        Ctx.RecordEdit("Read plan from sketch");
        SketchInstall.Adopt(Ctx.Level, result.level, SketchPlanCompiler.NewPrefix());
        Ctx.Changed();

        _genProblems.Clear();
        _genProblems.AddRange(result.problems);
        _genNotes = result.notes;
        _genError = null;

        int rooms = Ctx.Level.rooms?.Count ?? 0;
        int walls = Ctx.Level.walls?.Count ?? 0;
        int openings = Ctx.Level.openings?.Count ?? 0;
        int items = (Ctx.Level.furniture?.Count ?? 0) + (Ctx.Level.wallMounted?.Count ?? 0);

        // Counted off the LEVEL rather than off the spec, so it reports what was actually installed
        // rather than what was asked for: the two differ exactly when something failed to resolve,
        // which is the case this line exists for.
        _genOutcome = $"{rooms} rooms · {walls} walls · {openings} doors and windows · {items} items"
                    + $"  ({result.turns} turns, {Time.realtimeSinceStartup - _genStarted:0} s)";

        Ctx.Controller.Status(result.problems.Count == 0
            ? $"Read {rooms} rooms from the sketch."
            : $"Read {rooms} rooms, with {result.problems.Count} left unresolved.");
    }

    /// <summary>
    /// The whole on-device read, start to finish, inside one Tick. Only ever reached from Tick:
    /// never from OnGUI. Synchronous on purpose: the detector is bounded by its working resolution
    /// and finishes well under a second, and a run that cannot outlive the frame needs no phase
    /// latch, no cancel, and no staleness check against a floor switched mid-flight.
    ///
    /// Nothing is written until everything has succeeded. Detection, the frame and the compile are
    /// all pure, so a refusal at any point leaves the floor, the calibration and the undo stack
    /// exactly as they were. On success ONE RecordEdit covers both the estimated-scale write-back
    /// and the plan install, because the undo snapshot is the whole document: one undo takes back
    /// both together.
    /// </summary>
    private void RunLocalGeneration()
    {
        // NOT RefuseIfLocked(): that helper draws, and this runs from Tick. The rail refuses before
        // the button is live; this is the belt to that pair of braces.
        if (Ctx == null || Ctx.IsLocked)
        {
            _genError = "This design option is locked, so nothing can be written into it. Unlock it "
                      + "from the mode band at the top and press the button again.";
            _genOutcome = null;
            return;
        }

        var level = Ctx.Level;
        if (level == null)
        {
            _genError = "There is no floor to read this plan onto.";
            _genOutcome = null;
            return;
        }

        var underlay = ResidenceStore.UnderlayFor(Ctx.Doc, level.id);
        if (underlay == null || _texture == null)
        {
            _genError = "The sketch image is not loaded.";
            _genOutcome = null;
            return;
        }

        _genError = null;
        _genOutcome = null;
        _genNotes = null;
        _genProblems.Clear();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var detected = SketchPlanDetector.Detect(_texture.GetPixels32(), _texture.width,
                                                 _texture.height, underlay.metersPerPixel);
        if (!detected.Ok)
        {
            _genError = detected.refusal;
            Ctx.Controller.Status("Could not read that plan.");
            return;
        }

        var frame = SketchFrame.Build(underlay.originMeters, _texture.width, _texture.height,
                                      detected.metersPerPixel, underlay.rotationDeg);
        if (!frame.valid)
        {
            _genError = frame.reason;
            Ctx.Controller.Status("Could not read that plan.");
            return;
        }

        var compiled = SketchPlanCompiler.Compile(detected.spec, frame,
                                                  level.ceilingHeight, level.wallThickness);
        if (!compiled.Ok)
        {
            _genError = compiled.refusal;
            Ctx.Controller.Status("Could not read that plan.");
            return;
        }

        Ctx.RecordEdit("Read plan on device");

        if (detected.scaleEstimated)
        {
            underlay.metersPerPixel = detected.metersPerPixel;
            ApplyTransform(underlay);
            // previous = 0: only sibling pages that were never calibrated inherit the estimate; a
            // page somebody measured by hand keeps its own number.
            ApplyScaleToSiblings(underlay, 0f);
        }

        SketchInstall.Adopt(level, compiled.level, SketchPlanCompiler.NewPrefix());
        Ctx.Changed();
        watch.Stop();

        _genProblems.AddRange(compiled.issues);
        _genProblems.AddRange(compiled.warnings);
        _genProblems.AddRange(detected.warnings);

        int rooms = level.rooms?.Count ?? 0;
        int walls = level.walls?.Count ?? 0;
        int openings = level.openings?.Count ?? 0;

        // Counted off the LEVEL, like ApplyGeneration and for the same reason: it reports what was
        // actually installed.
        string scaleNote = detected.scaleEstimated
            ? $" · scale from {detected.scaleDoorways} " + (detected.scaleDoorways == 1 ? "doorway" : "doorways")
            : "";
        _genOutcome = $"{rooms} rooms · {walls} walls · {openings} doors and windows"
                    + $"  (on device, {watch.Elapsed.TotalSeconds:0.0} s{scaleNote})";

        Ctx.Controller.Status(detected.scaleEstimated
            ? $"Read {rooms} rooms on this device. The scale was estimated from doorways; calibrate "
              + "the plan for exact measurements."
            : $"Read {rooms} rooms on this device.");
    }

    /// <summary>
    /// The story list AND each story's plan. Merged, because a story and its sketch are the
    /// same act: importing a plan set IS how a residence gets its floors. Each floor is ONE row:
    /// its name (a field, editing renames, clicking switches), its plan's filename, a round ↻
    /// that swaps the plan, and a ✕ that removes floor and plan together behind a two-click
    /// confirm, or Import in the filename's place when it has none. It leads the rail:
    /// everything below it belongs to the active floor's plan.
    /// </summary>
    private void DrawFloors()
    {
        var c = Ctx.Controller;
        int count = c.LevelCount;
        int active = c.LevelIndex;
        float railW = UITheme.ContentWidth;

        UITheme.Header("Floors");
        for (int i = 0; i < count; i++)
        {
            string levelId = LevelIdAt(i);
            var levels = Ctx.Variant?.levels;
            var lvl = levels != null && i < levels.Count ? levels[i] : null;
            var u = ResidenceStore.UnderlayFor(Ctx.Doc, levelId);
            bool hasPlan = u != null && !string.IsNullOrEmpty(u.imageFileName);

            // The last floor can lose its plan but never itself, so with no plan either: no ✕.
            bool showRemove = count > 1 || hasPlan;
            float glyphs = (showRemove ? UITheme.GlyphReserve : 0f)
                         + (hasPlan ? UITheme.GlyphReserve : 0f);
            float nameW = Mathf.Floor((railW - glyphs) / 2f);
            float restW = railW - glyphs - nameW;

            GUILayout.BeginHorizontal();

            // The name IS the row: editing it renames, clicking it switches. The chip and the
            // trailing "Floor name" field this replaces were two controls for one fact.
            string typed = UITheme.TextRow(null, lvl?.name ?? "",
                hasPlan ? "This floor's name. Edit to rename, click to work on this floor. "
                          + "It has a plan imported."
                        : "This floor's name. Edit to rename, click to work on this floor. "
                          + "No plan imported for it yet.",
                active: i == active, GUILayout.Width(nameW - 6f));
            // rawType survives the TextField claiming the MouseDown; the switch itself is deferred.
            var e = Event.current;
            if (e != null && e.rawType == EventType.MouseDown && e.button == 0 && i != active
                && GUILayoutUtility.GetLastRect().Contains(e.mousePosition))
                c.RequestLevel(i);
            if (lvl != null && typed != lvl.name)
            {
                // Renaming is a document edit like any other, but it is per-VARIANT geometry, so
                // every variant's copy of this story is renamed together. They are one floor.
                c.RecordDocEdit("Rename a floor");
                ResidenceStore.RenameLevel(Ctx.Doc, levelId, typed);
                c.MarkDirty();
                if (i != active) c.RequestLevel(i);   // typing into a row is working on it
            }

            if (hasPlan)
            {
                // Whatever the user's file happened to be called, ellipsized to its cell: the box
                // is geometrically fixed. The full name leads the hover when trimmed. No label: the
                // filename is the plan: the value names itself.
                string shown = UITheme.Fit(u.imageFileName, UITheme.ValueStyle, restW - 4f);
                UITheme.Value(shown, shown == u.imageFileName
                                  ? SourceTip(u)
                                  : u.imageFileName + ". " + SourceTip(u),
                              GUILayout.Width(restW - 4f));

                if (UITheme.RoundGlyphButton("↻")) Import(levelId);
                UITheme.Tip("Swap in a different plan for this floor. The scale is set again from scratch.");
            }
            else
            {
                if (UITheme.PrimaryButton("Import plan…", GUILayout.Width(restW - 6f),
                                          GUILayout.Height(UITheme.RowH + 4f)))
                    Import(levelId);
                UITheme.Tip(PdfRaster.IsAvailable
                    ? "Import a PDF, photo or scan of this floor's plan, then set its scale by "
                      + "measuring one known dimension on it. Everything traced afterwards will be "
                      + "at true size. A PDF with more than one page lets you pick which one."
                    : "Import a photo or scan of this floor's plan, then set its scale by measuring "
                      + "one known dimension on it. Everything traced afterwards will be at true size.");
            }

            if (showRemove)
            {
                // Arming is plain OnGUI state, like the reset-to-sample confirm: a click lands
                // between passes, so the next Layout/Repaint pair agree on the new control count.
                // The removal itself stays deferred. It rebuilds every GameObject in the residence.
                if (UITheme.DangerButton("✕", GUILayout.Width(UITheme.GlyphW)))
                    _confirmRemoveLevel = _confirmRemoveLevel == i ? -1 : i;
                UITheme.Tip(count > 1
                    ? "Remove this floor and its plan from every design option."
                    : "A residence has to have at least one floor, so this removes only its plan. "
                      + "Anything traced stays.");
            }

            GUILayout.EndHorizontal();

            // A decode failure used to be completely silent: EnsureQuad destroyed the quad and
            // returned, so the rail described a sketch that was not on screen and said nothing
            // about why. Only the active floor's sketch is ever loaded, so only its row can say.
            if (i == active && hasPlan && !string.IsNullOrEmpty(_loadError))
                UITheme.Glyph("⚠", _loadError, UITheme.Danger);

            if (_confirmRemoveLevel == i && showRemove)
            {
                // The cost goes in the button's own label: the reset-to-sample confirmation's
                // rule. Stacked under the row, not beside it, so the price it exists to state is
                // never the part that gets cut off.
                if (count > 1)
                {
                    string price = "Remove " + (lvl?.name ?? "this floor") + ". Discards "
                                 + (hasPlan ? "its plan and " : "") + SketchInstall.ContentSummary(lvl);
                    // Wrapped, not fitted: the price is the part this button exists to state, and
                    // the block under the row is free to grow taller.
                    if (UITheme.DangerButtonWrapped(price, GUILayout.Width(railW)))
                    {
                        _confirmRemoveLevel = -1;
                        _pendingRemoveLevel = i;
                    }
                    UITheme.Tip(price + ", in every design option. One undo brings it back.");
                }
                else
                {
                    if (UITheme.DangerButton("Remove plan. Anything traced stays"))
                    {
                        _confirmRemoveLevel = -1;
                        _pendingRemovePlan = levelId;
                    }
                    UITheme.Tip("Delete this floor's underlay. Anything already traced stays.");
                }
                UITheme.GapTight();
                if (UITheme.GhostButton("Cancel")) _confirmRemoveLevel = -1;
                UITheme.Tip("Keep this floor as it is.");
            }

            if (i < count - 1) UITheme.Gap();
        }

        // Add floor: one button plus the name the new floor will get, editable before pressing.
        UITheme.Gap();
        string suggested = ResidenceStore.DefaultLevelName(count);
        string shownName = _addName ?? suggested;
        float btnW = UITheme.Measure("+ Add floor", UITheme.ButtonStyle);
        GUILayout.BeginHorizontal();
        if (UITheme.SecondaryButton("+ Add floor", GUILayout.Width(btnW),
                                    GUILayout.Height(UITheme.RowH + 4f)))
        {
            _pendingAddLevelName = shownName;
            _pendingAddLevel = true;
        }
        UITheme.Tip("Add a floor above the top one, to every design option at once. It starts empty; "
                    + "import a plan for it and trace it like any other floor.");
        string typedAdd = UITheme.TextRow(null, shownName, "What the new floor will be called",
            GUILayout.Width(railW - btnW - UITheme.MarginW(UITheme.ButtonStyle) - 6f));
        if (typedAdd != shownName) _addName = typedAdd;
        GUILayout.EndHorizontal();
    }

    private string LevelIdAt(int i)
    {
        var levels = Ctx.Variant?.levels;
        return levels != null && i >= 0 && i < levels.Count ? levels[i]?.id : null;
    }

    // Applied from Tick rather than from OnGUI: all three change the rail's control count, which
    // mid-OnGUI is the Mismatched LayoutGroup this codebase's whole deferral discipline prevents.
    private bool _pendingAddLevel;
    private string _pendingAddLevelName;  // the name the queued add carries, from the inline field
    private int _pendingRemoveLevel = -1;
    private string _pendingRemovePlan;    // levelId whose plan is to be deleted, applied in Tick

    // OnGUI-owned, like ResidenceEditController's _confirmReset: arming between passes redraws
    // consistently on the next Layout/Repaint pair, while the destructive act itself stays behind
    // the Tick latches above.
    private int _confirmRemoveLevel = -1;
    private string _addName;              // null = keep showing the suggested default name

    /// <summary>Where this sketch came from: the hover on the filename line.</summary>
    private static string SourceTip(UnderlayDef u)
        => u != null && !string.IsNullOrEmpty(u.sourceDocument) && u.sourcePage > 0
            ? $"The plan being traced, page {u.sourcePage} of {u.sourceDocument}"
            : "The plan being traced";

    private void DrawCalibration(UnderlayDef underlay)
    {
        switch (_calibStage)
        {
            case 0:
            {
                // One button, naming its own state: the image's real-world width once calibrated,
                // the ask before that. No header, no badge: the sentence lives in the tooltip.
                bool calibrated = underlay.metersPerPixel > 0f;
                string label = !calibrated ? "Set scale…"
                    : _texture != null
                        ? "Scale · " + Units.Format(_texture.width * underlay.metersPerPixel)
                        : "Re-calibrate…";
                bool press = calibrated ? UITheme.SecondaryButton(label) : UITheme.PrimaryButton(label);
                UITheme.Tip(calibrated
                    ? "How wide the whole image is in real life. Click to re-measure it against "
                      + "something you know the length of."
                    : "The sketch has no real-world size yet, so nothing traced over it will measure "
                      + "correctly until it does. Click, then click the two ends of something you "
                      + "know the length of.");
                if (press)
                {
                    _calibStage = 1;
                    _calibError = null;
                    _calibText = "";
                }
                break;
            }

            // Stages 1 and 2 have no control of their own to hover: the UI at that moment is the
            // image, and what the app is waiting for is a click on it. So the rail shows a step
            // indicator carrying the instruction, and DrawOverlay mirrors the same prompt at the
            // cursor, which is where you are actually looking while clicking.
            case 1:
                UITheme.Step(1, 3, CalibPrompt);
                if (UITheme.GhostButton("Cancel")) _calibStage = 0;
                UITheme.Tip("Give up on setting the scale");
                break;

            case 2:
                UITheme.Step(2, 3, CalibPrompt);
                if (UITheme.GhostButton("Cancel")) _calibStage = 0;
                UITheme.Tip("Give up on setting the scale");
                break;

            case 3:
                UITheme.Step(3, 3, CalibPrompt);
                _calibText = UITheme.TextRow("Distance", _calibText,
                    "How long is that in real life?   e.g.  12' 6\"   ·   36\"   ·   3.8m");

                if (!string.IsNullOrEmpty(_calibError)) UITheme.Glyph("⚠", _calibError, UITheme.Danger);

                GUILayout.BeginHorizontal();
                if (UITheme.PrimaryButton("Apply")) ApplyCalibration(underlay);
                UITheme.Tip("Rescale the sketch so those two points are that far apart");
                if (UITheme.GhostButton("Cancel", GUILayout.Height(UITheme.PrimaryH))) _calibStage = 0;
                UITheme.Tip("Give up on setting the scale");
                GUILayout.EndHorizontal();
                break;
        }
    }

    // Rescales the image so the two clicked points end up the stated distance apart.
    private void ApplyCalibration(UnderlayDef underlay)
    {
        if (!Units.TryParse(_calibText, Units.BareUnit.FollowDisplay, out float realMeters) || realMeters <= 0f)
        {
            _calibError = "Could not read that measurement.";
            return;
        }

        float drawnMeters = Vector2.Distance(_calibA, _calibB);
        if (drawnMeters < 1e-4f)
        {
            _calibError = "Those two points are in the same place.";
            return;
        }

        float factor = realMeters / drawnMeters;

        Ctx.Controller.RecordDocEdit("Calibrate sketch");
        // Kept so a RE-calibration can find the pages that are still carrying the number this one is
        // about to replace. See ApplyScaleToSiblings.
        float previous = underlay.metersPerPixel;
        underlay.metersPerPixel = Mathf.Max(1e-6f, underlay.metersPerPixel <= 0f
            ? factor * DefaultMetersPerPixel()
            : underlay.metersPerPixel * factor);

        _calibStage = 0;
        _calibError = null;
        ApplyTransform(underlay);
        Ctx.Controller.MarkDirty();

        int siblings = ApplyScaleToSiblings(underlay, previous);
        Ctx.Controller.Status(siblings > 0
            ? $"Scale set: that line is {Units.Format(realMeters)}. Applied to {siblings} other "
              + (siblings == 1 ? "floor" : "floors") + " from the same PDF."
            : "Scale set: that line is " + Units.Format(realMeters) + ".");
    }

    /// <summary>
    /// Carries a fresh calibration to every OTHER story traced from the same PDF that has not been
    /// calibrated yet. Returns how many were scaled.
    ///
    /// This is exact rather than approximate, and PdfRaster.DocumentDpi is why: every page of a
    /// document is rendered at ONE resolution, so meters-per-rendered-pixel is the same number on all
    /// of them whenever the drawing scale is, which for a set of floor plans of one building it
    /// always is. Different paper sizes do not matter; a page rendered at 150 dpi has 150 pixels to
    /// the inch whether it is A4 or ARCH D.
    ///
    /// Two kinds of page are carried: one that has never been calibrated, and one still holding the
    /// exact number this calibration just replaced, which is a page that INHERITED from this one and
    /// has not been touched since. That second case is what makes "Re-calibrate…" work: without it,
    /// fixing a bad measurement on the ground floor would leave every other floor on the bad one, and
    /// the whole point of sharing a calibration is that you only do it once.
    ///
    /// A page somebody has measured by hand is left alone. It has said something more specific than
    /// this can, and silently overwriting it would be the worse failure of the two.
    /// </summary>
    private int ApplyScaleToSiblings(UnderlayDef calibrated, float previous)
    {
        if (string.IsNullOrEmpty(calibrated?.sourceDocument)) return 0;

        int n = 0;
        foreach (var u in Ctx.Doc.underlays ?? new System.Collections.Generic.List<UnderlayDef>())
        {
            if (u == null || ReferenceEquals(u, calibrated)) continue;
            if (u.sourceDocument != calibrated.sourceDocument) continue;

            bool never = u.metersPerPixel <= 0f;
            bool inherited = previous > 0f && Mathf.Approximately(u.metersPerPixel, previous);
            if (!never && !inherited) continue;

            u.metersPerPixel = calibrated.metersPerPixel;
            n++;
        }
        return n;
    }

    // Before the first calibration the quad is shown at an arbitrary 1 px = 1 cm, purely so there is
    // something visible to click on.
    private float DefaultMetersPerPixel() => 0.01f;

    // Targets the given story, which since the Floors merge is not necessarily the active one,
    // every floor row carries its own Import / Replace button.
    private void Import(string levelId)
    {
        // .bmp was offered here for a long time and never worked: Texture2D.LoadImage decodes PNG and
        // JPG only, so a BMP copied into storage, wrote its UnderlayDef, and then rendered nothing at
        // all: no error anywhere. It is gone, and the decode failure it exposed is now reported (see
        // _loadError). .pdf is offered because that is how a floor plan actually arrives.
        var filter = PdfRaster.IsAvailable
            ? new FileBrowser.Filter("Floor plans", ".pdf", ".png", ".jpg", ".jpeg")
            : new FileBrowser.Filter("Floor plans", ".png", ".jpg", ".jpeg");
        FileBrowser.SetFilters(true, filter);
        FileBrowser.SetDefaultFilter(PdfRaster.IsAvailable ? ".pdf" : ".png");
        FileBrowser.ShowLoadDialog(
            paths =>
            {
                if (paths == null || paths.Length == 0) return;
                if (PdfRaster.LooksLikePdf(paths[0])) BeginPdfImport(paths[0], levelId);
                else ImportImageFile(paths[0], levelId);
            },
            () => { },
            FileBrowser.PickMode.Files, false, null, null, "Select a floor plan", "Import");
    }

    private void ImportImageFile(string sourcePath, string levelId)
    {
        string stored = ResidenceStore.ImportUnderlay(Ctx.Doc.id, sourcePath, out string err);
        if (stored == null) { Ctx.Controller.Status("Import failed: " + err); return; }
        AdoptSketch(stored, null, 0, levelId);
    }

    /// <summary>
    /// Writes a freshly imported sketch onto the document and points the quad at it. One place, so a
    /// PDF page and a photograph land in exactly the same state. Uncalibrated, at the origin.
    /// </summary>
    private void AdoptSketch(string storedFileName, string sourceDocument, int sourcePage, string levelId)
    {
        Ctx.Controller.RecordDocEdit("Import plan");
        ResidenceStore.SetUnderlay(Ctx.Doc, levelId, new UnderlayDef
        {
            imageFileName = storedFileName,
            originMeters = new[] { 0f, 0f },
            metersPerPixel = 0f,      // uncalibrated until the user measures something
            opacity = 0.6f,
            sourceDocument = sourceDocument,
            sourcePage = sourcePage,
        });
        _loadedFor = null;            // force a texture reload
        _loadError = null;
        Ctx.Controller.MarkDirty();
        Ctx.Controller.Status("Plan imported. Set its scale next.");
    }

    // ---------------------------------------------------------------------------------------
    // PDF
    // ---------------------------------------------------------------------------------------

    private PdfRaster.PdfDocument _pdf;   // held open only while the page picker is up
    private string _pdfPath;
    private string _pdfLevelId;           // the story a single picked page lands on
    private float _pdfDpi;
    private Texture2D[] _pdfThumbs;       // 1-based, index 0 unused; filled in a few pages per frame
    private int _pdfPicked = 1;

    private bool PickingPage => _pdf != null;

    private void BeginPdfImport(string path, string levelId)
    {
        ClosePdf();

        if (!PdfRaster.Open(path, out var doc, out string err))
        {
            Ctx.Controller.Status("Import failed: " + err);
            return;
        }

        // One page is not a choice, so it is not offered as one. It imports exactly as an image does.
        if (doc.PageCount == 1)
        {
            ImportPage(doc, path, 1, doc.DocumentDpi(), levelId);
            doc.Dispose();
            return;
        }

        _pdf = doc;
        _pdfPath = path;
        _pdfLevelId = levelId;
        _pdfDpi = doc.DocumentDpi();
        _pdfThumbs = new Texture2D[doc.PageCount + 1];
        _pdfPicked = 1;
    }

    private void ImportPage(PdfRaster.PdfDocument doc, string sourcePath, int page, float dpi, string levelId)
    {
        byte[] png = doc.RenderPng(page, dpi);
        if (png == null) { Ctx.Controller.Status("That page could not be rendered."); return; }

        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        // A plain ASCII hyphen, because ResidenceStore.SanitizeFileName rewrites anything outside
        // [A-Za-z0-9._ -()] to an underscore: an em dash would land on disk as "plan _ p2.png".
        string name = doc.PageCount > 1 ? $"{stem} - p{page}.png" : stem + ".png";

        string stored = ResidenceStore.ImportUnderlayBytes(Ctx.Doc.id, png, name, out string err);
        if (stored == null) { Ctx.Controller.Status("Import failed: " + err); return; }

        AdoptSketch(stored, Path.GetFileName(sourcePath), doc.PageCount > 1 ? page : 0, levelId);
    }

    /// <summary>
    /// Every page, as its own story. Page 1 lands on the floor already being edited and each further
    /// page adds one above it, so importing a two-page set into a fresh residence leaves it with exactly
    /// two floors rather than three.
    ///
    /// One RecordDocEdit for the whole thing: picking the wrong PDF is one mistake and should be one
    /// undo, not one per page.
    /// </summary>
    private void ImportAllPages()
    {
        if (_pdf == null) return;

        var c = Ctx.Controller;
        var doc = Ctx.Doc;
        string source = Path.GetFileName(_pdfPath);
        string stem = Path.GetFileNameWithoutExtension(_pdfPath);

        c.RecordDocEdit("Import plan");

        int startIndex = c.LevelIndex;
        int pageCount = _pdf.PageCount;   // read before ClosePdf, which drops the document
        int imported = 0;

        for (int page = 1; page <= _pdf.PageCount; page++)
        {
            byte[] png = _pdf.RenderPng(page, _pdfDpi);
            if (png == null) { c.Status($"Page {page} could not be rendered."); continue; }

            string stored = ResidenceStore.ImportUnderlayBytes(doc.id, png, $"{stem} - p{page}.png",
                                                          out string err);
            if (stored == null) { c.Status("Import failed: " + err); continue; }

            // Page 1 takes the floor already open; the rest add one each. AddLevel writes into every
            // variant with a shared id, which is what lets the sketch be keyed by that id.
            int levelIndex = page == 1 ? startIndex : ResidenceStore.AddLevel(doc);
            string levelId = LevelIdAt(levelIndex);
            if (levelId == null) continue;

            ResidenceStore.SetUnderlay(doc, levelId, new UnderlayDef
            {
                imageFileName = stored,
                originMeters = new[] { 0f, 0f },
                metersPerPixel = 0f,
                opacity = 0.6f,
                sourceDocument = source,
                sourcePage = page,
            });
            imported++;
        }

        ClosePdf();
        c.MarkDirty();

        // Land back on the first imported page, which is where the calibration has to happen.
        c.SetActiveLevel(startIndex);
        _loadedFor = null;
        c.Status(imported == pageCount
                 ? $"Imported {imported} floors. Set the scale on this one and it applies to them all."
                 : $"Imported {imported} of {pageCount} floors. Set the scale next.");
    }

    private bool _pendingImportAll;

    private void ClosePdf()
    {
        if (_pdfThumbs != null)
            foreach (var t in _pdfThumbs) if (t != null) Object.Destroy(t);
        _pdfThumbs = null;

        _pdf?.Dispose();
        _pdf = null;
        _pdfPath = null;
        _pdfLevelId = null;
    }

    /// <summary>
    /// The page grid, in the shape of the furniture catalog's: a tile per page, the page drawn on it.
    /// While this is up it IS the rail: a picker you can click away from is one that strands an open
    /// document and a dozen page-sized textures.
    /// </summary>
    private void DrawPagePicker()
    {
        UITheme.Header($"{_pdf.PageCount} pages");
        UITheme.Value(Path.GetFileName(_pdfPath), "The PDF being imported",
                      GUILayout.Width(UITheme.ContentWidth));

        UITheme.Gap();

        _pageScroll = UITheme.BeginScroll(_pageScroll, GUILayout.Height(GRID_HEIGHT));
        int cols = Mathf.Max(1, Mathf.FloorToInt(UITheme.ContentWidth / (THUMB_SIZE + TILE_GAP)));
        int col = 0;
        GUILayout.BeginHorizontal();
        for (int p = 1; p <= _pdf.PageCount; p++)
        {
            if (col >= cols) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); col = 0; }
            col++;

            if (UITheme.Thumb(ThumbFor(p), "Page " + p, _pdfPicked == p, THUMB_SIZE)) _pdfPicked = p;
            Vector2 inches = _pdf.PageSizeInches(p);
            UITheme.Tip($"Page {p}, {inches.x:F1}\" × {inches.y:F1}\" sheet");
        }
        GUILayout.EndHorizontal();
        UITheme.EndScroll();

        UITheme.Gap();
        if (UITheme.PrimaryButton($"Use all {_pdf.PageCount} pages as floors"))
        {
            _pendingImportAll = true;   // deferred: this adds stories, which changes the rail's shape
        }
        UITheme.Tip($"One floor per page, stacked from the ground up. Calibrating any one of them "
                    + "sets the scale for all of them, because every page is rendered at the same "
                    + "resolution.");

        UITheme.Gap();
        if (UITheme.SecondaryButton("Use only page " + _pdfPicked))
        {
            ImportPage(_pdf, _pdfPath, _pdfPicked, _pdfDpi, _pdfLevelId ?? Ctx.Level?.id);
            ClosePdf();
        }
        UITheme.Tip("Use this page as the plan of the floor you imported it for. Its scale is set "
                    + "next, by measuring one known dimension on it.");

        UITheme.Gap();
        if (UITheme.GhostButton("Cancel")) ClosePdf();
        UITheme.Tip("Import nothing and close this PDF");
    }

    // Rendering every page up front hitches for a second on a forty-page document, so the grid fills
    // in over a few frames. Only on Repaint: a texture created during the Layout pass would be a
    // different allocation than the one drawn, for no benefit.
    private Texture2D ThumbFor(int page)
    {
        if (_pdfThumbs == null || page < 1 || page >= _pdfThumbs.Length) return null;
        if (_pdfThumbs[page] != null) return _pdfThumbs[page];
        if (Event.current.type != EventType.Repaint) return null;
        if (_thumbBudget <= 0) return null;

        _thumbBudget--;
        Vector2 pts = _pdf.PageSizePoints(page);
        float longest = Mathf.Max(1f, Mathf.Max(pts.x, pts.y));
        _pdfThumbs[page] = _pdf.Render(page, THUMB_PIXELS * 72f / longest);
        return _pdfThumbs[page];
    }

    private int _thumbBudget;
    private Vector2 _pageScroll;

    private const float THUMB_SIZE = 76f;    // the furniture catalog's tile size, so the two rails match
    private const float TILE_GAP = UITheme.TileGap;
    private const float GRID_HEIGHT = UITheme.GridHeight;
    private const float THUMB_PIXELS = 160f; // longest side of a page thumbnail
    private const int THUMBS_PER_FRAME = 2;

    // ---------------------------------------------------------------------------------------

    private void EnsureQuad()
    {
        var underlay = ResidenceStore.UnderlayFor(Ctx?.Doc, Ctx?.Level?.id);
        if (underlay == null || string.IsNullOrEmpty(underlay.imageFileName)) { DestroyQuad(); return; }

        // Keyed by story as well as file: switching floors swaps the sketch, and two floors of one
        // plan set can perfectly well have been traced from the same image.
        string key = Ctx.Doc.id + "|" + Ctx.Level?.id + "|" + underlay.imageFileName;
        if (_quad != null && _loadedFor == key) { ApplyTransform(underlay); return; }
        // A sketch that has already failed to load is remembered by key with no quad, so a broken file
        // is read from disk once rather than on every frame the Sketch tool is open.
        if (_quad == null && _loadedFor == key) return;

        DestroyQuad();

        string path = ResidenceStore.UnderlayPath(Ctx.Doc.id, underlay.imageFileName);
        if (path == null || !File.Exists(path))
        {
            _loadError = "That sketch file is missing from this residence's folder.";
            _loadedFor = key;   // do not retry the same missing file every frame
            return;
        }

        _texture = new Texture2D(2, 2);
        if (!_texture.LoadImage(File.ReadAllBytes(path)))
        {
            DestroyQuad();
            _loadError = "That image could not be read. PNG and JPG are the formats that work.";
            _loadedFor = key;
            return;
        }
        _texture.wrapMode = TextureWrapMode.Clamp;
        _loadError = null;

        _quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _quad.name = "SketchUnderlay";
        Object.Destroy(_quad.GetComponent<Collider>());   // must never intercept a tracing click

        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        mat.mainTexture = _texture;
        // The whole transparent surface state, not just the _Surface float: URP only translates
        // _Surface into blend state inside its editor ShaderGUI, so a material configured from code
        // must set the blend mode, depth write and keyword itself. Without these the quad renders
        // opaque and the opacity in ApplyTransform does nothing.
        mat.SetFloat("_Surface", 1f);                     // transparent
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = 3000;
        _quad.GetComponent<MeshRenderer>().sharedMaterial = mat;

        _loadedFor = key;
        ApplyTransform(underlay);
    }

    private void ApplyTransform(UnderlayDef underlay)
    {
        if (_quad == null || _texture == null) return;

        float mpp = underlay.metersPerPixel > 0f ? underlay.metersPerPixel : DefaultMetersPerPixel();
        float w = _texture.width * mpp;
        float h = _texture.height * mpp;

        float y = (Ctx?.Level?.elevation ?? 0f) - 0.01f;   // just under the floors
        _quad.transform.position = new Vector3(
            (underlay.originMeters != null && underlay.originMeters.Length >= 2 ? underlay.originMeters[0] : 0f) + 0.5f * w,
            y,
            (underlay.originMeters != null && underlay.originMeters.Length >= 2 ? underlay.originMeters[1] : 0f) + 0.5f * h);
        _quad.transform.rotation = Quaternion.Euler(90f, underlay.rotationDeg, 0f);
        _quad.transform.localScale = new Vector3(w, h, 1f);

        var mr = _quad.GetComponent<MeshRenderer>();
        if (mr != null && mr.sharedMaterial != null)
        {
            var c = Color.white;
            c.a = Mathf.Clamp01(underlay.opacity);
            mr.sharedMaterial.color = c;
        }
    }

    private void DestroyQuad()
    {
        if (_quad != null) Object.Destroy(_quad);
        if (_texture != null) Object.Destroy(_texture);
        _quad = null;
        _texture = null;
        _loadedFor = null;
    }

    /// <summary>
    /// The instruction for the calibration step in progress. One string, used twice: as the step
    /// indicator's tooltip in the rail, and mirrored at the cursor over the image, which is the one
    /// that matters, because that is where you are looking while the app waits for a click.
    /// </summary>
    private string CalibPrompt => _calibStage switch
    {
        1 => "Click the first end of something you know the length of: a wall, a door, a graph-paper "
             + "gridline.",
        2 => "Click the other end.",
        3 => "How long is that in real life?",
        _ => null,
    };

    public override void DrawOverlay()
    {
        if (Ctx?.Cam == null || _calibStage == 0) return;

        float y = Ctx.Level?.elevation ?? 0f;
        var color = new Color(1f, 0.85f, 0.25f);

        // Stage 1 had nothing on screen at all: the rail said "click the first end" and the image, the
        // thing being clicked, said nothing. With the rail's prose gone this is the whole prompt.
        if (_calibStage == 1 && Ctx.GroundPoint(out Vector2 first) &&
            OverlayDraw.ToScreen(Ctx.Cam, first, y, out Vector2 f1))
            OverlayDraw.Readout(f1, "Click one end of something you know the length of");

        if (_calibStage >= 2 && OverlayDraw.ToScreen(Ctx.Cam, _calibA, y, out Vector2 ga))
            OverlayDraw.Dot(ga, 11f, color);

        if (_calibStage == 2 && Ctx.GroundPoint(out Vector2 live) &&
            OverlayDraw.ToScreen(Ctx.Cam, _calibA, y, out Vector2 a2) &&
            OverlayDraw.ToScreen(Ctx.Cam, live, y, out Vector2 l2))
        {
            OverlayDraw.Line(a2, l2, color, 3f);
            OverlayDraw.Readout(l2, "Click the other end");
        }

        if (_calibStage == 3 &&
            OverlayDraw.ToScreen(Ctx.Cam, _calibA, y, out Vector2 a3) &&
            OverlayDraw.ToScreen(Ctx.Cam, _calibB, y, out Vector2 b3))
        {
            OverlayDraw.Line(a3, b3, color, 3f);
            OverlayDraw.Dot(b3, 11f, color);
            OverlayDraw.Readout((a3 + b3) * 0.5f, "How long is this?");
        }
    }
}
