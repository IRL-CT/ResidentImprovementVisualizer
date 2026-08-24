using System.Collections.Generic;
using UnityEngine;

// Says what each area of the plan IS.
//
// This tool used to trace room polygons corner by corner. It does not any more: an area closed off by
// walls is a room the moment it closes, derived by RoomRegions and kept in step by RoomRegions.Sync on
// every wall edit. Tracing a floor over walls you have already drawn was drawing the same room twice,
// with nothing checking the two agreed.
//
// So what is left is the half that was never expressible: which room is which. The rail leads with
// the type selector, always on screen: arm a type, then every click in the plan makes that room the
// armed type: a whole floor is typed in one pass of clicks. The rooms themselves fold behind one
// dropdown header, and the picked room's name, type chips and area sit below it.
//
// It never runs the face finder in its per-frame path. After Sync, level.rooms IS the region list, so
// "which area did I click in" is ResidenceMetrics.RoomAt: one lookup that already exists and already
// screens out degenerate polygons. RoomRegions.Find is called only by "Detect rooms".
public class RoomTool : ResidenceToolBase
{
    public override string Id => "room";
    public override string DisplayName => "Rooms";

    public override string Hint =>
        "Walls that close off an area make a room by themselves. Arm a type, then click rooms to "
        + "paint them. Or pick a room and set it up in the rail.";

    // ALWAYS, unlike WallTool's mid-run-only rule. Floor/Room/Ceiling are already outside
    // TryAutoSelect's whitelist so a floor click was safe either way, but FURNITURE is in it, and
    // clicking a chair standing in a bedroom would select the chair and eject you to the Select tab.
    // In this tool every click in the plan means "this area".
    public override bool ClaimsClicks => true;

    private string _selectedId;
    private string _armedType;          // a RoomType id while the selector is armed; null = disarmed
    private bool _roomListOpen;         // the room dropdown
    private bool _pendingListToggle;    // deferred: opening/closing changes the rail's control count
    private bool _showTypes;            // the type chip row, folded behind the current type
    private bool _pendingDetect;        // deferred: it changes the rail's control count
    private readonly List<string> _warnings = new List<string>();

    public override void Exit()
    {
        _selectedId = null;
        _armedType = null;
        _roomListOpen = false;
        _pendingListToggle = false;
        _showTypes = false;
    }

    public override void HandleInput()
    {
        if (Ctx?.Level == null) return;
        if (!LeftClicked() || !Ctx.GroundPoint(out Vector2 xz)) return;

        var room = ResidenceMetrics.RoomAt(xz, Ctx.Level);
        if (room == null)
        {
            _selectedId = null;
            Ctx.Controller?.Status("That area is not closed off by walls yet.");
            return;
        }

        // With a type armed, a click in the plan ASSIGNS it, and keeps it armed, so a whole plan is
        // typed in one pass of clicks. The room is selected too: the rail follows the click.
        if (_armedType != null && !Ctx.IsLocked) SetType(room, _armedType);

        _selectedId = room.id;
        // reveal: false for CompareTool's reason. Clicking a row must not carry you out of the tab
        // you are working in. Selecting is what raises SelectionOverlay's outline over the room.
        Ctx.Controller?.Select(ResidenceElementMarker.Kind.Floor, room.id, reveal: false);
    }

    public override void Tick()
    {
        if (_pendingListToggle)
        {
            _pendingListToggle = false;
            _roomListOpen = !_roomListOpen;
        }

        if (!_pendingDetect) return;
        _pendingDetect = false;

        Ctx.RecordEdit("Detect rooms");
        _warnings.Clear();
        int n = RoomRegions.Sync(Ctx.Level, _warnings);
        Ctx.Controller?.Status(_warnings.Count > 0 ? _warnings[0]
                             : n == 0 ? "The rooms already match the walls."
                                      : $"{n} room{(n == 1 ? "" : "s")} updated from the walls.");
        Ctx.Changed();
    }

