using System.Collections.Generic;
using UnityEngine;

// Where an installed sensor actually is, derived from the element it hosts on.
//
// The counterpart of HomeRenderer.MountPose, and lifted out here for the same two reasons that one
// was: the ghost overlay has to place a REMOVED sensor from the variant that still has its host, and
// the renderer, the plan overlay, the change list's marker anchor and the report all have to agree
// about where a device is down to the millimetre. One function, five callers, no drift.
//
// It takes an explicit LevelDef rather than reading a renderer's current one, which is what lets the
// ghost resolve against the other variant's level.
//
// A worn device has no pose and says so: `resolved` is false for SensorHost.Occupant, and every
// caller either skips it (the renderer, the overlay) or shows it against a person instead (the
// console, the change list). Returning the wearer's live position would be wrong twice over: it is
// not where the device is INSTALLED, and it would make a pendant's marker jump around the plan every
// minute of the simulated day.
public static class SensorPose
{
    public struct Pose
    {
        public bool resolved;         // false => this device has no place in the plan
        public Vector3 position;      // world, meters, at the device's own mount height
        public Vector2 xz;            // the same point on the floor plane, for overlays and anchors
        public float yaw;             // degrees; 0 looks down +Z, PlanBuilder's convention
        public RoomDef room;          // the room it is in or belongs to, when there is one
        public string hostLabel;      // "Front door", "Bedroom 2", "Alice". Presentation-ready
    }

    /// <summary>
    /// Resolves a sensor against a level. Returns an unresolved pose rather than throwing when the
    /// host is missing: a proposal that deletes a wall leaves its sensors dangling for exactly as
    /// long as it takes VariantRevert or SelectTool.DeleteSelected to catch up, and a null-reference
    /// mid-render would be a worse answer than an invisible device.
    /// </summary>
    public static Pose Resolve(SensorDef sensor, LevelDef level, VariantDef variant = null)
    {
        var pose = new Pose();
        if (sensor == null || level == null) return pose;

        switch (sensor.hostKind)
        {
            case SensorHost.Opening: return OnOpening(sensor, level);
            case SensorHost.Furniture: return OnFurniture(sensor, level);
            case SensorHost.Wall: return OnWall(sensor, level);
            case SensorHost.Room: return InRoom(sensor, level);
            case SensorHost.Point: return AtPoint(sensor, level);
            case SensorHost.Occupant: return Worn(sensor, variant);
            default: return pose;
        }
    }

    /// <summary>True when this sensor draws anything in the plan at all.</summary>
    public static bool HasPlace(SensorDef sensor) => sensor != null && sensor.hostKind != SensorHost.Occupant;

    // ---------------------------------------------------------------------------------------

    // At the head of the doorway, on the face of the wall the click came from. hostSide, read
    // exactly as OnWall reads it. It used to sit on the centerline on the grounds that an opening has
    // no natural side, and a 2-6 cm box on the centerline of a 10 cm wall rendered buried inside the
    // wall's body. The face is where the physical device actually is: a doorbell is beside the
    // opening outside, a lock is on one face of the leaf.
    private static Pose OnOpening(SensorDef sensor, LevelDef level)
    {
        var opening = Find(level.openings, o => o.id, sensor.hostId);
        if (opening == null) return default;

        var wall = Find(level.walls, w => w.id, opening.wallId);
        if (wall == null) return default;

        var frame = WallMeshBuilder.BuildFrame(wall, level);
        float height = MountHeight(sensor, opening, level);

        Vector3 outward = sensor.hostSide == WallSide.Right ? -frame.left : frame.left;
        float push = 0.5f * frame.thickness + 0.01f;

        // The box sits proud of the mounting face; the PLAN anchor stays on the centerline. Room
        // attribution, overlay anchors and coverage all read `xz`, and an exterior door's outside
        // face is outside every room. Pushing the anchor with the box would re-home the device
        // to nowhere.
        Vector3 on = frame.origin + frame.forward * opening.offset;
        Vector3 p = on + Vector3.up * height + outward * push;
        var xz = new Vector2(on.x, on.z);

        return new Pose
        {
            resolved = true,
            position = p,
            xz = xz,
            // Looking out of the mounting face, plus whatever the device was turned by. A doorbell
            // pointed the wrong way through its own door would draw its cone into the wall.
            yaw = YawOf(outward) + sensor.facingYaw,
            room = HomeMetrics.RoomAt(xz, level),
            hostLabel = OpeningLabel(opening, level),
        };
    }

