using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// PlanBuilder exists because the render path never complains about bad geometry — it clamps, skips, or
// leaves a notch. These tests pin the three derivations that make hand-authored plans safe: shared
// walls collapse, overlapping walls resolve, and T-junctions split so corners can weld.
[TestFixture]
public class PlanBuilderTests
{
    [Test]
    public void SharedEdge_BecomesOneWallNotTwo()
    {
        var level = new PlanBuilder()
            .Room("a", "A", RoomType.Living, 0f, 0f, 4f, 3f)
            .Room("b", "B", RoomType.Living, 4f, 0f, 4f, 3f)
            .Build();

        // The line x = 4 is A's east edge and B's west edge. One wall, spanning z 0..3.
        var onLine = WallsOnVerticalLine(level, 4f);
        Assert.AreEqual(1, onLine.Count, "A shared edge must yield a single wall.");
        Assert.AreEqual(3f, WallLayout.WallLength(onLine[0]), 1e-3f);
    }

    [Test]
    public void PartiallyOverlappingWalls_ResolveIntoNonOverlappingRuns()
    {
        // Two rooms of different depth sharing the line x = 4: A covers z 0..3, B covers z 0..6.
        var level = new PlanBuilder()
            .Room("a", "A", RoomType.Living, 0f, 0f, 4f, 3f)
            .Room("b", "B", RoomType.Living, 4f, 0f, 4f, 6f)
            .Build();

        var onLine = WallsOnVerticalLine(level, 4f);
        Assert.AreEqual(2, onLine.Count, "The union should split at A's far corner.");

        float total = 0f;
        foreach (var w in onLine) total += WallLayout.WallLength(w);
        Assert.AreEqual(6f, total, 1e-3f, "The runs must cover the union exactly, with no overlap.");

        AssertNoOverlaps(level);
    }

    [Test]
    public void TJunction_SplitsTheThroughWallSoEndpointsCoincide()
    {
        // B's west wall (x = 4) ends at z = 3, which is the interior of A's north wall run.
        var level = new PlanBuilder()
            .Room("a", "A", RoomType.Living, 0f, 0f, 8f, 3f)
            .Room("b", "B", RoomType.Living, 4f, 3f, 4f, 3f)
            .Build();

        var onLine = WallsOnHorizontalLine(level, 3f);
        Assert.AreEqual(2, onLine.Count, "The through-wall must break at the T.");

        // The split point has to be an exact endpoint of both pieces, or WallMeshBuilder cannot weld.
        Assert.IsTrue(HasEndpointAt(level, new Vector2(4f, 3f)));
        AssertNoInteriorEndpoints(level);
    }

    [Test]
    public void EveryWallEndpointIsSharedOrACorner_NoDanglingTs()
    {
        var level = new PlanBuilder()
            .Room("a", "A", RoomType.Living, 0f, 0f, 6f, 4f)
            .Room("b", "B", RoomType.Bedroom, 6f, 0f, 3f, 2f)
            .Room("c", "C", RoomType.Bedroom, 6f, 2f, 3f, 2f)
            .Room("d", "D", RoomType.Hall, 0f, 4f, 9f, 1.2f)
            .Build();

        AssertNoInteriorEndpoints(level);
        AssertNoOverlaps(level);
    }

    [Test]
    public void RoomPolygon_IsCounterClockwiseWithTheAuthoredArea()
    {
        var level = new PlanBuilder()
            .Room("a", "A", RoomType.Bedroom, 1f, 2f, 4f, 3f)
            .Build();

        var room = level.rooms[0];
        var poly = PolygonTriangulator.ToVector2(room.polygon);

        Assert.AreEqual(4, poly.Count);
        Assert.Greater(PolygonTriangulator.SignedArea(poly), 0f, "Polygons must wind CCW.");
        Assert.AreEqual(12f, RoomMeshBuilder.FloorArea(room), 1e-3f);
        Assert.AreEqual("floor_carpet", room.floorMaterial, "Bedrooms follow RoomTool's default.");
    }

