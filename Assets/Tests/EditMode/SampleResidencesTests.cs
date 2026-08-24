using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// The six built-in samples are data, and data has no compiler. These tests are what stands between a
// mistyped coordinate and a residence that renders with a notched corner, a door clamped to half its width,
// or a bed inside a wall. None of which the render path would report.
[TestFixture]
public class SampleResidencesTests
{
    // Footprint area (m²) and the advertised room program, per sample. The rooms tile the footprint
    // exactly, so the area sum is a single check that catches any mistyped rectangle.
    private struct Expect
    {
        public float area;
        public int bedrooms;
        public int bathrooms;
        public bool careSetting;   // all doors 36" and step-free
        public int occupants;      // the headcount each Spec.blurb advertises
    }

    private static readonly Dictionary<string, Expect> Expected = new Dictionary<string, Expect>
    {
        ["studio_apartment"] = new Expect { area = 38.28f, bedrooms = 0, bathrooms = 1, occupants = 1 },
        ["apartment_2b1b"]   = new Expect { area = 74.00f, bedrooms = 2, bathrooms = 1, occupants = 2 },
        ["apartment_5b4b"]   = new Expect { area = 165.00f, bedrooms = 5, bathrooms = 4, careSetting = true, occupants = 5 },
        ["house_2b1b"]       = new Expect { area = 90.00f, bedrooms = 2, bathrooms = 1, occupants = 2 },
        ["house_3b2b"]       = new Expect { area = 125.00f, bedrooms = 3, bathrooms = 2, occupants = 4 },
        ["house_5b4b"]       = new Expect { area = 210.00f, bedrooms = 5, bathrooms = 4, careSetting = true, occupants = 5 },
    };

    private static IEnumerable<string> Keys
    {
        get { foreach (var s in SampleResidences.All) yield return s.key; }
    }

    // ---- the document ----

    [Test]
    public void EverySpecBuilds()
    {
        Assert.AreEqual(6, SampleResidences.All.Count, "Three apartments and three houses.");
        foreach (var key in Keys) Assert.IsNotNull(SampleResidences.Build(key), key);
    }

    /// <summary>The two care settings ship a smart home proposal beside their baseline.</summary>
    private static bool ShipsTechnology(string key)
        => key == "apartment_5b4b" || key == "house_5b4b";

    [Test, TestCaseSource(nameof(Keys))]
    public void Doc_OpensOnALockedBaseline(string key)
    {
        var doc = SampleResidences.Build(key);

        // Two care settings ship a second variant; the other four are baseline-only. Either way a
        // sample OPENS on the record of how the residence is: the proposal is a click away in the mode
        // band, and Compare is what it is for.
        Assert.AreEqual(ShipsTechnology(key) ? 2 : 1, doc.variants.Count);

        var baseline = doc.variants[0];
        Assert.IsTrue(baseline.isBaseline);
        Assert.IsTrue(baseline.locked, "The baseline is the record of the residence; it ships locked.");
        Assert.AreEqual(baseline.id, doc.activeVariantId);
        Assert.IsFalse(doc.exteriorEnabled, "No SiteDef is authored, so the exterior stays off.");
        Assert.IsNull(baseline.exterior);
        // The samples are single-story by design, not by limitation: the app edits and renders as
        // many floors as a residence has, one at a time. This pins what SampleResidences SHIPS, because
        // SampleRefresh treats a second floor as a signal that a user has started working on a sample
        // and stops refreshing it.
        Assert.AreEqual(1, baseline.levels.Count, "The shipped samples are single-story.");
        Assert.IsFalse(string.IsNullOrEmpty(doc.name));
        Assert.Contains("sample", doc.tags);

        // Every variant a sample ships is stamped and locked, which is what SampleRefresh reads to
        // tell one of ours from one the user branched. Miss it and the residence freezes at the generation
        // that installed it: the exact staleness trap SampleResidences.Generation exists to close.
        foreach (var v in doc.variants)
        {
            Assert.IsTrue(v.fromSample, key + ": a shipped variant is not stamped fromSample");
            Assert.IsTrue(v.locked, key + ": a shipped variant is not locked");
        }
    }

