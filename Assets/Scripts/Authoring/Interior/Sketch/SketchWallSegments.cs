using System.Collections.Generic;
using UnityEngine;

// Reads the cleaned wall mask into axis-aligned wall SEGMENTS with measured centerlines, and snaps
// their centerlines onto shared wall LINES.
//
// CROSSINGS CHAINED ALONG THE SPAN, NOT PER-PIXEL RUN LENGTHS. Each scanline perpendicular to a
// wall crosses it as one short dark group: a solid stroke, or two thin lines with a light channel
// between them (the double-line window convention, and a double-line wall, both land here for
// free). Chaining those crossings across consecutive scanlines follows a wobbly hand-drawn stroke
// wherever it wanders, which a fixed per-pixel run threshold does not: at the crest of a wobble the
// straight runs get short although the wall is perfectly continuous. The chain's centerline is the
// lower median of its per-scanline centers, so local damage (a label touching the wall, a door
// swing fragment) cannot move it.
//
// TWO TIERS. A MAJOR segment is long enough to establish a wall line by itself. A MINOR one (down
// to two strokes) is kept only if it lies on a line the majors established: that is what preserves
// the short jamb beside a door drawn near a corner without letting every text dash mint a wall.
//
// Determinism: scanlines advance in index order, crossings top-down/left-right, chains match
// first-fit in list order, every median is the lower median of a sorted copy, ties break to the
// smaller index. Centerlines are carried in HALF-PIXEL integers (center2 = lo + hi) so medians are
// exact.
public struct WallSeg
{
    public bool horizontal;
    public int line;         // index into SketchWallGrid.hLines / vLines; -1 until Snap assigns one
    public int center2;      // measured centerline in half pixels: lower median over the span
    public int s0, s1;       // span along the wall, inclusive, working pixels
    public int thickness;    // lower median crossing extent, px
    public bool major;       // long enough to establish a wall line on its own
    public bool[] dbl;       // per-position double-line flag, index 0 == s0; null when never set

    public int Span => s1 - s0 + 1;
    public float Center => center2 * 0.5f;
}

/// <summary>The snapped wall lines of the drawing, and every segment assigned to one.</summary>
public sealed class SketchWallGrid
{
    public float[] hLines = System.Array.Empty<float>();   // y centerlines, ascending
    public float[] vLines = System.Array.Empty<float>();   // x centerlines, ascending
    public List<WallSeg> segs = new List<WallSeg>();       // extraction order, line >= 0 for all
}

public static class SketchWallSegments
{
    /// <summary>How far along the wall a chain must reach to establish a line: the major threshold.</summary>
    public static int MajorMin(int stroke, int longEdge) => Mathf.Max(6 * stroke, longEdge / 60);

    // ---------------------------------------------------------------------------------------------
    // Extraction
    // ---------------------------------------------------------------------------------------------

    private struct Crossing
    {
        public int lo, hi;   // extent across the wall, inclusive
        public int runs;     // dark runs in the group; 2+ marks the double-line convention
    }

    private sealed class Chain
    {
        public int s0, lastPos;
        public int lastLo, lastHi;
        public List<int> center2s = new List<int>();
        public List<int> extents = new List<int>();
        public List<bool> dbls = new List<bool>();
    }

    /// <summary>Every horizontal then every vertical wall segment of the mask, in scan order.</summary>
    public static List<WallSeg> Extract(bool[] wall, int w, int h, int stroke)
    {
        int longEdge = Mathf.Max(w, h);
        int lMin = MajorMin(stroke, longEdge);
        int minorMin = 2 * stroke;
        var segs = new List<WallSeg>();
        ExtractOriented(wall, w, h, stroke, lMin, minorMin, horizontal: true, segs);
        ExtractOriented(wall, w, h, stroke, lMin, minorMin, horizontal: false, segs);
        return segs;
    }

