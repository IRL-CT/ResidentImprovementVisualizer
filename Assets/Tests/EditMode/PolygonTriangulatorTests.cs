using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// Room floors are free polygons, so the triangulator meets concave shapes (an L-shaped living room),
// sloppy input (duplicate and collinear points from a drag), and briefly self-intersecting shapes
// while a polygon is being drawn. It must degrade rather than throw or hang — a hung editor during a
// visioning session is worse than a slightly wrong floor.
[TestFixture]
public class PolygonTriangulatorTests
{
    [Test]
    public void Square_ProducesTwoTriangles()
    {
        var poly = Square(4f);
        var tris = PolygonTriangulator.Triangulate(poly);

        Assert.AreEqual(6, tris.Count);   // 2 triangles
        AssertCoversArea(poly, tris, 16f);
    }

    [Test]
    public void ConcaveLShape_FillsOnlyTheInterior()
    {
        // An L: 4x4 with a 2x2 bite taken out of the top-right. A fan triangulation would spill
        // across the notch; ear clipping must not.
        var poly = new List<Vector2>
        {
            new Vector2(0, 0), new Vector2(4, 0), new Vector2(4, 2),
            new Vector2(2, 2), new Vector2(2, 4), new Vector2(0, 4),
        };

        var tris = PolygonTriangulator.Triangulate(poly);

        Assert.AreEqual(12, tris.Count);            // n - 2 = 4 triangles
        AssertCoversArea(poly, tris, 12f);          // 16 - 4
    }

    [Test]
    public void ClockwiseInput_IsNormalisedAndStillWorks()
    {
        var poly = Square(4f);
        poly.Reverse();

        var tris = PolygonTriangulator.Triangulate(poly);

        Assert.AreEqual(6, tris.Count);
        AssertCoversArea(poly, tris, 16f);
    }

    [Test]
    public void DuplicateClosingPoint_IsDropped()
    {
        var poly = Square(4f);
        poly.Add(poly[0]);   // hand-authored data often repeats the first vertex

        var tris = PolygonTriangulator.Triangulate(poly);

        Assert.AreEqual(6, tris.Count);
    }

    [Test]
    public void CollinearPoints_DoNotDeadlockTheClipLoop()
    {
        // A midpoint dropped on a straight edge — exactly what a stray click produces.
        var poly = new List<Vector2>
        {
            new Vector2(0, 0), new Vector2(2, 0), new Vector2(4, 0),
            new Vector2(4, 4), new Vector2(0, 4),
        };

        var tris = PolygonTriangulator.Triangulate(poly);

        Assert.Greater(tris.Count, 0);
        AssertCoversArea(poly, tris, 16f);
    }

    [Test]
    public void DegenerateInput_ReturnsEmptyWithoutThrowing()
    {
        Assert.AreEqual(0, PolygonTriangulator.Triangulate(null).Count);
        Assert.AreEqual(0, PolygonTriangulator.Triangulate(new List<Vector2>()).Count);
        Assert.AreEqual(0, PolygonTriangulator.Triangulate(new List<Vector2>
        {
            new Vector2(0, 0), new Vector2(1, 1),
        }).Count);

        // Three identical points collapse to fewer than three distinct vertices.
        Assert.AreEqual(0, PolygonTriangulator.Triangulate(new List<Vector2>
        {
            Vector2.zero, Vector2.zero, Vector2.zero,
        }).Count);
    }

    [Test]
    public void SelfIntersectingPolygon_TerminatesAndReturnsWhatItCan()
    {
        // A bowtie. There is no correct answer; the requirement is simply that this returns.
        var poly = new List<Vector2>
        {
            new Vector2(0, 0), new Vector2(4, 4), new Vector2(4, 0), new Vector2(0, 4),
        };

        var tris = PolygonTriangulator.Triangulate(poly);

        Assert.AreEqual(0, tris.Count % 3);
    }

    [Test]
    public void SignedArea_PositiveForCounterClockwise()
    {
        var ccw = Square(2f);
        Assert.Greater(PolygonTriangulator.SignedArea(ccw), 0f);

        ccw.Reverse();
        Assert.Less(PolygonTriangulator.SignedArea(ccw), 0f);
    }

    [Test]
    public void AreaAndPerimeter()
    {
        var poly = Square(3f);
        Assert.AreEqual(9f, PolygonTriangulator.Area(poly), 1e-4f);
        Assert.AreEqual(12f, PolygonTriangulator.Perimeter(poly), 1e-4f);
    }

    [Test]
    public void ArrayRoundTrip_SkipsMalformedEntries()
    {
        var src = new[]
        {
            new[] { 1f, 2f },
            null,
            new[] { 3f },          // too short
            new[] { 4f, 5f, 6f },  // extra components are ignored
        };

        var v = PolygonTriangulator.ToVector2(src);

        Assert.AreEqual(2, v.Count);
        Assert.AreEqual(new Vector2(1, 2), v[0]);
        Assert.AreEqual(new Vector2(4, 5), v[1]);

        var back = PolygonTriangulator.ToArray(v);
        Assert.AreEqual(2, back.Length);
        Assert.AreEqual(4f, back[1][0], 1e-4f);
    }

    // ---------------------------------------------------------------------------------------

    private static List<Vector2> Square(float size) => new List<Vector2>
    {
        new Vector2(0, 0), new Vector2(size, 0), new Vector2(size, size), new Vector2(0, size),
    };

    // The triangles must tile the polygon exactly — same total area, no overlap, no spill.
    private static void AssertCoversArea(List<Vector2> poly, List<int> tris, float expected)
    {
        float sum = 0f;
        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            Vector2 a = poly[tris[i]], b = poly[tris[i + 1]], c = poly[tris[i + 2]];
            sum += Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) * 0.5f;
        }
        Assert.AreEqual(expected, sum, 1e-3f, "triangulated area does not match the polygon");
    }
}
