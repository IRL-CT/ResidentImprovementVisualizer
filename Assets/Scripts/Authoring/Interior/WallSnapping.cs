using System.Collections.Generic;
using UnityEngine;

// Where the wall tool's cursor actually lands.
//
// Tracing a plan is a long sequence of clicks, and every one of them wants to land somewhere exact:
// on the corner you just drew, on the face of a wall already there, square to the last segment. Doing
// that badly produces a model full of 0.003 m gaps that look fine and measure wrong, which defeats
// the whole point of a dimensionally accurate tool.
//
// Candidates are tried in strict priority order, most specific first:
//     1. an existing wall ENDPOINT. Closes corners exactly, so junctions actually weld
//     2. the axis-locked run CROSSING a wall: a square join lands where the run meets the wall,
//        not at the perpendicular foot of wherever the cursor hovers; a crossing within
//        WallLinker.MinSeg of the wall's end welds to its corner (that cut would be refused anyway)
//     3. a point ON an existing wall. T-junctions and splitting a run
//     4. LEVEL with a parallel wall's end: the open side of a C stops flush across the gap,
//        with the guide endpoint reported so the overlay can draw the reason
//     5. an AXIS from the previous point: the 90°/45° lock that keeps rooms rectangular
//     6. the GRID: a fallback so free space is still tidy
//
// 2 and 4 need a locked axis and an anchor, so a caller with no run in progress (MeasureTool) never
// sees them. Neither is grid-rounded: 2 lies exactly on the target centerline (the junction welds
// with zero offset) and 4 is exactly level with the endpoint (length equality is the whole point).
//
// Holding Shift disables all of it (`Options.enabled = false`). That matches the convention CLAUDE.md
// already documents for the existing fence tool, where Shift means "no snapping at all": the
// opposite of Shift in the transform tools, but consistent within the drawing tools.
public static class WallSnapping
{
    public enum SnapKind { None, Endpoint, OnWall, AxisOnWall, Align, Axis, Grid }

    public struct Result
    {
        public Vector2 point;
        public SnapKind kind;
        public string label;         // short hint for the cursor overlay, e.g. "corner", "90°"
        public string targetWallId;  // set for Endpoint, OnWall, AxisOnWall and Align
        public Vector2 guideFrom;    // Align only: the endpoint the run is level with
        public bool hasGuide;
    }

    public struct Options
    {
        public bool enabled;         // false => pass the raw point straight through (Shift held)
        public float endpointRadius; // world meters; generous, corners matter most
        public float wallRadius;     // world meters
        public float alignRadius;    // world meters, along the run; <= 0 disables alignment
        public float gridSize;       // <= 0 disables the grid
        public bool axisLock;
        public float axisStepDeg;    // 90 for orthogonal only, 45 to allow diagonals

        public static Options Default => new Options
        {
            enabled = true,
            endpointRadius = 0.35f,
            wallRadius = 0.20f,
            alignRadius = 0.25f,     // between wallRadius and endpointRadius: findable, not grabby
            gridSize = 0.05f,        // 50 mm. Fine enough not to fight a traced sketch
            axisLock = true,
            axisStepDeg = 45f,
        };
    }

    /// <summary>
    /// Snaps <paramref name="raw"/> (a world XZ point) for the wall tool.
    /// </summary>
    /// <param name="anchor">The previous point in the run, for the axis lock. Null on the first click.</param>
    /// <param name="ignoreWallId">A wall to exclude: the one currently being dragged.</param>
    public static Result Snap(Vector2 raw, LevelDef level, Vector2? anchor, Options opts,
                              string ignoreWallId = null)
    {
        if (!opts.enabled)
            return new Result { point = raw, kind = SnapKind.None };

        // 1. Existing endpoints.
        if (TryEndpoint(raw, level, opts.endpointRadius, ignoreWallId, out Result ep)) return ep;

        // 2: where the axis-locked run crosses an existing wall. Before the plain on-wall foot on
        // purpose: the foot lands at an arbitrary point of the wall, which is exactly the snap that
        // used to defeat drawing a square join into it.
        if (opts.axisLock && anchor.HasValue &&
            TryAxisIntersect(raw, level, anchor.Value, opts, ignoreWallId, out Result ai)) return ai;

        // 3: a point along an existing wall centerline.
        if (TryOnWall(raw, level, opts.wallRadius, ignoreWallId, out Result ow)) return ow;

        // 4. Level with a parallel wall's endpoint, across the gap.
        if (opts.axisLock && anchor.HasValue &&
            TryAlign(raw, level, anchor.Value, opts, ignoreWallId, out Result al)) return al;

        // 5. Axis lock relative to the previous point.
        if (opts.axisLock && anchor.HasValue &&
            TryAxis(raw, anchor.Value, opts, out Result ax)) return ax;

        // 6. Plain grid.
        if (opts.gridSize > ResidenceConventions.EPS)
        {
            var g = new Vector2(
                Mathf.Round(raw.x / opts.gridSize) * opts.gridSize,
                Mathf.Round(raw.y / opts.gridSize) * opts.gridSize);
            return new Result { point = g, kind = SnapKind.Grid, label = "grid" };
        }

        return new Result { point = raw, kind = SnapKind.None };
    }

