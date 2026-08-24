using UnityEngine;

// Shared IMGUI theme for every runtime tool panel (LibraryBrowser, EditController,
// TileBuildingEditor, BakePass, ModelRequesterUI).
//
// This is the Unity port of Assets/Redesign.html: the "calmer, clearer interface" visual
// target (Direction B: docked rails, light frosted-paper panels). Every panel renders through
// this one class, so reskinning here reskins the whole tool. The color/size tokens below are the
// literal values from the redesign's design system, so they line up 1:1 with its CSS variables.
//
// Usage inside OnGUI:
//   UITheme.PanelBackground(rect);                 // rounded frosted-paper card behind the panel
//   GUILayout.BeginArea(UITheme.Inset(rect));      // content inset with padding
//   UITheme.Title("…"); UITheme.Header("…"); …
//   GUILayout.EndArea();
//
// Newer component helpers mirror the redesign's kit and are opt-in:
//   UITheme.Segmented(sel, new[]{"Move","Rotate","Scale"});   // segmented control
//   UITheme.PrimaryButton("Generate"); UITheme.GhostButton("Cancel");
//   UITheme.Chip("Nature", active); UITheme.StatusBadge("Server connected", ok:true);
//
// Calling any UITheme member installs the skin for the remainder of the current OnGUI pass.
public static class UITheme
{
    // ---- layout constants (shared so panels line up) ----
    public const int LeftPanelWidth  = 320;   // LibraryBrowser
    public const int RightPanelWidth = 300;   // EditController / TileBuildingEditor (redesign right rail)
    public const int Margin          = 10;
    public const int Pad             = 14;    // inner padding inside a panel card (redesign: 14-17px)
    public const float RowH          = 26f;   // standard control height
    public const float PrimaryH      = 44f;   // primary target / command-bar height (redesign)
    // Unity's built-in vertical scrollbar plus its margin. Content sized to a scroll view's FULL width
    // overflows by exactly this much the moment the bar appears, which is the commonest way an IMGUI
    // panel grows a horizontal scrollbar it never asked for. BeginScroll reserves it.
    public const float ScrollbarW    = 16f;
    // Top band reserved for the centered command bar. Both docked rails start below it so the bar
    // never overlaps a rail at any window width.
    public static float RailTop => Margin + PrimaryH + Pad * 2f + 6f;

    // ---- the spacing scale ----
    //
    // Three steps, and nothing else. The rails used to carry nine different Space() values picked by
    // eye per file, stacked on top of margins the styles already had, so the gap between a button and
    // the header under it was a different number in every tool. Header and Foldout now OWN the space
    // above them (SectionH in their margin), so a caller never puts a Space before either; between
    // controls of one group it is Gap, and GapTight only where two things belong visibly together.
    public const float GapTightH = 4f;
    public const float GapH      = 8f;
    public const float SectionH  = 14f;
    public static void GapTight() => GUILayout.Space(GapTightH);
    public static void Gap()      => GUILayout.Space(GapH);
    public static void Section()  => GUILayout.Space(SectionH);

    // ---- one size for a glyph button, and the reserve it needs on a row ----
    //
    // Every ✕ in the app was drawn at 24, 26 or 28 px beside a reserve of 32 or 34: the same button,
    // three widths, and a few pixels of slack on each row that nothing accounted for.
    public const float GlyphW = 26f;
    /// <summary>What a glyph button takes off a row: its width plus the button style's margin.</summary>
    public static float GlyphReserve { get { Ensure(); return GlyphW + _skin.button.margin.horizontal; } }

    // ---- one shape for a thumbnail grid ----
    // Three visually identical grids (furniture, equipment, PDF pages) shipped at three heights and
    // two tile gaps.
    public const float GridHeight = 280f;
    public const float TileGap    = 6f;

    // ---- palette (literal tokens from Redesign.html) ----
    // Ink ramp
    public static readonly Color Ink        = Hex(0x1F2228);  // --ink   near-black text
    public static readonly Color Ink2       = Hex(0x6B7177);  // --ink2  secondary text
    public static readonly Color Ink3       = Hex(0x9AA0A6);  // --ink3  hint / tertiary text
    // Surfaces
    public static readonly Color PanelCard  = new(0.988f, 0.988f, 0.984f, 0.985f); // --panel #FCFCFB, opaque over scene
    public static readonly Color Field      = Hex(0xFFFFFF);  // --field input background
    // Darker than the redesign's --btn (#F1F1EE): that fill is two hex points off the mode band's Tile
    // wash and four off the card, so a secondary button read as a caption in a faint box. A button has
    // to be visibly a different surface from the paper it sits on.
    public static readonly Color Btn        = Hex(0xE9E9E4);  // secondary button
    public static readonly Color BtnHover   = Hex(0xDDDDD7);
    public static readonly Color Tile       = Hex(0xEFEEE9);  // --tile  segmented track / inset
    public static readonly Color Tile2      = Hex(0xE6E5DF);  // --tile2
    // Accent + tint
    public static readonly Color Accent     = Hex(0x2E63C8);  // --accent
    public static readonly Color AccentInk  = Hex(0x1C4BA0);  // --accent-ink
    public static readonly Color Tint       = Hex(0xEAF1FC);  // --tint   active-row wash
    public static readonly Color TintLine   = Hex(0xBCD2F4);  // --tint-line
    public static readonly Color Ok         = Hex(0x2E9E6B);  // --ok
    public static readonly Color Danger     = Hex(0xB3261E);  // delete red
    // Amber, between Ok and Danger: "this is consequential but not destructive". Its one job is the
    // mode band's RECORDING THE EXISTING HOME state. Editing the record of reality is not an error,
    // so Danger would be a lie, and it is not routine either, so Accent would be too.
    public static readonly Color Warn       = Hex(0xB4711A);
    public static readonly Color WarnTint   = Hex(0xFBF1E0);  // the band wash for the above
    // Hairlines
    public static readonly Color Line       = new(0.078f, 0.086f, 0.110f, 0.09f);  // --line
    public static readonly Color Line2       = new(0.078f, 0.086f, 0.110f, 0.14f); // --line2
    // The rim of anything you can press or type into. Line2 stays for card edges and dividers; at 14 %
    // it is too faint to separate a button from the card behind it, which is the whole of "the buttons
    // look like text".
    public static readonly Color BtnLine    = new(0.078f, 0.086f, 0.110f, 0.22f);

    // ---- back-compat aliases (older call sites used these names on the dark theme) ----
    public static readonly Color Panel      = PanelCard;
    public static readonly Color Inner      = Field;
    public static readonly Color AccentDim  = AccentInk;
    public static readonly Color TextColor  = Ink;
    public static readonly Color MutedColor = Ink2;
    public static readonly Color DividerCol = Line;

    static GUISkin   _skin;
    static GUIStyle  _title, _header, _sub;
    static GUIStyle  _cardStyle, _primary, _ghost, _chip, _chipOn, _segment;
    static Texture2D _cardTex, _maskTex, _btnTex, _btnHover, _accentTex, _fieldTex,
                     _tileTex, _tintTex, _white,
                     _outlineTex, _dangerTex, _dangerHoverTex, _bandTex, _warnBadgeTex,
                     _roundBtnTex, _roundBtnHoverTex;
    static bool      _building;

    // ---- fonts (Public Sans for UI, IBM Plex Mono for numbers) ----
    // Loaded from Resources/Fonts at first Build(); fall back to the built-in font if missing.
    static Font _sans, _sansMedium, _sansSemi, _mono;
    static bool _fontsLoaded;

    static Font LoadFont(string name)
    {
        var f = Resources.Load<Font>("Fonts/" + name);
        return f;
    }

    static void EnsureFonts()
    {
        if (_fontsLoaded) return;
        _fontsLoaded = true;
        _sans       = LoadFont("PublicSans-Regular");
        _sansMedium = LoadFont("PublicSans-Medium")   ?? _sans;
        _sansSemi   = LoadFont("PublicSans-SemiBold") ?? _sansMedium ?? _sans;
        _mono       = LoadFont("IBMPlexMono-Medium")  ?? LoadFont("IBMPlexMono-Regular");
    }

    // IBM Plex Mono style for numeric readouts; falls back to the mono label style when unavailable.
    static GUIStyle _num, _numSmall;
    public static Font MonoFont { get { EnsureFonts(); return _mono; } }

    static Color Hex(int rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

    // Builds (once) and installs the skin for this OnGUI pass.
    static void Ensure()
    {
        if (_skin == null) Build();
        if (!_building) GUI.skin = _skin;
    }

    static Texture2D Solid(Color c)
    {
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        t.SetPixel(0, 0, c); t.Apply();
        return t;
    }

    // Anti-aliased rounded-rect texture, 9-slice friendly. When borderCol.a > 0 a 1px inner
    // border is baked along the rounded edge. Use the matching radius as the GUIStyle.border.
    static Texture2D Rounded(int radius, Color fill, Color borderCol = default)
    {
        int size = radius * 2 + 6;
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        var px = new Color[size * size];
        float r = radius;
        bool hasBorder = borderCol.a > 0.001f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            // distance from the nearest straight edge into the rounded corner
            float cx = Mathf.Clamp(x + 0.5f, r, size - r);
            float cy = Mathf.Clamp(y + 0.5f, r, size - r);
            float d  = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
            float cover = Mathf.Clamp01(r - d + 0.5f);     // 1 inside, 0 outside, AA on the edge
            Color c = fill;
            if (hasBorder)
            {
                float edge = Mathf.Clamp01(d - (r - 1.5f)); // 0 interior → 1 at the rim
                c = Color.Lerp(fill, borderCol, edge);
            }
            c.a *= cover;
            px[y * size + x] = c;
        }
        t.SetPixels(px); t.Apply();
        return t;
    }

