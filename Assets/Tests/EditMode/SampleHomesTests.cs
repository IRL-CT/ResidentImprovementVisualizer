using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// The six built-in samples are data, and data has no compiler. These tests are what stands between a
// mistyped coordinate and a home that renders with a notched corner, a door clamped to half its width,
// or a bed inside a wall — none of which the render path would report.
[TestFixture]
public class SampleHomesTests
{
    // Footprint area (m²) and the advertised room program, per sample. The rooms tile the footprint
    // exactly, so the area sum is a single check that catches any mistyped rectangle.
    private struct Expect
    {
        public float area;
        public int bedrooms;
        public int bathrooms;
        public bool careSetting;   // all doors 36" and step-free
    }

    private static readonly Dictionary<string, Expect> Expected = new Dictionary<string, Expect>
    {
        ["studio_apartment"] = new Expect { area = 38.28f, bedrooms = 0, bathrooms = 1 },
        ["apartment_2b1b"]   = new Expect { area = 74.00f, bedrooms = 2, bathrooms = 1 },
        ["apartment_5b4b"]   = new Expect { area = 165.00f, bedrooms = 5, bathrooms = 4, careSetting = true },
        ["house_2b1b"]       = new Expect { area = 90.00f, bedrooms = 2, bathrooms = 1 },
        ["house_3b2b"]       = new Expect { area = 125.00f, bedrooms = 3, bathrooms = 2 },
        ["house_5b4b"]       = new Expect { area = 210.00f, bedrooms = 5, bathrooms = 4, careSetting = true },
    };

    private static IEnumerable<string> Keys
    {
        get { foreach (var s in SampleHomes.All) yield return s.key; }
    }

    // ---- the document ----

    [Test]
    public void EverySpecBuilds()
    {
        Assert.AreEqual(6, SampleHomes.All.Count, "Three apartments and three houses.");
        foreach (var key in Keys) Assert.IsNotNull(SampleHomes.Build(key), key);
    }

    [Test, TestCaseSource(nameof(Keys))]
    public void Doc_HasOneLockedBaselineAndNothingElse(string key)
    {
        var doc = SampleHomes.Build(key);

        Assert.AreEqual(1, doc.variants.Count, "Samples ship baseline-only.");
        var baseline = doc.variants[0];
        Assert.IsTrue(baseline.isBaseline);
        Assert.IsTrue(baseline.locked, "The baseline is the record of the home; it ships locked.");
        Assert.AreEqual(baseline.id, doc.activeVariantId);
        Assert.IsFalse(doc.exteriorEnabled, "No SiteDef is authored, so the exterior stays off.");
        Assert.IsNull(baseline.exterior);
        Assert.AreEqual(1, baseline.levels.Count, "Single storey — HomeRenderer only draws levels[0].");
        Assert.IsFalse(string.IsNullOrEmpty(doc.name));
        Assert.Contains("sample", doc.tags);
    }

    // ---- geometry ----

    [Test, TestCaseSource(nameof(Keys))]
    public void Plan_BuildsWithNoWarnings(string key)
    {
        var builder = SampleHomes.Plan(key);
        builder.Build();
        CollectionAssert.IsEmpty(builder.Warnings, $"{key}: " + string.Join(" | ", builder.Warnings));
    }

    [Test, TestCaseSource(nameof(Keys))]
    public void Walls_WeldAtEveryJunctionAndNeverOverlap(string key)
    {
        var level = Level(key);
        PlanBuilderTests.AssertNoInteriorEndpoints(level);
        PlanBuilderTests.AssertNoOverlaps(level);
    }

    [Test, TestCaseSource(nameof(Keys))]
    public void EveryOpening_ResolvesItsWallAndFitsOnIt(string key)
    {
        var level = Level(key);
        Assert.Greater(level.openings.Count, 0);

        foreach (var o in level.openings)
        {
            var wall = PlanBuilderTests.FindWall(level, o.wallId);
            Assert.IsNotNull(wall, $"{key}: opening {o.id} references missing wall '{o.wallId}'.");

            // The check that matters: WallLayout would silently clamp a bad offset, so an opening that
            // is not IsValid renders narrower than authored with no error anywhere.
            Assert.IsTrue(OpeningFit.IsValid(o, wall, level),
                $"{key}: opening {o.id} ({o.kind}, {o.width:0.###} m at {o.offset:0.###} m on a "
              + $"{WallLayout.WallLength(wall):0.###} m wall) does not fit.");

            Assert.LessOrEqual(o.sillHeight + o.height, level.ceilingHeight + 1e-3f,
                $"{key}: opening {o.id} is taller than the wall.");
        }
    }

