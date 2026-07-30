using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Point-to-point measuring with a running total.
//
// Deliberately simple, and deliberately present. The tool ships no accessibility rules, so the way a
// question like "is there 60 inches of turning space beside the bed?" gets answered right now is that
// somebody measures it. A tape that reads in feet and inches, snaps to wall corners, and keeps a
// running total is the honest version of that.
public class MeasureTool : HomeToolBase
{
    public override string Id => "measure";
    public override string DisplayName => "Measure";

    private readonly List<Vector2> _points = new List<Vector2>();
    private Vector2 _cursor;
    private bool _hasCursor;
    private WallSnapping.Options _opts = WallSnapping.Options.Default;

    public override void Enter(HomeToolContext ctx)
    {
        base.Enter(ctx);
        _opts.axisLock = false;   // measuring should follow the thing being measured, not square to it
    }

    public override void Exit() => _points.Clear();

    public override void HandleInput()
    {
        if (Ctx?.Level == null) return;

        _hasCursor = Ctx.GroundPoint(out Vector2 raw);
        if (_hasCursor)
        {
            _opts.enabled = !Ctx.ShiftHeld;
            _cursor = WallSnapping.Snap(raw, Ctx.Level, null, _opts).point;
        }

        if (LeftClicked() && _hasCursor) _points.Add(_cursor);
        if (KeyDown(Key.Escape) || KeyDown(Key.Enter)) _points.Clear();
        if (KeyDown(Key.Backspace) && _points.Count > 0) _points.RemoveAt(_points.Count - 1);
    }

    private float Total()
    {
        float sum = 0f;
        for (int i = 1; i < _points.Count; i++) sum += Vector2.Distance(_points[i - 1], _points[i]);
        return sum;
    }

    public override void DrawRail()
    {
        UITheme.Note("Click to drop points. Each leg and the running total are shown. " +
                     "Esc clears, Backspace removes the last point.");
        GUILayout.Space(8);

        if (_points.Count >= 2)
        {
            UITheme.Num(Units.Format(Total()));
            UITheme.Note("Total across " + (_points.Count - 1) + " leg" + (_points.Count == 2 ? "" : "s"));

            GUILayout.Space(6);
            for (int i = 1; i < _points.Count; i++)
                UITheme.Note($"  Leg {i}: {Units.Format(Vector2.Distance(_points[i - 1], _points[i]))}");
        }
        else if (_points.Count == 1) UITheme.Note("Click a second point.");

        if (_points.Count > 0 && UITheme.SecondaryButton("Clear")) _points.Clear();

        // Turning space per room, computed rather than measured — the number a future clearance rule
        // would test, surfaced now so it is at least visible.
        if (Ctx?.Level?.rooms != null && Ctx.Level.rooms.Count > 0)
        {
            GUILayout.Space(12);
            UITheme.Header("Turning space by room");
            foreach (var r in Ctx.Level.rooms)
            {
                if (r == null) continue;
                var circle = HomeMetrics.LargestInscribedCircle(r);
                if (!circle.valid) continue;
                UITheme.Note($"  {(string.IsNullOrEmpty(r.name) ? r.roomType : r.name)}: " +
                             Units.Format(circle.radius * 2f) + " circle");
            }
        }
    }

    public override void DrawOverlay()
    {
        if (Ctx?.Cam == null || Ctx.Level == null) return;

        float y = Ctx.Level.elevation;
        var color = new Color(1f, 0.45f, 0.75f);

        for (int i = 0; i < _points.Count; i++)
        {
            if (!OverlayDraw.ToScreen(Ctx.Cam, _points[i], y, out Vector2 g)) continue;
            OverlayDraw.Dot(g, 9f, color);

            if (i == 0 || !OverlayDraw.ToScreen(Ctx.Cam, _points[i - 1], y, out Vector2 prev)) continue;

            OverlayDraw.Line(prev, g, color, 2.5f);
            OverlayDraw.Readout((prev + g) * 0.5f,
                Units.Format(Vector2.Distance(_points[i - 1], _points[i])));
        }

        if (!_hasCursor || _points.Count == 0) return;
        if (!OverlayDraw.ToScreen(Ctx.Cam, _cursor, y, out Vector2 cur)) return;
        if (!OverlayDraw.ToScreen(Ctx.Cam, _points[_points.Count - 1], y, out Vector2 last)) return;

        OverlayDraw.DashedLine(last, cur, color, 2f);
        OverlayDraw.Readout(cur, Units.Format(Vector2.Distance(_points[_points.Count - 1], _cursor)));
    }
}
