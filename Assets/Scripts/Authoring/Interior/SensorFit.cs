using System.Collections.Generic;
using UnityEngine;

// Decides which element a sensing device installs on, from a point in the plan.
//
// The same contract OpeningFit and FurnitureFit follow, for the same reason: this tool is used by care
// staff and family members, so a click has to land somewhere sensible rather than be swallowed, and
// where it cannot, the refusal has to be a sentence someone can act on. "A stove sensor needs a stove
//. There is no range in this room" beats a click that does nothing.
//
// WHAT MAKES THIS DIFFERENT FROM THE OTHER TWO FITS: those answer "where", clamping a coordinate.
// This answers "on what". A door sensor is not near a door, it is ON a particular OpeningDef, and the
// difference is what lets the simulation ask "did anyone go through the door this sensor is on" and
// lets a proposal that widens that doorway carry its sensor with it. So the primary output is a host
// kind plus a host id, and a coordinate only appears for the two devices that genuinely have one.
//
// The search is nearest-first within a generous reach, exactly as ResidenceMetrics.NearestWall is
// deliberately generous: a click in a bathroom that finds the only door in it is right even when the
// cursor was two metres away, because there was never another candidate.
public static class SensorFit
{
    /// <summary>
    /// How far from the click a host may be and still win, meters. Generous on purpose. See the file
    /// header. The nearest candidate wins regardless, so a wide reach only decides WHICH host when
    /// there are two, and a room-scoped search is bounded by the room anyway.
    /// </summary>
    public const float HostReach = 3.0f;

    /// <summary>Tighter, for the two device families where the wrong host is a real mistake: a pad
    /// under the wrong bed, or a stove sensor on the neighboring cabinet.</summary>
    public const float ItemReach = 1.5f;

    public struct Result
    {
        public bool ok;             // false => nothing legal here; `reason` says what to do about it
        public string hostKind;     // SensorHost.*
        public string hostId;
        public Vector2 position;    // where it lands in the plan: the ghost, and Room/Point storage
        public Vector2? surfaceOffset; // Furniture hosts: the same point in the HOST'S own unrotated
                                       // frame: what the instance stores, so it rides the item
        public float hostOffset;    // Wall hosts only. Meters along a -> b
        public int hostSide;        // Wall and Opening hosts. WallSide.*, the face the click came from
        public float facingYaw;     // degrees RELATIVE to the host's own facing: what the instance
                                    // stores; SensorPose adds the host's base yaw back on
        public float coneYaw;       // degrees, world: where the cone will actually point; the ghost's
        public bool moved;          // true => this landed somewhere other than the click
        public string reason;       // shown verbatim; null when the click was already right
    }

    /// <summary>
    /// Resolves a host for <paramref name="deviceType"/> at <paramref name="at"/>.
    /// </summary>
    /// <param name="occupantId">The resident a worn or personal item belongs to, and the ONLY thing
    /// that decides between this fit's two answers for one. Named, and it hangs off that person with
    /// no place in the plan; left null, and it is put down on a surface near the click. Ignored for
    /// everything else, whose host a click already chooses.</param>
    public static Result Fit(string deviceType, Vector2 at, LevelDef level,
                             VariantDef variant = null, string occupantId = null)
    {
        if (level == null) return Fail("There is no floor plan to install this on yet.");
        if (!SensorDevices.TryGet(deviceType, out var device))
            return Fail("Unknown device.");

        switch (device.hostKind)
        {
            case SensorHost.Opening: return OnOpening(device, at, level);
            case SensorHost.Furniture: return OnFurniture(device, at, level);
            case SensorHost.Wall: return OnWall(device, at, level);
            case SensorHost.Room: return InRoom(device, at, level);
            case SensorHost.Point: return AtPoint(device, at, level);
            case SensorHost.Occupant: return Personal(device, at, level, variant, occupantId);
            default: return Fail("This device has nowhere to install.");
        }
    }

    /// <summary>
    /// True when this exact device is already installed on this exact host. The duplicate guard,
    /// two door sensors on one door is not a redundancy, it is one the user forgot they placed, and
    /// it doubles every alert that door raises.
    /// </summary>
    public static bool AlreadyInstalled(LevelDef level, string deviceType, string hostKind, string hostId)
    {
        if (level?.sensors == null || string.IsNullOrEmpty(hostId)) return false;
        foreach (var s in level.sensors)
            if (s != null && s.included && s.deviceType == deviceType
                && s.hostKind == hostKind && s.hostId == hostId) return true;
        return false;
    }

    // ---------------------------------------------------------------------------------------