    public override void DrawRail()
    {
        if (RefuseIfLocked()) return;

        // The type selector leads, always on screen. Arming flips only row highlights: no control
        // count changes, so no deferral is needed; the pick-one active-row idiom, not a Toggle.
        foreach (string t in RoomFinish.All)
        {
            if (UITheme.StateRowLine(RoomRegions.Pretty(t), "", _armedType == t))
                _armedType = _armedType == t ? null : t;
            UITheme.Tip(t == RoomType.Untyped
                ? "Arm it and clicks in the plan mark rooms as not yet named. Untyped rooms are drawn dashed."
                : $"Arm it, then every click in the plan makes that room a {RoomRegions.Pretty(t).ToLowerInvariant()}. The type picks the floor finish.");
        }

        UITheme.Divider();

        bool any = Ctx?.Level?.rooms != null && Ctx.Level.rooms.Count > 0;
        if (!any)
        {
            // No prose. The empty state is a hoverable control carrying the sentence, never a blank
            // rectangle.
            UITheme.Glyph("⌂", "Draw walls that close off an area and it becomes a room by itself.",
                          UITheme.Ink3);
            DrawDetect();
            return;
        }

        var room = Find(_selectedId);

        // The rooms, behind one dropdown header: closed, it names the picked room (or counts them);
        // open, it is the picker. Open/close is deferred (it changes the rail's control count) the
        // way the variant menu latches.
        string header = room != null
            ? (string.IsNullOrEmpty(room.name) ? RoomRegions.Pretty(room.roomType) : room.name)
            : $"Rooms ({Ctx.Level.rooms.Count})";
        if (UITheme.StateRowLine(header, _roomListOpen ? "▴" : "▾", _roomListOpen))
            _pendingListToggle = true;
        UITheme.Tip("The rooms of this floor. Pick one here or click inside it in the plan.");

        if (_roomListOpen)
        {
            foreach (var r in Ctx.Level.rooms)
            {
                if (r == null) continue;
                string title = string.IsNullOrEmpty(r.name) ? RoomRegions.Pretty(r.roomType) : r.name;
                if (!UITheme.StateRowLine(title, RoomRegions.Pretty(r.roomType), r.id == _selectedId))
                    continue;
                _selectedId = r.id;
                _pendingListToggle = true;   // picking closes the menu, the way the variant menu does
                Ctx.Controller?.Select(ResidenceElementMarker.Kind.Floor, r.id, reveal: false);
            }
        }
        else if (room != null)
        {
            UITheme.Gap();
            string typed = UITheme.TextRow("Name", room.name,
                "What this room is called. Clear it to go back to a name from the type.");
            if (typed != room.name)
            {
                Ctx.RecordEdit("Rename room");
                room.name = typed;
                Ctx.Changed(false);     // a keystroke must not rebuild every GameObject in the residence
            }

            // The twelve type chips fold behind the current type: open, they wrap over four rail
            // rows, which buried the rest of the rail under a control used about once per room.
            // Picking a chip closes the fold again. Chips and the armed selector both funnel through
            // SetType: one mutation site.
            _showTypes = UITheme.Foldout(_showTypes, "Type: " + RoomRegions.Pretty(room.roomType));
            if (_showTypes)
            {
                // Twelve chips is affordable only because ChipRow MEASURES and wraps.
                var chips = UITheme.ChipRow();
                foreach (string t in RoomFinish.All)
                {
                    if (chips.Chip(RoomRegions.Pretty(t), room.roomType == t))
                    {
                        SetType(room, t);
                        _showTypes = false;
                    }
                    UITheme.Tip(t == RoomType.Untyped
                        ? "Leave it unsaid. Untyped rooms are drawn dashed, so a plan shows what still needs naming."
                        : $"Make this a {RoomRegions.Pretty(t).ToLowerInvariant()}. The type picks the floor finish.");
                }
                chips.End();
            }

            UITheme.Gap();
            UITheme.MutedLine(Units.FormatArea(ResidenceMetrics.RoomArea(room)), "Floor area of this room");
        }

        DrawDetect();
    }

