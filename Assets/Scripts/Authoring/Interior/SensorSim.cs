using System.Collections.Generic;
using UnityEngine;

// A day in the life of the sensing layer, derived from the household's own schedule.
//
// THIS IS WHY THE SENSORS HOST ON ELEMENTS. OccupancyModel already answers "where is everyone at
// minute m" from the schedule and the plan; a sensor that names the opening, the bed or the range it
// watches turns that into "did anyone go through the front door", "is the bed empty", "is the hob on".
// Nothing is scripted per home: move a resident to a different bedroom in a proposal, and a different
// motion sensor is the one that goes quiet.
//
// PURE AND DETERMINISTIC. Given the same variant, level, mode and seed this returns the same day, every
// time, which it has to, because the timeline, the console, the report and the tests all describe the
// same day and would contradict each other otherwise. Nothing is stored: a simulated day is derived
// from the document exactly as occupant positions are, for the same reason (a stored event log is a
// second copy of the timeline that a proposal can contradict).
//
// ---------------------------------------------------------------------------------------------
// TWO MODES, AND THE REASON THERE ARE TWO
//
// A correct smart home on a normal day raises NOTHING. That is the system working, and it is what
// §4's reliability criterion is about: a package that cries wolf is a package staff learn to ignore.
// So Mode.Routine simulates the household's ordinary day and the tests assert it produces zero
// alerts on all six samples: the false-alarm floor, checked rather than hoped for.
//
// But a package that raises nothing also SHOWS nothing, and the argument the report makes is entirely
// about the exceptional day: the stove left on, the 3 AM door. Mode.Eventful injects a small set of
// incidents drawn one-for-one from the report's own scenarios, each landing on this home's real
// people and real devices at a fixed time. It is a demonstration and it says so; the UI labels the
// two "Typical day" and "Day with incidents" rather than letting anyone mistake the second for a
// prediction.
public static class SensorSim
{
    public enum Mode
    {
        /// <summary>The household's ordinary day. A correct package raises nothing here.</summary>
        Routine,
        /// <summary>Routine, plus the report's scenarios acted out on this home's own devices.</summary>
        Eventful,
    }

    /// <summary>When the dispenser presents a dose. §4.2.2 prices "up to 4 doses/day".</summary>
    public static readonly int[] DoseTimes = { 8 * 60, 12 * 60, 18 * 60, 22 * 60 };

    public struct Day
    {
        public Mode mode;
        public int seed;
        public List<SensorEvent> events;
        public List<SensorAlert> alerts;

        public bool Any => alerts != null && alerts.Count > 0;
        public int AlertCount => alerts?.Count ?? 0;

        /// <summary>Alerts per day, which is what SensorCost's remote-response offset multiplies.</summary>
        public float AlertsPerDay => AlertCount;

        public static Day Empty(Mode mode, int seed) => new Day
        {
            mode = mode, seed = seed,
            events = new List<SensorEvent>(),
            alerts = new List<SensorAlert>(),
        };
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Runs a whole day, minute by minute, and evaluates the rules over what the devices noticed.
    /// </summary>
    public static Day Simulate(VariantDef variant, LevelDef level, Mode mode = Mode.Eventful, int seed = 0)
    {
        if (level?.sensors == null || level.sensors.Count == 0) return Day.Empty(mode, seed);

        var devices = Prepare(variant, level);
        if (devices.Count == 0) return Day.Empty(mode, seed);

        var events = new List<SensorEvent>();

        // Where everyone was a minute ago, so a change of room can be recognised as a passage through
        // a door. Local to this run rather than static: several door sensors read the same transition,
        // so it has to be computed ONCE per minute and shared, not tracked per device.
        var previousRooms = new Dictionary<string, string>();
        var moves = new List<Move>();

        // One pass over the day. PoseAll is the only expensive call in here and its grid search is
        // memoised on the level, so a second simulation of the same plan costs almost nothing.
        for (int minute = 0; minute < Clock.MinutesPerDay; minute++)
        {
            var poses = OccupancyModel.PoseAll(variant, level, minute);
            CollectMoves(poses, previousRooms, moves);
            foreach (var d in devices) Step(d, devices, minute, poses, moves, variant, level, events);
        }

        // Anything still on at the end of the day is closed off, rather than wrapped into tomorrow.
        // A condition that straddles midnight is evaluated within the day it started: the timeline is
        // 24 hours and repeats, so wrapping would double-count the same stove on both ends of it.
        foreach (var d in devices)
            if (d.on) events.Add(Event(d, SensorEventKind.Off, Clock.MinutesPerDay - 1, null));

        if (mode == Mode.Eventful) InjectIncidents(devices, variant, level, seed, events);

        events.Sort((a, b) => a.minute.CompareTo(b.minute));

        return new Day
        {
            mode = mode,
            seed = seed,
            events = events,
            alerts = SensorRules.Evaluate(events, level, variant),
        };
    }

