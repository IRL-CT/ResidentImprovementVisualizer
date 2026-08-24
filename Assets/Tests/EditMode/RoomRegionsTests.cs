using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// RoomRegions derives the rooms from the wall graph, and until now nothing pinned it: the code and
// docs both referenced tests that did not exist. These are them.
//
// The first fixture is the property the design note calls the one that matters most: Find must
// reproduce the rooms of all six sample plans exactly. PlanBuilder derives those walls from room
// rectangles by a completely different (axis-aligned, span-union) route, so an arbitrary-angle face
// walk agreeing corner for corner is the cheapest guarantee this cannot mangle a home someone has.
//
// The rest pin the 2026-08 room-split bug: WallLinker welds a T-junction onto the DRAWN endpoint (up
// to ContactEps off the through-wall), and when SurvivingCuts refuses the cut, the through-wall stays
// whole. Find's step C must still insert the junction vertex. Within min(ContactEps, half the wall's
// thickness), or the partition is pruned as a dangling spur and the room silently never splits.
[TestFixture]
public class RoomRegionsTests
{
    // Direct corner compare vs. projected-extra-vertex compare: two tolerances on purpose. A corner
    // is exact at 1e-6; a vertex checked by projecting onto an authored edge costs ~1 ULP of that
    // edge's length (1.5e-6 across a 12.5 m hall), hence 1e-5 there. See docs/design/walls-and-rooms.md.
    private const float CornerTol = 1e-6f;
    private const float EdgeTol = 1e-5f;
    private const float AreaTol = 1e-4f;

    // ---- the six samples -----------------------------------------------------------------------

    private static IEnumerable<string> SampleKeys
    {
        get { foreach (var s in SampleHomes.All) yield return s.key; }
    }

    [Test, TestCaseSource(nameof(SampleKeys))]
    public void Find_ReproducesTheSamplePlanExactly(string key)
    {
        var doc = SampleHomes.Build(key);
        foreach (var level in doc.variants[0].levels)
        {
            var regions = RoomRegions.Find(level);
            Assert.AreEqual(level.rooms.Count, regions.Count,
                $"{key}: the walls close off a different number of areas than the plan has rooms.");

            var taken = new bool[regions.Count];
            foreach (var room in level.rooms)
            {
                var poly = PolygonTriangulator.ToVector2(room.polygon);
                int match = -1;
                for (int r = 0; r < regions.Count; r++)
                {
                    if (taken[r]) continue;
                    if (ContainsEveryCorner(regions[r].ring, poly, CornerTol)) { match = r; break; }
                }
                Assert.GreaterOrEqual(match, 0,
                    $"{key}: no derived region carries every authored corner of '{room.name}'.");
                taken[match] = true;

                var ring = regions[match].ring;
                foreach (var v in ring)
                {
                    Assert.LessOrEqual(DistToBoundary(v, poly), EdgeTol,
                        $"{key}: region for '{room.name}' has a vertex off the authored boundary.");
                }
                Assert.AreEqual(PolygonTriangulator.Area(poly), regions[match].area, AreaTol,
                    $"{key}: region area for '{room.name}' drifted.");
            }
        }
    }

    [Test]
    public void Find_NeverMutatesTheWalls()
    {
        var level = SampleHomes.Build("studio_apartment").variants[0].levels[0];
        var before = new List<float[]>();
        foreach (var w in level.walls)
        {
            before.Add((float[])w.a.Clone());
            before.Add((float[])w.b.Clone());
        }

        RoomRegions.Find(level);

        int k = 0;
        foreach (var w in level.walls)
        {
            CollectionAssert.AreEqual(before[k++], w.a, "Find mutated a wall endpoint.");
            CollectionAssert.AreEqual(before[k++], w.b, "Find mutated a wall endpoint.");
        }
    }

    // ---- the refused-cut split -----------------------------------------------------------------
    //
    // A 6 x 4 rectangle. The partition at x = 2 stops 15 mm short of both long walls: exactly what
    // WallLinker leaves behind when it detected the junction (welding it to the drawn endpoint) but
    // refused the cut. 15 mm is over WeldEps (1 mm) and under min(ContactEps, thickness/2) = 20 mm,
    // so it is inside the wall's rendered body: no visible gap, and the room must split.

