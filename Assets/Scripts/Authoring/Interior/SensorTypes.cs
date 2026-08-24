using System;
using System.Collections.Generic;

// The smart home sensing layer: what is installed in the dwelling, and what it is allowed to notice.
//
// Source: Assets/Resources/SmartHomeTechnology/SmartHomeReport.pdf: "Smart Home Technology Solutions
// for Individuals with Intellectual and Developmental Disabilities" (Cornell Tech / Center for Family
// Support, July 2025). Its architecture is three parts, and this file models the first two: peripheral
// sensors (§3.1.2) and the central hub (§3.1.3). The third, remote care access (§3.1.4), is MonitorTool
//: a console over derived state, with nothing of its own to store.
//
// Every device, cost, coverage figure and rule threshold in the catalog traces to a section of that
// report; docs/SMARTHOME.md is the map. Numbers a care team reads off the screen in a funding meeting
// have to be attributable, which is why the provenance is written down rather than remembered.
//
// ---------------------------------------------------------------------------------------------
// WHY A SENSOR HOSTS ON AN ELEMENT RATHER THAN CARRYING COORDINATES
//
// A door sensor belongs to an OpeningDef, a pressure pad to the ObjectInstance of a bed, a stove
// sensor to the range. Storing (x, z) instead would repeat the mistake OccupantDef exists to avoid:
// widen a doorway or move a bed in a proposal, and the sensor is left describing geometry that moved.
// Deriving the pose from the host is exactly the guarantee WallMountDef already gives. Moving a wall
// re-seats everything mounted on it. Extended to the four other things a sensor can hang off.
//
// It also makes the SIMULATION possible at all. "Did someone open the front door" is a question about
// an opening, and "is the stove on" is a question about a cook activity anchored to a range. Both are
// answerable because the sensor names the element, and OccupancyModel already knows where everyone is
// relative to those elements. A coordinate would have to be re-matched to an element every minute.
//
// Sensors live on LevelDef beside `wallMounted`. They are geometry-bound and per-story. The two
// occupant-worn devices reference OccupantDef.id, which hangs off the parent VariantDef rather than
// the level; the same variant, so the reference resolves, and a proposal that changes the household
// changes who is wearing a pendant.

// ---------------------------------------------------------------------------------------------
// The installed device
// ---------------------------------------------------------------------------------------------

[Serializable]
public class SensorDef
{
    public string id;
    public string deviceType;         // catalog key; same key space as SensorCatalog / SensorDevices

    public string hostKind;           // SensorHost.*
    public string hostId;             // the element this hangs off; see SensorHost for which

    // Where it sits, where the host's geometry does not already say. For a Point host (a water
    // sensor on a patch of floor) and a Room host, world [x, z]; null on a Room host falls back to
    // the room's inscribed center. For a Furniture host, the spot on the item's TOP FACE in the
    // item's own unrotated frame, so the device rides the item when it moves or turns. Null means
    // the item's center at the catalog height, which is what every older sensor on disk has.
    //
    // SensorPose ignores this for Opening, Wall and Occupant hosts, whose position IS the host's.
    public float[] position;          // [x, z] meters. See above for whose frame

    // Where along the wall (Wall hosts) and which face (Wall and Opening hosts). Same meaning and
    // units as WallMountDef.offset / .side (absolute meters along a -> b, and WallSide.Left/Right) 
    // so a smart switch re-homes exactly the way a light switch does when its wall is split or
    // moved, and a doorbell knows which face of its wall it sits proud of.
    public float hostOffset;
    public int hostSide;

    public float mountHeight;         // meters AFF; <= 0 => the catalog's default for this device

    // Detection envelope, as a disc optionally narrowed to a cone. <= 0 falls back to the catalog, so
    // a device left alone always reports the manufacturer's figure rather than a zero.
    public float coverageRadius;      // meters
    public float coverageAngle;       // degrees of arc; >= 360 (or <= 0) => omnidirectional
    // Degrees RELATIVE to the host's own facing: SensorPose adds the host's base yaw (a wall face's
    // outward normal, a host item's rotation) back on. Room and Point hosts have no base, so there
    // it is simply world yaw, 0 looking down +Z. PlanBuilder's convention.
    public float facingYaw;

    public string privacy;            // SensorPrivacy.*; <= see the role tiers in §3.1.4 / §5.5

