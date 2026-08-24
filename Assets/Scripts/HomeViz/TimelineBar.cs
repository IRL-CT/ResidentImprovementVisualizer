using System.Collections.Generic;
using UnityEngine;

// The clock and the household's day, along the bottom of the window.
//
// This was PeopleDashboard, a panel that floated over the middle of the scene and had to be dismissed
// to see the plan it was describing, which is backwards, because the whole argument the timeline makes
// is about the plan: five residents wanting one bathroom at half past seven is a claim you have to be
// LOOKING at the bathroom to feel. So it is a permanent strip instead, and the plan is never covered.
//
// Collapsed it is CollapsedHeight: the hour ruler, one lane per person compressed into a 16 px strip,
// the alert lane, and the transport. Expanded it grows upward into the full per-person gantt the panel
// used to be: exactly one row per person (ExpandedHeight), no taller. The rails deliberately stop at
// the COLLAPSED height and the expanded bar draws over them, so expanding does not re-lay-out anything
// else on screen.
//
// Still a plain class, not a MonoBehaviour. HomeEditController owns one and calls Draw from its OnGUI,
// the same relationship the rails have with UITheme. Everything is derived and nothing is stored: the
// roster comes from the variant, positions from OccupancyModel, the time from the renderer's clock.
// Scrubbing changes no data and dirties no file.
public class TimelineBar
{
    /// <summary>
    /// Height of the collapsed strip. The caller reserves this much even when expanded, so the rails do
    /// not move.
    ///
    /// Ruler (20) + person strip (16) + ALERT LANE (14) + control row (36) = 86, plus UITheme.Inset
    /// taking Pad off BOTH the top and the bottom (28) = 114. Getting this wrong clips the transport
    /// off the bottom edge, which is the one control here nobody can work without. The control row
    /// is RowH + 4 (the clock field's box) plus the button style's 3 px margin above and below: at
    /// 30 the field was squeezed to 24 and its text lost its descenders.
    /// </summary>
    public const float CollapsedHeight = UITheme.Pad * 2f + RulerHeight + StripHeight + AlertLaneHeight + ControlRowHeight;

    private const float RosterWidth = 190f;
    private const float RowHeight = 46f;
    private const float ControlRowHeight = UITheme.RowH + 4f + 6f;
    private const float RulerHeight = 20f;
    private const float StripHeight = 16f;
    private const float AlertLaneHeight = 14f;
    private const float Gutter = 16f;
    /// <summary>The clock field's box. Wide enough for "Time" and "12:45 pm" with the drag gutter.</summary>
    private const float TimeFieldWidth = 220f;
    /// <summary>The now-line. 1.5 px was a hairline you had to hunt for across a 1900 px bar.</summary>
    private const float NowLineWidth = 3f;

    /// <summary>Lanes the collapsed strip can show before it stops trying.</summary>
    private const int StripLanes = 4;

    /// <summary>
    /// How tall the expanded bar is for a roster of <paramref name="people"/>: the ruler, one row per
    /// person (at least one, so an empty household still opens to a gantt-shaped panel), the alert
    /// lane and the transport, inside the panel inset. The caller caps it against the window; past
    /// the cap the rows scroll.
    /// </summary>
    public static float ExpandedHeight(int people)
        => UITheme.Pad * 2f + RulerHeight + Mathf.Max(1, people) * RowHeight + AlertLaneHeight + ControlRowHeight;

    private Vector2 _scroll;
    private Texture2D _px;

    // Text styles sized for the hand-computed rects below. GUI.skin.label is 13 px with 3 px of padding
    // top and bottom (22 px of glyph box) and it used to be painted into 16 and 18 px rects, which
    // sheared every descender: "12p", "9p", "Getting ready". These carry no vertical padding and no
    // wrap, so a rect of their font height holds them.
    private GUIStyle _ruler, _rowName, _rowState, _nowChip;

    // Set when a roster row or the chevron is clicked, so the controller can act on it after the bar
    // has finished drawing: a stage change or an expand applied mid-OnGUI would desync IMGUI's layout
    // and repaint passes.
    public string ClickedOccupantId { get; private set; }
    public bool ToggleRequested { get; private set; }

