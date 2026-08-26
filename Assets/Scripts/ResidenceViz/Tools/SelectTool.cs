using UnityEngine;
using UnityEngine.InputSystem;

// Pick an element and edit its properties.
//
// WHAT A SELECTION SAYS ABOUT ITSELF, in order: its name, the controls that change it, then one muted
// line of figures. It used to be the other way round: a click printed a wall's length and thickness,
// a room's area and the diameter of the largest turning circle that fits in it, before anything you
// could act on. That is an audit reading of the tool, and this is a design one: the numbers are still
// here and still exact, they have simply stopped being the headline.
//
// The exception is the step-free badge on a doorway, which stays a badge. It is a verdict, not a
// figure, and it is the one thing about an opening worth seeing without reading.
public class SelectTool : ResidenceToolBase
{
    public override string Id => "select";
    public override string DisplayName => "Select";

    public override string Hint =>
        "Click a wall, room, item or resident to inspect it. A wall lists the doors and "
        + "windows in it. Furniture gets handles: "
        + "drag to move, the ring to turn, the cube to resize. Arrow keys nudge (Shift finer), "
        + "Z/X quarter-turn, F frames, Delete removes.";

    public override void HandleInput()
    {
        if (Ctx == null) return;

        // A click on a gizmo handle is a drag starting, not a new selection. Without this the same
        // click also raycasts, hits whatever is behind the handle, and swaps the selection out from
        // under the drag, which is exactly the moment you notice, because the item you grabbed stops
        // being the item that moves.
        bool gizmo = Ctx.Controller.GizmoBusy;

        if (LeftClicked() && !gizmo)
        {
            if (Ctx.PickElement(out ResidenceElementMarker marker, out _))
            {
                // An opening is a hole, not a thing you point at: clicking one selects the WALL that
                // hosts it, and that wall's rail lists its openings.
                //
                // REDIRECTING RATHER THAN IGNORING IS LOAD-BEARING. PickElement returns the FIRST
                // marker along the ray and stops, and ResidenceRenderer builds the opening handle proud of
                // the wall on both faces precisely so it wins that race, so a version that skipped
                // opening hits would select nothing at all, and clicking a doorway would read as the
                // tool being broken. The handle already carries its host wall in parentId.
                bool opening = marker.kind == ResidenceElementMarker.Kind.Opening;

                if (opening && string.IsNullOrEmpty(marker.parentId))
                {
                    Ctx.Controller.ClearSelection();   // an orphaned handle points at no wall
                }
                else
                {
                    Ctx.Controller.SelectedKind = opening ? ResidenceElementMarker.Kind.Wall : marker.kind;
                    Ctx.Controller.SelectedId = opening ? marker.parentId : marker.id;
                }
            }
            else Ctx.Controller.ClearSelection();
        }

        if ((KeyDown(Key.Delete) || KeyDown(Key.Backspace)) && !Ctx.IsLocked
            && !ResidenceEditController.TypingInUI
            && !IsRoomKind(Ctx.Controller.SelectedKind)) DeleteSelected();

        if (!Ctx.IsLocked && !gizmo) HandleTransformKeys();
        if (!Ctx.IsLocked) DragWallMount();
    }

    // ---------------------------------------------------------------------------------------
    // Keyboard transforms
    // ---------------------------------------------------------------------------------------

    // Arrows nudge, Z/X quarter-turn. Z and X rather than the more obvious Q/E for the same reason
    // the Furniture tool uses them for its ghost: Q/E raise and lower the overview camera, and camera
    // input is not gated on the pointer being off the rails, so both would fire at once. Using the
    // same pair before and after placing means one gesture, not two.
    private const float NUDGE = 0.05f;
    private const float NUDGE_FINE = 0.01f;

    private void HandleTransformKeys()
    {
        // Arrows and Z/X are letters while a rail field has the caret. See ResidenceEditController.TypingInUI.
        if (ResidenceEditController.TypingInUI) return;

        var item = FindFurniture(Ctx.Controller.SelectedId);
        if (item == null || Ctx.Controller.SelectedKind != ResidenceElementMarker.Kind.Furniture) return;
        if (item.position == null || item.position.Length < 3) return;

        float step = Ctx.ShiftHeld ? NUDGE_FINE : NUDGE;
        Vector2 d = Vector2.zero;
        if (KeyDown(Key.UpArrow)) d.y += step;
        if (KeyDown(Key.DownArrow)) d.y -= step;
        if (KeyDown(Key.LeftArrow)) d.x -= step;
        if (KeyDown(Key.RightArrow)) d.x += step;

        bool turnLeft = KeyDown(Key.Z);
        bool turnRight = KeyDown(Key.X);

        if (d == Vector2.zero && !turnLeft && !turnRight) return;

        Ctx.RecordEdit(d != Vector2.zero ? "Nudge furniture" : "Rotate furniture");
        item.position[0] += d.x;
        item.position[2] += d.y;
        if (turnLeft) item.rotationY = Mathf.Repeat(item.rotationY - 90f, 360f);
        if (turnRight) item.rotationY = Mathf.Repeat(item.rotationY + 90f, 360f);

        Ctx.Controller.CommitFurnitureEdit(item);
    }

    // ---------------------------------------------------------------------------------------
    // Dragging a wall-mounted item
    // ---------------------------------------------------------------------------------------

