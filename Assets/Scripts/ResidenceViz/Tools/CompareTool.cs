using System.Collections.Generic;
using UnityEngine;

// The change list, made into a place you can stand in rather than a paragraph you read.
//
// VariantDiff.Change has always carried `kind`, `id` and a `worldPos` anchor: everything needed to
// point at the element in the scene and act on it. The old variant panel rendered each one as
// `"• " + c`, which calls ToString() and throws all three away. So the list told you a bathroom door
// had been widened and gave you no way to find that door, and no way to change your mind about it
// short of hunting the door down by hand or undoing back past every good change made since.
//
// A TOOL rather than a foldout, because comparing needs all three of IResidenceTool's surfaces and a
// foldout has none of them: a rail for the list, DrawOverlay for the markers that put each change
// where it happens, and HandleInput for clicking those markers. It is first in the Review stage, so
// Review opens on it. Comparing IS the work there.
//
// IT COMPARES IN ONE DIRECTION: base environment → proposal, always. Before is the base environment
// and After is a proposal, and neither can be anything else. The previous version drew two identical
// Segmented rows over the same variant list, one above the other, distinguished only by tooltip, so
// both ends were arbitrary, both could name the same variant, and the diff could read backwards with
// nothing on screen saying so. VariantDiff.Compare still takes arbitrary variants; this rail simply
// no longer offers arbitrary ones.
//
// It also TAKES A CHANGE BACK OUT, through VariantRevert. See that file for why the inverse has to be
// exact.
//
// NOTHING HERE IS GATED ON Ctx.IsLocked. Every control in this rail writes to `to`: a proposal, and
// therefore always editable. IsLocked asks about the ACTIVE variant, which is a different question:
// standing on the base environment and opening Review used to hide every ✕ on a proposal that was
// perfectly editable.
//
// Selecting from a row deliberately passes reveal:false. The default carries you to the Select tab,
// which is right when you click a chair while tracing a sketch and wrong here. It would eject you
// from Compare on every row you clicked.
public class CompareTool : ResidenceToolBase
{
    public override string Id => "compare";
    public override string DisplayName => "Compare";

    public override string Hint =>
        "What a proposal changes, against the base environment. Click a change to find it in the "
        + "plan. ✕ takes that one change back out.";

    private const float MarkerHitRadius = 14f;

    private string _toId;
    private string _hoverId;
    private string _refusal;

    // Rebuilt every frame from the schema. Nothing here is cached across frames on purpose: undo
    // restores the whole ResidenceDoc without telling anyone, so a held Change would describe an element
    // that no longer exists. Kept only so DrawOverlay and HandleInput see the same list DrawRail
    // drew, within one frame.
    private readonly List<VariantDiff.Change> _changes = new List<VariantDiff.Change>();

    // The before/after overlay is ON when Review opens: the rail exists to show what changed, and
    // the overlay is the picture of it. The same two lines DrawGhostToggle runs when the toggle goes
    // on. Bring the view to After first, because the renderer diffs against whatever it renders.
    // The controller turns it off when the Review stage is left (not here: Compare ↔ Measure within
    // Review keeps it), and the toggle in the rail still works either way.
    public override void Enter(ResidenceToolContext ctx)
    {
        base.Enter(ctx);
        var from = From;
        var to = To;
        if (from == null || to == null || Ctx?.Renderer == null || Ctx.Renderer.GhostOn) return;
        if (Ctx.Variant != to) Ctx.Controller?.RequestVariant(to.id);
        Ctx.Renderer.SetGhostVariant(from.id, true);
    }

