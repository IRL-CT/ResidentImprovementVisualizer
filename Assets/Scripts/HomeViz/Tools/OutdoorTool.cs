using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// The one outdoor tool. Reachable only from the Outdoors stage, which only exists once a home has
// switched its exterior layer on, so a tool for the inside of an apartment shows nothing about site
// work unless someone asks for it.
//
// It writes the Site types verbatim: a walkway or ramp is a PathDef, a railing is a FenceDef.
// That is the whole reason the exterior costs so little here. WorldRenderer already draws both, and
// ExteriorBridge already translates a variant's SiteDef into what it expects. Until this file existed
// nothing in HomeViz could author either, so ExteriorBridge.HasContent was permanently false and the
// "Show outdoor additions" toggle could never do anything.
//
// The drawing gesture is WallTool's, not EditController's: click a chain of points, Enter to finish,
// Esc to cancel, Shift to suspend snapping. Snapping runs through WallSnapping against the interior
// level, which is what makes a ramp land exactly on the corner of the wall it serves.
public class OutdoorTool : HomeToolBase
{
    public override string Id => "outdoor";
    public override string DisplayName => "Outdoors";

    public override string Hint =>
        "Draw walkways, entry ramps and railings around the home. Click to place points; Enter finishes "
        + "the run, Esc cancels it, Shift draws without snapping.";

    private enum Kind { Walkway, Ramp, Railing }
    private static readonly string[] KindLabels = { "Walkway", "Entry ramp", "Railing" };

    private static readonly string[] KindTips =
    {
        "A path across the site: to the door, round the side, out to the garden.",
        "A ramp serving a step or threshold. The rise you enter is checked live against the 1:12 "
            + "maximum slope as you draw.",
        "A handrail or guardrail along a run, drawn the same way.",
    };

    // The slope a ramp is expected to hold: 1 unit of rise per 12 of run. Drawn live while you place
    // points, because "is this ramp long enough for that step?" is the question the tool exists to
    // answer, and it is far cheaper to answer before the run is committed than after.
    private const float RAMP_MAX_SLOPE = 1f / 12f;

    private Kind _kind = Kind.Walkway;

    private readonly List<Vector2> _chain = new List<Vector2>();

    // Always: every click here places a point on a walkway, ramp or railing.
    public override bool ClaimsClicks => true;
    private Vector2 _cursor;
    private bool _hasCursor;
    private WallSnapping.Result _snap;
    private WallSnapping.Options _opts = WallSnapping.Options.Default;

    // Settings are per-kind and remembered separately, so switching to a railing and back does not
    // lose the walkway width you just set.
    private string _pathMaterial = "pavement_light";
    private float _walkwayWidth = 1.22f;   // 48": a wheelchair plus a passing space
    private float _rampWidth = 0.91f;      // 36" clear
    private float _rampRise = 0.15f;       // the step or threshold the ramp has to overcome
    private string _fenceType = "picket";
    private float _railHeight = 0.91f;     // 36". Graspable guard height

    private bool _showRuns;

    // ---------------------------------------------------------------------------------------

    public override void Enter(HomeToolContext ctx)
    {
        base.Enter(ctx);
        _chain.Clear();
    }

    public override void Exit() => _chain.Clear();

    private float ActiveWidth => _kind == Kind.Ramp ? _rampWidth : _walkwayWidth;

    // Read-only view of the variant's outdoor layer. Never creates it: that happens on the first
    // commit, so merely opening this tool cannot make VariantDiff report "added an exterior".
    private SiteDef SiteOrNull => Ctx?.Variant?.exterior;

    private SiteDef EnsureSite()
    {
        var v = Ctx?.Variant;
        if (v == null) return null;
        if (v.exterior == null) v.exterior = ExteriorBridge.NewExterior();
        return v.exterior;
    }

    // ---------------------------------------------------------------------------------------

    public override void HandleInput()
    {
        if (Ctx == null || Ctx.Variant == null || Ctx.IsLocked) return;

        _hasCursor = Ctx.GroundPoint(out Vector2 raw);
        if (_hasCursor)
        {
            _opts.enabled = !Ctx.ShiftHeld;   // Shift = draw free, the documented convention
            Vector2? anchor = _chain.Count > 0 ? _chain[_chain.Count - 1] : (Vector2?)null;
            _snap = WallSnapping.Snap(raw, Ctx.Level, anchor, _opts);
            _cursor = _snap.point;
        }

        if (LeftClicked() && _hasCursor) AddPoint(_cursor);

        if (KeyDown(Key.Enter) || KeyDown(Key.NumpadEnter)) Commit();
        if (KeyDown(Key.Escape)) _chain.Clear();
        if (KeyDown(Key.Backspace) && _chain.Count > 0) _chain.RemoveAt(_chain.Count - 1);
    }