    // A mount is parameterised by (wall, offset along it, which face, height), so a free 3-D gizmo is
    // the wrong control for it. There is no direction it can travel that is not along a wall. Instead
    // the drag re-hosts it onto whatever wall the cursor is nearest, using the same answer the
    // Furniture tool uses when placing one, so re-hosting and placing can never disagree about which
    // side of a wall the cursor is on.
    //
    // Three things this has to get right, and the first version got none of them:
    //
    //  - A PRESS IS NOT A DRAG. wasPressedThisFrame and isPressed are both true on the press frame, so
    //    arming and moving in one pass meant every plain CLICK rewrote wallId, side and offset. The
    //    press only arms; nothing is written until the cursor has actually travelled.
    //  - THE CURSOR'S FLOOR PROJECTION IS NOT WHERE THE ITEM IS. ResidenceRenderer draws a mount at
    //    mountHeight above the floor and pushed out to the wall face; PickElement hits it there while
    //    GroundPoint intersects the floor. Under the perspective overview camera those points are
    //    ~0.8 m apart for a grab bar and ~1.4 m for a wall cabinet. Inside MOUNT_REACH of a DIFFERENT
    //    wall in a small bathroom, and nearly always across this wall's centerline, which is what
    //    NearestWall reads the face from. So the drag carries the press-time difference and moves the
    //    item BY the cursor's travel rather than TO its shadow. (Looking straight down there is no
    //    parallax at all, which is why this only ever bit at an angle.)
    //  - A RELEASE OVER A RAIL NEVER ARRIVES. HandleInput is not called while PointerOverUI, so a flag
    //    cleared only by LeftReleased() stays set. The button being up is the end of the gesture.
    private string _dragMountId;      // which mount the press landed on; null when idle
    private Vector2 _pressScreen;
    private Vector2 _grabOffset;      // the item's own plan position minus the press's ground point
    private bool _dragging;           // travelled far enough to be a drag rather than a click

    // Squared, in pixels. Small enough that a deliberate drag feels immediate, large enough that the
    // shake in a click never crosses it.
    private const float DRAG_START_SQ = 16f;

    private void DragWallMount()
    {
        if (Ctx.Controller.SelectedKind != ResidenceElementMarker.Kind.WallMount) { EndMountDrag(); return; }

        var m = FindWallMount(Ctx.Controller.SelectedId);
        if (m == null) { EndMountDrag(); return; }

        if (!LeftHeld()) { EndMountDrag(); return; }

        // Only arms when the press lands on this mount, so a click anywhere else still selects.
        if (LeftClicked())
        {
            if (!Ctx.PickElement(out ResidenceElementMarker hit, out _)
                || hit.kind != ResidenceElementMarker.Kind.WallMount || hit.id != m.instanceId) return;
            if (!Ctx.GroundPoint(out Vector2 press)) return;

            _dragMountId = m.instanceId;
            _pressScreen = Ctx.MousePosition;
            // A mount whose wall is gone has no position to anchor to, so it falls back to following
            // the cursor outright, which is the one case where re-hosting it anywhere is an improvement.
            _grabOffset = MountPoint(m, out bool anchored) - press;
            if (!anchored) _grabOffset = Vector2.zero;
            _dragging = false;
            return;
        }

        if (_dragMountId != m.instanceId) return;

        if (!_dragging && (Ctx.MousePosition - _pressScreen).sqrMagnitude < DRAG_START_SQ) return;
        _dragging = true;

        if (!Ctx.GroundPoint(out Vector2 at)) return;

        var wall = ResidenceMetrics.NearestWall(at + _grabOffset, Ctx.Level.walls, ResidenceConventions.MOUNT_REACH,
                                           out float offset, out int side);
        if (wall == null) return;

        Ctx.BeginGesture("Move wall-mounted item");
        m.wallId = wall.id;
        m.side = side;
        SetMountOffset(m, offset);

        // Re-pose the item, do not rebuild the residence: Ctx.Changed() means a full Rebuild(), which
        // respawns every GameObject in the plan (including the one being dragged) and allocates a
        // material per placeholder box, once a frame for the length of the drag. The furniture drag
        // goes through PoseFurnitureGO for exactly this reason; this is the mount's form of it.
        Ctx.Changed(false);
        Ctx.Renderer?.PoseWallMountGO(m);
    }

    // Closes the gesture, and rebuilds once if one actually happened: the drag itself only re-posed
    // the mounts, so anything else keyed off the level (the exterior, a ghost variant) catches up here.
    private void EndMountDrag()
    {
        if (_dragging) Ctx.Changed();
        _dragMountId = null;
        _dragging = false;
    }

    /// <summary>
    /// Where a mount actually sits in plan: along its wall, then out to the face it is mounted on.
    /// </summary>
    /// <remarks>
    /// The FACE, not the centerline. NearestWall reads the mounting side from which side of the
    /// centerline the point falls on, and its test is `>= 0`, so anchoring the drag to the centerline
    /// would feed it a point exactly on the line and flip every right-face mount to the left on the
    /// first drag frame. Built from WallMeshBuilder.BuildFrame, which is the same call
    /// ResidenceRenderer.RenderWallMounts poses the item with: two copies would be two chances to disagree
    /// about where the item the user is holding actually is.
    /// </remarks>
    private Vector2 MountPoint(WallMountDef m, out bool anchored)
    {
        anchored = false;
        var host = FindWall(m.wallId);
        if (host == null) return Vector2.zero;

        anchored = true;

        var frame = WallMeshBuilder.BuildFrame(host, Ctx.Level);
        Vector3 outward = m.side == WallSide.Left ? frame.left : -frame.left;
        Vector3 p = frame.origin
                    + frame.forward * m.offset
                    + outward * (0.5f * frame.thickness + Mathf.Max(0.001f, m.decorSurfaceOffset));
        return new Vector2(p.x, p.z);
    }