    public override void Exit()
    {
        _refusal = null;
        _hoverId = null;
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>Before. Always the base environment. See the header.</summary>
    private VariantDef From => ResidenceStore.Baseline(Ctx?.Doc);

    /// <summary>
    /// After. A proposal, never the baseline: the fallback used to be the ACTIVE variant, which is
    /// the base environment whenever Review is opened from it: an After identical to the Before.
    /// </summary>
    private VariantDef To
    {
        get
        {
            var picked = Resolve(_toId);
            if (picked != null && !picked.isBaseline) return picked;
            var active = Ctx?.Variant;
            if (active != null && !active.isBaseline) return active;
            return FirstProposal();
        }
    }

    private VariantDef FirstProposal()
    {
        var variants = Ctx?.Doc?.variants;
        if (variants == null) return null;
        foreach (var v in variants) if (v != null && !v.isBaseline) return v;
        return null;
    }

    private VariantDef Resolve(string id)
        => string.IsNullOrEmpty(id) ? null : ResidenceStore.FindVariant(Ctx?.Doc, id);

    private void Recompute()
    {
        _changes.Clear();
        var from = From;
        var to = To;
        if (from == null || to == null || from == to) return;
        _changes.AddRange(VariantDiff.Compare(from, to));
    }

    // ---------------------------------------------------------------------------------------

    public override void DrawRail()
    {
        if (Ctx?.Doc == null) return;
        Recompute();

        var from = From;
        var to = To;
        if (from == null)
        {
            UITheme.Glyph("⚠", "This residence has no base environment.", UITheme.Ink3);
            return;
        }
        if (to == null)
        {
            UITheme.Value("Before", from.name ?? "(unnamed)", "The residence as it stands.");
            UITheme.Gap();
            UITheme.Glyph("⚠", "No proposals yet. Press New proposal in the band above.", UITheme.Ink3);
            return;
        }

        DrawBeforeAfter(from, to);
        DrawDescription(to);

        UITheme.Gap();
        DrawGhostToggle(from, to);

        UITheme.Gap();
        DrawChangeList(from, to);

        UITheme.Gap();
        if (UITheme.PrimaryButton("Generate report", GUILayout.Height(UITheme.RowH + 8f)))
            Ctx.Controller?.RequestReport();
        UITheme.Tip("Save a before/after document: the plan, each changed room, and the "
                    + "turning-space numbers.");

        if (_changes.Count > 0)
        {
            UITheme.Gap();
            if (UITheme.DangerButton("Take back all " + _changes.Count + " changes"))
                RevertAll(from, to);
            UITheme.Tip("Empty this proposal, keeping its name. Undoable.");
        }
    }

    // Before is fixed and drawn as a value, not a control. There is nothing to pick. After is a
    // picker over proposals ONLY, so the two ends can never name the same variant.
    private void DrawBeforeAfter(VariantDef from, VariantDef to)
    {
        UITheme.Value("Before", from.name ?? "(unnamed)", "The residence as it stands.");

        var proposals = new List<VariantDef>();
        if (Ctx.Doc.variants != null)
            foreach (var v in Ctx.Doc.variants)
                if (v != null && !v.isBaseline) proposals.Add(v);

        UITheme.Gap();
        if (proposals.Count < 2)
        {
            UITheme.Value("After", to.name ?? "(unnamed)", "The proposal on show.");
            return;
        }

        var names = new string[proposals.Count];
        var tips = new string[proposals.Count];
        for (int i = 0; i < proposals.Count; i++)
        {
            names[i] = proposals[i].name ?? "(unnamed)";
            tips[i] = "Compare " + names[i] + " to " + (from.name ?? "the base environment") + ".";
        }

        int idx = IndexOf(proposals, to.id);
        // A Toolbar makes every cell the width of the widest, so four proposals give ~67 px each and a
        // renamed one clips immediately. The fitting that used to be done here by hand is now
        // UITheme.Segmented's own job. It takes the row label's share off first and ellipsises each
        // name into the cell it will actually land in, so this is the ordinary call. `tips` already
        // names each proposal in full.
        int next = UITheme.Segmented("After", idx, names, tips);
        if (next == idx || next < 0 || next >= proposals.Count) return;

        _toId = proposals[next].id;
        // Comparing something means looking at it. Switching After switches what is rendered, which
        // is what makes the picker feel like a view rather than a form field.
        Ctx.Controller?.RequestVariant(_toId);
    }

    // The chip's state is the RENDERER's, never a field of this tool's own. The overlay is sticky
    // across rebuilds, undo and variant switches by design (ResidenceRenderer.SetGhostVariant), and a
    // local mirror of it drifts the moment anything else touches it. ReportCapture turns it off and
    // back on around every capture. Reading GhostOn means the chip cannot lie about what is drawn.
    //
    // Turning it ON also brings the VIEW to After, because the renderer diffs against whatever it is
    // rendering: not against the pair this rail happens to be describing. Without that, opening
    // Review from the base environment (the common case: the After picker is not even drawn until
    // there are two proposals, so nothing has switched the view) lit the chip while BuildGhost
    // returned immediately on `other == _variant`, and the overlay was a silent no-op.
    private void DrawGhostToggle(VariantDef from, VariantDef to)
    {
        bool on = Ctx.Renderer != null && Ctx.Renderer.GhostOn;
        bool next = UITheme.Toggle("Before/after overlay", on, "Red: how it was. Green: how it is now.");
        if (next == on) return;

        if (next && to != null && Ctx.Variant != to) Ctx.Controller?.RequestVariant(to.id);
        Ctx.Renderer?.SetGhostVariant(from.id, next);
    }

    // The one piece of prose in this rail, and it is the user's own: the sentence that goes at the
    // top of the report and gets read out in the meeting. Content, like a variant's change list and a
    // person's note, not a caption the no-prose rule removed.
    private void DrawDescription(VariantDef to)
    {
        UITheme.Header("What this proposal does");

        // Wearing the same chrome as every other field. It was the one unstyled control in the app.
        string next = UITheme.TextArea(to.description, 70f,
                                       "A sentence or two in your own words. It heads the report.");

        if (next == to.description) return;
        to.description = next;
        Ctx.Controller?.MarkDirty();
    }

    private void DrawChangeList(VariantDef from, VariantDef to)
    {
        // No "from" in the header: the Before row above already names it.
        UITheme.Header("Changes");

        if (!string.IsNullOrEmpty(_refusal))
        {
            UITheme.Glyph("⚠", _refusal, UITheme.Danger);
        }

        if (_changes.Count == 0)
        {
            // No label: the "Changes" header two lines up already names this, and saying it twice
            // is the duplication the labels were added to remove.
            UITheme.Value("None", "Identical to " + from.name + " so far.");
            return;
        }

        _hoverId = null;

        // Grouped by the room the change happens in, because that is the unit a resident thinks in
        // and the unit the report is sectioned by. Changes with no position. Occupants, the outdoor
        // layer. Fall to a trailing group rather than being dropped.
        foreach (var group in GroupByRoom(to))
        {
            UITheme.Header(group.Key);
            foreach (var change in group.Value) DrawChangeRow(change);
        }
    }

    private void DrawChangeRow(VariantDiff.Change change)
    {
        GUILayout.BeginHorizontal();

        // Reserve the ✕'s width: change.label is composed from the user's own room names: "Changed
        // Master bedroom cased opening" is 36 characters, so without this the row takes the whole
        // rail and pushes the button that reverts it off the panel.
        bool revertable = change.kind != VariantDiff.ElementKind.Exterior;
        bool pick = UITheme.StateRow(Prefix(change.type) + " " + change.label,
                                     change.detail,
                                     change.id == Ctx.Controller?.SelectedId,
                                     muted: false,
                                     reserveRight: revertable ? UITheme.GlyphReserve : 0f);
        var rowRect = GUILayoutUtility.GetLastRect();
        UITheme.Tip(change.ToString() + "  ·  click to find it in the plan");

        if (Event.current.type == EventType.Repaint && rowRect.Contains(Event.current.mousePosition))
            _hoverId = change.id;

        // ✕ rather than the word: the row is already a sentence, and a second sentence beside it
        // would be the only two-verb row in the app.
        if (revertable)
        {
            if (UITheme.DangerButton("✕", GUILayout.Width(UITheme.GlyphW))) RevertOne(change);
            UITheme.Tip("Take this one change back out. The rest of the proposal stays.");
        }

        GUILayout.EndHorizontal();

        if (!pick) return;
        _refusal = null;
        Focus(change);
    }

    private void Focus(VariantDiff.Change change)
    {
        var controller = Ctx.Controller;
        if (controller == null) return;

        // reveal:false. See the header. Selecting must not carry the UI out of this tab.
        controller.Select(MarkerKind(change.kind), change.id, reveal: false);
        if (change.hasPos) controller.FocusElement(change.id, change.label);
    }

    // ---------------------------------------------------------------------------------------

    private void RevertOne(VariantDiff.Change change)
    {
        var from = From;
        var to = To;
        if (from == null || to == null) return;

        Ctx.RecordEdit("Take back a change");
        if (VariantRevert.Revert(from, to, change, out string reason))
        {
            _refusal = null;
            AfterRevert(from);
            Ctx.Controller?.Status("Took back: " + change);
        }
        else
        {
            // The refusal is shown, not logged. VariantRevert writes its reasons for a user.
            _refusal = reason;
        }
    }

    private void RevertAll(VariantDef from, VariantDef to)
    {
        Ctx.RecordEdit("Take back all changes");
        VariantRevert.RevertAll(from, to);
        _refusal = null;
        AfterRevert(from);
        Ctx.Controller?.Status(to.name + " now matches " + from.name + ".");
    }

    private void AfterRevert(VariantDef from)
    {
        Ctx.Controller?.ClearSelection();
        Ctx.Changed();
        // The overlay is computed from the diff, so it has to follow the diff changing.
        if (Ctx.Renderer != null && Ctx.Renderer.GhostOn) Ctx.Renderer.SetGhostVariant(from.id, true);
    }

    // ---------------------------------------------------------------------------------------

    public override void DrawOverlay()
    {
        if (Ctx?.Cam == null || Ctx.Level == null || _changes.Count == 0) return;

        float y = Ctx.Level.elevation;
        foreach (var change in _changes)
        {
            if (!change.hasPos) continue;
            if (!OverlayDraw.ToScreen(Ctx.Cam, change.worldPos, y, out Vector2 g)) continue;

            bool hot = change.id == _hoverId || change.id == Ctx.Controller?.SelectedId;
            OverlayDraw.Handle(g, hot ? 9f : 6f, TypeColor(change.type));
            if (hot) OverlayDraw.Readout(g + new Vector2(0f, -22f), change.ToString());
        }
    }

    public override void HandleInput()
    {
        if (!LeftClicked() || Ctx?.Cam == null || Ctx.Level == null) return;

        // The inverse of clicking a row: the markers are the list, drawn where the changes are.
        Vector2 mouse = Ctx.MousePosition;
        mouse.y = Screen.height - mouse.y;      // OverlayDraw works in GUI space, input in screen space

        float y = Ctx.Level.elevation;
        float best = MarkerHitRadius;
        bool found = false;
        VariantDiff.Change hit = default;

        foreach (var change in _changes)
        {
            if (!change.hasPos) continue;
            if (!OverlayDraw.ToScreen(Ctx.Cam, change.worldPos, y, out Vector2 g)) continue;
            float d = Vector2.Distance(g, mouse);
            if (d > best) continue;
            best = d;
            hit = change;
            found = true;
        }

        if (found) Focus(hit);
    }

    // ---------------------------------------------------------------------------------------

    private List<KeyValuePair<string, List<VariantDiff.Change>>> GroupByRoom(VariantDef to)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, List<VariantDiff.Change>>();

        // The change's OWN story, not levels[0]. Two floors of one dwelling share an XZ by
        // construction, so resolving every change against the ground floor files the upstairs
        // bathroom's widened door under the kitchen underneath it.
        bool manyStories = to.levels != null && to.levels.Count > 1;

        foreach (var change in _changes)
        {
            string key = RoomNameAt(LevelOf(to, change), change);
            // Only once there is more than one story: a heading saying "Ground floor" on every
            // single-story residence is a word added to every row for no information.
            if (manyStories && key != Elsewhere)
                key = StoryName(to, change) + " · " + key;
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<VariantDiff.Change>();
                groups[key] = list;
                order.Add(key);
            }
            list.Add(change);
        }

