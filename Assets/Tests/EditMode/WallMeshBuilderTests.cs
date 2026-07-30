using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// Two things are worth pinning here.
//
// First the FRAME: `left` must genuinely be the left-hand side when walking a -> b, because
// materialLeft/materialRight and the wall-mount `side` field all depend on it. Get it backwards and
// every grab bar mounts on the far side of the wall from where it was placed.
//
// Second the JUNCTION EXTENSION: corners close by overlapping boxes rather than mitering them, and
// the extension uses the NEIGHBOUR's thickness — a thin wall meeting a thick one still has to reach
// the thick wall's outer face. That asymmetry is easy to "simplify" away later, so it is asserted.
[TestFixture]
public class WallMeshBuilderTests
{
    private readonly List<Object> _spawned = new List<Object>();

    [TearDown]
    public void TearDown()
    {
        foreach (var o in _spawned) if (o != null) Object.DestroyImmediate(o);
        _spawned.Clear();
    }

    // ---- frame ----

    [Test]
    public void Frame_LeftIsTheLeftHandSideWhenWalkingAToB()
    {
        // Walking east (+X), your left hand points north (+Z).
        var frame = WallMeshBuilder.BuildFrame(Wall("w", 0, 0, 5, 0), Level());

        Assert.AreEqual(Vector3.right, frame.forward);
        Assert.AreEqual(0f, frame.left.x, 1e-4f);
        Assert.AreEqual(1f, frame.left.z, 1e-4f);
    }

    [Test]
    public void Frame_ReversedWallFlipsTheSides()
    {
        var frame = WallMeshBuilder.BuildFrame(Wall("w", 5, 0, 0, 0), Level());

        Assert.AreEqual(Vector3.left, frame.forward);
        Assert.AreEqual(-1f, frame.left.z, 1e-4f);
    }

    [Test]
    public void Frame_CarriesResolvedThicknessHeightAndOrigin()
    {
        var level = Level();
        level.elevation = 3f;
        level.ceilingHeight = 2.7f;
        level.wallThickness = 0.15f;

        var frame = WallMeshBuilder.BuildFrame(Wall("w", 1, 2, 5, 2), level);

        Assert.AreEqual(new Vector3(1f, 3f, 2f), frame.origin);
        Assert.AreEqual(4f, frame.length, 1e-4f);
        Assert.AreEqual(0.15f, frame.thickness, 1e-4f);
        Assert.AreEqual(2.7f, frame.height, 1e-4f);
    }

    // ---- junction extension ----

    [Test]
    public void FreeStandingWall_GetsNoExtension()
    {
        var level = Level(Wall("w1", 0, 0, 5, 0));

        WallMeshBuilder.ComputeExtensions(level.walls[0], level, out float s, out float e);

        Assert.AreEqual(0f, s, 1e-4f);
        Assert.AreEqual(0f, e, 1e-4f);
    }

    [Test]
    public void SharedCorner_ExtendsOnlyTheTouchingEnd()
    {
        var level = Level(
            Wall("w1", 0, 0, 5, 0),
            Wall("w2", 5, 0, 5, 4));          // meets w1 at its `b` end
        level.wallThickness = 0.114f;

        WallMeshBuilder.ComputeExtensions(level.walls[0], level, out float s, out float e);

        Assert.AreEqual(0f, s, 1e-4f);
        Assert.AreEqual(0.057f, e, 1e-4f);    // half the neighbour's thickness
    }

    [Test]
    public void ExtensionUsesTheNeighboursThicknessNotItsOwn()
    {
        // A thin partition meeting a thick exterior wall must reach across the THICK wall's
        // half-width, otherwise the inside corner keeps a visible notch.
        var thin = Wall("thin", 0, 0, 5, 0);
        thin.thickness = 0.09f;
        var thick = Wall("thick", 5, 0, 5, 4);
        thick.thickness = 0.30f;

        var level = Level(thin, thick);

        WallMeshBuilder.ComputeExtensions(thin, level, out _, out float e);

        Assert.AreEqual(0.15f, e, 1e-4f);
    }

    [Test]
    public void ThreeWayJunction_UsesTheThickestNeighbour()
    {
        var main = Wall("main", 0, 0, 5, 0);
        var a = Wall("a", 5, 0, 5, 3); a.thickness = 0.10f;
        var b = Wall("b", 5, 0, 5, -3); b.thickness = 0.25f;

        var level = Level(main, a, b);

        WallMeshBuilder.ComputeExtensions(main, level, out _, out float e);

        Assert.AreEqual(0.125f, e, 1e-4f);
    }

