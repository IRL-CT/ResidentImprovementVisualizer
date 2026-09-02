using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A break in a wall that reads as an opening: the graph repair finds these FIRST-CLASS, as gaps
/// between segments on one wall line, before any room exists. Widths and centers come from the raw
/// jamb endpoints, not the snapped grid, so the scale anchor keeps its accuracy.
/// </summary>
public struct SketchDoorwayCandidate
{
    public bool horizontal;    // orientation of the wall the gap sits in
    public int line;           // line index in the grid
    public float g0, g1;       // the flanking wall endpoints along the wall, raw px; width = g1-g0-1
    public int jambA, jambB;   // flanking run lengths, px; -1 when the flank is a perpendicular wall
    public int thickness;      // the wall's thickness at the gap, px
}

public sealed class SketchWallGraphResult
{
    public SketchCellMap cells;
    public List<SketchDoorwayCandidate> doorways = new List<SketchDoorwayCandidate>();
    public List<SketchCoverRun>[] hCover, vCover;   // final per-line coverage, virtual walls included
    public int rung;                                 // which tolerance rung was accepted
    public bool clean;                               // false when even the last rung left leak signals
}

// Closes the wall graph, and in doing so FINDS THE DOORWAYS.
//
// A doorway IS a gap between two collinear wall segments, and a corner pen lift IS a wall endpoint
// that missed a perpendicular line: both are repairs of the same graph, so one pass owns both. This
// replaces the old pixel bridging, which demanded that a gap and both flanks share one exact pixel
// row or column: photographed jambs never do, and that was the single largest source of merged and
// vanished rooms.
//
// THE LADDER. Repair runs at increasing tolerances (1, 2, 3 strokes), each attempt from the
// pristine snapped input. After each attempt the cells are built and checked for LEAK SIGNALS:
// no bounded cell at all; a wall whose covered edges all separate a room from itself (a partition
// that divided nothing means an unclosed corner elsewhere); or a long real wall covering no cell
// edge (its corner never met). A clean rung is accepted; if none is, the last one stands and the
// caller is told, so the answer degrades to a warning rather than a merge.
//
// Determinism: lines in index order, runs sorted by start, candidate perpendiculars scanned in
// coordinate order with the nearest (then the smaller index) winning.
public static class SketchWallGraph
{
    public static SketchWallGraphResult Repair(SketchWallGrid grid, int stroke, int longEdge)
    {
        SketchWallGraphResult last = null;
        for (int rung = 1; rung <= 3; rung++)
        {
            last = Attempt(grid, stroke, longEdge, rung * stroke);
            last.rung = rung;
            if (last.clean) return last;
        }
        return last;
    }

    // ---------------------------------------------------------------------------------------------

    private static SketchWallGraphResult Attempt(SketchWallGrid grid, int stroke, int longEdge, int t)
    {
        float doorMin = 2f * stroke;
        float doorMax = 30f * stroke;
        var result = new SketchWallGraphResult();

        // Phase 1: per line, weld pen lifts and record collinear doorway gaps.
        var hRuns = Collinear(grid, horizontal: true, doorMin, doorMax, result.doorways);
        var vRuns = Collinear(grid, horizontal: false, doorMin, doorMax, result.doorways);

        // Phase 2: corner and T repair, each orientation against the OTHER's phase-1 runs, so the
        // two cannot chase each other. A near-miss endpoint welds to the line; a door-sized miss
        // with real wall beyond it is a door drawn hard against a corner.
        var hFinal = Clone(hRuns);
        var vFinal = Clone(vRuns);
        Corners(hFinal, grid.hLines, grid.vLines, vRuns, t, doorMin, doorMax,
                horizontal: true, result.doorways);
        Corners(vFinal, grid.vLines, grid.hLines, hRuns, t, doorMin, doorMax,
                horizontal: false, result.doorways);
        for (int i = 0; i < hFinal.Length; i++) MergeOverlaps(hFinal[i]);
        for (int i = 0; i < vFinal.Length; i++) MergeOverlaps(vFinal[i]);

        result.hCover = hFinal;
        result.vCover = vFinal;
        result.cells = SketchCellMap.Build(grid.vLines, grid.hLines, hFinal, vFinal, stroke);
        int maxTol = 3 * stroke;   // the last rung: what escalation could still repair
        result.clean = result.cells.roomCount > 0
                    && !LeakSignals(result.cells, hFinal, horizontal: true, stroke, longEdge, maxTol)
                    && !LeakSignals(result.cells, vFinal, horizontal: false, stroke, longEdge, maxTol);
        return result;
    }

    // ---------------------------------------------------------------------------------------------
    // Phase 1: collinear welds and doorways
    // ---------------------------------------------------------------------------------------------

