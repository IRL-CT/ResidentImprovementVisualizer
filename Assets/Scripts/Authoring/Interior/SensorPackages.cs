using System;
using System.Collections.Generic;
using UnityEngine;

// A whole smart home package, derived from the plan and the household.
//
// ONE CALLER: the two care-home samples' "Smart home package" proposal. It was two: the Sensors rail
// offered Essential / Standard / Care home tiers and an "Add the N missing devices" button, and that
// UI is gone, so a user now installs device by device and this authors only what ships. What the
// removal did not touch is the rule that matters: a shipped package is DERIVED from the plan rather
// than listed by hand, so improving these rules improves both samples on the next
// SampleHomes.Generation bump rather than leaving them describing an older opinion.
//
// The rules below are the report's own placement guidance, read off §3.1.2 and the §4 subsections:
// motion sensors in hallways and living areas, door sensors on exits, water sensors under sinks and
// near toilets, pads under beds, a stove sensor on the range, a hub central. Nothing here is clever;
// what it is, is COMPLETE and consistent, which a package assembled by hand across a five-bedroom
// plan reliably is not: the seed for this whole feature was that no test could see a wardrobe
// standing in a doorway, and an unwatched back door is the same class of mistake.
public static class SensorPackages
{
    public enum Tier
    {
        /// <summary>The four devices that address the report's headline risks, and the hub.</summary>
        Essential,
        /// <summary>Adds movement sensing, water, and a doorbell. A home with one or two residents.</summary>
        Standard,
        /// <summary>A supported home: pads, pendants, prompts and medication. §2.2.2's group home.</summary>
        Care,
    }

    /// <summary>
    /// Builds the devices this plan should have. Nothing is added to the level: the caller records
    /// undo, appends, and re-renders, exactly as FurnitureTool.Place does for one item.
    /// </summary>
    /// <param name="idPrefix">
    /// Set for authored samples, so ids are stable across builds and a refreshed sample diffs against
    /// its predecessor as "unchanged" rather than "everything replaced". Null mints GUIDs, which is
    /// what any caller that is not authoring a sample wants.
    /// </param>
    public static List<SensorDef> Recommend(LevelDef level, VariantDef variant, Tier tier,
                                            string idPrefix = null)
    {
        var made = new List<SensorDef>();
        if (level == null) return made;

        var ids = new IdFactory(idPrefix);

        AddHub(level, tier, ids, made);
        AddExits(level, tier, ids, made);
        AddKitchen(level, tier, ids, made);
        AddWater(level, ids, made);
        if (tier != Tier.Essential) AddMotion(level, tier, ids, made);
        if (tier == Tier.Care) AddCare(level, variant, ids, made);

        return made;
    }

    // ---------------------------------------------------------------------------------------

    // §3.1.3: "placed in a central location like the living room for easy access". The living room
    // if there is one, the hall otherwise, and the largest room if the plan names neither.
    private static void AddHub(LevelDef level, Tier tier, IdFactory ids, List<SensorDef> made)
    {
        var room = FirstOfType(level, RoomType.Living) ?? FirstOfType(level, RoomType.Hall) ?? Largest(level);
        if (room == null) return;

        made.Add(InRoom("central_hub", room, level, ids));

        // §4.2.1: "on a wall in a central location like a hallway or living room".
        if (tier != Tier.Essential)
            made.Add(InRoom("smart_thermostat", FirstOfType(level, RoomType.Hall) ?? room, level, ids));
    }

    // §4.4.1. Door sensors on exits. Every way out, not the front one: the report's whole wandering
    // argument fails on the door nobody thought about, which is exactly what SensorCoverage's
    // unmonitored-exits gap reports and what this closes.
    private static void AddExits(LevelDef level, Tier tier, IdFactory ids, List<SensorDef> made)
    {
        if (level.openings == null) return;

        OpeningDef main = null;
        foreach (var o in level.openings)
        {
            if (o == null || !SensorCoverage.IsExteriorDoor(o, level)) continue;

            made.Add(OnOpening("door_sensor", o, level, ids));
            if (main == null || o.width > main.width) main = o;
        }

        if (main == null) return;

        // §4.5.2 puts one camera on the home, at the entrance, and nowhere else. Following that
        // exactly is what makes the console's Family role able to say "no camera can see you".
        if (tier != Tier.Essential) made.Add(OnOpening("video_doorbell", main, level, ids));
        if (tier == Tier.Care) made.Add(OnOpening("smart_lock", main, level, ids));
    }

