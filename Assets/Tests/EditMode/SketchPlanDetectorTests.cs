using System.Collections.Generic;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;

// The on-device plan reader, pinned against plans drawn in code. Positions are asserted with CV-wide
// tolerances (the detector is allowed to be a wall's width off); counts, kinds, keys and the two
// coordinate flips are asserted exactly, because those are the failures that render as a mirrored or
// mislabeled plan rather than a slightly shifted one.
[TestFixture]
public class SketchPlanDetectorTests
{
    private const float MPP = 0.01f;   // fixtures are drawn at one centimetre per pixel

    // ---- fixtures -------------------------------------------------------------------------------
    // Drawn at 900 x 600 px, one centimetre per pixel: a 7 m x 4.4 m dwelling, which is what the
    // compiler's validator considers a building rather than a closet.

    // One room: outer walls only, drawn with a 6 px stroke.
    private static SketchTestImages OneRoom()
    {
        var img = new SketchTestImages(900, 600);
        img.RectOutline(100, 80, 700, 440, 6);
        return img;
    }

    // Two rooms split by a vertical wall with one doorway. doorTopY places the gap; 80 px = 0.8 m.
    private static SketchTestImages TwoRoomsVertical(int doorTopY = 260)
    {
        var img = OneRoom();
        img.FillRect(444, 80, 6, 440);
        img.Erase(444, doorTopY, 6, 80);
        return img;
    }

    // Two rooms split by a horizontal wall with one doorway.
    private static SketchTestImages TwoRoomsHorizontal()
    {
        var img = OneRoom();
        img.FillRect(100, 294, 700, 6);
        img.Erase(400, 294, 80, 6);
        return img;
    }

    // One room with an exterior door punched through the south wall and a double-line window in the
    // north wall. Drawn with an 8 px stroke so the window's thin lines read thin against it.
    private static SketchTestImages DoorAndWindow()
    {
        var img = new SketchTestImages(900, 600);
        img.RectOutline(100, 80, 700, 440, 8);
        img.Erase(350, 512, 80, 8);            // exterior door, south wall
        img.Erase(250, 80, 150, 8);            // window break, north wall...
        img.FillRect(250, 80, 150, 2);         // ...outer pane line
        img.FillRect(250, 86, 150, 2);         // ...inner pane line, 4 px of light between
        return img;
    }

