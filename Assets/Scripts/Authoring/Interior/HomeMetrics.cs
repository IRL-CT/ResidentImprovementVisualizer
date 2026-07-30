using System.Collections.Generic;
using UnityEngine;

// Every dimension the tool reports, in one place.
//
// The plan ships no accessibility RULES, but it does promise a rules-ready schema — which is only
// true if the numbers a rule would test are actually computable. This file is that guarantee: clear
// widths, floor areas, and turning space are derived here now, so adding a rule later is writing a
// comparison rather than inventing geometry.
public static class HomeMetrics
{
    // ---------------------------------------------------------------------------------------
    // Walls and rooms
    // ---------------------------------------------------------------------------------------

    public static float WallLength(WallDef w) => WallLayout.WallLength(w);

    public static Vector2 WallMidpoint(WallDef w)
    {
        if (w?.a == null || w.b == null || w.a.Length < 2 || w.b.Length < 2) return Vector2.zero;
        return new Vector2(0.5f * (w.a[0] + w.b[0]), 0.5f * (w.a[1] + w.b[1]));
    }

    /// <summary>World XZ of a point at <paramref name="offset"/> meters along the wall from `a`.</summary>
    public static Vector2 PointOnWall(WallDef w, float offset)
    {
        if (w?.a == null || w.b == null || w.a.Length < 2 || w.b.Length < 2) return Vector2.zero;
        float len = WallLayout.WallLength(w);
        var a = new Vector2(w.a[0], w.a[1]);
        if (len <= HomeConventions.EPS) return a;
        var dir = (new Vector2(w.b[0], w.b[1]) - a) / len;
        return a + dir * Mathf.Clamp(offset, 0f, len);
    }

    public static float RoomArea(RoomDef r)
        => PolygonTriangulator.Area(PolygonTriangulator.ToVector2(r?.polygon));

    public static float RoomPerimeter(RoomDef r)
        => PolygonTriangulator.Perimeter(PolygonTriangulator.ToVector2(r?.polygon));

