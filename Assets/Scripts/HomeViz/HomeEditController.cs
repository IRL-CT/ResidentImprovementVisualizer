using System.Collections.Generic;
using Newtonsoft.Json;
using SimpleFileBrowser;
using UnityEngine;
using UnityEngine.InputSystem;

// The HomeViz application shell: holds the open document, owns the tool registry, draws the rails,
// and hosts undo.
//
// This is deliberately THIN. EditController is 5,424 lines because every tool's input handling,
// preview, commit logic, and panel live inline in one file behind a 13-case enum — so adding a tool
// means editing four places in a monolith. Here a tool is a file that implements IHomeTool plus one
// line in Register(), and this class never needs to know what any of them do.
//
// Layout follows the existing docked-rails idiom (UIShell / Redesign.html): library on the left,
// command bar across the top, inspector on the right, scene in the middle.
//
// The command bar drives a WORKFLOW STAGE (HomeWorkflow), not a mode in Brownfield's sense. Brownfield
// is a site tool whose operator ranges freely over terrain, buildings and generation; HomeViz is used
// in a meeting and its work has an order — import a plan, trace it, furnish it, then compare options.
// Showing one stage's tools at a time is what keeps the rail short enough to read at a table, and it
// is what lets outdoor work be present for the homes that need it and absent for the ones that do not.
public class HomeEditController : MonoBehaviour, EditHistory.IHost
{
    // USER WIRES THIS IN INSPECTOR:
    [SerializeField] private HomeRenderer homeRenderer;
    // USER WIRES THIS IN INSPECTOR:
    [SerializeField] private ViewController viewController;
    // USER WIRES THIS IN INSPECTOR: falls back to Camera.main.
    [SerializeField] private Camera cam;

    [Header("Layout")]
    [SerializeField] private float leftRailWidth = 250f;
    [SerializeField] private float rightRailWidth = 310f;
    // Tall enough for a full-height UITheme.CommandBar: PrimaryH (44) plus the Pad (14) that
    // UITheme.Inset takes off each side. Anything less clips the stage buttons.
    [SerializeField] private float topBarHeight = 72f;

    // ---------------------------------------------------------------------------------------

    public HomeDoc Doc { get; private set; }
    public VariantDef Variant => HomeStore.ActiveVariant(Doc);
    public LevelDef Level
    {
        get
        {
            var v = Variant;
            return v?.levels != null && v.levels.Count > 0 ? v.levels[0] : null;
        }
    }

    public bool Dirty { get; private set; }
    public bool PointerOverUI { get; private set; }

    // Shared selection. Tools read it; SelectTool writes it. Kept here rather than in a tool so the
    // inspector rail can describe the selection no matter which tool is active.
    public HomeElementMarker.Kind SelectedKind { get; set; }
    public string SelectedId { get; set; }
    public void ClearSelection() => SelectedId = null;

    public EditHistory History { get; private set; }
    public HomeRenderer Renderer => homeRenderer;

    private readonly List<IHomeTool> _tools = new List<IHomeTool>();
    private IHomeTool _active;
    private HomeToolContext _ctx;

    // The active workflow stage, and the stages currently on offer (Outdoors is absent unless the
    // home has switched its exterior layer on).
    private HomeStage _stage = HomeStage.Sketch;
    private List<HomeStage> _stages = HomeWorkflow.VisibleStages(null);

    // A stage change rewrites the command bar AND the tool list, so applying one from inside OnGUI
    // would leave IMGUI's layout pass and repaint pass disagreeing about how many controls exist.
    // UI-initiated changes queue here and land at the top of the next Update instead.
    private HomeStage? _pendingStage;
    private bool _stagesIncludeOutdoors;

    private List<HomeSummary> _library = new List<HomeSummary>();
    private Vector2 _libScroll, _railScroll;
    private string _status;
    private float _statusUntil;

    // Both collapsed by default. Design options opens itself in Review, where comparing IS the work.
    private bool _showVariants;
    private bool _showOutdoors;
    private bool _showSamples;

    private Rect _leftRect, _rightRect, _topRect;

    // ---------------------------------------------------------------------------------------

    private void Awake()
    {
        if (cam == null) cam = Camera.main;
        if (homeRenderer == null) homeRenderer = FindFirstObjectByType<HomeRenderer>();
        if (viewController == null) viewController = FindFirstObjectByType<ViewController>();

        History = new EditHistory(this);
        _ctx = new HomeToolContext
        {
            Controller = this, Renderer = homeRenderer, View = viewController,
            History = History, Cam = cam,
        };

        Register(new SelectTool());
        Register(new WallTool());
        Register(new OpeningTool());
        Register(new RoomTool());
        Register(new FurnitureTool());
        Register(new MeasureTool());
        Register(new UnderlayTool());
        Register(new OutdoorTool());
    }

