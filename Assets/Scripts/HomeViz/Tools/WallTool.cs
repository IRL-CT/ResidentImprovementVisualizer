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
// Shift suspends all snapping — the convention CLAUDE.md already documents for the fence tool.
public class WallTool : HomeToolBase
{
    public override string Id => "wall";
    public override string DisplayName => "Walls";

    private readonly List<Vector2> _chain = new List<Vector2>();
    private Vector2 _cursor;
    private WallSnapping.Result _snap;
    private bool _hasCursor;

    private string _typed = "";
    private float _thickness;
    private float _height;
    private bool _structural;
    private WallSnapping.Options _opts = WallSnapping.Options.Default;

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
            _opts.enabled = !Ctx.ShiftHeld;   // Shift = draw free
            Vector2? anchor = _chain.Count > 0 ? _chain[_chain.Count - 1] : (Vector2?)null;
            _snap = WallSnapping.Snap(raw, Ctx.Level, anchor, _opts);
            _cursor = _snap.point;
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
        if (kb == null || _chain.Count == 0) return;

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
        if (!Units.TryParse(_typed, Units.BareUnit.Feet, out float meters) || meters <= 0f)
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

        Ctx.RecordEdit("Draw wall");
        Ctx.Level.walls.Add(new WallDef
        {
            id = Guid.NewGuid().ToString(),
            a = new[] { a.x, a.y },
            b = new[] { b.x, b.y },
            thickness = _thickness,
            height = _height,
            materialLeft = "paint_white",
            materialRight = "paint_white",
            structural = _structural,
        });
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

        UITheme.Note("Click to place corners. Enter finishes the run, Esc cancels it. Hold Shift to draw without snapping.");
        GUILayout.Space(6);

        _thickness = UITheme.Stepper("Thickness", _thickness, 0.012f, "0.000", " m");
        UITheme.Note("  = " + Units.Format(_thickness));

        _height = UITheme.Stepper("Height", _height, 0.05f, "0.00", " m");
        UITheme.Note("  = " + Units.Format(_height));

        _structural = GUILayout.Toggle(_structural, "  Structural (load-bearing)");
        if (_structural)
            UITheme.Note("Flagged in the inspector so a proposal that moves it is visibly an engineering question.");

        GUILayout.Space(8);
        UITheme.Header("Snapping");
        _opts.axisLock = GUILayout.Toggle(_opts.axisLock, "  Square to 45°");
        _opts.gridSize = UITheme.Stepper("Grid", _opts.gridSize, 0.01f, "0.00", " m");

        if (_chain.Count > 0)
        {
            GUILayout.Space(8);
            UITheme.Header("Drawing");
            UITheme.Note($"{_chain.Count} point{(_chain.Count == 1 ? "" : "s")} placed.");
            if (!string.IsNullOrEmpty(_typed)) UITheme.Note("Length: " + _typed + "  (Enter to place)");
            if (UITheme.SecondaryButton("Finish run")) Finish();
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
            OverlayDraw.Readout(cursorGui, $"{Units.Format(d.magnitude)}   {angle:0}°{typed}");
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
        if (_snap.kind == WallSnapping.SnapKind.Endpoint || _snap.kind == WallSnapping.SnapKind.OnWall)
            OverlayDraw.Circle(cursorGui, 12f, snapColor, 20, 2f);
    }
}
