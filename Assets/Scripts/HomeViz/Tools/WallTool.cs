using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Draws wall centerlines by clicking a chain of points. The main authoring tool: with AI layout
// parsing out of scope, tracing a calibrated sketch is how every plan gets into the model.
//
// Two things make the difference between this and a polyline widget:
//
//   * The LIVE READOUT. Length and angle follow the cursor, so you draw TO a dimension rather than
//     eyeballing it and correcting later. Copied in spirit from the straight-run readout in
//     EditController's surface brush.
//   * TYPED LENGTH. Start a segment, type "12' 6", press Enter, and the endpoint lands exactly there.
//     A traced sketch is approximate; a measured wall is not, and the tool has to accept both.
//
// Shift suspends all snapping: the convention CLAUDE.md already documents for the fence tool.
public class WallTool : HomeToolBase
{
    public override string Id => "wall";
    public override string DisplayName => "Walls";

    public override string Hint =>
        "Click to place corners. Enter finishes the run, Esc cancels it. Type a length mid-run to "
        + "place a corner exactly. Walls divide each other where they cross; hold Shift to draw "
        + "free, with no snapping and no dividing.";

    private readonly List<Vector2> _chain = new List<Vector2>();

    // Only mid-run. A click that starts a run may just as well select the chair it landed on, but once
    // corners are down, every click belongs to the run.
    public override bool ClaimsClicks => _chain.Count > 0;
    private Vector2 _cursor;
    private WallSnapping.Result _snap;
    private bool _hasCursor;

    private string _typed = "";
    private float _thickness;
    private float _height;
    private WallSnapping.Options _opts = WallSnapping.Options.Default;

    // Whether this click will divide the walls it crosses. Mirrors _opts.enabled: Shift suspends
    // snapping AND linking together, so "draw free" means one thing rather than two.
    private bool _link = true;
    private readonly List<string> _warnings = new List<string>();
    private WallLinker.Plan _plan;

    public override void Enter(HomeToolContext ctx)
    {
        base.Enter(ctx);
        _chain.Clear();
        _typed = "";
        _thickness = ctx?.Level?.wallThickness ?? HomeConventions.DEFAULT_WALL_THICKNESS;
        _height = ctx?.Level?.ceilingHeight ?? HomeConventions.DEFAULT_CEILING_HEIGHT;
    }

    public override void Exit() => _chain.Clear();

    public override void HandleInput()
    {
        if (Ctx == null || Ctx.Level == null || Ctx.IsLocked) return;

        _hasCursor = Ctx.GroundPoint(out Vector2 raw);
        if (_hasCursor)
        {
            _link = !Ctx.ShiftHeld;
            _opts.enabled = _link;           // Shift = draw free: no snapping, no linking
            Vector2? anchor = _chain.Count > 0 ? _chain[_chain.Count - 1] : (Vector2?)null;
            _snap = WallSnapping.Snap(raw, Ctx.Level, anchor, _opts);
            _cursor = _snap.point;

            // Ask the linker what this click would do, so the ghost shows the divisions before they
            // happen: the same trick the fence tool plays with FenceLinker.FindCuts.
            _plan = _link && _chain.Count > 0
                ? WallLinker.Preview(Ctx.Level, new[] { _chain[_chain.Count - 1], _cursor },
                                     WallLinker.Options.Default)
                : default;
        }

        if (LeftClicked() && _hasCursor) AddPoint(_cursor);

        // Enter commits a typed length when one is being entered, otherwise finishes the run.
        if (KeyDown(Key.Enter) || KeyDown(Key.NumpadEnter))
        {
            if (!string.IsNullOrEmpty(_typed)) CommitTypedLength();
            else Finish();
        }

        if (KeyDown(Key.Escape)) { _chain.Clear(); _typed = ""; }

        if (KeyDown(Key.Backspace))
        {
            if (_typed.Length > 0) _typed = _typed.Substring(0, _typed.Length - 1);
            else if (_chain.Count > 0) _chain.RemoveAt(_chain.Count - 1);
        }

        CaptureTypedDigits();
    }

