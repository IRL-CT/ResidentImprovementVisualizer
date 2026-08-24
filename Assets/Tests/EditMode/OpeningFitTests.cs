using System.Collections.Generic;
using NUnit.Framework;

// OpeningFit exists to make DRAGGING a door feel right: it slides to the nearest legal spot rather
// than refusing or teleporting. These tests pin that behaviour, plus the two cases where there
// genuinely is no legal spot and the tool has to say so in words a care worker can act on.
[TestFixture]
public class OpeningFitTests
{
    private const float L = 5f;

    [Test]
    public void FreeWall_LegalRequestIsUntouched()
    {
        var r = OpeningFit.Fit(2.5f, 1f, L, null);

        Assert.IsTrue(r.ok);
        Assert.IsFalse(r.clamped);
        Assert.AreEqual(2.5f, r.offset, 1e-4f);
        Assert.IsNull(r.reason);
    }

    [Test]
    public void DragPastTheEnd_ClampsInsteadOfFailing()
    {
        var r = OpeningFit.Fit(99f, 1f, L, null);

        Assert.IsTrue(r.ok);
        Assert.IsTrue(r.clamped);
        Assert.AreEqual(L - 0.5f, r.offset, 1e-4f);
    }

    [Test]
    public void DragBeforeTheStart_ClampsToHalfWidth()
    {
        var r = OpeningFit.Fit(-3f, 1f, L, null);

        Assert.IsTrue(r.ok);
        Assert.AreEqual(0.5f, r.offset, 1e-4f);
    }

    [Test]
    public void MinEdge_KeepsSolidWallAtTheCorner()
    {
        var r = OpeningFit.Fit(0f, 1f, L, null, minEdge: 0.2f);

        Assert.IsTrue(r.ok);
        Assert.AreEqual(0.7f, r.offset, 1e-4f);   // 0.2 edge + half of a 1.0 opening
    }

    [Test]
    public void WiderThanTheWall_Fails()
    {
        var r = OpeningFit.Fit(2.5f, 6f, L, null);

        Assert.IsFalse(r.ok);
        StringAssert.Contains("Too wide", r.reason);
    }

    [Test]
    public void ZeroLengthWallOrWidth_Fails()
    {
        Assert.IsFalse(OpeningFit.Fit(0f, 1f, 0f, null).ok);
        Assert.IsFalse(OpeningFit.Fit(0f, 0f, L, null).ok);
    }

    [Test]
    public void NeighborOnTheLeft_PushesTheRequestClear()
    {
        var others = new List<OpeningDef> { At(1.0f, 1.0f) };   // occupies 0.5 .. 1.5

        var r = OpeningFit.Fit(1.2f, 1f, L, others);

        Assert.IsTrue(r.ok);
        Assert.IsTrue(r.clamped);
        Assert.AreEqual(2.0f, r.offset, 1e-4f);   // clear of 1.5, plus half of a 1.0 opening
    }

    [Test]
    public void NeighborOnTheRight_PushesTheRequestClear()
    {
        var others = new List<OpeningDef> { At(4.0f, 1.0f) };   // occupies 3.5 .. 4.5

        var r = OpeningFit.Fit(3.8f, 1f, L, others);

        Assert.IsTrue(r.ok);
        Assert.AreEqual(3.0f, r.offset, 1e-4f);
    }

    [Test]
    public void SqueezedBetweenTwoNeighbors_FailsWithTheSpaceAvailable()
    {
        var others = new List<OpeningDef>
        {
            At(1.0f, 1.0f),   // .. 1.5
            At(2.2f, 1.0f),   // 1.7 ..
        };

        var r = OpeningFit.Fit(1.6f, 1f, L, others);

        Assert.IsFalse(r.ok);
        StringAssert.Contains("No room", r.reason);
    }

    [Test]
    public void ExactlyFitsBetweenNeighbors_Succeeds()
    {
        var others = new List<OpeningDef>
        {
            At(0.5f, 1.0f),   // .. 1.0
            At(2.5f, 1.0f),   // 2.0 ..
        };

        var r = OpeningFit.Fit(1.5f, 1.0f, L, others);

        Assert.IsTrue(r.ok);
        Assert.AreEqual(1.5f, r.offset, 1e-4f);
    }

    [Test]
    public void IgnoreId_LetsAnOpeningBeDraggedPastItself()
    {
        // Without this, moving an opening would collide with its own current position and jam.
        var self = At(2.5f, 1.0f);
        self.id = "self";
        var others = new List<OpeningDef> { self };

        var r = OpeningFit.Fit(2.6f, 1f, L, others, ignoreId: "self");

        Assert.IsTrue(r.ok);
        Assert.AreEqual(2.6f, r.offset, 1e-4f);
    }

