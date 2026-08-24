using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Installs everything in the Smart living catalog: sensing devices, everyday aids, and the
// accessibility fixtures that live in FurnitureCatalog.
//
// The same shape as FurnitureTool: a searchable grid, chips per category, a ghost that previews where
// the click will actually land, because placing a door sensor and placing a chair are the same
// gesture and should feel like it. What differs is the question a click answers: FurnitureFit asks
// WHERE, clamping a coordinate, while SensorFit asks ON WHAT, resolving a host element. See its header
// for why that distinction is the whole feature.
//
// ---------------------------------------------------------------------------------------------
// THE TILE HAS THREE MODES, AND IT USED TO HAVE ONE
//
// It was a coverage disc, on the stated grounds that "every device in this catalog is a small grey
// box, so a grid of footprints would be sixteen identical dots: what distinguishes them is reach".
// The first half of that was true and the second half was only true of five of them. ELEVEN of the
// sixteen already had no reach at all, so eleven tiles were already the identical 5x5 dot the argument
// was made against: the failure FurnitureTool's own remarks block exists to prevent, sitting
// unnoticed in this file the whole time. Nine everyday items would have made it twenty.
//
// So the tile draws whatever actually tells this entry apart from its neighbors:
//
//   * REACH, for the five that have any, against the 9.6 m span. Unchanged.
//   * FOOTPRINT, for anything installed with no reach, against a 0.6 m span: a bin fills its tile, a
//     door sensor is a chip, a thermostat sits between them. Same idea as the furniture grid, one
//     order of magnitude down, because the biggest thing here is a 0.51 m bed pad rather than a
//     2.13 m hospital bed.
//   * A SWATCH BLOCK, for anything worn, because a zipper pull and a sock aid differ by a centimetre
//     or two, so against the same span every one of them rounds to the same handful of pixels. The
//     caption strip carries the name, and each everyday entry has its own colour, which is what the
//     eye actually picks out of a grid.
//
// ---------------------------------------------------------------------------------------------
// A PERSONAL ITEM NO LONGER NEEDS A PERSON
//
// A pendant belongs to a resident and a key turner lives in a pocket, so both hang off OccupantDef,
// and while that was the ONLY answer, eight of the twenty-five entries were unplaceable in a home
// with an empty roster. The rail refused them with a warning whose only advice was to go and use a
// different tab, which is a hard stop in a tool used to lay a home out before deciding who moves in.
//
// The rail's "Worn by" row now leads with NOBODY, and that is the default. SensorFit reads it as "put
// this down in the room that was clicked": a Room host, at counter height, drawn as a labeled box
// exactly like the hub and the medication dispenser beside it. Choosing a resident still does what it
// always did. Nothing downstream needed a line for either, and no figure about what the home can see
// moves. See SensorFit.Personal for why both of those are true by construction.
public class SensorTool : HomeToolBase
{
    // "Equipment", not "Sensors": two thirds of this grid no longer senses anything. It holds the
    // report's devices, the everyday aids that are not connected to anything at all, and the
    // accessibility fixtures that live in the furniture catalog. Id stays "sensor": a key, not a
    // caption, and every RequestTool call and HomeWorkflow.StageOf lookup goes through it.
    public override string Id => "sensor";
    public override string DisplayName => "Equipment";

    // Always: every click places something. Placing already selects what you put down.
    public override bool ClaimsClicks => true;

    public override string Hint =>
        "Pick something, then click where it goes: a doorway, a bed, a range, a wall, a counter or "
        + "a room. It installs on that element, so widening the door later carries its sensor with "
        + "it. Anything worn or personal can be assigned to a resident in the rail, or left "
        + "unassigned and put down on a counter or a table.";

    private string _category;
    private string _search = "";
    private Vector2 _gridScroll;

    private string _selectedId;
    private string _fixtureId;          // a FurnitureCatalog key; never set at the same time as above
    private string _wearer;             // for anything worn or personal
    private Vector2 _cursor;
    private bool _hasCursor;