    // ---------------------------------------------------------------------------------------
    // One device, resolved once and then stepped through the day
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A sensor with everything it needs already looked up. Resolving the host, the envelope and the
    /// room inside the minute loop would repeat 1,440 times what changes never.
    /// </summary>
    internal class Live
    {
        public SensorDef def;
        public SensorDevices.Device device;
        public SensorPose.Pose pose;
        public SensorCoverage.Envelope envelope;

        public OpeningDef opening;     // Opening hosts
        public ObjectInstance item;    // Furniture hosts
        public string roomId;          // whichever room it watches, for naming and occupancy tests
        public string label;

        public bool on;                // current state, carried across minutes
        public bool unattended;        // stove hosts only: on, with nobody in the room
        public string lastOccupant;    // who was last on this pad / at this appliance
        public int lastDoseIndex = -1;

        // Door hosts only, resolved on first use: which rooms this opening joins, and whether it is a
        // way out of the home. Neither changes during a day, and IsExteriorDoor samples the plan.
        public HashSet<string> joins;
        public bool exterior;
    }

    /// <summary>One person changing room this minute: the only thing a door can notice.</summary>
    internal struct Move
    {
        public string occupantId;
        public string fromRoomId;      // null => they were not in the home
        public string toRoomId;        // null => they have left it
    }

    private static List<Live> Prepare(VariantDef variant, LevelDef level)
    {
        var list = new List<Live>();
        foreach (var s in level.sensors)
        {
            if (s == null || !s.included || string.IsNullOrEmpty(s.id)) continue;
            if (!SensorDevices.TryGet(s.deviceType, out var device)) continue;

            var pose = SensorPose.Resolve(s, level, variant);
            var live = new Live
            {
                def = s,
                device = device,
                pose = pose,
                envelope = SensorCoverage.Envelope.Of(s, level),
                roomId = pose.room?.id,
                label = pose.hostLabel ?? device.displayName,
            };

            if (s.hostKind == SensorHost.Opening)
                live.opening = SensorPose.Find(level.openings, o => o.id, s.hostId);
            else if (s.hostKind == SensorHost.Furniture)
                live.item = SensorPose.Find(level.furniture, f => f.instanceId, s.hostId);

            list.Add(live);
        }
        return list;
    }

    /// <summary>
    /// Who changed room since the previous minute. Called once per minute, before any device is
    /// stepped, because every door sensor in the plan reads the same list. Tracking it per device
    /// would leave the second door sensor comparing against the first one's bookkeeping.
    /// </summary>
    private static void CollectMoves(Dictionary<string, OccupancyModel.Pose> poses,
                                     Dictionary<string, string> previousRooms, List<Move> moves)
    {
        moves.Clear();
        foreach (var kv in poses)
        {
            var p = kv.Value;
            string now = p.present ? p.room?.id : null;

            // First sighting is not a move: the day starts wherever the schedule says, and a passage
            // out of nowhere at 00:00 would put a door event on every home at midnight.
            if (!previousRooms.TryGetValue(kv.Key, out string before))
            {
                previousRooms[kv.Key] = now;
                continue;
            }

            previousRooms[kv.Key] = now;
            if (before == now) continue;

            moves.Add(new Move { occupantId = kv.Key, fromRoomId = before, toRoomId = now });
        }
    }

