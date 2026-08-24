using System.Collections.Generic;
using NUnit.Framework;

// WallLayout is the file that lets openings exist without CSG: a wall is a list of solid boxes, and a
// door is a gap the list skips. Every case below is one of the ways that decomposition can go wrong,
// an opening flush against a corner, one taller than the wall, two that overlap. Getting these right
// is what stops a traced plan from developing holes or phantom slivers.
[TestFixture]
public class WallLayoutTests
{
    private const float L = 5f;      // wall length
    private const float H = 2.5f;    // wall height

    [Test]
    public void NoOpenings_ProducesOneFullPanel()
    {
        var boxes = WallLayout.Build(L, H, null);

        Assert.AreEqual(1, boxes.Count);
        Assert.AreEqual(WallLayout.Kind.Panel, boxes[0].kind);
        Assert.AreEqual(0f, boxes[0].t0, 1e-4f);
        Assert.AreEqual(L, boxes[0].t1, 1e-4f);
        Assert.AreEqual(H, boxes[0].Height, 1e-4f);
    }

    [Test]
    public void CenteredDoor_ProducesTwoPanelsAndAHeader()
    {
        var boxes = WallLayout.Build(L, H, new List<OpeningDef> { Door(offset: 2.5f, width: 1f) });

        Assert.AreEqual(3, boxes.Count);

        var panels = Of(boxes, WallLayout.Kind.Panel);
        Assert.AreEqual(2, panels.Count);
        Assert.AreEqual(0f, panels[0].t0, 1e-4f);
        Assert.AreEqual(2f, panels[0].t1, 1e-4f);
        Assert.AreEqual(3f, panels[1].t0, 1e-4f);
        Assert.AreEqual(L, panels[1].t1, 1e-4f);

        var headers = Of(boxes, WallLayout.Kind.Header);
        Assert.AreEqual(1, headers.Count);
        Assert.AreEqual(2f, headers[0].t0, 1e-4f);
        Assert.AreEqual(3f, headers[0].t1, 1e-4f);
        Assert.AreEqual(2.032f, headers[0].y0, 1e-4f);   // top of a standard 80" door
        Assert.AreEqual(H, headers[0].y1, 1e-4f);
    }

    [Test]
    public void DoorFlushAgainstWallStart_EmitsNoLeadingPanel()
    {
        // A door hard against a corner is common in small bathrooms; a zero-width sliver panel here
        // would render as z-fighting garbage.
        var boxes = WallLayout.Build(L, H, new List<OpeningDef> { Door(offset: 0.5f, width: 1f) });

        Assert.AreEqual(2, boxes.Count);
        Assert.AreEqual(1, Of(boxes, WallLayout.Kind.Panel).Count);
        Assert.AreEqual(1f, Of(boxes, WallLayout.Kind.Panel)[0].t0, 1e-4f);
        Assert.AreEqual(1, Of(boxes, WallLayout.Kind.Header).Count);
    }

    [Test]
    public void DoorFlushAgainstWallEnd_EmitsNoTrailingPanel()
    {
        var boxes = WallLayout.Build(L, H, new List<OpeningDef> { Door(offset: L - 0.5f, width: 1f) });

        var panels = Of(boxes, WallLayout.Kind.Panel);
        Assert.AreEqual(1, panels.Count);
        Assert.AreEqual(0f, panels[0].t0, 1e-4f);
        Assert.AreEqual(4f, panels[0].t1, 1e-4f);
    }

    [Test]
    public void Window_ProducesHeaderAndSill()
    {
        var w = new OpeningDef
        {
            id = "w1", wallId = "wall", offset = 2.5f,
            width = 1.2f, height = 1.2f, sillHeight = 0.9f,
            kind = OpeningKind.Window,
        };

        var boxes = WallLayout.Build(L, H, new List<OpeningDef> { w });

        Assert.AreEqual(2, Of(boxes, WallLayout.Kind.Panel).Count);

        var sill = Of(boxes, WallLayout.Kind.Sill);
        Assert.AreEqual(1, sill.Count);
        Assert.AreEqual(0f, sill[0].y0, 1e-4f);
        Assert.AreEqual(0.9f, sill[0].y1, 1e-4f);

        var header = Of(boxes, WallLayout.Kind.Header);
        Assert.AreEqual(1, header.Count);
        Assert.AreEqual(2.1f, header[0].y0, 1e-4f);   // sill 0.9 + height 1.2
    }

    [Test]
    public void OpeningReachingTheCeiling_HasNoHeader()
    {
        // This is what makes a full-height pass-through read as a genuine gap rather than a doorway.
        var o = Door(offset: 2.5f, width: 1f);
        o.height = H;

        var boxes = WallLayout.Build(L, H, new List<OpeningDef> { o });

        Assert.AreEqual(0, Of(boxes, WallLayout.Kind.Header).Count);
        Assert.AreEqual(2, Of(boxes, WallLayout.Kind.Panel).Count);
    }

    [Test]
    public void OpeningTallerThanTheWall_IsClampedNotDropped()
    {
        var o = Door(offset: 2.5f, width: 1f);
        o.height = 10f;

        var boxes = WallLayout.Build(L, H, new List<OpeningDef> { o });

        Assert.AreEqual(0, Of(boxes, WallLayout.Kind.Header).Count);
        foreach (var b in boxes) Assert.LessOrEqual(b.y1, H + 1e-4f);
    }

