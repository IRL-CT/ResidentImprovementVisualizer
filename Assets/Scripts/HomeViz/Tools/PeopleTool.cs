using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Who lives here, and what their day looks like.
//
// The rail edits ONE person; the TimelineBar along the bottom shows everyone at once. That split is
// deliberate: 310 px is a good width for a name, a wheelchair toggle and a list of blocks, and a
// hopeless one for a 24-hour chart.
//
// Times are edited with steppers rather than text fields. A stepper click is one discrete edit and
// therefore one undo entry, where a text field either commits half-typed input ("19:" parses as
// nothing, "1" parses as 1 AM) or needs focus tracking to know when the user has finished. In a tool
// used live in a meeting, ±15 minutes on a button is also simply faster than typing.
//
// The clock itself is NOT here, and no longer even displayed here: HandleInput is skipped whenever the
// cursor is over a rail, so a clock ticking in a tool would stop exactly when someone is watching the
// timeline. It lives on HomeRenderer, and the TimelineBar is the one place it is read and scrubbed.
public class PeopleTool : HomeToolBase
{
    public override string Id => "people";
    public override string DisplayName => "People";

    public override string Hint =>
        "Who lives here and what their day is. Positions in the plan are derived from these schedules "
        + "and the clock along the bottom. Pick a person, give them blocks, and set each block's room "
        + "by clicking it in the plan.";

    private const int STEP_MINUTES = 15;

    // The activity row expanded for editing, and the one waiting for a room to be clicked in the plan.
    private string _editingActivity;
    private string _assigningRoomFor;

    // Always: a click here either assigns a room to an activity block or picks a resident, and both
    // are the People stage's own work.
    public override bool ClaimsClicks => true;

    private Vector2 _scroll;

    // Anything that changes how many controls the rail draws is deferred to the next Layout pass.
    // IMGUI requires the Layout and Repaint passes of one frame to agree on the control count, so
    // deleting a row from inside the loop that draws it throws; queueing it here does not.
    private System.Action _deferred;

    public override void Exit()
    {
        _editingActivity = null;
        _assigningRoomFor = null;
        _deferred = null;
    }

    // ---------------------------------------------------------------------------------------

    public override void HandleInput()
    {
        if (Ctx?.Level == null) return;

        if (KeyDown(Key.Escape)) _assigningRoomFor = null;

        if (!LeftClicked()) return;

        // Assigning a room: the next click on the floor is the answer, not a selection.
        if (_assigningRoomFor != null)
        {
            if (!Ctx.GroundPoint(out Vector2 xz)) return;

            var room = HomeMetrics.RoomAt(xz, Ctx.Level);
            if (room == null) { Ctx.Controller.Status("That is not inside a room."); return; }

            var activity = FindActivity(_assigningRoomFor, out _);
            if (activity != null)
            {
                Ctx.RecordEdit("Move activity");
                activity.roomId = room.id;
                TouchSchedule();
                Ctx.Controller.Status("Moved to " + OccupancyModel.RoomLabel(room));
            }
            _assigningRoomFor = null;
            return;
        }

        if (Ctx.PickElement(out HomeElementMarker marker, out _) &&
            marker.kind == HomeElementMarker.Kind.Occupant)
        {
            // reveal: false. People is already an inspector-shaped stage, and being thrown out of it
            // onto Select in the middle of editing somebody's day helps nobody.
            Ctx.Controller.Select(marker.kind, marker.id, reveal: false);
            _editingActivity = null;
        }
    }

    public override void DrawOverlay()
    {
        var person = Selected();
        if (person == null || Ctx?.Cam == null || Ctx.Level == null) return;

        var pose = OccupancyModel.PoseAt(person, Now, Ctx.Level);
        if (!pose.present || pose.room == null) return;

        // Outline the room they are in, so the marker and the space it is in read as one thing.
        var poly = PolygonTriangulator.ToVector2(pose.room.polygon);
        if (poly == null || poly.Count < 3) return;

        float y = Ctx.Level.elevation;
        var accent = new Color(0.29f, 0.47f, 0.78f, 0.9f);

        for (int i = 0; i < poly.Count; i++)
        {
            if (!OverlayDraw.ToScreen(Ctx.Cam, poly[i], y, out Vector2 a)) continue;
            if (!OverlayDraw.ToScreen(Ctx.Cam, poly[(i + 1) % poly.Count], y, out Vector2 b)) continue;
            OverlayDraw.Line(a, b, accent, 2f);
        }

        if (OverlayDraw.ToScreen(Ctx.Cam, pose.xz, y, out Vector2 at))
            OverlayDraw.Readout(at, person.name + " · " + OccupancyModel.Describe(pose));
    }

