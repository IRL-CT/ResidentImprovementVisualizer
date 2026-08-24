using UnityEngine;

// The time of day the People view is showing, and whether it is running.
//
// A PLAIN CLASS, not a MonoBehaviour, owned by ResidenceRenderer and ticked from its Update. Two reasons it
// cannot live where it looks like it should:
//
//   * Not in a tool. ResidenceEditController gates IResidenceTool.HandleInput on !PointerOverUI, so a clock
//     ticking there would stop dead whenever the cursor rested on a rail, which is exactly where the
//     cursor is while someone watches the timeline.
//   * Not in the document. Every ResidenceDoc mutation is snapshotted for undo and marks the file dirty, so
//     a clock stored there would make each tick an undo entry and each playback an unsaved change.
//
// Being a plain class also means the whole occupants feature needs no scene edit: ResidenceRenderer is
// already wired in ResidenceViz.unity.
public class OccupancyClock
{
    // Where the samples' mornings are busiest: the People view opens on something happening rather
    // than on a houseful of sleeping capsules.
    public const float DefaultMinutes = 7 * 60 + 30;

    // Speed presets, in minutes of the day per real second. One hour per second walks a full day in
    // 24 s, which is about as long as anyone watches before wanting to scrub.
    public static readonly float[] Speeds = { 15f, 30f, 60f, 120f, 240f };

    private float _minutes = DefaultMinutes;
    private int _lastWhole = -1;

    public bool Playing;
    public float MinutesPerSecond = 60f;

    /// <summary>Fractional minutes since midnight, always in [0, 1440).</summary>
    public float Minutes
    {
        get => _minutes;
        set => _minutes = Clock.Wrap(value);
    }

    /// <summary>The whole minute everything is placed at.</summary>
    public int Now => Mathf.FloorToInt(_minutes) % Clock.MinutesPerDay;

    /// <summary>Fraction through the day, for a scrubber.</summary>
    public float DayFraction
    {
        get => _minutes / Clock.MinutesPerDay;
        set => Minutes = value * Clock.MinutesPerDay;
    }

    public string Label => Clock.Format(Now);

    /// <summary>
    /// Advances the clock and reports whether anyone might need moving. Returns true only when the
    /// whole minute changed, so at 60 min/s the poses are recomputed 60 times a second at most, and
    /// while paused, not at all.
    /// </summary>
    public bool Advance(float deltaSeconds)
    {
        if (Playing && deltaSeconds > 0f) Minutes = _minutes + MinutesPerSecond * deltaSeconds;

        int now = Now;
        if (now == _lastWhole) return false;
        _lastWhole = now;
        return true;
    }

    /// <summary>Jumps the clock and forces the next Advance to report a change.</summary>
    public void ScrubTo(float minutes)
    {
        Minutes = minutes;
        _lastWhole = -1;
    }

    /// <summary>Forces a pose refresh without moving the clock. Used after the roster is edited.</summary>
    public void Invalidate() => _lastWhole = -1;

    // WRAPS. It used to clamp, and the only control that calls it only ever passes +1, so after four
    // clicks the chip was stuck at the top and did nothing for the rest of the session.
    public void CycleSpeed(int direction)
    {
        int i = 0;
        for (int k = 0; k < Speeds.Length; k++)
            if (Mathf.Approximately(Speeds[k], MinutesPerSecond)) { i = k; break; }
        int n = Speeds.Length;
        MinutesPerSecond = Speeds[((i + direction) % n + n) % n];
    }

    /// <summary>"1 hr/s": the speed readout beside the play button.</summary>
    public string SpeedLabel => LabelFor(MinutesPerSecond);

    /// <summary>
    /// The label for a speed. "min", never a bare "m": that letter means metres everywhere else on the
    /// same screen. Static so the chip can be measured against every preset and pinned to the widest.
    /// </summary>
    public static string LabelFor(float minutesPerSecond)
    {
        float m = minutesPerSecond;
        return m >= 60f
            ? (m / 60f).ToString(m % 60f == 0f ? "0" : "0.#") + " hr/s"
            : m.ToString("0") + " min/s";
    }
}
