using System.Collections.Generic;
using UnityEngine;

// Turns what the devices noticed into what a caregiver is told.
//
// The report states each of these as a sentence with a number in it: "if the stovetop is left
// unattended for 45 minutes", "If front door opens after 9 PM, alert caregiver and play verbal
// prompt", "alerts for prolonged absence (e.g., after 10-30 minutes)". SensorRuleDef is that sentence
// as data and this file is the evaluator, so a residence that keeps tripping a threshold can move it
// without a code change and without moving it for every other residence.
//
// PURE, and separate from SensorSim on purpose. The simulation decides what happened; this decides
// what is worth waking someone for. Keeping them apart is what lets the tests feed a hand-built
// six-event day straight in and assert a single alert, with no plan, no household and no clock.
//
// ---------------------------------------------------------------------------------------------
// THE FALSE-ALARM FLOOR
//
// §4 lists reliability and false alarms as an evaluation criterion, and the failure mode is not
// missing an alert. It is raising so many that staff stop reading them. Three things here exist for
// that and nothing else:
//
//   * A rule only fires inside its WINDOW. A door opening at 09:00 is someone going to work.
//   * A condition must HOLD for its threshold. A bed empty for four minutes is a trip to the toilet.
//   * A fall needs someone still in the room AND AWAKE. Without that last clause every sleeping
//     resident raises a fall alert every night, which is the single most obvious way to build a
//     system nobody keeps switched on.
//
// SensorSimTests asserts an ordinary day on all six samples produces zero alerts.
public static class SensorRules
{
    /// <summary>
    /// Evaluates every installed rule over a day's events. Events must be sorted by minute. Simulate
    /// sorts before calling, and a hand-built test day is written in order.
    /// </summary>
    public static List<SensorAlert> Evaluate(List<SensorEvent> events, LevelDef level,
                                             VariantDef variant = null)
    {
        var alerts = new List<SensorAlert>();
        if (events == null || events.Count == 0 || level?.sensors == null) return alerts;

        foreach (var sensor in level.sensors)
        {
            if (sensor == null || !sensor.included || string.IsNullOrEmpty(sensor.id)) continue;

            // A device switched out of monitoring still senses and still prompts locally. It simply
            // does not reach staff. §5.5 makes that a per-device decision, so it is honoured here
            // rather than at the console, where an unmonitored device would still generate the alert
            // and merely hide it.
            if (!sensor.monitored) continue;

            var mine = EventsFor(events, sensor.id);
            if (mine.Count == 0) continue;

            foreach (var rule in SensorDevices.EffectiveRules(sensor))
            {
                if (rule == null || !rule.enabled) continue;
                Apply(rule, sensor, mine, level, variant, alerts);
            }
        }

        alerts.Sort((a, b) => a.minute.CompareTo(b.minute));
        return alerts;
    }

    // ---------------------------------------------------------------------------------------

    private static void Apply(SensorRuleDef rule, SensorDef sensor, List<SensorEvent> mine,
                              LevelDef level, VariantDef variant, List<SensorAlert> alerts)
    {
        switch (rule.kind)
        {
            // The stove has TWO event streams (the hob, and the hob with nobody in the room) and the
            // threshold belongs to the second. §3.1's scenario is "left UNATTENDED for 45 minutes",
            // against sessions that normally run 15-20; measuring the hob alone raises an alert on
            // every meal that takes three quarters of an hour, and it did, on the samples.
            case SensorAlertKind.UnattendedCooktop:
                OnSpans(rule, sensor, mine, level, variant, alerts,
                        SensorEventKind.On, SensorEventKind.Off, SensorSim.Unattended);
                break;

            // A state that goes on and stays on too long: a temperature excursion.
            case SensorAlertKind.Temperature:
                OnSpans(rule, sensor, mine, level, variant, alerts, SensorEventKind.On, SensorEventKind.Off);
                break;

            // A state that goes OFF and stays off too long: an empty bed, a room that went still.
            case SensorAlertKind.BedExit:
            case SensorAlertKind.PossibleFall:
                OnSpans(rule, sensor, mine, level, variant, alerts, SensorEventKind.Off, SensorEventKind.On);
                break;

            // An instant, in a window: the door in the night.
            case SensorAlertKind.NightExit:
            case SensorAlertKind.WaterLeak:
            case SensorAlertKind.Panic:
                OnTriggers(rule, sensor, mine, level, variant, alerts);
                break;

            case SensorAlertKind.MissedMedication:
                OnDoses(rule, sensor, mine, level, variant, alerts);
                break;
        }
    }

