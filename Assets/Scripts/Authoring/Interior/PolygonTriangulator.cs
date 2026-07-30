using System.Collections.Generic;
using UnityEngine;

// Ear-clipping triangulation for simple polygons (concave allowed, holes not).
//
// Room floors are authored as free polygons, so a fan triangulation is not enough — an L-shaped
// living room or a bathroom with a bumped-out shower is concave, and a fan would fill across the
// notch. Ear clipping is the standard answer at this scale: O(n²) worst case, which is irrelevant
// when n is the dozen-or-so corners a real room has.
//
// OUTPUT WINDING: triangles come out in the same counter-clockwise (x, z) order as the input. That
// is deliberately NOT "ready to render" — which way a triangle must wind depends on whether it ends
// up as a floor (normal +Y) or a ceiling (normal -Y), so RoomMeshBuilder makes that decision. See
// the note there.
//
// Robustness matters more than elegance here: this runs on polygons a user is still dragging around,
// so duplicate points, collinear runs, and briefly self-intersecting shapes all arrive as input. The
// triangulator degrades (emits what it can and stops) rather than throwing or hanging.
public static class PolygonTriangulator
{
    /// <summary>
    /// Triangulates a simple polygon. Returns a flat list of index triples into
    /// <paramref name="poly"/>, wound counter-clockwise in (x, z). Empty when the polygon is
    /// degenerate. Duplicate and collinear vertices are skipped internally; the returned indices
    /// always refer to the ORIGINAL array so callers can keep their own per-vertex data.
    /// </summary>
    public static List<int> Triangulate(IReadOnlyList<Vector2> poly)
    {
        var tris = new List<int>();
        if (poly == null || poly.Count < 3) return tris;

        // Working set of original indices, with consecutive duplicates dropped.
        var idx = new List<int>(poly.Count);
        for (int i = 0; i < poly.Count; i++)
        {
            if (idx.Count > 0 && Near(poly[i], poly[idx[idx.Count - 1]])) continue;
            idx.Add(i);
        }
        // A closing point equal to the opening point is common in hand-authored data.
        while (idx.Count >= 2 && Near(poly[idx[0]], poly[idx[idx.Count - 1]]))
            idx.RemoveAt(idx.Count - 1);

        if (idx.Count < 3) return tris;

        // Normalise to counter-clockwise so the ear test has one consistent sense of "convex".
        if (SignedArea(poly, idx) < 0f) idx.Reverse();

        // Each successful clip removes one vertex; the guard bounds the pathological case where no
        // ear is ever found (self-intersecting input) so a bad drag can never hang the editor.
        int guard = idx.Count * idx.Count + 16;

        while (idx.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            int count = idx.Count;

            for (int i = 0; i < count; i++)
            {
                if (!IsEar(poly, idx, i)) continue;

                tris.Add(idx[(i - 1 + count) % count]);
                tris.Add(idx[i]);
                tris.Add(idx[(i + 1) % count]);
                idx.RemoveAt(i);
                clipped = true;
                break;
            }

            // No ear found — the polygon is self-intersecting or numerically degenerate. Keep the
            // triangles produced so far rather than discarding the user's whole room.
            if (!clipped) break;
        }

        if (idx.Count == 3)
        {
            tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]);
        }

        return tris;
    }

    /// <summary>Signed area of the full polygon. Positive == counter-clockwise in (x, z).</summary>
    public static float SignedArea(IReadOnlyList<Vector2> poly)
    {
        if (poly == null || poly.Count < 3) return 0f;
        float sum = 0f;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 p = poly[i];
            Vector2 q = poly[(i + 1) % poly.Count];
            sum += p.x * q.y - q.x * p.y;
        }
        return 0.5f * sum;
    }

    /// <summary>Unsigned area, i.e. the room's floor area in square meters.</summary>
    public static float Area(IReadOnlyList<Vector2> poly) => Mathf.Abs(SignedArea(poly));

    public static float Perimeter(IReadOnlyList<Vector2> poly)
    {
        if (poly == null || poly.Count < 2) return 0f;
        float sum = 0f;
        for (int i = 0; i < poly.Count; i++)
            sum += Vector2.Distance(poly[i], poly[(i + 1) % poly.Count]);
        return sum;
    }

    /// <summary>Converts the stored [[x, z], ...] form into Vector2s, skipping malformed entries.</summary>
    public static List<Vector2> ToVector2(float[][] points)
    {
        var list = new List<Vector2>();
        if (points == null) return list;
        foreach (var p in points)
            if (p != null && p.Length >= 2) list.Add(new Vector2(p[0], p[1]));
        return list;
    }

    public static float[][] ToArray(IReadOnlyList<Vector2> points)
    {
        if (points == null) return null;
        var arr = new float[points.Count][];
        for (int i = 0; i < points.Count; i++) arr[i] = new[] { points[i].x, points[i].y };
        return arr;
    }

    // ---------------------------------------------------------------------------------------

    private static float SignedArea(IReadOnlyList<Vector2> poly, List<int> idx)
    {
        float sum = 0f;
        for (int i = 0; i < idx.Count; i++)
        {
            Vector2 p = poly[idx[i]];
            Vector2 q = poly[idx[(i + 1) % idx.Count]];
            sum += p.x * q.y - q.x * p.y;
        }
        return 0.5f * sum;
    }

    // A vertex is an ear when it is convex and its triangle contains no REFLEX vertex of the polygon.
    //
    // Two details here are load-bearing, and getting either wrong silently fills in concave notches:
    //
    //   * Only reflex vertices are tested. A convex vertex of a simple polygon can never invalidate
    //     an ear, and skipping them is both faster and avoids false blocks.
    //   * Containment is INCLUSIVE (>= 0, not > 0). On an L-shaped room the reflex corner frequently
    //     lands exactly on the candidate ear's hypotenuse; with a strict test it fails to block the
    //     ear, and the clip cuts straight across the notch — filling the whole bounding box.
    private static bool IsEar(IReadOnlyList<Vector2> poly, List<int> idx, int i)
    {
        int n = idx.Count;
        int prev = (i - 1 + n) % n;
        int next = (i + 1) % n;

        Vector2 a = poly[idx[prev]], b = poly[idx[i]], c = poly[idx[next]];

        // Convex test for a counter-clockwise polygon. Collinear (cross ≈ 0) is rejected: clipping a
        // collinear vertex yields a zero-area triangle, which renders as nothing and can leave the
        // remaining ring in a state where no further ear is found.
        if (Cross(b - a, c - b) <= HomeConventions.EPS) return false;

        for (int j = 0; j < n; j++)
        {
            if (j == i || j == prev || j == next) continue;
            if (!IsReflex(poly, idx, j)) continue;
            if (PointInTriangleInclusive(poly[idx[j]], a, b, c)) return false;
        }
        return true;
    }

    private static bool IsReflex(IReadOnlyList<Vector2> poly, List<int> idx, int j)
    {
        int n = idx.Count;
        Vector2 p = poly[idx[(j - 1 + n) % n]];
        Vector2 q = poly[idx[j]];
        Vector2 r = poly[idx[(j + 1) % n]];
        return Cross(q - p, r - q) < -HomeConventions.EPS;
    }

    private static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

    private static bool PointInTriangleInclusive(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Cross(b - a, p - a);
        float d2 = Cross(c - b, p - b);
        float d3 = Cross(a - c, p - c);
        return d1 >= -HomeConventions.EPS && d2 >= -HomeConventions.EPS && d3 >= -HomeConventions.EPS;
    }

    private static bool Near(Vector2 p, Vector2 q)
        => (p - q).sqrMagnitude <= HomeConventions.EPS * HomeConventions.EPS;
}