    [Test]
    public void RefusedCut_PartitionEndingInsideTheWallBody_SplitsTheRoom()
    {
        var level = Rect6x4();
        level.walls.Add(Wall("part", 2f, 0.015f, 2f, 3.985f));

        var regions = RoomRegions.Find(level);

        Assert.AreEqual(2, regions.Count, "A partition ending inside the wall body must split the room.");

        // Not 24: the derived boundary bends THROUGH the off-line junction vertices: the same
        // convention SplitWall uses for a surviving cut. Trimming a sliver of half-base x gap
        // (0.5 * 6 * 0.015) off each long wall. That bend is what makes Sync idempotent.
        float total = regions[0].area + regions[1].area;
        Assert.AreEqual(24f - 2f * (0.5f * 6f * 0.015f), total, 1e-3f,
            "The two halves must cover the rectangle less the two junction slivers.");
        foreach (var r in regions)
            Assert.Greater(r.area, 7f, "Each side of the x = 2 partition is a real room.");
    }

    [Test]
    public void FreeDraw_GapWiderThanTheWallBody_DoesNotSplit()
    {
        // 50 mm short of each wall: outside min(ContactEps, thickness/2), a gap you can see.
        var level = Rect6x4();
        level.walls.Add(Wall("part", 2f, 0.05f, 2f, 3.95f));

        var regions = RoomRegions.Find(level);

        Assert.AreEqual(1, regions.Count, "A visible gap must not close a room.");
        Assert.AreEqual(24f, regions[0].area, 1e-3f);
    }

    [Test]
    public void BareXCrossing_InventsNoVertexAndNoRoom()
    {
        // The free-drawn wall crosses both long walls with no shared vertex anywhere. WallMeshBuilder
        // renders that as a notch; Find must not report rooms the plan does not draw.
        var level = Rect6x4();
        level.walls.Add(Wall("cross", 2f, -0.5f, 2f, 4.5f));

        var regions = RoomRegions.Find(level);

        Assert.AreEqual(1, regions.Count, "A bare X crossing must not split the room.");
        Assert.AreEqual(24f, regions[0].area, 1e-3f);
    }

    // ---- Sync over the split -------------------------------------------------------------------

    [Test]
    public void Sync_SplitKeepsIdentity_AndIsIdempotent()
    {
        var level = Rect6x4();
        level.rooms.Add(new RoomDef
        {
            id = "r1",
            name = "Studio",
            roomType = RoomType.Living,
            polygon = new[]
            {
                new[] { 0f, 0f }, new[] { 6f, 0f }, new[] { 6f, 4f }, new[] { 0f, 4f },
            },
        });
        level.walls.Add(Wall("part", 2f, 0.015f, 2f, 3.985f));

        int changes = RoomRegions.Sync(level);

        Assert.Greater(changes, 0);
        Assert.AreEqual(2, level.rooms.Count);

        var kept = level.rooms.Find(r => r.id == "r1");
        Assert.IsNotNull(kept, "The original room must keep its id across a split.");
        Assert.AreEqual("Studio", kept.name, "Sync writes polygon and NOTHING else.");
        Assert.AreEqual(RoomType.Living, kept.roomType);
        Assert.Greater(PolygonTriangulator.Area(PolygonTriangulator.ToVector2(kept.polygon)), 10f,
            "The room's inscribed center lies in the larger half, so r1 keeps that side.");

        var newcomer = level.rooms.Find(r => r.id != "r1");
        Assert.AreEqual(RoomType.Untyped, newcomer.roomType, "The other half is a new Untyped room.");
        Assert.IsEmpty(newcomer.name ?? "");

        Assert.AreEqual(0, RoomRegions.Sync(level), "A second Sync must change nothing.");
        Assert.IsTrue(RoomRegions.RoomsMatch(level, RoomRegions.Find(level)),
            "After Sync the Detect button must have nothing to offer.");
    }