    private void AddPoint(Vector2 p)
    {
        if (_chain.Count > 0 && (p - _chain[_chain.Count - 1]).sqrMagnitude < 1e-4f) return;
        _chain.Add(p);
    }

    // Unlike a wall run, an outdoor run is committed whole rather than segment by segment: a path and
    // a fence are each ONE record with a point list, so there is no partial object to salvage.
    private void Commit()
    {
        if (_chain.Count < 2) { _chain.Clear(); return; }

        var site = EnsureSite();
        if (site == null) { _chain.Clear(); return; }

        Ctx.RecordEdit(_kind == Kind.Railing ? "Draw railing"
                     : _kind == Kind.Ramp    ? "Draw entry ramp"
                                             : "Draw walkway");

        var pts = new float[_chain.Count][];
        for (int i = 0; i < _chain.Count; i++) pts[i] = new[] { _chain[i].x, _chain[i].y };

        if (_kind == Kind.Railing)
        {
            site.fences ??= new List<FenceDef>();
            site.fences.Add(new FenceDef
            {
                id = Guid.NewGuid().ToString("D"),
                fenceType = _fenceType,
                points = pts,
                smoothing = 0f,
                height = _railHeight,
            });
        }
        else
        {
            site.paths ??= new List<PathDef>();
            site.paths.Add(new PathDef
            {
                id = Guid.NewGuid().ToString("D"),
                material = _pathMaterial,
                width = ActiveWidth,
                points = pts,
                // Crisp corners. A 12-foot entry walk is laid out, not landscaped, and the smoothing
                // that suits a park trail rounds off the very corner a ramp has to hit squarely.
                smoothing = 0f,
            });
        }

        _chain.Clear();
        Ctx.Changed();
    }

    private float ChainLength(bool includeCursor)
    {
        float total = 0f;
        for (int i = 1; i < _chain.Count; i++) total += (_chain[i] - _chain[i - 1]).magnitude;
        if (includeCursor && _hasCursor && _chain.Count > 0)
            total += (_cursor - _chain[_chain.Count - 1]).magnitude;
        return total;
    }

    // ---------------------------------------------------------------------------------------
    // Rail
    // ---------------------------------------------------------------------------------------

    public override void DrawRail()
    {
        if (RefuseIfLocked()) return;

        int sel = UITheme.Segmented("Draw", (int)_kind, KindLabels, KindTips);
        if (sel != (int)_kind) { _kind = (Kind)sel; _chain.Clear(); }

        UITheme.Gap();

        switch (_kind)
        {
            case Kind.Walkway: DrawWalkwayControls(); break;
            case Kind.Ramp:    DrawRampControls();    break;
            case Kind.Railing: DrawRailingControls(); break;
        }

        if (_chain.Count > 0)
        {
            UITheme.Gap();
            UITheme.Value("Points", _chain.Count.ToString(), "Points placed in this run");
            UITheme.Value("Length", Units.Format(ChainLength(false)), "Length of the run so far");
            if (UITheme.SecondaryButton("Finish run")) Commit();
            UITheme.Tip("End the run here  (Enter)");
        }

        UITheme.Gap();
        DrawRunLists();
    }

    private void DrawWalkwayControls()
    {
        DrawPathMaterialChips();
        // One printing of the width, not two. The stepper used to render raw metres and a Value line
        // directly beneath it rendered the same number in feet and inches.
        _walkwayWidth = MeasureUI.Length("Width", "How wide the walkway is", _walkwayWidth, 0.05f, 0.3f, 4f);

        // Stays visible as a glyph: this is the accessibility finding the tool exists to surface, and
        // one nobody would go hunting for on hover.
        if (_walkwayWidth < 0.91f)
            UITheme.Glyph("⚠", "Under 36 inches, which is tight for a wheelchair, and two people cannot pass.",
                          UITheme.Danger);
    }