    // ---- mesh ----

    [Test]
    public void Mesh_HasThreeSubmeshesForLeftRightAndEdge()
    {
        var mesh = Track(WallMeshBuilder.Build(Wall("w", 0, 0, 5, 0), Level()));

        Assert.IsNotNull(mesh);
        Assert.AreEqual(WallMeshBuilder.SUB_COUNT, mesh.subMeshCount);
        Assert.Greater(mesh.GetTriangles(WallMeshBuilder.SUB_LEFT).Length, 0);
        Assert.Greater(mesh.GetTriangles(WallMeshBuilder.SUB_RIGHT).Length, 0);
        Assert.Greater(mesh.GetTriangles(WallMeshBuilder.SUB_EDGE).Length, 0);
    }

    [Test]
    public void Mesh_HasTwentyFourVerticesPerBox()
    {
        // Six quads per box, four hard-normal vertices each — corners must stay creased, not smoothed.
        var plain = Track(WallMeshBuilder.Build(Wall("w", 0, 0, 5, 0), Level()));
        Assert.AreEqual(24, plain.vertexCount);

        var level = Level(Wall("w", 0, 0, 5, 0));
        level.openings = new List<OpeningDef> { Door(2.5f, 0.9f) };
        var withDoor = Track(WallMeshBuilder.Build(level.walls[0], level));

        Assert.AreEqual(24 * 3, withDoor.vertexCount);   // two panels plus a header
    }

    [Test]
    public void Mesh_FaceNormalsPointOutOfBothSides()
    {
        var mesh = Track(WallMeshBuilder.Build(Wall("w", 0, 0, 5, 0), Level()));

        bool hasLeft = false, hasRight = false, hasUp = false;
        foreach (var n in mesh.normals)
        {
            if (Vector3.Dot(n, Vector3.forward) > 0.99f) hasLeft = true;   // +Z is this wall's left
            if (Vector3.Dot(n, Vector3.back) > 0.99f) hasRight = true;
            if (Vector3.Dot(n, Vector3.up) > 0.99f) hasUp = true;
        }

        Assert.IsTrue(hasLeft, "no outward normal on the left face");
        Assert.IsTrue(hasRight, "no outward normal on the right face");
        Assert.IsTrue(hasUp, "no upward normal on the wall top");
    }

    [Test]
    public void Mesh_SpansTheWallAndIsStretchedByJunctions()
    {
        var level = Level(
            Wall("w1", 0, 0, 5, 0),
            Wall("w2", 5, 0, 5, 4));
        level.wallThickness = 0.2f;

        var mesh = Track(WallMeshBuilder.Build(level.walls[0], level));

        // Local space: origin is endpoint `a`, so the far end reaches length + half the neighbour.
        Assert.AreEqual(5.1f, mesh.bounds.max.x, 1e-3f);
        Assert.AreEqual(0f, mesh.bounds.min.x, 1e-3f);
    }

    [Test]
    public void DegenerateWall_ProducesNoMesh()
    {
        Assert.IsNull(WallMeshBuilder.Build(Wall("w", 1, 1, 1, 1), Level()));
    }

    [Test]
    public void WallEntirelyConsumedByAnOpening_ProducesNoMesh()
    {
        var level = Level(Wall("w", 0, 0, 2, 0));
        level.ceilingHeight = 2.4f;
        var o = Door(1f, 2f);
        o.height = 2.4f;
        level.openings = new List<OpeningDef> { o };

        Assert.IsNull(WallMeshBuilder.Build(level.walls[0], level));
    }

    // ---------------------------------------------------------------------------------------

    private Mesh Track(Mesh m)
    {
        if (m != null) _spawned.Add(m);
        return m;
    }

    private static WallDef Wall(string id, float ax, float az, float bx, float bz)
        => new WallDef { id = id, a = new[] { ax, az }, b = new[] { bx, bz } };

    private static LevelDef Level(params WallDef[] walls) => new LevelDef
    {
        id = "L0",
        elevation = 0f,
        ceilingHeight = 2.44f,
        wallThickness = 0.114f,
        walls = new List<WallDef>(walls),
        openings = new List<OpeningDef>(),
    };

    private static OpeningDef Door(float offset, float width) => new OpeningDef
    {
        id = "d", wallId = "w", offset = offset, width = width, height = 2.032f,
        kind = OpeningKind.Door, swing = OpeningSwing.LeftIn,
    };
}