    private static void ExtractOriented(bool[] wall, int w, int h, int stroke, int lMin, int minorMin,
                                        bool horizontal, List<WallSeg> segs)
    {
        int chainLen = horizontal ? w : h;   // positions along the wall
        int crossLen = horizontal ? h : w;   // depth across the wall
        int thinCap = 2 * stroke;            // a run this thin may be one pane of a double line
        int chanCap = 2 * stroke + 1;        // the light channel a double line may hold
        int extCap = 6 * stroke;             // wider than any wall: a perpendicular wall or a blob

        var open = new List<Chain>();
        var still = new List<Chain>();
        var crossings = new List<Crossing>();

        for (int a = 0; a < chainLen; a++)
        {
            // 1. The crossings of this scanline: dark runs, with thin runs grouped across short
            //    light channels so a double line reads as ONE crossing centred mid-channel.
            crossings.Clear();
            int b = 0;
            while (b < crossLen)
            {
                if (!Dark(wall, w, horizontal, a, b)) { b++; continue; }
                int runLo = b;
                while (b < crossLen && Dark(wall, w, horizontal, a, b)) b++;
                int lo = runLo, hi = b - 1, runs = 1;
                int lastRunLen = hi - lo + 1;
                while (lastRunLen <= thinCap)
                {
                    int c = b;
                    while (c < crossLen && !Dark(wall, w, horizontal, a, c)) c++;
                    if (c >= crossLen || c - b > chanCap) break;
                    int nextLo = c;
                    while (c < crossLen && Dark(wall, w, horizontal, a, c)) c++;
                    int nextLen = c - nextLo;
                    if (nextLen > thinCap) break;   // too thick to be a pane; starts its own crossing
                    hi = c - 1; runs++; lastRunLen = nextLen; b = c;
                }
                if (hi - lo + 1 <= extCap)
                    crossings.Add(new Crossing { lo = lo, hi = hi, runs = runs });
            }

            // 2. Continue chains: each crossing takes the first still-open chain it overlaps.
            for (int ci = 0; ci < crossings.Count; ci++)
            {
                var cr = crossings[ci];
                int pick = -1;
                for (int k = 0; k < open.Count; k++)
                {
                    var ch = open[k];
                    if (ch.lastPos != a - 1) continue;   // already continued, or a fresh chain
                    if (cr.lo <= ch.lastHi && ch.lastLo <= cr.hi) { pick = k; break; }
                }
                Chain target;
                if (pick >= 0) target = open[pick];
                else { target = new Chain { s0 = a }; open.Add(target); }
                target.lastPos = a;
                target.lastLo = cr.lo;
                target.lastHi = cr.hi;
                target.center2s.Add(cr.lo + cr.hi);
                target.extents.Add(cr.hi - cr.lo + 1);
                target.dbls.Add(cr.runs >= 2);
            }

            // 3. Close every chain the scanline did not continue.
            still.Clear();
            for (int k = 0; k < open.Count; k++)
            {
                if (open[k].lastPos == a) still.Add(open[k]);
                else Emit(open[k], stroke, lMin, minorMin, horizontal, segs);
            }
            var t = open; open = still; still = t;
        }
        for (int k = 0; k < open.Count; k++)
            Emit(open[k], stroke, lMin, minorMin, horizontal, segs);
    }

    private static bool Dark(bool[] wall, int w, bool horizontal, int a, int b)
        => horizontal ? wall[b * w + a] : wall[a * w + b];

    private static void Emit(Chain c, int stroke, int lMin, int minorMin, bool horizontal,
                             List<WallSeg> segs)
        => EmitRange(c, 0, c.lastPos - c.s0, stroke, lMin, minorMin, horizontal, segs);