    private static void Step(Live live, List<Live> all, int minute,
                             Dictionary<string, OccupancyModel.Pose> poses,
                             List<Move> moves, VariantDef variant, LevelDef level, List<SensorEvent> events)
    {
        switch (live.def.deviceType)
        {
            case "motion_sensor":
            case "fall_radar":
                StepMotion(live, all, minute, poses, events);
                break;

            case "bed_chair_pad":
                StepPad(live, minute, poses, events);
                break;

            case "stove_sensor":
                StepStove(live, minute, poses, level, events);
                break;

            case "door_sensor":
            case "smart_lock":
            case "video_doorbell":
                StepDoor(live, minute, moves, level, events);
                break;

            case "med_dispenser":
                StepDispenser(live, minute, poses, variant, events);
                break;

            // Water sensors, pendants, switches, thermostats, hubs and the emerging devices report
            // nothing on an ordinary day. That is not a gap: a leak, a button press and a temperature
            // excursion are all incidents, and Mode.Eventful is where they come from.
        }
    }

    // Presence, not movement. A PIR reports "someone is in this envelope"; the interesting signal is
    // the ABSENCE of that while a person is known to be in the room, which is what a fall looks like
    // and what Mode.Eventful injects.
    private static void StepMotion(Live live, List<Live> all, int minute,
                                   Dictionary<string, OccupancyModel.Pose> poses, List<SensorEvent> events)
    {
        bool seen = false;
        foreach (var kv in poses)
        {
            var p = kv.Value;
            if (!p.present) continue;
            if (!live.envelope.Covers(p.xz)) continue;
            seen = true;
            break;
        }

        if (seen == live.on) return;

        // An Off carries WHO is still in the room, when anyone is, they are awake, and NO other
        // presence sensor in that room can see them. That one field is what lets the fall rule tell
        // "the room emptied" from "someone stopped moving" without knowing anything about occupancy,
        // and the third clause is what stops two sensors covering one room raising a fall every time
        // a resident crosses from the reach of one into the reach of the other.
        string stillThere = seen ? null : UnseenAwakeOccupantIn(live, all, poses);
        Transition(live, seen, minute, events, stillThere);
    }

    private static void StepPad(Live live, int minute,
                               Dictionary<string, OccupancyModel.Pose> poses, List<SensorEvent> events)
    {
        if (live.item == null) return;

        bool occupied = false;
        string who = null;
        foreach (var kv in poses)
        {
            var p = kv.Value;
            if (!p.present || p.activity == null) continue;

            // Anchored to this bed is the authoritative answer; sleeping in the room the bed is in is
            // the fallback, because a sample day that names a bedroom without naming the bed still
            // means the bed.
            bool onIt = p.activity.anchorId == live.item.instanceId
                     || (p.activity.kind == ActivityKind.Sleep && p.room?.id == live.roomId);
            if (!onIt) continue;

            occupied = true;
            who = kv.Key;
            break;
        }

        if (occupied == live.on) return;

        // The Off event names whoever was IN the bed, not whoever is in it now, which is nobody.
        // Without this the 3 AM alert reads "Someone left bed", and the whole value of the alert to a
        // DSP covering five residents is knowing which door to knock on.
        if (occupied) live.lastOccupant = who;
        Transition(live, occupied, minute, events, occupied ? who : live.lastOccupant);
    }

    // The stove reports TWO things, and the difference between them is the whole rule.
    //
    // The hob being on is what the plan tints and what the console shows. What §3.1's scenario is
    // actually about is the hob being on and UNATTENDED: "left unattended for 45 minutes", against
    // "the usual 15-20 minute sessions". Measuring the threshold against the hob alone raises an alert
    // on every meal that takes three quarters of an hour, which is a great many meals: the six samples
    // produced one on an ordinary day, before this split existed.
    //
    // So the unattended condition is emitted as its own span, marked with `Unattended`, and the rule
    // walks that one. Nothing else in the file needs to know: OnSpans takes a detail filter, and
    // StateAt skips the marked pair so the tint still follows the hob.
    private static void StepStove(Live live, int minute, Dictionary<string, OccupancyModel.Pose> poses,
                                  LevelDef level, List<SensorEvent> events)
    {
        if (live.item == null) return;

        bool cooking = false;
        string who = null;
        foreach (var kv in poses)
        {
            var p = kv.Value;
            if (!p.present || p.activity == null) continue;

            bool atIt = p.activity.anchorId == live.item.instanceId
                     || (p.activity.kind == ActivityKind.Cook && p.room?.id == live.roomId);
            if (!atIt) continue;

            cooking = true;
            who = kv.Key;
            break;
        }

        if (cooking) live.lastOccupant = who;
        if (cooking != live.on) Transition(live, cooking, minute, events, who ?? live.lastOccupant);

        // Anyone in the kitchen counts as attending it, not only the person cooking: a shared home's
        // kitchen usually has somebody in it, and a stove watched by a housemate is watched.
        bool attended = false;
        foreach (var kv in poses)
            if (kv.Value.present && kv.Value.room?.id == live.roomId) { attended = true; break; }

        bool unattended = live.on && !attended;
        if (unattended == live.unattended) return;

        live.unattended = unattended;
        events.Add(Event(live, unattended ? SensorEventKind.On : SensorEventKind.Off, minute,
                         live.lastOccupant, Unattended));
    }