    // An L-shaped room and a rectangular room in its notch, joined by a door through the notch's
    // north wall (well clear of the corners, so both jambs can vouch for the bridge).
    private static SketchTestImages LShape()
    {
        var img = OneRoom();
        img.FillRect(450, 380, 350, 6);        // horizontal wall carving the notch
        img.FillRect(444, 380, 6, 140);        // vertical wall closing it
        img.Erase(560, 380, 80, 6);            // the door into the notch room
        return img;
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

    private static List<SketchOpening> Windows(SketchPlanSpec spec)
    {
        var windows = new List<SketchOpening>();
        foreach (var o in spec.Openings) if (o.kind == OpeningKind.Window) windows.Add(o);
        return windows;
    }

    // ---- one room -------------------------------------------------------------------------------

    [Test]
    public void OneRoom_IsOneUntypedRoomOnTheWallCenterlines()
    {
        var result = Detect(OneRoom());

        Assert.IsTrue(result.Ok, result.refusal);
        Assert.AreEqual(1, result.spec.rooms.Count);

        var room = result.spec.rooms[0];
        Assert.AreEqual("room1", room.key);
        Assert.AreEqual("Room 1", room.name);
        Assert.AreEqual(RoomType.Untyped, room.roomType);
        Assert.IsFalse(room.IsPart);

        // The outer wall centerlines sit at x 102.5 and 796.5 px of 900, y 82.5 and 516.5 px of 600.
        // A wall's width of slack plus rounding.
        Assert.AreEqual(114, room.x, 12, "left edge");
        Assert.AreEqual(138, room.y, 15, "top edge");
        Assert.AreEqual(771, room.w, 20, "width");
        Assert.AreEqual(723, room.h, 25, "depth");

        // The redundant metric channel must agree with the coordinates, because both came from the
        // same measurement. 6.94 m x 4.34 m on centerlines.
        Assert.AreEqual(6.94f, room.widthMeters, 0.10f);
        Assert.AreEqual(4.34f, room.depthMeters, 0.10f);
    }

    [Test]
    public void RoomDrawnAtTheTopOfTheImage_GetsASmallY_SoTheFlipIsPaidExactlyOnce()
    {
        // GetPixels32 rows arrive bottom-up; the spec's y runs down from the top. A room drawn near
        // the image top must come out with a small y, or the plan installs mirrored.
        var img = new SketchTestImages(300, 300);
        img.RectOutline(40, 30, 200, 120, 6);

        var result = Detect(img);

        Assert.IsTrue(result.Ok, result.refusal);
        Assert.AreEqual(1, result.spec.rooms.Count);
        var room = result.spec.rooms[0];
        Assert.Less(room.y, 300, "the room was drawn in the top half of the picture");
        Assert.Less(room.y + room.h, 600);
    }

    // ---- two rooms and the doorway between them -------------------------------------------------

    [Test]
    public void TwoRooms_VerticalWall_MeetOnOneCenterlineWithOneDoorBetween()
    {
        var result = Detect(TwoRoomsVertical());

        Assert.IsTrue(result.Ok, result.refusal);
        Assert.AreEqual(2, result.spec.rooms.Count);

        // Reading order: the rooms share a top edge, so left comes first.
        var left = result.spec.rooms[0];
        var right = result.spec.rooms[1];
        Assert.AreEqual("room1", left.key);
        Assert.AreEqual("room2", right.key);
        Assert.Less(left.x, right.x);

        // Both sides of the shared wall land on its centerline (446.5 px = 496 normalised), within
        // rounding of each other: the gap between them is the regularizer's job to close, but the
        // detector must not leave a wall's width of void.
        int leftEdge = left.x + left.w;
        Assert.AreEqual(496, leftEdge, 12);
        Assert.AreEqual(496, right.x, 12);
        Assert.LessOrEqual(Mathf.Abs(leftEdge - right.x), 10);

        var doors = Doors(result.spec);
        Assert.AreEqual(1, doors.Count, "one doorway was drawn");
        var door = doors[0];
        Assert.AreEqual(OpeningKind.Door, door.kind);
        Assert.IsTrue(door.IsInterior);
        CollectionAssert.AreEquivalent(new[] { "room1", "room2" }, door.between);
        Assert.AreEqual(0.8f, door.widthMeters, 0.1f);
        Assert.AreEqual(0.5f, door.alongFraction, 0.05f, "the door was drawn centered");

        Assert.AreEqual(0, Windows(result.spec).Count);
    }

    [Test]
    public void DoorNearTheTopOfAVerticalWall_HasALargeAlongFraction_BecauseAlongRunsSouthToNorth()
    {
        // The alongFraction trap: on a vertical wall 0 is the SOUTH end, and south is the BOTTOM of
        // the picture. A door drawn near the top must come out near 1, not near 0.
        var result = Detect(TwoRoomsVertical(doorTopY: 120));

        Assert.IsTrue(result.Ok, result.refusal);
        var doors = Doors(result.spec);
        Assert.AreEqual(1, doors.Count);
        // Gap rows 120..199 of a span 86..513: centre 159.5, so 0.83 from the south end.
        Assert.AreEqual(0.83f, doors[0].alongFraction, 0.05f);
    }

    [Test]
    public void TwoRooms_HorizontalWall_OneDoor_AlongRunsWestToEast()
    {
        var result = Detect(TwoRoomsHorizontal());

        Assert.IsTrue(result.Ok, result.refusal);
        Assert.AreEqual(2, result.spec.rooms.Count);

        var doors = Doors(result.spec);
        Assert.AreEqual(1, doors.Count);
        Assert.IsTrue(doors[0].IsInterior);
        Assert.AreEqual(0.8f, doors[0].widthMeters, 0.1f);
        // Gap columns 400..479 of a span 106..793: centre 439.5, so 0.49 from the west end.
        Assert.AreEqual(0.49f, doors[0].alongFraction, 0.06f);
    }

    // ---- exterior openings ----------------------------------------------------------------------

    [Test]
    public void ExteriorDoorAndWindow_ComeOutOnTheRightEdgesWithTheRightKinds()
    {
        var result = Detect(DoorAndWindow());

        Assert.IsTrue(result.Ok, result.refusal);
        Assert.AreEqual(1, result.spec.rooms.Count);

        var doors = Doors(result.spec);
        Assert.AreEqual(1, doors.Count, "one exterior door was drawn");
        var door = doors[0];
        Assert.IsFalse(door.IsInterior);
        Assert.AreEqual("room1", door.room);
        Assert.AreEqual("south", door.edge, "the door was punched through the picture's bottom wall");
        Assert.AreEqual(0.8f, door.widthMeters, 0.12f);
        // Columns 350..429 of a span about 108..791: centre 389.5, 0.41 from the west end.
        Assert.AreEqual(0.41f, door.alongFraction, 0.07f);

        var windows = Windows(result.spec);
        Assert.AreEqual(1, windows.Count, "one double-line window was drawn");
        var window = windows[0];
        Assert.AreEqual("room1", window.room);
        Assert.AreEqual("north", window.edge);
        Assert.AreEqual(1.5f, window.widthMeters, 0.20f);
        Assert.AreEqual(0.9f, window.sillMeters, 1e-3f);
    }

    // ---- L-shaped rooms -------------------------------------------------------------------------

    [Test]
    public void LShapedRoom_DecomposesIntoAPartAndCompilesToOneRoom()
    {
        var result = Detect(LShape());

        Assert.IsTrue(result.Ok, result.refusal);

        int roots = 0, parts = 0;
        foreach (var r in result.spec.rooms)
        {
            if (r.IsPart) parts++; else roots++;
        }
        Assert.AreEqual(2, roots, "the L room and the room in its notch");
        Assert.AreEqual(1, parts, "the L's second rectangle");

        var doors = Doors(result.spec);
        Assert.AreEqual(1, doors.Count);
        Assert.IsTrue(doors[0].IsInterior, "the door names the two ROOMS, never a part");

        // Through the real compiler: the part must merge into one RoomDef with no wall between.
        var frame = SketchFrame.Build(new[] { 0f, 0f }, 900, 600, MPP, 0f);
        var compiled = SketchPlanCompiler.Compile(result.spec, frame, 0f, 0f);
        Assert.IsTrue(compiled.Ok, compiled.refusal);
        Assert.AreEqual(2, compiled.level.rooms.Count);
        CollectionAssert.IsEmpty(compiled.warnings, string.Join(" | ", compiled.warnings));
    }

    // ---- scale ----------------------------------------------------------------------------------

    [Test]
    public void Uncalibrated_EstimatesTheScaleFromTheDoorway_AndSaysSo()
    {
        var result = Detect(TwoRoomsVertical(), mpp: 0f);

        Assert.IsTrue(result.Ok, result.refusal);
        Assert.IsTrue(result.scaleEstimated);
        Assert.GreaterOrEqual(result.scaleDoorways, 1);

        // The 80 px doorway is declared 0.813 m, so the scale lands at 0.813/80 per pixel.
        Assert.AreEqual(0.813f / 80f, result.metersPerPixel, 0.0006f);

        // And the whole plan must still compile cleanly at that estimated scale.
        var frame = SketchFrame.Build(new[] { 0f, 0f }, 900, 600, result.metersPerPixel, 0f);
        var compiled = SketchPlanCompiler.Compile(result.spec, frame, 0f, 0f);
        Assert.IsTrue(compiled.Ok, compiled.refusal);
        CollectionAssert.IsEmpty(compiled.issues, string.Join(" | ", compiled.issues));
    }

    [Test]
    public void Calibrated_EchoesTheCalibrationBack_AndDoesNotClaimAnEstimate()
    {
        var result = Detect(TwoRoomsVertical());

        Assert.IsTrue(result.Ok, result.refusal);
        Assert.IsFalse(result.scaleEstimated);
        Assert.AreEqual(MPP, result.metersPerPixel, 1e-6f);
    }

    // ---- refusals -------------------------------------------------------------------------------

    [Test]
    public void UncalibratedWithNoDoorways_RefusesAndAsksForTheScale()
    {
        var result = Detect(OneRoom(), mpp: 0f);

        Assert.IsFalse(result.Ok);
        StringAssert.Contains("scale", result.refusal);
    }

    [Test]
    public void BlankImage_Refuses()
    {
        var result = Detect(new SketchTestImages(300, 300));

        Assert.IsFalse(result.Ok);
        Assert.IsNotNull(result.refusal);
    }

    // ---- the hand and the camera ----------------------------------------------------------------

    [Test]
    public void HandDrawnWobbleAndGrain_FindTheSameRoomsAndTheSameDoor()
    {
        var img = TwoRoomsVertical();
        img.Jitter(seed: 1, amplitudePx: 2.5f);
        img.Noise(seed: 2, amplitude: 10);

        var result = Detect(img);

        Assert.IsTrue(result.Ok, result.refusal);
        Assert.AreEqual(2, result.spec.rooms.Count);
        Assert.AreEqual(1, Doors(result.spec).Count);
    }

    [Test]
    public void PhotographedOffSquare_IsStraightenedAndStillReadsBothRooms()
    {
        var img = TwoRoomsVertical();
        img.Rotate(2f);
        img.Vignette(0.4f);

        var result = Detect(img);

        Assert.IsTrue(result.Ok, result.refusal);
        Assert.AreEqual(2f, Mathf.Abs(result.skewDegrees), 0.6f, "the page was turned two degrees");
        Assert.AreEqual(2, result.spec.rooms.Count);
    }

    // ---- determinism ----------------------------------------------------------------------------

    [Test]
    public void Detect_IsDeterministic_FieldForField()
    {
        var img = TwoRoomsVertical();
        img.Jitter(seed: 1, amplitudePx: 2.5f);
        img.Noise(seed: 2, amplitude: 10);
        var pixels = img.Pixels;

        var a = SketchPlanDetector.Detect(pixels, img.Width, img.Height, MPP);
        var b = SketchPlanDetector.Detect(pixels, img.Width, img.Height, MPP);

        Assert.IsTrue(a.Ok && b.Ok);
        Assert.AreEqual(JsonConvert.SerializeObject(a.spec), JsonConvert.SerializeObject(b.spec));
        Assert.AreEqual(a.metersPerPixel, b.metersPerPixel, 0f);
    }

    // ---- the failures the wall graph exists to fix ----------------------------------------------

    [Test]
    public void OffsetDoorway_JambsMisalignedByFivePixels_IsStillOneDoor()
    {
        // The two halves of a hand-drawn divider rarely share a pixel row. The old pixel bridging
        // demanded that they did; the wall graph only asks that they snap to one line.
        var img = OneRoom();
        img.FillRect(100, 294, 300, 6);        // left half of the divider
        img.FillRect(480, 299, 320, 6);        // right half, five pixels lower

        var result = Detect(img);

        Assert.IsTrue(result.Ok, result.refusal);
        Assert.AreEqual(2, result.spec.rooms.Count);
        var doors = Doors(result.spec);
        Assert.AreEqual(1, doors.Count, "the gap between the halves is one doorway");
        Assert.IsTrue(doors[0].IsInterior);
        Assert.AreEqual(0.8f, doors[0].widthMeters, 0.12f);
    }

    [Test]
    public void CornerPenLift_DoesNotLeakTheRooms()
    {
        // A missing corner used to flood a room into the outside and silently delete it. The
        // repair ladder escalates until the two wall ends meet their perpendicular lines.
        var img = TwoRoomsVertical();
        img.Erase(100, 80, 20, 20);            // the pen lifted at the northwest corner

        var result = Detect(img);

        Assert.IsTrue(result.Ok, result.refusal);
        Assert.AreEqual(2, result.spec.rooms.Count, "both rooms survive the open corner");
        Assert.AreEqual(1, Doors(result.spec).Count);
    }

    [Test]
    public void TextAndFurnitureMarks_MintNoRoomsAndNoOpenings()
    {
        var img = TwoRoomsVertical();
        img.Squiggle(180, 150, seed: 3);       // a room label
        img.Squiggle(180, 190, seed: 4);       // a second word under it
        img.FurnitureSymbol(600, 150, 40, 40); // a side table drawn in the right room

        var result = Detect(img);

        Assert.IsTrue(result.Ok, result.refusal);
        Assert.AreEqual(2, result.spec.rooms.Count);
        Assert.AreEqual(1, Doors(result.spec).Count);
        Assert.AreEqual(0, Windows(result.spec).Count);
    }

    [Test]
    public void DimensionLineOutsideThePlan_MintsNoPhantomDoor()
    {
        // Two collinear strokes with an arrowhead gap are exactly the shape of a doorway
        // candidate. The sides of the "gap" are both outside, so nothing may be emitted.
        var img = OneRoom();
        img.FillRect(150, 560, 250, 3);        // left half of a dimension line under the plan
        img.FillRect(480, 560, 320, 3);        // right half, an 80 px arrow gap between

        var result = Detect(img);

        Assert.IsTrue(result.Ok, result.refusal);
        Assert.AreEqual(1, result.spec.rooms.Count);
        Assert.AreEqual(0, Doors(result.spec).Count, "a dimension line is not a door");
        Assert.AreEqual(0, Windows(result.spec).Count);
        CollectionAssert.IsEmpty(result.warnings, string.Join(" | ", result.warnings));
    }

    [Test]
    public void UShapedRoom_DecomposesExactlyAndCompiles()
    {
        // A bay at the top center leaves a U-shaped room around it: three rectangles, one root.
        var img = OneRoom();
        img.FillRect(300, 80, 6, 220);         // the bay's west wall
        img.FillRect(560, 80, 6, 220);         // the bay's east wall
        img.FillRect(300, 294, 266, 6);        // the bay's south wall...
        img.Erase(400, 294, 80, 6);            // ...with the door into the U

        var result = Detect(img);

        Assert.IsTrue(result.Ok, result.refusal);
        int roots = 0, parts = 0;
        foreach (var r in result.spec.rooms)
        {
            if (r.IsPart) parts++; else roots++;
        }
        Assert.AreEqual(2, roots, "the bay and the U around it");
        Assert.AreEqual(2, parts, "the U is three rectangles: a root and two parts");

        var doors = Doors(result.spec);
        Assert.AreEqual(1, doors.Count);
        Assert.IsTrue(doors[0].IsInterior);

        var frame = SketchFrame.Build(new[] { 0f, 0f }, 900, 600, MPP, 0f);
        var compiled = SketchPlanCompiler.Compile(result.spec, frame, 0f, 0f);
        Assert.IsTrue(compiled.Ok, compiled.refusal);
        Assert.AreEqual(2, compiled.level.rooms.Count, "the parts merge into one U room");
        CollectionAssert.IsEmpty(compiled.warnings, string.Join(" | ", compiled.warnings));
    }

    [Test]
    public void PhotographedHandDrawnLShape_ReadsTheRoomsAndTheDoor()
    {
        // The whole gauntlet at once: wobble, grain, a turned page and corner shading, on the
        // least forgiving fixture. This is the photographed hand sketch the rework is for.
        var img = LShape();
        img.Jitter(seed: 1, amplitudePx: 2.5f);
        img.Noise(seed: 2, amplitude: 10);
        img.Rotate(1.5f);
        img.Vignette(0.35f);

        var result = Detect(img);

        Assert.IsTrue(result.Ok, result.refusal);
        int roots = 0;
        foreach (var r in result.spec.rooms) if (!r.IsPart) roots++;
        Assert.AreEqual(2, roots, "the L room and the room in its notch");
        Assert.AreEqual(1, Doors(result.spec).Count);

        // And byte-for-byte determinism holds on the hardest input, not just the clean ones.
        var pixels = img.Pixels;
        var a = SketchPlanDetector.Detect(pixels, img.Width, img.Height, MPP);
        var b = SketchPlanDetector.Detect(pixels, img.Width, img.Height, MPP);
        Assert.AreEqual(JsonConvert.SerializeObject(a.spec), JsonConvert.SerializeObject(b.spec));
    }

    // ---- end to end through the compiler --------------------------------------------------------

    [Test]
    public void TwoRoomPlan_CompilesToTheExpectedWallGraph()
    {
        var result = Detect(TwoRoomsVertical());
        Assert.IsTrue(result.Ok, result.refusal);

        var frame = SketchFrame.Build(new[] { 0f, 0f }, 900, 600, MPP, 0f);
        var compiled = SketchPlanCompiler.Compile(result.spec, frame, 0f, 0f);

        Assert.IsTrue(compiled.Ok, compiled.refusal);
        CollectionAssert.IsEmpty(compiled.issues, string.Join(" | ", compiled.issues));
        CollectionAssert.IsEmpty(compiled.warnings, string.Join(" | ", compiled.warnings));

        // Outer rectangle plus a divider whose ends split the top and bottom walls: 7 wall pieces.
        Assert.AreEqual(7, compiled.level.walls.Count);
        Assert.AreEqual(2, compiled.level.rooms.Count);
        Assert.AreEqual(1, compiled.level.openings.Count);
        Assert.AreEqual(OpeningKind.Door, compiled.level.openings[0].kind);

        // The install path: adopted onto a storey, everything arrives and the ids are re-stemmed.
        var storey = Stories.NewLevel("Ground floor");
        SketchInstall.Adopt(storey, compiled.level, "t1_");
        Assert.AreEqual(7, storey.walls.Count);
        StringAssert.StartsWith("t1_", storey.walls[0].id);
    }
}