    private static Result OnOpening(SensorDevices.Device device, Vector2 at, LevelDef level)
    {
        // A doorbell and a lock belong on a door, not a window; a door sensor is a contact sensor and
        // the report puts them on doors AND windows (§3.1.2: "door/window contact sensors"). So the
        // filter is per device rather than blanket.
        bool windowsAllowed = device.id == "door_sensor";

        OpeningDef best = null;
        WallDef bestWall = null;
        float bestDist = float.MaxValue;
        float nearestRefusedWindow = float.MaxValue;

        if (level.openings != null)
            foreach (var o in level.openings)
            {
                if (o == null) continue;

                var wall = SensorPose.Find(level.walls, w => w.id, o.wallId);
                if (wall == null) continue;

                bool isWindow = o.kind == OpeningKind.Window;
                float d = Vector2.Distance(at, ResidenceMetrics.PointOnWall(wall, o.offset));
                if (d > HostReach) continue;

                if (isWindow && !windowsAllowed)
                {
                    nearestRefusedWindow = Mathf.Min(nearestRefusedWindow, d);
                    continue;
                }
                if (d >= bestDist) continue;

                bestDist = d;
                best = o;
                bestWall = wall;
            }

        // Clicked ON a window, with a door merely somewhere nearby: say why rather than silently
        // installing two metres away. The generous reach is there to pick BETWEEN candidates, not to
        // turn an aimed click into a different answer, and a doorbell that lands on the back door
        // when someone pointed at the front window is the kind of surprise that erodes trust in every
        // other snap in the app.
        if (nearestRefusedWindow < bestDist)
            return Fail($"A {device.displayName.ToLowerInvariant()} goes on a door.");

        if (best == null)
            return Fail(nearestRefusedWindow < float.MaxValue
                ? $"A {device.displayName.ToLowerInvariant()} goes on a door."
                : "Click on a doorway to install this.");

        var frame = WallMeshBuilder.BuildFrame(bestWall, level);
        Vector2 on = ResidenceMetrics.PointOnWall(bestWall, best.offset);

        // Install on whichever face of the wall the click came from, so the box sits proud of that
        // face and a doorbell's cone covers the approach the user was pointing at rather than the
        // room behind it. The side is the stored fact; the yaw delta stays zero.
        Vector3 facing = SideFacing(frame, at, on);
        int side = Vector3.Dot(facing, frame.left) >= 0f ? WallSide.Left : WallSide.Right;

        return new Result
        {
            ok = true,
            hostKind = SensorHost.Opening,
            hostId = best.id,
            position = on,
            hostSide = side,
            facingYaw = 0f,
            coneYaw = SensorPose.YawOf(facing),
            moved = Vector2.Distance(at, on) > 0.05f,
            reason = Moved(at, on, $"Installed on the {SensorPose.OpeningLabel(best, level).ToLowerInvariant()}."),
        };
    }

    private static Result OnFurniture(SensorDevices.Device device, Vector2 at, LevelDef level)
    {
        var wanted = HostItemsFor(device.id);

        ObjectInstance best = null;
        float bestDist = float.MaxValue;
        bool sawSomething = false;

        if (level.furniture != null)
            foreach (var item in level.furniture)
            {
                if (item == null || !item.included || item.position == null || item.position.Length < 3) continue;

                var here = new Vector2(item.position[0], item.position[2]);
                // Distance to the item's FOOTPRINT, not its center: a click on the foot of a 2.03 m
                // bed is a click on the bed, and a center test would rank a nightstand beside it higher.
                float d = ResidenceMetrics.PointRectDistance(at, ResidenceMetrics.FootprintOf(item));
                if (d > ItemReach) continue;

                sawSomething = true;
                if (wanted != null && !wanted.Contains(item.prefabType)) continue;
                if (d >= bestDist) continue;

                bestDist = d;
                best = item;
            }

        // A device that sits on a surface is wanted even when the click is out over the room's
        // middle: fall back to the nearest surface in the clicked room, so "put it in the kitchen"
        // lands on the kitchen counter rather than refusing.
        if (best == null && ReferenceEquals(wanted, Surfaces))
            best = NearestInRoom(level, Surfaces, at);

        if (best == null)
            return Fail(RefusalFor(device, sawSomething));

        return OnItemTop(device, at, best, level);
    }

