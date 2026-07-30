using UnityEngine;
using UnityEngine.InputSystem;

// Places furniture and fixtures from the catalog.
//
// Reuses the existing object-placement stack rather than introducing a parallel one: free-standing
// items become ObjectInstance (the AuthoringTypes type, with boxSizeMeters carrying true dimensions
// exactly as LayoutConverter already uses it), and wall-mounted items become WallMountDef, whose
// decor fields are the same ones DecorAlignment and DecorPlacement already understand.
//
// The catalog is dimensionally honest even with no art: a wheelchair is 0.66 × 1.22 m whether it is a
// model or a labeled box, so "does it fit beside the bed" is answerable today.
public class FurnitureTool : HomeToolBase
{
    public override string Id => "furniture";
    public override string DisplayName => "Furniture";

    private string _category = "mobility";
    private string _selectedId;
    private float _rotation;
    private Vector2 _cursor;
    private bool _hasCursor;

    private WallDef _hoverWall;
    private float _hoverOffset;
    private int _hoverSide;

    private FurnitureCatalog Catalog => Ctx?.Renderer?.Catalog;
    private FurnitureCatalog.Entry Selected => Catalog?.Get(_selectedId);

    public override void HandleInput()
    {
        if (Ctx?.Level == null || Ctx.IsLocked || Selected == null) return;

        _hasCursor = Ctx.GroundPoint(out _cursor);
        if (!_hasCursor) return;

        // Scroll rotates the ghost before placing — the same gesture the Brownfield placement tool
        // uses, so it is already in muscle memory.
        if (Mouse.current != null)
        {
            float scroll = Mouse.current.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f && Ctx.ShiftHeld)
                _rotation = Mathf.Repeat(_rotation + Mathf.Sign(scroll) * 15f, 360f);
        }

        if (Selected.IsWallMounted) UpdateWallHover();