    public static Vector2 RoomCentroid(RoomDef r)
    {
        var poly = PolygonTriangulator.ToVector2(r?.polygon);
        if (poly.Count == 0) return Vector2.zero;

        // Area-weighted centroid, falling back to the vertex average for a degenerate polygon so a
        // half-drawn room still gets a sensible label position.
        float a2 = PolygonTriangulator.SignedArea(poly) * 2f;
        if (Mathf.Abs(a2) <= HomeConventions.EPS)
        {
            Vector2 sum = Vector2.zero;
            foreach (var p in poly) sum += p;
            return sum / poly.Count;
        }

        float cx = 0f, cy = 0f;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 p = poly[i], q = poly[(i + 1) % poly.Count];
            float cross = p.x * q.y - q.x * p.y;
            cx += (p.x + q.x) * cross;
            cy += (p.y + q.y) * cross;
        }
        return new Vector2(cx / (3f * a2), cy / (3f * a2));
    }

    /// <summary>Which room contains a world XZ point, or null. Guards the degenerate-polygon case
    /// that EnvironmentScale.PointInPolygon deliberately treats as "inside everything".</summary>
    public static RoomDef RoomAt(Vector2 p, LevelDef level)
    {
        if (level?.rooms == null) return null;
        foreach (var r in level.rooms)
        {
            if (r?.polygon == null || r.polygon.Length < 3) continue;
            if (EnvironmentScale.PointInPolygon(p.x, p.y, r.polygon)) return r;
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------
    // Openings
    // ---------------------------------------------------------------------------------------

    // Passage lost to the door leaf, its stop, and the hinge offset when a swing door stands open at
    // 90°. An estimate, and labelled as one wherever it surfaces — the whole reason
    // OpeningDef.clearWidth exists as a stored field is so a measured value beats this.
    private const float SWING_LEAF_LOSS  = 0.060f;   // ~2 3/8"
    private const float POCKET_LEAF_LOSS = 0.030f;   // pocket doors lose only the jamb reveal

    /// <summary>
    /// The clear passage width in meters. Returns the stored measured value when present; otherwise
    /// derives an estimate from the rough opening and the swing type. Use
    /// <see cref="IsClearWidthMeasured"/> to tell the two apart in the UI.
    /// </summary>
    public static float ClearWidth(OpeningDef o)
    {
        if (o == null) return 0f;
        if (o.clearWidth > HomeConventions.EPS) return o.clearWidth;

        switch (o.swing)
        {
            case OpeningSwing.Slider:
                // A two-panel slider only ever opens half its width.
                return Mathf.Max(0f, o.width * 0.5f);
            case OpeningSwing.Pocket:
                return Mathf.Max(0f, o.width - POCKET_LEAF_LOSS);
            case OpeningSwing.None:
                return o.width;
            case OpeningSwing.LeftIn:
            case OpeningSwing.LeftOut:
            case OpeningSwing.RightIn:
            case OpeningSwing.RightOut:
                return Mathf.Max(0f, o.width - SWING_LEAF_LOSS);
            default:
                // Cased openings and pass-throughs have no leaf at all.
                return o.kind == OpeningKind.Door
                    ? Mathf.Max(0f, o.width - SWING_LEAF_LOSS)
                    : o.width;
        }
    }

    public static bool IsClearWidthMeasured(OpeningDef o) => o != null && o.clearWidth > HomeConventions.EPS;

    /// <summary>True when the opening has a raised threshold — the most common trip and wheelchair
    /// obstacle in an existing home, and the thing a "step-free route" check would test.</summary>
    public static bool HasThreshold(OpeningDef o) => o != null && o.thresholdHeight > HomeConventions.EPS;

    // ---------------------------------------------------------------------------------------
    // Turning space
    // ---------------------------------------------------------------------------------------

    public struct Circle
    {
        public Vector2 center;
        public float radius;
        public bool valid;
    }

    /// <summary>
    /// The largest circle that fits inside a room — i.e. the turning space available to a wheelchair
    /// user, which is the single most useful derived number in an accessibility review.
    ///
    /// Solved by sampling rather than exactly: the exact answer is the maximum of the polygon's medial
    /// axis, which is a lot of machinery for a value that only needs to be right to a centimetre or
    /// two. A coarse grid finds the neighbourhood, then the window shrinks around the best sample for
    /// a few rounds. Note this ignores furniture — it is the space the ROOM offers, and subtracting
    /// obstacles is a rule's job, not a metric's.
    /// </summary>
    public static Circle LargestInscribedCircle(IReadOnlyList<Vector2> poly, int coarseSamples = 24, int refineSteps = 6)
    {
        var result = new Circle { valid = false };
        if (poly == null || poly.Count < 3) return result;

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in poly)
        {
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
        }

        float w = maxX - minX, h = maxY - minY;
        if (w <= HomeConventions.EPS || h <= HomeConventions.EPS) return result;

        int n = Mathf.Max(4, coarseSamples);
        Vector2 best = new Vector2(minX + 0.5f * w, minY + 0.5f * h);
        float bestD = -1f;

        for (int i = 0; i <= n; i++)
        for (int j = 0; j <= n; j++)
        {
            var p = new Vector2(minX + w * i / n, minY + h * j / n);
            float d = SignedDistanceInside(p, poly);
            if (d > bestD) { bestD = d; best = p; }
        }

        // Shrink a search window around the current best. Each round halves the window and re-samples
        // a small neighbourhood, which converges fast enough for room-sized polygons.
        float stepX = w / n, stepY = h / n;
        for (int s = 0; s < refineSteps; s++)
        {
            stepX *= 0.5f; stepY *= 0.5f;
            for (int i = -2; i <= 2; i++)
            for (int j = -2; j <= 2; j++)
            {
                if (i == 0 && j == 0) continue;
                var p = new Vector2(best.x + i * stepX, best.y + j * stepY);
                float d = SignedDistanceInside(p, poly);
                if (d > bestD) { bestD = d; best = p; }
            }
        }

        if (bestD <= 0f) return result;
        return new Circle { center = best, radius = bestD, valid = true };
    }

    public static Circle LargestInscribedCircle(RoomDef room)
        => LargestInscribedCircle(PolygonTriangulator.ToVector2(room?.polygon));

    // Distance from an interior point to the nearest edge; negative (well, -1) when outside.
    private static float SignedDistanceInside(Vector2 p, IReadOnlyList<Vector2> poly)
    {
        if (!PointInPolygon(p, poly)) return -1f;

        float best = float.MaxValue;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 a = poly[i], b = poly[(i + 1) % poly.Count];
            best = Mathf.Min(best, PointSegmentDistance(p, a, b));
        }
        return best;
    }

    public static float PointSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        if (lenSq <= HomeConventions.EPS) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
        return Vector2.Distance(p, a + ab * t);
    }

    // Local ray-cast test over Vector2s. EnvironmentScale.PointInPolygon does the same job but takes
    // float[][] and returns true for degenerate input; converting on every one of the thousands of
    // samples above would dominate the cost.
    public static bool PointInPolygon(Vector2 p, IReadOnlyList<Vector2> poly)
    {
        if (poly == null || poly.Count < 3) return false;
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            if (poly[i].y > p.y != poly[j].y > p.y &&
                p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                inside = !inside;
        }
        return inside;
    }
}
