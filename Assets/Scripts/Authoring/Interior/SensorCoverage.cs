using System.Collections.Generic;
using UnityEngine;

// How much of a residence the sensing layer actually watches, and what it misses.
//
// The same shape as ResidenceMetrics, and the same job one step over: ResidenceMetrics answers "does a
// wheelchair fit through here", this answers "would anyone know if something happened here". Both are
// geometry over one level, both are pure, and both are what the rail, the overlay and the report all
// read rather than each computing their own version.
//
// COVERAGE IS CLIPPED TO THE SENSOR'S OWN ROOM, and that is the one modelling decision in this file.
// A PIR sensor's 9.1 m range (§4.3.1) is longer than most residences, so an unclipped disc would report a
// single sensor in the hall as covering five bedrooms through their walls: a coverage figure that
// flatters a plan is worse than none, because the whole point of the figure is to find the gap. What
// is deliberately NOT modelled is occlusion WITHIN a room: a sensor does not lose the far corner of an
// L-shaped living room because of a returning wall. That is the same trade FurnitureFit makes about
// openings in perpendicular walls: the extra precision changes almost no real plan, and it would make
// the number move when a door was left open.
public static class SensorCoverage
{
    /// <summary>
    /// Grid pitch for the floor sweep, meters. Matches OccupancyModel's own search step, so "a point
    /// a person could stand at" means the same thing to both files.
    /// </summary>
    public const float Step = 0.15f;

    /// <summary>Devices that report a person being somewhere. What room coverage is measured over.</summary>
    private static readonly HashSet<string> PresenceSensing = new HashSet<string>
    {
        "motion_sensor", "fall_radar",
    };

    /// <summary>Devices that can speak into a room: how a prompt actually reaches a resident.</summary>
    private static readonly HashSet<string> Audible = new HashSet<string>
    {
        "voice_prompt_speaker", "central_hub",
    };

    /// <summary>Devices that make a doorway monitored: any of the three tells you it was used.</summary>
    private static readonly HashSet<string> DoorWatching = new HashSet<string>
    {
        "door_sensor", "smart_lock", "video_doorbell",
    };

    // ---------------------------------------------------------------------------------------
    // The primitive
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Whether <paramref name="sensor"/> can see <paramref name="point"/>: inside its radius, inside
    /// its cone, and in the same room. A device with no radius senses only the element it is attached
    /// to and covers no floor at all, which is correct for a door sensor and a pressure pad.
    /// </summary>
    public static bool Covers(SensorDef sensor, Vector2 point, LevelDef level)
        => Envelope.Of(sensor, level).Covers(point);

    /// <summary>
    /// A sensor's detection envelope, resolved once. The hot loops build one per sensor and then test
    /// thousands of points against it. Resolving the pose (which walks the room list to find which
    /// room the device is in) inside a per-point test made a five-bedroom coverage sweep quadratic in
    /// the plan for no reason.
    /// </summary>
    public struct Envelope
    {
        public bool valid;
        public Vector2 origin;
        public float radius;
        public float halfAngle;       // degrees; 180 => omnidirectional
        public Vector2 facing;
        public List<Vector2> roomPoly;  // the room clip; null when the device is in no room

        public static Envelope Of(SensorDef sensor, LevelDef level)
        {
            var e = new Envelope();
            if (sensor == null || !sensor.included) return e;

            e.radius = SensorDevices.RadiusOf(sensor);
            if (e.radius <= 0f) return e;

            var pose = SensorPose.Resolve(sensor, level);
            if (!pose.resolved) return e;

            e.valid = true;
            e.origin = pose.xz;
            e.halfAngle = 0.5f * SensorDevices.AngleOf(sensor);
            e.facing = SensorPose.Facing(pose.yaw);
            e.roomPoly = pose.room != null ? PolygonTriangulator.ToVector2(pose.room.polygon) : null;
            return e;
        }

        public bool Covers(Vector2 point)
        {
            if (!valid) return false;

            Vector2 offset = point - origin;
            float distance = offset.magnitude;
            if (distance > radius) return false;

            // Half-angle each side of the axis; Vector2.Angle is already the unsigned separation.
            if (halfAngle < 180f && distance > 1e-4f
                && Vector2.Angle(facing, offset / distance) > halfAngle) return false;

            // The room clip. See the file header. A sensor in no room at all (which nothing in the
            // tool allows, but a hand-edited file could) sees its bare radius.
            return roomPoly == null || ResidenceMetrics.PointInPolygon(point, roomPoly);
        }
    }