    // ---------------------------------------------------------------------------------------
    // Rail
    // ---------------------------------------------------------------------------------------

    public override void DrawRail()
    {
        if (Ctx?.Doc == null) return;

        if (Event.current.type == EventType.Layout && _deferred != null)
        {
            var run = _deferred;
            _deferred = null;
            run();
        }

        // No clock and no open/close button here. Both moved to the timeline bar along the bottom,
        // which is permanent: this rail used to carry a second copy of the transport and a third copy
        // of the time, and a button for a panel that no longer needs opening.
        var people = People();

        DrawRoster(people);

        // The Tip stays inside the guard. It hangs off GetLastRect(), so with the button gone it
        // would attach to the roster row above it.
        if (!Ctx.IsLocked)
        {
            if (UITheme.SecondaryButton("+ Add person")) _deferred = AddPerson;
            UITheme.Tip(people == null || people.Count == 0
                ? "Add the first resident, then give them a day."
                : "Add another resident.");
        }

        var selected = Selected();
        if (selected == null) return;

        UITheme.Divider();
        DrawPerson(selected);
    }

    /// <summary>The roster row's height with a one-line name and a state line: what the › beside it is sized to.</summary>
    private const float RowGlyphH = 48f;

    private void DrawRoster(List<OccupantDef> people)
    {
        if (people == null || people.Count == 0) return;

        var poses = Ctx.Renderer?.CurrentPoses();

        foreach (var p in people)
        {
            if (p == null) continue;

            string state = poses != null && poses.TryGetValue(p.id ?? "", out var pose)
                ? OccupancyModel.Describe(pose)
                : "Unknown";

            bool active = Ctx.Controller.SelectedId == p.id;
            string name = p.name ?? "Occupant";

            // The row plus a › on its right: the row is the hit target, the chevron is the affordance
            // that says a click opens something: this person's settings and day, below the list.
            GUILayout.BeginHorizontal();
            bool hit = UITheme.StateRow(name, state, active, muted: !p.included,
                                        reserveRight: UITheme.GlyphReserve);
            UITheme.Tip(active ? $"{name}'s settings and day are open below."
                               : $"Open {name}'s settings and day.");
            // A fixed height, matched to a one-line row (title + state + the row's padding): an
            // ExpandHeight button in this horizontal group stretched to the whole rail.
            if (UITheme.GhostButton("›", GUILayout.Width(UITheme.GlyphW), GUILayout.Height(RowGlyphH)))
                hit = true;
            UITheme.Tip($"Open {name}'s settings and day.");
            GUILayout.EndHorizontal();
            if (!hit) continue;

            Ctx.Controller.Select(HomeElementMarker.Kind.Occupant, p.id, reveal: false);
            _editingActivity = null;

            // Same as clicking the row in the People view: picking a name off a list is asking where
            // that person is. Clicking their marker in the plan is NOT. You are already looking at
            // them, and closing in would throw away the framing you clicked from.
            Ctx.Controller.FocusElement(p.id, p.name);
        }
    }

