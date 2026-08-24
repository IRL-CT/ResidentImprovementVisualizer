using System.Collections.Generic;
using UnityEngine;

// Draws the current selection in the scene: the true outline of the selected wall, room, opening,
// item or person, with vertex handles and its NAME.
//
// Until this existed, selecting something changed the RAIL and nothing else. On a plan with forty
// walls, clicking one and then reading "Wall: 12' 4"" in the inspector left you with no way to tell
// WHICH wall you had, short of deleting it and undoing. That is the gap this closes, and it is the
// same gap the handle layer will need solved before it can draw anything draggable.
//
// The chips used to carry a figure too: a wall's length and thickness, a room's area and turning
// circle, an item's footprint. That was the same number the rail was already printing, drawn a second
// time over the plan, and over a plan with a selection in every room it read as noise. The outline is
// what answers "which one"; the figures live in the rail, once.
//
// TWO PLACEMENT DECISIONS, both load-bearing:
//
//   * NOT IN A TOOL. HomeEditController gates IHomeTool.DrawOverlay on !PointerOverUI, so an overlay
//     drawn from SelectTool disappears the instant the cursor moves onto the rail, which is exactly
//     where the cursor is while you read the inspector describing the thing you just selected. Drawn
//     from the controller instead, outside that guard, for the same reason OccupancyClock is not in a
//     tool either.
//   * A PLAIN CLASS, not a MonoBehaviour, like TimelineBar and UITheme. Selection already lives on
//     the controller so the rail can describe it whatever tool is active; the highlight follows the
//     selection, not the tool, and needs no scene wiring to do it.
//
// Everything is derived from the schema each frame rather than cached. Undo restores the whole HomeDoc
// without notifying anyone, so a cached WallDef would be a stale object drawing a wall that no longer
// exists: the same trap the drag layer will have to avoid.
public class SelectionOverlay
{
    private static readonly Color Accent = new Color(0.18f, 0.39f, 0.78f);      // UITheme.Accent
    private static readonly Color Warm   = new Color(1f, 0.72f, 0.20f);         // WallTool's draw colour
    private static readonly Color Person = new Color(0.35f, 0.70f, 1f);

    // Reused every frame so a selection redraw allocates nothing. OnGUI runs several times a frame.
    private readonly List<Vector2> _gui = new List<Vector2>();
    private readonly List<Vector2> _world = new List<Vector2>();

    public void Draw(Camera cam, LevelDef level, VariantDef variant,
                     HomeElementMarker.Kind kind, string id, HomeRenderer renderer)
    {
        if (cam == null || level == null || string.IsNullOrEmpty(id)) return;

        switch (kind)
        {
            case HomeElementMarker.Kind.Wall:      DrawWall(cam, level, id); break;
            case HomeElementMarker.Kind.Opening:   DrawOpening(cam, level, id); break;
            case HomeElementMarker.Kind.Room:
            case HomeElementMarker.Kind.Floor:
            case HomeElementMarker.Kind.Ceiling:   DrawRoom(cam, level, id); break;
            case HomeElementMarker.Kind.Furniture: DrawFurniture(cam, level, id); break;
            case HomeElementMarker.Kind.WallMount: DrawMount(cam, level, id); break;
            case HomeElementMarker.Kind.Sensor:    DrawSensor(cam, level, variant, id); break;
            case HomeElementMarker.Kind.Occupant:  DrawOccupant(cam, level, variant, id, renderer); break;
        }
    }

    // ---- walls ---------------------------------------------------------------------------------

    // The wall is outlined at its TRUE THICKNESS, including the junction extensions, so the highlight
    // traces the box that is actually on screen rather than a centerline that floats inside it. That
    // also makes the shared-corner overlap visible, which is worth seeing while drawing.
    private void DrawWall(Camera cam, LevelDef level, string id)
    {
        var wall = Find(level.walls, w => w.id == id);
        if (wall == null || !Segments.TryEnds(wall, out Vector2 a, out Vector2 b)) return;

        float y = level.elevation;
        float half = 0.5f * WallLayout.EffectiveThickness(wall, level);
        Vector2 fwd = (b - a).normalized;
        Vector2 left = new Vector2(-fwd.y, fwd.x);

        WallMeshBuilder.ComputeExtensions(wall, level, out float startExt, out float endExt);
        Vector2 a2 = a - fwd * startExt;
        Vector2 b2 = b + fwd * endExt;

        _world.Clear();
        _world.Add(a2 + left * half);
        _world.Add(b2 + left * half);
        _world.Add(b2 - left * half);
        _world.Add(a2 - left * half);
        if (!Project(cam, _world, y)) return;
        OverlayDraw.Haloed(_gui, Accent, 2.5f, closed: true);

        // Endpoint handles sit on the CENTERLINE ends, not the outline corners: those are the points a
        // corner drag will grab, and showing them here is the preview of that.
        if (OverlayDraw.ToScreen(cam, a, y, out Vector2 ga)) OverlayDraw.Handle(ga, 8f, Accent);
        if (OverlayDraw.ToScreen(cam, b, y, out Vector2 gb)) OverlayDraw.Handle(gb, 8f, Accent);

        if (OverlayDraw.ToScreen(cam, 0.5f * (a + b), y, out Vector2 mid))
            OverlayDraw.Readout(mid, "Wall");
    }

