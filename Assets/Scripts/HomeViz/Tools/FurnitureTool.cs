using System.Collections.Generic;
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

    // Always: every click places an item. Placing already selects what you put down.
    public override bool ClaimsClicks => true;

    public override string Hint =>
        "Pick an item, then click to place it. Z and X rotate by 90°, Shift+scroll by 15°. Everything "
        + "slides clear of any doorway it is tall enough to block, and what you place comes up selected "
        + "with its handles on it.";

    // null means "All". InCategory(null) already returns every entry. It used to default to the
    // literal "mobility", which was only ever correct because that is the string the shipped asset
    // happens to use first.
    private string _category;
    private string _search = "";
    private Vector2 _gridScroll;

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

        // A wall-mounted item reads the cursor from the wall face under the pointer rather than
        // from the floor projection, which lands on the far side of the wall under the angled
        // camera: the same call SensorTool makes, through the same helper.
        _hasCursor = Selected.IsWallMounted ? MountPlacement.WallCursor(Ctx, out _cursor)
                                            : Ctx.GroundPoint(out _cursor);
        if (!_hasCursor) return;

        // Scroll rotates the ghost before placing: the same gesture the Site placement tool
        // uses, so it is already in muscle memory.
        if (Mouse.current != null)
        {
            float scroll = Mouse.current.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f && Ctx.ShiftHeld)
                _rotation = Mathf.Repeat(_rotation + Mathf.Sign(scroll) * 15f, 360f);
        }

        if (Selected.IsWallMounted) UpdateWallHover();

        if (LeftClicked()) Place();

        // Z/X rather than the Q/E this used to be: Q/E now raise and lower the overview camera, and
        // camera input is not gated on the pointer being off the rails, so both would have fired.
        if (KeyDown(Key.Z)) _rotation = Mathf.Repeat(_rotation - 90f, 360f);
        if (KeyDown(Key.X)) _rotation = Mathf.Repeat(_rotation + 90f, 360f);
    }

    // Through MountPlacement, not HomeMetrics directly, because the Smart living rail places grab
    // bars too and the two must not each have their own idea of which side of a wall a click is on.
    private void UpdateWallHover()
        => _hoverWall = MountPlacement.Hover(Ctx, _cursor, out _hoverOffset, out _hoverSide);

    private void Place()
    {
        var entry = Selected;
        if (entry == null) return;

        // Everything below goes through FurnitureFit for the same reason openings go through
        // OpeningFit: an item standing in a doorway is not caught by anything downstream. There is no
        // wall geometry in an opening to collide with, so if it is not stopped here it is not stopped
        // at all. The fit SLIDES rather than refuses, and a refusal still places, because swallowing a
        // click is the one outcome that reads as the tool being broken.
        if (entry.IsWallMounted)
        {
            // Records the edit, writes the mount, selects it and reports, and returns having said so
            // when there is no wall in reach, which is the one case with nothing left to do.
            MountPlacement.Place(Ctx, entry, _hoverWall, _hoverOffset, _hoverSide);
            return;
        }

        var fit = FitFloor(entry);

        Ctx.RecordEdit("Place " + entry.Label);
        var item = FurnitureCatalog.NewInstance(
            entry, new Vector3(fit.position.x, Ctx.Level.elevation, fit.position.y), _rotation);
        Ctx.Level.furniture.Add(item);
        Select(HomeElementMarker.Kind.Furniture, item.instanceId);

        Report(entry, fit.ok, fit.moved, fit.reason);
        Ctx.Changed();
    }

    private FurnitureFit.Result FitFloor(FurnitureCatalog.Entry entry)
        => FurnitureFit.Fit(_cursor,
                            FurnitureFit.Footprint(entry.widthM, entry.depthM, _rotation),
                            entry.heightM,
                            Ctx.Level);

    // What you just put down becomes the selection, so its handles are already on it and the rail is
    // already describing it. Before this, placing selected nothing and the only way to reach the
    // transform controls was to switch to Select and click the item you were looking straight at.
    //
    // reveal: false is MANDATORY here. Selecting is a side effect of placing, not the point of it, and
    // jumping to the Select tab after every single placement would make furnishing a room impossible.
    private void Select(HomeElementMarker.Kind kind, string id)
        => Ctx.Controller.Select(kind, id, reveal: false);

    private void Report(FurnitureCatalog.Entry entry, bool ok, bool moved, string reason)
    {
        if (!ok) Ctx.Controller.Status($"Placed {entry.Label}, but it does not fit cleanly: {reason}");
        else if (moved) Ctx.Controller.Status($"Placed {entry.Label}: {reason}");
    }

    public override void DrawRail()
    {
        if (RefuseIfLocked()) return;

        var cat = Catalog;
        // A configuration fault, not a message for whoever is using the app. It belongs in the console
        // where whoever wired the scene will see it, not in the rail where nobody can act on it.
        if (cat == null)
        {
            Debug.LogWarning("FurnitureTool: no FurnitureCatalog is wired to the HomeRenderer, so the "
                           + "furniture grid is empty. Assign one on the renderer in HomeViz.unity.");
            return;
        }

        _search = UITheme.TextRow("Search", _search, "Search the whole catalog by name");

        var categories = cat.Categories();
        // Wrapping by measurement: the category names come from the catalog asset, and "All" plus the
        // first three put four chips on a row that only holds about three of them.
        // No leading label: every chip on this row is a category name, and "Show" said only that a
        // row of categories is a row of categories: the same call DrawStageTools made for "Tool".
        var chips = UITheme.ChipRow();
        // "All" first, and it is the default. Searching is only useful across the whole catalog, so a
        // non-empty search ignores the category entirely rather than hiding matches behind a chip.
        if (chips.Chip("All", _category == null)) _category = null;
        UITheme.Tip("Every item in the catalog");
        for (int i = 0; i < categories.Count; i++)
        {
            if (chips.Chip(Pretty(categories[i]), _category == categories[i])) _category = categories[i];
            UITheme.Tip($"Only {Pretty(categories[i]).ToLowerInvariant()} items");
        }
        chips.End();

        UITheme.Gap();

        // Its own scroll view rather than relying on the rail's: with All selected this is 35 tiles,
        // which would otherwise push the Selected block and everything under it off the bottom.
        _gridScroll = UITheme.BeginScroll(_gridScroll, GUILayout.Height(GRID_HEIGHT));

        // Columns from the width that is actually left, not the constant 3. Three 76 px tiles plus
        // their margins come to ~246 px against the ~252 the rail has once two scrollbars have taken
        // their share. Close enough that it was one nested scroll view away from clipping a column.
        int cols = Mathf.Max(1, Mathf.FloorToInt(UITheme.ContentWidth / (THUMB_SIZE + TILE_GAP)));
        int col = 0;
        GUILayout.BeginHorizontal();
        foreach (var e in cat.InCategory(Searching ? null : _category))
        {
            if (!Matches(e, _search)) continue;

            if (col >= cols) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); col = 0; }
            col++;

            if (UITheme.Thumb(TileFor(e), e.Label, _selectedId == e.id, THUMB_SIZE)) _selectedId = e.id;
            UITheme.Tip($"{e.Label}: {Units.Format(e.widthM)} × {Units.Format(e.depthM)}, "
                        + $"{Units.Format(e.heightM)} high");
        }
        GUILayout.EndHorizontal();
        UITheme.EndScroll();

        var sel = Selected;
        if (sel == null) return;

        UITheme.Header(sel.Label);

        // That no art exists yet is folded into the size tooltip rather than printed: a room of grey
        // boxes only needs explaining once, and it is explained where the size is read.
        bool boxed = Ctx.Renderer?.FindPrefab(sel.id) == null;
        UITheme.Value("Footprint", $"{Units.Format(sel.widthM)} × {Units.Format(sel.depthM)}",
            $"Footprint, and {Units.Format(sel.heightM)} high"
            + (boxed ? ". No 3D model for this item yet, so it renders as a labeled box at its true size."
                     : ""));

        if (sel.IsWallMounted)
            UITheme.Value("Mounts at", Units.Format(sel.mountHeightM),
                "Mounted this far above the floor. Hover near a wall, and the side you hover on is the "
                + "side it mounts to.");
        else
            _rotation = MeasureUI.Angle("Facing", "Which way it faces when placed", _rotation);
    }

    public override void DrawOverlay()
    {
        var entry = Selected;
        if (Ctx?.Cam == null || Ctx.Level == null || entry == null || !_hasCursor) return;

        float y = Ctx.Level.elevation;
        var color = new Color(0.95f, 0.75f, 0.25f);

        // The ghost is drawn where the item will actually END UP, not under the cursor: the fit slides
        // it clear of openings, and a preview that ignored that would be a promise the click breaks.
        if (entry.IsWallMounted)
        {
            if (_hoverWall == null) return;
            Vector2 at = MountPlacement.Ghost(entry, _hoverOffset, _hoverWall, Ctx.Level);
            if (OverlayDraw.ToScreen(Ctx.Cam, at, y, out Vector2 g))
            {
                OverlayDraw.Dot(g, 12f, color);
                OverlayDraw.Readout(g, entry.Label + " · " + Units.Format(entry.mountHeightM) + " AFF");
            }
            return;
        }

        Vector2 center = FitFloor(entry).position;

        // True-size footprint ghost, so overlaps are visible before committing.
        float hw = 0.5f * entry.widthM, hd = 0.5f * entry.depthM;
        float rad = _rotation * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);

        var corners = new Vector2[4];
        var local = new[] { new Vector2(-hw, -hd), new Vector2(hw, -hd), new Vector2(hw, hd), new Vector2(-hw, hd) };
        // Quaternion.Euler(0, yaw, 0) maps (x, z) -> (x cos + z sin, -x sin + z cos). Rotating the
        // ghost the other way made it a mirror image of what spawned at every 15-degree step; the two
        // agreed only on the quarter turns, which is why it went unnoticed.
        for (int i = 0; i < 4; i++)
            corners[i] = center + new Vector2(local[i].x * cos + local[i].y * sin,
                                              -local[i].x * sin + local[i].y * cos);

        for (int i = 0; i < 4; i++)
            if (OverlayDraw.ToScreen(Ctx.Cam, corners[i], y, out Vector2 g1) &&
                OverlayDraw.ToScreen(Ctx.Cam, corners[(i + 1) % 4], y, out Vector2 g2))
                OverlayDraw.Line(g1, g2, color, 2.5f);

        if (OverlayDraw.ToScreen(Ctx.Cam, center, y, out Vector2 c))
            OverlayDraw.Readout(c, $"{entry.Label}  {Units.Format(entry.widthM)} × {Units.Format(entry.depthM)}");
    }

    // ---------------------------------------------------------------------------------------
    // The picker grid
    // ---------------------------------------------------------------------------------------

    private const float THUMB_SIZE = 76f;   // the Site Place rail's tile size
    private const float TILE_GAP = UITheme.TileGap;      // UITheme's button margin, 3 px each side
    private const float GRID_HEIGHT = UITheme.GridHeight;

    private bool Searching => !string.IsNullOrWhiteSpace(_search);

    // Matches the display name OR the catalog id, because the id is the key someone adding art works
    // in ("grab_bar") while the name is what the row shows ("Grab bar").
    private static bool Matches(FurnitureCatalog.Entry e, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        string f = filter.Trim();
        return (e.Label ?? "").IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0
            || (e.id ?? "").IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// The tile image: a rendered preview when real art exists under this id, a plan of the item's
    /// true footprint otherwise.
    /// </summary>
    /// <remarks>
    /// The same promise the renderer makes, made in the picker too: the day a prefab lands under a
    /// catalog key its tile becomes a real preview with no change here.
    ///
    /// Until then the tile is NOT just the swatch. The shipped catalog colours by CATEGORY, so a grid
    /// of flat swatches makes every mobility item the same blue and every bedroom item the same
    /// purple: the tile would say which chip you already clicked and nothing else. Drawing the
    /// footprint to scale instead puts back what the old list rows carried in text: a double bed
    /// reads as a big rectangle, a nightstand as a small square, a grab bar as a sliver, which is the
    /// one thing this catalog exists to be honest about.
    ///
    /// ThumbnailCache MUST be called from OnGUI exactly like this. It queues the render and returns
    /// null on the first frame, because rendering a camera swaps the active render target and doing
    /// that during a repaint blanks the entire IMGUI pass. Do not "fix" the null.
    /// </remarks>
    private Texture TileFor(FurnitureCatalog.Entry e)
    {
        var prefab = Ctx?.Renderer?.FindPrefab(e.id);
        if (prefab != null)
        {
            var tex = ThumbnailCache.GetPrefab(prefab);
            if (tex != null) return tex;
        }
        return Plan(e);
    }

    // One texture per catalog entry, built once. Static so it survives tool switches; 35 small
    // textures is never worth freeing.
    private static readonly Dictionary<string, Texture2D> _plans = new Dictionary<string, Texture2D>();

    private const int TILE_PX = 64;
    // The longest thing in the catalog is a 2.13 m hospital bed. Everything is drawn against this
    // fixed reference rather than normalised per tile, because the whole point is that a bed and a
    // nightstand are NOT the same size. Normalising would draw them identically.
    private const float TILE_SPAN_M = 2.3f;

    private static Texture2D Plan(FurnitureCatalog.Entry e)
    {
        if (_plans.TryGetValue(e.id, out var cached) && cached != null) return cached;

        var tex = new Texture2D(TILE_PX, TILE_PX) { hideFlags = HideFlags.HideAndDontSave };

        // Enough contrast to read on both the light tile and its selected state.
        Color ink = e.swatch;
        Color paper = new Color(ink.r, ink.g, ink.b, 0.18f);

        // Half-extents in pixels, floored at one pixel so a 50 mm grab bar is still a visible line.
        int hw = Mathf.Max(1, Mathf.RoundToInt(0.5f * TILE_PX * Mathf.Clamp01(e.widthM / TILE_SPAN_M)));
        int hd = Mathf.Max(1, Mathf.RoundToInt(0.5f * TILE_PX * Mathf.Clamp01(e.depthM / TILE_SPAN_M)));
        int c = TILE_PX / 2;

        var px = new Color[TILE_PX * TILE_PX];
        for (int y = 0; y < TILE_PX; y++)
            for (int x = 0; x < TILE_PX; x++)
                px[y * TILE_PX + x] = (Mathf.Abs(x - c) <= hw && Mathf.Abs(y - c) <= hd) ? ink : paper;

        tex.SetPixels(px);
        tex.filterMode = FilterMode.Point;   // a footprint is a measurement, not a gradient
        tex.Apply();

        _plans[e.id] = tex;
        return tex;
    }

    private static string Pretty(string token)
    {
        if (string.IsNullOrEmpty(token)) return "Other";
        string s = token.Replace('_', ' ');
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
