using System.Collections.Generic;
using UnityEngine;

// Every dimension the tool reports, in one place.
//
// The plan ships no accessibility RULES, but it does promise a rules-ready schema, which is only
// true if the numbers a rule would test are actually computable. This file is that guarantee: clear
// widths, floor areas, and turning space are derived here now, so adding a rule later is writing a
// comparison rather than inventing geometry.
public static class ResidenceMetrics
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
        if (len <= ResidenceConventions.EPS) return a;
        var dir = (new Vector2(w.b[0], w.b[1]) - a) / len;
        return a + dir * Mathf.Clamp(offset, 0f, len);
    }

    /// <summary>
    /// The wall nearest <paramref name="at"/>, with where along it the point falls and which face it
    /// is on: everything a wall-mounted item needs to know to be hosted.
    /// </summary>
    /// <param name="maxDistance">How far from a centerline still counts as "at that wall".</param>
    /// <remarks>
    /// A grab bar has to land ON a wall and on the correct FACE of it: mounting one on the outside of
    /// a bathroom wall is a silent, useless result. Both the tool that places mounts and the tool that
    /// moves them afterwards need this identical answer, so it lives here rather than in either of
    /// them: two copies would be two chances for placing and re-hosting to disagree about which side
    /// the cursor is on.
    /// </remarks>
    public static WallDef NearestWall(Vector2 at, IReadOnlyList<WallDef> walls, float maxDistance,
                                      out float offset, out int side)
    {
        offset = 0f;
        side = WallSide.Left;
        if (walls == null) return null;

        WallDef best = null;
        float bestSq = maxDistance * maxDistance;

        for (int i = 0; i < walls.Count; i++)
        {
            var w = walls[i];
            if (w?.a == null || w.b == null || w.a.Length < 2 || w.b.Length < 2) continue;

            var a = new Vector2(w.a[0], w.a[1]);
            var b = new Vector2(w.b[0], w.b[1]);
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            if (lenSq <= 1e-6f) continue;

            float t = Mathf.Clamp01(Vector2.Dot(at - a, ab) / lenSq);
            Vector2 foot = a + ab * t;
            float d = (at - foot).sqrMagnitude;
            if (d >= bestSq) continue;

            bestSq = d;
            best = w;
            offset = t * Mathf.Sqrt(lenSq);

            // Which side of the centerline the point is on decides the mounting face.
            Vector2 dir = ab / Mathf.Sqrt(lenSq);
            Vector2 left = new Vector2(-dir.y, dir.x);
            side = Vector2.Dot(at - foot, left) >= 0f ? WallSide.Left : WallSide.Right;
        }

        return best;
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
        if (Mathf.Abs(a2) <= ResidenceConventions.EPS)
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

    // Passage lost to the door leaf, its stop, and the hinge offset when a door stands open at 90°.
    // An estimate, and labelled as one wherever it surfaces: the whole reason OpeningDef.clearWidth
    // exists as a stored field is so a measured value beats this.
    private const float LEAF_LOSS = 0.060f;   // ~2 3/8"

    /// <summary>
    /// The clear passage width in meters. Returns the stored measured value when present; otherwise
    /// derives an estimate from the rough opening: a door loses its leaf and stop, anything with no
    /// leaf in it loses nothing. Use <see cref="IsClearWidthMeasured"/> to tell the two apart in
    /// the UI.
    /// </summary>
    public static float ClearWidth(OpeningDef o)
    {
        if (o == null) return 0f;
        if (o.clearWidth > ResidenceConventions.EPS) return o.clearWidth;

        // Windows, cased openings and pass-throughs have no leaf at all.
        return o.kind == OpeningKind.Door
            ? Mathf.Max(0f, o.width - LEAF_LOSS)
            : o.width;
    }

    public static bool IsClearWidthMeasured(OpeningDef o) => o != null && o.clearWidth > ResidenceConventions.EPS;

    /// <summary>True when the opening has a raised threshold: the most common trip and wheelchair
    /// obstacle in an existing residence, and the thing a "step-free route" check would test.</summary>
    public static bool HasThreshold(OpeningDef o) => o != null && o.thresholdHeight > ResidenceConventions.EPS;

    // ---------------------------------------------------------------------------------------
    // Footprints
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// An item's axis-aligned footprint in world XZ. Yaw is snapped to a quarter turn before the
    /// width/depth swap, mirroring SampleFurniture.FootprintXZ: the plans only ever use multiples of
    /// 90, and an approximate OBB would make every "is this clear of that" test approximate too.
    /// </summary>
    public static Rect FootprintOf(ObjectInstance item)
    {
        if (item?.position == null || item.position.Length < 3) return new Rect();

        // boxSizeMeters is [w, h, d]. Index 1 is height and plays no part in a footprint.
        float w = 0.6f, d = 0.6f;
        if (item.boxSizeMeters != null && item.boxSizeMeters.Length >= 3)
        {
            w = Mathf.Max(0f, item.boxSizeMeters[0]);
            d = Mathf.Max(0f, item.boxSizeMeters[2]);
        }

        int quarter = Mathf.RoundToInt(Mathf.Repeat(item.rotationY, 360f) / 90f) % 4;
        if (quarter == 1 || quarter == 3) { float t = w; w = d; d = t; }

        return new Rect(item.position[0] - 0.5f * w, item.position[2] - 0.5f * d, w, d);
    }

    /// <summary>An item's height in meters, or a sensible default when boxSizeMeters is absent.</summary>
    public static float HeightOf(ObjectInstance item)
        => item?.boxSizeMeters != null && item.boxSizeMeters.Length >= 3
            ? Mathf.Max(0f, item.boxSizeMeters[1])
            : 0.8f;

    /// <summary>Shortest distance from a point to a rect; zero when the point is inside it.</summary>
    public static float PointRectDistance(Vector2 p, Rect r)
    {
        float dx = Mathf.Max(r.xMin - p.x, 0f, p.x - r.xMax);
        float dy = Mathf.Max(r.yMin - p.y, 0f, p.y - r.yMax);
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Overlap area of two footprints; zero when they merely touch.</summary>
    public static float OverlapArea(Rect a, Rect b)
    {
        float w = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
        float h = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
        return w <= 0f || h <= 0f ? 0f : w * h;
    }

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
    /// The largest circle that fits inside a room. i.e. the turning space available to a wheelchair
    /// user, which is the single most useful derived number in an accessibility review.
    ///
    /// Solved by sampling rather than exactly: the exact answer is the maximum of the polygon's medial
    /// axis, which is a lot of machinery for a value that only needs to be right to a centimetre or
    /// two. A coarse grid finds the neighborhood, then the window shrinks around the best sample for
    /// a few rounds. Note this ignores furniture. It is the space the ROOM offers, and subtracting
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
        if (w <= ResidenceConventions.EPS || h <= ResidenceConventions.EPS) return result;

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
        // a small neighborhood, which converges fast enough for room-sized polygons.
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

    /// <summary>
    /// Distance from an interior point to the nearest edge; negative (well, -1) when outside. Public
    /// because "does a person of radius r fit here" is the same question as "is the clearance to the
    /// nearest wall at least r". See OccupancyModel.IsClear.
    /// </summary>
    public static float SignedDistanceInside(Vector2 p, IReadOnlyList<Vector2> poly)
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
        if (lenSq <= ResidenceConventions.EPS) return Vector2.Distance(p, a);
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
