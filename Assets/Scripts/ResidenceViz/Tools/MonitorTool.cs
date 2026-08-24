using System.Collections.Generic;
using UnityEngine;

// The remote care console: the third of the report's three components (§3.1.4), and the only one
// that is not a thing in the plan.
//
// Sensors and the hub are objects you install; remote care access is what a Direct Support
// Professional actually holds. So this tool authors nothing. It is a READ of the simulated day, laid
// out the way §3.1.4 describes the Responder App: alerts pushed with details, live sensor state,
// per-resident status, and the intervention offered right there rather than left to be inferred.
//
// ---------------------------------------------------------------------------------------------
// THE ROLE SWITCH IS THE POINT
//
// §5.3.3 gives DSPs full intervention, family members trends and view-only access, and residents a
// simplified surface; §5.5 asks for all of it "without compromising dignity or autonomy". Every
// version of that argument is a paragraph, and a paragraph is exactly what this app has no room for.
//
// Switching the role instead SHOWS it: as Family, the camera disappears from the device list, the
// alerts stop naming which room anyone is in, and the resident's position stops being reported at all.
// A care team can see what their sister will and will not be able to see, in one click, before anyone
// signs anything. That is the ethics section made checkable.
//
// ---------------------------------------------------------------------------------------------
// RESPONSES ARE CONSOLE STATE, NEVER DOCUMENT STATE
//
// Acknowledging an alert must not dirty the file or land in the undo stack. It is the same rule
// OccupancyClock lives by, for the same reason: scrubbing a day and answering the alerts in it is a
// read, and a read that marks a residence unsaved is a bug people learn to distrust the app for.
public class MonitorTool : ResidenceToolBase
{
    public override string Id => "monitor";
    public override string DisplayName => "Monitor";

    public override string Hint =>
        "What a caregiver would see. Play the day in the timeline and alerts land here as they would "
        + "on a phone; switch the role to see exactly what a family member or the resident can see.";

    /// <summary>Who is looking. §5.3.3's three tiers, in order of how much they may see.</summary>
    private enum Role { Dsp, Family, Resident }

    private Role _role = Role.Dsp;
    private Vector2 _scroll;

    // Answered alerts, by alert id. Transient by design. See the file header.
    private readonly HashSet<string> _answered = new HashSet<string>();
    private readonly Dictionary<string, string> _responses = new Dictionary<string, string>();

    private bool _showCoverage = true;
    private bool _showCost;
    private bool _showDevices;

    // Coverage stays on across the whole Smart living stage, so switching between placing devices and
    // watching what they catch does not blink the picture off and on.
    public override void Enter(ResidenceToolContext ctx)
    {
        base.Enter(ctx);
        if (ctx?.Controller != null) ctx.Controller.SensorCoverageVisible = true;
    }

    public override void Exit()
    {
        if (Ctx?.Controller != null) Ctx.Controller.SensorCoverageVisible = false;
        base.Exit();
    }

    private ResidenceRenderer Renderer => Ctx?.Renderer;
    private SensorSim.Day Day => Renderer != null ? Renderer.SensorDay : default;
    private int Now => Renderer != null ? Renderer.Occupancy.Now : 0;

    // ---------------------------------------------------------------------------------------

    public override void DrawRail()
    {
        var level = Ctx?.Level;
        if (level == null) return;

        if (level.sensors == null || level.sensors.Count == 0)
        {
            // The action IS the empty state, carrying the sentence on hover: the rule the whole UI
            // follows, and the reason there is no paragraph here.
            if (UITheme.PrimaryButton("Install some devices first"))
                Ctx.Controller.RequestTool("sensor");
            UITheme.Tip("Nothing is watching this residence yet, so there is nothing for a caregiver to "
                      + "see. The Equipment tab is where a package goes in.");
            return;
        }

        DrawRolePicker();
        DrawDayPicker();

        UITheme.Gap();
        DrawAlerts();

        UITheme.Gap();
        DrawResidents();

        DrawFoldouts();
    }

    private void DrawRolePicker()
    {
        _role = (Role)UITheme.Segmented("Viewing as", (int)_role,
            new[] { "DSP", "Family", "Resident" },
            new[]
            {
                "A Direct Support Professional: every alert, every device, and the ability to respond. "
                + "§5.3.3 names them the primary responder.",
                "A family member: trends and wellbeing, no camera, and no report of where anyone is. "
                + "§5.3.3 gives family the web portal.",
                "The resident's own view: their prompts and their pendant, and nothing about anybody "
                + "else in the residence.",
            });
    }