    private void DrawRampControls()
    {
        DrawPathMaterialChips();
        _rampWidth = MeasureUI.Length("Width", "How wide the ramp is", _rampWidth, 0.05f, 0.3f, 4f);
        _rampRise = MeasureUI.Length("Rise", "The step or threshold this ramp has to climb", _rampRise, 0.01f, 0f, 1f);

        float needed = _rampRise / RAMP_MAX_SLOPE;
        UITheme.Value("Run needed", Units.Format(needed), "How much run that rise needs at the 1:12 maximum slope");

        float drawn = ChainLength(true);
        if (drawn > 0.01f)
        {
            float slope = _rampRise / Mathf.Max(drawn, 0.001f);
            bool ok = slope <= RAMP_MAX_SLOPE + 1e-4f;
            UITheme.StatusBadge(ok
                ? $"Drawn {Units.Format(drawn)}, 1:{1f / Mathf.Max(slope, 1e-4f):0} slope"
                : $"Drawn {Units.Format(drawn)}, 1:{1f / Mathf.Max(slope, 1e-4f):0}, steeper than 1:12", ok);
            UITheme.Tip("The slope check is guidance while you draw. What gets stored is a path of this "
                        + "width. The grade itself stays out of the model.");
        }
    }

    private void DrawRailingControls()
    {
        var palette = Ctx?.Renderer != null && Ctx.Renderer.World != null
            ? Ctx.Renderer.World.FencePalette : null;

        if (palette == null || palette.entries == null || palette.entries.Count == 0)
        {
            // A scene-wiring fault. Nobody using the app can fix it, and whoever can is reading the
            // console, so that is where it goes.
            Debug.LogWarning("OutdoorTool: no FencePalette entries are wired, so railings will not "
                           + "render. Add one per type (e.g. \"picket\") to the palette on the "
                           + "Exterior/WorldRenderer in HomeViz.unity.");
        }
        else
        {
            var kinds = UITheme.ChipRow();
            kinds.Label("Railing");
            foreach (var e in palette.entries)
            {
                if (e == null || string.IsNullOrEmpty(e.fenceType)) continue;
                if (kinds.Chip(UITheme.PrettyId(e.fenceType), _fenceType == e.fenceType)) _fenceType = e.fenceType;
                UITheme.Tip($"Draw the railing as {UITheme.PrettyId(e.fenceType).ToLowerInvariant()}");
            }
            kinds.End();
        }

        _railHeight = MeasureUI.Length("Height", "How high the railing stands", _railHeight, 0.025f, 0.3f, 1.5f);
    }

    private void DrawPathMaterialChips()
    {
        var palette = Ctx?.Renderer != null && Ctx.Renderer.World != null
            ? Ctx.Renderer.World.PathMaterialPalette : null;

        if (palette == null || palette.entries == null || palette.entries.Count == 0)
        {
            Debug.LogWarning("OutdoorTool: no PathMaterialPalette entries are wired, so paths will not "
                           + "render. Add one per surface (e.g. \"pavement_light\") to the palette on "
                           + "the Exterior/WorldRenderer in HomeViz.unity.");
            return;
        }

        var surfaces = UITheme.ChipRow();
        surfaces.Label("Surface");
        foreach (var e in palette.entries)
        {
            if (e == null || string.IsNullOrEmpty(e.id)) continue;
            if (surfaces.Chip(UITheme.PrettyId(e.id), _pathMaterial == e.id)) _pathMaterial = e.id;
            UITheme.Tip($"Surface it in {UITheme.PrettyId(e.id).ToLowerInvariant()}");
        }
        surfaces.End();
    }