    [Test]
    public void MinGap_ReservesStudSpaceBetweenOpenings()
    {
        var others = new List<OpeningDef> { At(1.0f, 1.0f) };   // .. 1.5

        var r = OpeningFit.Fit(1.6f, 1f, L, others, minGap: 0.3f);

        Assert.IsTrue(r.ok);
        Assert.AreEqual(2.3f, r.offset, 1e-4f);   // 1.5 + 0.3 gap + 0.5 half-width
    }

    // MaxWidth is what BOUNDS the width field, so that dragging it can never ask for a width Fit
    // refuses. Before it, the number in the box climbed while the document silently declined to
    // follow: the control and the model disagreeing, with nothing on screen saying so.

    [Test]
    public void MaxWidth_OnAFreeWall_IsTheWholeWall()
    {
        Assert.AreEqual(L, OpeningFit.MaxWidth(2.5f, L, null), 1e-4f);
    }

    [Test]
    public void MaxWidth_ReservesTheEdgeAtBothEnds()
    {
        Assert.AreEqual(L - 0.4f, OpeningFit.MaxWidth(2.5f, L, null, minEdge: 0.2f), 1e-4f);
    }

    [Test]
    public void MaxWidth_BetweenTwoNeighbors_IsTheGap()
    {
        // The same pair as SqueezedBetweenTwoNeighbors_FailsWithTheSpaceAvailable: 1.5 .. 1.7.
        var others = new List<OpeningDef> { At(1.0f, 1.0f), At(2.2f, 1.0f) };

        Assert.AreEqual(0.2f, OpeningFit.MaxWidth(1.6f, L, others), 1e-4f);
    }

    [Test]
    public void MaxWidth_IgnoresTheOpeningBeingResized()
    {
        var self = At(2.5f, 1.0f);
        self.id = "self";
        var others = new List<OpeningDef> { self };

        // Without the exclusion an opening could never be widened. It would collide with itself.
        Assert.AreEqual(L, OpeningFit.MaxWidth(2.5f, L, others, ignoreId: "self"), 1e-4f);
    }

    [Test]
    public void MaxWidth_ZeroLengthWall_IsZero()
    {
        Assert.AreEqual(0f, OpeningFit.MaxWidth(0f, 0f, null), 1e-4f);
    }

    // THE PROPERTY THE WIDTH CONTROL RESTS ON: a width MaxWidth allows is always a width Fit accepts.
    // Fit and MaxWidth read the same question from opposite ends, so a careless edit to FreeSpan that
    // shifted one and not the other would show up here and nowhere else.
    [Test]
    public void MaxWidth_IsAlwaysAcceptedByFit()
    {
        var cases = new[]
        {
            new List<OpeningDef>(),
            new List<OpeningDef> { At(1.0f, 1.0f) },
            new List<OpeningDef> { At(1.0f, 1.0f), At(2.2f, 1.0f) },
            new List<OpeningDef> { At(0.5f, 1.0f), At(2.5f, 1.0f), At(4.2f, 0.6f) },
        };

        foreach (var others in cases)
            for (float offset = 0.1f; offset < L; offset += 0.1f)
                foreach (float edge in new[] { 0f, 0.2f })
                {
                    float w = OpeningFit.MaxWidth(offset, L, others, minEdge: edge);
                    if (w <= 1e-4f) continue;   // nothing fits here; Fit is entitled to refuse

                    var r = OpeningFit.Fit(offset, w, L, others, minEdge: edge);
                    Assert.IsTrue(r.ok,
                        $"MaxWidth offered {w:0.####} at {offset:0.##} (edge {edge}) and Fit refused: {r.reason}");
                }
    }

    [Test]
    public void FitVertical_ClampsSillAndHeightIntoTheWall()
    {
        OpeningFit.FitVertical(sill: 0.9f, height: 5f, wallHeight: 2.5f,
                               out float sill, out float height);

        Assert.AreEqual(0.9f, sill, 1e-4f);
        Assert.AreEqual(1.6f, height, 1e-3f);
    }

    [Test]
    public void FitVertical_SillBelowFloorIsRaised()
    {
        OpeningFit.FitVertical(sill: -1f, height: 1f, wallHeight: 2.5f,
                               out float sill, out float height);

        Assert.AreEqual(0f, sill, 1e-4f);
        Assert.AreEqual(1f, height, 1e-4f);
    }

    private static OpeningDef At(float offset, float width) => new OpeningDef
    {
        id = "o" + offset, wallId = "wall", offset = offset, width = width,
        height = 2.032f, kind = OpeningKind.Door,
    };
}