    [Test]
    public void DoorBetweenRoomsThatDoNotTouch_WarnsAndPlacesNothing()
    {
        var b = new PlanBuilder()
            .Room("a", "A", RoomType.Living, 0f, 0f, 3f, 3f)
            .Room("b", "B", RoomType.Living, 5f, 0f, 3f, 3f);
        b.DoorBetween("a", "b", 0.813f);

        var level = b.Build();

        Assert.AreEqual(0, level.openings.Count);
        Assert.AreEqual(1, b.Warnings.Count);
        Assert.That(b.Warnings[0], Does.Contain("do not share an edge"));
    }

    [Test]
    public void DoorBetween_LandsOnTheSharedWallAndFits()
    {
        var b = new PlanBuilder()
            .Room("hall", "Hall", RoomType.Hall, 0f, 0f, 6f, 1.4f)
            .Room("bed", "Bed", RoomType.Bedroom, 0f, 1.4f, 3f, 3f);
        b.DoorBetween("hall", "bed", 0.914f);

        var level = b.Build();

        CollectionAssert.IsEmpty(b.Warnings);
        Assert.AreEqual(1, level.openings.Count);

        var opening = level.openings[0];
        var wall = FindWall(level, opening.wallId);
        Assert.IsNotNull(wall, "The opening must reference a real wall.");
        Assert.IsTrue(OpeningFit.IsValid(opening, wall, level));

        // Centred in the 3 m overlap, on the wall that spans exactly that overlap.
        Assert.AreEqual(3f, WallLayout.WallLength(wall), 1e-3f);
        Assert.AreEqual(1.5f, opening.offset, 1e-3f);
    }

    [Test]
    public void UnknownFurnitureKey_Warns()
    {
        var b = new PlanBuilder().Room("a", "A", RoomType.Living, 0f, 0f, 4f, 4f);
        b.Against("not_a_real_item", "a", PlanEdge.South, 0.5f);

        b.Build();

        Assert.AreEqual(1, b.Warnings.Count);
        Assert.That(b.Warnings[0], Does.Contain("not a furniture catalog id"));
    }

    [Test]
    public void AgainstAWall_SeatsTheItemInsideTheRoomFacingIn()
    {
        var level = new PlanBuilder()
            .Room("a", "A", RoomType.Bedroom, 0f, 0f, 4f, 4f)
            .Against("twin_bed", "a", PlanEdge.North, 0.5f)
            .Build();

        var bed = level.furniture[0];
        Assert.AreEqual(180f, bed.rotationY, 1e-3f, "Against the north wall means facing south.");

        // twin_bed is 0.99 x 2.03; its head sits against z = 4 minus half a wall plus the inset.
        var item = SampleFurniture.Get("twin_bed");
        float expectedZ = 4f - (0.5f * HomeConventions.DEFAULT_WALL_THICKNESS + 0.02f) - 0.5f * item.depth;
        Assert.AreEqual(expectedZ, bed.position[2], 2e-3f);
        Assert.AreEqual(0f, bed.position[1], 1e-4f, "Ground floor items sit at the level elevation.");
        CollectionAssert.AreEqual(new[] { item.width, item.height, item.depth }, bed.boxSizeMeters);
    }

    [Test]
    public void MountedItem_PicksTheFaceLookingIntoItsRoom()
    {
        var level = new PlanBuilder()
            .Room("a", "A", RoomType.Bathroom, 0f, 0f, 2f, 2f)
            .Room("b", "B", RoomType.Bedroom, 2f, 0f, 3f, 2f)
            .Mount("grab_bar_24", "a", PlanEdge.East, 0.5f)
            .Build();

        var mount = level.wallMounted[0];
        var wall = FindWall(level, mount.wallId);
        Assert.IsNotNull(wall);

        // The shared wall runs +Z, so its "left" face is -X — the bathroom side.
        Assert.AreEqual(WallSide.Left, mount.side);
        Assert.AreEqual(0.84f, mount.mountHeight, 1e-3f, "Mount height comes from the catalog entry.");
    }

    // ===========================================================================================
    // Shared assertions, also used by SampleHomesTests
    // ===========================================================================================