    /// <summary>
    /// Walks the spans that begin with <paramref name="opens"/> and end with <paramref name="closes"/>,
    /// raising one alert per span that outlasts the threshold. Timed at the moment the threshold is
    /// crossed, not when the span began or ended.
    /// </summary>
    /// <remarks>
    /// The minute matters more than it looks. An alert stamped at the span's START would tell a DSP a
    /// stove was left on at 18:20 when the system could not have known until 19:05, and the timeline
    /// would show the alert before the cause. Stamping it at the END would be worse still. It would
    /// only appear once the situation had already resolved itself.
    ///
    /// A span left open at the end of the day still fires, because a stove on at 23:30 and never
    /// turned off is the most alarming case there is, not an incomplete record.
    /// </remarks>
    /// <param name="detail">
    /// When set, only events carrying this marker are walked: how the stove's unattended stream is
    /// separated from the hob's, which the plan tint follows. Null walks the device's main stream and
    /// skips every marked event, so the two can never be spliced into one span.
    /// </param>
    private static void OnSpans(SensorRuleDef rule, SensorDef sensor, List<SensorEvent> mine,
                                LevelDef level, VariantDef variant, List<SensorAlert> alerts,
                                string opens, string closes, string detail = null)
    {
        int start = -1;
        string who = null;

        foreach (var e in mine)
        {
            if (detail != null ? e.detail != detail : IsMarked(e)) continue;

            if (e.kind == opens)
            {
                if (start < 0) { start = e.minute; who = e.occupantId; }
                continue;
            }
            if (e.kind != closes || start < 0) continue;

            TryRaise(rule, sensor, start, e.minute, who, level, variant, alerts);
            start = -1;
            who = null;
        }

        if (start >= 0) TryRaise(rule, sensor, start, Clock.MinutesPerDay, who, level, variant, alerts);
    }

    private static void TryRaise(SensorRuleDef rule, SensorDef sensor, int start, int end, string who,
                                 LevelDef level, VariantDef variant, List<SensorAlert> alerts)
    {
        int held = end - start;
        if (held < rule.thresholdMinutes) return;

        int at = Clock.Wrap(start + rule.thresholdMinutes);
        if (!rule.InWindow(at)) return;

        // A fall is the one rule that needs to know a person was there. SensorSim writes that onto the
        // Off event (and only when they are awake) so an empty room going quiet raises nothing.
        if (rule.kind == SensorAlertKind.PossibleFall && string.IsNullOrEmpty(who)) return;

        alerts.Add(Build(rule, sensor, at, start, who, level, variant));
    }

    private static void OnTriggers(SensorRuleDef rule, SensorDef sensor, List<SensorEvent> mine,
                                   LevelDef level, VariantDef variant, List<SensorAlert> alerts)
    {
        foreach (var e in mine)
        {
            if (e.kind != SensorEventKind.Trigger || IsMarked(e)) continue;
            if (!rule.InWindow(e.minute)) continue;

            // A door in the night is only a wandering alert when it is a way OUT. Interior doors carry
            // the same device and the same rule, and a bathroom trip at 3 AM is not an elopement.
            if (rule.kind == SensorAlertKind.NightExit && !IsExit(sensor, level)) continue;

            alerts.Add(Build(rule, sensor, e.minute, e.minute, e.occupantId, level, variant));
        }
    }