    // A door reports the instant someone passes through it. Room changes are what the schedule gives,
    // so a transition into or out of the room this door serves is the passage. Including the one that
    // matters most, coming home or leaving, which shows as present flipping on the exterior door.
    private static void StepDoor(Live live, int minute, List<Move> moves,
                                 LevelDef level, List<SensorEvent> events)
    {
        if (live.opening == null || moves.Count == 0) return;

        // Resolved once, on the first minute this device is asked: the rooms a door joins do not
        // change during a day, and IsExteriorDoor samples the plan on both sides of the wall.
        if (live.joins == null)
        {
            live.exterior = SensorCoverage.IsExteriorDoor(live.opening, level);
            live.joins = OpeningRooms(live.opening, level);
        }

        foreach (var move in moves)
            if (Serves(live, move.fromRoomId, move.toRoomId))
                events.Add(Event(live, SensorEventKind.Trigger, minute, move.occupantId));
    }

    // Whether passing between these two rooms means going through THIS opening. An exterior door
    // serves any transition to or from outside; an interior one serves the two rooms it joins.
    //
    // A person crossing three rooms in one minute registers only on the doors between the two the
    // schedule names, which is a limit of a schedule with no path in it rather than of this test,
    // and the alternative, routing everyone through a corridor graph, is a great deal of machinery
    // for a marker that is already a capsule standing still.
    private static bool Serves(Live live, string roomBefore, string roomNow)
    {
        if (live.exterior) return roomBefore == null || roomNow == null;
        return live.joins.Contains(roomBefore) && live.joins.Contains(roomNow);
    }

    private static void StepDispenser(Live live, int minute, Dictionary<string, OccupancyModel.Pose> poses,
                                      VariantDef variant, List<SensorEvent> events)
    {
        for (int i = 0; i < DoseTimes.Length; i++)
        {
            if (DoseTimes[i] != minute || live.lastDoseIndex == i) continue;
            live.lastDoseIndex = i;

            string who = FirstOccupantIn(live.roomId, poses) ?? FirstOccupant(variant);
            events.Add(Event(live, SensorEventKind.Trigger, minute, who, DoseDue));

            // On a routine day the dose is taken. The grace period is the rule's, so "taken" one
            // minute later is what a compliant day looks like and no alert follows.
            events.Add(Event(live, SensorEventKind.Trigger, minute + 1, who, DoseTaken));
            return;
        }
    }

    // The event markers. Public because they are part of the contract between what a device reports
    // and what a rule reads. SensorRules matches on them, and the tests build days out of them.
    public const string DoseDue = "dose due";
    public const string DoseTaken = "dose taken";

    /// <summary>Marks the stove's second event stream: on, with nobody in the room. See StepStove.</summary>
    public const string Unattended = "unattended";

    // ---------------------------------------------------------------------------------------

    private static void Transition(Live live, bool on, int minute, List<SensorEvent> events, string who)
    {
        live.on = on;
        events.Add(Event(live, on ? SensorEventKind.On : SensorEventKind.Off, minute, who));
    }

    private static SensorEvent Event(Live live, string kind, int minute, string occupantId, string detail = null)
        => new SensorEvent
        {
            sensorId = live.def.id,
            deviceType = live.def.deviceType,
            kind = kind,
            minute = Mathf.Clamp(minute, 0, Clock.MinutesPerDay - 1),
            occupantId = occupantId,
            detail = detail ?? live.label,
        };

