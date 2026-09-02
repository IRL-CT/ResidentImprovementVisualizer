using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One maximal covered stretch of a wall line: real wall, welds, and the virtual wall across a
/// doorway, merged. What the cell map blocks with, and what the leak checks inspect.
/// </summary>
public struct SketchCoverRun
{
    public float lo, hi;     // along the wall, working px
    public int realPx;       // summed REAL segment span inside the run (virtual bridges excluded)
    public int thickness;    // the thickest member, px
}

// The rooms, as the bounded faces of the closed wall-line arrangement.
//
// The grid is COARSE AND EXACT: its cells are the intervals between consecutive wall lines, so a
// whole floor plan is a handful of cells, and whether a wall blocks the edge between two cells is a
// pure interval-coverage question with no pixels involved. A cell reachable from beyond the
// outermost lines without crossing a covered edge is OUTSIDE; every other connected group of cells
// is one room, already bounded by wall CENTERLINES, which is why no face-pushing stage exists any
// more: adjacent rooms share their wall's line by construction.
//
// Determinism: cells are flooded in row-major seed order, rooms labelled in first-cell scan order
// (which is what keeps "Room 1" the top-left room), and folds run in label order.
public sealed class SketchCellMap
{
    public const int OUTSIDE = -2;
    public const int FOLDED = -1;

    public float[] xs, ys;   // the wall lines: vertical (x) and horizontal (y), ascending
    public int nx, ny;       // cell counts per axis: lines minus one
    public int[] cell;       // ny rows by nx columns; OUTSIDE, FOLDED, or a room label
    public int roomCount;

    /// <summary>
    /// Builds the map. <paramref name="hCover"/> is indexed by horizontal line (one list per entry
    /// of <paramref name="ys"/>), <paramref name="vCover"/> by vertical line.
    /// </summary>
    public static SketchCellMap Build(float[] xs, float[] ys,
                                      List<SketchCoverRun>[] hCover, List<SketchCoverRun>[] vCover,
                                      int stroke)
    {
        const float EPS = 1.5f;

        var map = new SketchCellMap
        {
            xs = xs, ys = ys,
            nx = Mathf.Max(0, xs.Length - 1),
            ny = Mathf.Max(0, ys.Length - 1),
        };
        int nx = map.nx, ny = map.ny;
        map.cell = new int[nx * ny];
        if (nx == 0 || ny == 0) return map;

        // Blocked edges. blockedV[li * ny + j]: vertical line li blocks passage between column
        // li-1 and column li at row j (the outermost lines separate the border cells from OUTSIDE).
        var blockedV = new bool[xs.Length * ny];
        var blockedH = new bool[ys.Length * nx];
        for (int li = 0; li < xs.Length; li++)
            for (int j = 0; j < ny; j++)
                blockedV[li * ny + j] = Covers(vCover[li], ys[j], ys[j + 1], EPS);
        for (int li = 0; li < ys.Length; li++)
            for (int i = 0; i < nx; i++)
                blockedH[li * nx + i] = Covers(hCover[li], xs[i], xs[i + 1], EPS);

        // The outside: everything reachable from beyond the outermost lines.
        for (int i = 0; i < map.cell.Length; i++) map.cell[i] = int.MinValue;
        var queue = new int[nx * ny];
        int qn = 0;
        for (int j = 0; j < ny; j++)
        {
            if (!blockedV[0 * ny + j]) Seed(map.cell, queue, ref qn, j * nx + 0);
            if (!blockedV[nx * ny + j]) Seed(map.cell, queue, ref qn, j * nx + nx - 1);
        }
        for (int i = 0; i < nx; i++)
        {
            if (!blockedH[0 * nx + i]) Seed(map.cell, queue, ref qn, 0 * nx + i);
            if (!blockedH[ny * nx + i]) Seed(map.cell, queue, ref qn, (ny - 1) * nx + i);
        }
        Flood(map.cell, queue, qn, nx, ny, blockedV, blockedH, OUTSIDE);

        // The rooms, in first-cell scan order.
        int rooms = 0;
        for (int idx = 0; idx < map.cell.Length; idx++)
        {
            if (map.cell[idx] != int.MinValue) continue;
            map.cell[idx] = rooms;
            queue[0] = idx;
            Flood(map.cell, queue, 1, nx, ny, blockedV, blockedH, rooms);
            rooms++;
        }

        // Fold the thin strips: a "room" confined to less than a few strokes in one axis is a wall
        // channel or a hatch strip, never floor. Then relabel so room labels stay contiguous.
        float minSide = 3f * stroke;
        var minI = new int[rooms]; var maxI = new int[rooms];
        var minJ = new int[rooms]; var maxJ = new int[rooms];
        for (int r = 0; r < rooms; r++) { minI[r] = nx; maxI[r] = -1; minJ[r] = ny; maxJ[r] = -1; }
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int r = map.cell[j * nx + i];
                if (r < 0) continue;
                if (i < minI[r]) minI[r] = i;
                if (i > maxI[r]) maxI[r] = i;
                if (j < minJ[r]) minJ[r] = j;
                if (j > maxJ[r]) maxJ[r] = j;
            }
        var remap = new int[rooms];
        int kept = 0;
        for (int r = 0; r < rooms; r++)
        {
            bool thin = maxI[r] < 0
                     || xs[maxI[r] + 1] - xs[minI[r]] < minSide
                     || ys[maxJ[r] + 1] - ys[minJ[r]] < minSide;
            remap[r] = thin ? FOLDED : kept++;
        }
        if (kept < rooms)
            for (int i = 0; i < map.cell.Length; i++)
                if (map.cell[i] >= 0) map.cell[i] = remap[map.cell[i]];
        map.roomCount = kept;

