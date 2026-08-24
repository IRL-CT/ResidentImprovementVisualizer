using System;
using UnityEngine;

// Places doors, windows and pass-throughs into walls.
//
// EVERY DIMENSION IS A FREE NUMBER. Width used to be five inch presets (28/30/32/34/36) on the
// grounds that this is how doors are specified and how the questions are asked ("is that a 32 or a
// 36?"). But a measured width field was added later and sat directly beneath them, so the rail
// offered one dimension twice in two idioms, and the chips could not express the openings real
// buildings actually have. The clear passage is still shown live as you drag, which is the part that
// earned the presets their place: picking 32" and being told the clear passage is 29 5/8" is the
// moment this tool justifies itself.
//
// Every placement and drag goes through OpeningFit, which slides the opening to the nearest legal
// position rather than refusing. Dragging a door toward a corner should stop against the corner, not
// vanish or snap to the far end.
public class OpeningTool : ResidenceToolBase
{
    public override string Id => "opening";
    public override string DisplayName => "Openings";

    public override string Hint =>
        "Hover a wall and click to place. The preview turns red when it will not fit. Drag or type any "
        + "width, height and sill. The clear passage is shown live as you choose.";

    // Always: this tool's whole gesture is clicking a wall, which is exactly what the auto-select
    // would otherwise interpret.
    public override bool ClaimsClicks => true;

    private string _kind = OpeningKind.Door;
    private float _width = ResidenceConventions.DEFAULT_DOOR_WIDTH;
    private float _height = ResidenceConventions.DEFAULT_DOOR_HEIGHT;
    private float _sill;
    private float _threshold;

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
        string id = Guid.NewGuid().ToString();

        Ctx.RecordEdit("Add " + _kind);
        Ctx.Level.openings.Add(new OpeningDef
        {
            id = id,
            wallId = _hoverWall.id,
            offset = _fit.offset,
            width = _width,
            height = _height,
            sillHeight = _kind == OpeningKind.Window ? _sill : 0f,
            kind = _kind,
            thresholdHeight = _kind == OpeningKind.Door ? _threshold : 0f,
            clearWidth = 0f,
        });