    static void Build()
    {
        _building = true;   // guard: don't reassign GUI.skin while cloning from it

        _white     = Solid(Color.white);
        _cardTex   = Rounded(13, PanelCard, Line2);
        _maskTex   = Rounded(13, Color.white);
        _btnTex    = Rounded(7, Btn,    BtnLine);
        _btnHover  = Rounded(7, BtnHover, BtnLine);
        // The circular glyph button's face: at GlyphW (26) square, a radius-13 nine-slice IS the
        // circle: same fill and rim as a SecondaryButton, so it still reads as a button.
        _roundBtnTex      = Rounded(13, Btn,      BtnLine);
        _roundBtnHoverTex = Rounded(13, BtnHover, BtnLine);
        _accentTex = Rounded(7, Accent);
        _fieldTex  = Rounded(7, Field,  BtnLine);
        _tileTex   = Rounded(7, Tile,   Line);
        _tintTex   = Rounded(7, Tint,   TintLine);
        // Outline buttons: a zero-alpha interior and an opaque rim. Rounded lerps the fill towards the
        // border over the last pixel and a half and multiplies alpha by coverage, so a transparent fill
        // with an opaque border bakes to a clean ring with nothing inside it.
        _outlineTex     = Rounded(7, new Color(Btn.r, Btn.g, Btn.b, 0f), BtnLine);
        _dangerTex      = Rounded(7, new Color(Danger.r, Danger.g, Danger.b, 0f),
                                     new Color(Danger.r, Danger.g, Danger.b, 0.55f));
        _dangerHoverTex = Rounded(7, new Color(Danger.r, Danger.g, Danger.b, 0.08f),
                                     new Color(Danger.r, Danger.g, Danger.b, 0.55f));
        // White on a tinted strip: the mode band's washes are the same tone as Btn, so a secondary
        // button there was a button in name only.
        _bandTex        = Rounded(7, Field, BtnLine);
        // The read-only badge: the amber wash with an amber rim, so a refusal reads as a warning
        // rather than as a caption (the hollow-dot StatusBadge it replaced was set in the hint grey).
        _warnBadgeTex   = Rounded(7, WarnTint, new Color(Warn.r, Warn.g, Warn.b, 0.55f));

        EnsureFonts();

        _skin = Object.Instantiate(GUI.skin);
        _skin.hideFlags = HideFlags.HideAndDontSave;
        if (_sans != null) _skin.font = _sansMedium;   // Public Sans Medium as the base UI face

        // Label: 13 / medium per the redesign type scale
        var l = _skin.label;
        l.fontSize = 13; l.wordWrap = true;
        if (_sansMedium != null) l.font = _sansMedium;
        l.normal.textColor = Ink;
        l.padding = new RectOffset(2, 2, 3, 3);
        l.margin  = new RectOffset(2, 2, 1, 1);

        // Button. Label & button text 13 / medium; accent fill when pressed / "on" (toggle-as-button)
        var b = _skin.button;
        b.fontSize = 13; b.fontStyle = FontStyle.Normal;
        if (_sansMedium != null) b.font = _sansMedium;
        b.alignment = TextAnchor.MiddleCenter;
        b.padding = new RectOffset(10, 10, 6, 6);
        b.margin  = new RectOffset(3, 3, 3, 3);
        b.border  = new RectOffset(8, 8, 8, 8);
        SetBg(b.normal,   _btnTex,    Ink);
        SetBg(b.hover,    _btnHover,  Ink);
        SetBg(b.active,   _accentTex, Color.white);
        SetBg(b.focused,  _btnTex,    Ink);
        SetBg(b.onNormal, _accentTex, Color.white);
        SetBg(b.onHover,  _accentTex, Color.white);
        SetBg(b.onActive, _accentTex, Color.white);

        // Toggle (used as list-selection text and tab buttons)
        var t = _skin.toggle;
        t.fontSize = 12; t.wordWrap = false;
        t.normal.textColor   = Ink2;   t.hover.textColor   = Ink;
        t.onNormal.textColor = AccentInk; t.onHover.textColor = AccentInk;
        t.margin = new RectOffset(2, 2, 2, 2);

        // Text field. White field, hairline border
        var tf = _skin.textField;
        tf.fontSize = 12;
        tf.padding = new RectOffset(8, 8, 5, 5);
        tf.margin  = new RectOffset(3, 3, 3, 3);
        tf.border  = new RectOffset(8, 8, 8, 8);
        tf.normal.textColor = tf.focused.textColor = tf.hover.textColor = Ink;
        SetBg(tf.normal,  _fieldTex, Ink);
        SetBg(tf.focused, _fieldTex, Ink);
        SetBg(tf.hover,   _fieldTex, Ink);

        // Box / scroll surfaces. Soft tile inset
        _skin.box.normal.background = _tileTex;
        _skin.box.normal.textColor  = Ink2;
        _skin.box.border = new RectOffset(8, 8, 8, 8);
        _skin.horizontalSlider.margin = new RectOffset(3, 3, 9, 4);

        // Derived label styles
        // Title: 15 / semibold
        _title = new GUIStyle(l) { fontSize = 15, fontStyle = FontStyle.Bold, margin = new RectOffset(2, 2, 2, 8) };
        if (_sansSemi != null) { _title.font = _sansSemi; _title.fontStyle = FontStyle.Normal; }
        _title.normal.textColor = Ink;

        // Section header: 11 / bold, UPPERCASE, letter-spaced look (caps applied in Header())
        // A header OWNS the space above it (SectionH), so no caller puts a Space before one: the
        // old 10 px margin plus a Space(8|10) at every call site was two gaps that nobody added up.
        _header = new GUIStyle(l) { fontSize = 11, fontStyle = FontStyle.Bold, margin = new RectOffset(2, 2, (int)SectionH, 4) };
        if (_sansSemi != null) { _header.font = _sansSemi; _header.fontStyle = FontStyle.Normal; }
        _header.normal.textColor = AccentInk;

        // Hint / helper copy: 11 / regular
        _sub = new GUIStyle(l) { fontSize = 11 };
        if (_sans != null) _sub.font = _sans;
        _sub.normal.textColor = Ink2;

        // Numeric readouts. IBM Plex Mono
        _num = new GUIStyle(l) { fontSize = 13, alignment = TextAnchor.MiddleRight };
        if (_mono != null) _num.font = _mono;
        _num.normal.textColor = Ink;
        _numSmall = new GUIStyle(_num) { fontSize = 11 };
        _numSmall.normal.textColor = Ink3;

        // ---- component styles ----
        _cardStyle = new GUIStyle { border = new RectOffset(14, 14, 14, 14) };
        _cardStyle.normal.background = _cardTex;

        _primary = new GUIStyle(b);
        SetBg(_primary.normal,  _accentTex, Color.white);
        SetBg(_primary.hover,   _accentTex, Color.white);
        SetBg(_primary.active,  _accentTex, Color.white);
        SetBg(_primary.focused, _accentTex, Color.white);

        // OUTLINE, not borderless. A ghost button used to be Ink2 text with no background: the exact
        // look of a caption, and it drew Export, Star, Cancel, Undo and Redo. Nothing on screen said
        // those were buttons until a cursor crossed them. The rim is the affordance; the fill stays
        // clear so the hierarchy against Primary and Secondary is kept.
        _ghost = new GUIStyle(b);
        SetBg(_ghost.normal,  _outlineTex, Ink);
        SetBg(_ghost.hover,   _btnHover,   Ink);
        SetBg(_ghost.active,  _btnHover,   Ink);
        SetBg(_ghost.focused, _outlineTex, Ink);

        // An unlit chip is a control, not a hint, so it is set in Ink rather than the caption colour.
        _chip = new GUIStyle(b) { fontSize = 11, padding = new RectOffset(12, 12, 5, 5) };
        SetBg(_chip.normal, _btnTex, Ink); SetBg(_chip.hover, _btnHover, Ink);
        _chipOn = new GUIStyle(_chip);
        SetBg(_chipOn.normal, _accentTex, Color.white); SetBg(_chipOn.hover, _accentTex, Color.white);

        _segment = new GUIStyle(_skin.button) { fontSize = 12, fontStyle = FontStyle.Bold, margin = new RectOffset(0, 0, 0, 0) };

        _building = false;
        GUI.skin = _skin;
    }

    static void SetBg(GUIStyleState s, Texture2D bg, Color text) { s.background = bg; s.textColor = text; }

    // ---- drawing helpers ----

    // Rounded frosted-paper card behind a panel. Call before GUILayout.BeginArea(Inset(rect)).
    public static void PanelBackground(Rect rect)
    {
        Ensure();
        var prev = GUI.color;
        // soft drop shadow
        GUI.color = new Color(0.08f, 0.09f, 0.11f, 0.16f);
        GUI.Box(new Rect(rect.x - 2, rect.y + 4, rect.width + 4, rect.height + 2), GUIContent.none, _shadowStyle);
        // the card
        GUI.color = Color.white;
        GUI.Box(rect, GUIContent.none, _cardStyle);
        GUI.color = prev;
    }