    private void Register(IHomeTool tool) => _tools.Add(tool);

    private IHomeTool FindTool(string id)
    {
        foreach (var t in _tools) if (t.Id == id) return t;
        return null;
    }

    private void Start()
    {
        _ = HomeStore.Settings;   // applies the unit preference before anything is formatted

        // First run only. Landing on an empty library means the first thing anyone has to do is the
        // hardest step of the workflow — import a plan and calibrate it — so the samples go in before
        // the list is read. Archiving one keeps it archived; see HomeSettings.samplesSeeded.
        SampleHomeInstaller.SeedIfNeeded();
        SampleHomeInstaller.VerifyAgainstCatalog(homeRenderer?.Catalog);

        RefreshLibrary();
        SetStage(HomeStage.Sketch);

        // Reopen whatever was last worked on: this is a tool people return to across sessions,
        // and landing on an empty library every time is needless friction.
        string last = HomeStore.Settings.lastOpenedHomeId;
        if (!string.IsNullOrEmpty(last) && HomeStore.Exists(last)) OpenHome(last);
    }

    private void Update()
    {
        SyncStages();

        if (_pendingStage.HasValue)
        {
            var next = _pendingStage.Value;
            _pendingStage = null;
            SetStage(next);
        }

        if (Doc == null) return;

        HandleGlobalKeys();

        if (!PointerOverUI) _active?.HandleInput();

        // Central gesture close, matching EditController's convention: a whole drag collapses into
        // one undo entry regardless of which tool ran it.
        if (Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame)
            History.EndGesture();
    }

    private void HandleGlobalKeys()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        if ((kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed))
        {
            if (kb.zKey.wasPressedThisFrame) { History.Undo(); homeRenderer?.Rebuild(); MarkDirty(); }
            if (kb.yKey.wasPressedThisFrame) { History.Redo(); homeRenderer?.Rebuild(); MarkDirty(); }
            if (kb.sKey.wasPressedThisFrame) SaveHome();
        }

        if (kb.escapeKey.wasPressedThisFrame) ClearSelection();

        // Digits pick a tool WITHIN the active stage, so the count is small, the numbers match the
        // chips you can see, and every tool has a key — the flat 1–6 this replaced left the seventh
        // tool (Sketch) unreachable from the keyboard. Ctrl+digit moves between stages.
        int digit = -1;
        if (kb.digit1Key.wasPressedThisFrame) digit = 0;
        else if (kb.digit2Key.wasPressedThisFrame) digit = 1;
        else if (kb.digit3Key.wasPressedThisFrame) digit = 2;
        else if (kb.digit4Key.wasPressedThisFrame) digit = 3;
        else if (kb.digit5Key.wasPressedThisFrame) digit = 4;
        if (digit < 0) return;

