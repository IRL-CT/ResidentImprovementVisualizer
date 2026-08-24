using System.Collections.Generic;
using UnityEngine;

/// <summary>One room rectangle in world metres, on wall CENTERLINES. PlanBuilder's convention.</summary>
public struct SketchRect
{
    public string key;
    public string name;
    public string roomType;
    public float x0, z0, x1, z1;

    /// <summary>
    /// Which ROOM this rectangle belongs to, when a room needs more than one of them.
    ///
    /// An L-shaped living room is two rectangles that meet along an edge, and the two are the same
    /// room: no wall between them, one floor, one id, one entry in the change list. Empty or equal to
    /// <see cref="key"/> means the rectangle is a whole room on its own, which is every rectangle in
    /// every shipped sample plan. Hence <see cref="Room"/> falling back rather than a separate flag.
    /// </summary>
    public string roomKey;

    /// <summary>
    /// What the room was SAID to measure, in metres, independently of the rectangle above.
    ///
    /// This is a redundant channel and that is the whole point: the rectangle comes from normalised
    /// image coordinates and these come from reading the drawing, so the two disagree exactly when
    /// the coordinates are wrong at a scale nothing else here can see: a plan traced into half the
    /// 0..1000 range, or a room read off the wrong dimension line. Zero means "not stated", which is
    /// how every hand-built fixture and every non-generated caller leaves it.
    /// </summary>
    public float statedWidth, statedDepth;

    public float Width => x1 - x0;
    public float Depth => z1 - z0;
    public float Area  => Width * Depth;

    /// <summary>The room this rectangle belongs to. Itself, unless it was declared as a part.</summary>
    public string Room => string.IsNullOrEmpty(roomKey) ? key : roomKey;

    /// <summary>True when this rectangle is an extra piece of a room declared elsewhere.</summary>
    public bool IsPart => !string.IsNullOrEmpty(roomKey) && roomKey != key;
}

// Makes room rectangles that ALMOST share an edge actually share it.
//
// WHY THIS EXISTS, AND WHY IT IS THE PART THAT DECIDES WHETHER GENERATION WORKS AT ALL: a model
// asked for integers 0..1000 across a 12 m plan has about 12 mm of granularity at its very best, and
// in practice will put one room's right edge at 358 and its neighbor's left edge at 361. Fed
// straight to PlanBuilder that is not a shared wall. It is TWO near-parallel walls three
// centimetres apart, which is exactly the silent geometry failure PlanBuilder exists to prevent.
// Nothing downstream would say so: WallMeshBuilder would weld neither of them to anything and leave
// a notch, and RoomRegions would find no enclosed area between them.
//
// The existing tolerances are nowhere near coarse enough to close that gap and must not be widened
// to try: PlanBuilder.Q quantises to 1 mm because that is inside WallMeshBuilder.Near's weld radius,
// and Spans.TOL is 2 mm. Both are claims about rendering precision. What is needed here is a
// different KIND of number (a claim about buildings) so it lives here, applied before PlanBuilder
// ever sees a coordinate.
//
// IT RUNS IN METRES, AFTER THE IMAGE-TO-WORLD TRANSFORM, for that same reason. A tolerance in
// normalised units would mean a different physical distance on every sketch.
//
// THE ENVELOPE IS MEASURED, NOT ASSERTED. Driving all six shipped plans through this pass with every
// coordinate knocked out of place independently, which is what a model reading a picture actually
// produces, and the reason a per-ROOM jitter would prove nothing. Gives a sharp edge:
//
//     +/- 0.03 m   every plan rebuilds identically      +/- 0.15 m   every plan rebuilds identically
//     +/- 0.06 m   every plan rebuilds identically      +/- 0.20 m   walls start to double
//
// "Identically" is the strong form: the same wall COUNT as the authored plan (13, 22, 39, 25, 34 and
// 46 respectively), no unwelded T-junction, no overlapping pair, and nothing in PlanBuilder.Warnings.
// So the model has about 15 cm of room per coordinate. Roughly 12 units of the 0..1000 range on a
// 12 m plan, which is a generous budget for reading a calibrated sheet. Past 0.20 m the two sides of
// a shared edge can differ by more than the tolerance and the pass correctly stops guessing.
//
// SketchRegularizerTests pins both ends: the no-op on all six samples, and recovery at 0.15 m.
public static class SketchRegularizer
{
    /// <summary>
    /// How far apart two room boundaries can be and still be judged the same wall.
    ///
    /// This is the one number here worth arguing about, and it is a claim about dwellings rather
    /// than about arithmetic. Too small and shared walls stay doubled: the notch above. Too large
    /// and a genuine narrow chase or a shallow closet is merged out of existence.
    ///
    /// 0.25 m is defensible because rooms are addressed on CENTERLINES: two adjacent rooms share one
    /// centerline, so two genuinely distinct boundaries a quarter of a metre apart would mean a
    /// 25 cm void between rooms. That is a chase, not a room, and no sample plan has one.
    /// </summary>
    public const float DefaultTolerance = 0.25f;

