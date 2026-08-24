using System.Collections.Generic;
using UnityEngine;

// Where each person is at a given minute of the day.
//
// Positions are DERIVED, never stored. An occupant's schedule names rooms; this turns that plus a
// clock reading into a point on the floor. Storing coordinates instead would mean every proposal that
// moved a wall left people standing inside it, and every schedule edit would need a matching position
// edit: the drift problem the whole variant system exists to avoid.
//
// Everything here is pure and lives in CXRAuthoring, so the EditMode tests cover all of it without a
// scene, a renderer, or a MonoBehaviour.
public static class OccupancyModel
{
    // Half the marker's 0.45 m diameter, plus a little breathing room.
    public const float PersonRadius = 0.26f;

    // A wheelchair marker is a 0.66 x 1.22 m pad, not a capsule (ResidenceRenderer.BuildOccupantMarker), so
    // it needs more floor than a standing person. Half the pad's WIDTH plus a margin, though: not half
    // its length: asking for 0.61 in every direction demands a 1.22 m clear circle, which none of the
    // 1.8 m bathrooms can offer, and would push every wheelchair user back out to the room center.
    // A chair can be turned; the clearance that has to hold in all directions is the narrow one.
    public const float WheelchairRadius = 0.45f;

    // How far in front of a piece of furniture a person stands when an activity names an anchor. The
    // gap is measured from the item's front face, so it is the space between them, not center to center.
    private const float AnchorStandoff = 0.35f;

    // Fan-out ring limits when several people share a room. The minimum is what keeps two people from
    // reading as one box; the maximum keeps a group legible as a group rather than scattered.
    private const float MinSeparation = 0.55f;
    private const float MaxRingRadius = 0.9f;

    // Something you can stand ON is not an obstacle: a roll-in shower is 0.05 m tall and a threshold
    // ramp 0.03. Anything taller occupies the floor it sits on.
    private const float BlockingHeight = 0.15f;

    // Grid pitch for the free-floor search. 0.15 m is finer than the 0.26 m disc it is placing, so a
    // gap wide enough to stand in cannot fall between samples.
    private const float SearchStep = 0.15f;

    /// <summary>
    /// How a person relates to the item their activity names. This is a property of the ITEM, not of
    /// the activity kind: a "relax" block anchored to a range should still stand at the range.
    /// </summary>
    public enum Posture
    {
        InFrontOf,   // appliances and fixtures you work at: stand off the front face, facing it
        At,          // tables: stand at the edge, facing in
        On,          // beds, seating, fixtures you occupy: the item's own footprint
    }

    // Keyed by the catalog id, which is the shared key space between FurnitureCatalog, PrefabRegistry
    // and SampleFurniture, so this works for a residence the user drew, not just the shipped six.
    private static readonly HashSet<string> OccupiedItems = new HashSet<string>
    {
        "twin_bed", "full_bed", "hospital_bed", "sofa", "armchair", "recliner",
        "toilet", "bathtub", "roll_in_shower", "transfer_bench", "shower_seat", "wheelchair",
    };

    private static readonly HashSet<string> EdgeItems = new HashSet<string>
    {
        "dining_table", "coffee_table", "island",
    };

    /// <summary>Unknown ids fall back to InFrontOf, which is the behaviour this always had.</summary>
    public static Posture PostureFor(string prefabType)
    {
        if (string.IsNullOrEmpty(prefabType)) return Posture.InFrontOf;
        if (OccupiedItems.Contains(prefabType)) return Posture.On;
        if (EdgeItems.Contains(prefabType)) return Posture.At;
        return Posture.InFrontOf;
    }

    /// <summary>The floor clearance this person's marker actually needs.</summary>
    public static float RadiusFor(OccupantDef occupant)
        => occupant != null && occupant.usesWheelchair ? WheelchairRadius : PersonRadius;