    // False => the device still senses and still drives prompts in the home, but nothing it notices
    // is routed to the console. §5.5's "monitoring without compromising dignity" is a per-device
    // decision in practice, not a whole-system one, so it is a field on the device.
    public bool monitored = true;

    // Field initializer, matching WallMountDef.included and OccupantDef.included: JSON written before
    // this existed has no key, so a device loads as present rather than silently vanishing.
    public bool included = true;

    public string note;               // free text: "resident asked for this", "trial until March"

    // Per-device thresholds. Empty or null means "use the catalog's defaults for this device", which
    // is the normal case: the list only exists because the report's own thresholds are starting
    // points ("customizable threshold like 30-60 minutes", §4.4.2) and a home that keeps tripping a
    // rule needs to move it without every other home moving with it.
    public List<SensorRuleDef> rules;
}

// What a sensor hangs off. The host id points into a different list per kind, which is why this is a
// discriminator rather than five nullable id fields.
public static class SensorHost
{
    /// <summary>OpeningDef.id. Door sensors, smart locks, video doorbells.</summary>
    public const string Opening = "opening";
    /// <summary>ObjectInstance.instanceId. Pads, stove sensors, and everything that sits on a
    /// counter or table: the dispenser, the monitors, an unassigned personal item.</summary>
    public const string Furniture = "furniture";
    /// <summary>RoomDef.id: the bin, the bulb, and older data placed before hosts tightened.</summary>
    public const string Room = "room";
    /// <summary>WallDef.id. Switches, the hub, the thermostat, the radar, motion sensors.</summary>
    public const string Wall = "wall";
    /// <summary>A patch of floor, in SensorDef.position, belonging to no element. Water sensors.</summary>
    public const string Point = "point";
    /// <summary>OccupantDef.id. Worn, so it has no position at all. Pendants, wearables.</summary>
    public const string Occupant = "occupant";

    public static readonly string[] All = { Opening, Furniture, Room, Wall, Point, Occupant };

    public static bool IsKnown(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return false;
        foreach (var k in All) if (k == kind) return true;
        return false;
    }

    public static string Label(string kind) => kind switch
    {
        Opening => "a doorway",
        Furniture => "a piece of furniture",
        Room => "a room",
        Wall => "a wall",
        Point => "a spot on the floor",
        Occupant => "a resident",
        _ => "nothing",
    };
}

// How intrusive a device is, which is what the console's role tiers filter on.
//
// §5.5 asks for monitoring that does not compromise dignity, and §5.3.3 records SimplyHome's own
// position: "no constant cameras; optional entry-way only". A tier per device is what lets the
// console show a family member presence without showing them a camera feed, and lets a resident see
// their own prompts and nothing else. Ordered least to most intrusive; the console compares ordinals.
public static class SensorPrivacy
{
    /// <summary>
    /// Notices nothing and reports nowhere. A sock aid, a rocker knife, a touch-free bin: things that
    /// make a task possible without being connected to anything.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Passive"/>, which means "notices a STATE". Labelling an unpowered aid
    /// "Senses a condition" in the inspector badge would be a small lie in the one place the console's
    /// dignity argument is actually made. It ranks with Passive as least intrusive, which is what makes
    /// it visible to every console role and incapable of ever appearing in an alert.
    /// </remarks>
    public const string None = "none";
    /// <summary>Notices a state, not a person: water on the floor, a stove drawing current.</summary>
    public const string Passive = "passive";
    /// <summary>Notices that someone is there, not who or what they are doing. Motion, pads.</summary>
    public const string Presence = "presence";
    /// <summary>Can speak into the room, or hear it. Hub, speaker, two-way pendant.</summary>
    public const string Audio = "audio";
    /// <summary>Sees. The doorbell, and nothing else in this catalog. deliberately.</summary>
    public const string Video = "video";

    public static readonly string[] All = { None, Passive, Presence, Audio, Video };

    /// <summary>
    /// 0..3, least to most intrusive. Unknown tiers sort as Presence, the common default.
    /// </summary>
    /// <remarks>
    /// None and Passive deliberately TIE at 0. Every role filter compares with &lt;=, so a tie means an
    /// unpowered aid is shown to a family member and to the resident exactly as a water sensor is,
    /// which is right, because neither reports anything about a person. The distinction between them
    /// is what the badge SAYS, not who may see it.
    /// </remarks>
    public static int Rank(string privacy) => privacy switch
    {
        None => 0,
        Passive => 0,
        Presence => 1,
        Audio => 2,
        Video => 3,
        _ => 1,
    };