    // ---- openings ------------------------------------------------------------------------------

    // An opening has no geometry of its own (it is a gap WallLayout skips) so it is drawn as the span
    // it occupies on its host wall, with a tick across the thickness at each jamb.
    /// <summary>
    /// The opening under the cursor in the wall inspector's list, drawn dimmer than a selection so the
    /// two read as "about to pick" and "picked" rather than competing for the same meaning.
    /// </summary>
    // Public because an opening can no longer be clicked in the plan: the list in the wall's rail is
    // the way to one, and a list of same-named doors needs this to answer "which one" before you
    // commit. It costs nothing that the selection path did not already need: this is all derived
    // from the schema, with no GameObject, collider or marker involved, so it works for an opening
    // that is not the selection exactly as it does for one that is.
    public void DrawHoverOpening(Camera cam, LevelDef level, string id)
    {
        if (cam == null || level == null || string.IsNullOrEmpty(id)) return;
        DrawOpening(cam, level, id, HoverWarm);
    }

    private static readonly Color HoverWarm = new Color(1f, 0.72f, 0.20f, 0.55f);

    private void DrawOpening(Camera cam, LevelDef level, string id) => DrawOpening(cam, level, id, Warm);

    private void DrawOpening(Camera cam, LevelDef level, string id, Color color)
    {
        var op = Find(level.openings, o => o.id == id);
        if (op == null) return;
        var wall = Find(level.walls, w => w.id == op.wallId);
        if (wall == null || !Segments.TryEnds(wall, out Vector2 a, out Vector2 b)) return;

        float y = level.elevation;
        float half = 0.5f * WallLayout.EffectiveThickness(wall, level);
        Vector2 fwd = (b - a).normalized;
        Vector2 left = new Vector2(-fwd.y, fwd.x);

        Vector2 s = a + fwd * Mathf.Max(0f, op.offset - 0.5f * op.width);
        Vector2 e = a + fwd * Mathf.Min((b - a).magnitude, op.offset + 0.5f * op.width);

        _world.Clear();
        _world.Add(s + left * half);
        _world.Add(e + left * half);
        _world.Add(e - left * half);
        _world.Add(s - left * half);
        if (!Project(cam, _world, y)) return;
        OverlayDraw.Haloed(_gui, color, 3f, closed: true);

        if (OverlayDraw.ToScreen(cam, 0.5f * (s + e), y, out Vector2 mid))
            OverlayDraw.Readout(mid, Pretty(op.kind));
    }

    // ---- rooms ---------------------------------------------------------------------------------

    private void DrawRoom(Camera cam, LevelDef level, string id)
    {
        var room = Find(level.rooms, r => r.id == id);
        if (room?.polygon == null || room.polygon.Length < 3) return;

        float y = level.elevation;
        _world.Clear();
        _world.AddRange(PolygonTriangulator.ToVector2(room.polygon));
        if (_world.Count < 3 || !Project(cam, _world, y)) return;

        OverlayDraw.Haloed(_gui, Accent, 2.5f, closed: true);

        // No vertex handles. A room's polygon is DERIVED from the walls that enclose it, so there is
        // nothing here that can be dragged, and a handle that cannot be dragged is a promise the tool
        // does not keep, the same reason an empty panel reads as a bug. The WALL handles are the ones
        // that move this outline. The outline itself stays: answering "which one" is why this file
        // exists. (OverlayDraw.Handle keeps its other callers.)

        // The turning-circle ring used to be drawn here. It is gone from the plan, and from the rail
        // and the report, and now lives only in the Measure tool, which is where you go when that is
        // the question you are actually asking. HomeMetrics still computes it; OccupancyModel stands
        // people with it and the walkthrough spawns with it.
        if (OverlayDraw.ToScreen(cam, HomeMetrics.RoomCentroid(room), y, out Vector2 mid))
            OverlayDraw.Readout(mid, string.IsNullOrEmpty(room.name)
                                     ? RoomRegions.Pretty(room.roomType) : room.name);
    }

    // ---- furniture and mounts ------------------------------------------------------------------

