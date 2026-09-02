using System.Collections.Generic;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;

// Closet doors and the door symbols drawn inside them, pinned against plans drawn in code. The
// conventions come from real scanned plans: double swing arcs meeting mid-gap, bifold zigzags
// across the opening, a route line walked through a doorway, a stairs block. A closet door must
// come out as a wall with an opening, and the closet itself as a room typed storage named Closet;
// a small doorless cell must stay dropped.
[TestFixture]
public class SketchClosetTests
{
    private const float MPP = 0.01f;   // fixtures are drawn at one centimetre per pixel

    // ---- fixtures -------------------------------------------------------------------------------
    // Drawn at 900 x 600 px like SketchPlanDetectorTests: outer walls at (100, 80) to (800, 520).

    private static SketchTestImages OneRoom()
    {
        var img = new SketchTestImages(900, 600);
        img.RectOutline(100, 80, 700, 440, 6);
        return img;
    }

    private static SketchTestImages TwoRoomsVertical()
    {
        var img = OneRoom();
        img.FillRect(444, 80, 6, 440);
        img.Erase(444, 260, 6, 80);
        return img;
    }

    // A closet carved off the southwest corner: 1.5 x 0.64 m on centerlines, 0.96 m2, under the
    // doorless minimum. Its 80 px door has a 50 px jamb (long enough to establish the wall line)
    // and a 14 px jamb (short: exercises the short-jamb rescue), and two quarter arcs swing into
    // the room and meet mid-gap.
    private static void ArcCloset(SketchTestImages img)
    {
        img.FillRect(100, 450, 156, 6);        // the closet's north wall...
        img.Erase(156, 450, 80, 6);            // ...with the door punched through
        img.FillRect(250, 450, 6, 70);         // the closet's east wall
        img.Arc(156, 452, 38f, 0f, -90f, 2);   // west leaf swings up into the room
        img.Arc(236, 452, 38f, 180f, 270f, 2); // east leaf meets it mid-gap
    }

    // A closet carved off the southeast corner: 1.74 x 0.64 m, 1.11 m2. Its 90 px door has a
    // 64 px jamb and a 14 px jamb, and a bifold zigzag drawn straight across the gap.
    private static void ZigzagCloset(SketchTestImages img)
    {
        img.FillRect(620, 450, 180, 6);        // the closet's north wall...
        img.Erase(690, 450, 90, 6);            // ...with the door punched through
        img.FillRect(620, 450, 6, 70);         // the closet's west wall
        img.Zigzag(690, 452, 779, 452, -30, 4, 2);
    }

    private static SketchDetectResult Detect(SketchTestImages img, float mpp = MPP)
        => SketchPlanDetector.Detect(img.Pixels, img.Width, img.Height, mpp);

    private static List<SketchOpening> Doors(SketchPlanSpec spec)
    {
        var doors = new List<SketchOpening>();
        foreach (var o in spec.Openings)
            if (o.kind == OpeningKind.Door || o.kind == OpeningKind.CasedOpening) doors.Add(o);
        return doors;
    }

    private static List<SketchRoom> Closets(SketchPlanSpec spec)
    {
        var closets = new List<SketchRoom>();
        foreach (var r in spec.rooms) if (r.roomType == RoomType.Storage) closets.Add(r);
        return closets;
    }

    // ---- the closet conventions -----------------------------------------------------------------

    [Test]
    public void ClosetWithDoubleArcDoors_IsAClosetRoomWithADoor()
    {
        var img = OneRoom();
        ArcCloset(img);

        var result = Detect(img);

        Assert.IsTrue(result.Ok, result.refusal);
        int roots = 0;
        foreach (var r in result.spec.rooms) if (!r.IsPart) roots++;
        Assert.AreEqual(2, roots, "the room and the closet carved out of it");

        var closets = Closets(result.spec);
        Assert.AreEqual(1, closets.Count, "the small doored cell survives as a closet");
        Assert.AreEqual("Closet", closets[0].name);
        Assert.AreEqual(1.50f, closets[0].widthMeters, 0.10f);
        Assert.AreEqual(0.64f, closets[0].depthMeters, 0.10f);

        var doors = Doors(result.spec);
        Assert.AreEqual(1, doors.Count, "the closet door survives its own swing arcs");
        Assert.IsTrue(doors[0].IsInterior);
        CollectionAssert.Contains(doors[0].between, closets[0].key);
        Assert.AreEqual(0.80f, doors[0].widthMeters, 0.10f);

        // Through the real compiler: the closet must arrive as a RoomDef with its opening.
        var frame = SketchFrame.Build(new[] { 0f, 0f }, 900, 600, MPP, 0f);
        var compiled = SketchPlanCompiler.Compile(result.spec, frame, 0f, 0f);
        Assert.IsTrue(compiled.Ok, compiled.refusal);
        bool found = false;
        foreach (var room in compiled.level.rooms)
            if (room.name == "Closet" && room.roomType == RoomType.Storage) found = true;
        Assert.IsTrue(found, "the closet RoomDef survives compilation");
        Assert.AreEqual(1, compiled.level.openings.Count);
    }