    /// <summary>
    /// Someone in this sensor's room, awake, whom nothing else in that room can see either. The
    /// "nothing else" clause is the difference between a fall and a resident walking out of one
    /// sensor's cone and into another's.
    /// </summary>
    private static string UnseenAwakeOccupantIn(Live live, List<Live> all,
                                                Dictionary<string, OccupancyModel.Pose> poses)
    {
        if (string.IsNullOrEmpty(live.roomId)) return null;

        foreach (var kv in poses)
        {
            var p = kv.Value;
            if (!p.present || p.room?.id != live.roomId) continue;
            // Asleep is the false-alarm case this whole field exists to exclude: a sleeping person
            // produces no movement for hours, and a system that called that a fall every night is a
            // system nobody would keep switched on. §4's reliability criterion, made mechanical.
            if (p.activity != null && p.activity.kind == ActivityKind.Sleep) continue;

            bool seenElsewhere = false;
            foreach (var other in all)
            {
                if (other == live || other.roomId != live.roomId) continue;
                if (!other.envelope.valid) continue;
                if (!other.envelope.Covers(p.xz)) continue;
                seenElsewhere = true;
                break;
            }
            if (!seenElsewhere) return kv.Key;
        }
        return null;
    }

    private static string AwakeOccupantIn(string roomId, Dictionary<string, OccupancyModel.Pose> poses)
    {
        if (string.IsNullOrEmpty(roomId)) return null;
        foreach (var kv in poses)
        {
            var p = kv.Value;
            if (!p.present || p.room?.id != roomId) continue;
            // Asleep is the false-alarm case this whole field exists to exclude: a sleeping person
            // produces no movement for hours, and a system that called that a fall every night is a
            // system nobody would keep. §4's reliability criterion, made mechanical.
            if (p.activity != null && p.activity.kind == ActivityKind.Sleep) continue;
            return kv.Key;
        }
        return null;
    }

    private static string FirstOccupantIn(string roomId, Dictionary<string, OccupancyModel.Pose> poses)
    {
        if (string.IsNullOrEmpty(roomId)) return null;
        foreach (var kv in poses)
            if (kv.Value.present && kv.Value.room?.id == roomId) return kv.Key;
        return null;
    }

    internal static string FirstOccupant(VariantDef variant)
    {
        if (variant?.occupants == null) return null;
        foreach (var o in variant.occupants) if (o != null && o.included) return o.id;
        return null;
    }

    /// <summary>The ids of the (up to two) rooms an interior opening joins.</summary>
    internal static HashSet<string> OpeningRooms(OpeningDef opening, LevelDef level)
    {
        var rooms = new HashSet<string>();
        var wall = SensorPose.Find(level?.walls, w => w.id, opening?.wallId);
        if (wall == null) return rooms;

        var frame = WallMeshBuilder.BuildFrame(wall, level);
        Vector2 on = HomeMetrics.PointOnWall(wall, opening.offset);
        var left = new Vector2(frame.left.x, frame.left.z);
        float reach = 0.5f * frame.thickness + 0.25f;

        var a = HomeMetrics.RoomAt(on + left * reach, level);
        var b = HomeMetrics.RoomAt(on - left * reach, level);
        if (a != null) rooms.Add(a.id);
        if (b != null) rooms.Add(b.id);
        return rooms;
    }

    // ---------------------------------------------------------------------------------------
    // Incidents: the report's own scenarios, acted out on this home's own devices
    // ---------------------------------------------------------------------------------------
    //
    // Each of the seven below is one numbered scenario from the report, and each happens only if the
    // home actually has the device for it: a plan with no pressure pads gets no 3 AM bed exit, which
    // is the point: the demonstration day shows what THIS package would catch, not a fixed story.
    //
    // Times are fixed rather than random so the day is reproducible: the timeline, the console, the
    // report and the tests all describe one day, and a randomised one would make every screenshot and
    // every assertion describe a different afternoon. The seed picks WHO, not WHETHER or WHEN, so a
    // five-resident home does not always single out the same person.

    private const int NightDoorMinute = 2 * 60 + 40;      // §4.1: "If front door opens after 9 PM"
    private const int BedExitMinute = 3 * 60 + 10;        // §4.3.2: "Resident leaves bed at 3 AM"
    private const int BedReturnMinute = 3 * 60 + 45;
    private const int FallMinute = 14 * 60 + 20;          // §4.1: "no motion for 10 min"
    private const int FallEndMinute = 14 * 60 + 45;
    private const int LeakMinute = 19 * 60 + 5;           // §4.4.3: a forgotten faucet
    private const int PanicMinute = 21 * 60 + 15;         // §4.5.1: "presses for help (e.g. during seizure)"
    private const int ColdStart = 5 * 60 + 30;            // §4.2.1: an overnight heating failure
    private const int ColdEnd = 6 * 60 + 40;
    private const int StoveOverrun = 60;                  // §3.1: "left unattended for 45 minutes"

