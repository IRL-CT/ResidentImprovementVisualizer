using UnityEngine;

// Which of the two jobs you are doing, said permanently, in colour, under the command bar.
//
// HomeViz is two editors wearing one face. Editing the base environment. Making the model match the
// home that actually exists, and proposing a change to it are different acts with different
// consequences, and until this band the app looked identical in both. The state was never missing:
// VariantDef.isBaseline and VariantDef.locked spell it out, and the variant panel composed them into
// a string. That string lived in a foldout that was collapsed by default, at the bottom of the right
// rail, opened automatically only in Review. The only ambient signal was a tool refusing to work,
// which reads as a malfunction rather than an answer.
//
// AMBER IS FOR EDITING THE BASE, and that is the point of the whole file. Working in a proposal is
// the routine case and gets the accent; opening the base environment is the rare and consequential
// one, because an accidental edit there silently corrupts what every proposal is measured against,
// and nothing downstream would ever complain. It is not an error, so Danger would be a lie.
//
// LOCKING IS NOT A FEATURE HERE. It is one mode switch with one home. VariantDef.locked survives in
// the schema (HomeToolContext.IsLocked, TickGizmo and SampleRefresh all read it) but only the
// baseline ever carries it, and only "Modify base environment" / "Done" flip it. A proposal is
// always editable. The old ProposalLocked mode put a second Unlock button in the band while
// HomeToolBase.RefuseIfLocked put a third in every tool rail, and nothing said which was which.
//
// A plain class like TimelineBar and SelectionOverlay: the controller owns one and calls Draw from
// its OnGUI. Everything is re-derived per frame and nothing is cached: undo restores the whole
// HomeDoc without notifying anyone, so a held VariantDef would be a stale object describing a
// variant that no longer exists.
//
// There is no subtitle. Per this app's rule every caption is a tooltip, and a band that printed
// "the home as it is today" under its own title would be exactly the prose the rest of the UI had
// removed. The sentence is on the title's hover.
public class ModeBand
{
    /// <summary>
    /// Height of the collapsed band. One control row (28, plus the button style's 3 px margin above
    /// and below) and 6 above and below: a tighter inset than UITheme.Inset's Pad, because this is
    /// chrome continuous with the command bar rather than a card, and a card's padding here would
    /// push the command bar's tabs away from the scene. The margin is counted: a 28 px button in a
    /// 28 px region was drawn at y 3..31 and lost its bottom edge to the clip.
    /// </summary>
    public const float Height = 46f;

    private const float VPad = 6f;
    private const float BtnH = 28f;
    private const float MenuRowHeight = 52f;
    private const float MenuFooterHeight = 76f;
    private const float StripeWidth = 4f;

    public enum Mode
    {
        /// <summary>The base environment, read-only. Where a home spends most of its life.</summary>
        Base,
        /// <summary>The base environment, open for edits. Edits here change what proposals are measured against.</summary>
        EditingBase,
        /// <summary>A proposal. Always editable. See the header.</summary>
        Proposal,
    }

    // Requests, read by the controller after Draw has returned. Nothing here acts on the document:
    // several of these change the band's own control count, and applying that between a frame's
    // layout and repaint passes is what throws Mismatched LayoutGroup.
    public bool ToggleMenuRequested { get; private set; }
    /// <summary>"Modify base environment". Open the baseline for editing. Only ever the baseline.</summary>
    public bool EditBaseRequested { get; private set; }
    /// <summary>"Done". Close the baseline again.</summary>
    public bool DoneRequested { get; private set; }
    public bool NewProposalRequested { get; private set; }
    public bool CompareRequested { get; private set; }
    public bool ReportRequested { get; private set; }
    public string PickedVariantId { get; private set; }
    public string DeleteVariantId { get; private set; }

    private GUIStyle _titleStyle, _titlePill;

    // A proposal never reports a locked mode even if some older document carries locked=true on one:
    // the band is the only way in, and it can no longer produce that state.
    public static Mode ModeOf(VariantDef v)
    {
        if (v == null) return Mode.Base;
        if (!v.isBaseline) return Mode.Proposal;
        return v.locked ? Mode.Base : Mode.EditingBase;
    }

    /// <summary>
    /// The band's height for this frame. The caller needs it before Draw, because the rect has to be
    /// known in time to go into the pointer-over-UI test.
    /// </summary>
    public static float HeightFor(HomeDoc doc, bool menuOpen)
    {
        if (doc == null) return 0f;
        if (!menuOpen) return Height;
        int n = doc.variants?.Count ?? 0;
        return Height + n * MenuRowHeight + MenuFooterHeight;
    }