    public struct Pose
    {
        public Vector2 xz;            // where the marker stands, in world XZ
        public float yaw;             // degrees; 0 faces +Z, matching PlanBuilder's convention
        public RoomDef room;          // the resolved room, null when away or unresolvable
        public ActivityDef activity;  // what they are doing, null when the schedule is empty
        public bool present;          // false => no marker on THIS level (out, upstairs, or unresolved)

        // They are in the residence, just not on the story being posed. `present` stays false. There is
        // nothing to draw on this floor and no sensor here can see them, but "not on this floor" and
        // "not in the building" are different answers, and only one of them should be reported to a
        // caregiver as the resident having gone out.
        //
        // Always false when PoseAt is called without a variant, which is every single-story caller.
        public bool elsewhereInResidence;
    }

    // ---------------------------------------------------------------------------------------
    // Schedule lookup
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// What this person is doing at <paramref name="minutes"/>. On a gap in the schedule, falls back to
    /// the most recently STARTED activity rather than returning null: a hole in a hand-typed timeline
    /// should not make someone blink out of the house.
    /// </summary>
    public static ActivityDef ActivityAt(OccupantDef occupant, int minutes)
    {
        var schedule = occupant?.schedule;
        if (schedule == null || schedule.Count == 0) return null;

        int m = Clock.Wrap(minutes);

        // First match wins. Overlaps are a Validate warning, not a runtime decision to agonise over.
        foreach (var a in schedule)
            if (a != null && Clock.Spans(a.startMinutes, a.endMinutes, m)) return a;

        ActivityDef best = null;
        int bestAge = int.MaxValue;
        foreach (var a in schedule)
        {
            if (a == null) continue;
            int age = Clock.Wrap(m - Clock.Wrap(a.startMinutes));
            if (age >= bestAge) continue;
            bestAge = age;
            best = a;
        }
        return best;
    }

    // ---------------------------------------------------------------------------------------
    // Placement
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Where one person stands. <paramref name="indexInRoom"/> and <paramref name="countInRoom"/> come
    /// from PoseAll and fan a group out around the room's anchor point; call with (0, 1) for a person
    /// considered on their own.
    /// </summary>
    public static Pose PoseAt(OccupantDef occupant, int minutes, LevelDef level,
                              int indexInRoom = 0, int countInRoom = 1, VariantDef variant = null)
    {
        var pose = new Pose { present = false };
        if (occupant == null) return pose;

        pose.activity = ActivityAt(occupant, minutes);
        if (pose.activity == null) return pose;

        // No room named means away from residence: the expected, silent case for a work or errand block.
        if (string.IsNullOrEmpty(pose.activity.roomId)) return pose;

        pose.room = FindRoom(level, pose.activity.roomId);
        if (pose.room == null)
        {
            // The room may simply be on another story. Without this the only two answers available
            // were "here" and "out", so on a two-story residence everybody upstairs read as having left
            // the building: in the console, on the timeline and in the roster.
            pose.elsewhereInResidence = variant != null && FindRoomAnyLevel(variant, pose.activity.roomId) != null;
            return pose;   // Validate reports a genuinely missing room; here it just hides the marker.
        }

        var poly = PolygonTriangulator.ToVector2(pose.room.polygon);
        if (poly == null || poly.Count < 3) return pose;

        float radius = RadiusFor(occupant);

        // An anchored activity resolves against the item it names: on the bed while sleeping, at the
        // table while eating, in front of the range while cooking. Falls through to the free-floor
        // search when no face of the item is approachable, which happens when it is boxed in.
        var item = FindFurniture(level, pose.activity.anchorId);
        if (item != null && TryPlaceAtAnchor(item, level, poly, radius, indexInRoom, countInRoom,
                                             out Vector2 spot, out float facing))
        {
            pose.xz = spot;
            pose.yaw = facing;
            pose.present = true;
            return pose;
        }

        // Unanchored: the clearest floor in the room. NOT LargestInscribedCircle on its own: that
        // measures the bare room and would happily stand someone in the middle of the sofa.
        ResidenceMetrics.Circle circle = ResidenceMetrics.LargestInscribedCircle(pose.room);
        Vector2 preferred = circle.valid ? circle.center : ResidenceMetrics.RoomCentroid(pose.room);

        if (countInRoom > 1)
        {
            float ring = RingRadius(countInRoom, circle.valid ? circle.radius : 0f);
            float angle = Mathf.PI * 2f * indexInRoom / countInRoom;
            preferred += new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * ring;
        }

        pose.xz = ClearSpot(pose.room, level, poly, radius, preferred);
        // Face the room's center of mass, so a group on a ring looks at each other rather than at the
        // walls. Degenerate when someone is already standing on it, which is fine. Yaw is then 0.
        pose.yaw = YawToward(pose.xz, ResidenceMetrics.RoomCentroid(pose.room));
        pose.present = true;
        return pose;
    }