    [Test, TestCaseSource(nameof(Keys))]
    public void EveryMount_ResolvesItsWallAndSitsOnIt(string key)
    {
        var level = Level(key);
        Assert.Greater(level.wallMounted.Count, 0);

        foreach (var m in level.wallMounted)
        {
            var wall = PlanBuilderTests.FindWall(level, m.wallId);
            Assert.IsNotNull(wall, $"{key}: mount {m.instanceId} references missing wall '{m.wallId}'.");

            float length = WallLayout.WallLength(wall);
            Assert.GreaterOrEqual(m.offset, -1e-3f, $"{key}: mount {m.instanceId} is before the wall.");
            Assert.LessOrEqual(m.offset, length + 1e-3f, $"{key}: mount {m.instanceId} is past the wall.");
            Assert.Greater(m.mountHeight, 0f);
            Assert.Less(m.mountHeight, level.ceilingHeight);

            // Wall mounts take their size from FurnitureCatalog only — never boxSizeMeters — so an
            // unknown key renders as a 0.4 x 0.05 x 0.05 stub instead of the real item.
            Assert.IsTrue(SampleFurniture.Exists(m.prefabType),
                $"{key}: '{m.prefabType}' is not a catalog id.");
        }
    }

    [Test, TestCaseSource(nameof(Keys))]
    public void EveryId_IsUniqueAcrossAllElementTypes(string key)
    {
        // HomeRenderer.Mark writes walls, openings, rooms, furniture and mounts into ONE dictionary,
        // so a collision between a wall and a chair breaks selection rather than just looking odd.
        var level = Level(key);
        var seen = new HashSet<string>();

        void Claim(string id, string what)
        {
            Assert.IsFalse(string.IsNullOrEmpty(id), $"{key}: a {what} has no id.");
            Assert.IsTrue(seen.Add(id), $"{key}: id '{id}' is used more than once ({what}).");
        }

        foreach (var w in level.walls) Claim(w.id, "wall");
        foreach (var o in level.openings) Claim(o.id, "opening");
        foreach (var r in level.rooms) Claim(r.id, "room");
        foreach (var f in level.furniture) Claim(f.instanceId, "furniture");
        foreach (var m in level.wallMounted) Claim(m.instanceId, "mount");
    }

    // ---- the program ----

    [Test, TestCaseSource(nameof(Keys))]
    public void RoomSchedule_MatchesTheAdvertisedProgram(string key)
    {
        var level = Level(key);
        var expect = Expected[key];

        float total = 0f;
        int bedrooms = 0, bathrooms = 0;
        foreach (var r in level.rooms)
        {
            total += RoomMeshBuilder.FloorArea(r);
            if (r.roomType == RoomType.Bedroom) bedrooms++;
            if (r.roomType == RoomType.Bathroom) bathrooms++;

            Assert.Greater(PolygonTriangulator.SignedArea(PolygonTriangulator.ToVector2(r.polygon)), 0f,
                $"{key}: room {r.id} does not wind CCW.");
            Assert.IsFalse(string.IsNullOrEmpty(r.name), $"{key}: room {r.id} has no name.");
        }

        Assert.AreEqual(expect.area, total, 0.02f, $"{key}: the rooms do not tile the footprint.");
        Assert.AreEqual(expect.bedrooms, bedrooms, $"{key}: bedroom count.");
        Assert.AreEqual(expect.bathrooms, bathrooms, $"{key}: bathroom count.");

        AssertRoomsDoNotOverlap(key, level);
    }

