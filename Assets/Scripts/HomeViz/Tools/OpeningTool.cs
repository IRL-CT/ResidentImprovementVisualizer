using System;
using UnityEngine;

// Places doors, windows and pass-throughs into walls.
//
// Widths are offered as inch presets because that is how doors are specified and how the questions
// are asked — "is that a 32 or a 36?" A 32" door is the single most common accessibility problem in
// an existing home, so the rail shows the resulting CLEAR width live while you choose.
//
// Every placement and drag goes through OpeningFit, which slides the opening to the nearest legal
// position rather than refusing. Dragging a door toward a corner should stop against the corner, not
// vanish or snap to the far end.
public class OpeningTool : HomeToolBase
{
    public override string Id => "opening";
    public override string DisplayName => "Doors & windows";

    private string _kind = OpeningKind.Door;
    private float _width = HomeConventions.DEFAULT_DOOR_WIDTH;
    private float _height = HomeConventions.DEFAULT_DOOR_HEIGHT;
    private float _sill;
    private float _threshold;
    private string _swing = OpeningSwing.LeftIn;

    private WallDef _hoverWall;
    private float _hoverOffset;
    private OpeningFit.Result _fit;

    public override void HandleInput()
    {
        if (Ctx?.Level == null || Ctx.IsLocked) return;

        _hoverWall = null;
        if (!Ctx.GroundPoint(out Vector2 p)) return;

        // Nearest wall centerline to the cursor, within reach.
        float best = 0.9f * 0.9f;
        foreach (var w in Ctx.Level.walls)
        {
            if (w?.a == null || w.b == null) continue;

            var a = new Vector2(w.a[0], w.a[1]);
            var b = new Vector2(w.b[0], w.b[1]);
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            if (lenSq <= 1e-6f) continue;

            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
            Vector2 foot = a + ab * t;
            float d = (p - foot).sqrMagnitude;
            if (d >= best) continue;

            best = d;
            _hoverWall = w;
            _hoverOffset = t * Mathf.Sqrt(lenSq);
        }

        if (_hoverWall == null) return;

        _fit = OpeningFit.Fit(_hoverOffset, _width, WallLayout.WallLength(_hoverWall),
                              WallLayout.OpeningsFor(_hoverWall, Ctx.Level));

        if (LeftClicked() && _fit.ok) Place();
    }

    private void Place()
    {
        Ctx.RecordEdit("Add " + _kind);
        Ctx.Level.openings.Add(new OpeningDef
        {
            id = Guid.NewGuid().ToString(),
            wallId = _hoverWall.id,
            offset = _fit.offset,
            width = _width,
            height = _height,
            sillHeight = _kind == OpeningKind.Window ? _sill : 0f,
            kind = _kind,
            swing = _kind == OpeningKind.Door ? _swing : OpeningSwing.None,
            thresholdHeight = _kind == OpeningKind.Door ? _threshold : 0f,
            clearWidth = 0f,
        });
        Ctx.Changed();
    }