    private void DrawPerson(OccupantDef person)
    {
        UITheme.Header(person.name ?? "Occupant");

        if (Ctx.IsLocked)
        {
            RefuseIfLocked();
            DrawDayReadOnly(person);
            return;
        }

        string name = UITheme.TextRow("Name", person.name, "What this resident is called");
        if (name != person.name)
        {
            Ctx.RecordEdit("Rename occupant");
            person.name = name;
            TouchRoster();
        }

        bool chair = UITheme.Toggle("Uses a wheelchair", person.usesWheelchair,
            "Shows them seated in the plan, at the same eye height the walkthrough's Seated "
            + "setting uses, and gives them a wheelchair-sized footprint to stand clear in.");
        if (chair != person.usesWheelchair)
        {
            Ctx.RecordEdit("Edit occupant");
            person.usesWheelchair = chair;
            // The marker changes shape and height, so this one needs a rebuild rather than a re-pose.
            TouchRoster();
        }

        bool shown = UITheme.Toggle("Show in the plan", person.included,
            "Whether their marker appears in the plan and on the timeline");
        if (shown != person.included)
        {
            Ctx.RecordEdit("Show/hide occupant");
            person.included = shown;
            TouchRoster();
        }

        DrawColorPicker(person);

        UITheme.GapTight();
        string note = UITheme.TextRow("Note", person.note,
            "Anything worth recording about this resident. Shown when they are selected");
        if (note != person.note)
        {
            Ctx.RecordEdit("Edit occupant note");
            person.note = note;
            Ctx.Controller.MarkDirty();
        }

        UITheme.Header("Their day");
        DrawDay(person);

        UITheme.GapTight();
        if (UITheme.SecondaryButton("+ Add activity")) _deferred = () => AddActivity(person);
        UITheme.Tip("Add a block to their day. Where they stand in the plan comes from these blocks "
                    + "and the clock. A day with no blocks puts them nowhere.");

        UITheme.GapTight();
        if (UITheme.DangerButton("Remove this person")) _deferred = () => RemovePerson(person);
        UITheme.Tip("Take this resident out of the household");
    }

    // A colour chip's box, and how far inside it the swatch sits so the chip's own rim (and the accent
    // ring when it is the chosen one) stays visible around it.
    private const float SwatchW = 28f;
    private const float SwatchInset = 7f;

    private void DrawColorPicker(OccupantDef person)
    {
        UITheme.GapTight();
        // Wrapping, because the palette is data: one un-wrapped row of 28 px chips runs off the rail
        // as soon as OccupantPalette holds more than about eight colours.
        var row = UITheme.ChipRow();
        // The one chip row whose chips carry no text of their own (they are colour swatches) so the
        // label is the only thing on the row that says what it is for.
        row.Label("Color");

        int picked = -1;
        for (int i = 0; i < OccupantPalette.Count; i++)
        {
            float[] rgb = OccupantPalette.At(i);
            bool active = person.color != null && person.color.Length >= 3 &&
                          Mathf.Approximately(person.color[0], rgb[0]) &&
                          Mathf.Approximately(person.color[1], rgb[1]);
            // Every chip is drawn every pass. Leaving the loop early would change the control count
            // mid-frame, which is the one thing IMGUI will not forgive.
            //
            // An EMPTY chip with the swatch PAINTED over it, not a "●" tinted through contentColor: the
            // chip's face has no such glyph, so the dot fell back to another font and came out as a
            // bar. A painted square is the colour at full strength whatever the font ships with.
            if (row.Chip("", active, SwatchW)) picked = i;
            if (Event.current.type == EventType.Repaint)
            {
                var r = GUILayoutUtility.GetLastRect();
                var prev = GUI.color;
                GUI.color = new Color(rgb[0], rgb[1], rgb[2]);
                GUI.DrawTexture(new Rect(r.x + SwatchInset, r.y + SwatchInset,
                                         r.width - SwatchInset * 2f, r.height - SwatchInset * 2f),
                                UITheme.Pixel);
                GUI.color = prev;
            }
            UITheme.Tip("Color this resident's marker and their row on the timeline");
        }

        row.End();

        if (picked < 0) return;
        Ctx.RecordEdit("Recolor occupant");
        person.color = OccupantPalette.At(picked);
        TouchRoster();
    }

    private void DrawDayReadOnly(OccupantDef person)
    {
        UITheme.Header("Their day");
        if (person.schedule == null) return;
        foreach (var a in person.schedule)
        {
            if (a == null) continue;
            // Row content: this list is the person's day, not a caption on something else. Three
            // composed parts, one of them the user's own activity label and one a room name, so it is
            // bounded to the rail and left to wrap rather than running off the panel.
            UITheme.Value(Clock.FormatRange(a.startMinutes, a.endMinutes) + "   " + Title(a) +
                          "   " + RoomName(a.roomId), "A block in their day",
                          GUILayout.Width(UITheme.ContentWidth));   // a row of the day, not a figure
        }
    }

