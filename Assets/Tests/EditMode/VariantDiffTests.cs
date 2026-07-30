using System.Collections.Generic;
using NUnit.Framework;

// Compare is the feature that makes this a visioning tool rather than a modelling tool: it holds
// "how it is now" next to "what we're proposing". The 3D ghost is the visible half; this list of
// sentences is the half that gets read aloud in a meeting, so the strings are part of the contract.
//
// Everything hinges on element ids surviving a variant duplication. If they don't, every comparison
// degenerates into "removed everything, added everything" — which is why several tests below assert
// on Modified specifically rather than just on the change count.
[TestFixture]
public class VariantDiffTests
{
    private Units.UnitSystem _saved;

    [SetUp]
    public void SetUp()
    {
        _saved = Units.Display;
        Units.Display = Units.UnitSystem.FeetInches;
    }

    [TearDown]
    public void TearDown() => Units.Display = _saved;

    [Test]
    public void IdenticalVariants_ProduceNoChanges()
    {
        Assert.AreEqual(0, VariantDiff.Compare(Baseline(), Baseline()).Count);
    }

    [Test]
    public void NullInputs_AreSafe()
    {
        Assert.AreEqual(0, VariantDiff.Compare(null, Baseline()).Count);
        Assert.AreEqual(0, VariantDiff.Compare(Baseline(), null).Count);
        Assert.AreEqual(0, VariantDiff.Compare(null, null).Count);
    }

    [Test]
    public void WidenedDoor_ReportsAModificationInFeetAndInches()
    {
        var to = Baseline();
        to.levels[0].openings[0].width = 0.9144f;   // 32" -> 36"

        var changes = VariantDiff.Compare(Baseline(), to);
        var change = Single(changes, VariantDiff.ElementKind.Opening);

        Assert.AreEqual(VariantDiff.ChangeType.Modified, change.type);
        StringAssert.Contains("width", change.detail);
        StringAssert.Contains("2' 8\"", change.detail);    // the old 32"
        StringAssert.Contains("3'", change.detail);        // the new 36"
    }

    [Test]
    public void RemovedThreshold_IsCalledOutAsStepFree()
    {
        // The whole point of a lot of these proposals, so it gets prose rather than two numbers.
        var from = Baseline();
        from.levels[0].openings[0].thresholdHeight = 0.019f;

        var changes = VariantDiff.Compare(from, Baseline());
        var change = Single(changes, VariantDiff.ElementKind.Opening);

        StringAssert.Contains("step-free", change.detail);
    }

    [Test]
    public void AddedThreshold_ReportsTheHeight()
    {
        var to = Baseline();
        to.levels[0].openings[0].thresholdHeight = 0.019f;

        var change = Single(VariantDiff.Compare(Baseline(), to), VariantDiff.ElementKind.Opening);

        StringAssert.Contains("threshold added", change.detail);
    }

    [Test]
    public void AddedAndRemovedOpenings()
    {
        var to = Baseline();
        to.levels[0].openings.Add(new OpeningDef
        {
            id = "o2", wallId = "w1", offset = 3f, width = 0.9f, height = 2.032f,
            kind = OpeningKind.Window, swing = OpeningSwing.None, sillHeight = 0.9f,
        });

        var added = Single(VariantDiff.Compare(Baseline(), to), VariantDiff.ElementKind.Opening);
        Assert.AreEqual(VariantDiff.ChangeType.Added, added.type);

        var removed = Single(VariantDiff.Compare(to, Baseline()), VariantDiff.ElementKind.Opening);
        Assert.AreEqual(VariantDiff.ChangeType.Removed, removed.type);
    }

    [Test]
    public void MovedWall_IsReportedAsMoved()
    {
        var to = Baseline();
        to.levels[0].walls[0].b = new[] { 5f, 1f };

        var change = Single(VariantDiff.Compare(Baseline(), to), VariantDiff.ElementKind.Wall);

        Assert.AreEqual(VariantDiff.ChangeType.Modified, change.type);
        StringAssert.Contains("moved", change.detail);
    }

    [Test]
    public void AddedGrabBar_IsReportedWithItsMountHeight()
    {
        var to = Baseline();
        to.levels[0].wallMounted.Add(new WallMountDef
        {
            instanceId = "m1", prefabType = "grab_bar_36", wallId = "w1",
            offset = 1.2f, mountHeight = 0.84f, included = true,
        });

        var change = Single(VariantDiff.Compare(Baseline(), to), VariantDiff.ElementKind.WallMount);

        Assert.AreEqual(VariantDiff.ChangeType.Added, change.type);
        StringAssert.Contains("Grab bar 36", change.label);   // underscores cleaned up for reading
        StringAssert.Contains("2' 9", change.detail);          // 0.84 m mount height
    }

    [Test]
    public void ReplacedFurniture_IsAModificationNotAnAddAndRemove()
    {
        var from = Baseline();
        from.levels[0].furniture.Add(Furniture("f1", "twin_bed", 1f, 1f));

        var to = Baseline();
        to.levels[0].furniture.Add(Furniture("f1", "hospital_bed", 1f, 1f));

        var change = Single(VariantDiff.Compare(from, to), VariantDiff.ElementKind.Furniture);

        Assert.AreEqual(VariantDiff.ChangeType.Modified, change.type);
        StringAssert.Contains("replaced with", change.detail);
    }