    private static void InjectIncidents(List<Live> devices, VariantDef variant, LevelDef level,
                                        int seed, List<SensorEvent> events)
    {
        string who = PickResident(variant, seed);

        // An incident happens ONCE, in one place. Injecting a leak into every water sensor in a
        // four-bathroom home produced five simultaneous floods, which is not a demonstration of
        // anything except that the demonstration was written carelessly.
        bool leaked = false, fell = false, wandered = false;

        foreach (var d in devices)
        {
            switch (d.def.deviceType)
            {
                // Someone tries the front door in the small hours. Only an exterior door: an
                // interior one opening at 2:40 is a trip to the bathroom, not an elopement.
                case "door_sensor":
                    if (d.joins == null)
                    {
                        d.exterior = SensorCoverage.IsExteriorDoor(d.opening, level);
                        d.joins = OpeningRooms(d.opening, level);
                    }
                    if (wandered || !d.exterior) break;
                    wandered = true;
                    events.Add(Event(d, SensorEventKind.Trigger, NightDoorMinute, who));
                    break;

                // Out of bed at ten past three and not back for half an hour. Only the pad under the
                // chosen resident's own bed, so the alert names the right person and the right room.
                case "bed_chair_pad":
                    if (!OccupiedBy(d, variant, who)) break;
                    events.Add(Event(d, SensorEventKind.Off, BedExitMinute, who));
                    events.Add(Event(d, SensorEventKind.On, BedReturnMinute, who));
                    break;

                // The hob stays on an hour past the meal. Extending the LAST cooking span rather than
                // inventing one keeps the incident attached to a real meal in this household's day.
                case "stove_sensor":
                    LeaveStoveOn(events, d);
                    break;

                // Someone stops moving in a room they are still in: the shape a fall makes to a PIR
                // sensor, and the reason the rule looks for absence rather than presence.
                case "motion_sensor":
                case "fall_radar":
                    if (fell) break;
                    if (!AwakeIn(d, variant, level, FallMinute, out string faller)) break;
                    fell = true;
                    events.Add(Event(d, SensorEventKind.Off, FallMinute, faller));
                    events.Add(Event(d, SensorEventKind.On, FallEndMinute, faller));
                    break;

                case "water_sensor":
                    if (leaked) break;
                    leaked = true;
                    events.Add(Event(d, SensorEventKind.Trigger, LeakMinute, null));
                    break;

                case "panic_pendant":
                    // Worn by one person; only that person's pendant is pressed.
                    if (d.def.hostId == who) events.Add(Event(d, SensorEventKind.Trigger, PanicMinute, who));
                    break;

                case "smart_thermostat":
                case "air_quality_monitor":
                    events.Add(Event(d, SensorEventKind.On, ColdStart, null));
                    events.Add(Event(d, SensorEventKind.Off, ColdEnd, null));
                    break;

                // A dose presented and not taken. Dropping the "taken" event is exactly what a missed
                // dose IS, so the rule needs no special case for the demonstration.
                case "med_dispenser":
                    DropSecondDoseTaken(events, d.def.id);
                    break;
            }
        }
    }

    /// <summary>The resident an incident happens to. Seeded so a five-person home varies.</summary>
    private static string PickResident(VariantDef variant, int seed)
    {
        var roster = new List<OccupantDef>();
        if (variant?.occupants != null)
            foreach (var o in variant.occupants) if (o != null && o.included) roster.Add(o);

        if (roster.Count == 0) return null;
        int i = Mathf.Abs(seed) % roster.Count;
        return roster[i].id;
    }

    /// <summary>Whether this pad is under the bed the chosen resident sleeps on.</summary>
    private static bool OccupiedBy(Live pad, VariantDef variant, string occupantId)
    {
        if (pad.item == null || string.IsNullOrEmpty(occupantId)) return false;

        var person = SensorPose.Find(variant?.occupants, o => o.id, occupantId);
        if (person?.schedule == null) return false;

        foreach (var a in person.schedule)
        {
            if (a == null || a.kind != ActivityKind.Sleep) continue;
            if (a.anchorId == pad.item.instanceId) return true;
            if (a.roomId != null && a.roomId == pad.roomId) return true;
        }
        return false;
    }