    private static List<SketchCoverRun>[] Collinear(SketchWallGrid grid, bool horizontal,
                                                    float doorMin, float doorMax,
                                                    List<SketchDoorwayCandidate> doorways)
    {
        float[] lines = horizontal ? grid.hLines : grid.vLines;
        var runs = new List<SketchCoverRun>[lines.Length];
        for (int li = 0; li < lines.Length; li++) runs[li] = new List<SketchCoverRun>();

        // The line's segments, in span order (extraction order breaks the rare tie).
        var byLine = new List<int>[lines.Length];
        for (int i = 0; i < grid.segs.Count; i++)
        {
            var s = grid.segs[i];
            if (s.horizontal != horizontal) continue;
            (byLine[s.line] ?? (byLine[s.line] = new List<int>())).Add(i);
        }

        for (int li = 0; li < lines.Length; li++)
        {
            var idx = byLine[li];
            if (idx == null) continue;
            idx.Sort((a, b) =>
            {
                int byS = grid.segs[a].s0.CompareTo(grid.segs[b].s0);
                return byS != 0 ? byS : a.CompareTo(b);
            });

            var run = new SketchCoverRun { lo = grid.segs[idx[0]].s0, hi = grid.segs[idx[0]].s1,
                                           realPx = grid.segs[idx[0]].Span,
                                           thickness = grid.segs[idx[0]].thickness };
            int lastSpan = grid.segs[idx[0]].Span;

            for (int k = 1; k < idx.Count; k++)
            {
                var s = grid.segs[idx[k]];
                float gap = s.s0 - run.hi - 1;
                if (gap < doorMin)
                {
                    // A pen lift, or the shadow of a perpendicular wall crossing this one.
                }
                else if (gap <= doorMax)
                {
                    doorways.Add(new SketchDoorwayCandidate
                    {
                        horizontal = horizontal, line = li,
                        g0 = run.hi, g1 = s.s0,
                        jambA = lastSpan, jambB = s.Span,
                        thickness = Mathf.Max(run.thickness, s.thickness),
                    });
                }
                else
                {
                    // A genuinely open stretch: a corridor mouth, an open-plan boundary.
                    runs[li].Add(run);
                    run = new SketchCoverRun { lo = s.s0, hi = s.s1, realPx = s.Span,
                                               thickness = s.thickness };
                    lastSpan = s.Span;
                    continue;
                }
                if (s.s1 > run.hi) run.hi = s.s1;
                run.realPx += s.Span;
                if (s.thickness > run.thickness) run.thickness = s.thickness;
                lastSpan = s.Span;
            }
            runs[li].Add(run);
        }
        return runs;
    }

    // ---------------------------------------------------------------------------------------------
    // Phase 2: corners, Ts, and doors against corners
    // ---------------------------------------------------------------------------------------------

    private static void Corners(List<SketchCoverRun>[] runs, float[] ownLines, float[] perpLines,
                                List<SketchCoverRun>[] perpRuns, int t, float doorMin, float doorMax,
                                bool horizontal, List<SketchDoorwayCandidate> doorways)
    {
        for (int li = 0; li < runs.Length; li++)
        {
            float lineCoord = ownLines[li];
            var list = runs[li];
            for (int k = 0; k < list.Count; k++)
            {
                var run = list[k];
                run.lo = RepairEnd(run.lo, -1, lineCoord, perpLines, perpRuns, t, doorMin, doorMax,
                                   horizontal, li, run.realPx, run.thickness, doorways);
                run.hi = RepairEnd(run.hi, +1, lineCoord, perpLines, perpRuns, t, doorMin, doorMax,
                                   horizontal, li, run.realPx, run.thickness, doorways);
                list[k] = run;
            }
        }
    }

    /// <summary>
    /// One run endpoint. <paramref name="dir"/> is which way the run's OUTSIDE lies: -1 for the low
    /// end, +1 for the high end. Welds the endpoint to a perpendicular covering line within the
    /// tolerance (nearest wins, then the smaller line coordinate), or, failing that, records a
    /// door-against-a-corner when a covering perpendicular line sits a door's width away.
    /// </summary>
    private static float RepairEnd(float end, int dir, float lineCoord, float[] perpLines,
                                   List<SketchCoverRun>[] perpRuns, int t, float doorMin, float doorMax,
                                   bool horizontal, int li, int runRealPx, int runThickness,
                                   List<SketchDoorwayCandidate> doorways)
    {
        // Weld: the nearest covering perpendicular line within t, either side of the endpoint.
        int best = -1;
        float bestD = t + 0.001f;
        for (int j = 0; j < perpLines.Length; j++)
        {
            float d = Mathf.Abs(perpLines[j] - end);
            if (d < bestD && Covers(perpRuns[j], lineCoord, t))
            {
                bestD = d;
                best = j;
            }
        }
        if (best >= 0) return perpLines[best];

        // A door hard against a corner: the nearest covering perpendicular line a door's width
        // beyond the endpoint, in the direction the run does not go.
        for (int step = 0; step < perpLines.Length; step++)
        {
            // Walk outward from the endpoint: ascending distance is ascending (dir>0) or
            // descending (dir<0) line order.
            int j = dir > 0 ? IndexAbove(perpLines, end) + step
                            : IndexBelow(perpLines, end) - step;
            if (j < 0 || j >= perpLines.Length) break;
            float d = dir > 0 ? perpLines[j] - end : end - perpLines[j];
            if (d > doorMax) break;
            if (d < doorMin || d <= t) continue;
            int perpThickness = CoveringThickness(perpRuns[j], lineCoord, t);
            if (perpThickness < 0) continue;

            float face = perpLines[j] - dir * 0.5f * perpThickness;
            doorways.Add(new SketchDoorwayCandidate
            {
                horizontal = horizontal, line = li,
                g0 = dir > 0 ? end : face,
                g1 = dir > 0 ? face : end,
                jambA = dir > 0 ? runRealPx : -1,
                jambB = dir > 0 ? -1 : runRealPx,
                thickness = runThickness,
            });
            return perpLines[j];   // the virtual wall runs to the corner
        }

        return end;
    }

