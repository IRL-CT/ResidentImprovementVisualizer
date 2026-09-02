using System.Collections.Generic;
using UnityEngine;

public enum SketchWallSide { North, South, East, West }

/// <summary>One room rectangle on wall centerlines, in working pixels, top-down.</summary>
public struct SketchRoomRect
{
    public int room;      // cell-map room label
    public int parent;    // index of the room's root rect in the detector's list; -1 for a root
    public float cx0, cy0, cx1, cy1;

    public bool IsPart => parent >= 0;
}

/// <summary>One verified opening, still in working pixels: what Assemble turns into the spec.</summary>
public struct SketchGap
{
    public int rectA;             // index into the rect list
    public int rectB;             // -1 for an opening in an exterior wall
    public SketchWallSide edge;   // which side of rectA faces out, for exterior openings
    public bool horizontal;       // orientation of the WALL the opening sits in
    public int line;              // wall line index in the grid, oriented by `horizontal`
    public float center;          // px along the wall
    public float widthPx;
    public bool window;
    public bool closet;           // opens into a closet-sized cell; kept out of the scale anchor
}

// Turns the graph's doorway candidates and the segments' double-line marks into verified openings.
//
// A CANDIDATE IS NOT AN OPENING until the mask agrees. Collinear endpoints with a plausible gap
// also occur where a dimension line ends near a wall and where a text baseline lines up with a
// stroke, and a phantom door costs a wall AND corrupts the scale estimate. So the slab of pixels
// across the gap must read open, and each jamb must carry real ink (a perpendicular wall
// vouches for itself). Ambiguity emits nothing: a missed opening costs an opening, a phantom one
// costs a wall.
//
// DOOR SYMBOLS ARE TOLERATED, NOT READ. Swing arcs, bifold zigzags and a route line walked through
// a doorway all leave ink inside the gap. The graph has already ruled out any wall-like run on the
// line (it would have become a segment and closed the gap), so ink in the slab that blocks only
// short bursts of positions along the wall is a symbol crossing the band, and the gap still reads
// open. Long blocked stretches mean a shattered wall or a label lying along the line, and veto.
//
// WINDOWS are the double-line convention, already measured: segment extraction marked every
// position whose crossing was two thin lines around a light channel, so a window is simply a long
// enough run of those marks on a wall with the outside on one side. No second reader exists to
// disagree with the first.
public static class SketchOpeningReader
{
    public static List<SketchGap> Read(SketchWallGraphResult graph, SketchWallGrid grid,
                                       List<SketchRoomRect> rects, bool[] wall, int w, int h,
                                       int stroke, bool windows)
    {
        var gaps = new List<SketchGap>();
        var cells = graph.cells;

        // Doorways, in wall order: horizontal lines top to bottom, then vertical left to right,
        // then along each line. The pairwise rescan this replaces ordered by rectangle pair; that
        // order was as arbitrary as this one and nothing downstream reads order as meaning.
        var order = new List<int>();
        for (int i = 0; i < graph.doorways.Count; i++) order.Add(i);
        order.Sort((a, b) =>
        {
            var da = graph.doorways[a];
            var db = graph.doorways[b];
            if (da.horizontal != db.horizontal) return da.horizontal ? -1 : 1;
            if (da.line != db.line) return da.line.CompareTo(db.line);
            int byG = da.g0.CompareTo(db.g0);
            return byG != 0 ? byG : a.CompareTo(b);
        });

        int minJamb = 4 * stroke;
        foreach (int idx in order)
        {
            var d = graph.doorways[idx];
            float width = d.g1 - d.g0 - 1f;
            if (width < 2f) continue;

            // A short jamb (a run of 2 to 4 strokes) is usually annotation, but it is also the
            // wall stub beside a closet door drawn near a corner. The candidate is deferred, not
            // dropped: it survives only if it opens into a closet-sized cell, judged below.
            bool shortA = d.jambA >= 0 && d.jambA < minJamb;
            bool shortB = d.jambB >= 0 && d.jambB < minJamb;

            float lineCoord = d.horizontal ? cells.ys[d.line] : cells.xs[d.line];
            int thickness = Mathf.Max(d.thickness, 1);
            if (!GapReadsOpen(wall, w, h, d.horizontal, lineCoord, d.g0, d.g1, thickness, stroke))
                continue;

            float center = 0.5f * (d.g0 + d.g1);
            float off = 0.5f * thickness + 2f;
            int sideA, sideB;   // A: smaller coordinate side (north of an H wall, west of a V wall)
            if (d.horizontal)
            {
                sideA = cells.LabelAt(center, lineCoord - off);
                sideB = cells.LabelAt(center, lineCoord + off);
            }
            else
            {
                sideA = cells.LabelAt(lineCoord - off, center);
                sideB = cells.LabelAt(lineCoord + off, center);
            }

            if (sideA == SketchCellMap.FOLDED || sideB == SketchCellMap.FOLDED) continue;
            if (sideA == sideB) continue;   // both outside, or a break inside one room

            // A closet is at most about twice its door in each direction, so the smaller adjacent
            // rect against the door's own width judges closet-ness before any scale exists. Short
            // jambs are accepted only there; the flag also keeps the gap out of the scale anchor.
            if (sideA >= 0 && sideB >= 0)
            {
                int rectA = FindRect(rects, sideA, d.horizontal, lineCoord, center, -off);
                int rectB = FindRect(rects, sideB, d.horizontal, lineCoord, center, +off);
                if (rectA < 0 || rectB < 0) continue;
                bool closet = Mathf.Min(AreaPx(rects[rectA]), AreaPx(rects[rectB]))
                              <= 4f * width * width;
                if ((shortA || shortB) && !closet) continue;
                gaps.Add(new SketchGap
                {
                    rectA = rectA, rectB = rectB,
                    horizontal = d.horizontal, line = d.line,
                    center = center, widthPx = width, closet = closet,
                });
            }
            else
            {
                bool outsideOnA = sideA < 0;
                int room = outsideOnA ? sideB : sideA;
                int rect = FindRect(rects, room, d.horizontal, lineCoord, center, outsideOnA ? +off : -off);
                if (rect < 0) continue;
                bool closet = AreaPx(rects[rect]) <= 4f * width * width;
                if ((shortA || shortB) && !closet) continue;
                gaps.Add(new SketchGap
                {
                    rectA = rect, rectB = -1,
                    edge = Edge(d.horizontal, outsideOnA),
                    horizontal = d.horizontal, line = d.line,
                    center = center, widthPx = width, closet = closet,
                });
            }
        }

        if (windows) Windows(graph, grid, rects, stroke, gaps);
        Dedup(gaps);
        return gaps;
    }