    // On the host item, turning with it. Two forms, told apart by whether the sensor carries a point
    // of its own:
    //
    //  * `position` set. SensorFit's surface placement. The point is in the ITEM'S own unrotated
    //    frame, so a dispenser put on the corner of a counter rides the counter when it is moved or
    //    turned, and it sits on the item's TOP face: the item's height plus half the device's own.
    //  * `position` null: every sensor placed before surfaces existed, and the samples: the item's
    //    center at the catalog height. mountHeight 0 (the pad's default) puts the device just off
    //    the floor rather than at the item's origin, which is the same place.
    private static Pose OnFurniture(SensorDef sensor, LevelDef level)
    {
        var item = Find(level.furniture, f => f.instanceId, sensor.hostId);
        if (item == null || item.position == null || item.position.Length < 3) return default;

        Vector2 xz;
        float y;
        if (sensor.position != null && sensor.position.Length >= 2)
        {
            Vector2 world = LocalToWorld(new Vector2(sensor.position[0], sensor.position[1]),
                                         item.rotationY);
            xz = new Vector2(item.position[0] + world.x, item.position[2] + world.y);
            float half = 0.5f * Mathf.Max(0.03f, SensorDevices.Get(sensor.deviceType).height);
            y = level.elevation + HomeMetrics.HeightOf(item) + half;
        }
        else
        {
            xz = new Vector2(item.position[0], item.position[2]);
            y = level.elevation + Mathf.Max(0.02f, SensorDevices.MountHeightOf(sensor));
        }

        return new Pose
        {
            resolved = true,
            position = new Vector3(xz.x, y, xz.y),
            xz = xz,
            yaw = item.rotationY + sensor.facingYaw,
            room = HomeMetrics.RoomAt(xz, level),
            hostLabel = ItemLabel(item, level),
        };
    }

    // Exactly WallMountDef's arithmetic (offset along a -> b, pushed clear of the chosen face) so a
    // smart switch and a light switch on the same wall land on the same plane.
    private static Pose OnWall(SensorDef sensor, LevelDef level)
    {
        var wall = Find(level.walls, w => w.id, sensor.hostId);
        if (wall == null) return default;

        var frame = WallMeshBuilder.BuildFrame(wall, level);
        Vector3 outward = sensor.hostSide == WallSide.Left ? frame.left : -frame.left;
        float push = 0.5f * frame.thickness + 0.01f;

        Vector3 p = frame.origin + frame.forward * sensor.hostOffset
                  + Vector3.up * SensorDevices.MountHeightOf(sensor) + outward * push;
        var xz = new Vector2(p.x, p.z);

        return new Pose
        {
            resolved = true,
            position = p,
            xz = xz,
            yaw = YawOf(outward) + sensor.facingYaw,
            room = HomeMetrics.RoomAt(xz, level),
            hostLabel = RoomName(HomeMetrics.RoomAt(xz, level)),
        };
    }

    // A room-hosted device keeps the point it was placed at, because a motion sensor's cone starts
    // somewhere specific and the corner it was put in is the whole placement decision. Falling back
    // to the inscribed center (the point furthest from any wall) is what a device authored without
    // a point gets, and it is the most useful single point in a room for something omnidirectional.
    private static Pose InRoom(SensorDef sensor, LevelDef level)
    {
        var room = Find(level.rooms, r => r.id, sensor.hostId);
        if (room == null) return default;

        Vector2 xz = sensor.position != null && sensor.position.Length >= 2
            ? new Vector2(sensor.position[0], sensor.position[1])
            : HomeMetrics.LargestInscribedCircle(room).center;

        return new Pose
        {
            resolved = true,
            position = new Vector3(xz.x, level.elevation + FreeHeight(sensor, level), xz.y),
            xz = xz,
            yaw = sensor.facingYaw,
            room = room,
            hostLabel = RoomName(room),
        };
    }

    private static Pose AtPoint(SensorDef sensor, LevelDef level)
    {
        if (sensor.position == null || sensor.position.Length < 2) return default;

        var xz = new Vector2(sensor.position[0], sensor.position[1]);
        var room = HomeMetrics.RoomAt(xz, level);

        return new Pose
        {
            resolved = true,
            position = new Vector3(xz.x, level.elevation + FreeHeight(sensor, level), xz.y),
            xz = xz,
            yaw = sensor.facingYaw,
            room = room,
            hostLabel = RoomName(room),
        };
    }

