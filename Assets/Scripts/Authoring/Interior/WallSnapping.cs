using System.Collections.Generic;
using UnityEngine;

// Where the wall tool's cursor actually lands.
//
// Tracing a plan is a long sequence of clicks, and every one of them wants to land somewhere exact:
// on the corner you just drew, on the face of a wall already there, square to the last segment. Doing
// that badly produces a model full of 0.003 m gaps that look fine and measure wrong — which defeats
// the whole point of a dimensionally accurate tool.
//
// Candidates are tried in strict priority order, most specific first:
//     1. an existing wall ENDPOINT      — closes corners exactly, so junctions actually weld
//     2. a point ON an existing wall    — T-junctions, and splitting a run
//     3. an AXIS from the previous point— the 90°/45° lock that keeps rooms rectangular
//     4. the GRID                       — a fallback so free space is still tidy
//
// Holding Shift disables all of it (`Options.enabled = false`). That matches the convention CLAUDE.md
// already documents for the existing fence tool, where Shift means "no snapping at all" — the
// opposite of Shift in the transform tools, but consistent within the drawing tools.
public static class WallSnapping
{
    public enum SnapKind { None, Endpoint, OnWall, Axis, Grid }

    public struct Result
    {
        public Vector2 point;
        public SnapKind kind;
        public string label;         // short hint for the cursor overlay, e.g. "corner", "90°"
        public string targetWallId;  // set for Endpoint and OnWall
    }

    public struct Options
    {
        public bool enabled;         // false => pass the raw point straight through (Shift held)
        public float endpointRadius; // world meters; generous, corners matter most
        public float wallRadius;     // world meters
        public float gridSize;       // <= 0 disables the grid
        public bool axisLock;
        public float axisStepDeg;    // 90 for orthogonal only, 45 to allow diagonals

        public static Options Default => new Options
        {
            enabled = true,
            endpointRadius = 0.35f,
            wallRadius = 0.20f,
            gridSize = 0.05f,        // 50 mm — fine enough not to fight a traced sketch
            axisLock = true,
            axisStepDeg = 45f,
        };
    }

    /// <summary>
    /// Snaps <paramref name="raw"/> (a world XZ point) for the wall tool.
    /// </summary>
    /// <param name="anchor">The previous point in the run, for the axis lock. Null on the first click.</param>
    /// <param name="ignoreWallId">A wall to exclude — the one currently being dragged.</param>
    public static Result Snap(Vector2 raw, LevelDef level, Vector2? anchor, Options opts,
                              string ignoreWallId = null)
    {
        if (!opts.enabled)
            return new Result { point = raw, kind = SnapKind.None };

        // 1 — existing endpoints.
        if (TryEndpoint(raw, level, opts.endpointRadius, ignoreWallId, out Result ep)) return ep;

        // 2 — a point along an existing wall centerline.
        if (TryOnWall(raw, level, opts.wallRadius, ignoreWallId, out Result ow)) return ow;

        // 3 — axis lock relative to the previous point.
        if (opts.axisLock && anchor.HasValue &&
            TryAxis(raw, anchor.Value, opts, out Result ax)) return ax;

        // 4 — plain grid.
        if (opts.gridSize > HomeConventions.EPS)
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
            if (w.a == null || w.b == null || w.a.Length < 2 || w.b.Length < 2) continue;

            var a = new Vector2(w.a[0], w.a[1]);
            var b = new Vector2(w.b[0], w.b[1]);
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            if (lenSq <= HomeConventions.EPS) continue;

            float t = Mathf.Clamp01(Vector2.Dot(raw - a, ab) / lenSq);
            Vector2 foot = a + ab * t;

            float d = (raw - foot).sqrMagnitude;
            if (d < bestSq)
            {
                bestSq = d; found = true;
                result = new Result { point = foot, kind = SnapKind.OnWall, label = "on wall", targetWallId = w.id };
            }
        }

        return found;
    }

    private static bool TryAxis(Vector2 raw, Vector2 anchor, Options opts, out Result result)
    {
        result = default;

        Vector2 d = raw - anchor;
        float len = d.magnitude;
        if (len <= HomeConventions.EPS) return false;

        float step = opts.axisStepDeg > 1f ? opts.axisStepDeg : 90f;
        float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        float snappedAng = Mathf.Round(ang / step) * step;
        float deltaRad = (snappedAng - ang) * Mathf.Deg2Rad;

        // Perpendicular projection onto the snapped ray rather than preserving the raw length: the
        // point tracks under the cursor instead of shooting past it, which is what makes the lock
        // feel like a guide rather than a fight. With a 45° step the worst-case shortening is cos(22.5°).
        float projected = len * Mathf.Cos(deltaRad);
        if (projected <= HomeConventions.EPS) return false;

        if (opts.gridSize > HomeConventions.EPS)
            projected = Mathf.Round(projected / opts.gridSize) * opts.gridSize;

        float rad = snappedAng * Mathf.Deg2Rad;
        Vector2 p = anchor + new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * projected;

        result = new Result
        {
            point = p,
            kind = SnapKind.Axis,
            label = Mathf.RoundToInt(Mathf.Repeat(snappedAng, 360f)) + "°",
        };
        return true;
    }
}
