using UnityEngine;
using UnityEngine.InputSystem;

// Pick an element and edit its properties.
//
// The inspector here is where the accessibility story actually surfaces. Selecting a door does not
// just show its width — it shows the CLEAR width (and whether that number was measured or estimated),
// and whether the threshold is step-free. Selecting a room shows its area and the turning circle that
// fits inside it. Those are the numbers a proposal is argued with, so they belong one click away.
public class SelectTool : HomeToolBase
{
    public override string Id => "select";
    public override string DisplayName => "Select";

    public override void HandleInput()
    {
        if (Ctx == null) return;

        if (LeftClicked())
        {
            if (Ctx.PickElement(out HomeElementMarker marker, out _))
            {
                Ctx.Controller.SelectedKind = marker.kind;
                Ctx.Controller.SelectedId = marker.id;
            }
            else Ctx.Controller.ClearSelection();
        }

        if ((KeyDown(Key.Delete) || KeyDown(Key.Backspace)) && !Ctx.IsLocked) DeleteSelected();
    }

    public override void DrawRail()
    {
        if (Ctx?.Level == null) return;

        string id = Ctx.Controller.SelectedId;
        if (string.IsNullOrEmpty(id))
        {
            UITheme.Note("Click a wall, door, room, or piece of furniture to select it.");
            return;
        }

        switch (Ctx.Controller.SelectedKind)
        {
            case HomeElementMarker.Kind.Wall: DrawWall(id); break;
            case HomeElementMarker.Kind.Opening: DrawOpening(id); break;
            case HomeElementMarker.Kind.Room:
            case HomeElementMarker.Kind.Floor:
            case HomeElementMarker.Kind.Ceiling: DrawRoom(id); break;
            case HomeElementMarker.Kind.Furniture: DrawFurniture(id); break;
            case HomeElementMarker.Kind.WallMount: DrawWallMount(id); break;
        }

        GUILayout.Space(10);
        if (!Ctx.IsLocked && UITheme.DangerButton("Delete")) DeleteSelected();
    }

    // ---------------------------------------------------------------------------------------

    private void DrawWall(string id)
    {
        var w = FindWall(id);
        if (w == null) return;

        UITheme.Header(w.structural ? "Structural wall" : "Wall");
        UITheme.Num(Units.Format(HomeMetrics.WallLength(w)));
        UITheme.Note("Length");

        GUILayout.Space(6);
        float t = WallLayout.EffectiveThickness(w, Ctx.Level);
        float h = WallLayout.EffectiveHeight(w, Ctx.Level);
        UITheme.Note("Thickness  " + Units.Format(t));
        UITheme.Note("Height     " + Units.Format(h));

        if (Ctx.IsLocked) return;

        GUILayout.Space(6);
        float nt = UITheme.Stepper("Thickness", t, 0.012f, "0.000", " m");
        float nh = UITheme.Stepper("Height", h, 0.05f, "0.00", " m");
        bool ns = GUILayout.Toggle(w.structural, "  Structural (load-bearing)");

        if (!Mathf.Approximately(nt, t) || !Mathf.Approximately(nh, h) || ns != w.structural)
        {
            Ctx.RecordEdit("Edit wall");
            w.thickness = nt;
            w.height = nh;
            w.structural = ns;
            Ctx.Changed();
        }

        int openings = WallLayout.OpeningsFor(w, Ctx.Level).Count;
        if (openings > 0) UITheme.Note($"{openings} opening{(openings == 1 ? "" : "s")} in this wall.");
    }