    // Worn: no place, but a name, so the console and the change list can say whose it is.
    private static Pose Worn(SensorDef sensor, VariantDef variant)
    {
        var person = variant?.occupants == null ? null : Find(variant.occupants, o => o.id, sensor.hostId);
        return new Pose { resolved = false, hostLabel = person?.name ?? "a resident" };
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Where a device on an opening sits vertically: its own mount height, but never above the
    /// opening's head: a door sensor belongs on the frame, and the catalog's 2.0 m default is above
    /// the head of a 1.2 m window.
    /// </summary>
    private static float MountHeight(SensorDef sensor, OpeningDef opening, LevelDef level)
    {
        float wanted = SensorDevices.MountHeightOf(sensor);
        float head = opening.sillHeight + opening.height;
        float ceiling = level.ceilingHeight > 0f ? level.ceilingHeight : HomeConventions.DEFAULT_CEILING_HEIGHT;
        return Mathf.Clamp(Mathf.Min(wanted, head), 0.1f, ceiling - 0.05f);
    }

    /// <summary>
    /// The height of a device standing free in a room or on the floor: its catalog height, floored so
    /// the box's underside never dips below the floor plane, and lifted to the ceiling for a ceiling
    /// fitting. The pose is the box's CENTRE, which is why the half-height appears in both bounds,
    /// the bin's mountHeight of 0 used to sink half the bin below the floor.
    /// </summary>
    private static float FreeHeight(SensorDef sensor, LevelDef level)
    {
        float half = 0.5f * Mathf.Max(0.03f, SensorDevices.Get(sensor.deviceType).height);

        if (SensorDevices.CeilingMounted(sensor.deviceType))
        {
            float ceiling = level.ceilingHeight > 0f ? level.ceilingHeight
                                                     : HomeConventions.DEFAULT_CEILING_HEIGHT;
            return ceiling - half;
        }
        return Mathf.Max(SensorDevices.MountHeightOf(sensor), half);
    }

    /// <summary>Degrees about Y for a horizontal direction, matching Quaternion.LookRotation's frame.</summary>
    public static float YawOf(Vector3 direction)
    {
        var flat = new Vector2(direction.x, direction.z);
        if (flat.sqrMagnitude < 1e-8f) return 0f;
        return Mathf.Atan2(flat.x, flat.y) * Mathf.Rad2Deg;
    }

    /// <summary>Unit direction the sensor faces, in world XZ. The axis of its coverage cone.</summary>
    public static Vector2 Facing(float yaw)
    {
        float r = yaw * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(r), Mathf.Cos(r));
    }

    /// <summary>
    /// A point in a host item's own unrotated frame, taken to a world XZ offset from the item's
    /// center. Inverse of <see cref="WorldToLocal"/>; the yaw convention matches Facing's.
    /// </summary>
    public static Vector2 LocalToWorld(Vector2 local, float yawDeg)
    {
        float r = yawDeg * Mathf.Deg2Rad;
        float c = Mathf.Cos(r), s = Mathf.Sin(r);
        return new Vector2(local.x * c + local.y * s, -local.x * s + local.y * c);
    }

    /// <summary>A world XZ offset from a host item's center, expressed in the item's own frame.</summary>
    public static Vector2 WorldToLocal(Vector2 world, float yawDeg)
    {
        float r = yawDeg * Mathf.Deg2Rad;
        float c = Mathf.Cos(r), s = Mathf.Sin(r);
        return new Vector2(world.x * c - world.y * s, world.x * s + world.y * c);
    }

    // ---------------------------------------------------------------------------------------
    // Labels: every one of these is read aloud in a meeting, so they say what a person would say
    // ---------------------------------------------------------------------------------------

    /// <summary>"Front door", "Bedroom 2 door", "Bathroom window".</summary>
    public static string OpeningLabel(OpeningDef opening, LevelDef level)
    {
        if (opening == null) return "a doorway";

        string kind = opening.kind == OpeningKind.Window ? "window" : "door";

        var wall = Find(level?.walls, w => w.id, opening.wallId);
        if (wall != null)
        {
            var room = HomeMetrics.RoomAt(HomeMetrics.PointOnWall(wall, opening.offset), level);
            if (room != null) return $"{RoomName(room)} {kind}";
        }
        return char.ToUpperInvariant(kind[0]) + kind.Substring(1);
    }

    /// <summary>"Bed in Bedroom 2", falling back to the catalog name alone.</summary>
    public static string ItemLabel(ObjectInstance item, LevelDef level)
    {
        if (item == null) return "an item";

        // The furniture catalog's display names live in a ScriptableObject this assembly cannot
        // reach, and SampleFurniture carries dimensions rather than names, so the key is prettified.
        // "twin_bed" -> "Twin bed" is what a person would call it anyway.
        string name = Prettify(item.prefabType);

        bool placed = item.position != null && item.position.Length >= 3;
        var room = placed
            ? HomeMetrics.RoomAt(new Vector2(item.position[0], item.position[2]), level)
            : null;

        return room != null ? $"{name} in {RoomName(room)}" : name;
    }

    public static string RoomName(RoomDef room)
        => room == null ? "the home" : string.IsNullOrEmpty(room.name) ? "a room" : room.name;

    private static string Prettify(string key)
    {
        if (string.IsNullOrEmpty(key)) return "item";
        string s = key.Replace('_', ' ');
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>First element whose key matches, or null. The lookup every file here open-codes.</summary>
    public static T Find<T>(IReadOnlyList<T> list, System.Func<T, string> key, string id) where T : class
    {
        if (list == null || string.IsNullOrEmpty(id)) return null;
        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            if (item != null && key(item) == id) return item;
        }
        return null;
    }
}