    /// <summary>
    /// The alert whose mark was clicked, so the caller can scrub to it and select its device. Applied
    /// AFTER the bar has drawn, exactly like the two above and for the same reason. Scrubbing the
    /// clock re-poses everyone and re-tints every sensor mid-OnGUI.
    /// </summary>
    public SensorAlert? ClickedAlert { get; private set; }

    /// <summary>Set on the frame the scrub slider moved, so the caller can re-pose the markers at once.
    /// The clock's own tick would catch up next frame anyway; this removes the lag while dragging.</summary>
    public bool Scrubbed { get; private set; }

    /// <summary>
    /// Draws the bar into <paramref name="area"/>. The caller owns that rect and must include it in its
    /// pointer-over-UI test. Without that, scrubbing the clock also orbits the camera and every click
    /// here falls through to the scene behind.
    /// </summary>
    public void Draw(Rect area, bool expanded, VariantDef variant, LevelDef level, OccupancyClock clock,
                     SensorSim.Day day = default)
    {
        ClickedOccupantId = null;
        ToggleRequested = false;
        ClickedAlert = null;
        Scrubbed = false;
        if (clock == null) return;

        EnsurePixel();
        UITheme.PanelBackground(area);
        EnsureStyles();

        var people = Roster(variant);
        var poses = OccupancyModel.PoseAll(variant, level, clock.Now);

        var inner = UITheme.Inset(area);
        UITheme.BeginRegion(inner);

        // The hour ruler, the gantt rows and the now-line all have to agree about where midnight and
        // where 24:00 are, and only the rows live inside the scroll view. So the scrollbar has to be
        // accounted for ONCE, here, before any of the three is drawn. Computing the track width from
        // `inner` and then drawing the rows into a viewport 16 px narrower is what made the blocks run
        // wide of the ruler and lose late evening under the bar as soon as a household reached five.
        float bodyHeight = expanded
            ? Mathf.Max(RowHeight, inner.height - RulerHeight - AlertLaneHeight - ControlRowHeight)
            : StripHeight;
        bool scrolls = expanded && people.Count * RowHeight > bodyHeight + 0.5f;
        float bodyW = inner.width - (scrolls ? UITheme.ScrollbarW : 0f);

        float trackX = expanded ? RosterWidth + Gutter : 0f;
        float trackW = Mathf.Max(120f, bodyW - trackX);

        DrawHourRuler(trackX, trackW, clock);

        if (expanded)
        {
            _scroll = UITheme.BeginScroll(_scroll, GUILayout.Height(bodyHeight));
            foreach (var person in people) DrawRow(person, poses, trackX, trackW, level);
            UITheme.EndScroll();
        }
        else
        {
            DrawStrip(people, trackX, trackW, level);
        }

        DrawAlertLane(day, trackX, trackW);

        DrawNowLine(trackX, trackW, clock, bodyHeight);
        DrawControls(clock, expanded, people.Count);

        UITheme.EndRegion();
    }

    // ---------------------------------------------------------------------------------------

    private void EnsureStyles()
    {
        if (_ruler != null) return;
        var label = GUI.skin.label;    // UITheme's skin, installed by PanelBackground above
        _ruler = new GUIStyle(label)
        {
            fontSize = 11, wordWrap = false, alignment = TextAnchor.UpperLeft,
            padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0),
            clipping = TextClipping.Overflow,
        };
        _ruler.normal.textColor = UITheme.Ink3;