    [Test]
    public void RoomsMatch_SeesAShapeMismatchAtEqualCounts()
    {
        // One region, one stored room (counts agree) but the stored polygon has drifted off its
        // walls. A count-only gate hides the Detect button here; RoomsMatch must not.
        var level = Rect6x4();
        level.rooms.Add(new RoomDef
        {
            id = "r1",
            name = "Studio",
            roomType = RoomType.Living,
            polygon = new[]
            {
                new[] { 0.5f, 0.5f }, new[] { 6f, 0f }, new[] { 6f, 4f }, new[] { 0f, 4f },
            },
        });

        var regions = RoomRegions.Find(level);
        Assert.AreEqual(level.rooms.Count, regions.Count, "Counts agree by construction here.");
        Assert.IsFalse(RoomRegions.RoomsMatch(level, regions),
            "Equal counts with different shapes is exactly the drift RoomsMatch exists to see.");

        RoomRegions.Sync(level);
        Assert.IsTrue(RoomRegions.RoomsMatch(level, RoomRegions.Find(level)));
        Assert.AreEqual("r1", level.rooms[0].id, "Repair rewrites the polygon, not the identity.");
    }

    // ---- carved islands ------------------------------------------------------------------------
    //
    // A closed loop drawn INSIDE a room (detached, or touching the boundary at one vertex) is its
    // own room, and the enclosing region must not claim its floor too: one room, one space. The
    // enclosing ring is bridge-cut (still one single ring; the two coincident bridge edges cancel in
    // every even-odd test), so regions never overlap.

    [Test]
    public void NestedLoop_OuterRegionExcludesTheIsland()
    {
        var level = Rect6x4();
        level.walls.Add(Wall("i_s", 2f, 1f, 3f, 1f));
        level.walls.Add(Wall("i_e", 3f, 1f, 3f, 2f));
        level.walls.Add(Wall("i_n", 3f, 2f, 2f, 2f));
        level.walls.Add(Wall("i_w", 2f, 2f, 2f, 1f));

        var regions = RoomRegions.Find(level);

        Assert.AreEqual(2, regions.Count, "The island is a room and the ring around it is a room.");
        Assert.AreEqual(23f, regions[0].area, 1e-3f, "The outer room excludes the island's floor.");
        Assert.AreEqual(1f, regions[1].area, 1e-3f);

        var mid = new Vector2(2.5f, 1.5f);
        Assert.IsFalse(HomeMetrics.PointInPolygon(mid, regions[0].ring),
            "A point inside the island must read as OUTSIDE the carved outer ring.");
        Assert.IsTrue(HomeMetrics.PointInPolygon(mid, regions[1].ring));
        Assert.IsTrue(HomeMetrics.PointInPolygon(new Vector2(0.5f, 0.5f), regions[0].ring));
    }

    [Test]
    public void VertexTouchingIsland_CarvedThroughTheSharedCorner()
    {
        // The triangle touches the shell only at (0, 0); its edges run through the interior. The
        // keyhole face repeats that vertex, EmitFace splits it into outer + discarded inner, and the
        // carve must put the cut back through the shared corner.
        var level = Rect6x4();
        level.walls.Add(Wall("t1", 0f, 0f, 2f, 1f));
        level.walls.Add(Wall("t2", 2f, 1f, 1f, 2f));
        level.walls.Add(Wall("t3", 1f, 2f, 0f, 0f));

        var regions = RoomRegions.Find(level);

        Assert.AreEqual(2, regions.Count);
        Assert.AreEqual(22.5f, regions[0].area, 1e-3f, "The outer ring is carved at the shared corner.");
        Assert.AreEqual(1.5f, regions[1].area, 1e-3f);
        Assert.IsFalse(HomeMetrics.PointInPolygon(new Vector2(1f, 1f), regions[0].ring),
            "The triangle's interior must not belong to the outer room.");
    }