    // ---------------------------------------------------------------------------------------

    private static bool TryEndpoint(Vector2 raw, LevelDef level, float radius, string ignoreWallId,
                                    out Result result)
    {
        result = default;
        if (level?.walls == null || radius <= 0f) return false;

        float bestSq = radius * radius;
        bool found = false;

        foreach (var w in level.walls)
        {
            if (w == null || w.id == ignoreWallId) continue;
            if (w.a == null || w.b == null || w.a.Length < 2 || w.b.Length < 2) continue;

            var a = new Vector2(w.a[0], w.a[1]);
            var b = new Vector2(w.b[0], w.b[1]);

            float da = (raw - a).sqrMagnitude;
            if (da < bestSq)
            {
                bestSq = da; found = true;
                result = new Result { point = a, kind = SnapKind.Endpoint, label = "corner", targetWallId = w.id };
            }

            float db = (raw - b).sqrMagnitude;
            if (db < bestSq)
            {
                bestSq = db; found = true;
                result = new Result { point = b, kind = SnapKind.Endpoint, label = "corner", targetWallId = w.id };
            }
        }

        return found;
    }

    // Where the snapped axis from the anchor crosses a wall. The crossing is exact: never
    // grid-rounded, so the junction WallLinker then makes lands on the centerline with zero offset,
    // bit-weldable within WallMeshBuilder.Near. A crossing within WallLinker.MinSeg of the wall's end
    // is a cut SurvivingCuts would refuse; the wall's own corner is the junction that actually welds
    // there, so the snap returns that endpoint instead.
    private static bool TryAxisIntersect(Vector2 raw, LevelDef level, Vector2 anchor, Options opts,
                                         string ignoreWallId, out Result result)
    {
        result = default;
        if (level?.walls == null || opts.endpointRadius <= 0f) return false;
        if (!SnapAxis(raw, anchor, opts, out Vector2 dir, out float projected, out float ang)) return false;

        // The ray reaches endpointRadius past the cursor, so a crossing just beyond it still
        // catches. A crossing accepted at the very tip would clamp off the wall, and is also at
        // least endpointRadius from the cursor, so the distance gate below rejects it by itself.
        Vector2 far = anchor + dir * (projected + opts.endpointRadius);

        float bestD = opts.endpointRadius;
        bool found = false;

        foreach (var w in level.walls)
        {
            if (w == null || w.id == ignoreWallId) continue;
            if (!Segments.TryEnds(w, out Vector2 a, out Vector2 b)) continue;
            if (!Segments.Intersect(anchor, far, a, b, WallLinker.ContactEps,
                                    out _, out _, out Vector2 p)) continue;
            if (Vector2.Dot(p - anchor, dir) <= ResidenceConventions.EPS) continue;   // behind the run

            float d = Vector2.Distance(raw, p);
            if (d >= bestD) continue;

            if (Vector2.Distance(p, a) <= WallLinker.MinSeg)
                result = new Result { point = a, kind = SnapKind.Endpoint, label = "corner", targetWallId = w.id };
            else if (Vector2.Distance(p, b) <= WallLinker.MinSeg)
                result = new Result { point = b, kind = SnapKind.Endpoint, label = "corner", targetWallId = w.id };
            else
                result = new Result
                {
                    point = p,
                    kind = SnapKind.AxisOnWall,
                    label = AngleLabel(ang) + " on wall",
                    targetWallId = w.id,
                };
            bestD = d;
            found = true;
        }

        return found;
    }