    /// <summary>
    /// Everyone in the variant at one instant, keyed by occupant id. Resolves rooms first so that
    /// people sharing a room can be fanned apart instead of stacking into a single box.
    /// </summary>
    public static Dictionary<string, Pose> PoseAll(VariantDef variant, LevelDef level, int minutes)
    {
        var poses = new Dictionary<string, Pose>();
        var occupants = variant?.occupants;
        if (occupants == null) return poses;

        // Pass one: how many people end up in each room.
        var headcount = new Dictionary<string, int>();
        foreach (var o in occupants)
        {
            string roomId = RoomIdFor(o, minutes, level);
            if (roomId == null) continue;
            headcount.TryGetValue(roomId, out int n);
            headcount[roomId] = n + 1;
        }

        // Pass two: place each person, handing them their slot in the room's ring.
        var seen = new Dictionary<string, int>();
        foreach (var o in occupants)
        {
            if (o == null || string.IsNullOrEmpty(o.id) || poses.ContainsKey(o.id)) continue;

            int index = 0, count = 1;
            string roomId = RoomIdFor(o, minutes, level);
            if (roomId != null)
            {
                seen.TryGetValue(roomId, out index);
                seen[roomId] = index + 1;
                count = headcount[roomId];
            }

            poses[o.id] = PoseAt(o, minutes, level, index, count, variant);
        }
        return poses;
    }

    /// <summary>"Bathroom 1 · Getting ready", or "Out": the one-line status the rail and dashboard show.</summary>
    public static string Describe(Pose pose)
    {
        string what = pose.activity == null
            ? "No schedule"
            : (string.IsNullOrEmpty(pose.activity.label) ? ActivityKind.Label(pose.activity.kind) : pose.activity.label);

        if (pose.activity == null) return what;
        if (pose.room != null) return RoomLabel(pose.room) + " · " + what;
        // Upstairs is not a broken reference. Saying "room missing" for it would report a correct
        // schedule as a fault in every roster, rail and console that shows this line.
        if (pose.elsewhereInResidence) return what + " · on another floor";
        if (pose.activity.roomId != null && pose.activity.roomId.Length > 0) return what + " · room missing";
        return what;
    }

    public static string RoomLabel(RoomDef room)
    {
        if (room == null) return "Unknown";
        if (!string.IsNullOrEmpty(room.name)) return room.name;
        return string.IsNullOrEmpty(room.roomType) ? room.id : room.roomType;
    }

    // ---------------------------------------------------------------------------------------
    // Validation: everything that would otherwise be a silent placement bug
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Appends a plain-language warning for every problem in the variant's rosters. Called by
    /// PlanBuilder so authored samples fail a test rather than rendering wrong, and available to the
    /// UI. Nothing here throws: an unresolvable room is a proposal that deleted a room, not a crash.
    /// </summary>
    public static void Validate(VariantDef variant, LevelDef level, IList<string> warnings)
    {
        var occupants = variant?.occupants;
        if (occupants == null || warnings == null) return;

        var ids = new HashSet<string>();
        foreach (var o in occupants)
        {
            if (o == null) continue;

            string who = string.IsNullOrEmpty(o.name) ? (o.id ?? "an occupant") : o.name;

            if (string.IsNullOrEmpty(o.id)) warnings.Add($"'{who}' has no id.");
            else if (!ids.Add(o.id)) warnings.Add($"Two occupants share the id '{o.id}'.");

            if (string.IsNullOrEmpty(o.name)) warnings.Add($"Occupant '{o.id}' has no name.");

            if (o.schedule == null || o.schedule.Count == 0)
            {
                warnings.Add($"'{who}' has no schedule.");
                continue;
            }

            ValidateSchedule(o, who, level, variant, warnings);
        }
    }