    internal static WallDef FindWall(LevelDef level, string id)
    {
        foreach (var w in level.walls) if (w.id == id) return w;
        return null;
    }

    /// <summary>
    /// No wall endpoint may sit strictly inside another wall's span. That is the T-junction condition
    /// WallMeshBuilder.ComputeExtensions cannot weld, and it renders as a notch up to half a wall
    /// thickness wide.
    /// </summary>
    internal static void AssertNoInteriorEndpoints(LevelDef level)
    {
        foreach (var w in level.walls)
        {
            Vector2 a = P(w.a), bb = P(w.b);
            foreach (var other in level.walls)
            {
                if (ReferenceEquals(other, w)) continue;
                foreach (var p in new[] { P(other.a), P(other.b) })
                {
                    float d = HomeMetrics.PointSegmentDistance(p, a, bb);
                    if (d > 1e-3f) continue;                       // not on this wall at all
                    if ((p - a).magnitude < 1e-3f) continue;       // shares the start
                    if ((p - bb).magnitude < 1e-3f) continue;      // shares the end
                    Assert.Fail($"Wall {other.id}'s endpoint {p} lands mid-span of wall {w.id} "
                              + $"({a} -> {bb}) — that T-junction will not weld.");
                }
            }
        }
    }

    /// <summary>Two collinear walls must never cover the same stretch of their shared line.</summary>
    internal static void AssertNoOverlaps(LevelDef level)
    {
        for (int i = 0; i < level.walls.Count; i++)
        for (int j = i + 1; j < level.walls.Count; j++)
        {
            var p = level.walls[i];
            var q = level.walls[j];
            if (!Collinear(p, q, out float pLo, out float pHi, out float qLo, out float qHi)) continue;

            float overlap = Mathf.Min(pHi, qHi) - Mathf.Max(pLo, qLo);
            Assert.LessOrEqual(overlap, 1e-3f,
                $"Walls {p.id} and {q.id} overlap by {overlap:0.###} m on the same line.");
        }
    }

    private static bool Collinear(WallDef p, WallDef q, out float pLo, out float pHi,
                                 out float qLo, out float qHi)
    {
        pLo = pHi = qLo = qHi = 0f;
        bool pVert = Mathf.Abs(p.a[0] - p.b[0]) < 1e-3f;
        bool qVert = Mathf.Abs(q.a[0] - q.b[0]) < 1e-3f;
        if (pVert != qVert) return false;

        if (pVert)
        {
            if (Mathf.Abs(p.a[0] - q.a[0]) > 1e-3f) return false;
            Span(p.a[1], p.b[1], out pLo, out pHi);
            Span(q.a[1], q.b[1], out qLo, out qHi);
        }
        else
        {
            if (Mathf.Abs(p.a[1] - q.a[1]) > 1e-3f) return false;
            Span(p.a[0], p.b[0], out pLo, out pHi);
            Span(q.a[0], q.b[0], out qLo, out qHi);
        }
        return true;
    }

    private static void Span(float u, float v, out float lo, out float hi)
    {
        lo = Mathf.Min(u, v);
        hi = Mathf.Max(u, v);
    }

    private static bool HasEndpointAt(LevelDef level, Vector2 p)
    {
        foreach (var w in level.walls)
            if ((P(w.a) - p).magnitude < 1e-3f || (P(w.b) - p).magnitude < 1e-3f) return true;
        return false;
    }

    private static List<WallDef> WallsOnVerticalLine(LevelDef level, float x)
    {
        var list = new List<WallDef>();
        foreach (var w in level.walls)
            if (Mathf.Abs(w.a[0] - x) < 1e-3f && Mathf.Abs(w.b[0] - x) < 1e-3f) list.Add(w);
        return list;
    }

    private static List<WallDef> WallsOnHorizontalLine(LevelDef level, float z)
    {
        var list = new List<WallDef>();
        foreach (var w in level.walls)
            if (Mathf.Abs(w.a[1] - z) < 1e-3f && Mathf.Abs(w.b[1] - z) < 1e-3f) list.Add(w);
        return list;
    }

    private static Vector2 P(float[] v) => new Vector2(v[0], v[1]);
}