    private WallDef _hoverWall;
    private float _hoverOffset;
    private int _hoverSide;

    private SensorCatalog Catalog => Ctx?.Renderer?.Sensors;
    private SensorCatalog.Entry Selected => Catalog?.Get(_selectedId);

    // ---------------------------------------------------------------------------------------
    // Fixtures: the accessibility items that live in FurnitureCatalog, surfaced here
    // ---------------------------------------------------------------------------------------
    //
    // A grab bar beside a toilet is one of the things this stage exists to argue about, and it was in
    // the Furnish catalog between a sofa and a wardrobe. It is offered HERE TOO, and its data does not
    // move: placing one still writes a WallMountDef through MountPlacement, exactly as the Furnish
    // rail does, so every home already on disk, all six samples, VariantDiff, VariantRevert and the
    // wall-mount inspector are untouched by this. It is a second door into the same room.
    //
    // The chip is a SYNTHETIC category. FurnitureCatalog has its own "fixtures" string and this is not
    // it, hence the tilde, which no catalog key can contain.
    private const string FIXTURES = "~fixtures";

    /// <summary>
    /// The FurnitureCatalog keys offered here: the ones that are an accessibility decision rather than
    /// a furnishing one. Deliberately NOT every wall-mounted item: a wall cabinet and a light switch
    /// are furnishing, and listing them would make this chip a second copy of the Furnish rail.
    /// </summary>
    /// <remarks>
    /// WALL-MOUNTED ONLY, and that is why `threshold_ramp` is absent despite being just as much an
    /// accessibility fixture. A floor item needs a rotation to be placed at all, and a rotation needs
    /// a control, a ghost that turns and a pair of keys, which is the Furnish rail, in full, a second
    /// time. It stays one gesture away in Furnish rather than half-built here.
    /// </remarks>
    private static readonly string[] FixtureIds = { "grab_bar_24", "grab_bar_36", "handrail" };

    private FurnitureCatalog Fixtures => Ctx?.Renderer?.Catalog;
    private FurnitureCatalog.Entry Fixture => string.IsNullOrEmpty(_fixtureId)
        ? null : Fixtures?.Get(_fixtureId);

    // The coverage wash comes on with this tool and goes off with it. It answers "where can this home
    // not see", which is the question you are here to answer, and it is a wash across whole rooms, so
    // leaving it on while someone traces walls would be unreadable.
    public override void Enter(HomeToolContext ctx)
    {
        base.Enter(ctx);
        if (ctx?.Controller != null) ctx.Controller.SensorCoverageVisible = true;
    }

    public override void Exit()
    {
        if (Ctx?.Controller != null) Ctx.Controller.SensorCoverageVisible = false;
        base.Exit();
    }

    public override void HandleInput()
    {
        if (Ctx?.Level == null || Ctx.IsLocked) return;

        var fixture = Fixture;
        if (Selected == null && fixture == null) return;

        // Anything aimed at a wall face reads the cursor from the wall itself rather than from the
        // floor projection, which lands on the far side of the wall under the angled camera.
        bool wallAim = fixture != null
                    || Selected.hostKind == SensorHost.Wall
                    || Selected.hostKind == SensorHost.Opening;
        _hasCursor = wallAim ? MountPlacement.WallCursor(Ctx, out _cursor)
                             : Ctx.GroundPoint(out _cursor);
        if (!_hasCursor) return;

        if (fixture != null)
        {
            // Same hover as the Furnish rail, through the same helper, so the two cannot disagree
            // about which face of a wall the cursor is on.
            _hoverWall = MountPlacement.Hover(Ctx, _cursor, out _hoverOffset, out _hoverSide);
            if (LeftClicked())
                MountPlacement.Place(Ctx, fixture, _hoverWall, _hoverOffset, _hoverSide);
            return;
        }

        if (LeftClicked()) Place();
    }