        return map;
    }

    private static void Seed(int[] cell, int[] queue, ref int qn, int idx)
    {
        if (cell[idx] != int.MinValue) return;
        cell[idx] = OUTSIDE;
        queue[qn++] = idx;
    }

    /// <summary>BFS over cells crossing only unblocked edges, from the first qn queue entries.</summary>
    private static void Flood(int[] cell, int[] queue, int qn, int nx, int ny,
                              bool[] blockedV, bool[] blockedH, int label)
    {
        int head = 0, tail = qn;
        while (head < tail)
        {
            int idx = queue[head++];
            int i = idx % nx, j = idx / nx;

            if (i > 0 && !blockedV[i * ny + j] && cell[idx - 1] == int.MinValue)
            { cell[idx - 1] = label; queue[tail++] = idx - 1; }
            if (i < nx - 1 && !blockedV[(i + 1) * ny + j] && cell[idx + 1] == int.MinValue)
            { cell[idx + 1] = label; queue[tail++] = idx + 1; }
            if (j > 0 && !blockedH[j * nx + i] && cell[idx - nx] == int.MinValue)
            { cell[idx - nx] = label; queue[tail++] = idx - nx; }
            if (j < ny - 1 && !blockedH[(j + 1) * nx + i] && cell[idx + nx] == int.MinValue)
            { cell[idx + nx] = label; queue[tail++] = idx + nx; }
        }
    }

    private static bool Covers(List<SketchCoverRun> runs, float a, float b, float eps)
    {
        if (runs == null) return false;
        for (int i = 0; i < runs.Count; i++)
            if (runs[i].lo <= a + eps && runs[i].hi >= b - eps) return true;
        return false;
    }

    /// <summary>The label at a working-pixel position; OUTSIDE beyond the outermost lines.</summary>
    public int LabelAt(float px, float py)
    {
        if (nx == 0 || ny == 0) return OUTSIDE;
        if (px < xs[0] || px > xs[nx] || py < ys[0] || py > ys[ny]) return OUTSIDE;
        int i = Interval(xs, px);
        int j = Interval(ys, py);
        return cell[j * nx + i];
    }

    private static int Interval(float[] lines, float v)
    {
        // The last line whose coordinate is <= v, clamped into the cell range.
        int lo = 0, hi = lines.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (lines[mid] <= v) lo = mid; else hi = mid - 1;
        }
        return Mathf.Min(lo, lines.Length - 2);
    }

    // ---------------------------------------------------------------------------------------------
    // Rectangle partition
    // ---------------------------------------------------------------------------------------------

    public struct CellRect
    {
        public int room;
        public int i0, j0, i1, j1;   // cell index bounds, inclusive
    }

    /// <summary>
    /// Every room cut into rectangles by the row-major sweep: claim a cell, extend right along its
    /// row, then extend the block down while the whole column range still matches. Exact on any
    /// rectilinear cell set, two rectangles for an L, three for a U or a T, and deterministic
    /// because the sweep order is the scan order.
    /// </summary>
    public List<CellRect> Partition()
    {
        var rects = new List<CellRect>();
        var claimed = new bool[nx * ny];
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int label = cell[j * nx + i];
                if (label < 0 || claimed[j * nx + i]) continue;

                int ie = i;
                while (ie + 1 < nx && cell[j * nx + ie + 1] == label && !claimed[j * nx + ie + 1]) ie++;
                int je = j;
                while (je + 1 < ny)
                {
                    bool ok = true;
                    for (int q = i; q <= ie && ok; q++)
                        if (cell[(je + 1) * nx + q] != label || claimed[(je + 1) * nx + q]) ok = false;
                    if (!ok) break;
                    je++;
                }
                for (int jj = j; jj <= je; jj++)
                    for (int q = i; q <= ie; q++)
                        claimed[jj * nx + q] = true;
                rects.Add(new CellRect { room = label, i0 = i, j0 = j, i1 = ie, j1 = je });
            }
        return rects;
    }
}
