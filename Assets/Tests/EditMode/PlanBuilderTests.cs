using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// PlanBuilder exists because the render path never complains about bad geometry. It clamps, skips, or
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

    // -------------------------------------------------------------------------------------------
    // Rooms built from more than one rectangle
    //
    // An L-shaped room described as two ROOMS gets a full-height wall along the edge between them,
    // it renders, it encloses, and nothing anywhere reports it, which is the exact class of silent
    // failure this whole file exists to close. Declared as a room and a PART of it instead, the wall
    // has to disappear and the two rectangles have to come out as one floor.
    // -------------------------------------------------------------------------------------------

    [Test]
    public void TwoRoomsSharingAnEdge_KeepTheWallBetweenThem()
    {
        // The control for the case below: same two rectangles, declared as separate rooms.
        var level = new PlanBuilder()
            .Room("living", "Living", RoomType.Living, 0f, 0f, 5f, 4f)
            .Room("nook", "Nook", RoomType.Living, 5f, 0f, 3f, 2f)
            .Build();

        // The line x = 5 splits at z = 2 where the nook's north wall T-joins it, so it is two pieces
        //, but they cover the whole 4 m, because every stretch of it is a wall between two rooms or
        // between a room and the outside. The part case below is what has to differ: 2 m, not 4.
        var onLine = WallsOnVerticalLine(level, 5f);
        float total = 0f;
        foreach (var w in onLine) total += WallLayout.WallLength(w);
        Assert.AreEqual(4f, total, 1e-3f, "Two separate rooms must be walled off from each other.");

        Assert.AreEqual(2, level.rooms.Count);
    }

    [Test]
    public void APartOfARoom_HasNoWallBetweenItAndTheRestOfTheRoom()
    {
        var builder = new PlanBuilder()
            .Room("living", "Living", RoomType.Living, 0f, 0f, 5f, 4f);
        builder.RoomPart("living_nook", "living", 5f, 0f, 3f, 2f);
        var level = builder.Build();

        CollectionAssert.IsEmpty(builder.Warnings);

        // x = 5 is shared between the two pieces for z 0..2, and is the room's own east wall above
        // that. The shared stretch must be gone; the rest must survive.
        var onLine = WallsOnVerticalLine(level, 5f);
        Assert.AreEqual(1, onLine.Count, "Only the part of x = 5 outside the room should remain.");
        Assert.AreEqual(2f, WallLayout.WallLength(onLine[0]), 1e-3f,
                        "The surviving piece is z 2..4; the shared stretch z 0..2 is interior.");

        AssertNoOverlaps(level);
        AssertNoInteriorEndpoints(level);
    }

    [Test]
    public void APartOfARoom_ComesOutAsOneRoomWithTheUnionAsItsFloor()
    {
        var builder = new PlanBuilder()
            .Room("living", "Living", RoomType.Living, 0f, 0f, 5f, 4f);
        builder.RoomPart("living_nook", "living", 5f, 0f, 3f, 2f);
        var level = builder.Build();

        Assert.AreEqual(1, level.rooms.Count, "Two rectangles, one room.");
        Assert.AreEqual("r_living", level.rooms[0].id, "The room keeps the parent's id.");
        Assert.AreEqual("Living", level.rooms[0].name);

        var poly = PolygonTriangulator.ToVector2(level.rooms[0].polygon);
        Assert.AreEqual(6, poly.Count, "An L has six corners once collinear points are stripped.");
        Assert.AreEqual(5f * 4f + 3f * 2f, Mathf.Abs(PolygonTriangulator.SignedArea(poly)), 1e-3f);
        Assert.Greater(PolygonTriangulator.SignedArea(poly), 0f,
                       "CCW, like every other room polygon this builder emits.");

        // Every corner of the union, and no corner that is only a corner of one rectangle.
        AssertHasCorner(poly, new Vector2(0f, 0f));
        AssertHasCorner(poly, new Vector2(8f, 0f));
        AssertHasCorner(poly, new Vector2(8f, 2f));
        AssertHasCorner(poly, new Vector2(5f, 2f));
        AssertHasCorner(poly, new Vector2(5f, 4f));
        AssertHasCorner(poly, new Vector2(0f, 4f));
    }

    [Test]
    public void APartThatOnlyTouchesAtACorner_IsRefusedWithAWarning()
    {
        // A corner meeting pinches the room to nothing there. It is not a shape that can be walked
        // through, and inventing a bounding box would claim floor the plan does not show.
        var builder = new PlanBuilder()
            .Room("living", "Living", RoomType.Living, 0f, 0f, 4f, 4f);
        builder.RoomPart("living_odd", "living", 4f, 4f, 3f, 3f);
        var level = builder.Build();

        Assert.AreEqual(1, builder.Warnings.Count, string.Join(" / ", builder.Warnings));
        StringAssert.Contains("do not join", builder.Warnings[0]);

        var poly = PolygonTriangulator.ToVector2(level.rooms[0].polygon);
        Assert.AreEqual(4f * 4f, Mathf.Abs(PolygonTriangulator.SignedArea(poly)), 1e-3f,
                        "It falls back to the rectangle it was declared with.");
    }

    [Test]
    public void APartOfAnUnknownRoom_IsRefusedRatherThanBecomingItsOwnRoom()
    {
        var builder = new PlanBuilder()
            .Room("living", "Living", RoomType.Living, 0f, 0f, 4f, 4f);
        builder.RoomPart("orphan", "conservatory", 4f, 0f, 3f, 3f);
        var level = builder.Build();

        Assert.AreEqual(1, builder.Warnings.Count, string.Join(" / ", builder.Warnings));
        StringAssert.Contains("conservatory", builder.Warnings[0]);
        Assert.AreEqual(1, level.rooms.Count);
    }

    [Test]
    public void FurnitureInOnePart_StillCountsAsAnObstacleInAnother()
    {
        // The two pieces are one room, so two items sliding toward the shared corner have to see each
        // other. Keying the bookkeeping on the rectangle rather than on the room would let them
        // overlap, and nothing downstream would say so.
        var builder = new PlanBuilder()
            .Room("living", "Living", RoomType.Living, 0f, 0f, 5f, 4f);
        builder.RoomPart("living_nook", "living", 5f, 0f, 3f, 2f);
        builder.Against("sofa", "living", PlanEdge.South, 1f);
        builder.Against("armchair", "living_nook", PlanEdge.South, 0f);
        var level = builder.Build();

        CollectionAssert.IsEmpty(builder.Warnings);
        Assert.AreEqual(2, level.furniture.Count);

        var a = HomeMetrics.FootprintOf(level.furniture[0]);
        var b = HomeMetrics.FootprintOf(level.furniture[1]);
        Assert.IsFalse(a.Overlaps(b), "Items in two pieces of one room must not be placed on top "
                                    + "of each other.");
    }

    [Test]
    public void APartIsAddressableForOpeningsOnItsOwnWalls()
    {
        var builder = new PlanBuilder()
            .Room("living", "Living", RoomType.Living, 0f, 0f, 5f, 4f);
        builder.RoomPart("living_nook", "living", 5f, 0f, 3f, 2f);
        builder.Window("living_nook", PlanEdge.East, 0.5f, 1.2f);
        var level = builder.Build();

        CollectionAssert.IsEmpty(builder.Warnings);
        Assert.AreEqual(1, level.openings.Count, "A window on the nook's own east wall.");

        var host = level.walls.Find(w => w.id == level.openings[0].wallId);
        Assert.IsNotNull(host, "The opening has to resolve to a wall, or the renderer skips it.");
        Assert.AreEqual(8f, host.a[0], 1e-3f, "That wall is the nook's east edge, x = 8.");
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
        Assert.AreEqual(RoomType.Bedroom, room.roomType, "The type is what picks the floor finish.");
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

        // Centered in the 3 m overlap, on the wall that spans exactly that overlap.
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

        // The shared wall runs +Z, so its "left" face is -X: the bathroom side.
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
                              + $"({a} -> {bb}), so that T-junction will not weld.");
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

    private static void AssertHasCorner(IReadOnlyList<Vector2> poly, Vector2 want)
    {
        foreach (var p in poly) if ((p - want).sqrMagnitude < 1e-6f) return;
        Assert.Fail($"No corner at {want}. Got: {string.Join(", ", poly)}");
    }

    private static Vector2 P(float[] v) => new Vector2(v[0], v[1]);
}