    /// <summary>Whether ANY device of the given family covers this point.</summary>
    public static bool CoveredBy(LevelDef level, Vector2 point, HashSet<string> family)
    {
        if (level?.sensors == null) return false;
        foreach (var s in level.sensors)
        {
            if (s == null || !s.included || !family.Contains(s.deviceType)) continue;
            if (Covers(s, point, level)) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------------------------------
    // Room coverage
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The fraction of a room's floor watched by a presence-sensing device, 0..1. Sampled on the same
    /// grid OccupancyModel searches, so a gap a person could stand in cannot fall between samples.
    /// </summary>
    public static float RoomCoverage(LevelDef level, RoomDef room)
    {
        var poly = PolygonTriangulator.ToVector2(room?.polygon);
        if (poly == null || poly.Count < 3) return 0f;

        // Resolve every relevant envelope ONCE, before the sweep. A five-bedroom plan grids to a few
        // thousand samples per room, and resolving a pose walks the room list, so doing it per sample
        // made this quadratic in the plan for no reason.
        var envelopes = new List<Envelope>();
        if (level?.sensors != null)
            foreach (var s in level.sensors)
            {
                if (s == null || !s.included || !PresenceSensing.Contains(s.deviceType)) continue;
                if (SensorPose.Resolve(s, level).room?.id != room.id) continue;

                var e = Envelope.Of(s, level);
                if (e.valid) envelopes.Add(e);
            }

        if (envelopes.Count == 0) return 0f;

        Bounds2(poly, out Vector2 min, out Vector2 max);

        int inside = 0, covered = 0;
        for (float x = min.x; x <= max.x; x += Step)
        for (float z = min.y; z <= max.y; z += Step)
        {
            var p = new Vector2(x, z);
            if (!ResidenceMetrics.PointInPolygon(p, poly)) continue;

            inside++;
            for (int i = 0; i < envelopes.Count; i++)
                if (envelopes[i].Covers(p)) { covered++; break; }
        }

        return inside == 0 ? 0f : (float)covered / inside;
    }

    /// <summary>Coverage over every room in the level, in plan order. Used by the rail and the report.</summary>
    public static List<KeyValuePair<RoomDef, float>> AllRoomCoverage(LevelDef level)
    {
        var rows = new List<KeyValuePair<RoomDef, float>>();
        if (level?.rooms == null) return rows;
        foreach (var room in level.rooms)
            if (room != null) rows.Add(new KeyValuePair<RoomDef, float>(room, RoomCoverage(level, room)));
        return rows;
    }

    /// <summary>
    /// Floor area watched across the whole level, as a fraction of the total. The one number the
    /// report's before/after row shows, so it is area-weighted rather than a mean of room fractions,
    /// a covered hall and an uncovered store cupboard are not half a residence.
    /// </summary>
    public static float WholeResidenceCoverage(LevelDef level)
    {
        if (level?.rooms == null || level.rooms.Count == 0) return 0f;

        float total = 0f, covered = 0f;
        foreach (var room in level.rooms)
        {
            if (room == null) continue;
            float area = ResidenceMetrics.RoomArea(room);
            if (area <= 0f) continue;
            total += area;
            covered += area * RoomCoverage(level, room);
        }
        return total <= 0f ? 0f : covered / total;
    }

    /// <summary>
    /// The same figure for a residence with more than one story, area-weighted across all of them.
    ///
    /// The single-level form is named "whole residence" and answers for one floor, which was the same
    /// thing until a residence could have two. On a two-story dwelling with nothing upstairs it would
    /// report the ground floor's coverage as the building's: a figure that flatters the plan, in the
    /// row of the report someone reads to decide whether the package is worth funding. Weighting by
    /// area across every story is the same arithmetic the per-level form already does, one level up.
    /// </summary>
    public static float WholeResidenceCoverage(VariantDef variant)
    {
        if (variant?.levels == null) return 0f;

        float total = 0f, covered = 0f;
        foreach (var level in variant.levels)
        {
            if (level?.rooms == null) continue;
            foreach (var room in level.rooms)
            {
                if (room == null) continue;
                float area = ResidenceMetrics.RoomArea(room);
                if (area <= 0f) continue;
                total += area;
                covered += area * RoomCoverage(level, room);
            }
        }
        return total <= 0f ? 0f : covered / total;
    }

    // ---------------------------------------------------------------------------------------
    // Doorways
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A doorway to the outside: a door whose wall has a room on one side and open air on the other.
    /// Derived rather than flagged, because nothing in the schema marks an opening as exterior and a
    /// wall that becomes exterior when a proposal deletes the room behind it should change answer.
    /// </summary>
    public static bool IsExteriorDoor(OpeningDef opening, LevelDef level)
    {
        if (opening == null || level == null) return false;
        if (opening.kind == OpeningKind.Window) return false;

        var wall = SensorPose.Find(level.walls, w => w.id, opening.wallId);
        if (wall == null) return false;

        var frame = WallMeshBuilder.BuildFrame(wall, level);
        Vector2 on = ResidenceMetrics.PointOnWall(wall, opening.offset);
        var left = new Vector2(frame.left.x, frame.left.z);

        // Half a wall thickness clear of the centerline, plus enough to be unambiguously inside the
        // room rather than on its boundary polygon, which runs along that same centerline.
        float reach = 0.5f * frame.thickness + 0.25f;
        bool roomLeft = ResidenceMetrics.RoomAt(on + left * reach, level) != null;
        bool roomRight = ResidenceMetrics.RoomAt(on - left * reach, level) != null;

        return roomLeft != roomRight;
    }

    /// <summary>True when any door-watching device is installed on this opening.</summary>
    public static bool IsWatched(OpeningDef opening, LevelDef level)
    {
        if (opening == null || level?.sensors == null) return false;
        foreach (var s in level.sensors)
            if (s != null && s.included && s.hostKind == SensorHost.Opening
                && s.hostId == opening.id && DoorWatching.Contains(s.deviceType)) return true;
        return false;
    }

    /// <summary>
    /// Every way out of the residence that nothing is watching. This is the report's headline concern,
    /// wandering and elopement, §4.4.1. Reduced to a list a care team can act on.
    /// </summary>
    public static List<OpeningDef> UnmonitoredExits(LevelDef level)
    {
        var list = new List<OpeningDef>();
        if (level?.openings == null) return list;
        foreach (var o in level.openings)
            if (o != null && IsExteriorDoor(o, level) && !IsWatched(o, level)) list.Add(o);
        return list;
    }

    public static int ExitCount(LevelDef level)
    {
        int n = 0;
        if (level?.openings == null) return 0;
        foreach (var o in level.openings) if (o != null && IsExteriorDoor(o, level)) n++;
        return n;
    }

    /// <summary>
    /// Ways out of the BUILDING, across every story. A first-floor balcony door is a way out, and a
    /// figure that counts only the ground floor's is the kind that flatters a plan: the report's
    /// "ways out watched" row is read as a statement about the residence, not about one floor of it.
    /// </summary>
    public static int ExitCount(VariantDef variant)
    {
        int n = 0;
        foreach (var l in variant?.levels ?? new List<LevelDef>()) n += ExitCount(l);
        return n;
    }

    public static int UnmonitoredExitCount(VariantDef variant)
    {
        int n = 0;
        foreach (var l in variant?.levels ?? new List<LevelDef>()) n += UnmonitoredExits(l).Count;
        return n;
    }

    // ---------------------------------------------------------------------------------------
    // Gaps
    // ---------------------------------------------------------------------------------------

    /// <summary>One thing the package does not cover, said the way a person would say it.</summary>
    public struct Gap
    {
        public string roomId;         // null when the gap is about the residence rather than a room
        public string openingId;      // set when the gap is a doorway
        public string text;
        public string severity;       // SensorSeverity.*, so the rail and the console can rank them
    }

    /// <summary>
    /// What this package misses. Ordered most consequential first, and deliberately short: a list of
    /// twenty gaps is a list nobody reads, so this reports the four kinds that change a decision,
    /// no hub, an unwatched way out, a bedroom nobody would know you fell in, and a room no prompt
    /// can reach.
    /// </summary>
    public static List<Gap> Gaps(LevelDef level, VariantDef variant = null)
    {
        var gaps = new List<Gap>();
        if (level == null) return gaps;

        bool anySensors = level.sensors != null && level.sensors.Count > 0;
        if (!anySensors) return gaps;

        // 1: nothing can reach staff at all. §3.1.3: the hub is what routes an alert anywhere.
        var cost = SensorCost.Of(level);
        if (cost.hubMissing)
            gaps.Add(new Gap
            {
                text = "No hub, so nothing here can reach staff.",
                severity = SensorSeverity.Urgent,
            });

        // 2: the ways out.
        foreach (var exit in UnmonitoredExits(level))
            gaps.Add(new Gap
            {
                openingId = exit.id,
                roomId = RoomOfOpening(exit, level)?.id,
                text = $"{SensorPose.OpeningLabel(exit, level)} is a way out and nothing watches it.",
                severity = SensorSeverity.Urgent,
            });

        // 3. Sleeping rooms with no presence sensing. A fall at night in an unwatched bedroom is
        // exactly the scenario §4.3.1 exists for, so bedrooms and bathrooms are called out by name
        // while a store cupboard is not.
        if (level.rooms != null)
            foreach (var room in level.rooms)
            {
                if (room == null) continue;
                if (room.roomType != RoomType.Bedroom && room.roomType != RoomType.Bathroom) continue;
                if (RoomCoverage(level, room) > 0.25f) continue;

                gaps.Add(new Gap
                {
                    roomId = room.id,
                    text = $"{SensorPose.RoomName(room)} has no movement sensing.",
                    severity = SensorSeverity.Warning,
                });
            }

        // 4. Rooms no prompt can reach. Every response in the report ends in a spoken prompt, so a
        // covered room with no way to speak into it can raise an alert and do nothing about it.
        if (level.rooms != null)
            foreach (var room in level.rooms)
            {
                if (room == null) continue;
                if (room.roomType != RoomType.Bedroom && room.roomType != RoomType.Kitchen) continue;
                if (CanPrompt(level, room)) continue;

                gaps.Add(new Gap
                {
                    roomId = room.id,
                    text = $"No way to speak a prompt into {SensorPose.RoomName(room).ToLowerInvariant()}.",
                    severity = SensorSeverity.Info,
                });
            }

        gaps.Sort((a, b) => SensorSeverity.Rank(b.severity).CompareTo(SensorSeverity.Rank(a.severity)));
        return gaps;
    }

    /// <summary>Whether a prompt can be spoken into this room: a speaker or the hub reaching it.</summary>
    public static bool CanPrompt(LevelDef level, RoomDef room)
    {
        if (level?.sensors == null || room == null) return false;
        Vector2 center = ResidenceMetrics.LargestInscribedCircle(room).center;

        foreach (var s in level.sensors)
        {
            if (s == null || !s.included || !Audible.Contains(s.deviceType)) continue;

            // The hub has no radius of its own (it is a tablet, not a PA system) so it prompts the
            // room it is in and no other. A speaker prompts anything inside its reach.
            var pose = SensorPose.Resolve(s, level);
            if (!pose.resolved) continue;
            if (pose.room != null && pose.room.id == room.id) return true;
            if (Covers(s, center, level)) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>Which room an opening opens into. The interior side when it is an exterior door.</summary>
    public static RoomDef RoomOfOpening(OpeningDef opening, LevelDef level)
    {
        var wall = SensorPose.Find(level?.walls, w => w.id, opening?.wallId);
        if (wall == null) return null;

        var frame = WallMeshBuilder.BuildFrame(wall, level);
        Vector2 on = ResidenceMetrics.PointOnWall(wall, opening.offset);
        var left = new Vector2(frame.left.x, frame.left.z);
        float reach = 0.5f * frame.thickness + 0.25f;

        return ResidenceMetrics.RoomAt(on + left * reach, level)
            ?? ResidenceMetrics.RoomAt(on - left * reach, level);
    }

    private static void Bounds2(IReadOnlyList<Vector2> poly, out Vector2 min, out Vector2 max)
    {
        min = new Vector2(float.MaxValue, float.MaxValue);
        max = new Vector2(float.MinValue, float.MinValue);
        foreach (var p in poly)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }
    }
}