    private static bool TryOnWall(Vector2 raw, LevelDef level, float radius, string ignoreWallId,
                                  out Result result)
    {
        result = default;
        if (level?.walls == null || radius <= 0f) return false;

        float bestSq = radius * radius;
        bool found = false;

        foreach (var w in level.walls)
        {
            if (w == null || w.id == ignoreWallId) continue;
            if (!Segments.TryEnds(w, out Vector2 a, out Vector2 b)) continue;

            Vector2 foot = Segments.ClosestOn(raw, a, b, out _);
            float d = (raw - foot).sqrMagnitude;
            if (d < bestSq)
            {
                bestSq = d; found = true;
                result = new Result { point = foot, kind = SnapKind.OnWall, label = "on wall", targetWallId = w.id };
            }
        }

        return found;
    }

    // Level with a parallel wall's endpoint: drawing the open side of a C, the run stops flush with
    // the far end of the wall across the gap. The gap itself is deliberately unbounded. It being
    // the whole room's width is the use case. The point stays on the locked axis and is exactly at
    // the endpoint's station, never grid-rounded: length equality is the point of the snap.
    private static bool TryAlign(Vector2 raw, LevelDef level, Vector2 anchor, Options opts,
                                 string ignoreWallId, out Result result)
    {
        result = default;
        if (level?.walls == null || opts.alignRadius <= 0f) return false;
        if (!SnapAxis(raw, anchor, opts, out Vector2 dir, out float projected, out _)) return false;

        float best = opts.alignRadius;
        bool found = false;

        foreach (var w in level.walls)
        {
            if (w == null || w.id == ignoreWallId) continue;
            if (!Segments.TryEnds(w, out Vector2 a, out Vector2 b)) continue;
            // The one "these are parallel" threshold the project already has.
            if (Segments.SinBetween(dir, b - a) > WallLinker.MinJunctionSin) continue;

            for (int e = 0; e < 2; e++)
            {
                Vector2 end = e == 0 ? a : b;
                float s = Vector2.Dot(end - anchor, dir);
                if (s <= ResidenceConventions.EPS) continue;              // behind the run
                float d = Mathf.Abs(projected - s);
                if (d >= best) continue;

                best = d;
                found = true;
                result = new Result
                {
                    point = anchor + dir * s,
                    kind = SnapKind.Align,
                    label = "aligned",
                    targetWallId = w.id,
                    guideFrom = end,
                    hasGuide = true,
                };
            }
        }

        return found;
    }

    private static bool TryAxis(Vector2 raw, Vector2 anchor, Options opts, out Result result)
    {
        result = default;
        if (!SnapAxis(raw, anchor, opts, out Vector2 dir, out float projected, out float ang)) return false;

        if (opts.gridSize > ResidenceConventions.EPS)
            projected = Mathf.Round(projected / opts.gridSize) * opts.gridSize;

        result = new Result
        {
            point = anchor + dir * projected,
            kind = SnapKind.Axis,
            label = AngleLabel(ang),
        };
        return true;
    }

    // The axis lock's shared half: the snapped direction from the anchor and the cursor's
    // perpendicular projection onto it. Projection rather than preserving the raw length: the point
    // tracks under the cursor instead of shooting past it, which is what makes the lock feel like a
    // guide rather than a fight. With a 45° step the worst-case shortening is cos(22.5°). One
    // implementation on purpose: the intersection and alignment candidates must agree with the
    // plain axis lock about which axis is meant, or the snap flickers between rays.
    private static bool SnapAxis(Vector2 raw, Vector2 anchor, Options opts,
                                 out Vector2 dir, out float projected, out float snappedAngleDeg)
    {
        dir = default; projected = 0f; snappedAngleDeg = 0f;

        Vector2 d = raw - anchor;
        float len = d.magnitude;
        if (len <= ResidenceConventions.EPS) return false;

        float step = opts.axisStepDeg > 1f ? opts.axisStepDeg : 90f;
        float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        snappedAngleDeg = Mathf.Round(ang / step) * step;
        float deltaRad = (snappedAngleDeg - ang) * Mathf.Deg2Rad;

        projected = len * Mathf.Cos(deltaRad);
        if (projected <= ResidenceConventions.EPS) return false;

        float rad = snappedAngleDeg * Mathf.Deg2Rad;
        dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        return true;
    }

    private static string AngleLabel(float deg)
        => Mathf.RoundToInt(Mathf.Repeat(deg, 360f)) + "°";
}
