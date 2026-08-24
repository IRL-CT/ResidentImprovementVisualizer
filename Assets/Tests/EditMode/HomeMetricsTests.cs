using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// The plan ships no accessibility rules, but promises a rules-READY schema. That promise is only real
// if the numbers a rule would test are computable today. Clear widths, floor areas, turning space.
// These tests are the evidence for that claim.
[TestFixture]
public class HomeMetricsTests
{
    // ---- clear width: the number an accessibility rule would actually test ----

    [Test]
    public void ClearWidth_MeasuredValueBeatsTheEstimate()
    {
        var o = Door(0.9144f);          // 36" rough opening
        o.clearWidth = 0.85f;           // someone measured it on site

        Assert.AreEqual(0.85f, HomeMetrics.ClearWidth(o), 1e-4f);
        Assert.IsTrue(HomeMetrics.IsClearWidthMeasured(o));
    }

    [Test]
    public void ClearWidth_DoorLosesTheLeafAndStop()
    {
        var o = Door(0.9144f);

        Assert.AreEqual(0.9144f - 0.060f, HomeMetrics.ClearWidth(o), 1e-4f);
        Assert.IsFalse(HomeMetrics.IsClearWidthMeasured(o));
    }

    [Test]
    public void ClearWidth_CasedOpeningHasNoLeafToLose()
    {
        var o = Door(0.9144f);
        o.kind = OpeningKind.CasedOpening;

        Assert.AreEqual(0.9144f, HomeMetrics.ClearWidth(o), 1e-4f);
    }

    [Test]
    public void ClearWidth_WindowHasNoLeafToLose()
    {
        var o = Door(1.2f);
        o.kind = OpeningKind.Window;
        o.sillHeight = 0.914f;

        Assert.AreEqual(1.2f, HomeMetrics.ClearWidth(o), 1e-4f);
    }

    [Test]
    public void Threshold_IsDetected()
    {
        var flush = Door(0.9f);
        flush.thresholdHeight = 0f;
        Assert.IsFalse(HomeMetrics.HasThreshold(flush));

        var raised = Door(0.9f);
        raised.thresholdHeight = 0.019f;   // a 3/4" strip: a real trip hazard
        Assert.IsTrue(HomeMetrics.HasThreshold(raised));
    }

    // ---- rooms ----

    [Test]
    public void RoomAreaAndPerimeter()
    {
        var room = Room(Rect(0, 0, 4, 3));

        Assert.AreEqual(12f, HomeMetrics.RoomArea(room), 1e-3f);
        Assert.AreEqual(14f, HomeMetrics.RoomPerimeter(room), 1e-3f);
    }

    [Test]
    public void RoomCentroid_OfARectangleIsItsMiddle()
    {
        var room = Room(Rect(0, 0, 4, 2));
        var c = HomeMetrics.RoomCentroid(room);

        Assert.AreEqual(2f, c.x, 1e-3f);
        Assert.AreEqual(1f, c.y, 1e-3f);
    }

    [Test]
    public void RoomCentroid_DegeneratePolygonFallsBackToVertexAverage()
    {
        var room = new RoomDef { id = "r", polygon = new[] { new[] { 1f, 1f }, new[] { 3f, 1f } } };
        var c = HomeMetrics.RoomCentroid(room);

        Assert.AreEqual(2f, c.x, 1e-3f);
        Assert.AreEqual(1f, c.y, 1e-3f);
    }

    [Test]
    public void RoomAt_FindsTheContainingRoomAndIgnoresDegenerateOnes()
    {
        var level = new LevelDef
        {
            rooms = new List<RoomDef>
            {
                // A two-point "room" left over from an abandoned drag. EnvironmentScale.PointInPolygon
                // would report every point as inside it, so RoomAt must screen it out first.
                new RoomDef { id = "junk", polygon = new[] { new[] { 0f, 0f }, new[] { 1f, 0f } } },
                Room(Rect(0, 0, 4, 3), "bath"),
            }
        };

        Assert.AreEqual("bath", HomeMetrics.RoomAt(new Vector2(2f, 1.5f), level)?.id);
        Assert.IsNull(HomeMetrics.RoomAt(new Vector2(50f, 50f), level));
    }

    // ---- turning space ----

