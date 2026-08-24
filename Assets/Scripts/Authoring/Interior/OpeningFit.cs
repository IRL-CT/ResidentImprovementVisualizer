using System.Collections.Generic;
using UnityEngine;

// Decides where an opening is allowed to sit on its wall, and slides it to the nearest legal spot.
//
// This exists to make DRAGGING feel right. When someone drags a door along a wall toward a corner,
// the door should stop against the corner and stay there: not vanish, not refuse the drag, not jump
// to the far end. So the primary operation is "clamp to the nearest legal offset", and outright
// rejection is reserved for the cases where no legal offset exists at all (the opening is wider than
// the wall, or the gap between its neighbors is too small to hold it).
//
// The `reason` string is written to be shown verbatim in the inspector rail. Care staff and family
// members use this tool, so "Wider than the wall (7' 3")" beats a silent no-op or an error code.
public static class OpeningFit
{
    public struct Result
    {
        public bool ok;         // false => the opening cannot be placed on this wall at all
        public float offset;    // the clamped, legal centerline offset (valid only when ok)
        public bool clamped;    // true => the request was moved to make it fit
        public string reason;   // human-readable; null when the request was already legal
    }

    /// <summary>
    /// Clamps <paramref name="desiredOffset"/> to the nearest legal position for an opening of
    /// <paramref name="width"/> on a wall of <paramref name="wallLength"/>.
    /// </summary>
    /// <param name="others">Every other opening on the same wall. The one being moved must be
    /// excluded by the caller (or carry a matching <paramref name="ignoreId"/>).</param>
    /// <param name="minEdge">Solid wall required at each end of the run, meters. A door hard against
    /// a corner has nothing to frame into, so a small value here keeps proposals buildable.</param>
    /// <param name="minGap">Solid wall required between two adjacent openings, meters.</param>
    public static Result Fit(
        float desiredOffset,
        float width,
        float wallLength,
        IReadOnlyList<OpeningDef> others,
        string ignoreId = null,
        float minEdge = 0f,
        float minGap = 0f)
    {
        if (wallLength <= HomeConventions.EPS)
            return Fail("This wall has no length.");

        if (width <= HomeConventions.EPS)
            return Fail("Opening has no width.");

        if (width + 2f * minEdge > wallLength + HomeConventions.EPS)
            return Fail($"Too wide for this wall ({Units.Format(wallLength)}).");

        FreeSpan(desiredOffset, wallLength, others, ignoreId, minEdge, minGap,
                 out float lower, out float upper);

        float available = upper - lower;
        if (available + HomeConventions.EPS < width)
            return Fail($"No room here. Only {Units.Format(Mathf.Max(0f, available))} free.");

        float min = lower + 0.5f * width;
        float max = upper - 0.5f * width;
        float clampedOffset = Mathf.Clamp(desiredOffset, min, max);
        bool moved = Mathf.Abs(clampedOffset - desiredOffset) > HomeConventions.EPS;

        return new Result
        {
            ok = true,
            offset = clampedOffset,
            clamped = moved,
            reason = moved ? "Moved to fit." : null,
        };
    }

    // Walk the neighbors to find the free interval that CONTAINS a given position. Lower and upper
    // start at the wall's own ends and are pulled in by whichever openings bracket it. Comparing by
    // CENTER (not by span) is what makes a drag pass cleanly between two existing openings instead of
    // snagging on the one it is currently overlapping.
    //
    // Shared by Fit and MaxWidth on purpose. The width control asks "how wide may this be here?" and
    // the fit asks "where may something this wide sit?", and they are the same question read from
    // opposite ends: two copies of this walk would be two chances for the control to offer a width
    // the fit then refuses, which is the one failure this whole arrangement exists to prevent.
    private static void FreeSpan(float about, float wallLength, IReadOnlyList<OpeningDef> others,
                                 string ignoreId, float minEdge, float minGap,
                                 out float lower, out float upper)
    {
        lower = 0f + minEdge;
        upper = wallLength - minEdge;

        if (others == null) return;

        foreach (var o in others)
        {
            if (o == null) continue;
            if (ignoreId != null && o.id == ignoreId) continue;
            if (o.width <= HomeConventions.EPS) continue;

            float half = 0.5f * o.width;
            float oStart = o.offset - half;
            float oEnd   = o.offset + half;

            if (o.offset <= about)
            {
                if (oEnd + minGap > lower) lower = oEnd + minGap;
            }
            else
            {
                if (oStart - minGap < upper) upper = oStart - minGap;
            }
        }
    }

