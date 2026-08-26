using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// The room a residence you start yourself opens on. Two things are pinned here: that the geometry is
// sound (four welded walls, one room, 9.00 m2 on centerlines, and the wall graph agreeing with the
// stored room so Detect rooms never appears on a fresh residence), and that IsUntouched recognises
// exactly that room and nothing else. The second is what SketchInstall.IsEmpty now rests on, and it
// rests in turn on Build being deterministic.
[TestFixture]
public class StarterRoomTests
{
    [Test]
    public void Build_IsFourWallsOneRoomAndNothingElse()
    {
        var level = StarterRoom.Build();

        Assert.AreEqual(4, level.walls.Count, "A rectangle is four walls.");
        Assert.AreEqual(1, level.rooms.Count);
        Assert.AreEqual(0, level.openings.Count, "The starter room ships no door, deliberately.");
        Assert.AreEqual(0, level.furniture.Count);
        Assert.AreEqual(0, level.wallMounted.Count);
    }

    [Test]
    public void Build_LeavesPlanBuilderWithNothingUnresolved()
    {
        // The same declaration Build makes. PlanBuilder reports what it could not resolve rather than
        // throwing, so an empty Warnings list is the only thing that says the plan came out whole.
        var b = new PlanBuilder();
        b.Room(StarterRoom.RoomKey, StarterRoom.RoomName, RoomType.Living,
               -0.5f * StarterRoom.Side, -0.5f * StarterRoom.Side,
               StarterRoom.Side, StarterRoom.Side);
        b.Build();

        CollectionAssert.IsEmpty(b.Warnings);
    }

    [Test]
    public void Room_IsANineSquareMeterLivingRoom()
    {
        var room = StarterRoom.Build().rooms[0];

        Assert.AreEqual(StarterRoom.RoomName, room.name);
        Assert.AreEqual(RoomType.Living, room.roomType, "Living is what puts it on the oak floor.");
        // 3 x 3 on wall CENTERLINES: the convention every room rectangle uses, and the figure the
        // Select tool reports.
        Assert.AreEqual(StarterRoom.Side * StarterRoom.Side, ResidenceMetrics.RoomArea(room), 1e-3f);
    }

    [Test]
    public void EveryWallEndpointMeetsAnother_SoNoCornerIsNotched()
    {
        var level = StarterRoom.Build();

        foreach (var w in level.walls)
        {
            Assert.AreEqual(1, Meeting(level, w, w.a), "A wall start must land on exactly one other end.");
            Assert.AreEqual(1, Meeting(level, w, w.b), "A wall end must land on exactly one other end.");
        }
    }

    [Test]
    public void WallGraphAgreesWithTheStoredRoom_SoDetectRoomsNeverAppears()
    {
        var level = StarterRoom.Build();
        var regions = RoomRegions.Find(level);

        Assert.AreEqual(1, regions.Count, "One enclosed area.");
        Assert.IsTrue(RoomRegions.RoomsMatch(level, regions),
                      "Detect rooms is gated on this, and must not offer itself on a new residence.");
    }

    [Test]
    public void Build_IsDeterministic()
    {
        // IsUntouched recognises a starter room by rebuilding one and comparing, so a random id stem
        // or a drifting coordinate would quietly break the whole recognition.
        var a = StarterRoom.Build();
        var b = StarterRoom.Build();

        for (int i = 0; i < a.walls.Count; i++)
        {
            Assert.AreEqual(a.walls[i].id, b.walls[i].id);
            Assert.AreEqual(a.walls[i].a[0], b.walls[i].a[0], 0f);
            Assert.AreEqual(a.walls[i].a[1], b.walls[i].a[1], 0f);
            Assert.AreEqual(a.walls[i].b[0], b.walls[i].b[0], 0f);
            Assert.AreEqual(a.walls[i].b[1], b.walls[i].b[1], 0f);
        }
        Assert.AreEqual(a.rooms[0].id, b.rooms[0].id);
        StringAssert.StartsWith(StarterRoom.IdPrefix, a.rooms[0].id);
        StringAssert.StartsWith(StarterRoom.IdPrefix, a.walls[0].id);
    }

    [Test]
    public void IsUntouched_IsTrueForAFreshBuildAndFalseForAnEmptyStorey()
    {
        Assert.IsTrue(StarterRoom.IsUntouched(StarterRoom.Build()));
        Assert.IsFalse(StarterRoom.IsUntouched(Stories.NewLevel("Ground floor")),
                       "An empty storey is empty, not a starter room.");
        Assert.IsFalse(StarterRoom.IsUntouched(null));
    }