    [Test]
    public void Sync_NestedLoop_OuterKeepsIdentity_AndIsIdempotent()
    {
        var level = Rect6x4();
        level.rooms.Add(new RoomDef
        {
            id = "r1",
            name = "Studio",
            roomType = RoomType.Living,
            polygon = new[]
            {
                new[] { 0f, 0f }, new[] { 6f, 0f }, new[] { 6f, 4f }, new[] { 0f, 4f },
            },
        });
        // The island sits in a corner, away from the rectangle's inscribed center, so r1's identity
        // follows the annulus and the island becomes the Untyped newcomer.
        level.walls.Add(Wall("i_s", 4.5f, 3f, 5.5f, 3f));
        level.walls.Add(Wall("i_e", 5.5f, 3f, 5.5f, 3.5f));
        level.walls.Add(Wall("i_n", 5.5f, 3.5f, 4.5f, 3.5f));
        level.walls.Add(Wall("i_w", 4.5f, 3.5f, 4.5f, 3f));

        int changes = RoomRegions.Sync(level);

        Assert.Greater(changes, 0);
        Assert.AreEqual(2, level.rooms.Count);

        var kept = level.rooms.Find(r => r.id == "r1");
        Assert.IsNotNull(kept, "The enclosing room must keep its id.");
        Assert.AreEqual("Studio", kept.name, "Sync writes polygon and NOTHING else.");
        Assert.AreEqual(23.5f, PolygonTriangulator.Area(PolygonTriangulator.ToVector2(kept.polygon)),
            1e-3f, "The outer room's area shrinks by the island's.");

        var newcomer = level.rooms.Find(r => r.id != "r1");
        Assert.AreEqual(RoomType.Untyped, newcomer.roomType);
        Assert.AreEqual(0.5f, PolygonTriangulator.Area(PolygonTriangulator.ToVector2(newcomer.polygon)), 1e-3f);

        Assert.AreEqual(0, RoomRegions.Sync(level), "A second Sync must change nothing.");
        Assert.IsTrue(RoomRegions.RoomsMatch(level, RoomRegions.Find(level)),
            "After Sync the Detect button must have nothing to offer.");
    }

    [Test]
    public void Carve_IsDeterministic()
    {
        // RoomsMatch compares floats exactly, so the bridge must land on identical bits every run.
        var level = Rect6x4();
        level.walls.Add(Wall("i_s", 2f, 1f, 3f, 1f));
        level.walls.Add(Wall("i_e", 3f, 1f, 3f, 2f));
        level.walls.Add(Wall("i_n", 3f, 2f, 2f, 2f));
        level.walls.Add(Wall("i_w", 2f, 2f, 2f, 1f));

        var first = RoomRegions.Find(level);
        var second = RoomRegions.Find(level);

        Assert.AreEqual(first.Count, second.Count);
        for (int r = 0; r < first.Count; r++)
        {
            Assert.AreEqual(first[r].ring.Count, second[r].ring.Count);
            for (int v = 0; v < first[r].ring.Count; v++)
                Assert.AreEqual(first[r].ring[v], second[r].ring[v],
                    "The bridge cut must land on identical floats every run.");
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static WallDef Wall(string id, float ax, float az, float bx, float bz) => new WallDef
    {
        id = id,
        a = new[] { ax, az },
        b = new[] { bx, bz },
    };

    /// <summary>A closed 6 x 4 rectangle of default-thickness walls, corners shared exactly.</summary>
    private static LevelDef Rect6x4() => new LevelDef
    {
        id = "l1",
        walls = new List<WallDef>
        {
            Wall("s", 0f, 0f, 6f, 0f),
            Wall("e", 6f, 0f, 6f, 4f),
            Wall("n", 6f, 4f, 0f, 4f),
            Wall("w", 0f, 4f, 0f, 0f),
        },
        openings = new List<OpeningDef>(),
        rooms = new List<RoomDef>(),
    };

    private static bool ContainsEveryCorner(List<Vector2> ring, List<Vector2> corners, float tol)
    {
        foreach (var c in corners)
        {
            bool found = false;
            foreach (var v in ring)
            {
                if (Vector2.Distance(v, c) <= tol) { found = true; break; }
            }
            if (!found) return false;
        }
        return true;
    }

    private static float DistToBoundary(Vector2 p, List<Vector2> poly)
    {
        float best = float.MaxValue;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 a = poly[i], b = poly[(i + 1) % poly.Count];
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 < 1e-12f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            best = Mathf.Min(best, Vector2.Distance(a + t * ab, p));
        }
        return best;
    }
}
