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
        // This is the Shift-held path: the fence tool's convention, which the wall tool follows.
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

    [Test]
    public void AxisIntersection_BeatsThePerpendicularFoot()
    {
        // Drawing square toward a wall must land where the RUN crosses it, not at the perpendicular
        // foot of wherever the cursor hovers: the snap that used to defeat 90° joins.
        var level = Level(Wall("w1", 0, 0, 3, 0));

        var r = WallSnapping.Snap(new Vector2(1.07f, 0.12f), level, new Vector2(1f, 2f),
                                  WallSnapping.Options.Default);

        Assert.AreEqual(WallSnapping.SnapKind.AxisOnWall, r.kind);
        Assert.AreEqual(1f, r.point.x, 1e-3f, "the crossing of the squared run, not the foot at x = 1.07");
        Assert.AreEqual(0f, r.point.y, 1e-3f);
        Assert.AreEqual("w1", r.targetWallId);
    }

    [Test]
    public void AxisIntersection_NearTheWallsEnd_WeldsToTheCorner()
    {
        // A crossing within MinSeg of the wall's end is a cut the linker refuses; the corner is the
        // junction that actually welds, so the snap lands exactly there. Even though the cursor
        // itself is too far from the corner for plain endpoint snapping.
        var level = Level(Wall("w1", 0, 0, 3, 0));

        var r = WallSnapping.Snap(new Vector2(2.91f, 0.345f), level, new Vector2(2.91f, 2f),
                                  WallSnapping.Options.Default);

        Assert.AreEqual(WallSnapping.SnapKind.Endpoint, r.kind);
        Assert.AreEqual(3f, r.point.x, 1e-4f);
        Assert.AreEqual(0f, r.point.y, 1e-4f);
    }

    [Test]
    public void ParallelToTheWall_StillSnapsOntoIt()
    {
        // A run parallel to a wall never crosses it; the plain on-wall foot survives as the fallback.
        var level = Level(Wall("w1", 0, 0, 6, 0));

        var r = WallSnapping.Snap(new Vector2(3f, 0.15f), level, new Vector2(0f, 0.15f),
                                  WallSnapping.Options.Default);

        Assert.AreEqual(WallSnapping.SnapKind.OnWall, r.kind);
        Assert.AreEqual(3f, r.point.x, 1e-4f);
        Assert.AreEqual(0f, r.point.y, 1e-4f);
    }

    [Test]
    public void AlignedWithAParallelWallsEndpoint_SnapsLevelAcrossTheGap()
    {
        // The L -> C case: drawing up the open side of a C stops level with the far end of the
        // parallel wall across the gap, with the guide endpoint reported for the overlay.
        var level = Level(Wall("w1", 0, 0, 4, 0), Wall("w2", 4, 0, 4, 3));

        var r = WallSnapping.Snap(new Vector2(0.05f, 2.9f), level, Vector2.zero,
                                  WallSnapping.Options.Default);

        Assert.AreEqual(WallSnapping.SnapKind.Align, r.kind);
        Assert.AreEqual(0f, r.point.x, 1e-3f);
        Assert.AreEqual(3f, r.point.y, 1e-3f, "level with w2's far end, not the grid-rounded cursor");
        Assert.IsTrue(r.hasGuide);
        Assert.AreEqual(4f, r.guideFrom.x, 1e-4f);
        Assert.AreEqual(3f, r.guideFrom.y, 1e-4f);
        Assert.AreEqual("w2", r.targetWallId);
    }

    [Test]
    public void Alignment_LosesToAWallInThePath()
    {
        // A wall the squared run actually crosses is a junction-to-be; being level with something
        // further on cannot outrank it.
        var level = Level(Wall("w1", 0, 0, 4, 0), Wall("w2", 4, 0, 4, 3),
                          Wall("w3", -1f, 2.85f, 1f, 2.85f));

        var r = WallSnapping.Snap(new Vector2(0.05f, 2.9f), level, Vector2.zero,
                                  WallSnapping.Options.Default);

        Assert.AreEqual(WallSnapping.SnapKind.AxisOnWall, r.kind);
        Assert.AreEqual(2.85f, r.point.y, 1e-3f);
    }

    [Test]
    public void Alignment_RequiresTheAxisLock()
    {
        var level = Level(Wall("w1", 0, 0, 4, 0), Wall("w2", 4, 0, 4, 3));
        var opts = WallSnapping.Options.Default;
        opts.axisLock = false;

        var r = WallSnapping.Snap(new Vector2(0.05f, 2.9f), level, Vector2.zero, opts);

        Assert.AreEqual(WallSnapping.SnapKind.Grid, r.kind);
    }

    // ---------------------------------------------------------------------------------------

    private static WallDef Wall(string id, float ax, float az, float bx, float bz)
        => new WallDef { id = id, a = new[] { ax, az }, b = new[] { bx, bz } };

    private static LevelDef Level(params WallDef[] walls)
        => new LevelDef { walls = new List<WallDef>(walls) };
}