    /// <summary>
    /// The catch-up handle for a residence drawn before rooms followed walls, or one whose stored rooms and
    /// wall graph have drifted apart. Explicit and undoable. Sync is deliberately never run on load,
    /// because that would rewrite every stored polygon on every open.
    /// </summary>
    private void DrawDetect()
    {
        if (Ctx?.Level == null) return;

        var found = RoomRegions.Find(Ctx.Level);
        int rooms = Ctx.Level.rooms?.Count ?? 0;
        if (RoomRegions.RoomsMatch(Ctx.Level, found)) return;

        UITheme.Gap();
        if (UITheme.PrimaryButton("Detect rooms")) _pendingDetect = true;
        UITheme.Tip(found.Count == rooms
            ? "The rooms no longer match their walls. Take the rooms from the walls."
            : $"The walls close off {found.Count} area{(found.Count == 1 ? "" : "s")} but this floor has "
              + $"{rooms} room{(rooms == 1 ? "" : "s")}. Take the rooms from the walls.");
    }

    private void SetType(RoomDef room, string type)
    {
        if (room.roomType == type) return;

        Ctx.RecordEdit("Set room type");

        // A name the user typed is never overwritten; an auto-generated one always is. So
        // "Room 3" -> Bedroom becomes "Bedroom 2", "Bedroom 2" -> Office becomes "Office", and
        // "Grandma's room" -> Bedroom stays "Grandma's room".
        bool rename = RoomRegions.IsAutoName(room.name, room.roomType);
        room.roomType = type;
        if (rename) room.name = RoomRegions.AutoName(Ctx.Level, type);

        Ctx.Changed(false);
        Ctx.Renderer?.RebuildRooms();   // ends in RebuildSensors, so coverage re-derives this frame
    }

    public override void DrawOverlay()
    {
        if (Ctx?.Cam == null || Ctx.Level?.rooms == null) return;

        float y = Ctx.Level.elevation;
        var accent = new Color(0.35f, 0.70f, 1f);
        var muted = new Color(0.35f, 0.70f, 1f, 0.35f);

        foreach (var room in Ctx.Level.rooms)
        {
            if (room == null) continue;
            var poly = PolygonTriangulator.ToVector2(room.polygon);
            if (poly.Count < 3) continue;

            bool selected = room.id == _selectedId;
            bool untyped = room.roomType == RoomType.Untyped;
            var color = selected ? accent : muted;

            for (int i = 0; i < poly.Count; i++)
            {
                // The carve's bridge edges are construction, not boundary: a room with an island
                // carved out is outlined as the outer ring plus the island, no seam between them.
                if (IsBridgeEdge(poly, i)) continue;

                if (!OverlayDraw.ToScreen(Ctx.Cam, poly[i], y, out Vector2 a)) continue;
                if (!OverlayDraw.ToScreen(Ctx.Cam, poly[(i + 1) % poly.Count], y, out Vector2 b)) continue;

                // Untyped rooms are dashed, so "three areas still need naming" is visible in the plan
                // without a sentence anywhere.
                if (untyped) OverlayDraw.DashedLine(a, b, color, selected ? 2.5f : 2f);
                else OverlayDraw.Line(a, b, color, selected ? 2.5f : 2f);
            }

            if (OverlayDraw.ToScreen(Ctx.Cam, ResidenceMetrics.RoomCentroid(room), y, out Vector2 c))
                OverlayDraw.Readout(c, string.IsNullOrEmpty(room.name)
                                      ? RoomRegions.Pretty(room.roomType) : room.name);
        }
    }

    // A ring holding both (a, b) and (b, a) is a room with an island bridge-cut out of it. See
    // RoomRegions.CarveContainedRegions. The twin edges are bit-identical copies, so exact float
    // equality is the right test.
    private static bool IsBridgeEdge(List<Vector2> poly, int i)
    {
        Vector2 a = poly[i], b = poly[(i + 1) % poly.Count];
        for (int j = 0; j < poly.Count; j++)
        {
            if (j == i) continue;
            if (poly[j] == b && poly[(j + 1) % poly.Count] == a) return true;
        }
        return false;
    }

    private RoomDef Find(string id)
    {
        if (string.IsNullOrEmpty(id) || Ctx?.Level?.rooms == null) return null;
        foreach (var r in Ctx.Level.rooms) if (r != null && r.id == id) return r;
        return null;
    }
}