    [Test]
    public void LargestInscribedCircle_InASquare()
    {
        var circle = HomeMetrics.LargestInscribedCircle(RectPoly(0, 0, 4, 4));

        Assert.IsTrue(circle.valid);
        Assert.AreEqual(2f, circle.radius, 0.02f);
        Assert.AreEqual(2f, circle.center.x, 0.05f);
        Assert.AreEqual(2f, circle.center.y, 0.05f);
    }

    [Test]
    public void LargestInscribedCircle_InACorridorIsLimitedByTheNarrowDimension()
    {
        // A 6 x 2 hallway: the turning circle is capped at 1.0 m radius no matter how long it is.
        var circle = HomeMetrics.LargestInscribedCircle(RectPoly(0, 0, 6, 2));

        Assert.IsTrue(circle.valid);
        Assert.AreEqual(1f, circle.radius, 0.02f);
        Assert.AreEqual(1f, circle.center.y, 0.05f);
    }

    [Test]
    public void LargestInscribedCircle_ConcaveRoomIsSmallerThanItsBoundingBox()
    {
        // The L-shaped room from the triangulator tests. A naive bounding-box answer would be 2.0.
        var poly = new List<Vector2>
        {
            new Vector2(0, 0), new Vector2(4, 0), new Vector2(4, 2),
            new Vector2(2, 2), new Vector2(2, 4), new Vector2(0, 4),
        };

        var circle = HomeMetrics.LargestInscribedCircle(poly);

        Assert.IsTrue(circle.valid);
        Assert.Less(circle.radius, 2f);
        Assert.Greater(circle.radius, 0.9f);
    }

    [Test]
    public void LargestInscribedCircle_DegenerateInputIsInvalidNotZero()
    {
        Assert.IsFalse(HomeMetrics.LargestInscribedCircle((IReadOnlyList<Vector2>)null).valid);
        Assert.IsFalse(HomeMetrics.LargestInscribedCircle(new List<Vector2>
        {
            Vector2.zero, Vector2.one,
        }).valid);
    }

    // ---- wall helpers ----

    [Test]
    public void PointOnWall_InterpolatesAndClamps()
    {
        var w = new WallDef { id = "w", a = new[] { 0f, 0f }, b = new[] { 4f, 0f } };

        Assert.AreEqual(1f, HomeMetrics.PointOnWall(w, 1f).x, 1e-4f);
        Assert.AreEqual(4f, HomeMetrics.PointOnWall(w, 99f).x, 1e-4f);
        Assert.AreEqual(0f, HomeMetrics.PointOnWall(w, -5f).x, 1e-4f);
    }

    [Test]
    public void WallMidpoint()
    {
        var w = new WallDef { id = "w", a = new[] { 0f, 0f }, b = new[] { 4f, 2f } };
        var m = HomeMetrics.WallMidpoint(w);

        Assert.AreEqual(2f, m.x, 1e-4f);
        Assert.AreEqual(1f, m.y, 1e-4f);
    }

    [Test]
    public void PointSegmentDistance_HandlesTheDegenerateSegment()
    {
        Assert.AreEqual(1f, HomeMetrics.PointSegmentDistance(
            new Vector2(0, 1), new Vector2(-5, 0), new Vector2(5, 0)), 1e-4f);

        // Beyond the end, the distance is to the endpoint, not the infinite line.
        Assert.AreEqual(5f, HomeMetrics.PointSegmentDistance(
            new Vector2(10, 0), new Vector2(0, 0), new Vector2(5, 0)), 1e-4f);

        Assert.AreEqual(3f, HomeMetrics.PointSegmentDistance(
            new Vector2(3, 0), Vector2.zero, Vector2.zero), 1e-4f);
    }

    // ---------------------------------------------------------------------------------------

    private static OpeningDef Door(float width) => new OpeningDef
    {
        id = "d", wallId = "w", offset = 1f, width = width, height = 2.032f,
        kind = OpeningKind.Door,
    };

    private static float[][] Rect(float x, float z, float w, float d) => new[]
    {
        new[] { x, z }, new[] { x + w, z }, new[] { x + w, z + d }, new[] { x, z + d },
    };

    private static List<Vector2> RectPoly(float x, float z, float w, float d) => new List<Vector2>
    {
        new Vector2(x, z), new Vector2(x + w, z), new Vector2(x + w, z + d), new Vector2(x, z + d),
    };

    private static RoomDef Room(float[][] poly, string id = "r")
        => new RoomDef { id = id, name = id, roomType = RoomType.Other, polygon = poly };
}