    private void DrawDay(OccupantDef person)
    {
        // Empty: the "+ Add activity" button below is the whole panel, and it says so on hover.
        if (person.schedule == null || person.schedule.Count == 0) return;

        _scroll = UITheme.BeginScroll(_scroll, GUILayout.MaxHeight(Screen.height * 0.34f));

        // A copy, so an activity can be deleted from inside the loop.
        var day = new List<ActivityDef>(person.schedule);
        foreach (var a in day)
        {
            if (a == null) continue;

            bool editing = _editingActivity == a.id;
            if (UITheme.StateRow(Clock.FormatRange(a.startMinutes, a.endMinutes) + "   " + Title(a),
                                 RoomName(a.roomId), editing))
                _editingActivity = editing ? null : a.id;

            if (editing) DrawActivityEditor(person, a);
        }

        UITheme.EndScroll();
    }

    private void DrawActivityEditor(OccupantDef person, ActivityDef a)
    {
        UITheme.GapTight();

        // Kind first: it is what colours the block on the timeline and, for a new activity, what
        // chooses the room. Changing it never rewrites a room already chosen. roomId stays king.
        // Three per row was a guess: "Getting ready" alone needs about 100 px of a 282 px rail, so a
        // row of three of the longer labels overran. ChipRow measures instead.
        var kinds = UITheme.ChipRow();
        kinds.Label("Doing");
        foreach (var kind in ActivityKind.All)
        {
            bool hit = kinds.Chip(ActivityKind.Label(kind), a.kind == kind);
            UITheme.Tip($"{ActivityKind.Label(kind)}. Colors the block on the timeline, and suggests "
                        + "a room for a new one");
            if (!hit || a.kind == kind) continue;

            Ctx.RecordEdit("Change activity");
            a.kind = kind;
            if (kind == ActivityKind.Out) a.roomId = null;
            TouchSchedule();
        }
        kinds.End();

        int start = MeasureUI.Time("Starts",
                                   "When this block starts. Drag to scrub it, or type a time: "
                                   + "7:30, 07:30, 7:30 pm.", a.startMinutes, STEP_MINUTES);
        if (start != a.startMinutes)
        {
            Ctx.RecordEdit("Retime activity");
            a.startMinutes = start;
            TouchSchedule();
        }

        int end = MeasureUI.Time("Ends",
                                 "When it ends. An end before the start wraps past midnight, which is "
                                 + "how sleep is expressed.", a.endMinutes, STEP_MINUTES);
        if (end != a.endMinutes)
        {
            Ctx.RecordEdit("Retime activity");
            a.endMinutes = end;
            TouchSchedule();
        }

        UITheme.Value("Room", RoomName(a.roomId), "Where they are during this block");
        GUILayout.BeginHorizontal();
        bool picking = _assigningRoomFor == a.id;
        if (UITheme.Chip(picking ? "Click a room…" : "Set room", picking))
            _assigningRoomFor = picking ? null : a.id;
        UITheme.Tip("Then click a room in the plan to put them there for this block");
        bool outNow = UITheme.Chip("Out", string.IsNullOrEmpty(a.roomId));
        UITheme.Tip("Away from home. Their marker hides for this block");
        if (outNow && !string.IsNullOrEmpty(a.roomId))
        {
            Ctx.RecordEdit("Send occupant out");
            a.roomId = null;
            TouchSchedule();
        }
        GUILayout.EndHorizontal();

        bool del = UITheme.DangerButton("Delete activity");
        UITheme.Tip("Remove this block from their day");
        if (del)
            _deferred = () =>
            {
                Ctx.RecordEdit("Delete activity");
                person.schedule.Remove(a);
                _editingActivity = null;
                TouchSchedule();
            };

        UITheme.GapTight();
    }

    // ---------------------------------------------------------------------------------------
    // Edits
    // ---------------------------------------------------------------------------------------

