using System.Collections.Generic;
using UnityEngine;

// 2-D segment primitives in world XZ meters (Vector2 = (x, z)).
//
// These did not exist anywhere in the project. ResidenceMetrics has PointSegmentDistance and PointInPolygon,
// WallSnapping has a point-on-segment projection inlined (and so do OpeningTool and FurnitureTool,
// three copies of the same six lines), but nothing could answer "do these two segments cross, and
// where". Every wall in a plan is a segment, so a wall tool that divides and joins needs exactly that.
//
// Intersect is lifted from FenceLinker.SegIntersect, which has been in service on the Site tool's fence
// network for as long as that tool has existed. Its two non-obvious properties are both deliberate and
// both load-bearing here:
//
//   * PARALLEL AND COLLINEAR RETURN FALSE. Two walls running along the same line are not a junction,
//     they are an overlap, and chopping one at the other's endpoints turns a legitimate plan (a 100 mm
//     partition butting a 200 mm structural wall) into confetti.
//   * THE [0,1] RANGES ARE PADDED BY eps IN METERS, not in parameter space. A run that stops 15 mm
//     short of the wall it was aimed at still joins, and the padding means the same 15 mm regardless of
//     whether the segment is 0.4 m or 12 m long.
public static class Segments
{
    /// <summary>
    /// Parametric intersection of a1→a2 and b1→b2. Parallel or collinear returns false.
    /// <paramref name="t"/> and <paramref name="u"/> come back clamped to [0,1] and
    /// <paramref name="p"/> is the point on a.
    /// </summary>
    public static bool Intersect(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2, float eps,
                                 out float t, out float u, out Vector2 p)
    {
        t = 0f; u = 0f; p = default;
        Vector2 r = a2 - a1, s = b2 - b1;
        float rLen = r.magnitude, sLen = s.magnitude;
        if (rLen < 1e-6f || sLen < 1e-6f) return false;

        float denom = r.x * s.y - r.y * s.x;
        if (Mathf.Abs(denom) < 1e-9f) return false;   // parallel / collinear is never a junction

        Vector2 d = b1 - a1;
        t = (d.x * s.y - d.y * s.x) / denom;
        u = (d.x * r.y - d.y * r.x) / denom;

        // Pad in meters, so the tolerance does not shrink as a segment gets longer.
        float tPad = eps / rLen, uPad = eps / sLen;
        if (t < -tPad || t > 1f + tPad || u < -uPad || u > 1f + uPad) return false;

        t = Mathf.Clamp01(t);
        u = Mathf.Clamp01(u);
        p = a1 + t * r;
        return true;
    }