    [Test, TestCaseSource(nameof(Keys))]
    public void EveryHome_HasWhatYouNeedToLiveThere(string key)
    {
        var level = Level(key);
        var types = new HashSet<string>();
        foreach (var f in level.furniture) types.Add(f.prefabType);

        Assert.IsTrue(types.Contains("twin_bed") || types.Contains("full_bed")
                   || types.Contains("hospital_bed"), $"{key}: somewhere to sleep.");
        Assert.IsTrue(types.Contains("range"), $"{key}: somewhere to cook.");
        Assert.IsTrue(types.Contains("sink_base"), $"{key}: a kitchen sink.");
        Assert.IsTrue(types.Contains("refrigerator"), $"{key}: food storage.");
        Assert.IsTrue(types.Contains("toilet"), $"{key}: a toilet.");
        Assert.IsTrue(types.Contains("bathtub") || types.Contains("roll_in_shower"),
            $"{key}: somewhere to wash.");
        Assert.IsTrue(types.Contains("sink_pedestal") || types.Contains("vanity"),
            $"{key}: a basin.");
        Assert.IsTrue(types.Contains("sofa") || types.Contains("armchair") || types.Contains("recliner"),
            $"{key}: somewhere to sit.");

        // One bed per bedroom, at least — a five-bedroom sample with four beds is a data bug.
        int beds = 0;
        foreach (var f in level.furniture)
            if (f.prefabType == "twin_bed" || f.prefabType == "full_bed" || f.prefabType == "hospital_bed")
                beds++;
        Assert.GreaterOrEqual(beds, Expected[key].bedrooms, $"{key}: every bedroom needs a bed.");
    }

    // ---- furniture placement ----

    [Test, TestCaseSource(nameof(Keys))]
    public void EveryItem_SitsInsideARoomAndClearOfEveryOther(string key)
    {
        var level = Level(key);
        Assert.Greater(level.furniture.Count, 0);

        var boxes = new List<Rect>();
        foreach (var f in level.furniture)
        {
            Assert.IsTrue(f.included);
            Assert.IsTrue(SampleFurniture.Exists(f.prefabType), $"{key}: '{f.prefabType}' unknown.");
            Assert.AreEqual(0f, f.position[1], 1e-4f, $"{key}: {f.instanceId} floats off the floor.");

            Rect box = Footprint(f);
            boxes.Add(box);

            var room = RoomContaining(level, box.center);
            Assert.IsNotNull(room,
                $"{key}: {f.prefabType} ({f.instanceId}) at {box.center} is not in any room.");

            Rect rect = Bounds(room);
            Assert.IsTrue(Contains(rect, box),
                $"{key}: {f.prefabType} ({f.instanceId}) {Describe(box)} overhangs "
              + $"{room.name} {Describe(rect)}.");
        }

        for (int i = 0; i < boxes.Count; i++)
        for (int j = i + 1; j < boxes.Count; j++)
        {
            float overlap = OverlapArea(boxes[i], boxes[j]);
            Assert.LessOrEqual(overlap, 1e-3f,
                $"{key}: {level.furniture[i].prefabType} and {level.furniture[j].prefabType} "
              + $"overlap by {overlap:0.###} m² ({Describe(boxes[i])} vs {Describe(boxes[j])}).");
        }
    }

    // ---- accessibility: the reason the tool exists ----

    [Test, TestCaseSource(nameof(Keys))]
    public void EveryDoor_IsWideEnoughToUse(string key)
    {
        var level = Level(key);
        bool care = Expected[key].careSetting;
        float floor = care ? 0.914f : HomeConventions.DEFAULT_DOOR_WIDTH;

        foreach (var o in level.openings)
        {
            if (o.kind != OpeningKind.Door) continue;

            Assert.GreaterOrEqual(o.width, floor - 1e-3f,
                $"{key}: door {o.id} is only {Units.Format(o.width)}.");

            if (care)
                Assert.AreEqual(0f, o.thresholdHeight, 1e-4f,
                    $"{key}: door {o.id} has a threshold, but this is a care setting.");
        }
    }

    [Test]
    public void CareSettings_AreStepFreeThroughout()
    {
        foreach (var key in new[] { "apartment_5b4b", "house_5b4b" })
            foreach (var o in Level(key).openings)
                Assert.AreEqual(0f, o.thresholdHeight, 1e-4f, $"{key}: {o.id}");
    }

    [Test]
    public void CareSettings_FitAWheelchairTurningCircleInEveryBedroomAndBathroom()
    {
        // 1.5 m turning circle => 0.75 m radius. Furniture is ignored, which HomeMetrics documents —
        // this measures the room the plan offers, not the room as furnished.
        foreach (var key in new[] { "apartment_5b4b", "house_5b4b" })
        foreach (var room in Level(key).rooms)
        {
            if (room.roomType != RoomType.Bedroom && room.roomType != RoomType.Bathroom) continue;

            var poly = PolygonTriangulator.ToVector2(room.polygon);
            var circle = HomeMetrics.LargestInscribedCircle(poly, 48, 8);

            Assert.IsTrue(circle.valid, $"{key}: {room.name} has no inscribed circle.");
            Assert.GreaterOrEqual(circle.radius, 0.75f,
                $"{key}: {room.name} fits only a {2f * circle.radius:0.00} m turning circle.");
        }
    }