        if (LeftClicked()) Place();
        if (KeyDown(Key.Q)) _rotation = Mathf.Repeat(_rotation - 90f, 360f);
        if (KeyDown(Key.E)) _rotation = Mathf.Repeat(_rotation + 90f, 360f);
    }

    // A grab bar has to land ON a wall, and on the correct FACE of it — mounting one on the outside
    // of a bathroom wall is a silent, useless result.
    private void UpdateWallHover()
    {
        _hoverWall = null;
        float best = 1.2f * 1.2f;

        foreach (var w in Ctx.Level.walls)
        {
            if (w?.a == null || w.b == null) continue;

            var a = new Vector2(w.a[0], w.a[1]);
            var b = new Vector2(w.b[0], w.b[1]);
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            if (lenSq <= 1e-6f) continue;

            float t = Mathf.Clamp01(Vector2.Dot(_cursor - a, ab) / lenSq);
            Vector2 foot = a + ab * t;
            float d = (_cursor - foot).sqrMagnitude;
            if (d >= best) continue;

            best = d;
            _hoverWall = w;
            _hoverOffset = t * Mathf.Sqrt(lenSq);

            // Which side of the centerline the cursor is on decides the mounting face.
            Vector2 dir = ab / Mathf.Sqrt(lenSq);
            Vector2 left = new Vector2(-dir.y, dir.x);
            _hoverSide = Vector2.Dot(_cursor - foot, left) >= 0f ? WallSide.Left : WallSide.Right;
        }
    }

    private void Place()
    {
        var entry = Selected;
        if (entry == null) return;

        if (entry.IsWallMounted)
        {
            if (_hoverWall == null) { Ctx.Controller.Status("Move closer to a wall to mount this."); return; }

            Ctx.RecordEdit("Place " + entry.Label);
            Ctx.Level.wallMounted.Add(
                FurnitureCatalog.NewWallMount(entry, _hoverWall.id, _hoverOffset, _hoverSide));
        }
        else
        {
            Ctx.RecordEdit("Place " + entry.Label);
            Ctx.Level.furniture.Add(FurnitureCatalog.NewInstance(
                entry, new Vector3(_cursor.x, Ctx.Level.elevation, _cursor.y), _rotation));
        }

        Ctx.Changed();
    }

    public override void DrawRail()
    {
        if (RefuseIfLocked()) return;

        var cat = Catalog;
        if (cat == null) { UITheme.Note("No furniture catalog is wired to the renderer."); return; }

        UITheme.Note("Pick an item, then click to place. Q/E rotate by 90°, Shift+scroll by 15°.");
        GUILayout.Space(6);

        var categories = cat.Categories();
        GUILayout.BeginHorizontal();
        for (int i = 0; i < categories.Count; i++)
        {
            if (i > 0 && i % 3 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            if (UITheme.Chip(Pretty(categories[i]), _category == categories[i])) _category = categories[i];
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(8);
        foreach (var e in cat.InCategory(_category))
        {
            // The dimensions are the point, so they are on the row rather than hidden behind a click.
            string size = $"{Units.Format(e.widthM)} × {Units.Format(e.depthM)}";
            if (UITheme.StateRow(e.Label, size, _selectedId == e.id)) _selectedId = e.id;
        }

        var sel = Selected;
        if (sel == null) return;

        GUILayout.Space(10);
        UITheme.Header("Selected");
        UITheme.Num($"{Units.Format(sel.widthM)} × {Units.Format(sel.depthM)}");
        UITheme.Note($"{sel.Label} · {Units.Format(sel.heightM)} high");

        if (sel.IsWallMounted)
            UITheme.Note("Wall-mounted at " + Units.Format(sel.mountHeightM) + " above the floor. " +
                         "Hover near a wall; the side you hover on is the side it mounts to.");
        else
            _rotation = UITheme.SliderRow("Rotation", _rotation, 0f, 360f, "0", "°");

        // Flag that no art exists yet, so a room of grey boxes is understood rather than mistaken
        // for a rendering failure.
        var prefabs = Ctx.Renderer?.Prefabs;
        bool hasArt = false;
        if (prefabs?.entries != null)
            foreach (var pe in prefabs.entries)
                if (pe != null && string.Equals(pe.key, sel.id, System.StringComparison.OrdinalIgnoreCase))
                    { hasArt = pe.prefab != null; break; }

        if (!hasArt)
            UITheme.Note("No 3D model for this item yet — it renders as a labeled box at its true size. " +
                         "Adding a prefab under the key \"" + sel.id + "\" replaces it with no other change.");
    }

    public override void DrawOverlay()
    {
        var entry = Selected;
        if (Ctx?.Cam == null || Ctx.Level == null || entry == null || !_hasCursor) return;

        float y = Ctx.Level.elevation;
        var color = new Color(0.95f, 0.75f, 0.25f);

        if (entry.IsWallMounted)
        {
            if (_hoverWall == null) return;
            Vector2 at = HomeMetrics.PointOnWall(_hoverWall, _hoverOffset);
            if (OverlayDraw.ToScreen(Ctx.Cam, at, y, out Vector2 g))
            {
                OverlayDraw.Dot(g, 12f, color);
                OverlayDraw.Readout(g, entry.Label + " · " + Units.Format(entry.mountHeightM) + " AFF");
            }
            return;
        }

        // True-size footprint ghost, so overlaps are visible before committing.
        float hw = 0.5f * entry.widthM, hd = 0.5f * entry.depthM;
        float rad = _rotation * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);

        var corners = new Vector2[4];
        var local = new[] { new Vector2(-hw, -hd), new Vector2(hw, -hd), new Vector2(hw, hd), new Vector2(-hw, hd) };
        for (int i = 0; i < 4; i++)
            corners[i] = _cursor + new Vector2(local[i].x * cos - local[i].y * sin,
                                               local[i].x * sin + local[i].y * cos);

        for (int i = 0; i < 4; i++)
            if (OverlayDraw.ToScreen(Ctx.Cam, corners[i], y, out Vector2 g1) &&
                OverlayDraw.ToScreen(Ctx.Cam, corners[(i + 1) % 4], y, out Vector2 g2))
                OverlayDraw.Line(g1, g2, color, 2.5f);

        if (OverlayDraw.ToScreen(Ctx.Cam, _cursor, y, out Vector2 c))
            OverlayDraw.Readout(c, $"{entry.Label}  {Units.Format(entry.widthM)} × {Units.Format(entry.depthM)}");
    }

    private static string Pretty(string token)
    {
        if (string.IsNullOrEmpty(token)) return "Other";
        string s = token.Replace('_', ' ');
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