    public static string Label(string privacy) => privacy switch
    {
        None => "Not connected",
        Passive => "Senses a condition",
        Presence => "Senses that someone is there",
        Audio => "Can speak and listen",
        Video => "Sees",
        _ => "Senses that someone is there",
    };
}

// ---------------------------------------------------------------------------------------------
// Rules
// ---------------------------------------------------------------------------------------------

// One condition that turns sensor events into an alert. The report states these as prose with numbers
// in it: "if the stovetop is left unattended for 45 minutes", "If front door opens after 9 PM", and
// this is that prose made data so a home can move the number without a code change.
//
// A window that wraps past midnight is the normal case here, not the odd one: every night-time rule
// runs from evening to morning. So `windowStart`/`windowEnd` go through Clock.Spans, the same wrap
// ActivityDef uses to express sleep, rather than a hand-rolled comparison that would silently cover
// nothing between 21:00 and 06:00.
[Serializable]
public class SensorRuleDef
{
    public string kind;               // SensorAlertKind.*
    public int thresholdMinutes;      // how long the condition must hold; 0 => fire immediately
    public int windowStart;           // minutes from midnight; equal start and end => all day
    public int windowEnd;
    public string severity;           // SensorSeverity.*
    public bool enabled = true;

    public SensorRuleDef Copy() => new SensorRuleDef
    {
        kind = kind,
        thresholdMinutes = thresholdMinutes,
        windowStart = windowStart,
        windowEnd = windowEnd,
        severity = severity,
        enabled = enabled,
    };

    /// <summary>True when this rule is awake at <paramref name="minutes"/>. All-day when the window is empty.</summary>
    public bool InWindow(int minutes)
        => windowStart == windowEnd || Clock.Spans(windowStart, windowEnd, Clock.Wrap(minutes));
}

// The eight conditions the report describes. String constants rather than an enum for the reason
// RoomType and ActivityKind are: the schema is JSON a person may read and hand-edit, and an unknown
// value degrades to "something happened" instead of deserializing as a meaningless integer.
public static class SensorAlertKind
{
    /// <summary>Stove drawing current far longer than a meal takes. §3.1 scenario, §4.4.2.</summary>
    public const string UnattendedCooktop = "unattended_cooktop";
    /// <summary>An exterior door opening in the night window. §4.1 door sensor row, §4.4.1.</summary>
    public const string NightExit = "night_exit";
    /// <summary>A bed vacated in the small hours and not returned to. §4.3.2.</summary>
    public const string BedExit = "bed_exit";
    /// <summary>No motion where someone is known to be. §4.1 motion sensor row.</summary>
    public const string PossibleFall = "possible_fall";
    /// <summary>A dose dispensed and not taken. §4.2.2.</summary>
    public const string MissedMedication = "missed_medication";
    /// <summary>Water where water should not be. §4.4.3.</summary>
    public const string WaterLeak = "water_leak";
    /// <summary>The pendant button. §4.5.1.</summary>
    public const string Panic = "panic";
    /// <summary>Indoor temperature outside a safe band. §4.2.1.</summary>
    public const string Temperature = "temperature";

    public static readonly string[] All =
    {
        UnattendedCooktop, NightExit, BedExit, PossibleFall,
        MissedMedication, WaterLeak, Panic, Temperature,
    };

    public static bool IsKnown(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return false;
        foreach (var k in All) if (k == kind) return true;
        return false;
    }

    /// <summary>The alert's headline, as a DSP would read it on their phone.</summary>
    public static string Title(string kind) => kind switch
    {
        UnattendedCooktop => "Stove left on",
        NightExit => "Door opened overnight",
        BedExit => "Out of bed overnight",
        PossibleFall => "No movement",
        MissedMedication => "Medication not taken",
        WaterLeak => "Water detected",
        Panic => "Help requested",
        Temperature => "Temperature out of range",
        _ => "Something happened",
    };