    private void DrawOpening(string id)
    {
        var o = FindOpening(id);
        if (o == null) return;

        UITheme.Header(Pretty(o.kind));

        // The headline number is the CLEAR width, not the rough opening — that is the dimension a
        // wheelchair has to pass through and the one an accessibility rule would test.
        UITheme.Num(Units.Format(HomeMetrics.ClearWidth(o)));
        UITheme.Note(HomeMetrics.IsClearWidthMeasured(o)
            ? "Clear width (measured on site)"
            : "Clear width (estimated from the rough opening)");

        GUILayout.Space(4);
        UITheme.Note("Rough opening  " + Units.Format(o.width));
        UITheme.Note("Height         " + Units.Format(o.height));
        if (o.sillHeight > 0f) UITheme.Note("Sill           " + Units.Format(o.sillHeight));

        UITheme.StatusBadge(
            HomeMetrics.HasThreshold(o) ? "Threshold " + Units.Format(o.thresholdHeight) : "Step-free",
            !HomeMetrics.HasThreshold(o));

        if (Ctx.IsLocked) return;

        GUILayout.Space(8);
        UITheme.Header("Width");

        // Doors are specified in inches in this context, so the presets are the sizes people ask for.
        GUILayout.BeginHorizontal();
        foreach (int inches in new[] { 28, 30, 32, 34, 36 })
        {
            float m = inches * HomeConventions.IN_TO_M;
            if (UITheme.Chip(inches + "\"", Mathf.Abs(o.width - m) < 0.005f)) SetWidth(o, m);
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        float nt = UITheme.Stepper("Threshold", o.thresholdHeight, 0.003f, "0.000", " m");
        if (!Mathf.Approximately(nt, o.thresholdHeight))
        {
            Ctx.RecordEdit("Edit threshold");
            o.thresholdHeight = Mathf.Max(0f, nt);
            Ctx.Changed();
        }
        if (o.thresholdHeight > 0f && UITheme.SecondaryButton("Make step-free"))
        {
            Ctx.RecordEdit("Remove threshold");
            o.thresholdHeight = 0f;
            Ctx.Changed();
        }
    }

    private void SetWidth(OpeningDef o, float meters)
    {
        var wall = FindWall(o.wallId);
        if (wall == null) return;

        // Widening can push an opening past a corner or into its neighbour, so re-fit rather than
        // letting the wall silently develop an impossible hole.
        var fit = OpeningFit.Fit(o.offset, meters, WallLayout.WallLength(wall),
                                 WallLayout.OpeningsFor(wall, Ctx.Level), o.id);
        if (!fit.ok) { Ctx.Controller.Status(fit.reason); return; }

        Ctx.RecordEdit("Resize opening");
        o.width = meters;
        o.offset = fit.offset;
        o.clearWidth = 0f;   // the measured value no longer applies to a resized opening
        Ctx.Changed();
    }

    private void DrawRoom(string id)
    {
        var r = FindRoom(id);
        if (r == null) return;

        UITheme.Header(string.IsNullOrEmpty(r.name) ? Pretty(r.roomType) : r.name);
        UITheme.Num(Units.FormatArea(HomeMetrics.RoomArea(r)));
        UITheme.Note("Floor area");

        var circle = HomeMetrics.LargestInscribedCircle(r);
        if (circle.valid)
        {
            GUILayout.Space(6);
            UITheme.Num(Units.Format(circle.radius * 2f));
            UITheme.Note("Largest turning circle that fits");
        }

        if (Ctx.IsLocked) return;

        GUILayout.Space(8);
        string newName = GUILayout.TextField(r.name ?? "");
        if (newName != r.name)
        {
            Ctx.RecordEdit("Rename room");
            r.name = newName;
            Ctx.Changed(false);
        }
    }

    private void DrawFurniture(string id)
    {
        var f = FindFurniture(id);
        if (f == null) return;

        var entry = Ctx.Renderer?.Catalog?.Get(f.prefabType);
        UITheme.Header(entry != null ? entry.Label : Pretty(f.prefabType));

        if (f.boxSizeMeters != null && f.boxSizeMeters.Length >= 3)
            UITheme.Note($"{Units.Format(f.boxSizeMeters[0])} × {Units.Format(f.boxSizeMeters[2])} " +
                         $"× {Units.Format(f.boxSizeMeters[1])} high");

        var room = HomeMetrics.RoomAt(new Vector2(f.position[0], f.position[2]), Ctx.Level);
        if (room != null) UITheme.Note("In " + (string.IsNullOrEmpty(room.name) ? Pretty(room.roomType) : room.name));

        if (Ctx.IsLocked) return;

        GUILayout.Space(6);
        float rot = UITheme.SliderRow("Rotation", f.rotationY, 0f, 360f, "0", "°");
        if (!Mathf.Approximately(rot, f.rotationY))
        {
            Ctx.BeginGesture("Rotate furniture");
            f.rotationY = rot;
            Ctx.Changed();
        }
    }

    private void DrawWallMount(string id)
    {
        var m = FindWallMount(id);
        if (m == null) return;

        var entry = Ctx.Renderer?.Catalog?.Get(m.prefabType);
        UITheme.Header(entry != null ? entry.Label : Pretty(m.prefabType));
        UITheme.Num(Units.Format(m.mountHeight));
        UITheme.Note("Height above finished floor");

        if (Ctx.IsLocked) return;

        float h = UITheme.Stepper("Height", m.mountHeight, 0.025f, "0.00", " m");
        if (!Mathf.Approximately(h, m.mountHeight))
        {
            Ctx.RecordEdit("Move wall-mounted item");
            m.mountHeight = Mathf.Max(0f, h);
            Ctx.Changed();
        }
    }

    // ---------------------------------------------------------------------------------------

    private void DeleteSelected()
    {
        string id = Ctx.Controller.SelectedId;
        if (string.IsNullOrEmpty(id) || Ctx.Level == null) return;

        Ctx.RecordEdit("Delete");

        switch (Ctx.Controller.SelectedKind)
        {
            case HomeElementMarker.Kind.Wall:
                var w = FindWall(id);
                if (w != null)
                {
                    // An opening with no wall is unreachable and unrenderable, so it goes too.
                    Ctx.Level.openings.RemoveAll(o => o != null && o.wallId == id);
                    Ctx.Level.wallMounted.RemoveAll(m => m != null && m.wallId == id);
                    Ctx.Level.walls.Remove(w);
                }
                break;

            case HomeElementMarker.Kind.Opening:
                Ctx.Level.openings.RemoveAll(o => o != null && o.id == id);
                break;

            case HomeElementMarker.Kind.Room:
            case HomeElementMarker.Kind.Floor:
            case HomeElementMarker.Kind.Ceiling:
                Ctx.Level.rooms.RemoveAll(r => r != null && r.id == id);
                break;

            case HomeElementMarker.Kind.Furniture:
                Ctx.Level.furniture.RemoveAll(f => f != null && f.instanceId == id);
                break;

            case HomeElementMarker.Kind.WallMount:
                Ctx.Level.wallMounted.RemoveAll(m => m != null && m.instanceId == id);
                break;
        }

        Ctx.Controller.ClearSelection();
        Ctx.Changed();
    }

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

    private static string Pretty(string token)
    {
        if (string.IsNullOrEmpty(token)) return "Item";
        string s = token.Replace('_', ' ');
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