    private void Place()
    {
        var entry = Selected;
        if (entry == null) return;

        var fit = SensorFit.Fit(entry.id, _cursor, Ctx.Level, Ctx.Variant, _wearer);
        if (!fit.ok)
        {
            // A refusal here is REAL (there is no legal host, not merely an awkward one) so unlike
            // FurnitureFit this does not place anyway. Installing a stove sensor on a wardrobe would
            // be a device that reports nothing forever, which is worse than a click that says why not.
            Ctx.Controller.Status(fit.reason);
            return;
        }

        // The duplicate guard exists because "two door sensors on one door is not a redundancy, it is
        // one the user forgot they placed, and it doubles every alert that door raises". An item that
        // raises nothing has no such cost, and a household plausibly buys two sock aids or a rocker
        // knife for each end of the table, so the guard is scoped to the reason it was written for.
        if (RaisesAlerts(entry)
            && SensorFit.AlreadyInstalled(Ctx.Level, entry.id, fit.hostKind, fit.hostId))
        {
            Ctx.Controller.Status($"There is already a {entry.Label.ToLowerInvariant()} there.");
            return;
        }

        Ctx.RecordEdit("Install " + entry.Label);

        var sensor = SensorCatalog.NewInstance(entry, fit.hostKind, fit.hostId,
                                               fit.position, fit.facingYaw);
        sensor.hostOffset = fit.hostOffset;
        sensor.hostSide = fit.hostSide;

        // A device put down on a surface stores its spot in the HOST'S own frame, so it rides the
        // counter or table when that item is moved or turned. SensorPose reads it back out.
        if (fit.surfaceOffset.HasValue)
            sensor.position = new[] { fit.surfaceOffset.Value.x, fit.surfaceOffset.Value.y };

        Ctx.Level.sensors ??= new List<SensorDef>();
        Ctx.Level.sensors.Add(sensor);

        // reveal: false for the reason FurnitureTool.Place gives. Selecting is a side effect of
        // placing, and jumping to the Select tab after every device would make furnishing a
        // five-bedroom home with forty of them impossible.
        Ctx.Controller.Select(HomeElementMarker.Kind.Sensor, sensor.id, reveal: false);

        if (fit.moved) Ctx.Controller.Status($"Installed {entry.Label}: {fit.reason}");
        Ctx.Changed();
    }

    /// <summary>
    /// Whether this entry can ever put an alert on a caregiver's phone: the one property that
    /// separates a device from an everyday aid everywhere it matters.
    /// </summary>
    /// <remarks>
    /// Asked of the CATALOG's own rules rather than of a category string, so an aid that is later
    /// given a threshold starts being treated as a device with no second place to update. Coverage is
    /// in the test as well as rules because a device can be watched without carrying a rule of its
    /// own: the doorbell has reach and no alert, and it is still a device.
    /// </remarks>
    private static bool RaisesAlerts(SensorCatalog.Entry e)
        => e != null && (e.HasCoverage || SensorDevices.DefaultRules(e.id).Count > 0);

    // ---------------------------------------------------------------------------------------
    // Rail
    // ---------------------------------------------------------------------------------------