    private static int IndexAbove(float[] lines, float v)
    {
        for (int j = 0; j < lines.Length; j++) if (lines[j] > v) return j;
        return lines.Length;
    }

    private static int IndexBelow(float[] lines, float v)
    {
        for (int j = lines.Length - 1; j >= 0; j--) if (lines[j] < v) return j;
        return -1;
    }

    private static bool Covers(List<SketchCoverRun> runs, float coord, float t)
        => CoveringThickness(runs, coord, t) >= 0;

    /// <summary>The thickness of the first run covering the coordinate within t; -1 when none does.</summary>
    private static int CoveringThickness(List<SketchCoverRun> runs, float coord, float t)
    {
        if (runs == null) return -1;
        for (int i = 0; i < runs.Count; i++)
            if (runs[i].lo - t <= coord && coord <= runs[i].hi + t) return runs[i].thickness;
        return -1;
    }

    private static List<SketchCoverRun>[] Clone(List<SketchCoverRun>[] runs)
    {
        var copy = new List<SketchCoverRun>[runs.Length];
        for (int i = 0; i < runs.Length; i++) copy[i] = new List<SketchCoverRun>(runs[i]);
        return copy;
    }

    private static void MergeOverlaps(List<SketchCoverRun> runs)
    {
        runs.Sort((a, b) => a.lo.CompareTo(b.lo));
        for (int i = runs.Count - 1; i > 0; i--)
        {
            if (runs[i].lo > runs[i - 1].hi + 0.5f) continue;
            var merged = runs[i - 1];
            if (runs[i].hi > merged.hi) merged.hi = runs[i].hi;
            merged.realPx += runs[i].realPx;
            if (runs[i].thickness > merged.thickness) merged.thickness = runs[i].thickness;
            runs[i - 1] = merged;
            runs.RemoveAt(i);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Leak signals
    // ---------------------------------------------------------------------------------------------

    private static bool LeakSignals(SketchCellMap cells, List<SketchCoverRun>[] cover,
                                    bool horizontal, int stroke, int longEdge, int maxTol)
    {
        const float EPS = 1.5f;
        int longWall = 3 * SketchWallSegments.MajorMin(stroke, longEdge);
        float[] along = horizontal ? cells.xs : cells.ys;
        int edges = horizontal ? cells.nx : cells.ny;

        for (int li = 0; li < cover.Length; li++)
            for (int k = 0; k < cover[li].Count; k++)
            {
                var run = cover[li][k];
                int covered = 0;
                bool allSelf = true;
                bool coverable = false;
                for (int e = 0; e < edges; e++)
                {
                    if (run.lo - maxTol <= along[e] + EPS && run.hi + maxTol >= along[e + 1] - EPS)
                        coverable = true;
                    if (run.lo > along[e] + EPS || run.hi < along[e + 1] - EPS) continue;
                    covered++;
                    int a = SideLabel(cells, horizontal, li, e, -1);
                    int b = SideLabel(cells, horizontal, li, e, +1);
                    if (a != b || a < 0) allSelf = false;
                }
                // A wall that separates a room from itself everywhere it blocks, or a long real
                // wall blocking no edge it nearly covers: both mean a corner did not close. A run
                // no rung could stretch to an edge is annotation (a dimension line), not a leak.
                if (covered > 0 && allSelf) return true;
                if (covered == 0 && coverable && run.realPx >= longWall) return true;
            }
        return false;
    }

    /// <summary>The room label of the cell on one side of a line at one edge slot.</summary>
    private static int SideLabel(SketchCellMap cells, bool horizontal, int li, int e, int side)
    {
        if (horizontal)
        {
            int j = side < 0 ? li - 1 : li;
            return j < 0 || j >= cells.ny ? SketchCellMap.OUTSIDE : cells.cell[j * cells.nx + e];
        }
        int i = side < 0 ? li - 1 : li;
        return i < 0 || i >= cells.nx ? SketchCellMap.OUTSIDE : cells.cell[e * cells.nx + i];
    }
}
