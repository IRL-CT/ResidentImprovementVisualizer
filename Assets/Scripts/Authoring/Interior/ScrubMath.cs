using UnityEngine;

// The arithmetic behind a drag-scrubbable number field: how far a pixel of horizontal travel moves a
// value, what the value snaps to, and what happens at the ends of its range.
//
// This lives in CXRAuthoring for the reason SampleRefresh, Stories and RoomFinish do, so it can be
// tested at all. The control itself (UITheme.DragNumber) is IMGUI in Assembly-CSharp, which asmdefs
// cannot reference and which no EditMode test can drive; but every way this can be subtly wrong is in
// here, in three pure functions, where a test can pin it.
//
// THE ACCUMULATOR CARRIES A VALUE, NOT A PIXEL COUNT. That is the one decision the rest follows from.
// Summing pixels and converting at the end looks equivalent and is not: the conversion rate changes
// the instant Shift goes down, so a pixel total would be re-scaled retroactively and everything
// dragged BEFORE the modifier would silently move. Accumulating in value space means a modifier only
// ever affects travel that comes after it, which is what makes "drag roughly there, hold Shift, ease
// in" work instead of jumping.
//
// Quantisation is applied to the accumulator on the way OUT rather than into it, for the same reason:
// rounding on the way in would make every sub-step motion vanish, so a fine drag under a coarse step
// would move nothing at all no matter how far it travelled.
public static class ScrubMath
{
    /// <summary>Pixels of horizontal travel per step at normal speed, when a caller states no preference.</summary>
    public const float DefaultPxPerStep = 6f;

    /// <summary>How far the pointer must move before a press counts as a drag rather than a click.</summary>
    // Squared, and compared against the squared travel, so the hot path needs no square root. 4px:
    // tight enough that a deliberate nudge registers, loose enough that clicking to type never scrubs.
    public const float DragThresholdSq = 16f;

    /// <summary>Pixels of travel in one event at which acceleration starts to bite.</summary>
    public const float AccelPx = 12f;

    /// <summary>The most acceleration can multiply one event's travel by.</summary>
    public const float AccelMax = 24f;

    /// <summary>The effective step under the fine (Shift) and coarse (Ctrl/Alt) modifiers.</summary>
    // Fine wins when both are held: the cautious reading of an ambiguous chord is the one that cannot
    // run away with a dimension.
    public static float Step(float step, bool fine, bool coarse)
    {
        step = Mathf.Abs(step);
        if (step <= 0f) return 0f;
        if (fine) return step * 0.1f;
        if (coarse) return step * 10f;
        return step;
    }

    /// <summary>
    /// Adds one frame's horizontal travel to the running value. <paramref name="pxPerStep"/> is the
    /// travel one UNMODIFIED step costs; fine drags scale that up so the same pixel buys less.
    /// </summary>
    public static float Advance(float accum, float deltaPx, float step, float pxPerStep,
                                bool fine, bool coarse)
    {
        if (pxPerStep <= 0f) pxPerStep = DefaultPxPerStep;
        float eff = Step(step, fine, coarse);
        if (eff <= 0f) return accum;

        // Speed-based acceleration. A field's whole range can be a couple of thousand pixels at one
        // step per DefaultPxPerStep. Further than the screen is wide, so a long drag died pinned
        // against the display edge with the value still short. A flick therefore buys superlinear
        // travel, while an event slower than AccelPx is multiplied by ~1 and keeps single-step
        // precision. Never under Shift: fine means exact, and acceleration would silently undo it.
        float boost = fine ? 1f
            : Mathf.Min(1f + Mathf.Pow(Mathf.Abs(deltaPx) / AccelPx, 1.3f), AccelMax);

        return accum + deltaPx * boost * (eff / pxPerStep);
    }

    /// <summary>
    /// The value a raw accumulator actually commits to: snapped to the effective step, then wrapped
    /// into or clamped against the range.
    /// </summary>
    // Wrap is for angles, where 359 -> 1 is one degree of travel and not a 358-degree spring back.
    // Everything else clamps, and clamping is what makes a bound legible: the number stops dead under
    // a cursor that is still moving, which is the feedback the Warn border then names.
    public static float Settle(float raw, float step, float min, float max, bool wrap)
    {
        if (step > 0f) raw = Mathf.Round(raw / step) * step;

        // Float error accumulates over a long drag: 0.012 * 37 lands on 0.44399998, which then prints
        // one digit short of what the step promises. Snapping back to the step's own decade fixes it
        // without imposing a fixed precision on callers whose step is 0.003 or 15.
        raw = Quantise(raw, step);

        if (wrap && max > min)
        {
            float span = max - min;
            raw = raw - span * Mathf.Floor((raw - min) / span);
            // Repeat can land exactly on max through rounding, which for a wrapped range IS min.
            if (raw >= max) raw -= span;
            return raw;
        }

        return Mathf.Clamp(raw, min, max);
    }

    /// <summary>Whether a settled value is being held against one end of its range.</summary>
    // Drives the Warn border. Deliberately not "equals the bound": a value that legitimately sits at
    // its minimum should not glow, so the caller passes the UNSETTLED accumulator and this reports
    // only that the drag is still pushing outward against a stop.
    public static bool AtBound(float raw, float min, float max, bool wrap)
        => !wrap && (raw < min || raw > max);

    // Rounds away the float noise a repeated multiply leaves, to one decade finer than the step.
    private static float Quantise(float value, float step)
    {
        if (step <= 0f) return value;
        int decimals = Mathf.Clamp(Mathf.CeilToInt(-Mathf.Log10(step)) + 1, 0, 6);
        float scale = Mathf.Pow(10f, decimals);
        return Mathf.Round(value * scale) / scale;
    }
}