    /// <summary>
    /// The tightest separation between two distinct wall lines that any shipped sample plan expresses
    ///: 0.400 m, a closet return in the five-bedroom house.
    ///
    /// It is published because two different files need the same number for opposite reasons. Here it
    /// is a CEILING: <see cref="DefaultTolerance"/> times the cluster-width cap has to stay clear of it,
    /// or a genuine narrow return gets merged out of existence. In SketchPlanValidator it is a FLOOR:
    /// a gap narrower than this between two rooms that do not touch is not a chase, it is a pair of
    /// walls with a void between them, which is the failure this whole pass exists to prevent and
    /// which nothing downstream would report. Stating it once is what keeps the two from drifting.
    /// </summary>
    public const float MinGenuineSeparation = 0.40f;

    /// <summary>
    /// Below this, a rectangle is not a room. It is two boundaries the clustering has merged into
    /// one. Dropped and reported rather than emitted, because PlanBuilder would take a 5 cm "room"
    /// perfectly seriously and derive four walls around it.
    /// </summary>
    public const float MinRoomSide = 0.60f;

    /// <summary>
    /// How wide one cluster may grow, as a multiple of the tolerance.
    ///
    /// THIS NUMBER IS MEASURED, NOT CHOSEN. Across all six shipped sample plans the tightest genuine
    /// separation between two distinct wall lines on the same axis is 0.40 m: a closet return in
    /// the five-bedroom house. Any cluster allowed to span that far could swallow it, so the product
    /// of this factor and the tolerance has to stay clear of 0.40: 1.4 x 0.25 = 0.35 m.
    ///
    /// It also has to exist at all, which is the less obvious half. Pure single linkage CHAINS: a run
    /// of coordinates each 0.24 m from the last would merge into one cluster spanning several metres,
    /// which is how a row of narrow closets collapses into a single wall. Both bounds are pinned by
    /// SketchRegularizerTests, and the six-sample no-op is what would fail first if either moved.
    ///
    /// The 0.40 m it has to stay clear of is <see cref="MinGenuineSeparation"/>, which is where that
    /// number is stated and explained.
    /// </summary>
    private const float MAX_SPREAD_FACTOR = 1.4f;

    /// <summary>Coordinates settle on a millimetre, so PlanBuilder's own quantiser leaves them alone.</summary>
    private const float GRID = 0.001f;