    private static void EmitRange(Chain c, int i0, int i1, int stroke, int lMin, int minorMin,
                                  bool horizontal, List<WallSeg> segs)
    {
        int span = i1 - i0 + 1;
        if (span < minorMin) return;

        var centers = new int[span];
        for (int i = 0; i < span; i++) centers[i] = c.center2s[i0 + i];
        System.Array.Sort(centers);
        // A wall wanders a little; a diagonal line drifts by its whole length. The cap between them
        // scales with the span so residual skew never trips it. A chain past the cap is not thrown
        // away whole: a bifold panel drawn touching its jamb drags the wall's chain into the
        // diagonal, so the chain is split at its largest center step and each side judged again.
        // The straight wall pieces survive; the panel legs still fail here and vanish.
        if (centers[span - 1] - centers[0] > 4 * stroke + span / 6)
        {
            int cut = -1, largest = 0;
            for (int i = i0; i < i1; i++)
            {
                int step = Mathf.Abs(c.center2s[i + 1] - c.center2s[i]);
                if (step > largest) { largest = step; cut = i; }
            }
            if (cut < 0) return;   // unreachable: a positive drift always has a positive step
            EmitRange(c, i0, cut, stroke, lMin, minorMin, horizontal, segs);
            EmitRange(c, cut + 1, i1, stroke, lMin, minorMin, horizontal, segs);
            return;
        }

        var extents = new int[span];
        for (int i = 0; i < span; i++) extents[i] = c.extents[i0 + i];
        System.Array.Sort(extents);

        bool any = false;
        for (int i = 0; i < span && !any; i++) any = c.dbls[i0 + i];
        bool[] dbl = null;
        if (any)
        {
            dbl = new bool[span];
            for (int i = 0; i < span; i++) dbl[i] = c.dbls[i0 + i];
        }

        segs.Add(new WallSeg
        {
            horizontal = horizontal,
            line = -1,
            center2 = centers[(span - 1) / 2],
            s0 = c.s0 + i0,
            s1 = c.s0 + i1,
            thickness = Mathf.Max(1, extents[(span - 1) / 2]),
            major = span >= lMin,
            dbl = dbl,
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Snapping onto wall lines
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Clusters the MAJOR segments' centerlines into wall lines (single linkage with a width cap,
    /// the shape SketchRegularizer.Cluster keeps for the same reason: pure single linkage chains),
    /// then lets minors join a line the majors established, or drops them. Segments keep their
    /// extraction order; a dropped segment simply does not appear.
    /// </summary>
    public static SketchWallGrid Snap(List<WallSeg> segs, int stroke)
    {
        var grid = new SketchWallGrid();
        int tol2 = 4 * stroke;             // 2 strokes in px: far under any genuine wall separation
        int widthCap2 = 6 * stroke;        // 1.5x the tolerance: the guard against chaining

        grid.hLines = AssignLines(segs, horizontal: true, tol2, widthCap2);
        grid.vLines = AssignLines(segs, horizontal: false, tol2, widthCap2);

        for (int i = 0; i < segs.Count; i++)
            if (segs[i].line >= 0) grid.segs.Add(segs[i]);
        return grid;
    }

    private static float[] AssignLines(List<WallSeg> segs, bool horizontal, int tol2, int widthCap2)
    {
        // The majors, sorted by centerline then original index.
        var order = new List<int>();
        for (int i = 0; i < segs.Count; i++)
            if (segs[i].horizontal == horizontal && segs[i].major) order.Add(i);
        order.Sort((a, b) =>
        {
            int byC = segs[a].center2.CompareTo(segs[b].center2);
            return byC != 0 ? byC : a.CompareTo(b);
        });
        if (order.Count == 0)
        {
            // No lines: every minor of this orientation is dropped too.
            for (int i = 0; i < segs.Count; i++)
                if (segs[i].horizontal == horizontal) { var s = segs[i]; s.line = -1; segs[i] = s; }
            return System.Array.Empty<float>();
        }

        var reps = new List<int>();        // line centerline, half px
        int start = 0;
        for (int i = 1; i <= order.Count; i++)
        {
            bool split = i == order.Count
                      || segs[order[i]].center2 - segs[order[i - 1]].center2 > tol2
                      || segs[order[i]].center2 - segs[order[start]].center2 > widthCap2;
            if (!split) continue;

            int line = reps.Count;
            reps.Add(segs[order[start + (i - start - 1) / 2]].center2);   // lower median member
            for (int k = start; k < i; k++)
            {
                var s = segs[order[k]];
                s.line = line;
                segs[order[k]] = s;
            }
            start = i;
        }

        // Minors join the nearest line within the tolerance, or leave.
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (s.horizontal != horizontal || s.major) continue;
            int best = -1, bestD = tol2 + 1;
            for (int r = 0; r < reps.Count; r++)
            {
                int d = Mathf.Abs(s.center2 - reps[r]);
                if (d < bestD) { bestD = d; best = r; }
            }
            s.line = bestD <= tol2 ? best : -1;
            segs[i] = s;
        }

        var lines = new float[reps.Count];
        for (int r = 0; r < reps.Count; r++) lines[r] = reps[r] * 0.5f;
        return lines;
    }
}
