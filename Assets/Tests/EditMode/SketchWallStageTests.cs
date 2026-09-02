using System.Collections.Generic;
using NUnit.Framework;

// The detector's graph stages, pinned one at a time on handcrafted inputs, so a regression names
// the stage that moved rather than the plan that broke.
[TestFixture]
public class SketchWallStageTests
{
    /// <summary>The fixture's gray buffer as a top-down wall mask, no detector stages involved.</summary>
    private static bool[] Mask(SketchTestImages img)
    {
        var pixels = img.Pixels;   // bottom-up, like the detector's input
        var gray = SketchPlanDetector.ToGrayTopDown(pixels, img.Width, img.Height);
        var mask = new bool[gray.Length];
        for (int i = 0; i < gray.Length; i++) mask[i] = gray[i] < 128;
        return mask;
    }

    // ---- segment extraction ---------------------------------------------------------------------

    [Test]
    public void Extract_FindsTheFourWallsOfARectangle_OnTheirCenterlines()
    {
        var img = new SketchTestImages(300, 300);
        img.RectOutline(40, 30, 200, 140, 6);

        var segs = SketchWallSegments.Extract(Mask(img), 300, 300, stroke: 6);

        var majors = new List<WallSeg>();
        foreach (var s in segs) if (s.major) majors.Add(s);
        Assert.AreEqual(4, majors.Count);

        int horizontal = 0, vertical = 0;
        foreach (var s in majors)
        {
            Assert.AreEqual(6, s.thickness, "the stroke it was drawn with");
            if (s.horizontal)
            {
                horizontal++;
                Assert.That(s.Center, Is.EqualTo(32.5f).Within(1f).Or.EqualTo(166.5f).Within(1f));
            }
            else
            {
                vertical++;
                Assert.That(s.Center, Is.EqualTo(42.5f).Within(1f).Or.EqualTo(236.5f).Within(1f));
            }
        }
        Assert.AreEqual(2, horizontal);
        Assert.AreEqual(2, vertical);
    }

    [Test]
    public void Extract_MarksTheDoubleLineWindow_AsOneCrossing()
    {
        var img = new SketchTestImages(300, 300);
        img.RectOutline(40, 30, 220, 140, 8);
        img.Erase(100, 30, 80, 8);          // window break in the north wall...
        img.FillRect(100, 30, 80, 2);       // ...outer pane
        img.FillRect(100, 36, 80, 2);       // ...inner pane

        var segs = SketchWallSegments.Extract(Mask(img), 300, 300, stroke: 8);

        WallSeg north = default;
        bool found = false;
        foreach (var s in segs)
            if (s.major && s.horizontal && s.Center < 60f) { north = s; found = true; }
        Assert.IsTrue(found, "the north wall reads as ONE segment through the window");

        Assert.IsNotNull(north.dbl, "the window positions carry the double-line mark");
        int marked = 0;
        for (int i = 0; i < north.dbl.Length; i++) if (north.dbl[i]) marked++;
        Assert.AreEqual(80, marked, 6, "one mark per window column");
    }

    // ---- snapping -------------------------------------------------------------------------------

    [Test]
    public void Snap_PutsOffsetCollinearSegments_OnOneLine()
    {
        // The two halves of a hand-drawn wall, five pixels apart: one wall line, not two.
        var segs = new List<WallSeg>
        {
            new WallSeg { horizontal = true, center2 = 593, s0 = 40, s1 = 190, thickness = 6, major = true },
            new WallSeg { horizontal = true, center2 = 603, s0 = 260, s1 = 420, thickness = 6, major = true },
        };

        var grid = SketchWallSegments.Snap(segs, stroke: 6);

        Assert.AreEqual(1, grid.hLines.Length);
        Assert.AreEqual(296.5f, grid.hLines[0], 1e-3f, "the lower median member");
        Assert.AreEqual(2, grid.segs.Count);
        Assert.AreEqual(0, grid.segs[0].line);
        Assert.AreEqual(0, grid.segs[1].line);
    }

    // ---- cells ----------------------------------------------------------------------------------

    [Test]
    public void CellMap_CutsAnLShapedRoom_IntoTwoRects()
    {
        // A 200 x 200 square of four cells with the southeast cell open to the outside: an L.
        var xs = new[] { 0f, 100f, 200f };
        var ys = new[] { 0f, 100f, 200f };
        var hCover = new List<SketchCoverRun>[3];
        var vCover = new List<SketchCoverRun>[3];
        hCover[0] = Runs(0f, 200f);          // north wall
        hCover[1] = Runs(100f, 200f);        // the L's inner south wall
        hCover[2] = Runs(0f, 100f);          // south wall, stopping where the notch opens
        vCover[0] = Runs(0f, 200f);          // west wall
        vCover[1] = Runs(100f, 200f);        // the L's inner east wall
        vCover[2] = Runs(0f, 100f);          // east wall, stopping where the notch opens

        var cells = SketchCellMap.Build(xs, ys, hCover, vCover, stroke: 4);

        Assert.AreEqual(1, cells.roomCount);
        Assert.AreEqual(SketchCellMap.OUTSIDE, cells.LabelAt(150f, 150f), "the notch is outside");
        Assert.AreEqual(0, cells.LabelAt(50f, 150f));

        var rects = cells.Partition();
        Assert.AreEqual(2, rects.Count, "an L is two rectangles");
        Assert.AreEqual(0, rects[0].j0);
        Assert.AreEqual(0, rects[0].j1);
        Assert.AreEqual(1, rects[0].i1, "the top strip spans both columns");
        Assert.AreEqual(0, rects[1].i1, "the leg keeps to the west column");
    }

    private static List<SketchCoverRun> Runs(float lo, float hi)
        => new List<SketchCoverRun> { new SketchCoverRun { lo = lo, hi = hi, realPx = (int)(hi - lo) } };
}