    // What a selection is called comes first and is the whole headline; its figures come last, as one
    // muted line above Delete.
    //
    // THE SUMMARY IS RETURNED, NOT DRAWN, and that is not tidiness. Four of these six methods return
    // early when the variant is locked, and locked is the DEFAULT state of every residence in the library,
    // a line emitted inside them would be invisible in the common case.
    public override void DrawRail()
    {
        if (Ctx?.Level == null) return;

        string id = Ctx.Controller.SelectedId;
        // Nothing selected: the tab's own tooltip says what to do, and an empty rail is an honest
        // report that there is nothing to inspect.
        if (string.IsNullOrEmpty(id)) return;

        // The inspector still draws on a locked baseline (it is how you read the base environment),
        // but it draws with its controls missing, and nothing said why. The same badge every other
        // rail shows, at the top, once.
        if (Ctx.IsLocked)
            UITheme.LockBadge("Read-only",
                "The base environment is locked, so it is read-only. Press Modify base "
                + "environment above to edit it, or work in a proposal.");

        (string line, string tip) summary = default;

        switch (Ctx.Controller.SelectedKind)
        {
            case ResidenceElementMarker.Kind.Wall: summary = DrawWall(id); break;
            case ResidenceElementMarker.Kind.Opening: summary = DrawOpening(id); break;
            case ResidenceElementMarker.Kind.Room:
            case ResidenceElementMarker.Kind.Floor:
            case ResidenceElementMarker.Kind.Ceiling: summary = DrawRoom(id); break;
            case ResidenceElementMarker.Kind.Furniture: summary = DrawFurniture(id); break;
            case ResidenceElementMarker.Kind.WallMount: summary = DrawWallMount(id); break;
            case ResidenceElementMarker.Kind.Sensor: summary = DrawSensor(id); break;
            case ResidenceElementMarker.Kind.Occupant: DrawOccupant(id); break;
        }

        if (!string.IsNullOrEmpty(summary.line))
        {
            // The divider carries its own air; a Gap on top of it was two gaps nobody added up.
            UITheme.Divider();
            UITheme.MutedLine(summary.line, summary.tip);
        }

        UITheme.Gap();
        // The Tip has to sit INSIDE the guard: it hangs off GetLastRect(), so when the button is not
        // drawn it attaches to whatever the inspector drew last.
        if (Ctx.IsLocked) return;
        // A room follows its walls and cannot be deleted on its own. DrawRoom says so where this
        // button would have been.
        if (IsRoomKind(Ctx.Controller.SelectedKind)) return;
        if (UITheme.DangerButton("Delete")) DeleteSelected();
        UITheme.Tip("Remove this from the plan  (Delete)");
    }

    // ---------------------------------------------------------------------------------------

    private (string, string) DrawWall(string id)
    {
        var w = FindWall(id);
        if (w == null) return default;

        UITheme.Header("Wall");

        float t = WallLayout.EffectiveThickness(w, Ctx.Level);
        float h = WallLayout.EffectiveHeight(w, Ctx.Level);
        var summary = ($"{Units.Format(ResidenceMetrics.WallLength(w))} long · "
                       + $"{Units.Format(t)} thick · {Units.Format(h)} high",
                       "Length, thickness and height of this wall");

        if (Ctx.IsLocked)
        {
            // Browsable on a LOCKED baseline too, which is the default state of every residence in the
            // library: reading what doors a wall has is inspecting, not editing, and this list is now
            // the only route to an opening at all.
            DrawOpeningList(w);
            return summary;
        }

        UITheme.Gap();
        float nt = MeasureUI.Length("Thickness", "Wall thickness", t, 0.012f,
                                    ResidenceConventions.MIN_WALL_THICKNESS, ResidenceConventions.MAX_WALL_THICKNESS);
        float nh = MeasureUI.Length("Height", "Wall height", h, 0.05f,
                                    ResidenceConventions.MIN_WALL_HEIGHT, ResidenceConventions.MAX_WALL_HEIGHT);

        if (!Mathf.Approximately(nt, t) || !Mathf.Approximately(nh, h))
        {
            Ctx.RecordEdit("Edit wall");
            w.thickness = nt;
            w.height = nh;
            Ctx.Changed();
        }

        DrawOpeningList(w);
        return summary;
    }

    // Openings are reached THROUGH their wall, because an opening is not a thing you can point at,
    // it is the absence of one. This list is what replaced clicking the hole: it used to print a bare
    // count, which said a wall had three doors and gave you no way to reach any of them.
    private bool _showOpenings = true;

