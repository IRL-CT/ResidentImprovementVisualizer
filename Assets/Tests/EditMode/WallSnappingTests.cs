using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// Snapping is what makes a traced plan dimensionally trustworthy. Without exact corner welding you
// get a model full of 0.003 m gaps that look fine and measure wrong, which defeats the point of the
// whole tool. The priority order (endpoint > on-wall > axis > grid) is the contract being pinned here.
[TestFixture]
public class WallSnappingTests
{
    [Test]
    public void Disabled_PassesTheRawPointThrough()
    {
        // This is the Shift-held path — the fence tool's convention, which the wall tool follows.
        var opts = WallSnapping.Options.Default;
        opts.enabled = false;

        var r = WallSnapping.Snap(new Vector2(1.234f, 5.678f), Level(), null, opts);

        Assert.AreEqual(WallSnapping.SnapKind.None, r.kind);
        Assert.AreEqual(1.234f, r.point.x, 1e-5f);
        Assert.AreEqual(5.678f, r.point.y, 1e-5f);
    }

    [Test]
    public void ExistingEndpoint_WinsOverEverythingElse()
    {
        var level = Level(Wall("w1", 0, 0, 3, 0));

        var r = WallSnapping.Snap(new Vector2(3.1f, 0.05f), level, null, WallSnapping.Options.Default);

        Assert.AreEqual(WallSnapping.SnapKind.Endpoint, r.kind);
        Assert.AreEqual(3f, r.point.x, 1e-4f);
        Assert.AreEqual(0f, r.point.y, 1e-4f);
        Assert.AreEqual("w1", r.targetWallId);
    }

    [Test]
    public void PointAlongAWall_SnapsPerpendicularlyOntoIt()
    {
        // The T-junction case: a new wall meeting an existing run partway along.
        var level = Level(Wall("w1", 0, 0, 3, 0));

        var r = WallSnapping.Snap(new Vector2(1.5f, 0.1f), level, null, WallSnapping.Options.Default);

        Assert.AreEqual(WallSnapping.SnapKind.OnWall, r.kind);
        Assert.AreEqual(1.5f, r.point.x, 1e-4f);
        Assert.AreEqual(0f, r.point.y, 1e-4f);
        Assert.AreEqual("w1", r.targetWallId);
    }

    [Test]
    public void IgnoredWall_IsNotSnappedTo()
    {
        // Dragging a wall must not let it snap to its own endpoints.
        var level = Level(Wall("w1", 0, 0, 3, 0));

        var r = WallSnapping.Snap(new Vector2(3.02f, 0.01f), level, null,
                                  WallSnapping.Options.Default, ignoreWallId: "w1");

        Assert.AreNotEqual(WallSnapping.SnapKind.Endpoint, r.kind);
        Assert.AreNotEqual(WallSnapping.SnapKind.OnWall, r.kind);
    }

    [Test]
    public void AxisLock_SquaresTheSegmentToTheAnchor()
    {
        var anchor = new Vector2(0f, 0f);

        var r = WallSnapping.Snap(new Vector2(5f, 0.3f), Level(), anchor, WallSnapping.Options.Default);

        Assert.AreEqual(WallSnapping.SnapKind.Axis, r.kind);
        Assert.AreEqual(0f, r.point.y, 1e-3f, "should have snapped flat onto the 0° axis");
        Assert.AreEqual("0°", r.label);
    }

    [Test]
    public void AxisLock_SnapsToNinetyDegrees()
    {
        var r = WallSnapping.Snap(new Vector2(0.2f, 4f), Level(), Vector2.zero,
                                  WallSnapping.Options.Default);

        Assert.AreEqual(WallSnapping.SnapKind.Axis, r.kind);
        Assert.AreEqual(0f, r.point.x, 1e-3f);
        Assert.AreEqual("90°", r.label);
    }

    [Test]
    public void AxisLock_AllowsDiagonalsWhenTheStepIsFortyFive()
    {
        var opts = WallSnapping.Options.Default;
        opts.gridSize = 0f;   // isolate the angle from the length rounding

        var r = WallSnapping.Snap(new Vector2(3f, 3.2f), Level(), Vector2.zero, opts);

        Assert.AreEqual(WallSnapping.SnapKind.Axis, r.kind);
        Assert.AreEqual("45°", r.label);
        Assert.AreEqual(r.point.x, r.point.y, 1e-3f);
    }

    [Test]
    public void AxisLock_ProjectsPerpendicularlySoThePointTracksTheCursor()
    {
        // The snapped point must not shoot past the cursor: the projection is len*cos(delta), so a
        // 45°-off request lands short, not long.
        var opts = WallSnapping.Options.Default;
        opts.gridSize = 0f;
        opts.axisStepDeg = 90f;

        var r = WallSnapping.Snap(new Vector2(4f, 4f), Level(), Vector2.zero, opts);

        Assert.AreEqual(WallSnapping.SnapKind.Axis, r.kind);
        Assert.Less(r.point.magnitude, new Vector2(4f, 4f).magnitude);
        Assert.AreEqual(4f, r.point.magnitude, 1e-3f);   // 5.657 * cos(45°)
    }

    [Test]
    public void NoAnchor_FallsBackToTheGrid()
    {
        var r = WallSnapping.Snap(new Vector2(1.234f, 2.345f), Level(), null,
                                  WallSnapping.Options.Default);

        Assert.AreEqual(WallSnapping.SnapKind.Grid, r.kind);
        Assert.AreEqual(1.25f, r.point.x, 1e-4f);
        Assert.AreEqual(2.35f, r.point.y, 1e-4f);
    }

    [Test]
    public void GridDisabled_LeavesThePointAlone()
    {
        var opts = WallSnapping.Options.Default;
        opts.gridSize = 0f;
        opts.axisLock = false;

        var r = WallSnapping.Snap(new Vector2(1.234f, 2.345f), Level(), null, opts);

        Assert.AreEqual(WallSnapping.SnapKind.None, r.kind);
        Assert.AreEqual(1.234f, r.point.x, 1e-5f);
    }

    [Test]
    public void EmptyLevel_DoesNotThrow()
    {
        Assert.DoesNotThrow(() =>
            WallSnapping.Snap(Vector2.one, null, null, WallSnapping.Options.Default));
    }

    [Test]
    public void MalformedWall_IsSkipped()
    {
        var level = new LevelDef
        {
            walls = new List<WallDef>
            {
                new WallDef { id = "bad", a = null, b = new[] { 1f, 1f } },
                new WallDef { id = "short", a = new[] { 1f }, b = new[] { 1f, 1f } },
            }
        };

        Assert.DoesNotThrow(() =>
            WallSnapping.Snap(Vector2.one, level, null, WallSnapping.Options.Default));
    }

    // ---------------------------------------------------------------------------------------

    private static WallDef Wall(string id, float ax, float az, float bx, float bz)
        => new WallDef { id = id, a = new[] { ax, az }, b = new[] { bx, bz } };

    private static LevelDef Level(params WallDef[] walls)
        => new LevelDef { walls = new List<WallDef>(walls) };
}