    [Test, TestCaseSource(nameof(Keys))]
    public void OnlyTheCareSamplesShipTechnology(string key)
    {
        var doc = SampleResidences.Build(key);
        var baseline = doc.variants[0];

        // The baseline is the residence as it is: bare. Everything the technology proposal installs has to
        // read as ADDED against it, or the before/after argument has no before.
        Assert.IsTrue(baseline.levels[0].sensors == null || baseline.levels[0].sensors.Count == 0,
                      key + ": the baseline already has devices in it");

        if (!ShipsTechnology(key)) return;

        var tech = doc.variants[1];
        Assert.IsFalse(tech.isBaseline);
        Assert.AreEqual(baseline.id, tech.basedOnVariantId);
        Assert.Greater(tech.levels[0].sensors.Count, 20,
                       key + ": the shipped package is too thin to be a care home's");
    }

    // ---- geometry ----

    [Test, TestCaseSource(nameof(Keys))]
    public void Plan_BuildsWithNoWarnings(string key)
    {
        var builder = SampleResidences.Plan(key);
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

            // Wall mounts take their size from FurnitureCatalog only (never boxSizeMeters) so an
            // unknown key renders as a 0.4 x 0.05 x 0.05 stub instead of the real item.
            Assert.IsTrue(SampleFurniture.Exists(m.prefabType),
                $"{key}: '{m.prefabType}' is not a catalog id.");
        }
    }

    [Test, TestCaseSource(nameof(Keys))]
    public void EveryId_IsUniqueAcrossAllElementTypes(string key)
    {
        // ResidenceRenderer.Mark writes walls, openings, rooms, furniture, mounts AND occupants into ONE
        // dictionary, so a collision between a wall and a chair breaks selection rather than just
        // looking odd. Activity ids share the same namespace by convention, even though nothing marks
        // them today: the People rail addresses activities by id, and a clash there would edit the
        // wrong block.
        var baseline = Baseline(key);
        var level = baseline.levels[0];
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
        foreach (var p in baseline.occupants)
        {
            Claim(p.id, "occupant");
            foreach (var a in p.schedule) Claim(a.id, "activity");
        }

        // Devices go into the same flat dictionary as everything above (ResidenceRenderer.Mark has one) 
        // so a device id colliding with a wall's breaks selection for both.
        if (!ShipsTechnology(key)) return;
        foreach (var s in SampleResidences.Build(key).variants[1].levels[0].sensors) Claim(s.id, "sensor");
    }

    // ---- the smart home package ----

    [Test]
    public void EveryDeviceResolvesItsHost()
    {
        // A device whose host id resolves to nothing renders nowhere, covers nothing and reports
        // nothing. Present in the data and absent everywhere else, with no warning anywhere. It is
        // the sensing layer's version of an opening whose wallId ResidenceRenderer silently skips.
        foreach (var key in Keys)
        {
            if (!ShipsTechnology(key)) continue;

            var doc = SampleResidences.Build(key);
            var tech = doc.variants[1];
            var level = tech.levels[0];

            foreach (var s in level.sensors)
            {
                Assert.IsTrue(SensorDevices.Exists(s.deviceType),
                              $"{key}: '{s.deviceType}' is not a device id.");
                Assert.IsTrue(SensorHost.IsKnown(s.hostKind), $"{key}: unknown host kind.");

                bool found = s.hostKind switch
                {
                    SensorHost.Opening => SensorPose.Find(level.openings, o => o.id, s.hostId) != null,
                    SensorHost.Furniture => SensorPose.Find(level.furniture, f => f.instanceId, s.hostId) != null,
                    SensorHost.Wall => SensorPose.Find(level.walls, w => w.id, s.hostId) != null,
                    SensorHost.Room => SensorPose.Find(level.rooms, r => r.id, s.hostId) != null,
                    SensorHost.Point => SensorPose.Find(level.rooms, r => r.id, s.hostId) != null,
                    SensorHost.Occupant => SensorPose.Find(tech.occupants, p => p.id, s.hostId) != null,
                    _ => false,
                };
                Assert.IsTrue(found,
                    $"{key}: {s.deviceType} hosts on '{s.hostId}', which no {s.hostKind} matches.");
            }
        }
    }

    [Test]
    public void EveryPlacedDeviceIsInsideARoom()
    {
        // A water sensor stepped off the front of a basin against the far wall lands in the room next
        // door, which is how two of a five-bathroom residence's leak detectors ended up watching one
        // bathroom and none watching the other.
        foreach (var key in Keys)
        {
            if (!ShipsTechnology(key)) continue;

            var doc = SampleResidences.Build(key);
            var tech = doc.variants[1];
            var level = tech.levels[0];

            foreach (var s in level.sensors)
            {
                var pose = SensorPose.Resolve(s, level, tech);
                if (!pose.resolved) continue;      // worn: no place in the plan at all

                Assert.IsNotNull(ResidenceMetrics.RoomAt(pose.xz, level),
                    $"{key}: a {s.deviceType} sits outside every room, at {pose.xz}.");
            }
        }
    }

    [Test]
    public void TheCarePackagesCoverWhatTheyClaimTo()
    {
        // The claims the proposal's own description makes, checked. A blurb that says every way out is
        // watched and every bedroom sensed is a claim a care team will read as a commitment.
        foreach (var key in new[] { "apartment_5b4b", "house_5b4b" })
        {
            var doc = SampleResidences.Build(key);
            var tech = doc.variants[1];
            var level = tech.levels[0];

            CollectionAssert.IsEmpty(SensorCoverage.UnmonitoredExits(level),
                                     key + ": a way out of a care home is unwatched");

            foreach (var room in level.rooms)
            {
                if (room.roomType != RoomType.Bedroom) continue;
                Assert.Greater(SensorCoverage.RoomCoverage(level, room), 0.5f,
                               $"{key}: {room.name} is barely covered");
            }

            // A pad under every bed and a pendant for every resident: §4.3.2 and §4.5.1, and what
            // the description promises.
            int beds = 0, pads = 0;
            foreach (var f in level.furniture)
                if (f.prefabType == "twin_bed" || f.prefabType == "full_bed"
                    || f.prefabType == "hospital_bed") beds++;
            foreach (var s in level.sensors)
                if (s.deviceType == "bed_chair_pad") pads++;
            Assert.AreEqual(beds, pads, key + ": not every bed has a pad under it");

            int pendants = 0;
            foreach (var s in level.sensors) if (s.deviceType == "panic_pendant") pendants++;
            Assert.AreEqual(tech.occupants.Count, pendants,
                            key + ": not every resident has a pendant");

            // And it can actually reach staff, which nothing else in the package does.
            Assert.IsFalse(SensorCost.Of(level).hubMissing, key + ": the package has no hub");
        }
    }

    [Test]
    public void ThePackageIsStableAcrossBuilds()
    {
        // Ids have to be deterministic, or a refreshed sample diffs against its predecessor as
        // "every device removed and every device added" rather than as unchanged.
        foreach (var key in new[] { "apartment_5b4b", "house_5b4b" })
        {
            var a = SampleResidences.Build(key).variants[1].levels[0].sensors;
            var b = SampleResidences.Build(key).variants[1].levels[0].sensors;

            Assert.AreEqual(a.Count, b.Count, key);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].id, b[i].id, key + ": device ids are not stable");
                Assert.AreEqual(a[i].hostId, b[i].hostId, key + ": device hosts are not stable");
            }
        }
    }

    [Test]
    public void TheTechnologyProposalChangesNothingButTechnology()
    {
        // A proposal that also moved a wall would make the before/after argument about two things at
        // once. Every change it reports has to be a device.
        foreach (var key in new[] { "apartment_5b4b", "house_5b4b" })
        {
            var doc = SampleResidences.Build(key);
            var changes = VariantDiff.Compare(doc.variants[0], doc.variants[1]);

            Assert.Greater(changes.Count, 0, key + ": the proposal reports no change at all");
            foreach (var c in changes)
                Assert.AreEqual(VariantDiff.ElementKind.Sensor, c.kind,
                                $"{key}: the technology proposal also changes a {c.kind}: {c}");
        }
    }

    // ---- who lives here ----

    [Test, TestCaseSource(nameof(Keys))]
    public void Household_MatchesTheAdvertisedOccupancy(string key)
    {
        var baseline = Baseline(key);
        Assert.IsNotNull(baseline.occupants, $"{key}: no roster at all.");
        Assert.AreEqual(Expected[key].occupants, baseline.occupants.Count,
            $"{key}: the blurb says how many people live here; the roster must agree.");

        foreach (var p in baseline.occupants)
        {
            Assert.IsFalse(string.IsNullOrEmpty(p.name), $"{key}: an occupant has no name.");
            Assert.IsTrue(p.included, $"{key}: {p.name} ships hidden.");
            Assert.IsNotNull(p.color, $"{key}: {p.name} has no marker color.");
            Assert.GreaterOrEqual(p.color.Length, 3, $"{key}: {p.name}'s color is not [r,g,b].");
        }
    }

    [Test, TestCaseSource(nameof(Keys))]
    public void EveryDay_CoversAllOfItWithNoOverlap(string key)
    {
        // The same check OccupancyModel.Validate makes, asserted directly: a gap leaves someone frozen
        // at their last activity and an overlap silently picks whichever block was authored first.
        foreach (var p in Baseline(key).occupants)
        {
            var covered = new bool[Clock.MinutesPerDay];
            foreach (var a in p.schedule)
            {
                int start = Clock.Wrap(a.startMinutes);
                int span = Clock.DurationBetween(a.startMinutes, a.endMinutes);
                for (int i = 0; i < span; i++)
                {
                    int m = Clock.Wrap(start + i);
                    Assert.IsFalse(covered[m],
                        $"{key}: {p.name} is doing two things at {Clock.Format(m)}.");
                    covered[m] = true;
                }
            }

            for (int m = 0; m < covered.Length; m++)
                Assert.IsTrue(covered[m], $"{key}: {p.name} has nothing scheduled at {Clock.Format(m)}.");
        }
    }

    [Test, TestCaseSource(nameof(Keys))]
    public void EveryActivity_NamesARealRoomOrIsExplicitlyOut(string key)
    {
        var baseline = Baseline(key);
        var level = baseline.levels[0];

        foreach (var p in baseline.occupants)
        foreach (var a in p.schedule)
        {
            Assert.IsTrue(ActivityKind.IsKnown(a.kind), $"{key}: {p.name} has kind '{a.kind}'.");

            if (string.IsNullOrEmpty(a.roomId))
            {
                Assert.IsTrue(ActivityKind.IsAway(a.kind),
                    $"{key}: {p.name}'s \"{ActivityKind.Label(a.kind)}\" has no room but is not an 'out' block.");
                continue;
            }

            Assert.IsNotNull(OccupancyModel.FindRoom(level, a.roomId),
                $"{key}: {p.name} is scheduled into '{a.roomId}', which is not a room here.");

            if (string.IsNullOrEmpty(a.anchorId)) continue;
            Assert.IsNotNull(OccupancyModel.FindFurniture(level, a.anchorId),
                $"{key}: {p.name} is anchored to '{a.anchorId}', which is not an item here.");
        }
    }

    [Test, TestCaseSource(nameof(Keys))]
    public void EveryoneStandsInsideTheirOwnRoom_AllDay(string key)
    {
        // Sweeping the whole day at 10-minute steps is what catches a placement that only fails for
        // one activity. LargestInscribedCircle is per-room, so a bad result is bad all day, but an
        // ANCHOR that lands outside its room only shows up while that block is running.
        var baseline = Baseline(key);
        var level = baseline.levels[0];

        for (int minute = 0; minute < Clock.MinutesPerDay; minute += 10)
        {
            var poses = OccupancyModel.PoseAll(baseline, level, minute);

            foreach (var p in baseline.occupants)
            {
                Assert.IsTrue(poses.ContainsKey(p.id), $"{key}: {p.name} has no pose at {Clock.Format(minute)}.");
                var pose = poses[p.id];
                if (!pose.present) continue;

                Assert.IsNotNull(pose.room, $"{key}: {p.name} is present but in no room at {Clock.Format(minute)}.");
                var poly = PolygonTriangulator.ToVector2(pose.room.polygon);
                Assert.IsTrue(ResidenceMetrics.PointInPolygon(pose.xz, poly),
                    $"{key}: {p.name} stands at {pose.xz} at {Clock.Format(minute)}, outside {pose.room.name}.");
            }
        }
    }

    [Test, TestCaseSource(nameof(Keys))]
    public void NobodyEverStandsInsideTheFurniture(string key)
    {
        // The bug this pins: placement used to consult nothing but the room polygon, so an unanchored
        // activity landed on LargestInscribedCircle's center, which is computed on the BARE room and
        // put Maya inside the studio's armchair for four hours a day.
        //
        // The assertion is that nobody's CENTRE is ever inside a footprint. Deliberately not "everyone
        // keeps a full PersonRadius": a 1.8 x 2.0 m care bathroom holding a tub, a toilet and a basin
        // has no 0.52 m clear circle anywhere in it, and pushing people out of the room they are
        // scheduled into would be a worse answer than a tight fit.
        var baseline = Baseline(key);
        var level = baseline.levels[0];

        for (int minute = 0; minute < Clock.MinutesPerDay; minute += 10)
        {
            var poses = OccupancyModel.PoseAll(baseline, level, minute);

            foreach (var p in baseline.occupants)
            {
                var pose = poses[p.id];
                if (!pose.present) continue;

                // The item an activity names is one you are USING (sitting on, lying in, standing at) 
                // so it is not an obstacle for its own occupant.
                var anchor = pose.activity != null
                    ? OccupancyModel.FindFurniture(level, pose.activity.anchorId) : null;

                foreach (var f in level.furniture)
                {
                    if (!f.included || ReferenceEquals(f, anchor)) continue;
                    // Something you can stand on is not an obstacle: a roll-in shower is 50 mm tall.
                    if (SampleFurniture.Get(f.prefabType).height < 0.15f) continue;

                    Assert.Greater(ResidenceMetrics.PointRectDistance(pose.xz, Footprint(f)), 0f,
                        $"{key}: {p.name} stands inside a {f.prefabType} at {Clock.Format(minute)}.");
                }
            }
        }
    }

    [Test, TestCaseSource(nameof(Keys))]
    public void NothingStandsInADoorOrWindow(string key)
    {
        // Nothing downstream complains: WallLayout emits solid boxes only BETWEEN openings, so an item
        // centered on a door renders as a box floating in the hole. Every check here failed before the
        // builder learned about openings: a grab bar dead center in a doorway, a bath across the only
        // way into a bathroom, a wardrobe over a window.
        var level = Level(key);

        foreach (var o in level.openings)
        {
            var wall = PlanBuilderTests.FindWall(level, o.wallId);
            Assert.IsNotNull(wall, $"{key}: opening {o.id} has no wall.");

            bool vertical = Mathf.Abs(wall.a[0] - wall.b[0]) < 1e-3f;
            float coord = vertical ? wall.a[0] : wall.a[1];
            float lo = vertical ? Mathf.Min(wall.a[1], wall.b[1]) : Mathf.Min(wall.a[0], wall.b[0]);
            float openStart = lo + o.offset - 0.5f * o.width;
            float openEnd = lo + o.offset + 0.5f * o.width;

            foreach (var f in level.furniture)
            {
                // An item shorter than the sill passes underneath: a 0.84 m sofa under a 0.914 m
                // window is exactly where a sofa goes, and so is a kitchen run.
                if (SampleFurniture.Get(f.prefabType).height <= o.sillHeight + 1e-3f) continue;

                Rect r = Footprint(f);
                float toWall = vertical ? Mathf.Max(r.xMin - coord, coord - r.xMax)
                                        : Mathf.Max(r.yMin - coord, coord - r.yMax);
                if (toWall > 0.10f) continue;   // not against this wall at all

                float itemStart = vertical ? r.yMin : r.xMin;
                float itemEnd = vertical ? r.yMax : r.xMax;
                Assert.LessOrEqual(Mathf.Min(itemEnd, openEnd) - Mathf.Max(itemStart, openStart), 0.02f,
                    $"{key}: a {f.prefabType} stands in {o.kind} {o.id}.");
            }

            foreach (var m in level.wallMounted)
            {
                if (m.wallId != o.wallId) continue;

                var item = SampleFurniture.Get(m.prefabType);
                float bottom = m.mountHeight - 0.5f * item.height;
                float top = m.mountHeight + 0.5f * item.height;
                if (top <= o.sillHeight + 1e-3f || bottom >= o.sillHeight + o.height - 1e-3f) continue;

                float mountStart = m.offset - 0.5f * item.width;
                float mountEnd = m.offset + 0.5f * item.width;
                float openLocalStart = o.offset - 0.5f * o.width;
                float openLocalEnd = o.offset + 0.5f * o.width;
                Assert.LessOrEqual(Mathf.Min(mountEnd, openLocalEnd) - Mathf.Max(mountStart, openLocalStart), 0.02f,
                    $"{key}: a {m.prefabType} hangs in {o.kind} {o.id}.");
            }
        }
    }

    [Test, TestCaseSource(nameof(Keys))]
    public void EveryWallMount_FitsItsWallAndItsRoom(string key)
    {
        // Two separate failures. Horizontally, PlanBuilder.Find used to check only that a mount's
        // CENTRE landed on a wall segment, so a 0.91 m grab bar could hang 0.155 m past a corner into
        // open air. Vertically, mountHeight is the item's CENTRE, and the renderer read it as the
        // bottom, which put a 0.76 m wall cabinet's top at 2.13 m, essentially in the ceiling.
        var level = Level(key);
        float ceiling = level.ceilingHeight > 0f ? level.ceilingHeight : ResidenceConventions.DEFAULT_CEILING_HEIGHT;

        foreach (var m in level.wallMounted)
        {
            var wall = PlanBuilderTests.FindWall(level, m.wallId);
            Assert.IsNotNull(wall, $"{key}: mount {m.instanceId} has no wall.");

            var item = SampleFurniture.Get(m.prefabType);
            float length = ResidenceMetrics.WallLength(wall);

            Assert.GreaterOrEqual(m.offset - 0.5f * item.width, -0.02f,
                $"{key}: a {m.prefabType} overhangs the start of its wall.");
            Assert.LessOrEqual(m.offset + 0.5f * item.width, length + 0.02f,
                $"{key}: a {m.prefabType} overhangs the end of its {length:F2} m wall.");

            Assert.GreaterOrEqual(m.mountHeight - 0.5f * item.height, 0f,
                $"{key}: a {m.prefabType} is mounted through the floor.");
            Assert.LessOrEqual(m.mountHeight + 0.5f * item.height, ceiling,
                $"{key}: a {m.prefabType} is mounted through the ceiling.");
        }
    }

    [Test]
    public void CareHomes_QueueForTheSharedBathrooms()
    {
        // The claim the two care samples exist to make: bathrooms shared between two bedrooms are used
        // back to back in the morning, not simultaneously. If an edit ever puts two residents in one
        // bathroom at once, that is a change to the argument the sample is making, so say so here.
        foreach (var key in new[] { "apartment_5b4b", "house_5b4b" })
        {
            var baseline = Baseline(key);
            var level = baseline.levels[0];

            for (int minute = 5 * 60; minute < 11 * 60; minute += 5)
            {
                var poses = OccupancyModel.PoseAll(baseline, level, minute);
                var occupied = new Dictionary<string, string>();

                foreach (var p in baseline.occupants)
                {
                    if (!poses.TryGetValue(p.id, out var pose) || !pose.present) continue;
                    if (pose.room == null || pose.room.roomType != RoomType.Bathroom) continue;

                    Assert.IsFalse(occupied.TryGetValue(pose.room.id, out string already),
                        $"{key}: {p.name} and {already} are both in {pose.room.name} "
                      + $"at {Clock.Format(minute)}.");
                    occupied[pose.room.id] = p.name;
                }
            }
        }
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
    public void EveryResidence_HasWhatYouNeedToLiveThere(string key)
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

        // One bed per bedroom, at least: a five-bedroom sample with four beds is a data bug.
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
        float floor = care ? 0.914f : ResidenceConventions.DEFAULT_DOOR_WIDTH;

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
        // 1.5 m turning circle => 0.75 m radius. Furniture is ignored, which ResidenceMetrics documents,
        // this measures the room the plan offers, not the room as furnished.
        foreach (var key in new[] { "apartment_5b4b", "house_5b4b" })
        foreach (var room in Level(key).rooms)
        {
            if (room.roomType != RoomType.Bedroom && room.roomType != RoomType.Bathroom) continue;

            var poly = PolygonTriangulator.ToVector2(room.polygon);
            var circle = ResidenceMetrics.LargestInscribedCircle(poly, 48, 8);

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
            float clear = ResidenceMetrics.ClearWidth(o);
            Assert.Greater(clear, 0.7f, $"{key}: door {o.id} clear width {Units.Format(clear)}.");
            Assert.Less(clear, o.width + 1e-4f, "Clear width can never exceed the rough opening.");
        }
    }

    // ===========================================================================================

    private static LevelDef Level(string key) => SampleResidences.Build(key).variants[0].levels[0];

    // Occupants hang off the variant, not the level, so anything about people needs this rather than
    // Level(key).
    private static VariantDef Baseline(string key) => SampleResidences.Build(key).variants[0];

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
            if (ResidenceMetrics.PointInPolygon(p, PolygonTriangulator.ToVector2(r.polygon))) return r;
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