    private void DrawOpeningList(WallDef w)
    {
        // Already sorted ascending by centerline offset, which is the order you would read them off
        // the wall itself.
        var list = WallLayout.OpeningsFor(w, Ctx.Level);
        if (list.Count == 0) return;

        _showOpenings = UITheme.Foldout(_showOpenings, $"Openings ({list.Count})");
        UITheme.Tip("The openings in this wall: doors and windows. Click one to inspect and resize it.");
        if (!_showOpenings) return;

        foreach (var o in list)
        {
            if (o == null) continue;

            bool selected = Ctx.Controller.SelectedKind == ResidenceElementMarker.Kind.Opening
                            && Ctx.Controller.SelectedId == o.id;

            if (UITheme.StateRow(SensorPose.OpeningLabel(o, Ctx.Level), Units.Format(o.width), selected))
                Ctx.Controller.Select(ResidenceElementMarker.Kind.Opening, o.id, reveal: false);

            // Hovering a row lights that opening up in the plan, which is what answers "which one",
            // the same job SelectionOverlay does for a selection. Repaint only: GetLastRect returns a
            // dummy rect during the Layout pass, the constraint UITheme.Tip documents. The rails are
            // drawn before the selection overlay in the same OnGUI pass, so this lands on the very
            // frame the cursor arrives rather than one behind it.
            if (Event.current.type == EventType.Repaint
                && GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                Ctx.Controller.HoverOpeningId = o.id;
        }
    }

    private (string, string) DrawOpening(string id)
    {
        var o = FindOpening(id);
        if (o == null) return default;

        UITheme.Header(Pretty(o.kind));

        // The step-free badge stays a badge rather than folding into the summary line: it is a verdict,
        // not a figure, and it is the one thing about a doorway worth seeing without reading.
        bool step = ResidenceMetrics.HasThreshold(o);
        UITheme.StatusBadge(step ? "Has a threshold" : "Step-free", !step);
        UITheme.Tip(step
            ? "This doorway has a raised threshold to cross"
            : "Nothing to cross at this doorway");

        // The CLEAR width leads the summary, not the rough opening: that is the dimension a wheelchair
        // has to pass through and the one an accessibility rule would test.
        string sill = o.sillHeight > 0f ? $" · sill {Units.Format(o.sillHeight)}" : "";
        string thresh = step ? $" · threshold {Units.Format(o.thresholdHeight)}" : "";
        var summary = ($"{Units.Format(ResidenceMetrics.ClearWidth(o))} clear · "
                       + $"{Units.Format(o.width)} × {Units.Format(o.height)} rough{sill}{thresh}",
                       ResidenceMetrics.IsClearWidthMeasured(o)
                           ? "Clear width measured on site, then the rough opening it sits in."
                           : "Clear width estimated from the rough opening, then that opening.");

        var wall = FindWall(o.wallId);

        // The list stays on screen while one of its rows is being edited. Selecting an opening
        // replaces the wall's rail with this one, so without this the list you just clicked would
        // vanish underneath you, and flicking between the two doors in a wall would mean reselecting
        // the wall each time. The row for this opening draws as the active one.
        if (Ctx.IsLocked || wall == null)
        {
            if (wall != null) DrawOpeningList(wall);
            return summary;
        }

        float wallH = WallLayout.EffectiveHeight(wall, Ctx.Level);
        float wallLen = WallLayout.WallLength(wall);

        UITheme.Gap();

        // EVERY DIMENSION IS BOUNDED BY WHAT THE WALL WILL ACTUALLY TAKE, rather than by a generous
        // constant with a refusal behind it. A drag-scrubbed field whose value climbs while the
        // document declines to follow is the control and the model disagreeing silently, so the max
        // is the real limit, and the SetX guards below stay as the backstop rather than the interface.
        float maxW = Mathf.Max(ResidenceConventions.MIN_OPENING_WIDTH, OpeningFit.MaxWidth(o, wall, Ctx.Level));
        float nw = MeasureUI.Length("Width", "The rough opening. Drag it, or type a measured width.",
                                    o.width, 0.0127f, ResidenceConventions.MIN_OPENING_WIDTH, maxW);
        if (!Mathf.Approximately(nw, o.width)) SetWidth(o, nw);

        // Height and sill could not be changed at ALL once an opening was placed: the tool offered
        // both, the inspector offered neither, so a window's sill was frozen at whatever it was drawn
        // with. Both go through OpeningFit.FitVertical, which existed and was called by nobody.
        float maxH = Mathf.Max(ResidenceConventions.MIN_OPENING_HEIGHT, wallH - o.sillHeight);
        float nh = MeasureUI.Length("Height", "Opening height", o.height, 0.025f,
                                    ResidenceConventions.MIN_OPENING_HEIGHT, maxH);
        if (!Mathf.Approximately(nh, o.height)) SetVertical(o, o.sillHeight, nh, wallH);

        if (o.kind == OpeningKind.Window)
        {
            float maxS = Mathf.Max(0f, wallH - o.height);
            float ns = MeasureUI.Length("Sill", "Sill height above the floor", o.sillHeight, 0.025f, 0f, maxS);
            if (!Mathf.Approximately(ns, o.sillHeight)) SetVertical(o, ns, o.height, wallH);
        }

        // Position along the wall. An opening has no drag gesture anywhere, and now that clicking one
        // in the plan selects its wall instead, this is the only way to nudge a door that landed a few
        // inches off where it should be.
        float no = MeasureUI.Length("Position", "Position along the wall, from its start",
                                    o.offset, 0.01f, 0f, Mathf.Max(0f, wallLen));
        if (!Mathf.Approximately(no, o.offset)) SetOffset(o, no);

        UITheme.Gap();
        float nt = MeasureUI.Length("Threshold", "Threshold height", o.thresholdHeight, 0.003f,
                                    0f, ResidenceConventions.MAX_THRESHOLD);
        if (!Mathf.Approximately(nt, o.thresholdHeight))
        {
            Ctx.BeginGesture("Edit threshold");
            o.thresholdHeight = Mathf.Max(0f, nt);
            Ctx.Changed();
        }
        if (o.thresholdHeight > 0f && UITheme.SecondaryButton("Make step-free"))
        {
            Ctx.RecordEdit("Remove threshold");
            o.thresholdHeight = 0f;
            Ctx.Changed();
        }

        DrawOpeningList(wall);
        return summary;
    }

    private void SetWidth(OpeningDef o, float meters)
    {
        var wall = FindWall(o.wallId);
        if (wall == null) return;

        // Widening can push an opening past a corner or into its neighbor, so re-fit rather than
        // letting the wall silently develop an impossible hole.
        var fit = OpeningFit.Fit(o.offset, meters, WallLayout.WallLength(wall),
                                 WallLayout.OpeningsFor(wall, Ctx.Level), o.id);
        if (!fit.ok) { Ctx.Controller.Status(fit.reason); return; }

        // BeginGesture, not RecordEdit: these are drag-scrubbed fields, so a RecordEdit would push an
        // undo entry every frame the cursor moved and bury the rest of the history under one drag.
        // Idempotent until the central EndGesture that ResidenceEditController.Update runs on every
        // left-button release: the same arrangement SetMountOffset uses.
        Ctx.BeginGesture("Resize opening");
        o.width = meters;
        o.offset = fit.offset;
        o.clearWidth = 0f;   // the measured value no longer applies to a resized opening
        Ctx.Changed();
    }

    // Sill and height are driven from two separate fields but are one constraint: an opening must fit
    // between the floor and the wall top, so both go through here. Setting them independently is how
    // you drive the pair into an illegal state from opposite directions: raise the sill to the ceiling,
    // then wonder where the window went.
    private void SetVertical(OpeningDef o, float sill, float height, float wallHeight)
    {
        OpeningFit.FitVertical(sill, height, wallHeight,
                               out float fittedSill, out float fittedHeight);

        if (Mathf.Approximately(fittedSill, o.sillHeight)
            && Mathf.Approximately(fittedHeight, o.height)) return;

        Ctx.BeginGesture("Resize opening");
        o.sillHeight = fittedSill;
        o.height = fittedHeight;
        Ctx.Changed();
    }

    // Slides rather than refuses, which is OpeningFit's whole convention: a door pushed toward a corner
    // stops against the corner instead of snapping back to where it was.
    private void SetOffset(OpeningDef o, float meters)
    {
        var wall = FindWall(o.wallId);
        if (wall == null) return;

        var fit = OpeningFit.Fit(o, wall, Ctx.Level, meters);
        if (!fit.ok) { Ctx.Controller.Status(fit.reason); return; }

        if (Mathf.Approximately(fit.offset, o.offset)) return;

        Ctx.BeginGesture("Move opening");
        o.offset = fit.offset;
        Ctx.Changed();
    }

    // No turning circle here any more. It is still computed. OccupancyModel stands people with it and
    // the walkthrough spawns with it, but the Measure tool is the one place it is now REPORTED, so
    // asking for it is a deliberate act rather than something every room click prints at you.
    private (string, string) DrawRoom(string id)
    {
        var r = FindRoom(id);
        if (r == null) return default;

        UITheme.Header(string.IsNullOrEmpty(r.name) ? Pretty(r.roomType) : r.name);
        var summary = (Units.FormatArea(ResidenceMetrics.RoomArea(r)), "Floor area of this room");

        if (Ctx.IsLocked) return summary;

        UITheme.Gap();
        string newName = UITheme.TextRow("Name", r.name,
            "What this room is called. Clear it to go back to a name from the type.");
        if (newName != r.name)
        {
            Ctx.RecordEdit("Rename room");
            r.name = newName;
            Ctx.Changed(false);
        }

        // The same type chips the Rooms tool offers, so clicking a floor here does not mean changing
        // tabs to fix what it is.
        UITheme.Gap();
        var chips = UITheme.ChipRow();
        chips.Label("Type");
        foreach (string t in RoomFinish.All)
        {
            if (chips.Chip(RoomRegions.Pretty(t), r.roomType == t)) SetRoomType(r, t);
            UITheme.Tip($"Make this a {RoomRegions.Pretty(t).ToLowerInvariant()}.");
        }
        chips.End();

        // Where Delete used to be. A room is not a thing you can delete on its own: the walls still
        // enclose the area, so the next Sync would put it straight back with a fresh id. Losing the
        // name and the type, and reporting "room removed, room added" in every open comparison.
        UITheme.Gap();
        UITheme.Glyph("⌂", "A room is the area your walls close off. Remove a wall to remove the room.",
                      UITheme.Ink3);
        return summary;
    }

    private void SetRoomType(RoomDef room, string type)
    {
        if (room.roomType == type) return;
        Ctx.RecordEdit("Set room type");
        bool rename = RoomRegions.IsAutoName(room.name, room.roomType);
        room.roomType = type;
        if (rename) room.name = RoomRegions.AutoName(Ctx.Level, type);
        Ctx.Changed(false);
        Ctx.Renderer?.RebuildRooms();
    }

    private static readonly string[] TransformTools = { "Move", "Rotate", "Scale" };

    private static readonly string[] TransformTips =
    {
        "Drag the arrows or the center pad in the plan. Arrow keys nudge; hold Shift for finer steps.",
        "Drag the ring in the plan, or use Z and X for quarter turns. Hold Shift to snap to 15°.",
        "Drag the cube to resize proportionally, or a slider for one dimension. These are the item's "
            + "real dimensions: what gets drawn, and what clearances are measured against.",
    };

    private bool _lockAspect = true;

    private (string, string) DrawFurniture(string id)
    {
        var f = FindFurniture(id);
        if (f == null || f.position == null || f.position.Length < 3) return default;

        var ctrl = Ctx.Controller;
        var entry = Ctx.Renderer?.EntryFor(f.prefabType);
        UITheme.Header(entry != null ? entry.Label : Pretty(f.prefabType));

        Vector3 size = ctrl.FurnitureSize(f);
        var summary = ($"{Units.Format(size.x)} × {Units.Format(size.z)} · {Units.Format(size.y)} high",
                       "This item's true width, depth and height");

        // The room is a NAME, not a measurement, so it stays where it is.
        var room = ResidenceMetrics.RoomAt(new Vector2(f.position[0], f.position[2]), Ctx.Level);
        if (room != null)
            UITheme.Value("Room", string.IsNullOrEmpty(room.name) ? Pretty(room.roomType) : room.name,
                          "The room this item stands in");

        if (Ctx.IsLocked) return summary;

        // The same segmented Move / Rotate / Scale the Site inspector has, and it drives the scene
        // handles as well as the controls below it: one choice, not two.
        UITheme.Gap();
        ctrl.TransformMode = (TransformGizmo.Mode)UITheme.Segmented(
            "Handles", (int)ctrl.TransformMode, TransformTools, TransformTips);

        UITheme.Gap();
        switch (ctrl.TransformMode)
        {
            // Move has no rail controls: the gizmo in the plan IS the mover, and the segmented control
            // above carries the gesture in its own tooltip. Typing a raw X/Z coordinate was never how
            // anyone positioned a chair.
            case TransformGizmo.Mode.Rotate: DrawRotateControls(f); break;
            case TransformGizmo.Mode.Scale:  DrawResizeControls(f, entry, size); break;
        }
        return summary;
    }

    private void DrawRotateControls(ObjectInstance f)
    {
        float rot = MeasureUI.Angle("Facing",
                                    "Which way this faces. Drag the box, or type an exact bearing.",
                                    f.rotationY);
        if (!Mathf.Approximately(rot, f.rotationY))
        {
            Ctx.BeginGesture("Rotate furniture");
            f.rotationY = rot;
            Ctx.Renderer?.PoseFurnitureGO(f);
            // The re-fit and the rebuild wait for the mouse-up, so a slider drag is one undo entry
            // and does not fight the thumb by sliding the item aside mid-gesture.
            Ctx.Controller.NoteFurnitureEdit();
        }

        GUILayout.BeginHorizontal();
        bool left = UITheme.SecondaryButton("↺ 90°");
        UITheme.Tip("Turn a quarter turn anticlockwise  (Z)");
        bool right = UITheme.SecondaryButton("↻ 90°");
        UITheme.Tip("Turn a quarter turn clockwise  (X)");
        GUILayout.EndHorizontal();
        if (!left && !right) return;

        Ctx.RecordEdit("Rotate furniture");
        f.rotationY = Mathf.Repeat(f.rotationY + (left ? -90f : 90f), 360f);
        Ctx.Controller.CommitFurnitureEdit(f);
    }

    // Resizing writes the item's TRUE dimensions, not a multiplier. boxSizeMeters is what
    // ResidenceRenderer draws, what FurnitureFit tests against a doorway and what the occupancy checks
    // stand people clear of, so a resized item stays honest in every number the app reports, which a
    // free 0.1-5x scale factor, the way the Site tool scales a tree, would not.
    private void DrawResizeControls(ObjectInstance f, FurnitureCatalog.Entry entry, Vector3 size)
    {
        Vector3 nominal = entry != null ? entry.SizeMeters : size;
        Vector3 next = size;
        bool changed = false;

        // Three boxes that were identical on screen until they carried their names: this is the one
        // place in the app where a missing label was not a nuisance but an outright guess.
        next.x = DimensionSlider("Width", "The item's true side-to-side size", size.x, nominal.x, ref changed);
        next.z = DimensionSlider("Depth", "The item's true front-to-back size", size.z, nominal.z, ref changed);
        next.y = DimensionSlider("Height", "What a sill or counter is compared against", size.y, nominal.y, ref changed);

        _lockAspect = UITheme.Toggle("Lock aspect", _lockAspect,
            "Keep the proportions: a bed widened by a fifth also gets a fifth longer, instead "
            + "of turning into a different bed.");

        if (changed)
        {
            // With aspect locked, whichever dimension moved sets the factor for all three, so a bed
            // widened by a fifth also gets a fifth longer instead of turning into a different bed.
            if (_lockAspect) next = size * AspectFactor(size, next);

            Ctx.BeginGesture("Resize furniture");
            Ctx.Controller.SetFurnitureSize(f, next);
            Ctx.Renderer?.PoseFurnitureGO(f);
            Ctx.Controller.NoteFurnitureEdit();
        }

        UITheme.Gap();

        // Always one click back to the truth. Without it a resize is a one-way door, and the whole
        // reason resizing is expressed in real units is that these numbers are load-bearing.
        bool reset = entry != null && UITheme.SecondaryButton("Reset to catalog size");
        if (entry != null)
            UITheme.Tip($"Back to this item's real dimensions: "
                        + $"{Units.Format(entry.SizeMeters.x)} × {Units.Format(entry.SizeMeters.z)}");
        if (reset)
        {
            Ctx.RecordEdit("Reset size");
            Ctx.Controller.SetFurnitureSize(f, entry.SizeMeters);
            Ctx.Controller.CommitFurnitureEdit(f);
        }
    }

    // The slider spans half to double the catalog dimension: wide enough for the real cases (a built-in
    // counter run, an unusually narrow bed) and narrow enough that the thumb still resolves inches.
    //
    // The clamp is DISPLAY ONLY, and that is the whole subtlety. An item sized past the range: by a
    // proportional drag on the gizmo cube, which has no range. Must not be silently pulled back inside
    // it just because someone opened this tab. So an untouched slider returns the value it was given,
    // not the value it drew.
    // The quantum a resize snaps to. A fixed step cannot serve both a 0.04 m grab bar and a 2.13 m
    // hospital bed: 25 mm is a fifth of the bar and invisible on the bed. So it scales with the item,
    // rounded to a millimetre and floored there. Finer than that is below what Units will print.
    private static float StepFor(float nominal)
        => Mathf.Max(0.001f, Mathf.Round(nominal * 0.02f * 1000f) / 1000f);

    private float DimensionSlider(string label, string tooltip, float meters, float nominal,
                                  ref bool changed)
    {
        float lo = Mathf.Max(ResidenceEditController.MIN_ITEM_SIZE, 0.5f * nominal);
        float hi = Mathf.Max(lo + 0.05f, 2f * nominal);

        float shown = Mathf.Clamp(meters, lo, hi);
        float moved = MeasureUI.Length(label, tooltip, shown, StepFor(nominal), lo, hi);

        if (Mathf.Approximately(moved, shown)) return meters;

        changed = true;
        return moved;
    }

    private static float AspectFactor(Vector3 from, Vector3 to)
    {
        if (!Mathf.Approximately(from.x, to.x) && from.x > 0f) return to.x / from.x;
        if (!Mathf.Approximately(from.z, to.z) && from.z > 0f) return to.z / from.z;
        if (!Mathf.Approximately(from.y, to.y) && from.y > 0f) return to.y / from.y;
        return 1f;
    }

    // ---------------------------------------------------------------------------------------

    private (string, string) DrawWallMount(string id)
    {
        var m = FindWallMount(id);
        if (m == null) return default;

        var entry = Ctx.Renderer?.EntryFor(m.prefabType);
        UITheme.Header(entry != null ? entry.Label : Pretty(m.prefabType));

        var host = FindWall(m.wallId);
        var summary = ($"{Units.Format(m.mountHeight)} up · {Units.Format(m.offset)} along",
                       "Height above the finished floor, and how far along its wall this sits. Drag it "
                       + "in the plan to slide it, flip its face, or move it to another wall.");

        if (Ctx.IsLocked) return summary;

        UITheme.Gap();
        float h = MeasureUI.Length("Height", "Height above the finished floor", m.mountHeight, 0.025f, 0f, 3f);
        if (!Mathf.Approximately(h, m.mountHeight))
        {
            Ctx.RecordEdit("Move wall-mounted item");
            m.mountHeight = Mathf.Max(0f, h);
            Ctx.Changed();
        }

        if (host == null) return summary;

        // Along the wall and which face: the two things about a mount that were unreachable once it
        // was placed. Everything goes through FurnitureFit.FitMount, which keeps it on its own wall
        // and clear of any opening its vertical span reaches, exactly as placing one does.
        float len = ResidenceMetrics.WallLength(host);
        float along = MeasureUI.Length("Along wall", "How far along the wall this sits",
                                       Mathf.Clamp(m.offset, 0f, len), 0.01f, 0f, len);
        if (!Mathf.Approximately(along, m.offset))
        {
            Ctx.BeginGesture("Move wall-mounted item");
            SetMountOffset(m, along);
            Ctx.Changed();
        }

        GUILayout.BeginHorizontal();
        if (UITheme.Chip("Left face", m.side == WallSide.Left)) SetSide(m, WallSide.Left);
        UITheme.Tip("Mount on the left-hand face of the wall");
        if (UITheme.Chip("Right face", m.side == WallSide.Right)) SetSide(m, WallSide.Right);
        UITheme.Tip("Mount on the right-hand face of the wall");
        GUILayout.EndHorizontal();
        return summary;
    }

    // A smart home device gets no gizmo, for the reason a wall mount gets none: it is parameterised by
    // the element it watches, so there is no direction it can travel that is not "onto a different
    // host". What it does get is the two settings a care team actually changes after installation,
    // whether it reports to staff at all, and how long a condition has to hold before it does.
    private (string, string) DrawSensor(string id)
    {
        var s = FindSensor(id);
        if (s == null) return default;

        var entry = Ctx.Renderer?.Sensors?.Get(s.deviceType);
        var device = SensorDevices.Get(s.deviceType);
        UITheme.Header(entry != null ? entry.Label : SensorDevices.LabelOf(s));

        var pose = SensorPose.Resolve(s, Ctx.Level, Ctx.Variant);
        float radius = SensorDevices.RadiusOf(s);

        // An everyday aid is a device record with no envelope and no rules, and three of the controls
        // below assume it has both. "Watches the resident it is on" is nonsense about a sock aid; a
        // Reports-to-staff toggle offers a choice that changes nothing, because nothing routes; and
        // DrawSensorRules draws an empty section under a heading. So the rail says what is true of it
        // and stops: the same call SensorTool's rail makes, from the same question.
        bool senses = radius > 0f || SensorDevices.DefaultRules(s.deviceType).Count > 0
                                  || (s.rules != null && s.rules.Count > 0);

        string where = pose.hostLabel ?? SensorHost.Label(s.hostKind);
        string price = SensorCost.Money(device.purchaseLow) + " - " + SensorCost.Money(device.purchaseHigh);

        string reach = radius > 0f
            ? Units.Format(radius) + (SensorDevices.AngleOf(s) < 360f
                                          ? $" · {SensorDevices.AngleOf(s):0}°" : " all round")
            : "the " + SensorHost.Label(s.hostKind) + " it is on";

        var summary = (senses ? $"Range {reach} · {price}" : $"{where} · {price}",
                       (entry?.detects ?? "") + "\n\n" + (entry?.iddRationale ?? "")
                       + "\n\n" + (senses ? "Installed on " : "Belongs to ") + where + ".");

        // A verdict rather than a figure, so it stays a badge: the same reasoning that keeps a
        // doorway's step-free badge out of the muted summary line. Who can see what is the single most
        // consequential thing about a device in a residence someone lives in, and "Not connected" is as
        // much an answer to that as "Sees" is.
        UITheme.StatusBadge(SensorPrivacy.Label(SensorDevices.PrivacyOf(s)),
                            SensorPrivacy.Rank(SensorDevices.PrivacyOf(s)) <= 1);
        UITheme.Tip("§5.5: what this can and cannot notice. The console's Family and Resident roles "
                  + "filter on exactly this.");

        if (Ctx.IsLocked || !senses) return summary;

        UITheme.Gap();

        bool monitored = UITheme.Toggle("Reports to staff", s.monitored,
            "Off, it still senses and still prompts in the residence. It simply raises nothing "
            + "with the caregiver. §5.5 makes that a decision per device.");
        if (monitored != s.monitored)
        {
            Ctx.RecordEdit("Change what this device reports");
            s.monitored = monitored;
            Ctx.Renderer?.InvalidateSensorDay();
            Ctx.Changed(rebuildAll: false);
        }

        DrawSensorRules(s);
        return summary;
    }

    // The report's thresholds, as controls. §4.4.2 calls its own 30-60 minutes "a customizable
    // threshold", and a residence whose stove alert keeps crying wolf needs to move it without moving it
    // for every other residence, which is exactly what SensorDef.rules is and what these write into.
    private void DrawSensorRules(SensorDef s)
    {
        var rules = SensorDevices.EffectiveRules(s);
        if (rules.Count == 0) return;

        UITheme.Header("Alerts");

        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (rule == null) continue;

            UITheme.Value(SensorAlertKind.Title(rule.kind), SensorAlertKind.SuggestedResponse(rule.kind));

            if (rule.thresholdMinutes <= 0) continue;

            int minutes = Mathf.RoundToInt(
                MeasureUI.Number("Alert after", "How long before this raises an alert",
                                 rule.thresholdMinutes, 1f, 1f, 120f, "0", " min"));
            if (minutes == rule.thresholdMinutes) continue;

            // Copy-on-write: EffectiveRules hands back the CATALOG's defaults when the device carries
            // none, and writing into those would move the threshold on every device of this type in
            // every residence. The first edit is what gives this device rules of its own.
            Ctx.RecordEdit("Change an alert threshold");
            if (s.rules == null || s.rules.Count == 0)
            {
                s.rules = new System.Collections.Generic.List<SensorRuleDef>();
                foreach (var r in rules) s.rules.Add(r.Copy());
                rule = s.rules[i];
            }
            rule.thresholdMinutes = minutes;

            Ctx.Renderer?.InvalidateSensorDay();
            Ctx.Changed(rebuildAll: false);
            return;   // the list was just replaced; next frame draws the copy
        }
    }