    /// <summary>
    /// A flat tinted strip with a hairline along its bottom edge. Chrome that reads as continuous
    /// with the command bar above it, not as a card floating over the scene.
    ///
    /// Deliberately NOT PanelBackground: that draws a rounded card and a drop shadow, which would
    /// make the band look like one more panel to dismiss. The band is the window's own frame, and it
    /// has to read as permanent.
    /// </summary>
    public static void BandBackground(Rect rect, Color tone)
    {
        Ensure();
        var prev = GUI.color;
        GUI.color = tone;
        GUI.DrawTexture(rect, _white);
        GUI.color = Line2;
        GUI.DrawTexture(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), _white);
        GUI.color = prev;
    }

    /// <summary>A solid 1×1 texture for callers that paint their own rects (bars, strips, markers).</summary>
    public static Texture2D Pixel { get { Ensure(); return _white; } }

    // a maskTex-backed style reused for shadows / rounded fills
    static GUIStyle _shadowStyleCache;
    static GUIStyle _shadowStyle
    {
        get
        {
            if (_shadowStyleCache == null)
            {
                _shadowStyleCache = new GUIStyle { border = new RectOffset(14, 14, 14, 14) };
                _shadowStyleCache.normal.background = _maskTex;
            }
            return _shadowStyleCache;
        }
    }

    // Content rect inset by Pad on all sides.
    public static Rect Inset(Rect rect) =>
        new(rect.x + Pad, rect.y + Pad, rect.width - Pad * 2, rect.height - Pad * 2);

    // ---- how wide is the thing I am drawing into? ----
    //
    // Every "it does not fit" bug in this UI came from the same hole: IMGUI cannot tell a helper how
    // much room it has during the Layout pass, so nothing ever measured a string before drawing it and
    // long names were cut mid-glyph. But we OWN every container here: each one is a fixed-width rail
    // wrapped in BeginArea(Inset(rect)), so the width is knowable, it just was never published.
    //
    // A stack rather than a field because these nest: a rail inside a scroll view inside a panel, and
    // each level takes a little more off. Push/Pop pair with the Begin/End wrappers below; a caller
    // that uses raw BeginArea simply gets the default, which is what keeps the Site tool working
    // untouched.
    static readonly System.Collections.Generic.List<float> _widths = new();

    /// <summary>Content width available at this point in the layout, in pixels.</summary>
    public static float ContentWidth =>
        _widths.Count > 0 ? _widths[_widths.Count - 1] : RightPanelWidth - Pad * 2;

    /// <summary>
    /// Whether a container has actually published its width, as opposed to <see cref="ContentWidth"/>
    /// handing back its default. The distinction matters: a helper that fits itself to a GUESSED width
    /// inside an unconverted panel is worse than one that does not try, so the fitting helpers use this
    /// to stay out of the way of callers that predate the stack: the Site tool, in practice.
    /// </summary>
    public static bool HasWidth => _widths.Count > 0;

    public static void PushWidth(float w) => _widths.Add(Mathf.Max(1f, w));

    public static void PopWidth()
    {
        if (_widths.Count > 0) _widths.RemoveAt(_widths.Count - 1);
    }

    /// <summary>
    /// Clears the width stack. Called at the top of a panel-owner's OnGUI so an exception thrown
    /// mid-layout in one frame cannot leave a pushed width stranded and mis-size every later frame.
    /// </summary>
    public static void ResetWidths() => _widths.Clear();

    /// <summary>Frosted card + inset content area, publishing its width. Pair with EndPanel.</summary>
    public static void BeginPanel(Rect rect)
    {
        Ensure();
        PanelBackground(rect);
        var inner = Inset(rect);
        GUILayout.BeginArea(inner);
        PushWidth(inner.width);
    }

    public static void EndPanel()
    {
        GUILayout.EndArea();
        PopWidth();
    }

    /// <summary>
    /// A bare content area that publishes its width: for chrome that paints its own background
    /// (<see cref="BandBackground"/>) rather than a card. Pair with EndRegion.
    /// </summary>
    public static void BeginRegion(Rect rect)
    {
        Ensure();
        GUILayout.BeginArea(rect);
        PushWidth(rect.width);
    }

    public static void EndRegion()
    {
        GUILayout.EndArea();
        PopWidth();
    }

    /// <summary>
    /// A scroll view that can never grow a HORIZONTAL scrollbar, and that tells everything drawn
    /// inside it how much room is left.
    ///
    /// Two halves, and both are needed. GUIStyle.none as the horizontal scrollbar clamps the content
    /// width to the viewport, so wide content clips instead of scrolling sideways: the idiom the
    /// legacy Site rail and FurnitureTool already use. Reserving ScrollbarW is the other half: without
    /// it, content laid out to the full view width overflows by exactly the vertical bar's width the
    /// moment that bar appears, which is the horizontal scrollbar appearing to come from nowhere.
    /// </summary>
    public static Vector2 BeginScroll(Vector2 scroll, params GUILayoutOption[] opts)
    {
        Ensure();
        var next = GUILayout.BeginScrollView(scroll, false, false,
                                             GUIStyle.none, _skin.verticalScrollbar, _skin.scrollView,
                                             opts);
        PushWidth(ContentWidth - ScrollbarW);
        return next;
    }

    public static void EndScroll()
    {
        GUILayout.EndScrollView();
        PopWidth();
    }

    // ---- measuring text, for the boxes that cannot grow ----

    /// <summary>Width this string needs in this style, padding included.</summary>
    public static float Measure(string text, GUIStyle style)
    {
        Ensure();
        if (style == null) return 0f;
        return style.CalcSize(new GUIContent(text ?? "")).x;
    }

    /// <summary>
    /// Trims <paramref name="text"/> with a trailing ellipsis until it fits <paramref name="width"/>.
    ///
    /// Used ONLY where the box is geometrically fixed and cannot grow: a Toolbar cell, or a rect
    /// computed by hand. Everywhere a row is free to get taller the answer is word wrap instead, so
    /// that no name is ever hidden; see <see cref="StateRow"/>. Where this does trim, the caller is
    /// expected to carry the full string in the control's tooltip, per this app's rule that every
    /// caption lives on hover.
    /// </summary>
    public static string Fit(string text, GUIStyle style, float width)
    {
        Ensure();
        if (string.IsNullOrEmpty(text) || style == null || width <= 0f) return text;

        var c = new GUIContent(text);
        if (style.CalcSize(c).x <= width) return text;

        // Longest prefix that still fits once the ellipsis is added. Binary search rather than a
        // character-at-a-time walk because CalcSize is not free and this runs per frame.
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            c.text = text.Substring(0, mid) + "…";
            if (style.CalcSize(c).x <= width) lo = mid; else hi = mid - 1;
        }
        return lo <= 0 ? "…" : text.Substring(0, lo).TrimEnd() + "…";
    }

    /// <summary>Whether <see cref="Fit"/> would shorten this string, so a caller can add a tooltip.</summary>
    public static bool Fits(string text, GUIStyle style, float width) =>
        string.IsNullOrEmpty(text) || Measure(text, style) <= width;

    /// <summary>
    /// <see cref="Fit"/> across a Toolbar's labels. A Toolbar divides its rect EVENLY between its
    /// items: the same fact <see cref="HoverCells"/> relies on to slice the rect back apart, so one
    /// cell width applies to every label.
    /// </summary>
    public static string[] FitAll(string[] labels, GUIStyle style, float cellWidth)
    {
        if (labels == null) return null;
        var fitted = new string[labels.Length];
        for (int i = 0; i < labels.Length; i++) fitted[i] = Fit(labels[i], style, cellWidth);
        return fitted;
    }

    /// <summary>
    /// The width a Toolbar needs to show every label in full: the widest cell, times the cell count,
    /// because a Toolbar makes all its cells equal. This is what replaces a hardcoded pixel width.
    /// </summary>
    public static float MeasureBar(string[] items, GUIStyle style)
    {
        Ensure();
        if (items == null || items.Length == 0 || style == null) return 0f;
        float widest = 0f;
        for (int i = 0; i < items.Length; i++) widest = Mathf.Max(widest, Measure(items[i], style));
        return widest * items.Length;
    }

    /// <summary>
    /// The horizontal MARGIN a control of this style adds outside whatever width it is given.
    ///
    /// <see cref="Measure"/> reports padding but not margin, so a row that budgets only measured
    /// widths under-reserves by this much per control and overruns its panel by the total: six
    /// pixels a chip is enough to push the last button on a command bar past the edge, where
    /// BeginArea clips it away with no warning anywhere.
    /// </summary>
    public static float MarginW(GUIStyle style)
    {
        Ensure();
        return style == null ? 0f : style.margin.horizontal;
    }

    // The styles a caller has to be able to measure with. Measuring in one style and drawing in
    // another is how a "fitted" label still clips.
    public static GUIStyle CommandStyle { get { Ensure(); EnsureCommandStyle(); return _command; } }
    public static GUIStyle SegmentStyle { get { Ensure(); return _segment; } }
    public static GUIStyle ChipStyle    { get { Ensure(); return _chip; } }
    public static GUIStyle ButtonStyle  { get { Ensure(); return _skin.button; } }

    public static void Title(string text)  { Ensure(); GUILayout.Label(text, _title);  }
    // Section header renders UPPERCASE per the redesign (11/700 caps).
    public static void Header(string text) { Ensure(); GUILayout.Label(text == null ? "" : text.ToUpperInvariant(), _header); }
    public static void Note(string text)   { Ensure(); GUILayout.Label(text, _sub);    }

    // Inline IBM Plex Mono numeric label (right-aligned by default).
    public static void Num(string text, params GUILayoutOption[] opts) { Ensure(); GUILayout.Label(text, _num, opts); }
    public static void NumSmall(string text, params GUILayoutOption[] opts) { Ensure(); GUILayout.Label(text, _numSmall, opts); }

    // Flat clickable foldout row (▸ / ▾ + label). Reads like a section header, not a button.
    static GUIStyle _foldout;
    public static bool Foldout(bool open, string label)
    {
        Ensure();
        if (_foldout == null)
        {
            _foldout = new GUIStyle(_header) { alignment = TextAnchor.MiddleLeft };
            _foldout.normal.background = null;
            _foldout.hover.textColor = Accent;
            _foldout.padding = new RectOffset(2, 2, 4, 4);
            _foldout.margin  = new RectOffset(2, 2, (int)SectionH, 2);   // owns the space above it, like Header
        }
        string text = $"{(open ? "▾  " : "▸  ")}{(label ?? "").ToUpperInvariant()}";
        if (GUILayout.Button(text, _foldout, GUILayout.ExpandWidth(true))) open = !open;
        return open;
    }

    // Thin horizontal divider that fills the current layout width.
    public static void Divider()
    {
        Ensure();
        var r = GUILayoutUtility.GetRect(1, 7, GUILayout.ExpandWidth(true));
        r.y += 3; r.height = 1;
        var prev = GUI.color; GUI.color = Line2;
        GUI.DrawTexture(r, _white); GUI.color = prev;
    }

    // ---- tooltips, and the values that replaced the captions ----

    /// <summary>
    /// Hangs a hover tooltip on the control that was JUST drawn. Works after any UITheme helper and
    /// after any raw GUILayout control, which is what let the whole app gain tooltips without a single
    /// existing signature changing, and therefore without touching the Site tool's ~150 call sites.
    ///
    /// The Repaint guard belongs here rather than in the caller: GetLastRect returns a dummy rect
    /// during the Layout pass, and a dummy rect at the origin swallows the tooltip of whatever really
    /// is under the cursor.
    /// </summary>
    public static void Tip(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        UITooltip.Hover(GUILayoutUtility.GetLastRect(), text);
    }

    /// <summary>
    /// A bare value whose caption lives in its tooltip: the replacement for every "Num(x); Note(cap)"
    /// pair in the app.
    ///
    /// THIS IS THE REVERT SWITCH. Captions were removed on purpose, and the risk that a rail of bare
    /// numbers reads as noise was accepted with eyes open. Everything funnels through here so that
    /// judgement can be reversed by uncommenting one line, rather than by re-editing a hundred sites.
    /// </summary>
    public static void Value(string value, string tooltip, params GUILayoutOption[] opts)
    {
        Ensure();
        EnsureValueStyles();
        GUILayout.Label(value, _numLeft, opts);
        Tip(tooltip);
        // Note(tooltip);   // ← uncomment to bring every caption in the app back
    }

    /// <summary>
    /// A readout that says what it is: name on the left, figure on the right, on the same grid a
    /// labelled field uses.
    ///
    /// It exists because labelling only the EDITABLE half of a rail is worse than labelling neither.
    /// The inspector alternates the two: a wall's thickness field, then its length; an opening's
    /// width field, then the clear passage that width yields, and a named box above an anonymous
    /// number reads as though the app forgot to finish the second one.
    ///
    /// The two-argument form above is untouched and still bare: it is what the legacy Site tool calls.
    /// </summary>
    public static void Value(string label, string value, string tooltip,
                             params GUILayoutOption[] opts)
    {
        if (string.IsNullOrEmpty(label)) { Value(value, tooltip, opts); return; }

        Ensure();
        EnsureNumberStyles();

        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _valueLabel, GUILayout.Height(RowH));
        GUILayout.FlexibleSpace();
        GUILayout.Label(value, _valueRight, opts);
        GUILayout.EndHorizontal();

        // GetLastRect after EndHorizontal is the whole ROW, which is what should answer to the
        // cursor: a tip that only fired over the digits would be one nobody found.
        Tip(tooltip);
    }

    // ---- the one number control ----
    //
    // Editing a number here used to take one of five widgets: a -/+ stepper, a bare slider, a slider
    // with a hardcoded unit suffix, a hand-rolled clock stepper, and (in exactly two places) a text
    // field you could actually type into. So the two gestures anybody wants, "type the value I know"
    // and "push it and watch it move", lived in different controls and you mostly got one of them.
    //
    // DragNumber is both at once: a field you type into, that also scrubs when you drag across it.
    //
    // Four things here are load-bearing, and three of them are IMGUI rather than design:
    //
    //  * ONE RESERVED RECT, ONE TEXTFIELD, ALWAYS. The obvious build swaps a painted label for a
    //    TextField when the field gains focus, and that changes the control count between the layout
    //    pass and the repaint pass: the Mismatched LayoutGroup this codebase's whole deferral
    //    discipline (_pendingStage and friends) exists to prevent. Instead the rect is reserved once
    //    and GUI.TextField is drawn into it in BOTH modes; only the string and the styling differ, so
    //    the two passes agree by construction and no _pending flag is needed.
    //
    //  * IT TAKES hotControl. This is the first GUIUtility.GetControlID control in the project,
    //    every other custom hit-test here is the GetLastRect + Contains + Use idiom StateRow uses.
    //    It earns the exception: UITooltip.Draw already bails while hotControl != 0, so a scrub gets
    //    correct tooltip suppression for free, and a drag that began in one field cannot be stolen by
    //    another the cursor happens to cross.
    //
    //  * THE CURSOR IS NOT CAPTURED. Locking it would be nicer (infinite travel) but
    //    ViewController.SyncCapture is the sole arbiter of Cursor.lockState and releases whenever
    //    neither of ITS two drags is live, which would drop the lock mid-scrub and warp the pointer to
    //    a position this control never recorded. A window's width of travel is enough.
    //
    //  * FORMATTING AND PARSING ARE INJECTED, so UITheme stays ignorant of Units. That is the same
    //    split MeasureUI.cs documents and the reason the legacy Site tool is untouched by this file.
    //    NumberFormat instances must be cached statics at the call site: Unity's C# 9 does not cache
    //    method-group conversions, so building one inline would allocate two delegates per field per
    //    frame.

    /// <summary>How a <see cref="DragNumber"/> turns its value into text and back.</summary>
    public readonly struct NumberFormat
    {
        public readonly System.Func<float, string> Format;
        /// <summary>(typed text, value to keep if it cannot be read) → value.</summary>
        public readonly System.Func<string, float, float> Parse;

        public NumberFormat(System.Func<float, string> format, System.Func<string, float, float> parse)
        {
            Format = format;
            Parse  = parse;
        }

        public bool IsValid => Format != null && Parse != null;
    }

    // Only one field can be scrubbed or typed into at a time, so this is state, not a per-field table.
    static int     _dragId, _editId;
    static float   _dragAccum, _dragFrom;
    static Vector2 _dragOrigin;
    static bool    _dragMoved, _dragBound;
    static string  _editText;
    static bool    _editFocus;

    static string FieldName(int id) => "dragnum" + id;

    /// <summary>
    /// A number you can either type or drag. Returns the value, changed or not, so a call site reads
    /// exactly like the stepper or slider it replaces.
    /// </summary>
    /// <param name="label">One or two words naming the field, drawn inside it on the left.</param>
    /// <param name="tooltip">The sentence. Still on hover: a name is not an explanation.</param>
    /// <param name="step">The quantum. Shift makes it ten times finer, Ctrl or Alt ten times coarser.</param>
    /// <param name="pxPerStep">Horizontal travel one unmodified step costs. 0 takes the default.</param>
    /// <param name="wrap">The ends of the range are the same place: for an angle, or a time of day.</param>
    public static float DragNumber(string label, string tooltip, float value, in NumberFormat fmt,
                                   float step, float min = float.NegativeInfinity,
                                   float max = float.PositiveInfinity,
                                   float pxPerStep = 0f, bool wrap = false)
    {
        Ensure();
        EnsureNumberStyles();
        if (!fmt.IsValid) return value;

        int id = GUIUtility.GetControlID(FocusType.Keyboard);
        Rect r = GUILayoutUtility.GetRect(GUIContent.none, _dragBox,
                                          GUILayout.Height(RowH + 4f), GUILayout.ExpandWidth(true));

        // AFTER GetRect, deliberately. The rect is reserved from an explicit height, an expanding
        // width and GUIContent.none, so the style's padding cannot move it, which means the label
        // column can be measured against the width layout actually handed back rather than guessed
        // at. During the Layout pass that width is 0 and nothing is painted from it anyway.
        float labelW = LabelColumn(label, r.width, out string labelText);
        int   padL   = (int)(FieldInset + (labelW > 0f ? labelW + LabelGap : 0f));
        _dragBox.padding.left = _dragBoxOn.padding.left = _dragEdit.padding.left = padL;

        var e = Event.current;
        bool editing  = _editId == id;
        bool dragging = _dragId == id && GUIUtility.hotControl == id;
        float result  = value;

        // Tab and Shift+Tab reach the field without a click, so focus arriving from anywhere puts it
        // into edit mode rather than leaving a focused field that silently ignores what is typed.
        if (!editing && !dragging && GUI.GetNameOfFocusedControl() == FieldName(id))
        {
            BeginEdit(id, fmt.Format(value));
            editing = true;
        }

        switch (e.type)
        {
            case EventType.MouseDown:
                // Not while editing: once the caret is in, a click is a click in a text field and has
                // to be allowed through to position it.
                if (!editing && e.button == 0 && r.Contains(e.mousePosition))
                {
                    GUIUtility.hotControl = id;
                    GUIUtility.keyboardControl = 0;
                    _dragId     = id;
                    _dragAccum  = value;
                    _dragFrom   = value;
                    _dragOrigin = e.mousePosition;
                    _dragMoved  = false;
                    _dragBound  = false;
                    e.Use();
                }
                break;

            case EventType.MouseDrag:
                if (dragging)
                {
                    if (!_dragMoved &&
                        (e.mousePosition - _dragOrigin).sqrMagnitude >= ScrubMath.DragThresholdSq)
                        _dragMoved = true;

                    if (_dragMoved)
                    {
                        bool fine   = e.shift;
                        bool coarse = e.control || e.alt;

                        _dragAccum = ScrubMath.Advance(_dragAccum, e.delta.x, step, pxPerStep,
                                                       fine, coarse);
                        _dragBound = ScrubMath.AtBound(_dragAccum, min, max, wrap);

                        // Bound the ACCUMULATOR (step 0, so nothing is rounded) before reading a value
                        // off it. Without this a long overshoot has to be dragged all the way back
                        // before the number responds again; with it, reversing moves immediately. And
                        // it must not quantise: re-anchoring to the settled value every frame would
                        // discard sub-step motion, so a fine drag under a coarse step would travel
                        // nowhere however far the pointer went.
                        _dragAccum = ScrubMath.Settle(_dragAccum, 0f, min, max, wrap);
                        result = ScrubMath.Settle(_dragAccum, ScrubMath.Step(step, fine, coarse),
                                                  min, max, wrap);
                        GUI.changed = true;
                    }
                    e.Use();
                }
                break;

            case EventType.MouseUp:
                if (dragging)
                {
                    GUIUtility.hotControl = 0;
                    _dragId = 0;
                    // Under the threshold this was never a drag. It was a click asking to type.
                    if (!_dragMoved) BeginEdit(id, fmt.Format(value));
                    e.Use();
                }
                break;
        }

        // ---- keys BEFORE the field, because GUI.TextField eats what it recognises ----
        //
        // Draw first and the TextField has already consumed the event and flipped e.type to Used, so
        // the switch below never matches and Enter, Escape and the nudge keys all silently do nothing.
        // Taking the four we own up front leaves every other keystroke to fall through to the editor.
        if (editing && e.type == EventType.KeyDown)
        {
            switch (e.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    result = Commit(fmt, value, min, max, wrap);
                    e.Use();
                    break;
                case KeyCode.Escape:
                    EndEdit();
                    e.Use();
                    break;
                // Up and down only. Left and right belong to the caret, which is why this is the pair
                // that nudges everywhere from Blender to a browser's number input.
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                    result = Nudge(fmt, value, e.keyCode == KeyCode.UpArrow ? 1 : -1,
                                   step, min, max, wrap, e.shift, e.control || e.alt);
                    e.Use();
                    break;
            }
            // Enter and Escape both end the edit, so the field below has to be drawn as a field again
            // rather than as a caret over a string nobody is typing into any more.
            editing = _editId == id;
        }

        // ---- the field itself: one TextField in both modes, so the two passes agree ----
        GUI.SetNextControlName(FieldName(id));
        GUIStyle style = editing ? _dragEdit : dragging ? _dragBoxOn : _dragBox;
        string shown = editing ? (_editText ?? "") : fmt.Format(result);
        string typed = GUI.TextField(r, shown, style);
        if (editing && typed != _editText) _editText = typed;

        // Focus is asked for repeatedly until it actually ARRIVES, and only then is the request
        // dropped. GUI.FocusControl does not take effect within the pass that calls it, so clearing
        // the flag immediately would let the blur test below fire on this very frame. Focus the
        // field, observe it is not focused yet, and commit. Clicking into a box would do nothing.
        if (_editFocus && editing)
        {
            GUI.FocusControl(FieldName(id));
            if (GUI.GetNameOfFocusedControl() == FieldName(id)) _editFocus = false;
        }

        // Focus left for anywhere else: another field, a button, the scene. Committing on blur is
        // what stops a typed value being silently thrown away by clicking off it.
        if (_editId == id && !_editFocus && GUI.GetNameOfFocusedControl() != FieldName(id))
        {
            result = Commit(fmt, value, min, max, wrap);
            editing = false;
        }

        if (e.type == EventType.Repaint)
        {
            PaintFieldChrome(r, editing, dragging, _dragBound && dragging, labelText, labelW);

            if (dragging && _dragMoved)
            {
                // Pinned at the cursor, because that is where the eye is during a drag. Reading the
                // value should not mean looking back at the rail you dragged away from.
                float delta = result - _dragFrom;
                UITooltip.Pin(e.mousePosition,
                              Mathf.Approximately(delta, 0f)
                                  ? fmt.Format(result)
                                  : fmt.Format(result) + "   " + (delta >= 0f ? "+" : "−")
                                    + fmt.Format(Mathf.Abs(delta)));
            }
            else if (!editing)
            {
                UITooltip.Hover(r, tooltip);
            }
        }

        return result;
    }

    static void BeginEdit(int id, string text)
    {
        _editId    = id;
        _editText  = text;
        _editFocus = true;
    }

    static void EndEdit()
    {
        _editId   = 0;
        _editText = null;
        if (GUIUtility.keyboardControl != 0) GUIUtility.keyboardControl = 0;
    }

    static float Commit(in NumberFormat fmt, float fallback, float min, float max, bool wrap)
    {
        string text = _editText;
        EndEdit();
        // Unreadable text keeps the old value rather than zeroing the field: the forgiving-parser
        // rule Units.cs states, applied to every number in the app.
        return ScrubMath.Settle(fmt.Parse(text, fallback), 0f, min, max, wrap);
    }

    static float Nudge(in NumberFormat fmt, float value, int dir, float step,
                       float min, float max, bool wrap, bool fine, bool coarse)
    {
        float eff  = ScrubMath.Step(step, fine, coarse);
        float next = ScrubMath.Settle(value + dir * eff, eff, min, max, wrap);
        // The caret stays in the field, so the text has to follow the value it no longer describes.
        _editText = fmt.Format(next);
        return next;
    }

    /// <summary>
    /// A text field wearing <see cref="DragNumber"/>'s chrome and carrying its name the same way.
    ///
    /// This is the answer for the eight bare boxes: a home's name, a person's, a room's, a floor's,
    /// the two search boxes, a person's note, a calibration distance, which were indistinguishable
    /// from one another with nothing on screen saying which was which. `PeopleTool` drew two of them
    /// in one panel.
    ///
    /// One reserved rect and one GUI.TextField, for the reason DragNumber has exactly that: the
    /// Layout and Repaint passes agree on the control count by construction.
    /// </summary>
    public static string TextRow(string label, string text, string tooltip = null,
                                 params GUILayoutOption[] opts)
        => TextRow(label, text, tooltip, false, opts);

    /// <summary>
    /// <see cref="TextRow"/> washed with the active tint when <paramref name="active"/>: for a field
    /// that is also a list row (the Import rail's floor names), where "which one is current" has to
    /// be readable at a glance the way a lit chip was.
    /// </summary>
    public static string TextRow(string label, string text, string tooltip, bool active,
                                 params GUILayoutOption[] opts)
    {
        Ensure();
        EnsureNumberStyles();

        int id = GUIUtility.GetControlID(FocusType.Keyboard);

        var layout = new GUILayoutOption[(opts?.Length ?? 0) + 2];
        layout[0] = GUILayout.Height(RowH + 4f);
        layout[1] = GUILayout.ExpandWidth(true);
        for (int i = 0; i < (opts?.Length ?? 0); i++) layout[i + 2] = opts[i];

        var box = active ? _textBoxOn : _textBox;
        Rect r = GUILayoutUtility.GetRect(GUIContent.none, box, layout);

        float labelW = LabelColumn(label, r.width, out string labelText);
        box.padding.left = (int)(FieldInset + (labelW > 0f ? labelW + LabelGap : 0f));

        GUI.SetNextControlName(FieldName(id));
        string typed = GUI.TextField(r, text ?? "", box);

        if (Event.current != null && Event.current.type == EventType.Repaint)
        {
            bool focused = GUI.GetNameOfFocusedControl() == FieldName(id);
            bool lit     = focused || active;
            bool over    = r.Contains(Event.current.mousePosition);
            Color edge   = lit ? Accent : over ? TintLine : Line2;
            float w      = lit ? 2f : 1f;

            Color prev = GUI.color;
            GUI.color = edge;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, w), _white);
            GUI.DrawTexture(new Rect(r.x, r.yMax - w, r.width, w), _white);
            GUI.DrawTexture(new Rect(r.x, r.y, w, r.height), _white);
            GUI.DrawTexture(new Rect(r.xMax - w, r.y, w, r.height), _white);
            GUI.color = prev;

            PaintLabel(r, labelText, labelW, focused);
            UITooltip.Hover(r, tooltip);
        }
        return typed;
    }

    /// <summary>
    /// <see cref="TextRow"/> for something that should not be left legible on screen. Today, the
    /// Anthropic API key.
    ///
    /// IT SHOWS WHAT YOU ARE ENTERING, and hides on demand rather than the other way round. The first
    /// version masked by default with GUI.PasswordField, and a ~100-character key in a 250 px rail
    /// broke it twice over: the editor computes its scroll offset from the REAL string on the pass a
    /// paste lands, then repaint hands it a run of '•' of a different glyph width, so the view window
    /// falls past the end of the dots and the box renders EMPTY. Dragging recomputed the offset, which
    /// is what made it look like paste had failed. And a field you cannot read is one you cannot tell
    /// you are editing at all.
    ///
    /// So the hidden state is a SHORT FIXED placeholder: a dozen dots at most, never the key's own
    /// length, which cannot scroll, cannot go blank, and leaks no length. It is also disabled while
    /// hidden, because a box that silently swallows typing is a worse trap than a visible secret: to
    /// edit it you click the eye, which is one gesture and says what it does.
    ///
    /// What still holds is the part that matters: the key is on screen only while it is being entered.
    /// SecretRow never redisplays a STORED key. DrawApiKey clears the field on save and summarizes
    /// what is on disk through ApiKeyStore.Masked, which is seven characters and a tail.
    ///
    /// One reserved rect and one GUI.TextField, always: the same construction TextRow and DragNumber
    /// have, so the layout and repaint passes agree on the control count whatever the state is, and
    /// the control id never changes hash under a toggle (swapping to GUI.PasswordField did, which
    /// dropped keyboard focus every time the eye was clicked).
    /// </summary>
    public static string SecretRow(string label, string text, ref bool reveal, string tooltip = null,
                                   params GUILayoutOption[] opts)
    {
        Ensure();
        EnsureNumberStyles();

        int id = GUIUtility.GetControlID(FocusType.Keyboard);

        var layout = new GUILayoutOption[(opts?.Length ?? 0) + 2];
        layout[0] = GUILayout.Height(RowH + 4f);
        layout[1] = GUILayout.ExpandWidth(true);
        for (int i = 0; i < (opts?.Length ?? 0); i++) layout[i + 2] = opts[i];

        Rect r = GUILayoutUtility.GetRect(GUIContent.none, _secretBox, layout);

        float labelW = LabelColumn(label, r.width, out string labelText);
        _secretBox.padding.left = (int)(FieldInset + (labelW > 0f ? labelW + LabelGap : 0f));

        Rect gutter = new Rect(r.xMax - FieldInset - GlyphGutter, r.y, GlyphGutter, r.height);
        var  e      = Event.current;
        bool over   = e != null && r.Contains(e.mousePosition);

        // THE TOGGLE'S CLICK IS TAKEN BEFORE THE FIELD IS DRAWN, and that ordering is the whole of it.
        // The gutter sits INSIDE the field's rect, and a text field claims the MouseDown for its whole
        // rect the moment it is drawn, so a GUI.Button drawn afterwards never saw a press at all. The
        // click fell through to the field instead, which put the caret at the end of the text and
        // scrolled it, so the toggle did nothing and the dots slid sideways for no visible reason.
        //
        // Same rule DragNumber states for its keys ("handled BEFORE the field is drawn, or GUI.TextField
        // has already consumed the event") and StateRow for its row click, applied to a rect this one
        // has to share.
        if (e != null && e.type == EventType.MouseDown && e.button == 0 && gutter.Contains(e.mousePosition))
        {
            reveal = !reveal;
            e.Use();
        }

        // ALWAYS a TextField, never GUI.PasswordField. See the header. Hidden is a short fixed
        // placeholder drawn into a disabled field, and its return is thrown away so a run of dots can
        // never be written back over the key.
        string real  = text ?? "";
        string shown = reveal ? real : new string(MaskChar, Mathf.Min(real.Length, MaskGlyphs));

        bool wasEnabled = GUI.enabled;
        if (!reveal) GUI.enabled = false;

        GUI.SetNextControlName(FieldName(id));
        string typed = GUI.TextField(r, shown, _secretBox);

        GUI.enabled = wasEnabled;
        if (!reveal) typed = real;

        if (e != null && e.type == EventType.Repaint)
        {
            bool focused = GUI.GetNameOfFocusedControl() == FieldName(id);
            Color edge   = focused ? Accent : over ? TintLine : Line2;
            float w      = focused ? 2f : 1f;

            Color prev = GUI.color;
            GUI.color = edge;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, w), _white);
            GUI.DrawTexture(new Rect(r.x, r.yMax - w, r.width, w), _white);
            GUI.DrawTexture(new Rect(r.x, r.y, w, r.height), _white);
            GUI.DrawTexture(new Rect(r.xMax - w, r.y, w, r.height), _white);
            GUI.color = prev;

            PaintLabel(r, labelText, labelW, focused);

            // PAINTED, not a GUI.Button: the press is already claimed above, so a control here would
            // only be a second one competing for the same pixels. It is the same gutter, style and
            // tinting PaintFieldChrome gives DragNumber's ↔, so a secret row and a number row line
            // their affordances up on one edge.
            Color glyph = reveal ? Accent : gutter.Contains(e.mousePosition) ? Ink2 : Ink3;
            GUI.color = glyph;
            GUI.Label(gutter, reveal ? "◉" : "◌", _dragGlyph);
            GUI.color = prev;

            UITooltip.Hover(r, tooltip);
        }

        return typed;
    }

    /// <summary>
    /// The one on/off control: a labelled row wearing the same chrome as <see cref="TextRow"/> and
    /// <see cref="DragNumber"/>. Name on the left, a ●/○ in the right gutter, the tint wash and accent
    /// edge when on.
    ///
    /// A boolean used to be drawn three ways depending on the file: Unity's default grey checkbox,
    /// a lit chip, and a chip whose LABEL changed with its state. This is the same rect, height and
    /// border as the field above and below it, so a rail of settings lines up as one column.
    ///
    /// No GetControlID and no hotControl. DragNumber is deliberately the only control here that
    /// takes one, and a click is not a gesture. Hit-testing is the StateRow idiom (GetRect, Contains,
    /// Use), and because the rect is reserved once and nothing is a GUILayout control inside it, the
    /// layout and repaint passes agree by construction.
    /// </summary>
    public static bool Toggle(string label, bool value, string tooltip = null)
    {
        Ensure();
        EnsureNumberStyles();

        Rect r = GUILayoutUtility.GetRect(GUIContent.none, _textBox,
                                          GUILayout.Height(RowH + 4f), GUILayout.ExpandWidth(true));

        var e = Event.current;
        if (e != null && e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
        {
            value = !value;
            GUI.changed = true;
            // A click on a setting ends any typing elsewhere, as a click on a button does.
            GUIUtility.keyboardControl = 0;
            e.Use();
        }

        if (e != null && e.type == EventType.Repaint)
        {
            bool over = r.Contains(e.mousePosition);

            // The wash first, then the rim over it: the same order the fields paint in.
            GUI.Box(r, GUIContent.none, value ? _toggleOn : _toggleOff);

            Color edge = value ? Accent : over ? TintLine : BtnLine;
            float w    = value ? 2f : 1f;
            Color prev = GUI.color;
            GUI.color = edge;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, w), _white);
            GUI.DrawTexture(new Rect(r.x, r.yMax - w, r.width, w), _white);
            GUI.DrawTexture(new Rect(r.x, r.y, w, r.height), _white);
            GUI.DrawTexture(new Rect(r.xMax - w, r.y, w, r.height), _white);
            GUI.color = prev;

            // A toggle has no value column, so its name may run the width of the row less the gutter,
            // the 55 % cap LabelColumn applies is for a label that shares its box with a number.
            float room  = Mathf.Max(0f, r.width - FieldInset * 2f - GlyphGutter - LabelGap);
            string shown = Fit(label ?? "", _fieldLabel, room);
            float labelW = Mathf.Min(Measure(shown, _fieldLabel), room);
            PaintLabel(r, shown, labelW, value);

            // Same gutter, style and tinting as DragNumber's ↔ and SecretRow's eye, so every field's
            // affordance sits on one edge.
            var gutter = new Rect(r.xMax - FieldInset - GlyphGutter, r.y, GlyphGutter, r.height);
            GUI.color = value ? Accent : over ? Ink2 : Ink3;
            GUI.Label(gutter, value ? "●" : "○", _dragGlyph);
            GUI.color = prev;

            // A trimmed label leads its own tooltip with the full text. Fit's standing rule.
            string tip = shown == label ? tooltip
                       : string.IsNullOrEmpty(tooltip) ? label
                       : label + ". " + tooltip;
            UITooltip.Hover(r, tip);
        }

        return value;
    }

    static GUIStyle _toggleOn, _toggleOff, _textArea;

    /// <summary>
    /// A multi-line text box wearing <see cref="TextRow"/>'s chrome: for the one piece of prose a
    /// rail holds that is the user's own (a proposal's description). It was the only control in
    /// HomeViz drawn with no theme style at all.
    /// </summary>
    public static string TextArea(string text, float height, string tooltip = null)
    {
        Ensure();
        EnsureNumberStyles();
        if (_textArea == null)
        {
            _textArea = new GUIStyle(_skin.textArea)
            {
                fontSize  = 13,
                alignment = TextAnchor.UpperLeft,
                wordWrap  = true,
                padding   = new RectOffset((int)FieldInset, (int)FieldInset, 6, 6),
                margin    = new RectOffset(3, 3, 3, 3),
                border    = new RectOffset(8, 8, 8, 8),
            };
            if (_sansMedium != null) _textArea.font = _sansMedium;
            _textArea.normal.textColor = _textArea.hover.textColor = _textArea.focused.textColor = Ink;
            SetBg(_textArea.normal,  _fieldTex, Ink);
            SetBg(_textArea.hover,   _fieldTex, Ink);
            SetBg(_textArea.focused, _fieldTex, Ink);
        }

        string typed = HasWidth
            ? GUILayout.TextArea(text ?? "", _textArea, GUILayout.Height(height), GUILayout.Width(ContentWidth))
            : GUILayout.TextArea(text ?? "", _textArea, GUILayout.Height(height));
        Tip(tooltip);
        return typed;
    }

    // Border and the affordance arrow, painted OVER the field. Which box is being dragged is the
    // question this control exists to answer out loud, so the active state is a filled wash and a
    // two-pixel accent edge rather than a hairline somebody has to hunt for.
    static void PaintFieldChrome(Rect r, bool editing, bool dragging, bool atBound,
                                 string label, float labelW)
    {
        bool hot   = editing || dragging;
        bool over  = r.Contains(Event.current.mousePosition);
        Color edge = atBound ? Warn : hot ? Accent : over ? TintLine : Line2;
        float w    = hot || atBound ? 2f : 1f;

        Color prev = GUI.color;
        GUI.color = edge;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, w), _white);
        GUI.DrawTexture(new Rect(r.x, r.yMax - w, r.width, w), _white);
        GUI.DrawTexture(new Rect(r.x, r.y, w, r.height), _white);
        GUI.DrawTexture(new Rect(r.xMax - w, r.y, w, r.height), _white);

        // The label survives every state, INCLUDING typing, and that is the one place it parts company
        // with the arrow below. The arrow is an affordance and the caret replaces it; the label is the
        // field's identity, and a box that forgets its own name the moment you click into it is the
        // bug this whole change exists to fix.
        GUI.color = prev;
        PaintLabel(r, label, labelW, hot);

        // Hidden while typing: the caret is the affordance then, and a glyph beside it would read as
        // a character somebody had entered.
        if (!editing)
        {
            GUI.color = dragging ? Accent : over ? Ink2 : Ink3;
            GUI.Label(new Rect(r.xMax - FieldInset - GlyphGutter, r.y, GlyphGutter, r.height),
                      "↔", _dragGlyph);
        }
        GUI.color = prev;
    }

    // ---- the label a control draws INSIDE itself ----
    //
    // Captions were removed from this app once, on the grounds that a sentence under every control is
    // prose and prose is what the rails had too much of. What came back is not that sentence: it is the
    // control's NAME, one or two words, in the box with it. The distinction is the whole rule: a name
    // says which of three identical boxes this is, and only a name can do that job, because a tooltip
    // cannot be read and compared against its neighbor at the same time.
    //
    // Three fields wide the value never moves: the label is set in the UI face, small and muted, hard
    // against the left inset, while the value stays mono and right-aligned exactly where it always was.

    /// <summary>Breathing room inside a labelled control's border. Shared so every one lines up.</summary>
    public const float FieldInset = 10f;
    /// <summary>Room between the label and the value it names.</summary>
    const float LabelGap = 8f;
    /// <summary>The drag arrow's gutter, on the RIGHT now that the label owns the left.</summary>
    const float GlyphGutter = 18f;

    /// <summary>What a hidden <see cref="SecretRow"/> stands in with, and how many of them.</summary>
    // Capped, and deliberately NOT the value's own length: a fixed run cannot scroll out of view the
    // way a per-character mask did, and it does not publish how long the secret is.
    const char MaskChar   = '•';
    const int  MaskGlyphs = 12;

    /// <summary>
    /// The label column inside a box <paramref name="boxWidth"/> wide: its width, and the text to
    /// draw in it.
    ///
    /// Capped a little over half the box, and ellipsised by <see cref="Fit"/> to get there. A label
    /// that pushed the number off the right edge would trade one unreadable control for another,
    /// and per Fit's own rule the caller carries the full string in the tooltip, which every one of
    /// them already does.
    /// </summary>
    static float LabelColumn(string label, float boxWidth, out string shown)
    {
        shown = null;
        if (string.IsNullOrEmpty(label)) return 0f;
        EnsureNumberStyles();

        float room = Mathf.Max(0f, boxWidth * 0.55f - FieldInset);
        if (room <= 1f) return 0f;

        // The common case measures ONCE. This runs for every field on screen on every event pass, and
        // CalcSize is not free. Going straight to Fit would pay for a binary search's worth of them
        // on labels that were never going to be trimmed.
        float w = Measure(label, _fieldLabel);
        if (w <= room) { shown = label; return w; }

        shown = Fit(label, _fieldLabel, room);
        return Mathf.Min(Measure(shown, _fieldLabel), room);
    }

    /// <summary>Paints a label into the left of <paramref name="r"/>. Repaint-only, like every painter here.</summary>
    static void PaintLabel(Rect r, string shown, float labelW, bool hot)
    {
        if (string.IsNullOrEmpty(shown) || labelW <= 0f) return;
        var prev = GUI.color;
        GUI.color = hot ? AccentInk : Ink2;
        GUI.Label(new Rect(r.x + FieldInset, r.y, labelW, r.height), shown, _fieldLabel);
        GUI.color = prev;
    }

    static GUIStyle _dragBox, _dragBoxOn, _dragEdit, _dragGlyph, _fieldLabel, _textBox, _textBoxOn,
                    _secretBox, _valueLabel, _valueRight;
    static void EnsureNumberStyles()
    {
        if (_dragBox != null) return;

        // Built from the skin's textField so the caret, the selection colour and the focus behaviour
        // are Unity's own: only the face, the alignment and the padding are ours.
        _dragBox = new GUIStyle(_skin.textField)
        {
            fontSize  = 13,
            alignment = TextAnchor.MiddleRight,
            // The LEFT is the label's room and is rewritten per call by DragNumber; the right is the
            // arrow's gutter, which the label displaced. Take either back and a glyph sits under a value.
            padding   = new RectOffset((int)FieldInset, (int)(FieldInset + GlyphGutter), 0, 0),
            margin    = new RectOffset(3, 3, 3, 3),
        };
        if (_mono != null) _dragBox.font = _mono;
        _dragBox.normal.textColor = _dragBox.hover.textColor = _dragBox.focused.textColor = Ink;

        _dragBoxOn = new GUIStyle(_dragBox);
        _dragBoxOn.normal.textColor = _dragBoxOn.hover.textColor = _dragBoxOn.focused.textColor = AccentInk;
        SetBg(_dragBoxOn.normal, _tintTex, AccentInk);
        SetBg(_dragBoxOn.hover,  _tintTex, AccentInk);

        // Typing reads left to right, so the caret starts where the eye does, but AFTER the label,
        // which stays put while you type. A value only aligns right once it is a value again rather
        // than a half-finished string.
        _dragEdit = new GUIStyle(_dragBox)
        {
            alignment = TextAnchor.MiddleLeft,
            padding   = new RectOffset((int)FieldInset, (int)FieldInset, 0, 0),
        };

        // A text field wearing the same chrome, for the name and search boxes. Left-aligned in the UI
        // face, because what goes in it is a word rather than a measurement.
        _textBox = new GUIStyle(_skin.textField)
        {
            fontSize  = 13,
            alignment = TextAnchor.MiddleLeft,
            padding   = new RectOffset((int)FieldInset, (int)FieldInset, 0, 0),
            margin    = new RectOffset(3, 3, 3, 3),
        };
        _textBox.normal.textColor = _textBox.hover.textColor = _textBox.focused.textColor = Ink;

        // The active-row wash: _dragBoxOn's construction, on the text field.
        _textBoxOn = new GUIStyle(_textBox);
        _textBoxOn.normal.textColor = _textBoxOn.hover.textColor = _textBoxOn.focused.textColor = AccentInk;
        SetBg(_textBoxOn.normal, _tintTex, AccentInk);
        SetBg(_textBoxOn.hover,  _tintTex, AccentInk);

        // Its own style rather than _textBox with the padding rewritten, because SecretRow needs the
        // RIGHT inset back for the reveal toggle and _textBox is shared with nine other call sites
        // that never set it: a per-call write there would leak into whichever field drew next.
        _secretBox = new GUIStyle(_textBox)
        {
            padding = new RectOffset((int)FieldInset, (int)(FieldInset + GlyphGutter), 0, 0),
        };

        // The control's NAME, in the UI face at 11px so it can never be mistaken for the mono value
        // beside it. Tinted by GUI.color at the call site, the way _dragGlyph is.
        _fieldLabel = new GUIStyle(_sub)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize  = 11,
            wordWrap  = false,
            padding   = new RectOffset(0, 0, 0, 0),
        };
        _fieldLabel.normal.textColor = Color.white;

        // The same name, drawn through GUILayout rather than into a rect: for a readout, a segmented
        // group or a chip row, none of which has an inside to paint into. The left padding is the
        // field's own inset plus its margin, so a labelled readout and a labelled field line their
        // names up on one edge instead of missing each other by three pixels.
        _valueLabel = new GUIStyle(_fieldLabel) { padding = new RectOffset((int)FieldInset + 3, 0, 0, 0) };
        _valueLabel.normal.textColor = Ink2;

        _valueRight = new GUIStyle(_num) { padding = new RectOffset(0, (int)FieldInset + 3, 0, 0) };

        // The UI face, not the mono one the value is set in: IBM Plex Mono has no arrow glyph. Setting
        // font to null falls back to the skin font, which is what already renders the warning triangle
        // in Glyph() and the rotate arrows on SelectTool's buttons. Tinted by GUI.color at the call site.
        _dragGlyph = new GUIStyle(_sub)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize  = 13,
        };
        _dragGlyph.font = null;
        _dragGlyph.normal.textColor = Color.white;

        // The Toggle row's two washes: the plain field when off, the tint when on: the same textures
        // DragNumber's idle and dragging states use, so a setting and a number share one look.
        _toggleOff = new GUIStyle { border = new RectOffset(8, 8, 8, 8) };
        _toggleOff.normal.background = _fieldTex;
        _toggleOn = new GUIStyle { border = new RectOffset(8, 8, 8, 8) };
        _toggleOn.normal.background = _tintTex;
    }

    /// <summary>
    /// A bare glyph carrying its message in a tooltip: how a warning survives in a UI with no prose.
    /// The glyph is a signal, not a sentence: it says "look here" on its own, which is the one job the
    /// removed text was doing that could not simply vanish.
    /// </summary>
    public static void Glyph(string glyph, string tooltip, Color tint)
    {
        Ensure();
        var prev = GUI.contentColor;
        GUI.contentColor = tint;
        GUILayout.Label(glyph, _title, GUILayout.Height(RowH));
        GUI.contentColor = prev;
        Tip(tooltip);
    }

    /// <summary>A compact "1 / 3" wizard step whose instruction is its tooltip.</summary>
    public static void Step(int n, int of, string tooltip)
    {
        Ensure();
        var prev = GUI.contentColor;
        GUI.contentColor = AccentInk;
        GUILayout.Label($"{n} / {of}", _num, GUILayout.Height(RowH));
        GUI.contentColor = prev;
        Tip(tooltip);
    }

    /// <summary>
    /// One muted, wrapped line of figures: the compact measurement summary that sits at the foot of
    /// an inspector, below the controls rather than above them.
    ///
    /// It WRAPS, and it passes an explicit width, for the reason <see cref="StateRow"/> documents: a
    /// word-wrapped label with no width still asks for its full natural width, so the ROW grows
    /// sideways instead of the text moving to a second line. Callers that predate the width stack get
    /// the unwrapped path, exactly as the fitting helpers do.
    /// </summary>
    public static void MutedLine(string text, string tooltip = null)
    {
        Ensure();
        EnsureValueStyles();
        if (HasWidth) GUILayout.Label(text, _measureLine, GUILayout.Width(ContentWidth));
        else GUILayout.Label(text, _measureLine);
        Tip(tooltip);
    }

    // _num is right-aligned, which is right beside a caption and wrong for a column of bare values.
    static GUIStyle _numLeft, _measureLine;
    static void EnsureValueStyles()
    {
        if (_numLeft != null) return;
        _numLeft = new GUIStyle(_num) { alignment = TextAnchor.MiddleLeft };
        // _numSmall is already the muted tertiary ink at 11px: the right weight for a line that is
        // present but is no longer the headline.
        _measureLine = new GUIStyle(_numSmall) { alignment = TextAnchor.UpperLeft, wordWrap = true };
    }

    // ---- component helpers (opt-in, mirror the redesign kit) ----

    // Segmented control (e.g. Move / Rotate / Scale). Returns the selected index.
    // Through the tooltip form, so it fits itself wherever a container has published a width; a
    // caller that has not (the Site tool) lands on exactly the Toolbar call this used to make.
    public static int Segmented(int selected, string[] options) => Segmented(selected, options, null);

    /// <summary>
    /// Segmented control with a tooltip per cell. One of only two places `Tip` cannot do the job: a
    /// Toolbar is N controls inside ONE layout rect, so GetLastRect covers all of them at once.
    ///
    /// IT FITS ITSELF, which is the half that was missing. A Toolbar's minimum width is the SUM of
    /// its cells' natural widths, so a bar whose labels are wider than the rail does not shrink. It
    /// overruns the panel and BeginArea clips whatever is past the edge, silently. That is what put
    /// "Day · Typical day / Day with incidents" off the right edge of the Smart living rail, and the sensor
    /// package tier bar with it, before that control was removed. The compare tool had already
    /// measured its own labels by hand; doing it here means every caller fits by construction
    /// instead of remembering to.
    ///
    /// Two callers are deliberately left alone: one that pins its own width (the command bar's
    /// view-mode picker, which is measured into that bar's reserve) and one whose container never
    /// published a width (the Site tool), per <see cref="HasWidth"/>'s rule that fitting to a GUESSED
    /// width is worse than not trying.
    /// </summary>
    public static int Segmented(int selected, string[] options, string[] tooltips,
                                params GUILayoutOption[] opts)
    {
        if ((opts == null || opts.Length == 0) && HasWidth)
            return SegmentedIn(ContentWidth, selected, options, tooltips);

        return SegmentedRaw(selected, options, tooltips, opts);
    }

    static int SegmentedRaw(int selected, string[] options, string[] tooltips,
                            params GUILayoutOption[] opts)
        => SegmentedRaw(_segment, selected, options, tooltips, opts);

    // Measuring in one style and drawing in another is how a "fitted" label still clips, so the style
    // that decided the fit is the style that draws it.
    static int SegmentedRaw(GUIStyle style, int selected, string[] options, string[] tooltips,
                            params GUILayoutOption[] opts)
    {
        Ensure();
        var layout = new GUILayoutOption[(opts?.Length ?? 0) + 1];
        layout[0] = GUILayout.Height(RowH + 4);
        for (int i = 0; i < (opts?.Length ?? 0); i++) layout[i + 1] = opts[i];

        int picked = GUILayout.Toolbar(selected, options, style ?? _segment, layout);
        HoverCells(GUILayoutUtility.GetLastRect(), options.Length, tooltips);
        return picked;
    }

    /// <summary>
    /// The widest of these side paddings at which every label still fits its cell. Falling back to
    /// the tightest, at which point <see cref="Fit"/> takes over.
    ///
    /// A segmented cell carries the button style's 10 px either side, which is 20 px a cell that says
    /// nothing; on a three-way bar in a 266 px rail that is 60 px, most of a word. So when the labels
    /// do not fit, the PADDING gives way before the text does: an ellipsis loses letters off a name
    /// somebody has to read, a narrower gutter loses only air. Graduated rather than one step so a bar
    /// gives up exactly as much air as it needs and no more: the monitor's `Viewing as` bar clears
    /// "Resident" at 5 px where it would not at 10, and no other bar in the app changes at all.
    /// </summary>
    static readonly int[] SegmentPads = { 10, 7, 5, 3 };
    static readonly System.Collections.Generic.Dictionary<int, GUIStyle> _segmentPadded = new();

    static GUIStyle SegmentAt(int sidePad)
    {
        Ensure();
        if (sidePad >= _segment.padding.left) return _segment;
        if (_segmentPadded.TryGetValue(sidePad, out var style) && style != null) return style;

        style = new GUIStyle(_segment)
        {
            padding = new RectOffset(sidePad, sidePad,
                                     _segment.padding.top, _segment.padding.bottom),
        };
        _segmentPadded[sidePad] = style;
        return style;
    }

    /// <summary>The roomiest style from <see cref="SegmentPads"/> that fits every label in one cell.</summary>
    static GUIStyle SegmentStyleFor(string[] options, float cell)
    {
        GUIStyle last = null;
        for (int i = 0; i < SegmentPads.Length; i++)
        {
            last = SegmentAt(SegmentPads[i]);
            if (AllFit(options, last, cell)) return last;
        }
        return last;   // nothing fits. Fit ellipsises into the tightest cells available
    }

    static bool AllFit(string[] options, GUIStyle style, float cell)
    {
        for (int i = 0; i < options.Length; i++)
            if (Measure(options[i], style) > cell) return false;
        return true;
    }

    /// <summary>
    /// The bar, pinned to <paramref name="available"/> and with every label fitted to the cell it
    /// will actually land in. A Toolbar divides its rect EVENLY, so one cell width applies to all of
    /// them: the same fact <see cref="HoverCells"/> and <see cref="FitAll"/> already rely on.
    /// </summary>
    static int SegmentedIn(float available, int selected, string[] options, string[] tooltips)
    {
        Ensure();
        if (options == null || options.Length == 0) return selected;

        // The style's own margin sits OUTSIDE the width we pin, so it has to come off first or the
        // bar overruns by exactly that much: the mistake the chip row documents at its Draw().
        available = Mathf.Max(40f, available - _segment.margin.horizontal);

        // A Toolbar divides its rect EVENLY, so one cell width applies to every label: the same fact
        // HoverCells and FitAll already rely on.
        float cell = available / options.Length;

        // Padding first, letters last. Trimming a name is the only one of the two that costs the
        // reader anything, so it is what gives way last rather than first.
        var style = SegmentStyleFor(options, cell);

        var shown = FitAll(options, style, cell);
        return SegmentedRaw(style, selected, shown, LeadWithFullName(options, shown, tooltips),
                            GUILayout.Width(available));
    }

    /// <summary>
    /// A trimmed label leads its own tooltip with the full text: <see cref="Fit"/>'s standing rule,
    /// and the same thing HomeEditController.StageTips does for a shortened stage tab. It is what
    /// makes the ellipsis cost nothing.
    /// </summary>
    static string[] LeadWithFullName(string[] full, string[] shown, string[] tips)
    {
        if (full == null || shown == null) return tips;

        bool anyTrimmed = false;
        for (int i = 0; i < full.Length && i < shown.Length; i++)
            if (shown[i] != full[i]) { anyTrimmed = true; break; }
        if (!anyTrimmed) return tips;

        var led = new string[full.Length];
        for (int i = 0; i < full.Length; i++)
        {
            string tip = tips != null && i < tips.Length ? tips[i] : null;
            bool trimmed = i < shown.Length && shown[i] != full[i];
            led[i] = !trimmed ? tip
                   : string.IsNullOrEmpty(tip) ? full[i]
                   : full[i] + ". " + tip;
        }
        return led;
    }

    /// <summary>
    /// A segmented control that names the QUESTION as well as the answers.
    ///
    /// Every one of these already carried text (Move / Rotate / Scale, DSP / Family / Resident) and
    /// none of them said what the choice was ABOUT. The label goes on the same row, not above it, so
    /// the cells keep the height they had.
    ///
    /// <see cref="LabelBarWidth"/> is how a caller that has to measure its own labels first (the
    /// compare tool fits proposal names into a cell) finds out what is left for the bar.
    /// </summary>
    public static int Segmented(string label, int selected, string[] options, string[] tooltips,
                                params GUILayoutOption[] opts)
    {
        if (string.IsNullOrEmpty(label)) return Segmented(selected, options, tooltips, opts);

        Ensure();
        EnsureNumberStyles();

        // The label takes its share BEFORE the cells are measured, or every option is fitted to a
        // cell wider than the one it ends up in and clips anyway. Its margin counts too: it is drawn
        // at LabelBarWidth but occupies that plus its margin on the row.
        float labelW = LabelBarWidth(label);
        float labelSlot = labelW + _valueLabel.margin.horizontal;

        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _valueLabel,
                        GUILayout.Width(labelW), GUILayout.Height(RowH + 4));
        int picked = (opts == null || opts.Length == 0) && HasWidth
            ? SegmentedIn(ContentWidth - labelSlot, selected, options, tooltips)
            : SegmentedRaw(selected, options, tooltips, opts);
        GUILayout.EndHorizontal();
        return picked;
    }

    /// <summary>Room a leading row label takes, including the gap after it.</summary>
    public static float LabelBarWidth(string label)
    {
        if (string.IsNullOrEmpty(label)) return 0f;
        Ensure();
        EnsureNumberStyles();
        return Measure(label, _valueLabel) + LabelGap;
    }

    // A Toolbar divides its rect evenly between its items, so slicing it back apart is exact.
    // Repaint only, for the same reason Tip is: GetLastRect is a dummy during the Layout pass.
    static void HoverCells(Rect r, int count, string[] tooltips)
    {
        if (tooltips == null || count <= 0) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        float cw = r.width / count;
        for (int i = 0; i < count && i < tooltips.Length; i++)
            UITooltip.Hover(new Rect(r.x + i * cw, r.y, cw, r.height), tooltips[i]);
    }

    // Accent primary action button: 44px tall target (UITheme.PrimaryH) per the redesign.
    public static bool PrimaryButton(string text, params GUILayoutOption[] opts)
    {
        Ensure();
        if (opts == null || opts.Length == 0) opts = new[] { GUILayout.Height(PrimaryH) };
        return GUILayout.Button(text, _primary, opts);
    }

    // Secondary (default) button: same look as GUI.skin.button, named for clarity.
    public static bool SecondaryButton(string text, params GUILayoutOption[] opts)
    {
        Ensure();
        return GUILayout.Button(text, _skin.button, opts);
    }

    /// <summary>
    /// A button for a TINTED strip: the mode band. Its washes (Tile, Tint, WarnTint) are the same
    /// tone as <see cref="Btn"/>, so a SecondaryButton drawn on one disappeared into it; this is a
    /// white field with the button rim, which reads as a button on all three.
    /// </summary>
    public static bool BandButton(string text, params GUILayoutOption[] opts)
    {
        Ensure();
        return GUILayout.Button(text, BandButtonStyle, opts);
    }

    static GUIStyle _band;
    /// <summary>Exposed so the band can MEASURE with the style it draws with.</summary>
    public static GUIStyle BandButtonStyle
    {
        get
        {
            Ensure();
            if (_band == null)
            {
                _band = new GUIStyle(_skin.button);
                SetBg(_band.normal,  _bandTex,  Ink);
                SetBg(_band.hover,   _btnHover, Ink);
                SetBg(_band.active,  _btnHover, Ink);
                SetBg(_band.focused, _bandTex,  Ink);
            }
            return _band;
        }
    }

    // Borderless ghost / cancel button.
    public static bool GhostButton(string text, params GUILayoutOption[] opts)
    {
        Ensure();
        return GUILayout.Button(text, _ghost, opts);
    }

    // Filter pill / chip. Returns true when clicked.
    public static bool Chip(string text, bool active, params GUILayoutOption[] opts)
    {
        Ensure();
        return GUILayout.Button(text, active ? _chipOn : _chip, opts);
    }

    /// <summary>
    /// A row of chips that wraps when it runs out of rail.
    ///
    /// Every chip row in the app was hand-rolled as "BeginHorizontal, wrap every N, EndHorizontal"
    /// with N picked by eye, and most of them never wrapped at all: the rooms tool's twelve type
    /// chips wrap constantly while a four-chip row sits within pixels of a 282 px rail. The
    /// count was never the right question: the labels are what vary, several of them come from a
    /// palette asset or a catalog, and the rail width is now knowable. So this measures each label and
    /// breaks the line when the next one would not fit.
    ///
    /// A struct held in a local, so its running width mutates in place:
    ///     var row = UITheme.ChipRow();
    ///     if (row.Chip("Bathroom", on)) …
    ///     row.End();
    /// </summary>
    public struct ChipFlow
    {
        private float _used;
        private float _limit;
        private bool  _open;

        internal void Start(float limit)
        {
            _limit = limit;
            _used  = 0f;
            _open  = false;
        }

        /// <summary>
        /// A name for the row, drawn as its first item.
        ///
        /// It is not a chip (it does not click and it does not tint) but it is MEASURED and counted
        /// into the running width like one, because a leading label that the wrap arithmetic did not
        /// know about is exactly how a four-chip row (~280 px in a 282 px rail) starts falling off
        /// the panel again.
        /// </summary>
        public void Label(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Ensure();
            EnsureNumberStyles();

            float w = LabelBarWidth(text);
            if (!_open) { GUILayout.BeginHorizontal(); _open = true; _used = 0f; }
            _used += w;
            GUILayout.Label(text, _valueLabel, GUILayout.Width(w), GUILayout.Height(RowH));
        }

        /// <summary>
        /// A chip pinned to <paramref name="fixedWidth"/>. Separate from the measuring form because a
        /// caller that pins the width must be measured at that width too. Measuring the glyph and
        /// drawing at 28 px is how a row silently overruns anyway.
        /// </summary>
        public bool Chip(string text, bool active, float fixedWidth)
        {
            Ensure();
            var style = active ? _chipOn : _chip;
            return Draw(text, style, fixedWidth + style.margin.horizontal,
                        GUILayout.Width(fixedWidth));
        }

        public bool Chip(string text, bool active, params GUILayoutOption[] opts)
        {
            Ensure();
            var style = active ? _chipOn : _chip;
            // margin is outside CalcSize, and it is what actually separates two chips on a row.
            return Draw(text, style, Measure(text, style) + style.margin.horizontal, opts);
        }

        private bool Draw(string text, GUIStyle style, float w, params GUILayoutOption[] opts)
        {
            if (_open && _used + w > _limit)
            {
                GUILayout.EndHorizontal();
                _open = false;
            }
            if (!_open)
            {
                GUILayout.BeginHorizontal();
                _open = true;
                _used = 0f;
            }

            _used += w;
            bool hit = GUILayout.Button(text, style, opts);
            return hit;
        }

        /// <summary>Closes the open row. Safe to call when no chip was ever drawn.</summary>
        // No trailing FlexibleSpace: a chip stretches to fill its row, which is how every hand-rolled
        // chip row in this app has always looked. All this changes is WHERE the row breaks.
        public void End()
        {
            if (!_open) return;
            GUILayout.EndHorizontal();
            _open = false;
        }
    }

    /// <summary>Starts a wrapping chip row across the current <see cref="ContentWidth"/>.</summary>
    public static ChipFlow ChipRow(float width = 0f)
    {
        Ensure();
        var flow = new ChipFlow();
        flow.Start(width > 0f ? width : ContentWidth);
        return flow;
    }

    // Inline status badge: a dot + label, green when ok else muted.
    public static void StatusBadge(string text, bool ok)
    {
        Ensure();
        var prev = GUI.contentColor;
        GUI.contentColor = ok ? Ok : Ink3;
        GUILayout.Label((ok ? "● " : "○ ") + text, _sub);
        GUI.contentColor = prev;
    }

    // The read-only badge: a full-width amber pill with the ⚠ glyph, RowH high, its explanation on
    // hover. One look everywhere a rail refuses to edit. It replaced a grey hollow-dot caption that
    // read as a hint rather than as the reason the controls below were missing. Not a button (the mode
    // band carries the one switch), so no hover state; the rim is what says "badge" not "text".
    static GUIStyle _lockBadge;
    public static void LockBadge(string text, string tooltip)
    {
        Ensure();
        if (_lockBadge == null)
        {
            _lockBadge = new GUIStyle(_skin.label)
            {
                fontSize = 12, wordWrap = false, alignment = TextAnchor.MiddleLeft,
                border = new RectOffset(8, 8, 8, 8),
                padding = new RectOffset((int)FieldInset, (int)FieldInset, 0, 0),
                margin = new RectOffset(2, 2, 3, 3),
            };
            if (_sansSemi != null) _lockBadge.font = _sansSemi;
            _lockBadge.normal.background = _warnBadgeTex;
            _lockBadge.normal.textColor = Warn;
        }
        var opts = HasWidth
            ? new[] { GUILayout.Height(RowH), GUILayout.Width(ContentWidth) }
            : new[] { GUILayout.Height(RowH), GUILayout.ExpandWidth(true) };
        GUILayout.Label("⚠  " + text, _lockBadge, opts);
        Tip(tooltip);
    }

    // Danger / destructive action: a red OUTLINE button, used for Delete. It used to be borderless
    // bold red text, which reads as a warning label rather than as something you press; the rim is
    // what says "button", the colour is what says "careful". Normal weight for the same reason.
    static GUIStyle _danger;
    public static bool DangerButton(string text, params GUILayoutOption[] opts)
    {
        Ensure();
        return GUILayout.Button(text, DangerStyle, opts);
    }

    /// <summary>
    /// A circular glyph button: <see cref="GlyphW"/> square on a radius-13 face, which at 26 px is a
    /// true circle. For a small in-row action that is not a delete (the ↻ replace-plan control); a row
    /// delete stays <see cref="DangerButton"/>, everywhere. The glyph renders through the skin-font
    /// fallback, the same trick _dragGlyph uses for the drag arrow.
    /// </summary>
    static GUIStyle _roundGlyph;
    public static bool RoundGlyphButton(string glyph, params GUILayoutOption[] opts)
    {
        Ensure();
        if (_roundGlyph == null)
        {
            _roundGlyph = new GUIStyle(_skin.button)
            {
                padding   = new RectOffset(0, 0, 0, 0),
                alignment = TextAnchor.MiddleCenter,
                border    = new RectOffset(13, 13, 13, 13),
            };
            _roundGlyph.font = null;   // skin-font fallback: the UI face has no ↻
            SetBg(_roundGlyph.normal,  _roundBtnTex,      Ink);
            SetBg(_roundGlyph.hover,   _roundBtnHoverTex, Ink);
            SetBg(_roundGlyph.active,  _roundBtnHoverTex, Ink);
            SetBg(_roundGlyph.focused, _roundBtnTex,      Ink);
        }

        var layout = new GUILayoutOption[(opts?.Length ?? 0) + 2];
        for (int i = 0; i < (opts?.Length ?? 0); i++) layout[i] = opts[i];
        // Last wins in GUILayout, so the circle cannot be stretched back into a lozenge.
        layout[layout.Length - 2] = GUILayout.Width(GlyphW);
        layout[layout.Length - 1] = GUILayout.Height(GlyphW);
        return GUILayout.Button(glyph, _roundGlyph, layout);
    }

    /// <summary>Exposed so a caller can <see cref="Fit"/> a value in the style it draws with.</summary>
    public static GUIStyle ValueStyle
    {
        get
        {
            Ensure();
            EnsureValueStyles();
            return _numLeft;
        }
    }

    /// <summary>
    /// <see cref="DangerButton"/> that wraps: for a two-click confirm whose price-naming label
    /// outgrows the rail. The price has to be ON SCREEN, which is the confirm's whole point, and the
    /// block below a row is free to grow taller, so this wraps where a fixed box would Fit.
    /// </summary>
    static GUIStyle _dangerWrap;
    public static bool DangerButtonWrapped(string text, params GUILayoutOption[] opts)
    {
        Ensure();
        if (_dangerWrap == null) _dangerWrap = new GUIStyle(DangerStyle) { wordWrap = true };
        return GUILayout.Button(text, _dangerWrap, opts);
    }

    /// <summary>Exposed so a caller with a long, composed label can <see cref="Fit"/> it first.</summary>
    public static GUIStyle DangerStyle
    {
        get
        {
            Ensure();
            if (_danger == null)
            {
                _danger = new GUIStyle(_ghost);
                SetBg(_danger.normal,  _dangerTex,      Danger);
                SetBg(_danger.hover,   _dangerHoverTex, Danger);
                SetBg(_danger.active,  _dangerHoverTex, Danger);
                SetBg(_danger.focused, _dangerTex,      Danger);
            }
            return _danger;
        }
    }

    // ---- redesign kit: rows, thumbnails, command bar ----

    // A list row washed with the active-tint when `active`: a title plus an optional state line.
    // Returns true when the row body is clicked. Drawn as a vertical group whose own background is
    // the tile/tint texture, so the panel auto-sizes the row and the labels paint on top of it
    // (no GUILayout.BeginArea / manual rects, which previously hid the title behind the wash).
    // `reserveRight` is room to leave for a control sharing this row: the ✕ that takes one change
    // back out, or the one that deletes a drawn run. Without it the row lays out to the full rail
    // width and that button is pushed off the panel.
    public static bool StateRow(string title, string state, bool active, bool muted = false,
                                float reserveRight = 0f)
    {
        Ensure();
        EnsureRowStyles();

        var style = active ? _rowOn : (muted ? _rowMuted : _rowFlat);

        // NO PUBLISHED WIDTH MEANS AN UNCONVERTED CALLER. Today, the Site tool's LibraryBrowser,
        // which draws its own BeginArea and knows nothing about the width stack. Those keep the exact
        // behaviour they had before this row learned to wrap: natural width, no wrapping, clipped at
        // the panel edge. Wrapping without a width to wrap INTO would only grow the row sideways out
        // of a panel this method cannot measure, which is worse than the clipping it replaced.
        if (!HasWidth)
        {
            bool wrapped = _rowTitle.wordWrap;
            _rowTitle.wordWrap = false;
            GUILayout.BeginVertical(style);
            GUILayout.Label(title, _rowTitle);
            if (!string.IsNullOrEmpty(state))
                GUILayout.Label(state, active ? _rowStateOn : _rowState);
            GUILayout.EndVertical();
            _rowTitle.wordWrap = wrapped;
        }
        else
        {
            // The explicit width is what makes the wrap actually happen. A word-wrapped label with no
            // width still asks the layout for its full natural width, and the ROW grows sideways out
            // of the panel instead of the text moving onto a second line.
            var w = GUILayout.Width(
                Mathf.Max(40f, ContentWidth - _rowOn.padding.horizontal - reserveRight));

            GUILayout.BeginVertical(style, w);
            GUILayout.Label(title, _rowTitle, w);
            if (!string.IsNullOrEmpty(state))
                GUILayout.Label(state, active ? _rowStateOn : _rowState, w);
            GUILayout.EndVertical();
        }

        var r = GUILayoutUtility.GetLastRect();
        var e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
        {
            e.Use();
            return true;
        }
        return false;
    }

    /// <summary>
    /// StateRow's ONE-LINE form: title left, state right, on a single row: for long pickers where
    /// the two-line row spends more rail than the list is worth (the Rooms rail). The title goes
    /// through <see cref="Fit"/> with the full text in the tooltip, which is legal here and not in
    /// StateRow: this box is geometrically fixed, and wrapping is what the two-line form is for.
    /// </summary>
    public static bool StateRowLine(string title, string state, bool active, bool muted = false,
                                    float reserveRight = 0f)
    {
        Ensure();
        EnsureRowStyles();
        EnsureRowLineStyles();

        var style = active ? _lineOn : (muted ? _lineMuted : _lineFlat);
        var stateStyle = active ? _lineStateOn : _lineState;
        title ??= "";
        string fitted = title;

        // The unconverted-caller rule StateRow documents: no published width, no fitting. Natural
        // width, clipped at the panel edge.
        if (!HasWidth)
        {
            GUILayout.BeginHorizontal(style);
            GUILayout.Label(title, _lineTitle);
            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(state)) GUILayout.Label(state, stateStyle);
            GUILayout.EndHorizontal();
        }
        else
        {
            float w = Mathf.Max(40f, ContentWidth - reserveRight);
            float stateW = string.IsNullOrEmpty(state) ? 0f : Measure(state, stateStyle);
            float titleW = Mathf.Max(20f, w - style.padding.horizontal
                                          - stateW - (stateW > 0f ? LabelGap : 0f));
            fitted = Fit(title, _lineTitle, titleW);

            GUILayout.BeginHorizontal(style, GUILayout.Width(w));
            GUILayout.Label(fitted, _lineTitle);
            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(state)) GUILayout.Label(state, stateStyle);
            GUILayout.EndHorizontal();
        }

        var r = GUILayoutUtility.GetLastRect();
        if (fitted != title) Tip(title);

        var e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
        {
            e.Use();
            return true;
        }
        return false;
    }

    static GUIStyle _lineOn, _lineMuted, _lineFlat, _lineTitle, _lineState, _lineStateOn;
    static void EnsureRowLineStyles()
    {
        if (_lineOn != null) return;
        // The two-line row's surfaces with the vertical padding halved: one line does not need the
        // breathing room two do.
        var pad = new RectOffset(11, 11, 5, 5);
        _lineOn    = new GUIStyle(_rowOn)    { padding = pad };
        _lineMuted = new GUIStyle(_rowMuted) { padding = pad };
        _lineFlat  = new GUIStyle(_rowFlat)  { padding = pad };
        _lineTitle = new GUIStyle(_rowTitle) { wordWrap = false };
        // Nudged down so the smaller state text sits on roughly the title's baseline.
        _lineState   = new GUIStyle(_rowState)   { padding = new RectOffset(0, 0, 2, 0) };
        _lineStateOn = new GUIStyle(_rowStateOn) { padding = new RectOffset(0, 0, 2, 0) };
    }

    static GUIStyle _rowOn, _rowMuted, _rowFlat, _rowTitle, _rowState, _rowStateOn;
    static void EnsureRowStyles()
    {
        if (_rowOn != null) return;
        var pad = new RectOffset(11, 11, 8, 8);
        var mrg = new RectOffset(0, 0, 2, 2);
        _rowOn = new GUIStyle { border = new RectOffset(8, 8, 8, 8), padding = pad, margin = mrg };
        _rowOn.normal.background = _tintTex;        // active row: blue wash
        _rowMuted = new GUIStyle { border = new RectOffset(8, 8, 8, 8), padding = pad, margin = mrg };
        _rowMuted.normal.background = _tileTex;     // backdrop row: neutral tile
        // A SURFACE, not bare text. The unselected row used to have no background and no rim, which
        // left every list in the app (residents, rooms, proposals, homes) as clickable borderless
        // text, the exact thing the button rule forbids. The white field + BtnLine rim is the
        // BandButton surface: visibly a thing you press, still quieter than the tinted active row.
        _rowFlat = new GUIStyle { border = new RectOffset(8, 8, 8, 8), padding = pad, margin = mrg };
        _rowFlat.normal.background = _bandTex;

        // wordWrap TRUE, and that is a fix rather than a preference. This style draws every list title
        // in the app. Home names, sample names, occupant names, proposal names, VariantDiff change
        // labels, and all of those are data. With no wrap they were cut mid-glyph with no ellipsis
        // and no tooltip: the two five-bedroom samples ship 37-character names ("Assisted living
        // house: 5 bed, 4 bath") in a box that holds about 28. StateRow gives it an explicit width,
        // which is the other half of making the wrap happen rather than the row growing.
        _rowTitle = new GUIStyle(_sub) { fontSize = 13, wordWrap = true };
        if (_sansMedium != null) _rowTitle.font = _sansMedium;
        _rowTitle.normal.textColor = Ink;
        _rowState = new GUIStyle(_sub) { fontSize = 11 };
        _rowState.normal.textColor = Ink3;
        _rowStateOn = new GUIStyle(_rowState);
        _rowStateOn.normal.textColor = AccentInk;
    }

    // Thumbnail tile button: image (or color swatch) with a caption, accent ring when selected.
    public static bool Thumb(Texture tex, string label, bool selected, float size = 64f)
    {
        Ensure();
        EnsureThumbStyles();
        var style = selected ? _thumbOn : _thumb;
        GUILayout.BeginVertical(GUILayout.Width(size));
        var clicked = GUILayout.Button(GUIContent.none, style, GUILayout.Width(size), GUILayout.Height(size));
        var r = GUILayoutUtility.GetLastRect();
        if (tex != null)
        {
            var pad = new Rect(r.x + 4, r.y + 4, r.width - 8, r.height - 8);
            GUI.DrawTexture(pad, tex, ScaleMode.ScaleToFit);
        }
        if (!string.IsNullOrEmpty(label))
            GUILayout.Label(label, _thumbCap, GUILayout.Width(size), GUILayout.Height(26f));
        GUILayout.EndVertical();
        return clicked;
    }

    // "roof_tar_weathered" -> "Roof Tar Weathered"
    public static string PrettyId(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        var parts = id.Replace('_', ' ').Split(' ');
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
        return string.Join(" ", parts);
    }

    static GUIStyle _thumb, _thumbOn, _thumbCap;
    static void EnsureThumbStyles()
    {
        if (_thumb != null) return;
        _thumb = new GUIStyle { border = new RectOffset(8, 8, 8, 8), margin = new RectOffset(3, 3, 3, 3) };
        _thumb.normal.background = _fieldTex;
        _thumb.hover.background  = _tintTex;
        _thumbOn = new GUIStyle(_thumb);
        _thumbOn.normal.background = _tintTex;        // accent-tinted host
        _thumbCap = new GUIStyle(_sub) { fontSize = 10, alignment = TextAnchor.UpperCenter, wordWrap = true, clipping = TextClipping.Clip };
        _thumbCap.normal.textColor = Ink2;
    }

    // Top command bar (Browse / Place / Terrain / Build / Manage / Generate). Returns selected index.
    //
    // `opts` lets a caller that shares its bar with other controls pin the width: a Toolbar in a
    // horizontal group expands to fill otherwise, leaving nothing for what sits beside it.
    public static int CommandBar(int selected, string[] items, params GUILayoutOption[] opts)
    {
        Ensure();
        EnsureCommandStyle();

        var layout = new GUILayoutOption[(opts?.Length ?? 0) + 1];
        layout[0] = GUILayout.Height(PrimaryH);
        for (int i = 0; i < (opts?.Length ?? 0); i++) layout[i + 1] = opts[i];

        return GUILayout.Toolbar(selected, items, _command, layout);
    }

    /// <summary>
    /// Command bar with a tooltip per tab: the other place `Tip` cannot reach, for the same reason
    /// <see cref="Segmented"/> cannot. This is what carries each tool's instructions now that they no
    /// longer print in the rail: hovering the tab you are working in says what the tool does.
    /// </summary>
    public static int CommandBar(int selected, string[] items, string[] tooltips,
                                 params GUILayoutOption[] opts)
    {
        int picked = CommandBar(selected, items, opts);
        HoverCells(GUILayoutUtility.GetLastRect(), items.Length, tooltips);
        return picked;
    }

    static GUIStyle _command;
    static void EnsureCommandStyle()
    {
        if (_command != null) return;
        _command = new GUIStyle(_skin.button) { fontSize = 13, fontStyle = FontStyle.Normal, fixedHeight = PrimaryH, padding = new RectOffset(14, 14, 0, 0) };
        if (_sansMedium != null) _command.font = _sansMedium;
    }
}