    /// <summary>Parameter of p projected onto a→b, clamped to [0,1]. Zero-length returns 0.</summary>
    public static float ParamOn(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-9f) return 0f;
        return Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
    }

    /// <summary>The closest point to p on the segment a→b, with its parameter.</summary>
    public static Vector2 ClosestOn(Vector2 p, Vector2 a, Vector2 b, out float t)
    {
        t = ParamOn(p, a, b);
        return a + t * (b - a);
    }

    /// <summary>|sin| of the angle between two directions: the cheap "are these parallel" test.</summary>
    public static float SinBetween(Vector2 d0, Vector2 d1)
    {
        float l0 = d0.magnitude, l1 = d1.magnitude;
        if (l0 < 1e-6f || l1 < 1e-6f) return 0f;
        return Mathf.Abs(d0.x * d1.y - d0.y * d1.x) / (l0 * l1);
    }

    /// <summary>
    /// True when b1→b2 lies along the same infinite line as a1→a2: parallel within
    /// <paramref name="minSin"/> AND both of b's endpoints within <paramref name="eps"/> of a's line.
    /// The overlap interval comes back as parameters on a, which may fall outside [0,1] when b extends
    /// past a's ends. Callers that need the shared part clamp it themselves.
    /// </summary>
    public static bool CollinearOverlap(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2,
                                        float eps, float minSin, out float t0, out float t1)
    {
        t0 = 0f; t1 = 0f;
        Vector2 ab = a2 - a1;
        float len = ab.magnitude;
        if (len < 1e-6f) return false;
        if (SinBetween(ab, b2 - b1) > minSin) return false;

        Vector2 dir = ab / len;
        Vector2 n = new Vector2(-dir.y, dir.x);
        if (Mathf.Abs(Vector2.Dot(b1 - a1, n)) > eps) return false;
        if (Mathf.Abs(Vector2.Dot(b2 - a1, n)) > eps) return false;

        float p0 = Vector2.Dot(b1 - a1, dir) / len;
        float p1 = Vector2.Dot(b2 - a1, dir) / len;
        t0 = Mathf.Min(p0, p1);
        t1 = Mathf.Max(p0, p1);
        return true;
    }

    /// <summary>
    /// Snaps p onto the nearest candidate within <paramref name="eps"/>, or returns it unchanged.
    ///
    /// This is FenceLinker.WeldToVertex generalised, and it is the single most important function here.
    /// WallMeshBuilder.ComputeExtensions welds a corner only when two endpoints coincide within ~1 mm,
    /// and it does so by comparing the stored floats, so a junction computed twice, once per side, is
    /// a ~57 mm notch waiting to happen. Welding both sides onto the SAME candidate value makes them
    /// bit-identical rather than merely close.
    /// </summary>
    public static Vector2 Weld(Vector2 p, IReadOnlyList<Vector2> candidates, float eps)
    {
        if (candidates == null) return p;
        float best = eps;
        Vector2 welded = p;
        for (int i = 0; i < candidates.Count; i++)
        {
            float d = Vector2.Distance(p, candidates[i]);
            if (d <= best) { best = d; welded = candidates[i]; }
        }
        return welded;
    }

    public static bool Near(Vector2 a, Vector2 b, float eps) => (a - b).sqrMagnitude <= eps * eps;

    /// <summary>
    /// Folds p into a canonical set of junction points: welds it onto a nearby member if there is one,
    /// otherwise adds it as a new member. Returns the canonical value, which callers must then store
    /// verbatim.
    ///
    /// This is Weld's bookkeeping half, and the pair is what makes a junction ONE Vector2 rather than
    /// one per side. A three-way meeting collapses to a single rep, so all three walls write the same
    /// bits. Lifted out of WallLinker so the linker and RoomRegions cannot drift on what counts as one
    /// junction. They have to agree, or an area the linker welded shut is one the face finder reads
    /// as open.
    /// </summary>
    public static Vector2 Canonical(List<Vector2> reps, Vector2 p, float eps)
    {
        if (reps == null) return p;
        return reps[CanonicalIndex(reps, p, eps)];
    }

    /// <summary>
    /// <see cref="Canonical"/>'s index form, for callers building a vertex-indexed graph. Same rule,
    /// one implementation. RoomRegions keys its edges by vertex index, and an index that disagreed
    /// with the welded value about which points are one junction would build a graph whose edges do
    /// not meet.
    /// </summary>
    public static int CanonicalIndex(List<Vector2> reps, Vector2 p, float eps)
    {
        int best = -1;
        float bestD = eps;
        for (int i = 0; i < reps.Count; i++)
        {
            float d = Vector2.Distance(p, reps[i]);
            if (d <= bestD) { bestD = d; best = i; }
        }
        if (best >= 0) return best;
        reps.Add(p);
        return reps.Count - 1;
    }

    /// <summary>True when any member of the list is within eps of p.</summary>
    public static bool Contains(IReadOnlyList<Vector2> list, Vector2 p, float eps)
    {
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
            if (Near(p, list[i], eps)) return true;
        return false;
    }

    // ---- WallDef conveniences ------------------------------------------------------------------
    // WallDef stores a and b as float[2]. Every caller here would otherwise repeat the unpacking, and
    // a malformed or short array must never throw in the middle of a drag.

    public static bool TryEnds(WallDef w, out Vector2 a, out Vector2 b)
    {
        a = default; b = default;
        if (w?.a == null || w.b == null || w.a.Length < 2 || w.b.Length < 2) return false;
        a = new Vector2(w.a[0], w.a[1]);
        b = new Vector2(w.b[0], w.b[1]);
        return (b - a).sqrMagnitude > 1e-9f;
    }

    public static void SetEnds(WallDef w, Vector2 a, Vector2 b)
    {
        w.a = new[] { a.x, a.y };
        w.b = new[] { b.x, b.y };
    }
}