    /// <summary>
    /// A placement on the top face of <paramref name="item"/>: the click clamped into the footprint
    /// and inset by the device's own half-width, stored in the item's own frame so the device rides
    /// the item when it is moved or turned. SensorPose puts it at the item's top.
    /// </summary>
    private static Result OnItemTop(SensorDevices.Device device, Vector2 at, ObjectInstance item,
                                    LevelDef level)
    {
        var rect = ResidenceMetrics.FootprintOf(item);
        Vector2 on = ClampInto(rect, at, 0.5f * Mathf.Max(device.width, device.depth));
        var center = new Vector2(item.position[0], item.position[2]);

        return new Result
        {
            ok = true,
            hostKind = SensorHost.Furniture,
            hostId = item.instanceId,
            position = on,
            surfaceOffset = SensorPose.WorldToLocal(on - center, item.rotationY),
            facingYaw = 0f,
            coneYaw = item.rotationY,
            moved = Vector2.Distance(at, on) > 0.05f,
            reason = Moved(at, on, $"Placed on the {SensorPose.ItemLabel(item, level).ToLowerInvariant()}."),
        };
    }

    private static Result OnWall(SensorDevices.Device device, Vector2 at, LevelDef level)
    {
        var wall = ResidenceMetrics.NearestWall(at, level.walls, ResidenceConventions.MOUNT_REACH,
                                           out float offset, out int side);
        if (wall == null) return Fail("Move closer to a wall to install this.");

        Vector2 on = ResidenceMetrics.PointOnWall(wall, offset);
        var frame = WallMeshBuilder.BuildFrame(wall, level);
        Vector3 outward = side == WallSide.Left ? frame.left : -frame.left;

        return new Result
        {
            ok = true,
            hostKind = SensorHost.Wall,
            hostId = wall.id,
            position = on,
            hostOffset = offset,
            hostSide = side,
            facingYaw = 0f,
            coneYaw = SensorPose.YawOf(outward),
            moved = false,
            reason = null,
        };
    }

    private static Result InRoom(SensorDevices.Device device, Vector2 at, LevelDef level)
    {
        var room = ResidenceMetrics.RoomAt(at, level);
        if (room == null) return Fail("Click inside a room to install this.");

        // A device with a cone gets pushed off the wall it was clicked against and turned to face into
        // the room. A motion sensor mounted in a corner covers the room; the same sensor left facing
        // the wall it is on covers a wall. The report's own guidance is "mounted on walls, ceilings,
        // or corners... to optimize coverage" (§4.3.1), and nothing downstream would complain if it
        // pointed the wrong way: the cone would simply be drawn into the plaster.
        Vector2 inward = InwardFrom(at, room);
        float yaw = device.coverageAngle < 360f ? SensorPose.YawOf(new Vector3(inward.x, 0f, inward.y)) : 0f;

        return new Result
        {
            ok = true,
            hostKind = SensorHost.Room,
            hostId = room.id,
            position = at,
            facingYaw = yaw,
            coneYaw = yaw,
            moved = false,
            reason = null,
        };
    }

    private static Result AtPoint(SensorDevices.Device device, Vector2 at, LevelDef level)
    {
        var room = ResidenceMetrics.RoomAt(at, level);
        if (room == null) return Fail("Click inside a room to put this on the floor.");

        // Water pools at the fixture, so a click near one snaps to its foot. The report puts these
        // "on the floor near potential leak sources like sinks, toilets, washing machines" (§4.4.3),
        // and a sensor a metre from the basin is a sensor that finds out late.
        Vector2 snapped = at;
        string moved = null;

        var fixtureItem = NearestOf(level, PlumbingItems, at, ItemReach, out float d);
        if (fixtureItem != null && d > 0.05f)
        {
            var rect = ResidenceMetrics.FootprintOf(fixtureItem);
            snapped = ClosestOn(rect, at, 0.12f);
            moved = $"Moved to the foot of the {SensorPose.ItemLabel(fixtureItem, level).ToLowerInvariant()}.";
        }

        return new Result
        {
            ok = true,
            hostKind = SensorHost.Point,
            hostId = room.id,     // the room it is in, so the console and the report can name it
            position = snapped,
            moved = moved != null,
            reason = moved,
        };
    }