    private SensorDef FindSensor(string id) => ResidenceRenderer.FindSensor(id, Ctx.Level);

    private void SetSide(WallMountDef m, int side)
    {
        if (m.side == side) return;
        Ctx.RecordEdit("Turn wall-mounted item");
        m.side = side;
        Ctx.Changed();
    }

    // Writes an offset through the fit, so a grab bar can be neither pushed off the end of its wall
    // nor parked across a doorway: the same rule that guards placing one.
    private void SetMountOffset(WallMountDef m, float offset)
    {
        var wall = FindWall(m.wallId);
        if (wall == null) { m.offset = offset; return; }

        var entry = Ctx.Renderer?.EntryFor(m.prefabType);
        float width = entry != null ? entry.widthM : 0.4f;
        float height = entry != null ? entry.heightM : 0.05f;

        var fit = FurnitureFit.FitMount(offset, width,
                                        m.mountHeight - 0.5f * height,
                                        m.mountHeight + 0.5f * height,
                                        wall, Ctx.Level.openings);
        m.offset = fit.offset;
        if (!fit.ok) Ctx.Controller.Status(fit.reason);
    }

    // Read-only here on purpose: the day is edited in the People tool, where there is room for a
    // timeline. This is the "who is this and where are they right now" answer a click should give.
    private void DrawOccupant(string id)
    {
        var p = FindOccupant(id);
        if (p == null) return;

        UITheme.Header(string.IsNullOrEmpty(p.name) ? "Occupant" : p.name);

        var clock = Ctx.Renderer?.Occupancy;
        int now = clock?.Now ?? 0;
        var pose = OccupancyModel.PoseAt(p, now, Ctx.Level);

        UITheme.Value("Time", Clock.Format(now), "The time the plan is showing");
        UITheme.Value("Doing", OccupancyModel.Describe(pose), "What they are doing right now, and where");

        if (pose.activity != null)
            UITheme.Value("Runs", Clock.FormatRange(pose.activity.startMinutes, pose.activity.endMinutes),
                          "When the current block runs from and to");

        if (p.usesWheelchair)
        {
            UITheme.StatusBadge("Uses a wheelchair", true);
            UITheme.Tip("Shown seated in the plan, at the same eye height the walkthrough's Seated "
                        + "setting uses, and given a wheelchair-sized footprint to stand clear in.");
        }

        // The occupant's own note: what the user typed, not a caption. Left visible.
        if (!string.IsNullOrEmpty(p.note)) { UITheme.GapTight(); UITheme.Note(p.note); }

        int blocks = p.schedule?.Count ?? 0;
        UITheme.Value("Blocks", blocks.ToString(), "Activity blocks in this person's day");

        UITheme.Gap();
        if (UITheme.SecondaryButton("Edit this person's day"))
            Ctx.Controller.RequestStage(ResidenceStage.People);
        UITheme.Tip("Go to the People tab to change their schedule");
    }