    /// <summary>Someone awake in this sensor's room at that minute: the person who would fall.</summary>
    private static bool AwakeIn(Live live, VariantDef variant, LevelDef level, int minute, out string occupantId)
    {
        occupantId = null;
        if (string.IsNullOrEmpty(live.roomId)) return false;

        var poses = OccupancyModel.PoseAll(variant, level, minute);
        occupantId = AwakeOccupantIn(live.roomId, poses);
        return occupantId != null;
    }

    /// <summary>
    /// The cook walks away and the hob stays on: the last meal of the day runs an hour past its end,
    /// with nobody in the kitchen.
    /// </summary>
    /// <remarks>
    /// Attaching this to a real meal rather than inventing one is what keeps it a simulation. A
    /// household that never cooks cannot leave the stove on, and this does nothing for them.
    ///
    /// The unattended span is written explicitly because incidents are injected after the minute loop
    /// has finished, so nothing is left to notice that the kitchen emptied. StepStove's second stream
    /// has already run to the end of the day.
    /// </remarks>
    private static void LeaveStoveOn(List<SensorEvent> events, Live live)
    {
        int last = -1;
        for (int i = 0; i < events.Count; i++)
            if (events[i].sensorId == live.def.id && events[i].kind == SensorEventKind.Off
                && events[i].detail != Unattended) last = i;

        if (last < 0) return;

        var off = events[last];
        int left = off.minute;
        int stillOn = Mathf.Min(Clock.MinutesPerDay - 1, left + StoveOverrun);

        off.minute = stillOn;
        events[last] = off;

        events.Add(Event(live, SensorEventKind.On, left, off.occupantId, Unattended));
        events.Add(Event(live, SensorEventKind.Off, stillOn, off.occupantId, Unattended));
    }

    private static void DropSecondDoseTaken(List<SensorEvent> events, string sensorId)
    {
        int seen = 0;
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i].sensorId != sensorId || events[i].detail != DoseTaken) continue;
            seen++;
            if (seen != 2) continue;
            events.RemoveAt(i);
            return;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Queries the console and the timeline ask of a finished day
    // ---------------------------------------------------------------------------------------

    /// <summary>Alerts raised in the <paramref name="windowMinutes"/> up to and including now.</summary>
    public static List<SensorAlert> AlertsAround(Day day, int minute, int windowMinutes = 60)
    {
        var list = new List<SensorAlert>();
        if (day.alerts == null) return list;

        foreach (var a in day.alerts)
        {
            int age = Clock.DurationBetween(a.minute, minute);
            if (age <= windowMinutes) list.Add(a);
        }
        list.Sort((x, y) => y.minute.CompareTo(x.minute));   // newest first, as a phone would show them
        return list;
    }

    public enum State { Idle, Active, Alerting }

    /// <summary>
    /// What a device is doing at this minute, which is what tints it in the plan. Alerting wins over
    /// active: a stove that is on and has been on too long is the second thing, not the first.
    /// </summary>
    public static State StateAt(Day day, string sensorId, int minute, int alertHold = 15)
    {
        if (string.IsNullOrEmpty(sensorId)) return State.Idle;

        if (day.alerts != null)
            foreach (var a in day.alerts)
                if (a.sensorId == sensorId && Clock.DurationBetween(a.minute, minute) <= alertHold)
                    return State.Alerting;

        if (day.events == null) return State.Idle;

        // The last On/Off before now decides. Triggers are instants and read as active briefly, which
        // is what makes a door sensor visible at all when someone walks through it.
        //
        // A device's second stream is skipped: the stove's tint follows the hob, not the derived
        // "unattended" condition, so it lights while someone is cooking rather than only once they
        // have walked away.
        var state = State.Idle;
        foreach (var e in day.events)
        {
            if (e.sensorId != sensorId || e.minute > minute || SensorRules.IsMarked(e)) continue;
            if (e.kind == SensorEventKind.On) state = State.Active;
            else if (e.kind == SensorEventKind.Off) state = State.Idle;
            else if (e.kind == SensorEventKind.Trigger && minute - e.minute <= 2) state = State.Active;
        }
        return state;
    }
}