    private void AddPerson()
    {
        var variant = Ctx.Variant;
        if (variant == null) return;

        Ctx.RecordEdit("Add occupant");
        variant.occupants ??= new List<OccupantDef>();

        int n = variant.occupants.Count;
        var person = new OccupantDef
        {
            id = System.Guid.NewGuid().ToString(),
            name = "Person " + (n + 1),
            color = OccupantPalette.At(n),
            included = true,
            schedule = new List<ActivityDef>(),
        };

        // A starter day that covers all 1440 minutes, so the new person is somewhere from the moment
        // they exist. An empty schedule would put them nowhere and warn about a gap.
        string bedroom = FirstRoomOfType(RoomType.Bedroom) ?? FirstRoom();
        string living = FirstRoomOfType(RoomType.Living) ?? FirstRoom();
        person.schedule.Add(Activity(ActivityKind.Sleep, 22 * 60, 7 * 60, bedroom));
        person.schedule.Add(Activity(ActivityKind.Relax, 7 * 60, 22 * 60, living));

        variant.occupants.Add(person);
        Ctx.Controller.Select(HomeElementMarker.Kind.Occupant, person.id, reveal: false);
        TouchRoster();
    }

    private void RemovePerson(OccupantDef person)
    {
        Ctx.RecordEdit("Remove occupant");
        Ctx.Variant?.occupants?.Remove(person);
        Ctx.Controller.ClearSelection();
        _editingActivity = null;
        TouchRoster();
    }

    private void AddActivity(OccupantDef person)
    {
        Ctx.RecordEdit("Add activity");
        person.schedule ??= new List<ActivityDef>();

        // Starts at whatever the clock is showing: the user is almost always looking at the moment
        // they want to describe.
        int start = Now;
        var a = Activity(ActivityKind.Other, start, Clock.Wrap(start + 60), FirstRoom());
        person.schedule.Add(a);
        _editingActivity = a.id;
        TouchSchedule();
    }

    private static ActivityDef Activity(string kind, int start, int end, string roomId)
        => new ActivityDef
        {
            id = System.Guid.NewGuid().ToString(),
            kind = kind,
            startMinutes = Clock.Wrap(start),
            endMinutes = Clock.Wrap(end),
            roomId = roomId,
        };

    // A schedule edit only moves markers; the roster changing adds or removes them.
    private void TouchSchedule()
    {
        Ctx.Controller.MarkDirty();
        Ctx.Renderer?.UpdateOccupantPoses();
    }

    private void TouchRoster()
    {
        Ctx.Controller.MarkDirty();
        Ctx.Renderer?.RebuildOccupants();
    }

    // ---------------------------------------------------------------------------------------

    private int Now => Ctx.Renderer?.Occupancy?.Now ?? 0;

    private List<OccupantDef> People() => Ctx.Variant?.occupants;

    private OccupantDef Selected()
    {
        if (Ctx?.Controller == null || Ctx.Controller.SelectedKind != HomeElementMarker.Kind.Occupant)
            return null;

        string id = Ctx.Controller.SelectedId;
        var people = People();
        if (string.IsNullOrEmpty(id) || people == null) return null;
        foreach (var p in people) if (p != null && p.id == id) return p;
        return null;
    }

    private ActivityDef FindActivity(string activityId, out OccupantDef owner)
    {
        owner = null;
        var people = People();
        if (people == null || string.IsNullOrEmpty(activityId)) return null;

        foreach (var p in people)
        {
            if (p?.schedule == null) continue;
            foreach (var a in p.schedule)
                if (a != null && a.id == activityId) { owner = p; return a; }
        }
        return null;
    }

    private string RoomName(string roomId)
    {
        if (string.IsNullOrEmpty(roomId)) return "Out of the house";
        var room = OccupancyModel.FindRoom(Ctx.Level, roomId);
        return room != null ? OccupancyModel.RoomLabel(room) : "Room missing";
    }

    private static string Title(ActivityDef a)
        => string.IsNullOrEmpty(a.label) ? ActivityKind.Label(a.kind) : a.label;

    private string FirstRoom()
    {
        var rooms = Ctx.Level?.rooms;
        if (rooms == null) return null;
        foreach (var r in rooms) if (r != null && !string.IsNullOrEmpty(r.id)) return r.id;
        return null;
    }

    private string FirstRoomOfType(string roomType)
    {
        var rooms = Ctx.Level?.rooms;
        if (rooms == null) return null;
        foreach (var r in rooms) if (r != null && r.roomType == roomType) return r.id;
        return null;
    }
}