    /// <summary>
    /// Draws the band into <paramref name="area"/>. The caller owns that rect and MUST include it in
    /// its pointer-over-UI test. Without that every click here also falls through to the scene, and
    /// for this band that would mean pressing "New proposal" additionally placed a wall.
    /// </summary>
    public void Draw(Rect area, HomeDoc doc, VariantDef variant, int changeCount,
                     string baselineName, bool menuOpen)
    {
        ToggleMenuRequested = false;
        EditBaseRequested = false;
        DoneRequested = false;
        NewProposalRequested = false;
        CompareRequested = false;
        ReportRequested = false;
        PickedVariantId = null;
        DeleteVariantId = null;

        if (doc == null || area.height <= 0f) return;

        var mode = ModeOf(variant);
        UITheme.BandBackground(area, Wash(mode));

        // A stripe down the leading edge, so the mode reads from peripheral vision without anyone
        // having to look up at the words.
        var prev = GUI.color;
        GUI.color = Ink(mode);
        GUI.DrawTexture(new Rect(area.x, area.y, StripeWidth, area.height), UITheme.Pixel);
        GUI.color = prev;

        var row = new Rect(area.x + StripeWidth + 10f, area.y + VPad,
                           area.width - StripeWidth - 20f, Height - VPad * 2f);
        UITheme.BeginRegion(row);
        GUILayout.BeginHorizontal();
        // The title is whatever the variant is called, so it is the elastic half of this row and the
        // buttons are the fixed half. Give it what the buttons do not want, rather than letting it
        // push them off the end of the band.
        DrawTitle(doc, variant, mode, changeCount, baselineName,
                  row.width - ActionsWidth(mode) - 12f);
        GUILayout.FlexibleSpace();
        DrawActions(mode);
        GUILayout.EndHorizontal();
        UITheme.EndRegion();

        if (!menuOpen) return;

        var menu = new Rect(area.x + StripeWidth + 10f, area.y + Height,
                            area.width - StripeWidth - 20f, area.height - Height - 8f);
        UITheme.BeginRegion(menu);
        DrawMenu(doc, variant);
        UITheme.EndRegion();
    }

    // What DrawActions is about to ask for, so the title can be told what is left.
    private static float ActionsWidth(Mode mode)
    {
        var s = UITheme.BandButtonStyle;
        return mode switch
        {
            Mode.Base        => UITheme.Measure("Modify base environment", s)
                              + UITheme.Measure("New proposal", s) + s.margin.horizontal * 2f,
            Mode.EditingBase => UITheme.Measure("Done", s) + s.margin.horizontal,
            _                => UITheme.Measure("Compare", s)
                              + UITheme.Measure("Report", s) + s.margin.horizontal * 2f,
        };
    }

    // ---------------------------------------------------------------------------------------

    private void DrawTitle(HomeDoc doc, VariantDef variant, Mode mode, int changeCount,
                           string baselineName, float available)
    {
        EnsureStyles();

        string name = string.IsNullOrEmpty(variant?.name) ? "(unnamed)" : variant.name;
        bool switchable = (doc.variants?.Count ?? 0) > 1;

        // The base environment is named by its ROLE, not by whatever the baseline variant happens to
        // be called ("Existing" on every home this app has ever written). Its name still appears in
        // the variant menu, in Compare's Before row and in the report. A proposal is named, because
        // there can be several and picking between them is the point.
        string text = mode switch
        {
            Mode.EditingBase => "EDITING BASE ENVIRONMENT",
            Mode.Base        => "BASE ENVIRONMENT · READ-ONLY",
            _                => name.ToUpperInvariant() + " · " + Changes(changeCount),
        };
        if (switchable) text += "  ▾";

        // The colour is written into the STYLE, not applied through GUI.contentColor. contentColor
        // tints what the style already carries, and this style inherits UITheme's near-black Ink,
        // so tinting it amber yields mud. The same trap OverlayDraw.Readout documents.
        var ink = TitleInk(mode);
        _titleStyle.normal.textColor = ink;
        _titleStyle.hover.textColor = ink;
        _titleStyle.active.textColor = ink;
        _titlePill.normal.textColor = ink;
        _titlePill.hover.textColor = ink;
        _titlePill.active.textColor = ink;

        // A PILL when it can be clicked, a plain label when it cannot. The title used to be a button
        // wearing a label's style in every state, with a trailing ▾ as the only hint, and in the
        // default Base mode it was painted in the hint colour, so the one control that switches
        // variants was the least button-like thing on the screen. Same frame, same branch in both
        // passes, so the control count cannot disagree.
        var style = switchable ? _titlePill : _titleStyle;

        // A 28 px-high button cannot hold a second line, so this is one of the few places the answer
        // is an ellipsis rather than a wrap, and the full text goes into the tooltip that is already
        // here, so nothing is lost. `name` is the user's, and the intended form is documented on
        // VariantDef.name as "Proposal 08/23/2026. Widen bath door", which is wider than this band.
        string shown = UITheme.Fit(text, style, available);
        bool trimmed = shown != text;

        bool hit = GUILayout.Button(shown, style, GUILayout.Height(BtnH));
        string tip = TitleTip(mode, baselineName, switchable);
        UITheme.Tip(trimmed ? text + ". " + tip : tip);
        if (hit && switchable) ToggleMenuRequested = true;
    }