    [Test]
    public void MovedFurniture_IsReported()
    {
        var from = Baseline();
        from.levels[0].furniture.Add(Furniture("f1", "armchair", 1f, 1f));

        var to = Baseline();
        to.levels[0].furniture.Add(Furniture("f1", "armchair", 2.5f, 1f));

        var change = Single(VariantDiff.Compare(from, to), VariantDiff.ElementKind.Furniture);

        Assert.AreEqual(VariantDiff.ChangeType.Modified, change.type);
        StringAssert.Contains("moved", change.detail);
    }

    [Test]
    public void RenamedRoom_IsReported()
    {
        var from = Baseline();
        from.levels[0].rooms.Add(new RoomDef
        {
            id = "r1", name = "Bathroom", roomType = RoomType.Bathroom,
            polygon = new[] { new[] { 0f, 0f }, new[] { 3f, 0f }, new[] { 3f, 3f }, new[] { 0f, 3f } },
        });

        var to = Baseline();
        to.levels[0].rooms.Add(new RoomDef
        {
            id = "r1", name = "Accessible bathroom", roomType = RoomType.Bathroom,
            polygon = new[] { new[] { 0f, 0f }, new[] { 3f, 0f }, new[] { 3f, 3f }, new[] { 0f, 3f } },
        });

        var change = Single(VariantDiff.Compare(from, to), VariantDiff.ElementKind.Room);

        StringAssert.Contains("renamed", change.detail);
    }

    // ---- the optional exterior layer ----

    [Test]
    public void AddedExteriorRamp_IsSummarisedNotEnumerated()
    {
        var to = Baseline();
        to.exterior = new SiteDef
        {
            paths = new List<PathDef>
            {
                new PathDef { id = "p1", material = "pavement_light", width = 1.2f,
                              points = new[] { new[] { 0f, 0f }, new[] { 4f, 0f } } },
            },
            fences = new List<FenceDef>
            {
                new FenceDef { id = "f1", fenceType = "wrought_iron",
                               points = new[] { new[] { 0f, 0f }, new[] { 4f, 0f } } },
            },
        };

        var change = Single(VariantDiff.Compare(Baseline(), to), VariantDiff.ElementKind.Exterior);

        Assert.AreEqual(VariantDiff.ChangeType.Added, change.type);
        StringAssert.Contains("1 walkway", change.detail);
        StringAssert.Contains("1 railing", change.detail);
        Assert.IsFalse(change.hasPos, "the exterior summary is not a point in space");
    }

    [Test]
    public void NoExteriorOnEitherSide_ProducesNoExteriorChange()
    {
        var changes = VariantDiff.Compare(Baseline(), Baseline());

        foreach (var c in changes)
            Assert.AreNotEqual(VariantDiff.ElementKind.Exterior, c.kind);
    }

    [Test]
    public void EmptyExteriorObject_IsNotTreatedAsPresent()
    {
        // A SiteDef that exists but holds nothing must not read as "added an exterior".
        var to = Baseline();
        to.exterior = new SiteDef();

        foreach (var c in VariantDiff.Compare(Baseline(), to))
            Assert.AreNotEqual(VariantDiff.ElementKind.Exterior, c.kind);
    }

    [Test]
    public void ChangeToString_ReadsAsASentence()
    {
        var to = Baseline();
        to.levels[0].openings[0].width = 0.9144f;

        var change = Single(VariantDiff.Compare(Baseline(), to), VariantDiff.ElementKind.Opening);

        StringAssert.StartsWith("Changed", change.ToString());
        StringAssert.Contains(":", change.ToString());
    }

    // ---------------------------------------------------------------------------------------

    // A one-wall, one-door level. Ids are fixed so a "duplicated" variant matches element for element.
    private static VariantDef Baseline() => new VariantDef
    {
        id = "v0",
        name = "Existing",
        isBaseline = true,
        levels = new List<LevelDef>
        {
            new LevelDef
            {
                id = "L0", name = "Ground floor",
                ceilingHeight = 2.44f, wallThickness = 0.114f,
                walls = new List<WallDef>
                {
                    new WallDef { id = "w1", a = new[] { 0f, 0f }, b = new[] { 5f, 0f } },
                },
                openings = new List<OpeningDef>
                {
                    new OpeningDef
                    {
                        id = "o1", wallId = "w1", offset = 2.5f,
                        width = 0.8128f,          // 32"
                        height = 2.032f,
                        kind = OpeningKind.Door, swing = OpeningSwing.LeftIn,
                    },
                },
                rooms = new List<RoomDef>(),
                furniture = new List<ObjectInstance>(),
                wallMounted = new List<WallMountDef>(),
            }
        },
    };

    private static ObjectInstance Furniture(string id, string type, float x, float z)
        => new ObjectInstance
        {
            instanceId = id, prefabType = type,
            position = new[] { x, 0f, z }, scale = 1f, included = true,
        };

    private static VariantDiff.Change Single(List<VariantDiff.Change> changes, VariantDiff.ElementKind kind)
    {
        VariantDiff.Change found = default;
        int n = 0;
        foreach (var c in changes)
            if (c.kind == kind) { found = c; n++; }

        Assert.AreEqual(1, n, $"expected exactly one {kind} change, got {n}");
        return found;
    }
}