    private static float AreaPx(SketchRoomRect r) => (r.cx1 - r.cx0) * (r.cy1 - r.cy0);

    /// <summary>Which side of a room an exterior wall is, from the wall's orientation and where the
    /// outside lies. Image y runs down, so the outside ABOVE a horizontal wall is the north.</summary>
    private static SketchWallSide Edge(bool horizontal, bool outsideOnSmallerCoordinate)
    {
        if (horizontal) return outsideOnSmallerCoordinate ? SketchWallSide.North : SketchWallSide.South;
        return outsideOnSmallerCoordinate ? SketchWallSide.West : SketchWallSide.East;
    }

    /// <summary>
    /// The gap really is open. A nearly ink free slab passes at once. A slab crossed by door
    /// symbols (swing arcs, bifold panels, a route line walked through the doorway) passes when
    /// the ink blocks only short bursts of positions along the wall: the graph has already ruled
    /// out any wall-like run on the line, so long blocked stretches mean a shattered wall or a
    /// label lying along the line, and those still veto. The slab spans the gap along the wall
    /// and the wall's thickness plus a pixel each way across it.
    /// </summary>
    public static bool GapReadsOpen(bool[] wall, int w, int h, bool horizontal, float lineCoord,
                                    float g0, float g1, int thickness, int stroke)
    {
        int a0 = Mathf.CeilToInt(g0 + 1f);
        int a1 = Mathf.FloorToInt(g1 - 1f);
        int c0 = Mathf.RoundToInt(lineCoord - 0.5f * thickness - 1f);
        int c1 = Mathf.RoundToInt(lineCoord + 0.5f * thickness + 1f);
        if (a1 < a0) return false;

        int total = 0, dark = 0, run = 0, maxRun = 0;
        for (int a = a0; a <= a1; a++)
        {
            bool blocked = false;
            for (int c = c0; c <= c1; c++)
            {
                int x = horizontal ? a : c;
                int y = horizontal ? c : a;
                if (x < 0 || x >= w || y < 0 || y >= h) continue;
                total++;
                if (!wall[y * w + x]) continue;
                dark++;
                blocked = true;
            }
            run = blocked ? run + 1 : 0;
            if (run > maxRun) maxRun = run;
        }
        if (total == 0) return false;
        if (dark * 10 <= total) return true;
        return maxRun <= 3 * stroke && dark * 10 <= 3 * total;
    }