    private static void ValidateSchedule(OccupantDef o, string who, LevelDef level, VariantDef variant,
                                         IList<string> warnings)
    {
        // One pass over the day marks coverage and catches overlap at the same time, which is both
        // simpler and more honest than comparing intervals pairwise across a midnight wrap.
        var covered = new bool[Clock.MinutesPerDay];
        int firstOverlap = -1;

        foreach (var a in o.schedule)
        {
            if (a == null) continue;

            if (!ActivityKind.IsKnown(a.kind))
                warnings.Add($"'{who}' has an activity of unknown kind '{a.kind}'.");

            if (a.startMinutes < 0 || a.startMinutes >= Clock.MinutesPerDay ||
                a.endMinutes < 0 || a.endMinutes >= Clock.MinutesPerDay)
                warnings.Add($"'{who}' has an activity with a time outside the day " +
                             $"({a.startMinutes} to {a.endMinutes} minutes).");

            ValidatePlace(a, o, who, level, variant, warnings);

            int start = Clock.Wrap(a.startMinutes);
            int span = Clock.DurationBetween(a.startMinutes, a.endMinutes);
            for (int i = 0; i < span; i++)
            {
                int m = Clock.Wrap(start + i);
                if (covered[m] && firstOverlap < 0) firstOverlap = m;
                covered[m] = true;
            }
        }

        if (firstOverlap >= 0)
            warnings.Add($"'{who}' is doing two things at once at {Clock.Format(firstOverlap)}.");

        for (int m = 0; m < covered.Length; m++)
        {
            if (covered[m]) continue;
            warnings.Add($"'{who}' has nothing scheduled at {Clock.Format(m)}.");
            break;   // one report per person; the first gap is enough to send someone back to the table
        }
    }