    private void DrawActions(Mode mode)
    {
        switch (mode)
        {
            case Mode.Base:
                if (UITheme.BandButton("Modify base environment", GUILayout.Height(BtnH)))
                    EditBaseRequested = true;
                UITheme.Tip("Edit the home as it stands. Every proposal is measured against this.");
                if (UITheme.BandButton("New proposal", GUILayout.Height(BtnH)))
                    NewProposalRequested = true;
                UITheme.Tip("Copy the base environment into a new proposal.");
                break;

            case Mode.EditingBase:
                if (UITheme.BandButton("Done", GUILayout.Height(BtnH)))
                    DoneRequested = true;
                UITheme.Tip("Close the base environment. It goes back to read-only.");
                break;

            case Mode.Proposal:
                if (UITheme.BandButton("Compare", GUILayout.Height(BtnH)))
                    CompareRequested = true;
                UITheme.Tip("List what this proposal changes.");
                if (UITheme.BandButton("Report", GUILayout.Height(BtnH)))
                    ReportRequested = true;
                UITheme.Tip("Save a before/after document.");
                break;
        }
    }

    private void DrawMenu(HomeDoc doc, VariantDef current)
    {
        foreach (var v in doc.variants)
        {
            if (v == null) continue;
            bool active = v.id == doc.activeVariantId;
            string state = v.isBaseline
                ? (v.locked ? "base environment · read-only" : "base environment · editing")
                : "proposal";
            bool pick = UITheme.StateRow(v.name ?? "(unnamed)", state, active);
            UITheme.Tip(v.isBaseline
                ? "The home as it stands. Switching is instant."
                : "One proposed design. Switching is instant.");
            if (pick) PickedVariantId = v.id;
        }

        UITheme.Gap();
        GUILayout.BeginHorizontal();
        if (UITheme.BandButton("New proposal", GUILayout.Height(BtnH)))
            NewProposalRequested = true;
        UITheme.Tip("Copy the variant showing now into a new proposal.");
        GUILayout.FlexibleSpace();
        if (current != null && !current.isBaseline)
        {
            // Not "Delete " + name: the name is the user's and can be any length, and the row it sits
            // in already prints it: the band's own title says which proposal is showing, and the
            // tooltip names it again.
            if (UITheme.DangerButton("Delete proposal", GUILayout.Height(BtnH)))
                DeleteVariantId = current.id;
            UITheme.Tip($"Discard {current.name}.");
        }
        GUILayout.EndHorizontal();
    }

    // ---------------------------------------------------------------------------------------

    private static string Changes(int n)
        => n == 1 ? "1 CHANGE" : n + " CHANGES";

    private static string TitleTip(Mode mode, string baselineName, bool switchable)
    {
        string tail = switchable ? " Click to switch." : "";
        return mode switch
        {
            Mode.EditingBase =>
                "Edits here change the home as it stands." + tail,
            Mode.Base =>
                "The home as it stands. Read-only." + tail,
            _ =>
                "A proposed design, counted against " + (baselineName ?? "the base environment")
                + "." + tail,
        };
    }

    private static Color Wash(Mode mode) => mode switch
    {
        Mode.EditingBase => UITheme.WarnTint,
        Mode.Base        => UITheme.Tile,
        _                => UITheme.Tint,
    };

    // The stripe's colour. Slate for the base environment, amber while editing it, accent for a proposal.
    private static Color Ink(Mode mode) => mode switch
    {
        Mode.EditingBase => UITheme.Warn,
        Mode.Base        => UITheme.Ink2,
        _                => UITheme.AccentInk,
    };

    // The title's colour. Same as the stripe except in Base, where the title is set in Ink rather than
    // the slate: Ink2 is the theme's caption colour, and a clickable title painted in it is a button
    // drawn as a hint: the stripe beside it carries the slate on its own.
    private static Color TitleInk(Mode mode) => mode == Mode.Base ? UITheme.Ink : Ink(mode);

    private void EnsureStyles()
    {
        if (_titleStyle != null) return;
        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(0, 8, 0, 0),
            // UITheme sets wordWrap on the base label, and this one is drawn into a fixed 28 px-high
            // button: a wrapped second line would be cut off horizontally. DrawTitle fits the string
            // to the width instead.
            wordWrap = false,
        };
        _titleStyle.normal.background = null;
        _titleStyle.hover.background = null;
        _titleStyle.active.background = null;

        // The same text in a white pill with the button rim. UITheme.BandButtonStyle's chrome on the
        // title's face: for when the title is a control. Padding either side so the ▾ has air.
        var band = UITheme.BandButtonStyle;
        _titlePill = new GUIStyle(_titleStyle)
        {
            padding = new RectOffset(10, 10, 0, 0),
            border  = band.border,
        };
        _titlePill.normal.background = band.normal.background;
        _titlePill.hover.background  = band.hover.background;
        _titlePill.active.background = band.active.background;
    }
}