    // Which day is on screen, and it says which. A demonstration mistaken for a prediction is the one
    // way this feature could mislead a funding meeting, so the label is a control rather than a note.
    private void DrawDayPicker()
    {
        if (Renderer == null) return;

        int mode = Renderer.SensorDayMode == SensorSim.Mode.Routine ? 0 : 1;
        // "Typical" / "Incidents", not "Typical day" / "Day with incidents": the row is labelled Day,
        // so repeating the word inside both cells spent the width that then forced an ellipsis on it.
        // The tooltips are unchanged and still say which day this is in full.
        int next = UITheme.Segmented("Day", mode,
            new[] { "Typical", "Incidents" },
            new[]
            {
                "The household's ordinary day. A package that is working raises nothing at all here, "
                + "which is the point, and what the tests check.",
                "The same day with the report's own scenarios acted out on this residence's devices: the "
                + "stove left on, the 3 AM door, a fall, a missed dose. A demonstration of what the package catches.",
            });

        if (next != mode)
            Renderer.SensorDayMode = next == 0 ? SensorSim.Mode.Routine : SensorSim.Mode.Eventful;
    }

    // ---------------------------------------------------------------------------------------
    // Alerts
    // ---------------------------------------------------------------------------------------

    private void DrawAlerts()
    {
        var alerts = SensorSim.AlertsAround(Day, Now, AlertWindow);
        var visible = new List<SensorAlert>();
        foreach (var a in alerts) if (CanSee(a)) visible.Add(a);

        UITheme.Header($"Alerts · {Clock.Format(Now)}");

        if (visible.Count == 0)
        {
            UITheme.StatusBadge("All quiet", true);
            UITheme.Tip(alerts.Count == 0
                ? $"Nothing in the last {AlertWindow} minutes. Run the clock in the timeline to walk "
                  + "the day."
                : $"{alerts.Count} alert(s) in the last {AlertWindow} minutes, none of them visible "
                  + "to this role.");
            return;
        }

        _scroll = UITheme.BeginScroll(_scroll, GUILayout.Height(Mathf.Min(240f, 76f * visible.Count)));
        foreach (var alert in visible) DrawAlertCard(alert);
        UITheme.EndScroll();
    }

    /// <summary>How far back the console looks. §3.1.4's example alert is 30 minutes old.</summary>
    private const int AlertWindow = 60;

    private void DrawAlertCard(SensorAlert alert)
    {
        bool answered = _answered.Contains(alert.id);
        string when = Clock.Format(alert.minute);

        // The title carries the severity glyph rather than a coloured strip: a strip is decoration, a
        // glyph survives being read by someone who cannot distinguish the colours.
        string glyph = alert.severity == SensorSeverity.Urgent ? "⚠"
                     : alert.severity == SensorSeverity.Warning ? "•" : "·";

        if (UITheme.StateRow($"{glyph} {alert.Title}", when, !answered, muted: answered))
            Focus(alert);
        UITheme.Tip(Body(alert) + "\n\n" + SensorSeverity.Label(alert.severity)
                    + ". Click to find the device in the plan.");

        UITheme.MutedLine(Body(alert));

        if (answered)
        {
            _responses.TryGetValue(alert.id, out string what);
            UITheme.StatusBadge(string.IsNullOrEmpty(what) ? "Answered" : what, true);
            return;
        }

        // Only a DSP may act. §5.3.3 is explicit that family have view-only access and that residents
        // use a simplified surface, so the buttons are simply not drawn, rather than drawn disabled
        // with an explanation nobody can act on.
        if (_role != Role.Dsp)
        {
            UITheme.MutedLine("View only", "§5.3.3: family members and residents see what is "
                                         + "happening; a DSP is the responder.");
            return;
        }

        var row = UITheme.ChipRow();
        row.Label("Respond");
        if (row.Chip("Prompt", false)) Answer(alert, "Prompted through the hub");
        UITheme.Tip("Speak the prompt into the room. It is the response almost every scenario in the "
                  + "report ends in, and the one that saves somebody the drive over.");
        if (row.Chip("Call", false)) Answer(alert, "Called");
        UITheme.Tip("Call the resident.");
        if (row.Chip("Check", false)) Answer(alert, "Checked the entry camera");
        UITheme.Tip("Look at the entry camera. It is the only one in the residence. §4.5.2.");
        if (row.Chip("Dispatch", false)) Answer(alert, "Sent someone");
        UITheme.Tip("Send a person. §5.2.2 puts this at $20 to $40 of labor, which is what the other "
                  + "three save when they settle it.");
        row.End();

        UITheme.MutedLine(alert.Response, "What the report suggests for this kind of alert.");
    }

    private void Answer(SensorAlert alert, string what)
    {
        _answered.Add(alert.id);
        _responses[alert.id] = what;
        // Deliberately no RecordEdit and no MarkDirty. See the file header.
        Ctx.Controller.Status($"{what}: {alert.Title.ToLowerInvariant()} at {Clock.Format(alert.minute)}.");
    }