    public override void DrawRail()
    {
        if (RefuseIfLocked()) return;

        var cat = Catalog;
        if (cat == null)
        {
            // A scene-wiring fault. It belongs in the console where whoever can fix it is looking, not
            // in the rail where nobody using the app can act on it: the same call FurnitureTool makes.
            Debug.LogWarning("SensorTool: no SensorCatalog is wired to the HomeRenderer, so the device "
                           + "grid is empty. Assign Assets/Resources/SensorCatalog.asset in HomeViz.unity.");
            return;
        }

        bool coverage = Ctx.Controller.SensorCoverageVisible;
        bool showCoverage = UITheme.Toggle("Coverage", coverage,
            "Draw what each device can see, over the plan. The gap at the far end of a "
            + "corridor is a picture; a percentage is not.");
        if (showCoverage != coverage) Ctx.Controller.SensorCoverageVisible = showCoverage;

        UITheme.Gap();

        _search = UITheme.TextRow("Search", _search, "Search everything in the catalog by name");

        // No leading label, for the reason FurnitureTool's row has none.
        var chips = UITheme.ChipRow();
        if (chips.Chip("All", _category == null)) _category = null;
        UITheme.Tip("Everything in the catalog");
        foreach (var c in cat.Categories())
        {
            if (chips.Chip(SensorDevices.SensorCategory.Label(c), _category == c)) _category = c;
            UITheme.Tip($"{SensorDevices.SensorCategory.Label(c)} only");
        }
        if (chips.Chip("Fixtures", _category == FIXTURES)) _category = FIXTURES;
        UITheme.Tip("Grab bars and handrails count as furniture, so they carry no "
                  + "price here. A bar is a change to the building, and it shows in the plan and in "
                  + "the report's room sections.");
        chips.End();

        UITheme.Gap();

        _gridScroll = UITheme.BeginScroll(_gridScroll, GUILayout.Height(GRID_HEIGHT));

        int cols = Mathf.Max(1, Mathf.FloorToInt(UITheme.ContentWidth / (THUMB_SIZE + TILE_GAP)));
        int col = 0;
        GUILayout.BeginHorizontal();

        // Devices, unless the Fixtures chip is the one lit. A SEARCH crosses both: someone typing
        // "grab" wants the grab bars whichever chip happens to be selected, which is the same reason a
        // search already ignores the category.
        if (_category != FIXTURES || Searching)
            foreach (var e in cat.InCategory(Searching || _category == FIXTURES ? null : _category))
            {
                if (!Matches(e, _search)) continue;
                GridCell(ref col, cols);

                if (UITheme.Thumb(Tile(e), e.Label, _selectedId == e.id, THUMB_SIZE))
                {
                    _selectedId = e.id;
                    _fixtureId = null;
                }
                UITheme.Tip(TileTip(e));
            }

        if (_category == FIXTURES || Searching)
            foreach (var f in FixtureEntries())
            {
                if (!MatchesFixture(f, _search)) continue;
                GridCell(ref col, cols);

                if (UITheme.Thumb(FixtureTile(f), f.Label, _fixtureId == f.id, THUMB_SIZE))
                {
                    _fixtureId = f.id;
                    _selectedId = null;
                }
                UITheme.Tip($"{f.Label}: {Units.Format(f.widthM)} wide, mounted at "
                          + $"{Units.Format(f.mountHeightM)}. Click a wall to put one up.");
            }

        GUILayout.EndHorizontal();
        UITheme.EndScroll();

        var fixture = Fixture;
        if (fixture != null) { DrawFixtureDetail(fixture); return; }

        var sel = Selected;
        if (sel == null) return;

        UITheme.Header(sel.Label);

        // The name, what it does, then the figures: the rail-wide order. The where-to-click
        // instruction lives on the grid tile's tooltip, and the vendors are on the cost row's
        // hover so a rail is not a price list.
        UITheme.MutedLine(sel.detects, sel.iddRationale);

        UITheme.Gap();

        if (sel.IsWorn) DrawWearerPicker(sel);

        if (sel.HasCoverage)
            UITheme.Value("Range", Units.Format(sel.coverageRadiusM)
                          + (sel.coverageAngleDeg < 360f ? $" · {sel.coverageAngleDeg:0}°" : " · all round"),
                          "How far it senses, and over how wide an arc. Drawn in the plan while this "
                          + "device is picked.");

        UITheme.Value("Costs", sel.CostLine, CostTip(sel));
    }