    [Test]
    public void IsUntouched_GoesFalseTheMomentAnythingIsDoneToTheRoom()
    {
        var renamed = StarterRoom.Build();
        renamed.rooms[0].name = "Bedroom 1";
        Assert.IsFalse(StarterRoom.IsUntouched(renamed), "Renaming it makes it theirs.");

        var retyped = StarterRoom.Build();
        retyped.rooms[0].roomType = RoomType.Bedroom;
        Assert.IsFalse(StarterRoom.IsUntouched(retyped));

        var moved = StarterRoom.Build();
        moved.walls[0].b[1] += 0.5f;
        Assert.IsFalse(StarterRoom.IsUntouched(moved));

        var resized = StarterRoom.Build();
        resized.rooms[0].polygon[2][0] += 0.5f;
        Assert.IsFalse(StarterRoom.IsUntouched(resized));

        var doored = StarterRoom.Build();
        doored.openings.Add(new OpeningDef
        {
            id = "o_0",
            wallId = doored.walls[0].id,
            offset = 0.5f * StarterRoom.Side,
            width = ResidenceConventions.DEFAULT_DOOR_WIDTH,
            height = ResidenceConventions.DEFAULT_DOOR_HEIGHT,
            kind = OpeningKind.Door,
        });
        Assert.IsFalse(StarterRoom.IsUntouched(doored));

        var furnished = StarterRoom.Build();
        furnished.furniture.Add(new ObjectInstance { instanceId = "f_0", prefabType = "armchair", scale = 1f });
        Assert.IsFalse(StarterRoom.IsUntouched(furnished));

        var mounted = StarterRoom.Build();
        mounted.wallMounted.Add(new WallMountDef
        {
            instanceId = "m_0",
            prefabType = "grab_bar_24",
            wallId = mounted.walls[0].id,
            offset = 0.5f * StarterRoom.Side,
            mountHeight = 0.9f,
        });
        Assert.IsFalse(StarterRoom.IsUntouched(mounted));

        var sensed = StarterRoom.Build();
        sensed.sensors = new List<SensorDef> { new SensorDef { id = "d_0", deviceType = "motion" } };
        Assert.IsFalse(StarterRoom.IsUntouched(sensed));
    }

    [Test]
    public void IsUntouched_SurvivesAThicknessOrCeilingEdit()
    {
        // Those are facts about the FLOOR, not work done in the room, and the walls inherit them
        // rather than storing them, so the comparison must not notice.
        var level = StarterRoom.Build();
        level.wallThickness = 0.20f;
        level.ceilingHeight = 3.0f;

        Assert.IsTrue(StarterRoom.IsUntouched(level));
    }

    [Test]
    public void SketchInstall_TreatsAnUntouchedStarterRoomAsAnEmptyFloor()
    {
        var level = StarterRoom.Build();
        Assert.IsTrue(SketchInstall.IsEmpty(level),
                      "Read this plan must still say it replaces nothing on a new residence.");

        level.furniture.Add(new ObjectInstance { instanceId = "f_0", prefabType = "armchair", scale = 1f });
        Assert.IsFalse(SketchInstall.IsEmpty(level));
        StringAssert.Contains("1 item", SketchInstall.ContentSummary(level));
    }

    [Test]
    public void Adopt_InstallsTheRoomAndKeepsTheStoreyIdentity()
    {
        // Exactly what ResidenceStore.Create does. The storey id keys its underlay and pairs it across
        // variants, so it has to survive.
        var storey = Stories.NewLevel("Ground floor", 2.5f);
        string id = storey.id;

        SketchInstall.Adopt(storey, StarterRoom.Build(), null);

        Assert.AreEqual(id, storey.id);
        Assert.AreEqual("Ground floor", storey.name);
        Assert.AreEqual(2.5f, storey.elevation, 1e-4f);
        Assert.IsTrue(StarterRoom.IsUntouched(storey));
    }

    // How many OTHER wall endpoints land on this point, within the weld radius corners close at.
    private static int Meeting(LevelDef level, WallDef self, float[] p)
    {
        int n = 0;
        var at = new Vector2(p[0], p[1]);
        foreach (var w in level.walls)
        {
            if (ReferenceEquals(w, self)) continue;
            if (Vector2.Distance(at, new Vector2(w.a[0], w.a[1])) <= WallLinker.WeldEps) n++;
            if (Vector2.Distance(at, new Vector2(w.b[0], w.b[1])) <= WallLinker.WeldEps) n++;
        }
        return n;
    }
}