        if (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed)
        {
            if (digit < _stages.Count) SetStage(_stages[digit]);
        }
        else
        {
            var ids = HomeWorkflow.ToolIdsFor(_stage);
            if (digit < ids.Length) SetTool(FindTool(ids[digit]));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Document lifecycle
    // ---------------------------------------------------------------------------------------

    public void RefreshLibrary() => _library = HomeStore.List();

    public void NewHome()
    {
        Doc = HomeStore.Create("Untitled home");
        AfterOpen();
        Status("Created " + Doc.name);
    }

    public void OpenHome(string id)
    {
        var doc = HomeStore.Load(id);
        if (doc == null) { Status("Could not open that home."); return; }
        Doc = doc;
        AfterOpen();
        Status("Opened " + Doc.name);
    }

    private void AfterOpen()
    {
        Dirty = false;
        History.Clear();
        ClearSelection();
        RefreshLibrary();
        SyncStages();   // this home may or may not have an exterior; the command bar follows it

        homeRenderer?.RenderHome(Doc, Doc.activeVariantId);
        viewController?.FrameContent();

        HomeStore.Settings.lastOpenedHomeId = Doc.id;
        HomeStore.SaveSettings();
    }

    public void SaveHome()
    {
        if (Doc == null) return;
        if (HomeStore.Save(Doc, out string err))
        {
            Dirty = false;
            RefreshLibrary();
            Status("Saved " + Doc.name);
        }
        else Status("Save failed: " + err);
    }

    public void MarkDirty() => Dirty = true;

    public void SetActiveVariant(string variantId)
    {
        if (Doc == null) return;
        Doc.activeVariantId = variantId;
        ClearSelection();
        History.Clear();   // undo does not span variants — each is its own editing context
        homeRenderer?.RenderHome(Doc, variantId);
        MarkDirty();
    }

    // ---------------------------------------------------------------------------------------
    // Tools
    // ---------------------------------------------------------------------------------------

    public void SetTool(IHomeTool tool)
    {
        if (tool == null || _active == tool) return;
        _active?.Exit();
        _active = tool;
        _active?.Enter(_ctx);

        // Picking a tool by any route (hotkey, another panel) carries the rail to the stage it lives
        // in, so the chips never disagree with what is actually active.
        _stage = HomeWorkflow.StageOf(tool.Id, _stage);
    }

    public IHomeTool ActiveTool => _active;

    public void SetStage(HomeStage stage)
    {
        _stage = stage;
        // Review is where a proposal gets read out, so its panel opens with it. Everywhere else the
        // rail belongs to the tool.
        _showVariants = stage == HomeStage.Review;
        SetTool(FindTool(HomeWorkflow.PrimaryToolId(stage)));
    }

    /// <summary>Queues a stage change for the next frame. Use this from anything drawn in OnGUI.</summary>
    public void RequestStage(HomeStage stage) => _pendingStage = stage;

    /// <summary>
    /// Keeps the stage list in step with the document — Outdoors appears and disappears with
    /// HomeDoc.exteriorEnabled. Runs once per frame from Update, never from OnGUI, so the command bar
    /// has the same number of buttons in a frame's layout pass and its repaint pass.
    /// </summary>
    private void SyncStages()
    {
        bool wantOutdoors = Doc != null && Doc.exteriorEnabled;
        if (_stages != null && wantOutdoors == _stagesIncludeOutdoors) return;

        _stages = HomeWorkflow.VisibleStages(Doc);
        _stagesIncludeOutdoors = wantOutdoors;
        if (!_stages.Contains(_stage)) RequestStage(HomeStage.Draw);
    }

    // ---------------------------------------------------------------------------------------
    // EditHistory.IHost — the whole HomeDoc is the undo unit
    // ---------------------------------------------------------------------------------------

    public string ActiveContextId(EditHistory.Scope scope)
        => scope == EditHistory.Scope.Environment ? Doc?.id : null;

    public string Serialize(EditHistory.Scope scope, string contextId)
    {
        if (scope != EditHistory.Scope.Environment || Doc == null || Doc.id != contextId) return null;
        return JsonConvert.SerializeObject(Doc);
    }

    public void Restore(EditHistory.Scope scope, string contextId, string json)
    {
        if (scope != EditHistory.Scope.Environment || json == null) return;

        var restored = JsonConvert.DeserializeObject<HomeDoc>(json);
        if (restored == null) return;

        HomeStore.Migrate(restored);
        Doc = restored;
        ClearSelection();
        homeRenderer?.RenderHome(Doc, Doc.activeVariantId);
    }

    // ---------------------------------------------------------------------------------------
    // UI
    // ---------------------------------------------------------------------------------------

    private void OnGUI()
    {
        float w = Screen.width, h = Screen.height;
        _topRect = new Rect(leftRailWidth, 0f, w - leftRailWidth - rightRailWidth, topBarHeight);
        _leftRect = new Rect(0f, 0f, leftRailWidth, h);
        _rightRect = new Rect(w - rightRailWidth, 0f, rightRailWidth, h);

        Vector2 m = Event.current.mousePosition;
        PointerOverUI = _leftRect.Contains(m) || _rightRect.Contains(m) || _topRect.Contains(m);

        DrawLeftRail();
        DrawTopBar();
        DrawRightRail();

        if (Doc != null && !PointerOverUI) _active?.DrawOverlay();

        DrawStatus();
    }

    private void DrawLeftRail()
    {
        UITheme.PanelBackground(_leftRect);
        GUILayout.BeginArea(UITheme.Inset(_leftRect));

        UITheme.Title("CXRHomeViz");
        UITheme.Note("Home improvement visioning");
        GUILayout.Space(8);

        GUILayout.BeginHorizontal();
        if (UITheme.PrimaryButton("New home")) NewHome();
        if (UITheme.SecondaryButton("Import")) ImportHome();
        GUILayout.EndHorizontal();

        // Its own row: three buttons will not fit the rail's width.
        if (UITheme.SecondaryButton(_showSamples ? "Sample homes ▾" : "Sample homes ▸"))
            _showSamples = !_showSamples;

        if (_showSamples) DrawSamplePicker();

        GUILayout.Space(10);
        UITheme.Header($"Homes ({_library.Count})");

        // Nothing on disk yet. Everything here is local files, so the first move is always the same
        // one: get a floor plan in and calibrate it. Say so, and take them there. Reachable after
        // archiving everything, since the samples are only seeded once.
        if (_library.Count == 0)
        {
            UITheme.Note("No homes yet. Start one, then import a floor plan sketch and set its scale — "
                       + "everything you trace afterwards is at true size. Or open a sample above to "
                       + "look around a finished plan first.");
            if (UITheme.SecondaryButton("Start from a floor plan"))
            {
                NewHome();
                RequestStage(HomeStage.Sketch);
            }
        }

        _libScroll = GUILayout.BeginScrollView(_libScroll, GUILayout.MaxHeight(Screen.height * 0.45f));
        foreach (var row in _library)
        {
            bool isOpen = Doc != null && Doc.id == row.id;
            string label = (row.favorite ? "★ " : "") + row.name;
            if (UITheme.StateRow(label, $"v{row.version} · {row.variantCount} variant{(row.variantCount == 1 ? "" : "s")}", isOpen))
                OpenHome(row.id);
        }
        GUILayout.EndScrollView();

        if (Doc != null)
        {
            GUILayout.Space(10);
            UITheme.Header("This home");
            Doc.name = GUILayout.TextField(Doc.name ?? "");

            GUILayout.BeginHorizontal();
            if (UITheme.PrimaryButton(Dirty ? "Save *" : "Save")) SaveHome();
            if (UITheme.SecondaryButton("Save As")) SaveAs();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (UITheme.GhostButton("Export")) ExportHome();
            if (UITheme.GhostButton(Doc.favorite ? "Unstar" : "Star"))
            {
                HomeStore.ToggleFavorite(Doc.id);
                Doc.favorite = !Doc.favorite;
                RefreshLibrary();
            }
            GUILayout.EndHorizontal();

            if (UITheme.DangerButton("Archive")) ArchiveHome();
        }

        GUILayout.EndArea();
    }

    // The built-in samples. Each click writes a fresh COPY into the library rather than opening a
    // shared original, so a sample can be pulled again after it has been edited or archived.
    private void DrawSamplePicker()
    {
        UITheme.Note("Finished plans to look around. Each one is added as a new home; the baseline is "
                   + "locked, so use Design options → Unlock or New proposal to change it.");

        foreach (var spec in SampleHomes.All)
        {
            if (!UITheme.StateRow(spec.displayName, spec.blurb, false)) continue;

            var doc = SampleHomeInstaller.Install(spec.key);
            if (doc == null) { Status("Could not add that sample."); continue; }

            Doc = doc;
            AfterOpen();
            Status("Added " + doc.name);
        }
    }

    private void DrawTopBar()
    {
        UITheme.PanelBackground(_topRect);
        GUILayout.BeginArea(UITheme.Inset(_topRect));
        GUILayout.BeginHorizontal();

        // The stage bar. Same widget as Brownfield's command bar, so the two apps read as one family
        // even though what they command is different.
        if (Doc != null)
        {
            var labels = HomeWorkflow.LabelsFor(_stages);
            int current = Mathf.Max(0, _stages.IndexOf(_stage));
            int picked = UITheme.CommandBar(current, labels, GUILayout.Width(104f * labels.Length));
            if (picked != current && picked >= 0 && picked < _stages.Count) RequestStage(_stages[picked]);

            GUILayout.Space(18);
        }

        // View modes
        foreach (ViewController.Mode mode in System.Enum.GetValues(typeof(ViewController.Mode)))
        {
            bool on = viewController != null && viewController.Current == mode;
            if (UITheme.Chip(mode.ToString(), on) && viewController != null)
                viewController.SetMode(mode);
        }

        GUILayout.Space(14);

        // The eye-height toggle — the cheapest meaningful accessibility feature in the tool.
        if (viewController != null && viewController.Current == ViewController.Mode.Walkthrough)
        {
            if (UITheme.Chip(viewController.Seated ? "Seated (wheelchair)" : "Standing", viewController.Seated))
                viewController.ToggleSeated();
            GUILayout.Space(14);
        }

        GUILayout.FlexibleSpace();

        if (Doc != null)
        {
            GUI.enabled = History.CanUndo;
            if (UITheme.GhostButton("Undo")) { History.Undo(); homeRenderer?.Rebuild(); MarkDirty(); }
            GUI.enabled = History.CanRedo;
            if (UITheme.GhostButton("Redo")) { History.Redo(); homeRenderer?.Rebuild(); MarkDirty(); }
            GUI.enabled = true;
        }

        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    private void DrawRightRail()
    {
        UITheme.PanelBackground(_rightRect);
        GUILayout.BeginArea(UITheme.Inset(_rightRect));

        if (Doc == null)
        {
            UITheme.Title("No home open");
            UITheme.Note("Create a new home, or open one from the library, to begin.");
            GUILayout.EndArea();
            return;
        }

        _railScroll = GUILayout.BeginScrollView(_railScroll);

        // 1 — only this stage's tools. The old rail listed all seven at once, which meant the
        // controls for tracing a sketch sat next to the ones for measuring a finished proposal.
        UITheme.Header(HomeWorkflow.Label(_stage));
        DrawStageTools();

        // 2 — the active tool owns the rest of the rail.
        UITheme.Divider();
        UITheme.Header(_active != null ? _active.DisplayName : "Tool");
        _active?.DrawRail();

        // 3 — everything that is not this stage's work, folded away.
        GUILayout.Space(16);
        DrawVariantFoldout();
        DrawOutdoorFoldout();

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawStageTools()
    {
        var ids = HomeWorkflow.ToolIdsFor(_stage);

        GUILayout.BeginHorizontal();
        int col = 0;
        foreach (var id in ids)
        {
            var tool = FindTool(id);
            if (tool == null) continue;
            if (col > 0 && col % 2 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            if (UITheme.Chip(tool.DisplayName, _active == tool)) SetTool(tool);
            col++;
        }
        GUILayout.EndHorizontal();
    }

    private void DrawVariantFoldout()
    {
        _showVariants = UITheme.Foldout(_showVariants, $"Design options ({Doc.variants.Count})");
        if (_showVariants) DrawVariantPanel();
    }

    // The exterior gate, and nothing else. Collapsed, at the bottom, below the work — which is the
    // right weight for a feature most homes in this tool never use. Turning it on adds the Outdoors
    // stage to the command bar; turning it off takes every outdoor control away again.
    private void DrawOutdoorFoldout()
    {
        _showOutdoors = UITheme.Foldout(_showOutdoors, "Outdoors (optional)");
        if (_showOutdoors) DrawExteriorToggle();
    }

    // Baseline + named proposals. Switching is a re-render, so it is instant.
    private void DrawVariantPanel()
    {
        UITheme.Header($"Design options ({Doc.variants.Count})");

        foreach (var v in Doc.variants)
        {
            if (v == null) continue;
            bool active = v.id == Doc.activeVariantId;
            string state = v.isBaseline ? (v.locked ? "baseline · locked" : "baseline") : (v.locked ? "locked" : "proposal");
            if (UITheme.StateRow(v.name ?? "(unnamed)", state, active)) SetActiveVariant(v.id);
        }

        var cur = Variant;
        if (cur == null) return;

        GUILayout.BeginHorizontal();
        if (UITheme.SecondaryButton("New proposal")) NewProposalFrom(cur);
        if (UITheme.GhostButton(cur.locked ? "Unlock" : "Lock"))
        {
            cur.locked = !cur.locked;
            MarkDirty();
        }
        GUILayout.EndHorizontal();

        if (!cur.isBaseline && UITheme.DangerButton("Delete this proposal"))
        {
            Doc.variants.Remove(cur);
            SetActiveVariant(HomeStore.Baseline(Doc)?.id);
            MarkDirty();
        }

        // Compare against the baseline: the textual change list is what gets read aloud in a meeting.
        var baseline = HomeStore.Baseline(Doc);
        if (baseline != null && baseline != cur)
        {
            GUILayout.Space(8);
            UITheme.Header("Changes from " + baseline.name);

            var changes = VariantDiff.Compare(baseline, cur);
            if (changes.Count == 0) UITheme.Note("Identical to the baseline so far.");
            foreach (var c in changes) UITheme.Note("• " + c);

            if (UITheme.GhostButton("Show baseline as ghost"))
                homeRenderer?.SetGhostVariant(baseline.id, true);
            if (UITheme.GhostButton("Hide ghost"))
                homeRenderer?.SetGhostVariant(baseline.id, false);
        }
    }

    private void NewProposalFrom(VariantDef source)
    {
        // Deep copy PRESERVING every element id — that is what lets VariantDiff report a widened door
        // as a modification instead of a delete plus an add.
        var copy = HomeStore.Clone(source);
        copy.id = System.Guid.NewGuid().ToString();
        copy.name = "Proposal " + (char)('A' + Mathf.Max(0, Doc.variants.Count - 1));
        copy.description = "Based on " + source.name;
        copy.basedOnVariantId = source.id;
        copy.isBaseline = false;
        copy.locked = false;

        RecordDocEdit("New proposal");
        Doc.variants.Add(copy);
        SetActiveVariant(copy.id);
        Status("Created " + copy.name);
    }

    private void DrawExteriorToggle()
    {
        bool on = GUILayout.Toggle(Doc.exteriorEnabled, "  Include outdoor additions");
        if (on != Doc.exteriorEnabled)
        {
            RecordDocEdit("Toggle exterior");
            Doc.exteriorEnabled = on;

            var v = Variant;
            if (on && v != null && v.exterior == null) v.exterior = ExteriorBridge.NewExterior();

            MarkDirty();
            homeRenderer?.Rebuild();

            // The Outdoors stage exists only while this is on. SyncStages picks the change up next
            // frame; turning it on lands you there so the tools are one click away, not a hunt.
            if (on) RequestStage(HomeStage.Outdoors);
        }

        UITheme.Note(Doc.exteriorEnabled
            ? "On. The Outdoors stage draws entry ramps, walkways and railings around the home; "
            + "they render through the same site renderer the Brownfield tool uses."
            : "Off. Turn on for a home where an outdoor change — an entry ramp, a path to the door, "
            + "a porch railing — is part of the proposal.");
    }

    public void RecordDocEdit(string label) => History.RecordBefore(EditHistory.Scope.Environment, label);

    // ---------------------------------------------------------------------------------------

    private void SaveAs()
    {
        if (Doc == null) return;
        SaveHome();
        var copy = HomeStore.Duplicate(Doc.id, Doc.name + " copy");
        if (copy != null) { Doc = copy; AfterOpen(); Status("Saved as " + copy.name); }
    }

    private void ArchiveHome()
    {
        if (Doc == null) return;
        HomeStore.Archive(Doc.id);
        Doc = null;
        homeRenderer?.RenderHome(null);
        RefreshLibrary();
        Status("Archived.");
    }

    // Export/import is the whole sharing story now that there is no server: one self-contained file
    // holding the home plus its traced sketch, which you can email to a colleague.
    private void ExportHome()
    {
        if (Doc == null) return;
        SaveHome();

        FileBrowser.SetFilters(true, new FileBrowser.Filter("HomeViz home", HomeStore.EXPORT_EXT));
        FileBrowser.SetDefaultFilter(HomeStore.EXPORT_EXT);
        FileBrowser.ShowSaveDialog(
            paths =>
            {
                if (paths == null || paths.Length == 0) return;
                Status(HomeStore.ExportHome(Doc.id, paths[0], out string err)
                    ? "Exported to " + System.IO.Path.GetFileName(paths[0])
                    : "Export failed: " + err);
            },
            () => Status("Export cancelled."),
            FileBrowser.PickMode.Files, false, null,
            Doc.name + HomeStore.EXPORT_EXT, "Export home", "Export");
    }

    private void ImportHome()
    {
        FileBrowser.SetFilters(true, new FileBrowser.Filter("HomeViz home", HomeStore.EXPORT_EXT));
        FileBrowser.SetDefaultFilter(HomeStore.EXPORT_EXT);
        FileBrowser.ShowLoadDialog(
            paths =>
            {
                if (paths == null || paths.Length == 0) return;

                var doc = HomeStore.ImportHome(paths[0], out string err);
                if (doc == null) { Status("Import failed: " + err); return; }

                Doc = doc;
                AfterOpen();
                Status("Imported " + doc.name);
            },
            () => Status("Import cancelled."),
            FileBrowser.PickMode.Files, false, null, null, "Import home", "Import");
    }

    public void Status(string text)
    {
        _status = text;
        _statusUntil = Time.realtimeSinceStartup + 4f;
    }

    private void DrawStatus()
    {
        if (string.IsNullOrEmpty(_status) || Time.realtimeSinceStartup > _statusUntil) return;

        var r = new Rect(_topRect.x + 12f, Screen.height - 42f, _topRect.width - 24f, 28f);
        GUI.Label(r, _status);
    }
}