    /// <summary>
    /// Snaps every room's edges onto shared coordinates and drops anything that collapses.
    ///
    /// Surviving rooms keep their input ORDER: the same rule RoomRegions.Sync follows, and for the
    /// same reason: order is not identity, and reshuffling would make the result depend on something
    /// the caller never asked about.
    /// </summary>
    public static List<SketchRect> Snap(IReadOnlyList<SketchRect> rects, float tolerance,
                                        List<string> warnings = null)
    {
        var result = new List<SketchRect>(rects?.Count ?? 0);
        if (rects == null || rects.Count == 0) return result;

        var xs = new List<float>(rects.Count * 2);
        var zs = new List<float>(rects.Count * 2);
        foreach (var r in rects)
        {
            xs.Add(Mathf.Min(r.x0, r.x1)); xs.Add(Mathf.Max(r.x0, r.x1));
            zs.Add(Mathf.Min(r.z0, r.z1)); zs.Add(Mathf.Max(r.z0, r.z1));
        }

        var xClusters = Cluster(xs, tolerance);
        var zClusters = Cluster(zs, tolerance);

        foreach (var r in rects)
        {
            var snapped = r;
            snapped.x0 = SnapTo(xClusters, Mathf.Min(r.x0, r.x1));
            snapped.x1 = SnapTo(xClusters, Mathf.Max(r.x0, r.x1));
            snapped.z0 = SnapTo(zClusters, Mathf.Min(r.z0, r.z1));
            snapped.z1 = SnapTo(zClusters, Mathf.Max(r.z0, r.z1));

            if (snapped.Width < MinRoomSide || snapped.Depth < MinRoomSide)
            {
                warnings?.Add($"'{Label(r)}' came out {Fmt(snapped.Width)} x {Fmt(snapped.Depth)} m "
                            + "once its walls were lined up with its neighbors', which is too small "
                            + "to be a room. It was left out.");
                continue;
            }

            result.Add(snapped);
        }

        return result;
    }

    public static List<SketchRect> Snap(IReadOnlyList<SketchRect> rects, List<string> warnings = null)
        => Snap(rects, DefaultTolerance, warnings);

    // -----------------------------------------------------------------------------------------

    private struct Band
    {
        public float lo, hi, rep;
    }

    /// <summary>
    /// One-dimensional single-linkage clustering, with a width cap.
    ///
    /// The representative is the cluster MEAN and not its first, smallest or largest member. That
    /// choice is what makes the pass IDEMPOTENT and order-independent: the mean minimises total
    /// movement and does not depend on which room happened to be listed first. Re-running Snap on
    /// its own output is then a no-op, because adjacent representatives are provably more than
    /// `tolerance` apart: the clusters they came from were separated by a gap wider than that.
    ///
    /// The width cap is the guard against CHAINING. Pure single linkage would merge a run of
    /// coordinates each 24 cm from the last into one cluster spanning several metres, which is how a
    /// row of narrow closets would collapse into a single wall. A cluster may not grow wider than
    /// twice the tolerance; past that, a new one starts.
    /// </summary>
    private static List<Band> Cluster(List<float> values, float tolerance)
    {
        var bands = new List<Band>();
        if (values.Count == 0) return bands;

        float tol = Mathf.Max(tolerance, GRID);
        float maxWidth = MAX_SPREAD_FACTOR * tol;

        values.Sort();

        int start = 0;
        double sum = values[0];
        for (int i = 1; i <= values.Count; i++)
        {
            bool split = i == values.Count
                      || values[i] - values[i - 1] > tol
                      || values[i] - values[start] > maxWidth;

            if (!split) { sum += values[i]; continue; }

            int count = i - start;
            bands.Add(new Band
            {
                lo = values[start],
                hi = values[i - 1],
                rep = Quantise((float)(sum / count)),
            });

            if (i == values.Count) break;
            start = i;
            sum = values[i];
        }

        return bands;
    }

    /// <summary>
    /// The representative of the band containing <paramref name="v"/>. The bands partition every
    /// coordinate that went in, so this always finds one; the nearest-band fallback exists only so a
    /// caller that snaps a value the clustering never saw gets a sane answer rather than a zero.
    /// </summary>
    private static float SnapTo(List<Band> bands, float v)
    {
        float best = v;
        float bestDist = float.MaxValue;

        for (int i = 0; i < bands.Count; i++)
        {
            var b = bands[i];
            if (v >= b.lo - GRID && v <= b.hi + GRID) return b.rep;

            float d = v < b.lo ? b.lo - v : v - b.hi;
            if (d < bestDist) { bestDist = d; best = b.rep; }
        }

        return best;
    }

    private static float Quantise(float v) => Mathf.Round(v / GRID) * GRID;

    private static string Label(SketchRect r)
        => !string.IsNullOrEmpty(r.name) ? r.name : (r.key ?? "a room");

    private static string Fmt(float v) => v.ToString("0.00");
}