    [Test]
    public void CareSettings_HaveRollInBathingAndGrabBars()
    {
        foreach (var key in new[] { "apartment_5b4b", "house_5b4b" })
        {
            var level = Level(key);

            int rollIn = 0, grabBars = 0;
            foreach (var f in level.furniture) if (f.prefabType == "roll_in_shower") rollIn++;
            foreach (var m in level.wallMounted)
                if (m.prefabType == "grab_bar_24" || m.prefabType == "grab_bar_36") grabBars++;

            Assert.GreaterOrEqual(rollIn, 1, $"{key}: at least one roll-in shower.");
            Assert.GreaterOrEqual(grabBars, 4, $"{key}: grab bars in every bathroom.");
        }
    }

    [Test]
    public void CareSettings_HaveHandrailsAlongTheCorridor()
    {
        foreach (var key in new[] { "apartment_5b4b", "house_5b4b" })
        {
            int handrails = 0;
            foreach (var m in Level(key).wallMounted) if (m.prefabType == "handrail") handrails++;
            Assert.GreaterOrEqual(handrails, 3, $"{key}: the corridor needs handrails.");
        }
    }

    // ---- clear width is derivable everywhere, which is the rules-ready promise ----

    [Test, TestCaseSource(nameof(Keys))]
    public void EveryDoor_ReportsAUsableClearWidth(string key)
    {
        foreach (var o in Level(key).openings)
        {
            if (o.kind != OpeningKind.Door) continue;
            float clear = HomeMetrics.ClearWidth(o);
            Assert.Greater(clear, 0.7f, $"{key}: door {o.id} clear width {Units.Format(clear)}.");
            Assert.Less(clear, o.width + 1e-4f, "Clear width can never exceed the rough opening.");
        }
    }

    // ===========================================================================================

    private static LevelDef Level(string key) => SampleHomes.Build(key).variants[0].levels[0];

    private static Rect Footprint(ObjectInstance f)
    {
        var item = SampleFurniture.Get(f.prefabType);
        Vector2 size = SampleFurniture.FootprintXZ(item, f.rotationY);
        return new Rect(f.position[0] - 0.5f * size.x, f.position[2] - 0.5f * size.y, size.x, size.y);
    }

    private static Rect Bounds(RoomDef room)
    {
        var poly = PolygonTriangulator.ToVector2(room.polygon);
        float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
        foreach (var p in poly)
        {
            x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x);
            z0 = Mathf.Min(z0, p.y); z1 = Mathf.Max(z1, p.y);
        }
        return new Rect(x0, z0, x1 - x0, z1 - z0);
    }

    private static RoomDef RoomContaining(LevelDef level, Vector2 p)
    {
        foreach (var r in level.rooms)
            if (HomeMetrics.PointInPolygon(p, PolygonTriangulator.ToVector2(r.polygon))) return r;
        return null;
    }

    // Items may sit under the wall's own half-thickness, so the room rectangle (a centerline
    // rectangle) is the containment test, not the finished face.
    private static bool Contains(Rect room, Rect box) =>
        box.xMin >= room.xMin - 1e-3f && box.xMax <= room.xMax + 1e-3f &&
        box.yMin >= room.yMin - 1e-3f && box.yMax <= room.yMax + 1e-3f;

    private static float OverlapArea(Rect a, Rect b)
    {
        float w = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
        float h = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
        return w <= 0f || h <= 0f ? 0f : w * h;
    }

    private static void AssertRoomsDoNotOverlap(string key, LevelDef level)
    {
        for (int i = 0; i < level.rooms.Count; i++)
        for (int j = i + 1; j < level.rooms.Count; j++)
        {
            float overlap = OverlapArea(Bounds(level.rooms[i]), Bounds(level.rooms[j]));
            Assert.LessOrEqual(overlap, 1e-3f,
                $"{key}: {level.rooms[i].name} and {level.rooms[j].name} overlap by {overlap:0.###} m².");
        }
    }

    private static string Describe(Rect r) =>
        $"[{r.xMin:0.##}..{r.xMax:0.##}] x [{r.yMin:0.##}..{r.yMax:0.##}]";
}