    // A pendant, a sock aid and a key turner belong to a PERSON, and while the roster was the only
    // route to one, this whole half of the catalog was gated behind the People tab. That is a hard
    // stop in a tool used to lay a residence out BEFORE deciding who moves into it, and it refused the one
    // gesture every other entry in the grid answers: click, and something appears in the plan.
    //
    // So there are two answers, and the caller picks between them purely by whether it names a
    // resident. NAMED, and the device hangs off that person exactly as it always has: no pose, shown
    // against them in the console, carried along by a proposal that rehouses them. UNNAMED, and it is
    // simply KEPT somewhere: on the nearest counter, table or nightstand, which is what a sock aid in
    // a bedside drawer or a pendant on its charger actually is.
    //
    // Nothing downstream needed a line for the second answer, which is the whole reason it is cheap:
    // Furniture is the stove sensor's host and the dispenser's, so ResidenceRenderer draws it, the delete
    // cascade removes it with its host, VariantRevert restores it and VariantDiff reports it. And no
    // figure this app prints about what a residence can SEE moves either, because every one of these items
    // has a zero envelope and no rules: the two properties SensorCoverageTests and SensorSimTests
    // already pin.
    private static Result Personal(SensorDevices.Device device, Vector2 at, LevelDef level,
                                   VariantDef variant, string occupantId)
    {
        if (!string.IsNullOrEmpty(occupantId))
        {
            var person = SensorPose.Find(variant?.occupants, o => o.id, occupantId);

            // A detour rather than a dead end, on VariantRevert's rule: the resident this was aimed at
            // has left the household, and putting it down in a room is still available.
            if (person == null)
                return Fail("That resident is not in this residence. Pick another, or leave it unassigned "
                          + "and click a room to put it down there.");

            return new Result
            {
                ok = true,
                hostKind = SensorHost.Occupant,
                hostId = person.id,
                reason = null,
            };
        }

        // Unassigned: put it down on a surface near the click (a counter, a table, a nightstand) 
        // which is what a sock aid in a bedside drawer or a pendant on its charger actually is. It
        // used to float at a fixed counter height in mid-room, which read as a rendering bug.
        var surface = NearestOf(level, Surfaces, at, ItemReach, out _)
                   ?? NearestInRoom(level, Surfaces, at);
        if (surface == null)
            return Fail("Click near a counter, a table or a cabinet to put this down, or choose "
                      + "the resident it belongs to.");

        return OnItemTop(device, at, surface, level);
    }

    // ---------------------------------------------------------------------------------------
    // Which items host which device
    // ---------------------------------------------------------------------------------------

    // Catalog ids, the key space shared by FurnitureCatalog, PrefabRegistry and SampleFurniture, so
    // these work on a residence the user drew, not only on the shipped six.
    private static readonly HashSet<string> BedsAndSeats = new HashSet<string>
    {
        "twin_bed", "full_bed", "hospital_bed", "recliner", "armchair", "sofa",
    };

    private static readonly HashSet<string> Cooktops = new HashSet<string> { "range" };

    private static readonly HashSet<string> PlumbingItems = new HashSet<string>
    {
        "toilet", "sink_pedestal", "vanity", "bathtub", "roll_in_shower", "sink_base",
    };

    /// <summary>
    /// Items with a usable top face: where a dispenser, a monitor or an unassigned personal item is
    /// put down. base_cabinet at 0.91 is the counter the report specifies for the dispenser (§4.2.2).
    /// </summary>
    private static readonly HashSet<string> Surfaces = new HashSet<string>
    {
        "base_cabinet", "sink_base", "island", "dining_table", "coffee_table",
        "nightstand", "dresser", "tv_stand",
    };

    private static HashSet<string> HostItemsFor(string deviceType) => deviceType switch
    {
        "bed_chair_pad" => BedsAndSeats,
        "stove_sensor" => Cooktops,
        "med_dispenser" => Surfaces,
        "large_print_measures" => Surfaces,
        "voice_prompt_speaker" => Surfaces,
        "air_quality_monitor" => Surfaces,
        _ => null,      // no constraint: any item will do
    };

    private static string RefusalFor(SensorDevices.Device device, bool sawSomething)
    {
        if (ReferenceEquals(HostItemsFor(device.id), Surfaces))
            return "This sits on a counter, a table or a cabinet. There is no surface like that "
                 + "near here.";

        if (device.id == "stove_sensor")
            return sawSomething
                ? "A stove sensor goes on a range. This is not one."
                : "A stove sensor goes on a range. This residence has none.";

        if (device.id == "bed_chair_pad")
            return sawSomething
                ? "A pad goes under a bed or a chair. This is neither."
                : "A pad goes under a bed or a chair. This residence has neither.";

        return "Click on the piece of furniture this installs on.";
    }

    // ---------------------------------------------------------------------------------------