    // A worn or personal item has TWO homes, and this row is where you pick. §4.5.1 for the pendant,
    // it is on the person, wherever they are, and the same is true of a key turner, which lives in a
    // pocket. But a home is laid out long before anyone is named, and while a resident was the only
    // answer this row was a dead end: an empty roster refused eight of the twenty-five entries with a
    // warning whose only advice was to go and use a different tab.
    //
    // "Nobody" is the other answer and it is the DEFAULT, because it is the one that always works.
    // It leads the row for that reason, and SensorFit reads it as "put this down on the nearest
    // counter or table": a Furniture host, a labeled box in the plan, the gesture every tile answers.
    //
    // "Worn by" is right for a pendant and wrong for a sock aid, which is used rather than worn, so
    // the label follows whether the thing reports anything. Both mean the same to the schema.
    private void DrawWearerPicker(SensorCatalog.Entry entry)
    {
        bool worn = RaisesAlerts(entry);
        var roster = Ctx.Variant?.occupants;

        var chips = UITheme.ChipRow();
        chips.Label(worn ? "Worn by" : "Belongs to");

        if (chips.Chip("Nobody", string.IsNullOrEmpty(_wearer))) _wearer = null;
        UITheme.Tip("Nobody in particular. Click near a counter or a table and it is put down "
                  + "there, drawn as a labeled box like every other device. No resident needed.");

        if (roster != null)
            foreach (var person in roster)
            {
                if (person == null || !person.included) continue;
                if (chips.Chip(person.name ?? "Resident", _wearer == person.id)) _wearer = person.id;
                UITheme.Tip(worn ? $"{person.name} wears this one" : $"{person.name} uses this one");
            }
        chips.End();
    }

