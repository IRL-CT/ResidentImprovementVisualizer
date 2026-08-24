using NUnit.Framework;

// Units is the only place meters become text and text becomes meters, so a bug here is a bug in every
// dimension the tool shows. The parse cases below are all real ways people type a measurement into
// the calibration prompt: the field that gates the entire tracing workflow.
[TestFixture]
public class UnitsTests
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

    // ---- formatting ----

    [Test]
    public void Format_WholeFeetAndInches()
    {
        Assert.AreEqual("12' 6\"", Units.FormatFeetInches(3.81f));   // 150"
        Assert.AreEqual("2' 8\"", Units.FormatFeetInches(0.8128f));  // 32"
        Assert.AreEqual("3'", Units.FormatFeetInches(0.9144f));      // exactly 36": no inch part
    }

    [Test]
    public void Format_SubInchRoundsToNearestEighth()
    {
        // A 4.5" stud wall: the default interior partition thickness.
        Assert.AreEqual("4 1/2\"", Units.FormatFeetInches(0.114f));
        Assert.AreEqual("1/2\"", Units.FormatFeetInches(0.0127f));
    }

    [Test]
    public void Format_Zero()
    {
        Assert.AreEqual("0\"", Units.FormatFeetInches(0f));
    }

    [Test]
    public void Format_RoundsUpAcrossTheFootBoundaryConsistently()
    {
        // 11.99" must read as 1', never as 0' 12": this is why rounding happens before the split.
        Assert.AreEqual("1'", Units.FormatFeetInches(11.99f * 0.0254f));
    }

    [Test]
    public void Format_Negative()
    {
        Assert.AreEqual("-2' 8\"", Units.FormatFeetInches(-0.8128f));
    }

    [Test]
    public void FormatMetric_KeepsSubMetrePrecision()
    {
        Assert.AreEqual("0.114 m", Units.FormatMetric(0.114f));
        Assert.AreEqual("3.81 m", Units.FormatMetric(3.81f));
    }

    [Test]
    public void FormatArea_ImperialAndMetric()
    {
        // 10 m² ≈ 107.6 sq ft
        Assert.AreEqual("108 sq ft", Units.FormatArea(10f, Units.UnitSystem.FeetInches));
        Assert.AreEqual("10 m²", Units.FormatArea(10f, Units.UnitSystem.Metric));
    }

    // ---- parsing ----

    [Test]
    public void Parse_FeetAndInches_AllTheWaysPeopleType()
    {
        AssertParses("12' 6\"", 3.81f);
        AssertParses("12'6\"", 3.81f);
        AssertParses("12 ft 6 in", 3.81f);
        // Unclosed inches: the single most common typo, and it must not silently mean 12 feet.
        AssertParses("12' 6", 3.81f);
    }

    [Test]
    public void Parse_FeetOnly()
    {
        AssertParses("12'", 3.6576f);
        AssertParses("12.5'", 3.81f);
        AssertParses("12 feet", 3.6576f);
    }

    [Test]
    public void Parse_InchesOnly()
    {
        AssertParses("6\"", 0.1524f);
        AssertParses("32 in", 0.8128f);
    }

    [Test]
    public void Parse_DoubleApostropheIsInchesNotFeet()
    {
        // The reason inches are matched before feet: `''` starts with `'`, so a feet-first scan
        // would read this as six FEET: a 4x error in the most safety-relevant direction.
        AssertParses("6''", 0.1524f);
    }

    [Test]
    public void Parse_Fractions()
    {
        AssertParses("6 1/2\"", 6.5f * 0.0254f);
        AssertParses("1/2\"", 0.0127f);
        AssertParses("12' 6 1/2\"", 12f * 0.3048f + 6.5f * 0.0254f);
    }

    [Test]
    public void Parse_Metric()
    {
        AssertParses("3.8m", 3.8f);
        AssertParses("3.8 m", 3.8f);
        AssertParses("380cm", 3.8f);
        AssertParses("3810mm", 3.81f);
    }

    [Test]
    public void Parse_BareNumberFollowsDeclaredUnit()
    {
        Assert.IsTrue(Units.TryParse("12", Units.BareUnit.Feet, out float asFeet));
        Assert.AreEqual(3.6576f, asFeet, 0.0005f);

        // A door-width field wants "32" to mean 32 inches, not 32 feet.
        Assert.IsTrue(Units.TryParse("32", Units.BareUnit.Inches, out float asInches));
        Assert.AreEqual(0.8128f, asInches, 0.0005f);

        Assert.IsTrue(Units.TryParse("3.8", Units.BareUnit.Meters, out float asMeters));
        Assert.AreEqual(3.8f, asMeters, 0.0005f);
    }

    [Test]
    public void Parse_RejectsGarbage()
    {
        Assert.IsFalse(Units.TryParse("", out _));
        Assert.IsFalse(Units.TryParse("   ", out _));
        Assert.IsFalse(Units.TryParse(null, out _));
        Assert.IsFalse(Units.TryParse("wide", out _));
        Assert.IsFalse(Units.TryParse("m", out _));
    }

    [Test]
    public void Parse_FallbackUsedOnFailure()
    {
        Assert.AreEqual(1.5f, Units.Parse("nonsense", 1.5f), 0.0001f);
        Assert.AreEqual(0.1524f, Units.Parse("6\"", 1.5f), 0.0005f);
    }

    [Test]
    public void RoundTrip_FormatThenParse()
    {
        foreach (float m in new[] { 0.114f, 0.8128f, 2.44f, 3.81f, 0.9144f })
        {
            string text = Units.FormatFeetInches(m);
            Assert.IsTrue(Units.TryParse(text, out float back), $"failed to re-parse \"{text}\"");
            // Round-trip is bounded by the 1/16" display rounding, not by the parser.
            Assert.AreEqual(m, back, 0.002f, $"round trip drifted for \"{text}\"");
        }
    }

    private static void AssertParses(string text, float expectedMeters)
    {
        Assert.IsTrue(Units.TryParse(text, out float m), $"failed to parse \"{text}\"");
        Assert.AreEqual(expectedMeters, m, 0.0005f, $"wrong value for \"{text}\"");
    }
}