    private void Focus(SensorAlert alert)
    {
        // reveal: false, exactly as CompareTool's rows do: clicking an alert must not eject you from
        // the console you are working in.
        Ctx.Controller.Select(ResidenceElementMarker.Kind.Sensor, alert.sensorId, reveal: false);
        Ctx.Controller.FocusElement(alert.sensorId);
    }

    /// <summary>
    /// Whether this role may see this alert at all, and the first half of the privacy tiers.
    /// </summary>
    private bool CanSee(SensorAlert alert)
    {
        if (_role == Role.Dsp) return true;

        var sensor = ResidenceRenderer.FindSensor(alert.sensorId, Ctx.Level);
        string privacy = SensorDevices.PrivacyOf(sensor);

        // Family never see the camera. §5.3.3 records SimplyHome's own position: "no constant
        // cameras; optional entry-way only", and the entry-way camera is the one device whose feed a
        // resident has the strongest claim to keep from their relatives.
        if (_role == Role.Family) return privacy != SensorPrivacy.Video;

        // A resident sees what concerns them and nothing about anyone else in a shared home.
        return alert.occupantId == null || alert.occupantId == FirstResidentId();
    }

    /// <summary>
    /// The alert text this role is allowed to read, and the second half of the tiers.
    /// </summary>
    /// <remarks>
    /// A family member is told that something needs attention and where the DEVICE is, not where the
    /// person is. "No movement in bedroom 3 for 10 minutes, and Alice is in there" is a DSP's sentence;
    /// a relative gets "Something needs attention in bedroom 3". That difference is the whole of §5.5
    /// in one string, and it is checkable rather than promised.
    /// </remarks>
    private string Body(SensorAlert alert)
    {
        if (_role == Role.Dsp) return alert.body;
        if (_role == Role.Resident) return alert.body;
        return $"Something needs attention: {alert.where}.";
    }

    private string FirstResidentId()
    {
        var roster = Ctx?.Variant?.occupants;
        if (roster == null) return null;
        foreach (var p in roster) if (p != null && p.included) return p.id;
        return null;
    }

    // ---------------------------------------------------------------------------------------
    // Residents
    // ---------------------------------------------------------------------------------------

    private void DrawResidents()
    {
        var roster = Ctx?.Variant?.occupants;
        if (roster == null || roster.Count == 0) return;

        UITheme.Header("Residents");

        var poses = Renderer != null ? Renderer.CurrentPoses() : null;

        foreach (var person in roster)
        {
            if (person == null || !person.included) continue;

            string where = "Unknown";
            if (_role == Role.Dsp && poses != null && poses.TryGetValue(person.id, out var pose))
                where = OccupancyModel.Describe(pose);
            else if (_role != Role.Dsp)
                where = HasPendant(person) ? "Pendant on" : "At residence";

            if (UITheme.StateRow(person.name ?? "Resident", where, false))
                Ctx.Controller.FocusElement(person.id, person.name);

            UITheme.Tip(_role == Role.Dsp
                ? "Where they are now, derived from their schedule and the clock. Click to find them."
                : "§5.3.3: family and residents see wellbeing.");
        }
    }

    private bool HasPendant(OccupantDef person)
    {
        var sensors = Ctx?.Level?.sensors;
        if (sensors == null) return false;
        foreach (var s in sensors)
            if (s != null && s.included && s.hostKind == SensorHost.Occupant && s.hostId == person.id)
                return true;
        return false;
    }

    // ---------------------------------------------------------------------------------------
    // Coverage, cost, devices
    // ---------------------------------------------------------------------------------------