    /// <summary>
    /// What the report says a DSP does about it (§3.1 scenario, §5.2.2). Shown as the console's
    /// suggested response, so the tool proposes the intervention the report argues for: a prompt or a
    /// call rather than a drive across town. Rather than leaving the reader to infer it.
    /// </summary>
    public static string SuggestedResponse(string kind) => kind switch
    {
        UnattendedCooktop => "Prompt through the hub, or cut power at the outlet switch",
        NightExit => "Play a verbal prompt, then call if the door stays open",
        BedExit => "Prompt through the hub speaker before checking in person",
        PossibleFall => "Call, then check the camera at the entry if there is no answer",
        MissedMedication => "Call with a reminder; log it for the nurse",
        WaterLeak => "Call to guide the resident, and send someone if it does not stop",
        Panic => "Answer the pendant now; dispatch if there is no reply",
        Temperature => "Adjust the thermostat remotely",
        _ => "Check in with the resident",
    };
}

public static class SensorSeverity
{
    /// <summary>Worth knowing, worth no interruption. Trends, routine state changes.</summary>
    public const string Info = "info";
    /// <summary>Someone should look, soon. A prompt usually settles it.</summary>
    public const string Warning = "warning";
    /// <summary>Someone should act now. Panic, fire risk, a fall.</summary>
    public const string Urgent = "urgent";

    public static readonly string[] All = { Info, Warning, Urgent };

    public static int Rank(string severity) => severity switch
    {
        Urgent => 2,
        Warning => 1,
        _ => 0,
    };

    public static string Label(string severity) => severity switch
    {
        Urgent => "Urgent",
        Warning => "Attention",
        _ => "Information",
    };

    /// <summary>[r,g,b] for the timeline diamond and the console card. Matches UITheme's Danger/Warn/Ink3.</summary>
    public static float[] Swatch(string severity) => severity switch
    {
        Urgent => new[] { 0.70f, 0.15f, 0.12f },
        Warning => new[] { 0.71f, 0.44f, 0.10f },
        _ => new[] { 0.60f, 0.63f, 0.65f },
    };
}

// ---------------------------------------------------------------------------------------------
// What a sensor reports, and what a rule makes of it
// ---------------------------------------------------------------------------------------------

// One thing a device noticed, at one minute of the day. Produced by SensorSim, never stored: the day
// is DERIVED from the household's schedule and the plan, for the same reason occupant positions are.
// A stored event log would be a second copy of the timeline that a proposal could contradict.
public struct SensorEvent
{
    public string sensorId;
    public string deviceType;
    public string kind;               // SensorEventKind.*
    public int minute;                // 0..1439
    public string occupantId;         // who caused it, when that is knowable; null otherwise
    public string detail;             // "Front door", "Bedroom 2". Already presentation-ready

    public override string ToString() => $"{Clock.Format(minute)} {deviceType}: {kind}";
}

public static class SensorEventKind
{
    /// <summary>A state began: motion started, door opened, stove switched on, bed occupied.</summary>
    public const string On = "on";
    /// <summary>That state ended.</summary>
    public const string Off = "off";
    /// <summary>An instant with no duration: a dose dispensed, a button pressed, a leak seen.</summary>
    public const string Trigger = "trigger";

    public static readonly string[] All = { On, Off, Trigger };
}

// One alert a DSP would receive. Also derived. SensorRules.Evaluate is a pure function of the event
// list, so scrubbing the clock, switching variants and undoing all produce a consistent day without
// anything to invalidate but a cache.
public struct SensorAlert
{
    public string id;                 // stable within a simulated day: "<sensorId>@<minute>"
    public string kind;               // SensorAlertKind.*
    public string severity;           // SensorSeverity.*
    public string sensorId;
    public string deviceType;
    public int minute;                // when it would land on the caregiver's phone
    public int sinceMinute;           // when the condition started; == minute for instant alerts
    public string occupantId;         // who it concerns, when knowable
    public string where;              // room or element name, presentation-ready
    public string body;               // the sentence the console and the report both show

    public string Title => SensorAlertKind.Title(kind);
    public string Response => SensorAlertKind.SuggestedResponse(kind);

    /// <summary>How long the condition had held when the alert fired.</summary>
    public int HeldMinutes => Clock.DurationBetween(sinceMinute, minute);

    public override string ToString() => $"{Clock.Format(minute)} {Title}: {body}";
}