        _rowName = new GUIStyle(label)
        {
            fontSize = 13, wordWrap = false, alignment = TextAnchor.UpperLeft,
            padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0),
        };
        _rowName.normal.textColor = UITheme.Ink;

        // Ink2, not Ink3: where someone is right now is the one line this column exists to say, and
        // the tertiary hint grey on the card was the least legible text in the bar.
        _rowState = new GUIStyle(_rowName) { fontSize = 11 };
        _rowState.normal.textColor = UITheme.Ink2;

        // The now-chip's own contrast, written into the style. GUI.color tints, so the ambient Ink
        // would bleed through white text (the trap OverlayDraw.Readout documents).
        _nowChip = new GUIStyle(label)
        {
            fontSize = 11, wordWrap = false, alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0),
            clipping = TextClipping.Overflow,
        };
        if (UITheme.MonoFont != null) _nowChip.font = UITheme.MonoFont;
        _nowChip.normal.textColor = Color.white;
    }

    // The speed chip is pinned to the widest of its five labels: a chip that changes width every
    // click would shove the time field beside it sideways, the same reason the top bar pins its
    // units and eye-height chips.
    private static float _speedChipW;
    private static float SpeedChipWidth()
    {
        if (_speedChipW > 0f) return _speedChipW;
        float w = 0f;
        foreach (float s in OccupancyClock.Speeds)
            w = Mathf.Max(w, UITheme.Measure(OccupancyClock.LabelFor(s), UITheme.ChipStyle));
        _speedChipW = w;
        return w;
    }

    private static float HourX(float trackX, float trackW, float hour) => trackX + trackW * hour / 24f;

    // Hour labels every three hours. Enough to read the shape of a day, few enough to stay legible
    // when the window is narrow. Over a tick at every hour, taller on the labelled ones, so the
    // ruler and the grid below it agree about where each hour falls.
    private void DrawHourRuler(float trackX, float trackW, OccupancyClock clock)
    {
        var strip = GUILayoutUtility.GetRect(1f, RulerHeight, GUILayout.ExpandWidth(true));

        var prev = GUI.color;
        for (int hour = 0; hour <= 24; hour++)
        {
            float x = HourX(trackX, trackW, hour);
            bool major = hour % 3 == 0;

            GUI.color = major ? UITheme.Line2 : UITheme.Line;
            float tickH = major ? 5f : 3f;
            GUI.DrawTexture(new Rect(x, strip.yMax - tickH, 1f, tickH), _px);

            if (major && hour < 24)
            {
                GUI.color = Color.white;
                GUI.Label(new Rect(x + 3f, strip.y + 1f, 46f, 14f), Clock.FormatShort(hour * 60), _ruler);
            }
        }
        GUI.color = prev;

        // Clicking the ruler SCRUBS to that time: the spatial gesture the slider used to offer, on
        // the track the now-line is already drawn against. It used to expand the bar instead; the
        // chevron is the expand control, and a ruler that jumped the panel open when you reached for
        // a time was a ruler you learned not to touch. Confined to the track's own span.
        var e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0 && strip.Contains(e.mousePosition)
            && e.mousePosition.x >= trackX && e.mousePosition.x <= trackX + trackW && clock != null)
        {
            float frac = Mathf.Clamp01((e.mousePosition.x - trackX) / Mathf.Max(1f, trackW));
            clock.ScrubTo(frac * Clock.MinutesPerDay);
            Scrubbed = true;
            e.Use();
        }
    }

    // The whole household squeezed into one band: a lane per person, their colour at the left, their
    // day across the rest. Not a summary of the gantt so much as the gantt at a glance. You can still
    // see that everyone is asleep at 3am and that three people converge on the same hour at 7:30.
    private void DrawStrip(List<OccupantDef> people, float trackX, float trackW, LevelDef level)
    {
        var band = GUILayoutUtility.GetRect(1f, StripHeight, GUILayout.ExpandWidth(true));
        if (people.Count == 0) return;

        int shown = Mathf.Min(people.Count, StripLanes);
        float laneH = Mathf.Max(2f, (band.height - (shown - 1)) / shown);

        for (int i = 0; i < shown; i++)
        {
            var person = people[i];
            float y = band.y + i * (laneH + 1f);

            var prev = GUI.color;
            GUI.color = Tint(person);
            GUI.DrawTexture(new Rect(band.x, y, 6f, laneH), _px);
            GUI.color = prev;

            DrawDay(person, new Rect(trackX + 10f, y, trackW - 10f, laneH), level);
        }
    }

    // What the sensing layer noticed, along the same 24 hours as the household's day, and directly
    // under it, which is the whole reason it is here rather than in the console.
    //
    // The argument the timeline exists to make is that a home is a place where things happen at
    // times, and an alert is only legible beside the day that caused it: the 3 AM mark sits under a
    // band of sleep, and the stove mark sits at the end of somebody's cooking block. Read in a list of
    // cards, the same alerts are just cards.
    //
    // Events are hairlines and alerts are marks, because there are hundreds of the first and a handful
    // of the second. Drawing them alike would bury the ones a person is meant to act on.
    private void DrawAlertLane(SensorSim.Day day, float trackX, float trackW)
    {
        var lane = GUILayoutUtility.GetRect(1f, AlertLaneHeight, GUILayout.ExpandWidth(true));

        var prev = GUI.color;
        GUI.color = UITheme.Tile;
        GUI.DrawTexture(new Rect(trackX, lane.y + 5f, trackW, 4f), _px);
        GUI.color = prev;

        if (day.events == null) return;

        // The sensor traffic first, faint, so the alerts land on top of it rather than under it.
        prev = GUI.color;
        GUI.color = new Color(UITheme.Ink3.r, UITheme.Ink3.g, UITheme.Ink3.b, 0.5f);
        foreach (var e in day.events)
        {
            float x = trackX + trackW * e.minute / Clock.MinutesPerDay;
            GUI.DrawTexture(new Rect(x, lane.y + 5f, 1f, 4f), _px);
        }
        GUI.color = prev;

        if (day.alerts == null) return;

        var mouse = Event.current.mousePosition;
        foreach (var alert in day.alerts)
        {
            float x = trackX + trackW * alert.minute / Clock.MinutesPerDay;
            var mark = new Rect(x - 3f, lane.y + 1f, 7f, 12f);

            float[] rgb = SensorSeverity.Swatch(alert.severity);

            prev = GUI.color;
            GUI.color = new Color(rgb[0], rgb[1], rgb[2]);
            GUI.DrawTexture(mark, _px);
            GUI.color = prev;

            // A 7 px mark is a small target, so the hit box is widened rather than the mark: the same
            // trade the roster rows make, and the reason the tooltip is on the rect rather than the
            // texture.
            var hit = new Rect(x - 6f, lane.y, 13f, AlertLaneHeight);
            UITooltip.Hover(hit, $"{Clock.Format(alert.minute)}: {alert.Title}. {alert.body}");

            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && hit.Contains(mouse))
            {
                ClickedAlert = alert;
                e.Use();
            }
        }
    }

    private void DrawRow(OccupantDef person, Dictionary<string, OccupancyModel.Pose> poses,
                         float trackX, float trackW, LevelDef level)
    {
        if (person == null) return;

        var row = GUILayoutUtility.GetRect(1f, RowHeight, GUILayout.ExpandWidth(true));
        poses.TryGetValue(person.id ?? "", out var pose);

        // Name, colour dot, and where they are right now.
        var dot = new Rect(row.x + 2f, row.y + 8f, 10f, 10f);
        var prev = GUI.color;
        GUI.color = Tint(person);
        GUI.DrawTexture(dot, _px);
        GUI.color = prev;

        // Two hand-computed boxes, which is one line each and cannot become two, so these are fitted
        // with an ellipsis rather than wrapped, and carry the full string on hover. Both are data: a
        // resident's own name, and "Master bedroom · Getting ready" composed from a room name and an
        // activity. Sized to their own styles' font heights (no vertical padding), which is what
        // stopped the bottoms of the glyphs being clipped off.
        var nameRect = new Rect(row.x + 18f, row.y + 3f, RosterWidth - 22f, 18f);
        var stateRect = new Rect(row.x + 18f, row.y + 24f, RosterWidth - 22f, 16f);

        string name = person.name ?? "Occupant";
        string state = OccupancyModel.Describe(pose);

        GUI.Label(nameRect, UITheme.Fit(name, _rowName, nameRect.width), _rowName);
        UITooltip.Hover(nameRect, name);

        GUI.Label(stateRect, UITheme.Fit(state, _rowState, stateRect.width), _rowState);
        UITooltip.Hover(stateRect, state);

        DrawDay(person, new Rect(trackX, row.y + 8f, trackW, RowHeight - 20f), level);

        // The whole row is the hit target. Clicking a person here selects and frames them.
        var e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0 && row.Contains(e.mousePosition))
        {
            ClickedOccupantId = person.id;
            e.Use();
        }
    }

    // One filled bar per activity, laid out across the 24-hour track. A block that wraps past midnight
    // is drawn as two pieces rather than clipped, because sleep is the block everyone looks for and it
    // always wraps. Each piece says what it is on hover (the time span, the activity and the room) 
    // because a coloured bar with no name is a bar you have to guess at. Then the hour grid over the
    // top: a hairline at every hour, a little stronger every third, so a block's edges can be read
    // against the ruler without a ruler-length glance.
    private void DrawDay(OccupantDef person, Rect track, LevelDef level)
    {
        var prev = GUI.color;

        GUI.color = UITheme.Tile;
        GUI.DrawTexture(track, _px);

        if (person.schedule != null)
        {
            foreach (var a in person.schedule)
            {
                if (a == null) continue;

                float[] rgb = ActivityKind.Swatch(a.kind);
                // Away blocks are washed out: what matters visually is when the house is occupied.
                float alpha = ActivityKind.IsAway(a.kind) ? 0.35f : 0.9f;
                GUI.color = new Color(rgb[0], rgb[1], rgb[2], alpha);

                int start = Clock.Wrap(a.startMinutes);
                int span = Clock.DurationBetween(a.startMinutes, a.endMinutes);
                string tip = Describe(a, level);

                Block(track, start, Mathf.Min(span, Clock.MinutesPerDay - start), tip);
                int overflow = start + span - Clock.MinutesPerDay;
                if (overflow > 0) Block(track, 0, overflow, tip);
            }
        }

        // The grid, over the blocks so it stays readable across them; faint enough not to read as
        // edges of the blocks themselves. Midnight and 24:00 are the track's own ends.
        for (int hour = 1; hour < 24; hour++)
        {
            GUI.color = new Color(UITheme.Ink.r, UITheme.Ink.g, UITheme.Ink.b, hour % 3 == 0 ? 0.18f : 0.09f);
            GUI.DrawTexture(new Rect(HourX(track.x, track.width, hour), track.y, 1f, track.height), _px);
        }

        GUI.color = prev;
    }

    private void Block(Rect track, int startMinute, int spanMinutes, string tooltip)
    {
        if (spanMinutes <= 0) return;
        float x = track.x + track.width * startMinute / Clock.MinutesPerDay;
        float w = Mathf.Max(1.5f, track.width * spanMinutes / Clock.MinutesPerDay);
        var r = new Rect(x, track.y, w, track.height);
        GUI.DrawTexture(r, _px);
        // Hovered on the rect the block was drawn into, so a lane of the collapsed strip answers too.
        UITooltip.Hover(r, tooltip);
    }

    // "7:00 am (7:45 am) Getting ready · Master bedroom". Room from the level by id; an activity
    // with no room is time away from home.
    private static string Describe(ActivityDef a, LevelDef level)
    {
        string where = "Away";
        if (!string.IsNullOrEmpty(a.roomId) && level?.rooms != null)
        {
            foreach (var r in level.rooms)
            {
                if (r == null || r.id != a.roomId) continue;
                where = string.IsNullOrEmpty(r.name) ? RoomRegions.Pretty(r.roomType) : r.name;
                break;
            }
        }
        return $"{Clock.FormatRange(a.startMinutes, a.endMinutes)} · {ActivityKind.Label(a.kind)} · {where}";
    }

    // Drawn after the rows so it sits on top of every block, and through the ruler, where it carries
    // the time it marks in a chip: the one readout in the bar that sits exactly where the eye is
    // when following the line. Outside the scroll view on purpose: it marks a time, not a row, and
    // should not scroll away. Covers the ruler, the body and the alert lane.
    private void DrawNowLine(float trackX, float trackW, OccupancyClock clock, float bodyHeight)
    {
        float x = trackX + trackW * clock.DayFraction;
        float height = RulerHeight + bodyHeight + AlertLaneHeight;

        var prev = GUI.color;
        GUI.color = UITheme.Accent;
        GUI.DrawTexture(new Rect(x - NowLineWidth * 0.5f, 0f, NowLineWidth, height), _px);

        // The chip: the current time, white on accent, centered on the line and kept inside the track.
        string text = Clock.Format(clock.Now);
        float w = Mathf.Ceil(_nowChip.CalcSize(new GUIContent(text)).x) + 10f;
        float left = Mathf.Clamp(x - w * 0.5f, trackX, Mathf.Max(trackX, trackX + trackW - w));
        var chip = new Rect(left, 0f, w, RulerHeight - 3f);
        GUI.DrawTexture(chip, _px);
        GUI.color = prev;
        GUI.Label(chip, text, _nowChip);
        UITooltip.Hover(chip, "Now. Click the ruler or drag the Time field to move it.");
    }

    // The transport, drawn in both states: this is now the app's ONLY clock. It used to be here, in
    // the panel's header, and again in the People rail: the time was on screen three times and the
    // play button twice.
    private void DrawControls(OccupancyClock clock, bool expanded, int count)
    {
        GUILayout.BeginHorizontal(GUILayout.Height(ControlRowHeight));

        if (UITheme.Chip(expanded ? "▼" : "▲", false, GUILayout.Width(30f))) ToggleRequested = true;
        UITheme.Tip(expanded
            ? "Collapse the timeline"
            : count == 1 ? "Expand to one person's whole day" : $"Expand to all {count} people's days");

        UITheme.Gap();

        if (UITheme.Chip(clock.Playing ? "❚❚" : "▶", clock.Playing, GUILayout.Width(38f)))
            clock.Playing = !clock.Playing;
        UITheme.Tip(clock.Playing ? "Pause the clock" : "Run the clock through the day");

        // Pinned to the widest speed label, so cycling it does not shove the time field sideways.
        if (UITheme.Chip(clock.SpeedLabel, false, GUILayout.Width(SpeedChipWidth())))
            clock.CycleSpeed(1);
        UITheme.Tip("How fast the clock runs. Click to cycle.");

        UITheme.Gap();

        // The clock IS the scrubber. It was a bare slider with a read-only time beside it: the one
        // slider left in the app, and the one number you could not type. A Time field drags across
        // the day (six pixels a quarter-hour, so the row's width is most of a day; Ctrl is coarser)
        // and takes a typed time, and the ruler above jumps to a click. Same control as an
        // activity's start and end in the People rail.
        //
        // Boxed to TimeFieldWidth rather than left to fill the row: a DragNumber expands to whatever it
        // is given, and across a 1900 px bar that put the word "Time" and the value it names at
        // opposite ends of the window. Drag travel is not bounded by the box, so nothing is lost.
        GUILayout.BeginHorizontal(GUILayout.Width(TimeFieldWidth));
        int t = MeasureUI.Time("Time",
                               "Scrub to any time of day. Drag, or type one. Everyone in the plan "
                               + "moves to where they would be.",
                               clock.Now, 15);
        GUILayout.EndHorizontal();
        if (t != clock.Now)
        {
            clock.ScrubTo(t);
            Scrubbed = true;
        }
        GUILayout.FlexibleSpace();

        GUILayout.EndHorizontal();
    }

    // ---------------------------------------------------------------------------------------

    private static List<OccupantDef> Roster(VariantDef variant)
    {
        var list = new List<OccupantDef>();
        if (variant?.occupants == null) return list;
        foreach (var p in variant.occupants) if (p != null) list.Add(p);
        return list;
    }

    private static Color Tint(OccupantDef person)
    {
        float[] rgb = person.color != null && person.color.Length >= 3 ? person.color : OccupantPalette.At(0);
        return new Color(rgb[0], rgb[1], rgb[2]);
    }

    // One white pixel, tinted through GUI.color. UITheme keeps its own private, and a second copy here
    // is cheaper than widening that class's surface for one panel.
    private void EnsurePixel()
    {
        if (_px != null) return;
        _px = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        _px.SetPixel(0, 0, Color.white);
        _px.Apply();
        _px.hideFlags = HideFlags.HideAndDontSave;
    }
}