    /// <summary>
    /// One opening per span: where a window run overlaps a verified doorway gap on the same wall
    /// line, the doorway stands and the window is dropped. The doorway passed the mask and named
    /// its sides; the window is a line pattern reading of the same ink.
    /// </summary>
    public static void Dedup(List<SketchGap> gaps)
    {
        var kept = new List<SketchGap>(gaps.Count);
        for (int i = 0; i < gaps.Count; i++)
        {
            var g = gaps[i];
            bool drop = false;
            if (g.window)
                for (int j = 0; j < gaps.Count && !drop; j++)
                {
                    var d = gaps[j];
                    if (d.window || d.horizontal != g.horizontal || d.line != g.line) continue;
                    drop = Mathf.Abs(d.center - g.center) < 0.5f * (d.widthPx + g.widthPx);
                }
            if (!drop) kept.Add(g);
        }
        gaps.Clear();
        gaps.AddRange(kept);
    }

    /// <summary>
    /// The rectangle of a room that hosts a point just off the wall at the opening's center: the
    /// first rect of that room containing it, since a room's rects tile the room.
    /// </summary>
    private static int FindRect(List<SketchRoomRect> rects, int room, bool horizontal,
                                float lineCoord, float center, float off)
    {
        float px = horizontal ? center : lineCoord + off;
        float py = horizontal ? lineCoord + off : center;
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            if (r.room != room) continue;
            if (px >= r.cx0 - 0.5f && px <= r.cx1 + 0.5f && py >= r.cy0 - 0.5f && py <= r.cy1 + 0.5f)
                return i;
        }
        return -1;
    }

    // ---------------------------------------------------------------------------------------------
    // Windows
    // ---------------------------------------------------------------------------------------------

    private static void Windows(SketchWallGraphResult graph, SketchWallGrid grid,
                                List<SketchRoomRect> rects, int stroke, List<SketchGap> gaps)
    {
        var cells = graph.cells;
        int minRun = Mathf.Max(6, Mathf.Max(4, 2 * stroke));

        for (int si = 0; si < grid.segs.Count; si++)
        {
            var seg = grid.segs[si];
            if (seg.dbl == null) continue;
            float lineCoord = seg.horizontal ? cells.ys[seg.line] : cells.xs[seg.line];
            float off = 0.5f * seg.thickness + 2f;

            int run = 0;
            for (int p = 0; p <= seg.Span; p++)
            {
                if (p < seg.Span && seg.dbl[p]) { run++; continue; }
                if (run >= minRun)
                {
                    float center = seg.s0 + p - 1 - 0.5f * (run - 1);
                    int sideA, sideB;
                    if (seg.horizontal)
                    {
                        sideA = cells.LabelAt(center, lineCoord - off);
                        sideB = cells.LabelAt(center, lineCoord + off);
                    }
                    else
                    {
                        sideA = cells.LabelAt(lineCoord - off, center);
                        sideB = cells.LabelAt(lineCoord + off, center);
                    }

                    // Only the exterior convention is trusted; an interior double line is a
                    // pass-through counter, a stated not-yet.
                    bool outsideOnA = sideA == SketchCellMap.OUTSIDE && sideB >= 0;
                    bool outsideOnB = sideB == SketchCellMap.OUTSIDE && sideA >= 0;
                    if (outsideOnA || outsideOnB)
                    {
                        int room = outsideOnA ? sideB : sideA;
                        int rect = FindRect(rects, room, seg.horizontal, lineCoord, center,
                                            outsideOnA ? +off : -off);
                        if (rect >= 0)
                            gaps.Add(new SketchGap
                            {
                                rectA = rect, rectB = -1,
                                edge = Edge(seg.horizontal, outsideOnA),
                                horizontal = seg.horizontal, line = seg.line,
                                center = center, widthPx = run,
                                window = true,
                            });
                    }
                }
                run = 0;
            }
        }
    }
}