    // Digits, quotes, feet marks and a decimal point feed the length box while drawing, so you never
    // have to move the mouse to a field mid-run.
    private void CaptureTypedDigits()
    {
        var kb = Keyboard.current;
        // Not while a rail field has focus: the digits belong to the field, not to the run.
        if (kb == null || _chain.Count == 0 || HomeEditController.TypingInUI) return;

        for (Key k = Key.Digit1; k <= Key.Digit0; k++)
            if (kb[k].wasPressedThisFrame) _typed += DigitOf(k);

        if (kb.periodKey.wasPressedThisFrame) _typed += ".";
        if (kb.quoteKey.wasPressedThisFrame) _typed += "'";
        if (kb.spaceKey.wasPressedThisFrame && _typed.Length > 0) _typed += " ";
        if (kb.slashKey.wasPressedThisFrame) _typed += "/";
    }

    private static string DigitOf(Key k) => k == Key.Digit0 ? "0" : ((int)(k - Key.Digit1) + 1).ToString();

    private void CommitTypedLength()
    {
        if (_chain.Count == 0) return;
        if (!Units.TryParse(_typed, Units.BareUnit.FollowDisplay, out float meters) || meters <= 0f)
        {
            _typed = "";
            return;
        }

        Vector2 from = _chain[_chain.Count - 1];
        Vector2 dir = (_cursor - from);
        if (dir.sqrMagnitude < 1e-6f) { _typed = ""; return; }

        AddPoint(from + dir.normalized * meters);
        _typed = "";
    }

    private void AddPoint(Vector2 p)
    {
        if (_chain.Count > 0 && (p - _chain[_chain.Count - 1]).sqrMagnitude < 1e-4f) return;

        _chain.Add(p);
        if (_chain.Count < 2) return;

        // Commit each segment as it is completed rather than at the end of the run, so a long trace
        // is never lost to a mis-click and every segment is separately undoable.
        CommitSegment(_chain[_chain.Count - 2], _chain[_chain.Count - 1]);
    }

    private void CommitSegment(Vector2 a, Vector2 b)
    {
        if ((b - a).magnitude < 0.02f) return;

        var template = new WallDef
        {
            thickness = _thickness,
            height = _height,
            materialLeft = "paint_white",
            materialRight = "paint_white",
        };

        Ctx.RecordEdit("Draw wall");

        _warnings.Clear();
        int added = 0;

        if (_link)
        {
            // One RecordEdit covers the whole mutation: this segment AND the splits it causes in the
            // walls it crosses. Warnings are the OpeningFit convention. Written to be read verbatim.
            added = WallLinker.Link(Ctx.Level, new[] { a, b }, template,
                                    WallLinker.Options.Default, _warnings).Count;
        }
        else
        {
            // Shift = draw free: no snapping, no linking. The fence tool's convention, and the escape
            // hatch for the case where the rules are getting in the way.
            template.id = Guid.NewGuid().ToString();
            Segments.SetEnds(template, a, b);
            Ctx.Level.walls.Add(template);
            added = 1;
        }

        // An enclosed area is a room, so the rooms follow the walls in the SAME undo step: a segment
        // that closes a shape has made a room, and pressing undo once must take both back.
        //
        // This runs on the free-draw path too: Shift means no snapping and no dividing, not no rooms.
        // (What it does mean is that two walls crossing without sharing a vertex stay uncrossed, so
        // they enclose nothing, which is exactly what the wall mesh draws there.)
        RoomRegions.Sync(Ctx.Level, _warnings);

        // Sync's warnings go on the end, so the first thing shown is still the one about the gesture
        // the user just made rather than a consequence of it.
        if (_warnings.Count > 0) Ctx.Controller?.Status(_warnings[0]);
        else if (added == 0) Ctx.Controller?.Status("A wall already runs along that line.");

        Ctx.Changed();
    }

    private void Finish()
    {
        _chain.Clear();
        _typed = "";
    }

    // ---------------------------------------------------------------------------------------