    [Test]
    public void OpeningSpanningTheWholeWall_LeavesNothing()
    {
        var o = Door(offset: 2.5f, width: L);
        o.height = H;

        var boxes = WallLayout.Build(L, H, new List<OpeningDef> { o });

        Assert.AreEqual(0, boxes.Count);
    }

    [Test]
    public void ZeroLengthWall_ProducesNothing()
    {
        Assert.AreEqual(0, WallLayout.Build(0f, H, null).Count);
        Assert.AreEqual(0, WallLayout.Build(L, 0f, null).Count);
    }

    [Test]
    public void OverlappingOpenings_MergeIntoOneVoidWithoutCrashing()
    {
        // Bad data should render oddly, never throw: a visioning session must not die on a
        // hand-edited JSON file.
        var boxes = WallLayout.Build(L, H, new List<OpeningDef>
        {
            Door(offset: 2.0f, width: 1.4f),   // 1.3 .. 2.7
            Door(offset: 2.6f, width: 1.4f),   // 1.9 .. 3.3
        });

        var panels = Of(boxes, WallLayout.Kind.Panel);
        Assert.AreEqual(2, panels.Count);
        Assert.AreEqual(1.3f, panels[0].t1, 1e-4f);
        Assert.AreEqual(3.3f, panels[1].t0, 1e-4f);
    }

    [Test]
    public void OpeningEntirelyOffTheWall_IsIgnored()
    {
        var boxes = WallLayout.Build(L, H, new List<OpeningDef> { Door(offset: 20f, width: 1f) });

        Assert.AreEqual(1, boxes.Count);
        Assert.AreEqual(WallLayout.Kind.Panel, boxes[0].kind);
    }

    [Test]
    public void TwoOpenings_ProduceThreePanels()
    {
        var boxes = WallLayout.Build(L, H, new List<OpeningDef>
        {
            Door(offset: 1.2f, width: 0.9f),
            Door(offset: 3.6f, width: 0.9f),
        });

        Assert.AreEqual(3, Of(boxes, WallLayout.Kind.Panel).Count);
        Assert.AreEqual(2, Of(boxes, WallLayout.Kind.Header).Count);
    }

    // ---- helpers on the live level model ----

    [Test]
    public void EffectiveThicknessAndHeight_FallBackThroughLevelThenGlobalDefault()
    {
        var level = new LevelDef { ceilingHeight = 0f, wallThickness = 0f };
        var wall = new WallDef { id = "w", a = new[] { 0f, 0f }, b = new[] { 3f, 0f } };

        Assert.AreEqual(HomeConventions.DEFAULT_CEILING_HEIGHT, WallLayout.EffectiveHeight(wall, level), 1e-4f);
        Assert.AreEqual(HomeConventions.DEFAULT_WALL_THICKNESS, WallLayout.EffectiveThickness(wall, level), 1e-4f);

        level.ceilingHeight = 2.7f;
        level.wallThickness = 0.15f;
        Assert.AreEqual(2.7f, WallLayout.EffectiveHeight(wall, level), 1e-4f);
        Assert.AreEqual(0.15f, WallLayout.EffectiveThickness(wall, level), 1e-4f);

        wall.height = 3.0f;
        wall.thickness = 0.2f;
        Assert.AreEqual(3.0f, WallLayout.EffectiveHeight(wall, level), 1e-4f);
        Assert.AreEqual(0.2f, WallLayout.EffectiveThickness(wall, level), 1e-4f);
    }

    [Test]
    public void OpeningsFor_FiltersByWallAndSortsByOffset()
    {
        var level = new LevelDef
        {
            openings = new List<OpeningDef>
            {
                new OpeningDef { id = "b", wallId = "w1", offset = 3f, width = 1f },
                new OpeningDef { id = "x", wallId = "w2", offset = 1f, width = 1f },
                new OpeningDef { id = "a", wallId = "w1", offset = 1f, width = 1f },
            }
        };
        var wall = new WallDef { id = "w1", a = new[] { 0f, 0f }, b = new[] { 5f, 0f } };

        var found = WallLayout.OpeningsFor(wall, level);

        Assert.AreEqual(2, found.Count);
        Assert.AreEqual("a", found[0].id);
        Assert.AreEqual("b", found[1].id);
    }

    [Test]
    public void WallLength_IsEuclidean()
    {
        var wall = new WallDef { id = "w", a = new[] { 0f, 0f }, b = new[] { 3f, 4f } };
        Assert.AreEqual(5f, WallLayout.WallLength(wall), 1e-4f);
    }

    // ---------------------------------------------------------------------------------------

    private static OpeningDef Door(float offset, float width) => new OpeningDef
    {
        id = "d" + offset,
        wallId = "wall",
        offset = offset,
        width = width,
        height = 2.032f,
        sillHeight = 0f,
        kind = OpeningKind.Door,
    };

    private static List<WallLayout.Box> Of(List<WallLayout.Box> boxes, WallLayout.Kind kind)
    {
        var list = new List<WallLayout.Box>();
        foreach (var b in boxes) if (b.kind == kind) list.Add(b);
        list.Sort((x, y) => x.t0.CompareTo(y.t0));
        return list;
    }
}