    private static void ValidatePlace(ActivityDef a, OccupantDef o, string who, LevelDef level,
                                      VariantDef variant,
                                      IList<string> warnings)
    {
        string what = string.IsNullOrEmpty(a.label) ? ActivityKind.Label(a.kind) : a.label;

        if (string.IsNullOrEmpty(a.roomId))
        {
            // Only a problem when the kind implies being residence. "Out" with no room is the normal case.
            if (!ActivityKind.IsAway(a.kind))
                warnings.Add($"'{who}' has no room for \"{what}\".");
            return;
        }

        var room = FindRoom(level, a.roomId);
        if (room == null)
        {
            // Only a fault if the room is on NO story. Scheduling someone into an upstairs bedroom is
            // the whole point of having stories, and warning about it would make a correct plan noisy
            //: every activity on every other floor would report itself as broken.
            if (FindRoomAnyLevel(variant, a.roomId) == null)
                warnings.Add($"'{who}' is scheduled into '{a.roomId}' for \"{what}\", which is not a room anywhere in this residence.");
            return;
        }

        if (string.IsNullOrEmpty(a.anchorId)) return;

        var item = FindFurniture(level, a.anchorId);
        if (item == null)
        {
            if (FindFurnitureAnyLevel(variant, a.anchorId) == null)
                warnings.Add($"'{who}' is anchored to '{a.anchorId}' for \"{what}\", which is not an item anywhere in this residence.");
            return;
        }

        var poly = PolygonTriangulator.ToVector2(room.polygon);
        if (poly != null && poly.Count >= 3 && !ResidenceMetrics.PointInPolygon(XZ(item.position), poly))
            warnings.Add($"'{who}' is anchored to an item outside '{RoomLabel(room)}' for \"{what}\".");
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    public static RoomDef FindRoom(LevelDef level, string roomId)
    {
        if (level?.rooms == null || string.IsNullOrEmpty(roomId)) return null;
        foreach (var r in level.rooms)
            if (r != null && r.id == roomId) return r;
        return null;
    }

    /// <summary>
    /// The same room, on whichever story of the variant holds it. Occupants hang off the VARIANT
    /// while rooms hang off a LEVEL, so anything that resolves a person's room against one story is
    /// asking half the question: this asks the other half.
    /// </summary>
    public static RoomDef FindRoomAnyLevel(VariantDef variant, string roomId)
        => FindRoomAnyLevel(variant, roomId, out _);

    public static RoomDef FindRoomAnyLevel(VariantDef variant, string roomId, out LevelDef onLevel)
    {
        onLevel = null;
        if (variant?.levels == null || string.IsNullOrEmpty(roomId)) return null;
        foreach (var l in variant.levels)
        {
            var r = FindRoom(l, roomId);
            if (r != null) { onLevel = l; return r; }
        }
        return null;
    }

    /// <summary>The same, for an anchor item.</summary>
    public static ObjectInstance FindFurnitureAnyLevel(VariantDef variant, string instanceId)
    {
        if (variant?.levels == null || string.IsNullOrEmpty(instanceId)) return null;
        foreach (var l in variant.levels)
        {
            var f = FindFurniture(l, instanceId);
            if (f != null) return f;
        }
        return null;
    }

    public static ObjectInstance FindFurniture(LevelDef level, string instanceId)
    {
        if (level?.furniture == null || string.IsNullOrEmpty(instanceId)) return null;
        foreach (var f in level.furniture)
            if (f != null && f.instanceId == instanceId) return f;
        return null;
    }

    // The room a person resolves into, or null when they are away or the room is missing. Shared by
    // both passes of PoseAll so headcount and placement can never disagree.
    private static string RoomIdFor(OccupantDef o, int minutes, LevelDef level)
    {
        if (o == null || !o.included) return null;
        var a = ActivityAt(o, minutes);
        if (a == null || string.IsNullOrEmpty(a.roomId)) return null;
        return FindRoom(level, a.roomId) != null ? a.roomId : null;
    }

    /// <summary>
    /// Is a disc of <paramref name="radius"/> at <paramref name="p"/> on clear floor? Two conditions,
    /// and the old code checked neither properly.
    ///
    /// The room polygon runs along wall CENTERLINES (PlanBuilder's convention), so its own boundary is
    /// half a wall thickness INSIDE the plaster. Testing a bare point against it, which is all
    /// PointInPolygon does. Happily leaves someone standing in the wall.
    /// </summary>
    public static bool IsClear(Vector2 p, float radius, LevelDef level,
                               IReadOnlyList<Vector2> poly, ObjectInstance ignore)
    {
        float wall = 0.5f * WallLayout.EffectiveThickness(null, level);
        if (ResidenceMetrics.SignedDistanceInside(p, poly) < radius + wall) return false;

        if (level?.furniture == null) return true;
        foreach (var f in level.furniture)
        {
            if (f == null || !f.included || ReferenceEquals(f, ignore)) continue;
            if (ResidenceMetrics.HeightOf(f) < BlockingHeight) continue;
            if (ResidenceMetrics.PointRectDistance(p, ResidenceMetrics.FootprintOf(f)) < radius) return false;
        }
        return true;
    }

    // Resolves an anchored activity against the item it names. Returns false when nothing around the
    // item is clear, which hands the person to the free-floor search rather than dropping them inside
    // whatever is in the way.
    private static bool TryPlaceAtAnchor(ObjectInstance item, LevelDef level,
                                         IReadOnlyList<Vector2> poly, float radius,
                                         int index, int count, out Vector2 spot, out float yaw)
    {
        spot = Vector2.zero;
        yaw = 0f;
        if (item?.position == null || item.position.Length < 3) return false;

        Vector2 center = XZ(item.position);
        float rad = item.rotationY * Mathf.Deg2Rad;

        // Several people on one sofa spread along its width instead of stacking on its center. This
        // used to be unreachable for anchored people: the ring sat after an early return.
        float lateral = 0f;
        if (count > 1)
        {
            float usable = Mathf.Max(0f, LocalWidth(item) - 2f * radius);
            lateral = (index - 0.5f * (count - 1)) * Mathf.Min(usable / (count - 1), MinSeparation);
        }

        Posture posture = PostureFor(item.prefabType);
        if (posture == Posture.On)
        {
            // You occupy this one, so it is not its own obstacle, but everything else still is.
            var across = new Vector2(Mathf.Cos(rad), -Mathf.Sin(rad));   // the item's local +X
            spot = center + across * lateral;
            yaw = item.rotationY;
            return IsClear(spot, radius, level, poly, item);
        }

        // A table is approached to its edge; an appliance keeps a working gap in front of it.
        float gap = posture == Posture.At ? radius : AnchorStandoff + radius;

        // Front face first, then the other three. A range shoved into a corner may only be reachable
        // from one side, and refusing outright would drop the person on the room center instead.
        for (int turn = 0; turn < 4; turn++)
        {
            float a = rad + turn * Mathf.PI * 0.5f;
            var dir = new Vector2(Mathf.Sin(a), Mathf.Cos(a));
            var side = new Vector2(dir.y, -dir.x);
            // Turns 0 and 2 leave by the depth faces, 1 and 3 by the width faces.
            float half = 0.5f * (turn % 2 == 0 ? LocalDepth(item) : LocalWidth(item));

            Vector2 candidate = center + dir * (half + gap) + side * lateral;
            if (!IsClear(candidate, radius, level, poly, item)) continue;

            spot = candidate;
            yaw = Mathf.Repeat(a * Mathf.Rad2Deg + 180f, 360f);   // face back at the item
            return true;
        }
        return false;
    }

    // The clearest floor in the room, nearest `preferred`. Relaxes rather than failing: a crowded room
    // must still place everyone, because hiding a marker is a worse answer than a tight fit.
    private static Vector2 ClearSpot(RoomDef room, LevelDef level, IReadOnlyList<Vector2> poly,
                                     float radius, Vector2 preferred)
    {
        if (TryFindClearSpot(room, level, poly, radius, preferred, out Vector2 spot)) return spot;
        if (radius > PersonRadius &&
            TryFindClearSpot(room, level, poly, PersonRadius, preferred, out spot)) return spot;
        if (TryFindClearSpot(room, level, poly, 0f, preferred, out spot)) return spot;
        return preferred;
    }

    /// <summary>
    /// Nearest point to <paramref name="preferred"/> where the disc fits. Sampled on a grid rather than
    /// solved: the exact answer is a Minkowski-difference free-space polygon, which is a great deal of
    /// machinery for a marker that only needs to be standing somewhere sensible.
    /// </summary>
    public static bool TryFindClearSpot(RoomDef room, LevelDef level, IReadOnlyList<Vector2> poly,
                                        float radius, Vector2 preferred, out Vector2 spot)
    {
        spot = preferred;
        if (poly == null || poly.Count < 3) return false;

        // Overwhelmingly the common case once the plans are sane, and it keeps the grid off the
        // per-frame path entirely.
        if (IsClear(preferred, radius, level, poly, null)) return true;

        // Room ids repeat across residences ("r_bath1" exists in four of the six samples), so the cache has
        // to be scoped to the level as well or one residence's answer is served to another.
        if (!ReferenceEquals(_cacheLevel, level))
        {
            _spotCache.Clear();
            _cacheLevel = level;
        }

        var key = new SpotKey(room?.id, radius, preferred);
        if (_spotCache.TryGetValue(key, out Vector2 cached))
        {
            spot = cached;
            return !float.IsNaN(cached.x);
        }

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in poly)
        {
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
        }

        int nx = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / SearchStep));
        int ny = Mathf.Max(1, Mathf.CeilToInt((maxY - minY) / SearchStep));

        bool found = false;
        float best = float.MaxValue;
        for (int i = 0; i <= nx; i++)
        for (int j = 0; j <= ny; j++)
        {
            var p = new Vector2(minX + (maxX - minX) * i / nx, minY + (maxY - minY) * j / ny);
            float d = (p - preferred).sqrMagnitude;
            if (d >= best) continue;
            if (!IsClear(p, radius, level, poly, null)) continue;
            best = d; spot = p; found = true;
        }

        // Negative results are cached too: a bathroom with no clear metre of floor would otherwise
        // re-run the whole grid on every OnGUI pass.
        _spotCache[key] = found ? spot : new Vector2(float.NaN, float.NaN);
        return found;
    }

    /// <summary>
    /// Drops the memoised free-floor spots. Called from ResidenceRenderer whenever the level is rebuilt,
    /// because the search depends on the geometry but never on the clock. PoseAll runs every simulated
    /// minute AND from OnGUI, so re-running a grid per call would be felt.
    /// </summary>
    public static void InvalidateCache()
    {
        _spotCache.Clear();
        _cacheLevel = null;
    }

    private static readonly Dictionary<SpotKey, Vector2> _spotCache = new Dictionary<SpotKey, Vector2>();
    private static LevelDef _cacheLevel;

    // Quantised so the same request from frame to frame hits the same entry.
    private readonly struct SpotKey : System.IEquatable<SpotKey>
    {
        private readonly string _room;
        private readonly int _radius, _x, _y;

        public SpotKey(string room, float radius, Vector2 preferred)
        {
            _room = room ?? "";
            _radius = Mathf.RoundToInt(radius * 100f);
            _x = Mathf.RoundToInt(preferred.x * 100f);
            _y = Mathf.RoundToInt(preferred.y * 100f);
        }

        public bool Equals(SpotKey o)
            => _radius == o._radius && _x == o._x && _y == o._y && _room == o._room;

        public override bool Equals(object o) => o is SpotKey k && Equals(k);

        public override int GetHashCode()
            => (((_room.GetHashCode() * 397) ^ _radius) * 397 ^ _x) * 397 ^ _y;
    }

    private static float LocalWidth(ObjectInstance item)
        => item?.boxSizeMeters != null && item.boxSizeMeters.Length >= 3
            ? Mathf.Max(0.1f, item.boxSizeMeters[0])
            : 0.6f;

    private static float LocalDepth(ObjectInstance item)
        => item?.boxSizeMeters != null && item.boxSizeMeters.Length >= 3
            ? Mathf.Max(0.1f, item.boxSizeMeters[2])
            : 0.6f;

    // Radius that keeps a group of `count` at least MinSeparation apart, without leaving the room. The
    // chord between neighbors on a ring of radius r is 2r·sin(π/n), so this inverts that.
    private static float RingRadius(int count, float roomRadius)
    {
        float needed = MinSeparation / (2f * Mathf.Max(0.05f, Mathf.Sin(Mathf.PI / Mathf.Max(2, count))));
        float available = Mathf.Max(0f, roomRadius - PersonRadius - 0.05f);
        if (available <= 0f) return Mathf.Min(needed, MaxRingRadius);
        return Mathf.Clamp(needed, 0f, Mathf.Min(MaxRingRadius, available));
    }

    private static float YawToward(Vector2 from, Vector2 to)
    {
        Vector2 d = to - from;
        if (d.sqrMagnitude <= ResidenceConventions.EPS * ResidenceConventions.EPS) return 0f;
        return Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
    }

    private static Vector2 XZ(float[] p)
        => p != null && p.Length >= 3 ? new Vector2(p[0], p[2]) : Vector2.zero;
}
