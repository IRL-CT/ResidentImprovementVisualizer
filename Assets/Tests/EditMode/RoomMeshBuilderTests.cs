using NUnit.Framework;
using UnityEngine;

// The winding decision lives here rather than in the triangulator, so this is where it gets checked.
// Looking down at the XZ plane from +Y, a counter-clockwise (x, z) polygon also reads
// counter-clockwise on screen: the wrong way round for an up-facing floor under Unity's
// clockwise-is-front rule. Floors therefore reverse the triangulator's order and ceilings keep it.
//
// An inverted floor is invisible from above and solid from below, which in a first-person walkthrough
// reads as "the floor disappeared". Cheap to assert, expensive to chase.
[TestFixture]
public class RoomMeshBuilderTests
{
    private Mesh _mesh;

    [TearDown]
    public void TearDown()
    {
        if (_mesh != null) Object.DestroyImmediate(_mesh);
        _mesh = null;
    }

    [Test]
    public void Floor_FacesUp()
    {
        _mesh = RoomMeshBuilder.BuildFloor(Room(), Level());

        Assert.IsNotNull(_mesh);
        AssertEveryTriangleFaces(_mesh, Vector3.up);
    }

    [Test]
    public void Ceiling_FacesDown()
    {
        _mesh = RoomMeshBuilder.BuildCeiling(Room(), Level());

        Assert.IsNotNull(_mesh);
        AssertEveryTriangleFaces(_mesh, Vector3.down);
    }

    [Test]
    public void Floor_SitsAtTheLevelElevation()
    {
        var level = Level();
        level.elevation = 3.2f;

        _mesh = RoomMeshBuilder.BuildFloor(Room(), level);

        Assert.AreEqual(3.2f, _mesh.bounds.min.y, 1e-3f);
        Assert.AreEqual(3.2f, _mesh.bounds.max.y, 1e-3f);
    }

    [Test]
    public void Ceiling_SitsAtElevationPlusCeilingHeight()
    {
        var level = Level();
        level.elevation = 3.2f;
        level.ceilingHeight = 2.44f;

        _mesh = RoomMeshBuilder.BuildCeiling(Room(), level);

        Assert.AreEqual(3.2f + 2.44f, _mesh.bounds.min.y, 1e-3f);
    }

    [Test]
    public void RoomCeilingHeightOverridesTheLevel()
    {
        var room = Room();
        room.ceilingHeight = 3.0f;   // a vaulted living room in an otherwise 8-ft home

        Assert.AreEqual(3.0f, RoomMeshBuilder.EffectiveCeilingHeight(room, Level()), 1e-4f);

        room.ceilingHeight = 0f;
        Assert.AreEqual(2.44f, RoomMeshBuilder.EffectiveCeilingHeight(room, Level()), 1e-4f);
        Assert.AreEqual(HomeConventions.DEFAULT_CEILING_HEIGHT,
                        RoomMeshBuilder.EffectiveCeilingHeight(room, new LevelDef()), 1e-4f);
    }

    [Test]
    public void UvsAreWorldMetresSoFinishesStayContinuousBetweenRooms()
    {
        _mesh = RoomMeshBuilder.BuildFloor(Room(), Level());

        var bounds = new Bounds(_mesh.uv[0], Vector3.zero);
        foreach (var uv in _mesh.uv) bounds.Encapsulate(uv);

        Assert.AreEqual(0f, bounds.min.x, 1e-3f);
        Assert.AreEqual(4f, bounds.max.x, 1e-3f);
        Assert.AreEqual(3f, bounds.max.y, 1e-3f);
    }

    [Test]
    public void FloorArea()
    {
        Assert.AreEqual(12f, RoomMeshBuilder.FloorArea(Room()), 1e-3f);
    }

    [Test]
    public void DegenerateRoom_ProducesNoMesh()
    {
        Assert.IsNull(RoomMeshBuilder.BuildFloor(new RoomDef { id = "r" }, Level()));
        Assert.IsNull(RoomMeshBuilder.BuildFloor(new RoomDef
        {
            id = "r", polygon = new[] { new[] { 0f, 0f }, new[] { 1f, 0f } },
        }, Level()));
        Assert.IsNull(RoomMeshBuilder.BuildCeiling(null, Level()));
    }

    // ---------------------------------------------------------------------------------------

    // Under Unity's left-handed cross product, Cross(p1 - p0, p2 - p0) yields the outward normal of a
    // correctly wound front face: the same relation MeshAccum's AddQuad convention is built on.
    private static void AssertEveryTriangleFaces(Mesh mesh, Vector3 expected)
    {
        var v = mesh.vertices;
        var t = mesh.triangles;

        Assert.Greater(t.Length, 0, "mesh has no triangles");

        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            Vector3 n = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]).normalized;
            Assert.Greater(Vector3.Dot(n, expected), 0.99f,
                           $"triangle {i / 3} winds the wrong way (normal {n}, expected {expected})");
        }
    }

    private static RoomDef Room() => new RoomDef
    {
        id = "r1",
        name = "Bedroom",
        roomType = RoomType.Bedroom,
        polygon = new[]
        {
            new[] { 0f, 0f }, new[] { 4f, 0f }, new[] { 4f, 3f }, new[] { 0f, 3f },
        },
    };

    private static LevelDef Level() => new LevelDef
    {
        id = "L0", elevation = 0f, ceilingHeight = 2.44f, wallThickness = 0.114f,
    };
}