    /// <summary>Wraps the grid to a new row when the current one is full. Shared by both passes so a
    /// mixed search result tiles as one grid rather than as two that each start a row.</summary>
    private static void GridCell(ref int col, int cols)
    {
        if (col >= cols) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); col = 0; }
        col++;
    }

    private IEnumerable<FurnitureCatalog.Entry> FixtureEntries()
    {
        var cat = Fixtures;
        if (cat == null) yield break;

        foreach (var id in FixtureIds)
        {
            var e = cat.Get(id);
            // A missing key is a catalog someone edited, not a bug worth throwing over: the chip
            // simply offers one fewer bar.
            if (e != null) yield return e;
        }
    }

    private static bool MatchesFixture(FurnitureCatalog.Entry e, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        string f = filter.Trim();
        return (e.Label ?? "").IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0
            || (e.id ?? "").IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // No cost row, and that is the honest answer rather than an omission: FurnitureCatalog carries no
    // price at all, and a bar is a change to the BUILDING. It belongs in the plan and in the report's
    // room sections, not in a technology total a funder reads as equipment. Printing $0 beside a
    // device that costs $18 would be worse than saying nothing.
    private void DrawFixtureDetail(FurnitureCatalog.Entry f)
    {
        UITheme.Header(f.Label);

        UITheme.Value("Size", Units.Format(f.widthM) + " × " + Units.Format(f.depthM),
                      "Its true size, which is what the plan draws and what the fit tests against a "
                    + "doorway.");
        UITheme.Value("Mounts at", Units.Format(f.mountHeightM),
                      "Height above the floor to the center of the bar. Change it after placing, in "
                    + "the Select tab.");

        UITheme.MutedLine("Click a wall to put one up. It slides clear of any doorway it would cross.",
                          "Stored as a wall mount, like the grab bars the Furnish tab places. It "
                        + "appears in the change list as one, and either tab can move it later.");
    }

    // ---------------------------------------------------------------------------------------
    // Overlay
    // ---------------------------------------------------------------------------------------

    // The Furnish rail's wall-mount ghost, drawn from the same helper for the same reason the
    // placement is: the preview has to show where the fit will actually put the bar, not where the
    // cursor is, or the click breaks a promise the ghost made.
    private void DrawFixtureGhost(FurnitureCatalog.Entry f)
    {
        float y = Ctx.Level.elevation;

        if (_hoverWall == null)
        {
            if (OverlayDraw.ToScreen(Ctx.Cam, _cursor, y, out Vector2 none))
                OverlayDraw.Readout(none, "Move closer to a wall to mount this.");
            return;
        }

        Vector2 at = MountPlacement.Ghost(f, _hoverOffset, _hoverWall, Ctx.Level);
        if (!OverlayDraw.ToScreen(Ctx.Cam, at, y, out Vector2 g)) return;

        OverlayDraw.Dot(g, 12f, new Color(0.95f, 0.75f, 0.25f));
        OverlayDraw.Readout(g, f.Label + " · " + Units.Format(f.mountHeightM) + " AFF");
    }

    public override void DrawOverlay()
    {
        if (Ctx?.Cam == null || Ctx.Level == null || !_hasCursor) return;

        var fixture = Fixture;
        if (fixture != null) { DrawFixtureGhost(fixture); return; }

        var entry = Selected;
        if (entry == null) return;

        var fit = SensorFit.Fit(entry.id, _cursor, Ctx.Level, Ctx.Variant, _wearer);
        float y = Ctx.Level.elevation;

        // A refusal is shown at the cursor rather than swallowed. The rail is where someone reads a
        // number; the plan is where they are looking while they aim, which is the same reason the
        // calibration prompt is mirrored onto the image.
        if (!fit.ok)
        {
            if (OverlayDraw.ToScreen(Ctx.Cam, _cursor, y, out Vector2 bad))
                OverlayDraw.Readout(bad, fit.reason);
            return;
        }

        // Asked of the FIT, not of the entry: a personal item is only placeless when a resident was
        // actually named, and the same entry left unassigned lands in a room and gets the ordinary
        // ghost below. Reading entry.IsWorn here would have promised "click to assign" over a click
        // that was about to draw a box.
        if (fit.hostKind == SensorHost.Occupant)
        {
            if (OverlayDraw.ToScreen(Ctx.Cam, _cursor, y, out Vector2 w))
                OverlayDraw.Readout(w, entry.Label + " · worn, click to assign");
            return;
        }

        var color = new Color(0.30f, 0.62f, 0.90f);

        // The ghost is drawn where the device will actually END UP (snapped to its host) for the
        // same reason FurnitureTool's is: a preview that ignored the snap is a promise the click
        // breaks. And it draws the coverage, because that is what the placement decision is about.
        if (OverlayDraw.ToScreen(Ctx.Cam, fit.position, y, out Vector2 at))
        {
            OverlayDraw.Dot(at, 12f, color);
            OverlayDraw.Readout(at, entry.Label + " · " + HostPhrase(fit));
        }

        if (entry.HasCoverage)
            SensorOverlay.DrawCone(Ctx.Cam, fit.position, y, entry.coverageRadiusM,
                                   entry.coverageAngleDeg, fit.coneYaw, color);
    }

    private static string HostPhrase(SensorFit.Result fit)
        => "on " + SensorHost.Label(fit.hostKind);

    // ---------------------------------------------------------------------------------------
    // The picker grid
    // ---------------------------------------------------------------------------------------

    private const float THUMB_SIZE = 76f;
    private const float TILE_GAP = UITheme.TileGap;
    private const float GRID_HEIGHT = UITheme.GridHeight;

    private bool Searching => !string.IsNullOrWhiteSpace(_search);

    private static bool Matches(SensorCatalog.Entry e, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        string f = filter.Trim();
        return (e.Label ?? "").IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0
            || (e.id ?? "").IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0
            || (e.detects ?? "").IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string TileTip(SensorCatalog.Entry e)
    {
        // Range for anything that senses at a distance, the price, then where a click puts it,
        // the instruction that used to sit in its own rail row.
        string what = e.HasCoverage
            ? $"Range {Units.Format(e.coverageRadiusM)}"
              + (e.coverageAngleDeg < 360f ? $" over {e.coverageAngleDeg:0}°" : " all round") + ". "
            : "";

        string place = e.hostKind switch
        {
            SensorHost.Opening => "Click a doorway to install it.",
            SensorHost.Wall => "Click a wall to install it.",
            SensorHost.Furniture => "Click the counter, table or furniture it sits on.",
            SensorHost.Point => "Click the floor near a sink, a toilet or a bath.",
            SensorHost.Occupant => "Assign it to a resident, or click near a counter or a table "
                                 + "to put it down.",
            _ => "Click inside a room to install it.",
        };

        return $"{e.Label} · {e.PurchaseRange}. {what}{place}";
    }

    private Texture Tile(SensorCatalog.Entry e)
    {
        var prefab = Ctx?.Renderer?.FindPrefab(e.id);
        if (prefab != null)
        {
            var tex = ThumbnailCache.GetPrefab(prefab);
            if (tex != null) return tex;
        }
        return Drawn(e);
    }

    private static readonly Dictionary<string, Texture2D> _tiles = new Dictionary<string, Texture2D>();

    private const int TILE_PX = 64;
    // The longest reach in the catalog is the PIR sensor's 9.1 m. Everything WITH REACH is drawn
    // against that one fixed span rather than normalised per tile, for the reason the furniture grid
    // is: the point is that a motion sensor and a door sensor do NOT see the same amount of a home.
    private const float REACH_SPAN_M = 9.6f;
    // And everything without it against this one. The biggest installed item with no reach is the
    // 0.51 m bed pad, so 0.6 spends the tile on the range that actually varies: at the 9.6 m span
    // every one of them rounds to the same dot, which is the bug this whole split fixes.
    private const float SIZE_SPAN_M = 0.6f;

    /// <summary>
    /// The tile for an entry with no art: its reach, its footprint or its colour, whichever actually
    /// distinguishes it. See the three modes in the file header.
    /// </summary>
    private static Texture2D Drawn(SensorCatalog.Entry e)
    {
        if (_tiles.TryGetValue(e.id, out var cached) && cached != null) return cached;

        var tex = new Texture2D(TILE_PX, TILE_PX) { hideFlags = HideFlags.HideAndDontSave };

        Color ink = e.swatch;
        Color paper = new Color(ink.r, ink.g, ink.b, 0.16f);

        var px = new Color[TILE_PX * TILE_PX];
        for (int i = 0; i < px.Length; i++) px[i] = paper;

        int c = TILE_PX / 2;

        if (e.HasCoverage) PaintReach(px, e, c);
        else if (e.IsWorn) PaintBlock(px, ink);
        else PaintFootprint(px, e, ink, c);

        // The thing itself, always drawn, so a door sensor with no reach at all is still a mark rather
        // than an empty tile, and so a footprint smaller than the mark still reads as an object.
        // Skipped on a worn block, which is already solid.
        if (!e.IsWorn)
            for (int y = c - 2; y <= c + 2; y++)
            for (int x = c - 2; x <= c + 2; x++)
                if (x >= 0 && x < TILE_PX && y >= 0 && y < TILE_PX) px[y * TILE_PX + x] = ink;

        tex.SetPixels(px);
        tex.filterMode = FilterMode.Point;
        tex.Apply();

        _tiles[e.id] = tex;
        return tex;
    }

    private static void PaintReach(Color[] px, SensorCatalog.Entry e, int c)
    {
        Color ink = e.swatch;
        float radiusPx = 0.5f * TILE_PX * Mathf.Clamp01(e.coverageRadiusM / REACH_SPAN_M);
        float half = 0.5f * Mathf.Clamp(e.coverageAngleDeg <= 0f ? 360f : e.coverageAngleDeg, 1f, 360f);
        if (radiusPx < 1f) return;

        // The cone is drawn opening UPWARD, which is the direction the plan overlay uses for a
        // yaw of zero, so the tile and the plan describe the same shape.
        for (int y = 0; y < TILE_PX; y++)
        for (int x = 0; x < TILE_PX; x++)
        {
            float dx = x - c, dy = y - c;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            if (d > radiusPx) continue;
            if (half < 180f && d > 0.5f)
            {
                float a = Mathf.Abs(Mathf.Atan2(dx, dy) * Mathf.Rad2Deg);
                if (a > half) continue;
            }
            px[y * TILE_PX + x] = new Color(ink.r, ink.g, ink.b, 0.55f);
        }
    }

    // The plan footprint at true scale against SIZE_SPAN_M. FurnitureTool.Plan, one order of
    // magnitude down. Deliberately NOT normalised per tile: the whole point is that a bin and a door
    // sensor are not the same size.
    private static void PaintFootprint(Color[] px, SensorCatalog.Entry e, Color ink, int c)
    {
        int hw = Mathf.Max(1, Mathf.RoundToInt(0.5f * TILE_PX * Mathf.Clamp01(e.widthM / SIZE_SPAN_M)));
        int hd = Mathf.Max(1, Mathf.RoundToInt(0.5f * TILE_PX * Mathf.Clamp01(e.depthM / SIZE_SPAN_M)));

        for (int y = c - hd; y <= c + hd; y++)
        for (int x = c - hw; x <= c + hw; x++)
            if (x >= 0 && x < TILE_PX && y >= 0 && y < TILE_PX)
                px[y * TILE_PX + x] = new Color(ink.r, ink.g, ink.b, 0.55f);
    }

    // A worn item gets the entry's own colour, with the caption carrying the name. The argument used
    // to be that nothing is ever drawn for one, and half of that is no longer true: an unassigned
    // personal item is put down in a room and rendered like any other device. The half that decides
    // this still holds: the largest of them is a 0.30 m sock aid and the smallest a 0.01 m zipper
    // pull, so against SIZE_SPAN_M every one of them rounds to the same handful of pixels, which is
    // the identical-dot failure the three modes exist to avoid.
    //
    // Inset rather than edge to edge, so a grid of them still reads as tiles rather than as one block
    // of colour.
    private static void PaintBlock(Color[] px, Color ink)
    {
        const int INSET = 12;
        for (int y = INSET; y < TILE_PX - INSET; y++)
        for (int x = INSET; x < TILE_PX - INSET; x++)
            px[y * TILE_PX + x] = new Color(ink.r, ink.g, ink.b, 0.75f);
    }

    // A fixture's own span. 1.3 m rather than the devices' 0.6, because the longest bar is a 1.22 m
    // handrail, and rather than the furniture grid's 2.3, because at that scale all three bars clamp
    // to slivers of the same width and the tile says nothing. Here a 24" bar is half the tile, a 36"
    // bar is three quarters and a handrail fills it, which is the whole difference between them.
    private const float FIXTURE_SPAN_M = 1.3f;

    private static Texture2D FixtureTile(FurnitureCatalog.Entry f)
    {
        string key = "fx:" + f.id;
        if (_tiles.TryGetValue(key, out var cached) && cached != null) return cached;

        var tex = new Texture2D(TILE_PX, TILE_PX) { hideFlags = HideFlags.HideAndDontSave };

        Color ink = f.swatch;
        var px = new Color[TILE_PX * TILE_PX];
        for (int i = 0; i < px.Length; i++) px[i] = new Color(ink.r, ink.g, ink.b, 0.16f);

        int c = TILE_PX / 2;
        int hw = Mathf.Max(1, Mathf.RoundToInt(0.5f * TILE_PX * Mathf.Clamp01(f.widthM / FIXTURE_SPAN_M)));
        int hd = Mathf.Max(2, Mathf.RoundToInt(0.5f * TILE_PX * Mathf.Clamp01(f.depthM / FIXTURE_SPAN_M)));

        for (int y = c - hd; y <= c + hd; y++)
        for (int x = c - hw; x <= c + hw; x++)
            if (x >= 0 && x < TILE_PX && y >= 0 && y < TILE_PX) px[y * TILE_PX + x] = ink;

        tex.SetPixels(px);
        tex.filterMode = FilterMode.Point;
        tex.Apply();

        _tiles[key] = tex;
        return tex;
    }

    private static string CostTip(SensorCatalog.Entry e)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Purchase ").Append(e.PurchaseRange);
        if (!string.IsNullOrEmpty(e.MonthlyRange)) sb.Append(", plus ").Append(e.MonthlyRange);
        else sb.Append(". The system's monthly fee sits on the hub");
        sb.Append(".");

        if (e.vendors != null && e.vendors.Count > 0)
        {
            sb.Append("\n\nFrom the report §").Append(e.reportSection).Append(":");
            foreach (var v in e.vendors) sb.Append("\n• ").Append(v.Line);
        }
        return sb.ToString();
    }
}