    [Test]
    public void BifoldZigzagAcrossTheGap_IsStillOneDoor()
    {
        var img = OneRoom();
        ZigzagCloset(img);

        var result = Detect(img);

        Assert.IsTrue(result.Ok, result.refusal);
        var closets = Closets(result.spec);
        Assert.AreEqual(1, closets.Count, "the closet behind the bifold survives");

        var doors = Doors(result.spec);
        Assert.AreEqual(1, doors.Count, "the zigzag panels do not veto the doorway");
        Assert.IsTrue(doors[0].IsInterior);
        Assert.AreEqual(0.90f, doors[0].widthMeters, 0.10f);
    }

    [Test]
    public void EvacRouteThroughADoorway_DoesNotVetoTheDoor()
    {
        var img = TwoRoomsVertical();
        img.Line(300, 310, 430, 300, 3);       // a route line drawn through the doorway
        img.Line(430, 300, 468, 285, 3);
        img.Line(468, 285, 600, 280, 3);
        img.Jitter(seed: 1, amplitudePx: 2.5f);

        var result = Detect(img);

        Assert.IsTrue(result.Ok, result.refusal);
        Assert.AreEqual(2, result.spec.rooms.Count);
        Assert.AreEqual(1, Doors(result.spec).Count, "the route line does not veto the doorway");
    }

    [Test]
    public void StairsBlock_MintsNoRoomsAndNoClosets()
    {
        // A stairs symbol: a box against the west wall with six tread lines. Every slice between
        // treads is a small DOORLESS cell, so the closet rescue must leave all of them dropped.
        var img = TwoRoomsVertical();
        img.RectOutline(100, 150, 80, 140, 6);
        for (int y = 170; y <= 270; y += 20) img.FillRect(106, y, 68, 3);

        var result = Detect(img);

        Assert.IsTrue(result.Ok, result.refusal);
        int roots = 0;
        foreach (var r in result.spec.rooms) if (!r.IsPart) roots++;
        Assert.AreEqual(2, roots, "the stairs mint no rooms");
        Assert.AreEqual(0, Closets(result.spec).Count, "doorless slices are not closets");
        Assert.AreEqual(1, Doors(result.spec).Count, "only the drawn doorway");
    }

    [Test]
    public void RealisticAnnotatedPlan_ReadsRoomsClosetsAndStaysDeterministic()
    {
        // The shape of the real scans: two rooms, an arc closet, a zigzag closet, a route line
        // through the doorway, labels, wobble and grain.
        var img = TwoRoomsVertical();
        ArcCloset(img);
        ZigzagCloset(img);
        img.Line(300, 310, 430, 300, 3);
        img.Line(430, 300, 468, 285, 3);
        img.Line(468, 285, 600, 280, 3);
        img.Squiggle(250, 150, seed: 3);
        img.Squiggle(600, 150, seed: 4);
        img.Jitter(seed: 1, amplitudePx: 2.5f);
        img.Noise(seed: 2, amplitude: 10);

        var result = Detect(img);

        Assert.IsTrue(result.Ok, result.refusal);
        int roots = 0;
        foreach (var r in result.spec.rooms) if (!r.IsPart) roots++;
        Assert.AreEqual(4, roots, "two rooms and two closets");

        var closets = Closets(result.spec);
        Assert.AreEqual(2, closets.Count);
        Assert.AreEqual("Closet", closets[0].name);
        Assert.AreEqual("Closet 2", closets[1].name);

        Assert.AreEqual(3, Doors(result.spec).Count, "the doorway and both closet doors");

        // Byte for byte determinism holds on the busiest input.
        var pixels = img.Pixels;
        var a = SketchPlanDetector.Detect(pixels, img.Width, img.Height, MPP);
        var b = SketchPlanDetector.Detect(pixels, img.Width, img.Height, MPP);
        Assert.AreEqual(JsonConvert.SerializeObject(a.spec), JsonConvert.SerializeObject(b.spec));
    }