    /// <summary>
    /// The widest opening that will fit at <paramref name="atOffset"/>, given its neighbors. Zero
    /// when nothing fits there at all.
    /// </summary>
    /// <remarks>
    /// This is what BOUNDS the width field rather than letting it refuse. Fit rejects an over-wide
    /// request outright. Correct for a placement, wrong under a drag-scrubbed number, where the
    /// value in the box would keep climbing while the document silently declined to follow it. Handing
    /// this to MeasureUI.Length as its max means the control can never ask for a width the fit will
    /// turn down.
    /// </remarks>
    public static float MaxWidth(float atOffset, float wallLength, IReadOnlyList<OpeningDef> others,
                                 string ignoreId = null, float minEdge = 0f, float minGap = 0f)
    {
        if (wallLength <= HomeConventions.EPS) return 0f;

        FreeSpan(atOffset, wallLength, others, ignoreId, minEdge, minGap,
                 out float lower, out float upper);
        return Mathf.Max(0f, upper - lower);
    }

    /// <summary>
    /// Convenience overload operating on a live level: resolves the wall length and gathers the
    /// sibling openings, excluding <paramref name="opening"/> itself.
    /// </summary>
    public static float MaxWidth(OpeningDef opening, WallDef wall, LevelDef level,
                                 float minEdge = 0f, float minGap = 0f)
    {
        if (opening == null || wall == null) return 0f;

        float length = WallLayout.WallLength(wall);
        var siblings = WallLayout.OpeningsFor(wall, level);
        return MaxWidth(opening.offset, length, siblings, opening.id, minEdge, minGap);
    }

    /// <summary>
    /// Convenience overload operating on a live level: resolves the wall length and gathers the
    /// sibling openings, excluding <paramref name="opening"/> itself.
    /// </summary>
    public static Result Fit(OpeningDef opening, WallDef wall, LevelDef level, float desiredOffset,
                             float minEdge = 0f, float minGap = 0f)
    {
        float length = WallLayout.WallLength(wall);
        var siblings = WallLayout.OpeningsFor(wall, level);
        return Fit(desiredOffset, opening.width, length, siblings, opening.id, minEdge, minGap);
    }

    /// <summary>
    /// True when the opening currently sits legally on its wall. Used to flag imported or
    /// hand-edited data without moving anything.
    /// </summary>
    public static bool IsValid(OpeningDef opening, WallDef wall, LevelDef level)
    {
        var r = Fit(opening, wall, level, opening.offset);
        return r.ok && !r.clamped;
    }

    /// <summary>
    /// Vertical validity: an opening must fit between the floor and the wall top. Returns the clamped
    /// (sill, height) pair. Separate from the horizontal fit because the two are independent: a
    /// window can be horizontally fine and vertically impossible.
    /// </summary>
    public static void FitVertical(float sill, float height, float wallHeight,
                                   out float fittedSill, out float fittedHeight)
    {
        fittedSill = Mathf.Clamp(sill, 0f, Mathf.Max(0f, wallHeight - HomeConventions.EPS));
        float maxHeight = wallHeight - fittedSill;
        fittedHeight = Mathf.Clamp(height, HomeConventions.EPS, Mathf.Max(HomeConventions.EPS, maxHeight));
    }

    private static Result Fail(string reason) => new Result { ok = false, reason = reason };
}