    private static ObjectInstance NearestOf(LevelDef level, HashSet<string> wanted, Vector2 at,
                                            float reach, out float distance)
    {
        ObjectInstance best = null;
        distance = float.MaxValue;

        if (level.furniture == null) return null;
        foreach (var item in level.furniture)
        {
            if (item == null || !item.included || !wanted.Contains(item.prefabType)) continue;
            if (item.position == null || item.position.Length < 3) continue;

            float d = ResidenceMetrics.PointRectDistance(at, ResidenceMetrics.FootprintOf(item));
            if (d > reach || d >= distance) continue;

            distance = d;
            best = item;
        }
        return best;
    }

    /// <summary>
    /// The nearest item of the wanted kinds in the room the click landed in, at any distance. The
    /// room-scoped counterpart of <see cref="NearestOf"/>, for the surfaces a device is put ON.
    /// </summary>
    private static ObjectInstance NearestInRoom(LevelDef level, HashSet<string> wanted, Vector2 at)
    {
        var room = ResidenceMetrics.RoomAt(at, level);
        if (room == null || level.furniture == null) return null;

        ObjectInstance best = null;
        float bestDist = float.MaxValue;
        foreach (var item in level.furniture)
        {
            if (item == null || !item.included || !wanted.Contains(item.prefabType)) continue;
            if (item.position == null || item.position.Length < 3) continue;
            if (ResidenceMetrics.RoomAt(new Vector2(item.position[0], item.position[2]), level) != room)
                continue;

            float d = ResidenceMetrics.PointRectDistance(at, ResidenceMetrics.FootprintOf(item));
            if (d >= bestDist) continue;
            bestDist = d;
            best = item;
        }
        return best;
    }

    /// <summary>
    /// The point inside a rect nearest <paramref name="at"/>, inset by <paramref name="inset"/> so a
    /// box of that half-width sits wholly on the surface. An inset wider than the rect collapses to
    /// its center line.
    /// </summary>
    private static Vector2 ClampInto(Rect rect, Vector2 at, float inset)
    {
        float ix = Mathf.Min(inset, 0.5f * rect.width);
        float iy = Mathf.Min(inset, 0.5f * rect.height);
        return new Vector2(Mathf.Clamp(at.x, rect.xMin + ix, rect.xMax - ix),
                           Mathf.Clamp(at.y, rect.yMin + iy, rect.yMax - iy));
    }

    /// <summary>The point <paramref name="margin"/> outside a rect, nearest to <paramref name="at"/>.</summary>
    private static Vector2 ClosestOn(Rect rect, Vector2 at, float margin)
    {
        Vector2 clamped = new Vector2(Mathf.Clamp(at.x, rect.xMin, rect.xMax),
                                      Mathf.Clamp(at.y, rect.yMin, rect.yMax));
        Vector2 away = at - clamped;
        if (away.sqrMagnitude < 1e-6f)
        {
            // Inside the footprint: push out through the nearest edge rather than picking a direction.
            float left = at.x - rect.xMin, right = rect.xMax - at.x;
            float down = at.y - rect.yMin, up = rect.yMax - at.y;
            float min = Mathf.Min(Mathf.Min(left, right), Mathf.Min(down, up));
            if (Mathf.Approximately(min, left))  return new Vector2(rect.xMin - margin, at.y);
            if (Mathf.Approximately(min, right)) return new Vector2(rect.xMax + margin, at.y);
            if (Mathf.Approximately(min, down))  return new Vector2(at.x, rect.yMin - margin);
            return new Vector2(at.x, rect.yMax + margin);
        }
        return clamped + away.normalized * margin;
    }

    /// <summary>
    /// Which face of a wall the click came from, as a world direction. Used so a device installed on
    /// an opening faces the side it was placed from.
    /// </summary>
    private static Vector3 SideFacing(WallMeshBuilder.Frame frame, Vector2 at, Vector2 on)
    {
        var toClick = new Vector3(at.x - on.x, 0f, at.y - on.y);
        return Vector3.Dot(toClick, frame.left) >= 0f ? frame.left : -frame.left;
    }

    /// <summary>
    /// A direction pointing into the room from <paramref name="at"/>. Away from the nearest wall.
    /// The room's inscribed center is the point furthest from any boundary, so aiming at it is the
    /// cheapest correct answer and needs no wall lookup at all.
    /// </summary>
    private static Vector2 InwardFrom(Vector2 at, RoomDef room)
    {
        Vector2 center = ResidenceMetrics.LargestInscribedCircle(room).center;
        Vector2 d = center - at;
        return d.sqrMagnitude < 1e-4f ? Vector2.up : d.normalized;
    }

    private static string Moved(Vector2 from, Vector2 to, string reason)
        => Vector2.Distance(from, to) > 0.05f ? reason : null;

    private static Result Fail(string reason) => new Result { ok = false, reason = reason };
}