        // Placing selects what you placed, the way the furniture tool does. It matters more here:
        // an opening is no longer reachable by clicking it in the plan, so without this the thing
        // just drawn could only be got at by selecting its wall and finding it in the list.
        // reveal:false so a run of doors is not interrupted by a jump to the Select tab.
        Ctx.Controller.Select(ResidenceElementMarker.Kind.Opening, id, reveal: false);
        Ctx.Changed();
    }

    public override void DrawRail()
    {
        if (RefuseIfLocked()) return;

        var kinds = UITheme.ChipRow();
        // "Add", not "Type": the lit chip is what the next click puts in the wall, and the row should
        // say so: "Type" described the chips, "Add" says what they do.
        kinds.Label("Add");
        if (kinds.Chip("Door", _kind == OpeningKind.Door)) SetKind(OpeningKind.Door);
        UITheme.Tip("A doorway with a door in it");
        if (kinds.Chip("Window", _kind == OpeningKind.Window)) SetKind(OpeningKind.Window);
        UITheme.Tip("A window, set above a sill");
        if (kinds.Chip("Cased opening", _kind == OpeningKind.CasedOpening)) SetKind(OpeningKind.CasedOpening);
        UITheme.Tip("A cased opening: a hole in the wall with no door in it");
        kinds.End();

        UITheme.Gap();

        // Deliberately NOT bounded by OpeningFit.MaxWidth the way the inspector's field is. The
        // hovered wall changes every frame, so a max derived from it would move under a drag that is
        // already in progress. The refusal here is honest without it: _fit turns the preview red and
        // the glyph at the foot of this rail says why, both live.
        _width = MeasureUI.Length("Width", "The rough opening. Drag it, or type a measured width.",
                                  _width, 0.0127f,
                                  ResidenceConventions.MIN_OPENING_WIDTH, ResidenceConventions.MAX_OPENING_WIDTH);

        // Show what the choice actually yields once a door leaf is in the way. Picking "32" and being
        // told the clear passage is 29 5/8" is the moment the tool earns its keep. Doors only: a
        // window or cased opening has no leaf, so its clear passage IS the width above and the
        // readout was restating the field.
        if (_kind == OpeningKind.Door)
        {
            var probe = new OpeningDef { width = _width, kind = _kind };
            UITheme.Value("Clear passage", Units.Format(ResidenceMetrics.ClearWidth(probe)),
                "The clear passage this leaves with the door open, which is what a wheelchair goes "
                + "through, and always less than the rough opening above.");
        }

        UITheme.Gap();
        _height = MeasureUI.Length("Height", "Opening height", _height, 0.025f,
                                   ResidenceConventions.MIN_OPENING_HEIGHT, ResidenceConventions.MAX_OPENING_HEIGHT);

        if (_kind == OpeningKind.Window)
            _sill = MeasureUI.Length("Sill", "Sill height above the floor", _sill, 0.025f,
                                     0f, ResidenceConventions.MAX_WINDOW_SILL);

        if (_kind == OpeningKind.Door)
        {
            UITheme.Gap();
            _threshold = MeasureUI.Length("Threshold", "Threshold height to cross", _threshold, 0.003f,
                                          0f, ResidenceConventions.MAX_THRESHOLD);
            UITheme.StatusBadge(_threshold > 0f ? "Has a threshold" : "Step-free", _threshold <= 0f);
            UITheme.Tip(_threshold > 0f
                ? "There is a raised lip to cross at this doorway"
                : "Nothing to cross at this doorway");
        }

        // The fit refusal stays VISIBLE, as a glyph. A warning behind a hover is a warning nobody
        // reads, and this one is the difference between placing a door and thinking you placed one.
        if (_hoverWall != null && !_fit.ok) UITheme.Glyph("⚠", _fit.reason, UITheme.Danger);
    }

    private void SetKind(string kind)
    {
        _kind = kind;
        if (kind == OpeningKind.Window)
        {
            _width = ResidenceConventions.DEFAULT_WINDOW_WIDTH;
            _height = ResidenceConventions.DEFAULT_WINDOW_HEIGHT;
            _sill = ResidenceConventions.DEFAULT_WINDOW_SILL;
        }
        else
        {
            _width = ResidenceConventions.DEFAULT_DOOR_WIDTH;
            _height = ResidenceConventions.DEFAULT_DOOR_HEIGHT;
            _sill = 0f;
        }
    }

    public override void DrawOverlay()
    {
        if (Ctx?.Cam == null || _hoverWall == null || Ctx.Level == null) return;

        float y = Ctx.Level.elevation;
        float offset = _fit.ok ? _fit.offset : _hoverOffset;

        Vector2 center = ResidenceMetrics.PointOnWall(_hoverWall, offset);
        Vector2 a = ResidenceMetrics.PointOnWall(_hoverWall, offset - 0.5f * _width);
        Vector2 b = ResidenceMetrics.PointOnWall(_hoverWall, offset + 0.5f * _width);

        Color color = _fit.ok ? new Color(0.20f, 0.75f, 0.45f) : new Color(0.90f, 0.30f, 0.30f);

        if (OverlayDraw.ToScreen(Ctx.Cam, a, y, out Vector2 ga) &&
            OverlayDraw.ToScreen(Ctx.Cam, b, y, out Vector2 gb))
            OverlayDraw.Line(ga, gb, color, 6f);

        if (OverlayDraw.ToScreen(Ctx.Cam, center, y, out Vector2 gc))
        {
            // The clear-passage figure rides along for doors only: anything without a leaf has a
            // clear passage equal to the width already shown.
            string clear = _kind == OpeningKind.Door
                ? $" · clear {Units.Format(ResidenceMetrics.ClearWidth(new OpeningDef { width = _width, kind = _kind }))}"
                : "";
            OverlayDraw.Readout(gc, _fit.ok ? $"{Units.Format(_width)}{clear}" : _fit.reason);
        }
    }
}