    // ---- the scale guard ------------------------------------------------------------------------

    [Test]
    public void SpuriousSmallGap_DoesNotShrinkTheScale()
    {
        // Two honest 80 px doorways and one 30 px break. The old floor took the smallest gap and
        // shrank the whole scale by 2.7x; the floor now needs a second gap within 1.5x.
        var img = OneRoom();
        img.FillRect(340, 80, 6, 440);
        img.Erase(340, 260, 6, 80);
        img.Erase(340, 430, 6, 30);
        img.FillRect(560, 80, 6, 440);
        img.Erase(560, 260, 6, 80);

        var result = Detect(img, mpp: 0f);

        Assert.IsTrue(result.Ok, result.refusal);
        Assert.IsTrue(result.scaleEstimated);
        Assert.AreEqual(2, result.scaleDoorways, "the two supported doorways anchor the scale");
        Assert.AreEqual(0.813f / 80f, result.metersPerPixel, 0.0006f);
    }

    // ---- the gap verifier, stage by stage -------------------------------------------------------

    private static bool[] Mask(int w, int h, params (int x, int y, int rw, int rh)[] darks)
    {
        var wall = new bool[w * h];
        foreach (var (x, y, rw, rh) in darks)
            for (int yy = y; yy < y + rh; yy++)
                for (int xx = x; xx < x + rw; xx++)
                    wall[yy * w + xx] = true;
        return wall;
    }

    [Test]
    public void GapReadsOpen_EmptyGapPasses()
    {
        var wall = Mask(100, 40);
        Assert.IsTrue(SketchOpeningReader.GapReadsOpen(wall, 100, 40, horizontal: true,
                                                       lineCoord: 20f, g0: 10f, g1: 90f,
                                                       thickness: 6, stroke: 4));
    }

    [Test]
    public void GapReadsOpen_SymbolBurstsPass()
    {
        // Thin strokes crossing the band, the way arcs and panel legs do: too much ink for the
        // clean tenth, but every blocked stretch is short.
        var wall = Mask(100, 40, (25, 16, 3, 9), (40, 16, 3, 9), (55, 16, 3, 9), (70, 16, 3, 9));
        Assert.IsTrue(SketchOpeningReader.GapReadsOpen(wall, 100, 40, horizontal: true,
                                                       lineCoord: 20f, g0: 10f, g1: 90f,
                                                       thickness: 6, stroke: 4));
    }

    [Test]
    public void GapReadsOpen_ALongBarAlongTheLineVetoes()
    {
        var wall = Mask(100, 40, (30, 19, 31, 3));
        Assert.IsFalse(SketchOpeningReader.GapReadsOpen(wall, 100, 40, horizontal: true,
                                                        lineCoord: 20f, g0: 10f, g1: 90f,
                                                        thickness: 6, stroke: 4));
    }

    [Test]
    public void GapReadsOpen_ADenseBandVetoes()
    {
        // Hatching: most of the band inked. The density cap holds even though single columns
        // alternate with light ones, keeping runs short.
        var darks = new List<(int, int, int, int)>();
        for (int x = 12; x < 88; x += 2) darks.Add((x, 16, 1, 9));
        var wall = Mask(100, 40, darks.ToArray());
        Assert.IsFalse(SketchOpeningReader.GapReadsOpen(wall, 100, 40, horizontal: true,
                                                        lineCoord: 20f, g0: 10f, g1: 90f,
                                                        thickness: 6, stroke: 4));
    }

    // ---- door and window dedup ------------------------------------------------------------------

    private static SketchGap Gap(bool window, int line, float center, float widthPx)
        => new SketchGap { horizontal = true, line = line, center = center, widthPx = widthPx,
                           window = window };

    [Test]
    public void Dedup_DropsTheWindowThatOverlapsADoorway()
    {
        var gaps = new List<SketchGap> { Gap(false, 0, 100f, 80f), Gap(true, 0, 120f, 60f) };
        SketchOpeningReader.Dedup(gaps);
        Assert.AreEqual(1, gaps.Count);
        Assert.IsFalse(gaps[0].window, "the doorway stands, the window is dropped");
    }

    [Test]
    public void Dedup_KeepsADisjointWindow()
    {
        var gaps = new List<SketchGap> { Gap(false, 0, 100f, 80f), Gap(true, 0, 300f, 60f),
                                         Gap(true, 1, 110f, 60f) };
        SketchOpeningReader.Dedup(gaps);
        Assert.AreEqual(3, gaps.Count, "windows clear of the doorway and on other lines stay");
    }
}