    // Drawn runs, behind a foldout with a ✕ per row: the same idiom as EditController's path and
    // fence lists.
    private void DrawRunLists()
    {
        var site = SiteOrNull;
        int paths = site?.paths?.Count ?? 0;
        int fences = site?.fences?.Count ?? 0;
        if (paths + fences == 0) return;

        _showRuns = UITheme.Foldout(_showRuns, $"Drawn outdoors ({paths + fences})");
        if (!_showRuns) return;

        if (site?.paths != null)
        {
            for (int i = 0; i < site.paths.Count; i++)
            {
                var p = site.paths[i];
                if (p == null) continue;

                GUILayout.BeginHorizontal();
                // Row content, not a caption: this list IS the panel. Three composed parts, one of
                // them a palette id, so it is bounded to what the ✕ leaves and wraps inside that.
                UITheme.Value($"{UITheme.PrettyId(p.material)} · {Units.Format(p.width)} wide · {Units.Format(RunLength(p.points))}",
                              "A walkway already drawn",
                              GUILayout.Width(UITheme.ContentWidth - UITheme.GlyphReserve));
                bool remove = UITheme.DangerButton("✕", GUILayout.Width(UITheme.GlyphW));
                UITheme.Tip("Remove this walkway");
                GUILayout.EndHorizontal();

                if (remove)
                {
                    Ctx.RecordEdit("Delete walkway");
                    site.paths.RemoveAt(i);
                    Ctx.Changed();
                    break;
                }
            }
        }

        if (site?.fences != null)
        {
            for (int i = 0; i < site.fences.Count; i++)
            {
                var f = site.fences[i];
                if (f == null) continue;

                GUILayout.BeginHorizontal();
                UITheme.Value($"{UITheme.PrettyId(f.fenceType)} railing · {Units.Format(f.height)} high · {Units.Format(RunLength(f.points))}",
                              "A railing already drawn",
                              GUILayout.Width(UITheme.ContentWidth - UITheme.GlyphReserve));
                bool remove = UITheme.DangerButton("✕", GUILayout.Width(UITheme.GlyphW));
                UITheme.Tip("Remove this railing");
                GUILayout.EndHorizontal();

                if (remove)
                {
                    Ctx.RecordEdit("Delete railing");
                    site.fences.RemoveAt(i);
                    Ctx.Changed();
                    break;
                }
            }
        }
    }

    private static float RunLength(float[][] points)
    {
        if (points == null || points.Length < 2) return 0f;
        float total = 0f;
        for (int i = 1; i < points.Length; i++)
        {
            if (points[i] == null || points[i - 1] == null ||
                points[i].Length < 2 || points[i - 1].Length < 2) continue;
            total += new Vector2(points[i][0] - points[i - 1][0],
                                 points[i][1] - points[i - 1][1]).magnitude;
        }
        return total;
    }

    // ---------------------------------------------------------------------------------------
    // Overlay
    // ---------------------------------------------------------------------------------------

    public override void DrawOverlay()
    {
        if (Ctx?.Cam == null || !_hasCursor) return;

        float y = Ctx.Level?.elevation ?? 0f;
        var snapColor = new Color(0.20f, 0.65f, 0.95f);
        var drawColor = _kind == Kind.Railing
            ? new Color(0.55f, 0.75f, 0.45f)
            : new Color(1f, 0.72f, 0.20f);

        for (int i = 0; i < _chain.Count; i++)
        {
            if (!OverlayDraw.ToScreen(Ctx.Cam, _chain[i], y, out Vector2 g)) continue;
            OverlayDraw.Dot(g, 8f, drawColor);
            if (i > 0 && OverlayDraw.ToScreen(Ctx.Cam, _chain[i - 1], y, out Vector2 prev))
                OverlayDraw.Line(prev, g, drawColor, 2.5f);
        }

        if (!OverlayDraw.ToScreen(Ctx.Cam, _cursor, y, out Vector2 cursorGui)) return;

        if (_chain.Count > 0 &&
            OverlayDraw.ToScreen(Ctx.Cam, _chain[_chain.Count - 1], y, out Vector2 lastGui))
        {
            OverlayDraw.Line(lastGui, cursorGui, drawColor, 2.5f);
            OverlayDraw.Readout(cursorGui, LiveReadout());
        }
        else
        {
            OverlayDraw.Readout(cursorGui, "Click to start a " + KindLabels[(int)_kind].ToLowerInvariant());
        }

        OverlayDraw.Dot(cursorGui, 10f, snapColor);
        if (_snap.kind == WallSnapping.SnapKind.Endpoint || _snap.kind == WallSnapping.SnapKind.OnWall)
            OverlayDraw.Circle(cursorGui, 12f, snapColor, 20, 2f);
    }

    // A ramp's readout is its slope, not just its length: that is the number the run is being drawn
    // to satisfy.
    private string LiveReadout()
    {
        float total = ChainLength(true);
        if (_kind != Kind.Ramp) return Units.Format(total);

        float slope = _rampRise / Mathf.Max(total, 0.001f);
        string verdict = slope <= RAMP_MAX_SLOPE + 1e-4f ? "✓ 1:12" : "steeper than 1:12";
        return $"{Units.Format(total)}   1:{1f / Mathf.Max(slope, 1e-4f):0}  {verdict}";
    }
}