    // §4.4.2: the range. Plus, at Care, the switch §4.4.2 names as the way to cut its power.
    private static void AddKitchen(LevelDef level, Tier tier, IdFactory ids, List<SensorDef> made)
    {
        var range = FirstItem(level, "range");
        if (range == null) return;

        made.Add(OnItem("stove_sensor", range, ids));

        if (tier != Tier.Care) return;

        var wall = HomeMetrics.NearestWall(new Vector2(range.position[0], range.position[2]),
                                           level.walls, HomeConventions.MOUNT_REACH,
                                           out float offset, out int side);
        if (wall != null) made.Add(OnWall("smart_switch", wall, offset, side, ids));
    }

    // §4.4.3: "on the floor near potential leak sources like sinks, toilets, washing machines".
    // One per wet room, at the fixture, which is where a leak reaches first.
    private static void AddWater(LevelDef level, IdFactory ids, List<SensorDef> made)
    {
        if (level.rooms == null) return;

        foreach (var room in level.rooms)
        {
            if (room == null) continue;
            if (room.roomType != RoomType.Bathroom && room.roomType != RoomType.Kitchen
                && room.roomType != RoomType.Laundry) continue;

            var fixture = FirstItemIn(level, room, WetFixtures);
            Vector2 center = HomeMetrics.LargestInscribedCircle(room).center;
            Vector2 at = fixture != null ? FootOf(fixture, room, center) : center;

            made.Add(AtPoint("water_sensor", room, at, ids));
        }
    }

    // §4.3.1: "high-traffic areas like living rooms, hallways, bedrooms, or entryways". Store
    // cupboards and laundries are deliberately left out: a package is judged on whether staff read
    // its alerts, and a sensor in a cupboard contributes nothing but noise.
    private static void AddMotion(LevelDef level, Tier tier, IdFactory ids, List<SensorDef> made)
    {
        if (level.rooms == null) return;

        var device = SensorDevices.Get("motion_sensor");

        foreach (var room in level.rooms)
        {
            if (room == null || !WatchedRoom(room.roomType)) continue;
            if (tier != Tier.Care && room.roomType == RoomType.Bathroom) continue;

            made.Add(InCorner("motion_sensor", room, level, ids, Vector2.zero, false));

            // A room longer than one sensor can see needs a second, at the other end. The care homes'
            // corridors are the case: §2.2.2's 1.6 m corridor runs the length of the building, and one
            // sensor in a corner of it reached barely half, which is precisely the stretch a resident
            // walks at 3 AM, and the stretch a fall would go unnoticed in.
            if (LongestExtent(room) <= 0.8f * device.coverageRadius) continue;

            var first = made[made.Count - 1];
            made.Add(InCorner("motion_sensor", room, level, ids,
                              new Vector2(first.position[0], first.position[1]), true));
        }
    }

    /// <summary>The longest side of a room's bounding box: how far a sensor in it has to see.</summary>
    private static float LongestExtent(RoomDef room)
    {
        var poly = PolygonTriangulator.ToVector2(room?.polygon);
        if (poly == null || poly.Count < 3) return 0f;

        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (var p in poly) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }

        return Mathf.Max(max.x - min.x, max.y - min.y);
    }

    // The supported-home layer: what §5.3 describes and what the two five-bedroom samples ship.
    private static void AddCare(LevelDef level, VariantDef variant, IdFactory ids, List<SensorDef> made)
    {
        // §4.3.2: a pad under every bed.
        if (level.furniture != null)
            foreach (var item in level.furniture)
                if (item != null && item.included && Beds.Contains(item.prefabType))
                    made.Add(OnItem("bed_chair_pad", item, ids));

        // §4.5.1. Worn by each resident. The one device that follows a person rather than a room.
        if (variant?.occupants != null)
            foreach (var person in variant.occupants)
                if (person != null && person.included)
                    made.Add(Worn("panic_pendant", person, ids));

        // Every response in the report ends in a spoken prompt, so every room an alert can come from
        // needs a way to speak into it. Without this the package can notice a 3 AM bed exit and have
        // no way to say "it's still night", which is the intervention, not a nicety.
        if (level.rooms != null)
            foreach (var room in level.rooms)
                if (room != null && (room.roomType == RoomType.Bedroom || room.roomType == RoomType.Kitchen))
                    made.Add(InRoom("voice_prompt_speaker", room, level, ids));

        // §4.2.2: one dispenser, in a shared room where staff can refill it and residents pass it.
        var med = FirstOfType(level, RoomType.Kitchen) ?? FirstOfType(level, RoomType.Living);
        if (med != null) made.Add(InRoom("med_dispenser", med, level, ids));
    }

    // ---------------------------------------------------------------------------------------
    // Placement
    // ---------------------------------------------------------------------------------------

    private static SensorDef OnOpening(string type, OpeningDef opening, LevelDef level, IdFactory ids)
    {
        var s = New(type, SensorHost.Opening, opening.id, ids);

        // Face out of the home, so a doorbell's cone covers the approach rather than the hallway.
        // The stored fact is the mounting FACE; SensorPose derives the yaw from it, so the yaw delta
        // stays zero rather than doubling the base as an absolute yaw here used to.
        var wall = SensorPose.Find(level.walls, w => w.id, opening.wallId);
        if (wall != null)
        {
            var frame = WallMeshBuilder.BuildFrame(wall, level);
            Vector2 on = HomeMetrics.PointOnWall(wall, opening.offset);
            var left = new Vector2(frame.left.x, frame.left.z);
            bool roomLeft = HomeMetrics.RoomAt(on + left * (0.5f * frame.thickness + 0.25f), level) != null;
            s.hostSide = roomLeft ? WallSide.Right : WallSide.Left;
        }
        return s;
    }

    private static SensorDef OnItem(string type, ObjectInstance item, IdFactory ids)
    {
        // No yaw of its own: SensorPose already turns a furniture-hosted device with its host, and
        // writing the item's rotation here as well doubled it.
        return New(type, SensorHost.Furniture, item.instanceId, ids);
    }

    private static SensorDef OnWall(string type, WallDef wall, float offset, int side, IdFactory ids)
    {
        var s = New(type, SensorHost.Wall, wall.id, ids);
        s.hostOffset = offset;
        s.hostSide = side;
        return s;
    }

    private static SensorDef InRoom(string type, RoomDef room, LevelDef level, IdFactory ids)
    {
        var s = New(type, SensorHost.Room, room.id, ids);
        Vector2 at = HomeMetrics.LargestInscribedCircle(room).center;
        s.position = new[] { at.x, at.y };
        return s;
    }

    /// <summary>
    /// A coned device in the corner that sees the most of the room, aimed at its center.
    /// </summary>
    /// <remarks>
    /// The corner is not a stylistic choice. A 110-degree cone (§4.3.1's stated field of view) placed
    /// at a rectangular room's CENTRE covers barely half its floor, while the same cone in a corner
    /// covers effectively all of it: the room subtends only 90 degrees from there. Placing these in
    /// the middle would make every coverage figure in the app look like a plan with a hole in it, and
    /// would raise a fall alert every time a resident stood in a part of their own bedroom the sensor
    /// was pointed away from.
    /// </remarks>
    private static SensorDef InCorner(string type, RoomDef room, LevelDef level, IdFactory ids,
                                      Vector2 avoid, bool hasAvoid)
    {
        var s = New(type, SensorHost.Room, room.id, ids);

        var poly = PolygonTriangulator.ToVector2(room.polygon);
        Vector2 center = HomeMetrics.LargestInscribedCircle(room).center;
        Vector2 best = center;
        float bestScore = -1f;

        if (poly != null)
            foreach (var corner in poly)
            {
                // Pulled in off the corner so the device is inside the room rather than in the wall
                // the polygon runs along, and so its own cone origin is not on the boundary.
                Vector2 inward = center - corner;
                if (inward.sqrMagnitude < 1e-6f) continue;

                Vector2 at = corner + inward.normalized * 0.35f;
                if (!HomeMetrics.PointInPolygon(at, poly)) continue;

                // Furthest from the center normally; furthest from the FIRST sensor when there is
                // one, so a second in a long room goes to the other end rather than beside it.
                float score = hasAvoid ? Vector2.Distance(at, avoid) : Vector2.Distance(at, center);
                if (score <= bestScore) continue;
                bestScore = score;
                best = at;
            }

        s.position = new[] { best.x, best.y };
        Vector2 axis = center - best;
        s.facingYaw = axis.sqrMagnitude < 1e-6f ? 0f : SensorPose.YawOf(new Vector3(axis.x, 0f, axis.y));
        return s;
    }

    private static SensorDef AtPoint(string type, RoomDef room, Vector2 at, IdFactory ids)
    {
        var s = New(type, SensorHost.Point, room.id, ids);
        s.position = new[] { at.x, at.y };
        return s;
    }

    private static SensorDef Worn(string type, OccupantDef person, IdFactory ids)
        => New(type, SensorHost.Occupant, person.id, ids);

    private static SensorDef New(string type, string hostKind, string hostId, IdFactory ids)
    {
        var device = SensorDevices.Get(type);
        return new SensorDef
        {
            id = ids.Next(),
            deviceType = type,
            hostKind = hostKind,
            hostId = hostId,
            mountHeight = device.mountHeight,
            coverageRadius = device.coverageRadius,
            coverageAngle = device.coverageAngle,
            privacy = device.privacy,
            monitored = true,
            included = true,
            // Left null on purpose: SensorDevices.EffectiveRules reads the defaults, so a package
            // installed today picks up an improved threshold tomorrow, while a home whose staff have
            // tuned one keeps theirs. Baking them in here would freeze every package at this build.
            rules = null,
        };
    }

    // ---------------------------------------------------------------------------------------

    private static readonly HashSet<string> Beds = new HashSet<string>
    {
        "twin_bed", "full_bed", "hospital_bed",
    };

    private static readonly HashSet<string> WetFixtures = new HashSet<string>
    {
        "toilet", "sink_pedestal", "vanity", "bathtub", "roll_in_shower", "sink_base",
    };

    private static bool WatchedRoom(string roomType)
        => roomType == RoomType.Bedroom || roomType == RoomType.Hall || roomType == RoomType.Living
        || roomType == RoomType.Kitchen || roomType == RoomType.Dining || roomType == RoomType.Entry
        || roomType == RoomType.Bathroom;

    private static RoomDef FirstOfType(LevelDef level, string roomType)
    {
        if (level?.rooms == null) return null;
        foreach (var r in level.rooms) if (r != null && r.roomType == roomType) return r;
        return null;
    }

    private static RoomDef Largest(LevelDef level)
    {
        RoomDef best = null;
        float bestArea = 0f;
        if (level?.rooms == null) return null;
        foreach (var r in level.rooms)
        {
            if (r == null) continue;
            float a = HomeMetrics.RoomArea(r);
            if (a <= bestArea) continue;
            bestArea = a;
            best = r;
        }
        return best;
    }

    private static ObjectInstance FirstItem(LevelDef level, string prefabType)
    {
        if (level?.furniture == null) return null;
        foreach (var f in level.furniture)
            if (f != null && f.included && f.prefabType == prefabType
                && f.position != null && f.position.Length >= 3) return f;
        return null;
    }

    private static ObjectInstance FirstItemIn(LevelDef level, RoomDef room, HashSet<string> types)
    {
        if (level?.furniture == null) return null;
        var poly = PolygonTriangulator.ToVector2(room.polygon);

        foreach (var f in level.furniture)
        {
            if (f == null || !f.included || !types.Contains(f.prefabType)) continue;
            if (f.position == null || f.position.Length < 3) continue;
            if (!HomeMetrics.PointInPolygon(new Vector2(f.position[0], f.position[2]), poly)) continue;
            return f;
        }
        return null;
    }

    /// <summary>
    /// A point just clear of a fixture's footprint, on the side facing into the room: where water
    /// off that fixture reaches the floor first.
    /// </summary>
    /// <remarks>
    /// Stepping a fixed distance off one edge of the rect is what the first version did, and it put
    /// two of a five-bathroom home's water sensors in the room next door: a basin against the far wall
    /// steps straight through it. Offsetting TOWARD the room's own center cannot leave the room, and
    /// the result is checked against the polygon anyway.
    /// </remarks>
    private static Vector2 FootOf(ObjectInstance item, RoomDef room, Vector2 center)
    {
        var rect = HomeMetrics.FootprintOf(item);
        Vector2 from = rect.center;

        Vector2 toCenter = center - from;
        if (toCenter.sqrMagnitude < 1e-6f) return center;

        float reach = 0.5f * Mathf.Max(rect.width, rect.height) + 0.15f;
        Vector2 at = from + toCenter.normalized * reach;

        var poly = PolygonTriangulator.ToVector2(room.polygon);
        return HomeMetrics.PointInPolygon(at, poly) ? at : center;
    }

    // Deterministic ids for authored samples, GUIDs for anything a user adds. The distinction matters
    // to VariantDiff: a sample rebuilt at the next Generation must produce the SAME ids, or a refresh
    // reads as "every device removed and re-added".
    private sealed class IdFactory
    {
        private readonly string _prefix;
        private int _next;

        public IdFactory(string prefix) { _prefix = prefix; }

        public string Next()
            => string.IsNullOrEmpty(_prefix) ? Guid.NewGuid().ToString() : _prefix + (++_next);
    }
}