    // The TRUE rotated rectangle, not the axis-aligned bound. HomeMetrics.FootprintOf snaps to quarter
    // turns and FurnitureFit.Footprint returns an extent rather than corners; at 45 degrees either one
    // would outline a box noticeably larger than the item on screen.
    private void DrawFurniture(Camera cam, LevelDef level, string id)
    {
        var item = Find(level.furniture, f => f.instanceId == id);
        if (item?.position == null || item.position.Length < 3) return;

        float w = 0.5f, d = 0.5f;
        if (item.boxSizeMeters != null && item.boxSizeMeters.Length >= 3)
        {
            w = 0.5f * item.boxSizeMeters[0];
            d = 0.5f * item.boxSizeMeters[2];
        }

        float rad = item.rotationY * Mathf.Deg2Rad;
        // Quaternion.Euler(0, yaw, 0) maps +x to (cos, -sin) in XZ: the sign that the placement ghost
        // had backwards, which is why a preview at 30 degrees used to mirror what actually spawned.
        Vector2 ex = new Vector2(Mathf.Cos(rad), -Mathf.Sin(rad));
        Vector2 ez = new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
        Vector2 c = new Vector2(item.position[0], item.position[2]);

        _world.Clear();
        _world.Add(c + ex * w + ez * d);
        _world.Add(c + ex * w - ez * d);
        _world.Add(c - ex * w - ez * d);
        _world.Add(c - ex * w + ez * d);
        if (!Project(cam, _world, level.elevation)) return;

        OverlayDraw.Haloed(_gui, Warm, 2.5f, closed: true);
        foreach (var g in _gui) OverlayDraw.Handle(g, 6f, Warm);

        if (OverlayDraw.ToScreen(cam, c, level.elevation, out Vector2 mid))
            OverlayDraw.Readout(mid, Pretty(item.prefabType));
    }

    // A device is a ring and a handle, like a wall mount: the two things in this app that are a point
    // rather than a shape. What it adds is the ELEMENT IT WATCHES, named at the marker: a small box on
    // a wall says nothing about whether it is on the front door or the bathroom one, and that is the
    // only interesting thing about it.
    //
    // Its coverage is deliberately NOT drawn here. SensorOverlay draws that for every device at once,
    // and drawing it a second time for the selected one would put two rings at different alphas on the
    // same arc: the same reasoning that took the figures out of the selection chips.
    private void DrawSensor(Camera cam, LevelDef level, VariantDef variant, string id)
    {
        var s = HomeRenderer.FindSensor(id, level);
        if (s == null) return;

        var pose = SensorPose.Resolve(s, level, variant);
        if (!pose.resolved) return;   // worn: it is on a person, not in the plan

        if (!OverlayDraw.ToScreen(cam, pose.xz, pose.position.y, out Vector2 g)) return;

        OverlayDraw.Circle(g, 14f, Accent, 24, 2.5f);
        OverlayDraw.Handle(g, 7f, Accent);
        OverlayDraw.Readout(g, SensorDevices.LabelOf(s) + " · " + (pose.hostLabel ?? ""));
    }

    private void DrawMount(Camera cam, LevelDef level, string id)
    {
        var m = Find(level.wallMounted, x => x.instanceId == id);
        if (m == null) return;
        var wall = Find(level.walls, w => w.id == m.wallId);
        if (wall == null) return;

        Vector2 at = HomeMetrics.PointOnWall(wall, m.offset);
        if (!OverlayDraw.ToScreen(cam, at, level.elevation + m.mountHeight, out Vector2 g)) return;

        OverlayDraw.Circle(g, 14f, Warm, 24, 2.5f);
        OverlayDraw.Handle(g, 7f, Warm);
        OverlayDraw.Readout(g, Pretty(m.prefabType));
    }

    // ---- occupants -----------------------------------------------------------------------------

    private void DrawOccupant(Camera cam, LevelDef level, VariantDef variant, string id,
                              HomeRenderer renderer)
    {
        var person = Find(variant?.occupants, p => p.id == id);
        if (person == null || renderer == null) return;

        // Positions are derived from the schedule and the clock, never stored, so ask the model where
        // this person is right now rather than reading a coordinate that does not exist.
        var poses = renderer.CurrentPoses();
        if (poses == null || !poses.TryGetValue(id, out OccupancyModel.Pose pose)) return;

        // Someone who is out has no marker to ring. Saying so beats drawing nothing and leaving the
        // user to wonder whether the click registered.
        if (!pose.present)
        {
            OverlayDraw.Readout(new Vector2(Screen.width * 0.5f, 90f), $"{person.name} is out right now.");
            return;
        }

        if (!OverlayDraw.ToScreen(cam, pose.xz, level.elevation, out Vector2 g)) return;
        OverlayDraw.Circle(g, 18f, Person, 28, 3f);
        OverlayDraw.Handle(g, 7f, Person);
        OverlayDraw.Readout(g, $"{person.name}   {OccupancyModel.Describe(pose)}");
    }

    // ---- plumbing ------------------------------------------------------------------------------

    // Projects a world XZ ring into _gui. False when any point is behind the camera: a partially
    // projected outline is worse than none, because the missing edge reads as a gap in the geometry.
    private bool Project(Camera cam, List<Vector2> worldXZ, float y)
    {
        _gui.Clear();
        foreach (var p in worldXZ)
        {
            if (!OverlayDraw.ToScreen(cam, p, y, out Vector2 g)) return false;
            _gui.Add(g);
        }
        return _gui.Count >= 2;
    }

    private static T Find<T>(IReadOnlyList<T> list, System.Func<T, bool> match) where T : class
    {
        if (list == null) return null;
        for (int i = 0; i < list.Count; i++)
            if (list[i] != null && match(list[i])) return list[i];
        return null;
    }

    private static string Pretty(string token)
    {
        if (string.IsNullOrEmpty(token)) return "Item";
        string s = token.Replace('_', ' ');
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
