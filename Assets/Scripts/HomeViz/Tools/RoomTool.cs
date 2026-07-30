using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Draws room floor polygons.
//
// Rooms are authored rather than detected from the wall graph. Auto-detecting enclosed regions is
// appealing but fragile while a plan is half-traced — and half-traced is the normal state of this
// tool for most of a session. Explicit polygons always work, and detection can be added later as a
// convenience without changing anything stored.
//
// Live area readout while drawing, because "is this bedroom big enough" is answered in square feet.
public class RoomTool : HomeToolBase
{
    public override string Id => "room";
    public override string DisplayName => "Rooms";

    private readonly List<Vector2> _points = new List<Vector2>();
    private Vector2 _cursor;
    private bool _hasCursor;

    private string _roomType = RoomType.Bedroom;
    private string _floorMaterial = "floor_vinyl";
    private WallSnapping.Options _opts = WallSnapping.Options.Default;

    public override void Exit() => _points.Clear();

    public override void HandleInput()
    {
        if (Ctx?.Level == null || Ctx.IsLocked) return;

        _hasCursor = Ctx.GroundPoint(out Vector2 raw);
        if (_hasCursor)
        {
            _opts.enabled = !Ctx.ShiftHeld;
            // Snapping to wall corners matters more here than anywhere: a room polygon that lands on
            // its walls' centerlines reports the right area, and one that misses by 40 mm does not.
            var snap = WallSnapping.Snap(raw, Ctx.Level, _points.Count > 0 ? _points[_points.Count - 1] : (Vector2?)null, _opts);
            _cursor = snap.point;
        }

        if (LeftClicked() && _hasCursor)
        {
            if (_points.Count > 0 && (_cursor - _points[_points.Count - 1]).sqrMagnitude < 1e-4f) return;
            _points.Add(_cursor);
        }

        if (KeyDown(Key.Enter) || KeyDown(Key.NumpadEnter)) Commit();
        if (KeyDown(Key.Escape)) _points.Clear();
        if (KeyDown(Key.Backspace) && _points.Count > 0) _points.RemoveAt(_points.Count - 1);
    }

    private void Commit()
    {
        if (_points.Count < 3) { _points.Clear(); return; }

        Ctx.RecordEdit("Add room");
        Ctx.Level.rooms.Add(new RoomDef
        {
            id = Guid.NewGuid().ToString(),
            name = Pretty(_roomType),
            roomType = _roomType,
            polygon = PolygonTriangulator.ToArray(_points),
            floorMaterial = _floorMaterial,
            ceilingMaterial = "ceiling_white",
        });
        _points.Clear();
        Ctx.Changed();
    }

    public override void DrawRail()
    {
        if (RefuseIfLocked()) return;

        UITheme.Note("Click the corners of the room. Enter closes it, Backspace removes the last point.");
        GUILayout.Space(6);

        UITheme.Header("Room type");
        string[] types = { RoomType.Bedroom, RoomType.Bathroom, RoomType.Kitchen,
                           RoomType.Living, RoomType.Hall, RoomType.Entry };
        GUILayout.BeginHorizontal();
        for (int i = 0; i < types.Length; i++)
        {
            if (i == 3) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            if (UITheme.Chip(Pretty(types[i]), _roomType == types[i])) SetType(types[i]);
        }
        GUILayout.EndHorizontal();

        var palette = Ctx.Renderer?.MaterialPalette;
        if (palette != null)
        {
            GUILayout.Space(8);
            UITheme.Header("Floor finish");
            GUILayout.BeginHorizontal();
            foreach (var e in palette.For(InteriorMaterialPalette.Surface.Floor))
                if (UITheme.Chip(Pretty(e.materialId), _floorMaterial == e.materialId))
                    _floorMaterial = e.materialId;
            GUILayout.EndHorizontal();
        }

        if (_points.Count >= 3)
        {
            GUILayout.Space(8);
            UITheme.Num(Units.FormatArea(PolygonTriangulator.Area(_points)));
            UITheme.Note("Area so far");
            if (UITheme.PrimaryButton("Close room")) Commit();
        }
    }

    // Bathrooms get a wipeable floor by default, bedrooms a soft one — small touch, saves a click on
    // nearly every room.
    private void SetType(string type)
    {
        _roomType = type;
        _floorMaterial = type switch
        {
            RoomType.Bathroom => "floor_vinyl",
            RoomType.Kitchen => "floor_vinyl",
            RoomType.Bedroom => "floor_carpet",
            _ => "floor_oak",
        };
    }

    public override void DrawOverlay()
    {
        if (Ctx?.Cam == null || Ctx.Level == null || _points.Count == 0) return;

        float y = Ctx.Level.elevation;
        var color = new Color(0.35f, 0.70f, 1f);

        for (int i = 0; i < _points.Count; i++)
        {
            if (!OverlayDraw.ToScreen(Ctx.Cam, _points[i], y, out Vector2 g)) continue;
            OverlayDraw.Dot(g, 8f, color);

            if (i > 0 && OverlayDraw.ToScreen(Ctx.Cam, _points[i - 1], y, out Vector2 prev))
                OverlayDraw.Line(prev, g, color, 2.5f);
        }

        if (!_hasCursor || !OverlayDraw.ToScreen(Ctx.Cam, _cursor, y, out Vector2 cur)) return;

        if (OverlayDraw.ToScreen(Ctx.Cam, _points[_points.Count - 1], y, out Vector2 last))
            OverlayDraw.Line(last, cur, color, 2f);

        // Closing edge, so the shape being committed is unambiguous before Enter is pressed.
        if (_points.Count >= 2 && OverlayDraw.ToScreen(Ctx.Cam, _points[0], y, out Vector2 first))
            OverlayDraw.DashedLine(cur, first, new Color(color.r, color.g, color.b, 0.6f), 2f);

        if (_points.Count >= 2)
        {
            var preview = new List<Vector2>(_points) { _cursor };
            OverlayDraw.Readout(cur, Units.FormatArea(PolygonTriangulator.Area(preview)));
        }
    }

    private static string Pretty(string token)
    {
        if (string.IsNullOrEmpty(token)) return "Room";
        string s = token.Replace('_', ' ');
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