    // ---------------------------------------------------------------------------------------

    private void DeleteSelected()
    {
        string id = Ctx.Controller.SelectedId;
        if (string.IsNullOrEmpty(id) || Ctx.Level == null) return;

        Ctx.RecordEdit("Delete");

        switch (Ctx.Controller.SelectedKind)
        {
            case ResidenceElementMarker.Kind.Wall:
                var w = FindWall(id);
                if (w != null)
                {
                    // An opening with no wall is unreachable and unrenderable, so it goes too, and a
                    // sensor whose host went with it is the same thing one level further down. The
                    // doomed openings are collected BEFORE they are removed, or their door sensors
                    // would be left pointing at ids nothing resolves. VariantRevert.RevertWall runs the
                    // identical cascade, and the two must not disagree.
                    var orphaned = new System.Collections.Generic.HashSet<string>();
                    foreach (var o in Ctx.Level.openings)
                        if (o != null && o.wallId == id) orphaned.Add(o.id);

                    Ctx.Level.openings.RemoveAll(o => o != null && o.wallId == id);
                    Ctx.Level.wallMounted.RemoveAll(m => m != null && m.wallId == id);
                    RemoveSensors(s => (s.hostKind == SensorHost.Wall && s.hostId == id)
                                    || (s.hostKind == SensorHost.Opening && orphaned.Contains(s.hostId)));
                    Ctx.Level.walls.Remove(w);

                    // The rooms follow the walls. Removing the wall between two bedrooms is how you
                    // merge them, and this is what makes that gesture mean that: in the same
                    // RecordEdit, so one undo takes the wall AND the merge back.
                    RoomRegions.Sync(Ctx.Level, null);
                }
                break;

            case ResidenceElementMarker.Kind.Opening:
                Ctx.Level.openings.RemoveAll(o => o != null && o.id == id);
                RemoveSensors(s => s.hostKind == SensorHost.Opening && s.hostId == id);
                break;

            case ResidenceElementMarker.Kind.Furniture:
                Ctx.Level.furniture.RemoveAll(f => f != null && f.instanceId == id);
                RemoveSensors(s => s.hostKind == SensorHost.Furniture && s.hostId == id);
                break;

            case ResidenceElementMarker.Kind.WallMount:
                Ctx.Level.wallMounted.RemoveAll(m => m != null && m.instanceId == id);
                break;

            case ResidenceElementMarker.Kind.Sensor:
                RemoveSensors(s => s.id == id);
                break;

            // Occupants hang off the variant, not the level: the one delete case that does not
            // reach into Ctx.Level.
            case ResidenceElementMarker.Kind.Occupant:
                Ctx.Variant?.occupants?.RemoveAll(p => p != null && p.id == id);
                // A pendant is worn. With nobody left to wear it, it would report against an id no
                // roster resolves: the same cascade VariantRevert.RevertOccupant runs.
                RemoveSensors(s => s.hostKind == SensorHost.Occupant && s.hostId == id);
                break;
        }

        Ctx.Controller.ClearSelection();
        Ctx.Renderer?.InvalidateSensorDay();
        Ctx.Changed();
    }