    /// <summary>
    /// A dose presented and not confirmed taken within the grace period. Pairs each "due" with the
    /// next "taken"; an unpaired due is the missed dose.
    /// </summary>
    private static void OnDoses(SensorRuleDef rule, SensorDef sensor, List<SensorEvent> mine,
                                LevelDef level, VariantDef variant, List<SensorAlert> alerts)
    {
        for (int i = 0; i < mine.Count; i++)
        {
            if (mine[i].detail != SensorSim.DoseDue) continue;

            int due = mine[i].minute;
            bool taken = false;

            for (int j = i + 1; j < mine.Count; j++)
            {
                if (mine[j].detail != SensorSim.DoseTaken) continue;
                if (mine[j].minute - due > rule.thresholdMinutes) break;
                taken = true;
                break;
            }

            if (taken) continue;

            int at = Clock.Wrap(due + rule.thresholdMinutes);
            if (rule.InWindow(at)) alerts.Add(Build(rule, sensor, at, due, mine[i].occupantId, level, variant));
        }
    }

    // ---------------------------------------------------------------------------------------

    private static SensorAlert Build(SensorRuleDef rule, SensorDef sensor, int at, int since,
                                     string who, LevelDef level, VariantDef variant)
    {
        var pose = SensorPose.Resolve(sensor, level, variant);
        string where = pose.hostLabel ?? SensorPose.RoomName(pose.room);
        string name = NameOf(who, variant);

        return new SensorAlert
        {
            id = sensor.id + "@" + at,
            kind = rule.kind,
            severity = string.IsNullOrEmpty(rule.severity) ? SensorSeverity.Warning : rule.severity,
            sensorId = sensor.id,
            deviceType = sensor.deviceType,
            minute = at,
            sinceMinute = since,
            occupantId = who,
            where = where,
            body = Body(rule.kind, at, since, where, name),
        };
    }

    /// <summary>
    /// The sentence the console shows and the report prints. Written the way the report writes them,
    /// "Door open for 30 minutes at 2 AM" (§3.1.4), because these are read aloud in a meeting and
    /// "ALERT: SENSOR_3 THRESHOLD EXCEEDED" is not a sentence anyone can act on.
    /// </summary>
    private static string Body(string kind, int at, int since, string where, string name)
    {
        int held = Clock.DurationBetween(since, at);
        string when = Clock.Format(at);
        string who = string.IsNullOrEmpty(name) ? "Someone" : name;

        return kind switch
        {
            SensorAlertKind.UnattendedCooktop =>
                $"The stove has been on for {held} minutes, since {Clock.Format(since)}.",
            SensorAlertKind.NightExit =>
                $"{where} opened at {when}.",
            SensorAlertKind.BedExit =>
                $"{who} left bed at {Clock.Format(since)} and has not returned.",
            SensorAlertKind.PossibleFall =>
                $"No movement in {where.ToLowerInvariant()} for {held} minutes, and {who} is in there.",
            SensorAlertKind.MissedMedication =>
                $"The {Clock.Format(since)} dose has not been taken.",
            SensorAlertKind.WaterLeak =>
                $"Water on the floor in {where.ToLowerInvariant()}, at {when}.",
            SensorAlertKind.Panic =>
                $"{who} pressed their pendant at {when}.",
            SensorAlertKind.Temperature =>
                $"{where} has been outside its safe temperature range for {held} minutes.",
            _ => $"{where}, at {when}.",
        };
    }

    private static string NameOf(string occupantId, VariantDef variant)
    {
        var person = SensorPose.Find(variant?.occupants, o => o.id, occupantId);
        return person?.name;
    }

    private static bool IsExit(SensorDef sensor, LevelDef level)
    {
        if (sensor.hostKind != SensorHost.Opening) return false;
        var opening = SensorPose.Find(level?.openings, o => o.id, sensor.hostId);
        return SensorCoverage.IsExteriorDoor(opening, level);
    }

    /// <summary>
    /// True for an event belonging to a device's SECOND stream rather than its main one. Kept in one
    /// place so adding another derived condition later cannot leak into the spans of the first.
    /// </summary>
    public static bool IsMarked(SensorEvent e)
        => e.detail == SensorSim.Unattended
        || e.detail == SensorSim.DoseDue
        || e.detail == SensorSim.DoseTaken;

    private static List<SensorEvent> EventsFor(List<SensorEvent> events, string sensorId)
    {
        var mine = new List<SensorEvent>();
        foreach (var e in events) if (e.sensorId == sensorId) mine.Add(e);
        return mine;
    }
}