        // "Elsewhere" last wherever it appeared, so the rooms read in the order they were changed and
        // the catch-all does not interrupt them.
        order.Remove(Elsewhere);
        var outList = new List<KeyValuePair<string, List<VariantDiff.Change>>>();
        foreach (var key in order)
            outList.Add(new KeyValuePair<string, List<VariantDiff.Change>>(key, groups[key]));
        if (groups.TryGetValue(Elsewhere, out var rest))
            outList.Add(new KeyValuePair<string, List<VariantDiff.Change>>(Elsewhere, rest));
        return outList;
    }

    private const string Elsewhere = "Elsewhere";

    /// <summary>The story a change was reported from, by id then by index. Null for a change that
    /// belongs to no level at all: an occupant, or the outdoor layer.</summary>
    public static LevelDef LevelOf(VariantDef v, VariantDiff.Change change)
    {
        var levels = v?.levels;
        if (levels == null || levels.Count == 0) return null;

        if (!string.IsNullOrEmpty(change.levelId))
            foreach (var l in levels)
                if (l != null && l.id == change.levelId) return l;

        if (change.levelIndex >= 0 && change.levelIndex < levels.Count) return levels[change.levelIndex];
        // A change with no level at all is variant-wide, so it belongs to no story rather than to
        // the first one. Only a change recorded before Change carried a level falls back to [0].
        return change.levelIndex < 0 ? null : levels[0];
    }

    private static string StoryName(VariantDef v, VariantDiff.Change change)
    {
        var l = LevelOf(v, change);
        if (l == null) return "";
        return string.IsNullOrEmpty(l.name) ? "Floor " + (change.levelIndex + 1) : l.name;
    }

    /// <summary>
    /// Which room a change happens in. Shared with the report, which sections by exactly this.
    /// </summary>
    public static string RoomNameAt(LevelDef level, VariantDiff.Change change)
    {
        if (!change.hasPos || level?.rooms == null) return Elsewhere;

        foreach (var room in level.rooms)
        {
            if (room == null) continue;
            var poly = PolygonTriangulator.ToVector2(room.polygon);
            if (poly == null || poly.Count < 3) continue;
            if (!ResidenceMetrics.PointInPolygon(change.worldPos, poly)) continue;
            return string.IsNullOrEmpty(room.name) ? UITheme.PrettyId(room.roomType) : room.name;
        }
        return Elsewhere;
    }

    private static int IndexOf(List<VariantDef> variants, string id)
    {
        if (variants == null || string.IsNullOrEmpty(id)) return -1;
        for (int i = 0; i < variants.Count; i++)
            if (variants[i] != null && variants[i].id == id) return i;
        return -1;
    }

    private static string Prefix(VariantDiff.ChangeType t) => t switch
    {
        VariantDiff.ChangeType.Added => "Added",
        VariantDiff.ChangeType.Removed => "Removed",
        _ => "Changed",
    };

    private static Color TypeColor(VariantDiff.ChangeType t) => t switch
    {
        VariantDiff.ChangeType.Added => UITheme.Ok,
        VariantDiff.ChangeType.Removed => UITheme.Danger,
        _ => UITheme.Accent,
    };

    // VariantDiff.ElementKind and ResidenceElementMarker.Kind are parallel by construction: the diff
    // reports on exactly the things the renderer marks.
    private static ResidenceElementMarker.Kind MarkerKind(VariantDiff.ElementKind kind) => kind switch
    {
        VariantDiff.ElementKind.Wall => ResidenceElementMarker.Kind.Wall,
        VariantDiff.ElementKind.Opening => ResidenceElementMarker.Kind.Opening,
        VariantDiff.ElementKind.Room => ResidenceElementMarker.Kind.Room,
        VariantDiff.ElementKind.Furniture => ResidenceElementMarker.Kind.Furniture,
        VariantDiff.ElementKind.WallMount => ResidenceElementMarker.Kind.WallMount,
        _ => ResidenceElementMarker.Kind.Occupant,
    };
}