    private static bool IsRoomKind(ResidenceElementMarker.Kind k)
        => k == ResidenceElementMarker.Kind.Room
        || k == ResidenceElementMarker.Kind.Floor
        || k == ResidenceElementMarker.Kind.Ceiling;

    private void RemoveSensors(System.Predicate<SensorDef> match)
        => Ctx.Level.sensors?.RemoveAll(s => s != null && match(s));

    private WallDef FindWall(string id)
    {
        foreach (var w in Ctx.Level.walls) if (w != null && w.id == id) return w;
        return null;
    }

    private OpeningDef FindOpening(string id)
    {
        foreach (var o in Ctx.Level.openings) if (o != null && o.id == id) return o;
        return null;
    }

    private RoomDef FindRoom(string id)
    {
        foreach (var r in Ctx.Level.rooms) if (r != null && r.id == id) return r;
        return null;
    }

    private ObjectInstance FindFurniture(string id)
    {
        foreach (var f in Ctx.Level.furniture) if (f != null && f.instanceId == id) return f;
        return null;
    }

    private WallMountDef FindWallMount(string id)
    {
        foreach (var m in Ctx.Level.wallMounted) if (m != null && m.instanceId == id) return m;
        return null;
    }

    private OccupantDef FindOccupant(string id)
    {
        var people = Ctx.Variant?.occupants;
        if (people == null) return null;
        foreach (var p in people) if (p != null && p.id == id) return p;
        return null;
    }

    private static string Pretty(string token)
    {
        if (string.IsNullOrEmpty(token)) return "Item";
        string s = token.Replace('_', ' ');
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