    public override void DrawRail()
    {
        if (RefuseIfLocked()) return;

        _thickness = MeasureUI.Length("Thickness", "Wall thickness", _thickness, 0.012f,
                                      HomeConventions.MIN_WALL_THICKNESS, HomeConventions.MAX_WALL_THICKNESS);
        _height = MeasureUI.Length("Height", "Wall height", _height, 0.05f,
                                   HomeConventions.MIN_WALL_HEIGHT, HomeConventions.MAX_WALL_HEIGHT);

        UITheme.Gap();
        _opts.axisLock = UITheme.Toggle("Square to 45°", _opts.axisLock,
                                        "Hold each run to the nearest 45° axis");
        _opts.gridSize = MeasureUI.Length("Grid", "Grid the corners snap to", _opts.gridSize, 0.01f, 0f, 1f);

        if (_chain.Count > 0)
        {
            UITheme.Gap();
            UITheme.Value("Corners", _chain.Count.ToString(), "Corners in this run");
            if (!string.IsNullOrEmpty(_typed))
                UITheme.Value("Length", _typed, "Press Enter to place the corner at this length.");
            if (UITheme.SecondaryButton("Finish run")) Finish();
            UITheme.Tip("End the run here  (Enter)");
        }
    }

    public override void DrawOverlay()
    {
        if (Ctx?.Cam == null || Ctx.Level == null || !_hasCursor) return;

        float y = Ctx.Level.elevation;
        var snapColor = new Color(0.20f, 0.65f, 0.95f);
        var drawColor = new Color(1f, 0.72f, 0.20f);

        // Already-placed points in this run.
        for (int i = 0; i < _chain.Count; i++)
            if (OverlayDraw.ToScreen(Ctx.Cam, _chain[i], y, out Vector2 g))
                OverlayDraw.Dot(g, 8f, drawColor);

        if (!OverlayDraw.ToScreen(Ctx.Cam, _cursor, y, out Vector2 cursorGui)) return;

        // The rubber band, plus the live dimension. This readout is what lets someone draw a 12'6"
        // wall on purpose instead of drawing something and fixing it afterwards.
        if (_chain.Count > 0 &&
            OverlayDraw.ToScreen(Ctx.Cam, _chain[_chain.Count - 1], y, out Vector2 lastGui))
        {
            OverlayDraw.Line(lastGui, cursorGui, drawColor, 2.5f);

            Vector2 d = _cursor - _chain[_chain.Count - 1];
            float angle = Mathf.Repeat(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, 360f);
            string typed = string.IsNullOrEmpty(_typed) ? "" : "   ⌨ " + _typed;

            // Where this run will divide the walls it crosses. Drawn as hollow rings rather than
            // filled dots so they read as "a junction will be made here", distinct from the solid
            // dots marking corners already placed.
            int cuts = _plan.junctions?.Count ?? 0;
            for (int i = 0; i < cuts; i++)
                if (OverlayDraw.ToScreen(Ctx.Cam, _plan.junctions[i], y, out Vector2 jg))
                {
                    OverlayDraw.Circle(jg, 7f, snapColor, 16, 2f);
                    OverlayDraw.Dot(jg, 3f, snapColor);
                }

            string note = cuts > 0 ? $"   ✂ {cuts}" :
                          _plan.duplicatesExisting ? "   • already walled" : "";
            OverlayDraw.Readout(cursorGui, $"{Units.Format(d.magnitude)}   {angle:0}°{typed}{note}");
        }
        else
        {
            OverlayDraw.Readout(cursorGui, _snap.kind == WallSnapping.SnapKind.None
                ? "Click to start a wall"
                : "Start: " + _snap.label);
        }

        OverlayDraw.Dot(cursorGui, 10f, snapColor);

        // Name the snap that is in effect, so a corner that welded is visibly distinct from one that
        // merely landed nearby.
        if (_snap.kind == WallSnapping.SnapKind.Endpoint || _snap.kind == WallSnapping.SnapKind.OnWall
            || _snap.kind == WallSnapping.SnapKind.AxisOnWall)
            OverlayDraw.Circle(cursorGui, 12f, snapColor, 20, 2f);

        // The alignment guide: a dashed line back to the endpoint the run is level with, so the
        // snap shows WHY the cursor stopped here.
        if (_snap.kind == WallSnapping.SnapKind.Align && _snap.hasGuide &&
            OverlayDraw.ToScreen(Ctx.Cam, _snap.guideFrom, y, out Vector2 guideGui))
        {
            OverlayDraw.DashedLine(guideGui, cursorGui, snapColor, 1.5f);
            OverlayDraw.Dot(guideGui, 5f, snapColor);
        }
    }
}
