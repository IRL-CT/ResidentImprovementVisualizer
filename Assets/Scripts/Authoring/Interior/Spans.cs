using System.Collections.Generic;
using UnityEngine;

// 1-D interval algebra over half-open runs, each stored as a Vector2 (lo, hi) in meters.
//
// Union and Split are moved VERBATIM out of PlanBuilder, where they were private and where they are
// the whole of the wall derivation: room edges are grouped by line, unioned so a shared edge collapses
// to one wall, then re-split at every point a perpendicular line touches so every T-junction and
// crossing has coincident endpoints, which is what WallMeshBuilder.ComputeExtensions needs to weld a
// corner instead of leaving a ~57 mm notch.
//
// They moved because the same algebra is needed at EDIT time and PlanBuilder is authoring-time only:
// WallLinker splits a wall at a set of junction arc-positions, and the rectangle room stamp asks
// "which parts of this edge are not already covered by a neighbor's wall" so that stamping a room
// against an existing one SHARES its wall rather than doubling it. Subtract is what answers that.
//
// TOL is PlanBuilder's, unchanged. It is deliberately coarser than HomeConventions.EPS: these are
// authored coordinates quantised to a 1 mm grid, and two runs meeting within 2 mm are meeting.
public static class Spans
{
    public const float TOL = 0.002f;

    /// <summary>
    /// Merges overlapping and touching runs. Sorts <paramref name="spans"/> in place: the caller
    /// owns the list and PlanBuilder has always relied on that.
    /// </summary>
    public static List<Vector2> Union(List<Vector2> spans)
    {
        spans.Sort((p, q) => p.x.CompareTo(q.x));
        var merged = new List<Vector2>();
        foreach (var s in spans)
        {
            if (merged.Count > 0 && s.x <= merged[merged.Count - 1].y + TOL)
            {
                var last = merged[merged.Count - 1];
                merged[merged.Count - 1] = new Vector2(last.x, Mathf.Max(last.y, s.y));
            }
            else merged.Add(s);
        }
        return merged;
    }

    /// <summary>
    /// Cuts <paramref name="run"/> at every break strictly inside it. Breaks at or beyond either end
    /// are ignored (a junction on a run's own endpoint is a shared corner, not a split) and pieces
    /// shorter than TOL are never emitted.
    /// </summary>
    public static List<Vector2> Split(Vector2 run, List<float> breaks)
    {
        var cuts = new List<float>();
        foreach (float b in breaks)
            if (b > run.x + TOL && b < run.y - TOL) cuts.Add(b);

        cuts.Sort();

        var pieces = new List<Vector2>();
        float start = run.x;
        foreach (float c in cuts)
        {
            if (c - start > TOL) pieces.Add(new Vector2(start, c));
            start = c;
        }
        if (run.y - start > TOL) pieces.Add(new Vector2(start, run.y));
        return pieces;
    }

    /// <summary>
    /// The parts of <paramref name="run"/> left uncovered by <paramref name="covered"/>. Returns an
    /// empty list when the run is fully covered, which is exactly the signal the room stamp needs to
    /// emit no wall at all along an edge its neighbor already owns.
    /// </summary>
    public static List<Vector2> Subtract(Vector2 run, List<Vector2> covered)
    {
        var gaps = new List<Vector2>();
        if (run.y - run.x <= TOL) return gaps;

        if (covered == null || covered.Count == 0)
        {
            gaps.Add(run);
            return gaps;
        }

        // Union first so overlapping cover runs cannot leave a phantom gap between them.
        var merged = Union(new List<Vector2>(covered));

        float start = run.x;
        foreach (var c in merged)
        {
            if (c.y <= start + TOL) continue;      // entirely behind the cursor
            if (c.x >= run.y - TOL) break;         // entirely past the end; merged is sorted

            if (c.x - start > TOL) gaps.Add(new Vector2(start, c.x));
            start = Mathf.Max(start, c.y);
            if (start >= run.y - TOL) return gaps;
        }
        if (run.y - start > TOL) gaps.Add(new Vector2(start, run.y));
        return gaps;
    }

    /// <summary>True when <paramref name="spans"/> leaves no gap in <paramref name="run"/>.</summary>
    public static bool Covers(List<Vector2> spans, Vector2 run) => Subtract(run, spans).Count == 0;

    /// <summary>Total covered length, with overlaps counted once.</summary>
    public static float Length(List<Vector2> spans)
    {
        if (spans == null || spans.Count == 0) return 0f;
        float total = 0f;
        foreach (var s in Union(new List<Vector2>(spans))) total += s.y - s.x;
        return total;
    }
}
