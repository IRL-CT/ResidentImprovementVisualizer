using System;
using System.Collections.Generic;
using UnityEngine;

// Decomposes one wall into the solid rectangular chunks that remain once its openings are taken out.
//
// THIS IS THE FILE THAT AVOIDS CSG. Cutting a door out of a wall is the obvious way to model an
// opening and the wrong way to implement one: boolean mesh subtraction is slow, numerically fragile,
// and produces geometry nobody can re-edit. Instead a wall is never solid to begin with — it is a
// list of boxes, and an opening is simply a gap the list skips over:
//
//     wall a------------------------------------------------b       length L, height H
//     openings         [ door ]              [ window ]
//     result     PANEL |       | PANEL      |         | PANEL       full-height segments
//                      | HEADER|            | HEADER  |             above each opening
//                                           |  SILL   |             below each window
//
// Everything here works in the wall's 1-D CENTERLINE space: `t` runs 0..length from endpoint `a`
// toward `b`, and `y` runs 0..height above the finished floor. No 3-D vectors, no transforms, no
// Unity scene access — which is exactly why it is cheap to unit-test every awkward case (an opening
// flush against a corner, two openings touching, an opening taller than its wall).
//
// Junction extension is deliberately NOT applied here. WallMeshBuilder extends the boxes that touch
// t=0 and t=length outward by half a thickness so corners close. Keeping that out of this file means
// an opening's `offset` never shifts when a neighbouring wall is added or removed.
public static class WallLayout
{
    public enum Kind
    {
        Panel,   // full-height wall between openings
        Header,  // the strip above an opening, up to the wall top
        Sill,    // the strip below a window, down to the floor
    }

    // One solid chunk, in wall-local (t, y) space. Meters throughout.
    public struct Box
    {
        public float t0, t1;   // along the centerline from `a`
        public float y0, y1;   // above finished floor
        public Kind kind;
        public string openingId;   // the opening this header/sill belongs to; null for panels

        public float Length => t1 - t0;
        public float Height => y1 - y0;
    }

    // The 1-D footprint an opening occupies along the centerline.
    private struct Span
    {
        public float t0, t1;
        public float sill, top;
        public string id;
    }

    /// <summary>
    /// Builds the solid boxes for a wall of the given length and height, with the given openings.
    /// Openings are matched to the wall by the caller; anything passed in is assumed to belong here.
    /// Overlapping openings are tolerated (they merge into one void) rather than throwing — bad data
    /// should render oddly, never crash a visioning session.
    /// </summary>
    public static List<Box> Build(float wallLength, float wallHeight, IReadOnlyList<OpeningDef> openings)
    {
        var boxes = new List<Box>();
        if (wallLength <= HomeConventions.EPS || wallHeight <= HomeConventions.EPS)
            return boxes;   // degenerate wall — nothing to draw

        var spans = CollectSpans(wallLength, wallHeight, openings);

        if (spans.Count == 0)
        {
            AddBox(boxes, 0f, wallLength, 0f, wallHeight, Kind.Panel, null);
            return boxes;
        }

        float cursor = 0f;
        foreach (var s in spans)
        {
            // Full-height wall between the previous opening and this one.
            if (s.t0 > cursor + HomeConventions.EPS)
                AddBox(boxes, cursor, s.t0, 0f, wallHeight, Kind.Panel, null);

            // Header: the strip from the top of the opening to the top of the wall. Absent when the
            // opening runs to (or past) the ceiling, which is what makes a full-height pass-through
            // read as a genuine gap rather than a doorway.
            if (s.top < wallHeight - HomeConventions.EPS)
                AddBox(boxes, s.t0, s.t1, s.top, wallHeight, Kind.Header, s.id);

            // Sill: the strip below a window. Doors have sill 0 and produce nothing here.
            if (s.sill > HomeConventions.EPS)
                AddBox(boxes, s.t0, s.t1, 0f, s.sill, Kind.Sill, s.id);

            cursor = Mathf.Max(cursor, s.t1);
        }

        if (cursor < wallLength - HomeConventions.EPS)
            AddBox(boxes, cursor, wallLength, 0f, wallHeight, Kind.Panel, null);

        return boxes;
    }

    /// <summary>
    /// Convenience overload: pulls this wall's openings out of a level and resolves the wall's
    /// effective thickness/height defaults.
    /// </summary>
    public static List<Box> Build(WallDef wall, LevelDef level)
    {
        float length = WallLength(wall);
        float height = EffectiveHeight(wall, level);
        return Build(length, height, OpeningsFor(wall, level));
    }

    // ---------------------------------------------------------------------------------------

    /// Openings belonging to a wall, in ascending centerline order.
    public static List<OpeningDef> OpeningsFor(WallDef wall, LevelDef level)
    {
        var result = new List<OpeningDef>();
        if (wall == null || level?.openings == null) return result;
        foreach (var o in level.openings)
            if (o != null && o.wallId == wall.id) result.Add(o);
        result.Sort((x, y) => x.offset.CompareTo(y.offset));
        return result;
    }

    public static float WallLength(WallDef w)
    {
        if (w?.a == null || w.b == null || w.a.Length < 2 || w.b.Length < 2) return 0f;
        float dx = w.b[0] - w.a[0];
        float dz = w.b[1] - w.a[1];
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    public static float EffectiveHeight(WallDef w, LevelDef level)
    {
        if (w != null && w.height > HomeConventions.EPS) return w.height;
        if (level != null && level.ceilingHeight > HomeConventions.EPS) return level.ceilingHeight;
        return HomeConventions.DEFAULT_CEILING_HEIGHT;
    }

    public static float EffectiveThickness(WallDef w, LevelDef level)
    {
        if (w != null && w.thickness > HomeConventions.EPS) return w.thickness;
        if (level != null && level.wallThickness > HomeConventions.EPS) return level.wallThickness;
        return HomeConventions.DEFAULT_WALL_THICKNESS;
    }

    // ---------------------------------------------------------------------------------------

    // Turns openings into clamped, ordered 1-D spans. Anything degenerate or entirely off the wall
    // is dropped here so Build() below can stay simple.
    private static List<Span> CollectSpans(float wallLength, float wallHeight, IReadOnlyList<OpeningDef> openings)
    {
        var spans = new List<Span>();
        if (openings == null) return spans;

        foreach (var o in openings)
        {
            if (o == null || o.width <= HomeConventions.EPS) continue;

            float half = 0.5f * o.width;
            float t0 = Mathf.Max(0f, o.offset - half);
            float t1 = Mathf.Min(wallLength, o.offset + half);
            if (t1 - t0 <= HomeConventions.EPS) continue;   // sits entirely off the end of the wall

            float sill = Mathf.Clamp(o.sillHeight, 0f, wallHeight);
            float rawHeight = o.height > HomeConventions.EPS ? o.height : wallHeight;
            float top = Mathf.Min(sill + rawHeight, wallHeight);
            if (top - sill <= HomeConventions.EPS) continue;   // no vertical extent — not a void

            spans.Add(new Span { t0 = t0, t1 = t1, sill = sill, top = top, id = o.id });
        }

        spans.Sort((x, y) => x.t0.CompareTo(y.t0));
        return spans;
    }

    private static void AddBox(List<Box> list, float t0, float t1, float y0, float y1, Kind kind, string openingId)
    {
        if (t1 - t0 <= HomeConventions.EPS || y1 - y0 <= HomeConventions.EPS) return;
        list.Add(new Box { t0 = t0, t1 = t1, y0 = y0, y1 = y1, kind = kind, openingId = openingId });
    }
}