    public override void DrawRail()
    {
        if (RefuseIfLocked()) return;

        UITheme.Note("Hover a wall and click to place. The preview turns red when it will not fit.");
        GUILayout.Space(6);

        GUILayout.BeginHorizontal();
        if (UITheme.Chip("Door", _kind == OpeningKind.Door)) SetKind(OpeningKind.Door);
        if (UITheme.Chip("Window", _kind == OpeningKind.Window)) SetKind(OpeningKind.Window);
        if (UITheme.Chip("Opening", _kind == OpeningKind.CasedOpening)) SetKind(OpeningKind.CasedOpening);
        GUILayout.EndHorizontal();

        GUILayout.Space(8);
        UITheme.Header("Width");
        GUILayout.BeginHorizontal();
        foreach (int inches in new[] { 28, 30, 32, 34, 36 })
        {
            float m = inches * HomeConventions.IN_TO_M;
            if (UITheme.Chip(inches + "\"", Mathf.Abs(_width - m) < 0.005f)) _width = m;
        }
        GUILayout.EndHorizontal();

        // Show what the choice actually yields once a door leaf is in the way. Picking "32" and being
        // told the clear passage is 29 5/8" is the moment the tool earns its keep.
        var probe = new OpeningDef { width = _width, kind = _kind, swing = _swing };
        UITheme.Num(Units.Format(HomeMetrics.ClearWidth(probe)));
        UITheme.Note("Clear passage with the door open");

        GUILayout.Space(8);
        _height = UITheme.Stepper("Height", _height, 0.025f, "0.00", " m");
        UITheme.Note("  = " + Units.Format(_height));

        if (_kind == OpeningKind.Window)
        {
            _sill = UITheme.Stepper("Sill", _sill, 0.025f, "0.00", " m");
            UITheme.Note("  = " + Units.Format(_sill));
        }

        if (_kind == OpeningKind.Door)
        {
            GUILayout.Space(8);
            UITheme.Header("Swing");
            GUILayout.BeginHorizontal();
            if (UITheme.Chip("L in", _swing == OpeningSwing.LeftIn)) _swing = OpeningSwing.LeftIn;
            if (UITheme.Chip("R in", _swing == OpeningSwing.RightIn)) _swing = OpeningSwing.RightIn;
            if (UITheme.Chip("Slide", _swing == OpeningSwing.Slider)) _swing = OpeningSwing.Slider;
            if (UITheme.Chip("Pocket", _swing == OpeningSwing.Pocket)) _swing = OpeningSwing.Pocket;
            GUILayout.EndHorizontal();

            _threshold = UITheme.Stepper("Threshold", _threshold, 0.003f, "0.000", " m");
            UITheme.StatusBadge(_threshold > 0f ? "Has a threshold" : "Step-free", _threshold <= 0f);
        }

        if (_hoverWall != null && !_fit.ok) UITheme.Note("⚠ " + _fit.reason);
    }

    private void SetKind(string kind)
    {
        _kind = kind;
        if (kind == OpeningKind.Window)
        {
            _width = HomeConventions.DEFAULT_WINDOW_WIDTH;
            _height = HomeConventions.DEFAULT_WINDOW_HEIGHT;
            _sill = HomeConventions.DEFAULT_WINDOW_SILL;
        }
        else
        {
            _width = HomeConventions.DEFAULT_DOOR_WIDTH;
            _height = HomeConventions.DEFAULT_DOOR_HEIGHT;
            _sill = 0f;
        }
    }

    public override void DrawOverlay()
    {
        if (Ctx?.Cam == null || _hoverWall == null || Ctx.Level == null) return;

        float y = Ctx.Level.elevation;
        float offset = _fit.ok ? _fit.offset : _hoverOffset;

        Vector2 centre = HomeMetrics.PointOnWall(_hoverWall, offset);
        Vector2 a = HomeMetrics.PointOnWall(_hoverWall, offset - 0.5f * _width);
        Vector2 b = HomeMetrics.PointOnWall(_hoverWall, offset + 0.5f * _width);

        Color color = _fit.ok ? new Color(0.20f, 0.75f, 0.45f) : new Color(0.90f, 0.30f, 0.30f);

        if (OverlayDraw.ToScreen(Ctx.Cam, a, y, out Vector2 ga) &&
            OverlayDraw.ToScreen(Ctx.Cam, b, y, out Vector2 gb))
            OverlayDraw.Line(ga, gb, color, 6f);

        if (OverlayDraw.ToScreen(Ctx.Cam, centre, y, out Vector2 gc))
            OverlayDraw.Readout(gc, _fit.ok
                ? $"{Units.Format(_width)} · clear {Units.Format(HomeMetrics.ClearWidth(new OpeningDef { width = _width, kind = _kind, swing = _swing }))}"
                : _fit.reason);
    }
}