    private void DrawFoldouts()
    {
        var level = Ctx.Level;

        _showCoverage = UITheme.Foldout(_showCoverage, "Coverage");
        if (_showCoverage)
        {
            // Whole BUILDING, not the story on screen. These read as claims about the residence, and the
            // one thing a care team must not discover later is the way out on a floor nobody was
            // looking at when the figure was quoted. Same numbers the report prints, from the same
            // functions, so the console and the document cannot disagree.
            var variant = Ctx.Variant;
            float whole = SensorCoverage.WholeResidenceCoverage(variant);
            UITheme.Value("Coverage", $"{whole * 100f:0}%",
                          "The share of this residence's floor a movement sensor can see, across every "
                          + "story. Measured on the same grid the occupancy model stands people on, "
                          + "and clipped to each sensor's own room, because a sensor does not see through a "
                          + "wall.");

            int exits = SensorCoverage.ExitCount(variant);
            UITheme.Value("Ways out", $"{exits - SensorCoverage.UnmonitoredExitCount(variant)}"
                          + $" of {exits} watched",
                          "§4.4.1: wandering is the risk this addresses, and it fails on the door "
                          + "nobody thought about.");

            var seenGaps = new System.Collections.Generic.HashSet<string>();
            foreach (var lvl in variant?.levels ?? new System.Collections.Generic.List<LevelDef>())
                foreach (var gap in SensorCoverage.Gaps(lvl, variant))
                {
                    if (!seenGaps.Add(gap.text)) continue;
                    UITheme.Glyph(gap.severity == SensorSeverity.Urgent ? "⚠" : "•", gap.text,
                                  gap.severity == SensorSeverity.Urgent ? UITheme.Danger : UITheme.Warn);
                }
        }

        _showCost = UITheme.Foldout(_showCost, "Cost");
        if (_showCost) DrawCost(Ctx.Variant);

        // The device list stays per story, unlike the figures above. It is a list you click to select
        // and focus a device, and selection follows the level being rendered. Offering a row that
        // cannot be reached without switching floors first would be a control that does nothing.
        _showDevices = UITheme.Foldout(_showDevices, "Devices");
        if (_showDevices) DrawDevices(level);
    }

    private void DrawCost(VariantDef variant)
    {
        var cost = SensorCost.Of(variant);

        UITheme.Value("To install", cost.UpfrontRange,
                      "The report's own purchase ranges, added up. §4.1 and each §4 subsection's "
                      + "vendor list, plus typical retail for anything from Everyday living.");

        UITheme.Value("A month", cost.MonthlyRange,
                      "§5.4 prices the system as one hub plus its sensors on ONE monthly fee, so only "
                      + "the hub, the pendants and the dispenser carry a monthly here. Adding one per "
                      + "device would count the system fee five times over.");

        SensorCost.MonthlySaving(out float low, out float high);
        UITheme.Value("Labor saved", "−" + SensorCost.Money(low) + " - " + SensorCost.Money(high),
                      $"At {SensorCost.AssumedIncidentsPerWeek:0} incidents a week, each answered "
                      + "remotely. §5.2.2 puts that at $20 to $40 an incident.\n\n"
                      + "This is an assumption you can disagree with. The demonstration day acts "
                      + "out seven scenarios at once to show what "
                      + "the package catches, and treating it as typical would inflate this fivefold.");
    }

    private void DrawDevices(LevelDef level)
    {
        foreach (var row in SensorCost.ByDevice(level))
        {
            var device = SensorDevices.Get(row.Key);

            // The camera is simply absent for a family member: the same rule the alerts follow, so
            // the two halves of the console cannot contradict each other about what a role can see.
            if (_role == Role.Family && device.privacy == SensorPrivacy.Video) continue;

            if (UITheme.StateRow(device.displayName ?? row.Key, "×" + row.Value, false))
                SelectFirst(level, row.Key);

            UITheme.Tip($"{SensorPrivacy.Label(device.privacy)}. "
                        + SensorCost.Money(device.purchaseLow) + " - "
                        + SensorCost.Money(device.purchaseHigh) + " each. Click to find one.");
        }
    }

    private void SelectFirst(LevelDef level, string deviceType)
    {
        foreach (var s in level.sensors)
        {
            if (s == null || s.deviceType != deviceType) continue;
            Ctx.Controller.Select(ResidenceElementMarker.Kind.Sensor, s.id, reveal: false);
            Ctx.Controller.FocusElement(s.id);
            return;
        }
    }

    // ---------------------------------------------------------------------------------------

    public override void DrawOverlay()
    {
        if (Ctx?.Cam == null || Ctx.Level == null) return;

        // Alert pins in the plan, so the console and the residence agree about where something is
        // happening. Only the ones this role may see: the filter is the same one the cards use.
        foreach (var alert in SensorSim.AlertsAround(Day, Now, AlertWindow))
        {
            if (!CanSee(alert)) continue;

            var sensor = ResidenceRenderer.FindSensor(alert.sensorId, Ctx.Level);
            if (sensor == null) continue;

            var pose = SensorPose.Resolve(sensor, Ctx.Level, Ctx.Variant);
            if (!pose.resolved) continue;
            if (!OverlayDraw.ToScreen(Ctx.Cam, pose.xz, Ctx.Level.elevation, out Vector2 g)) continue;

            var rgb = SensorSeverity.Swatch(alert.severity);
            var color = new Color(rgb[0], rgb[1], rgb[2], _answered.Contains(alert.id) ? 0.35f : 1f);

            OverlayDraw.Circle(g, 14f, color, 20, 3f);
            OverlayDraw.Readout(g, alert.Title);
        }
    }
}
