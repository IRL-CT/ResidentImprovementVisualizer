using System.Collections.Generic;
using UnityEngine;

/// <summary>Which side of a room rectangle a thing sits on. South is -Z, North is +Z.</summary>
public enum PlanEdge { South, East, North, West }

// Turns a set of axis-aligned room rectangles into a valid LevelDef.
//
// WHY THIS EXISTS: nothing downstream of the schema throws on bad geometry. WallLayout silently
// CLAMPS an opening that hangs off its wall, WallMeshBuilder leaves a ~57 mm notch wherever two wall
// endpoints miss each other by more than 1 mm, and HomeRenderer skips an opening whose wallId does
// not resolve. Hand-authoring several floor plans as coordinate literals would therefore produce
// plans that are quietly wrong in ways nobody notices. So the sample plans are authored as ROOMS and
// everything error-prone is derived:
//
//   * Walls come from the room rectangles, then get de-duplicated and split (see BuildWalls). Two
//     rooms sharing an edge yield ONE wall; every T-junction and crossing is split so all endpoints
//     coincide exactly, which is what WallMeshBuilder.ComputeExtensions needs to weld the corner.
//   * Openings are placed by RELATIONSHIP ("a door between the hall and bedroom 1"), not by wall id
//     and offset. The host wall is resolved after splitting, so the splitting stays invisible to the
//     author, and every result is checked with OpeningFit.IsValid.
//   * Furniture is placed against a named edge, so the flush position and the yaw that faces into the
//     room are computed rather than typed.
//
// Anything that could not be resolved lands in Warnings instead of throwing: the sample tests assert
// that list is empty, which is what turns a silent geometry bug into a failing test.
public sealed class PlanBuilder
{
    // Coordinates are quantised to this, comfortably inside WallMeshBuilder.Near's 1 mm weld radius.
    private const float GRID = 0.001f;
    // Shared with Spans, which owns the run union/split this builder's wall derivation is built on.
    // Tied to that constant rather than repeated, so the two can never drift apart.
    private const float TOL  = Spans.TOL;


    private readonly float _ceilingHeight;
    private readonly float _wallThickness;

    private readonly List<RoomRect> _rooms = new List<RoomRect>();
    private readonly Dictionary<string, RoomRect> _byKey = new Dictionary<string, RoomRect>();
    private readonly List<PendingOpening> _openings = new List<PendingOpening>();
    private readonly List<PendingItem> _items = new List<PendingItem>();
    private readonly List<PendingMount> _mounts = new List<PendingMount>();
    private readonly List<PendingPerson> _people = new List<PendingPerson>();
    private readonly Dictionary<string, PendingPerson> _peopleByKey = new Dictionary<string, PendingPerson>();
    private readonly List<string> _warnings = new List<string>();

    public PlanBuilder(float ceilingHeight = 0f, float wallThickness = 0f)
    {
        _ceilingHeight = ceilingHeight > 0f ? ceilingHeight : HomeConventions.DEFAULT_CEILING_HEIGHT;
        _wallThickness = wallThickness > 0f ? wallThickness : HomeConventions.DEFAULT_WALL_THICKNESS;
    }

    public IReadOnlyList<string> Warnings => _warnings;

    // -------------------------------------------------------------------------------------------
    // Authoring surface
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A room as an axis-aligned rectangle on WALL CENTERLINES: (x, z) is the min corner, (w, d) the
    /// size. Centerlines rather than finished faces is the project's convention (see RoomTool), so
    /// reported areas match what the Select tool shows.
    /// </summary>
    public PlanBuilder Room(string key, string name, string roomType, float x, float z, float w, float d)
    {
        if (string.IsNullOrEmpty(key)) { Warn("A room was declared with no key."); return this; }
        if (_byKey.ContainsKey(key)) { Warn($"Duplicate room key '{key}'."); return this; }
        if (w <= TOL || d <= TOL) { Warn($"Room '{key}' has no area."); return this; }

        var r = new RoomRect
        {
            key = key, name = name, roomType = roomType, roomKey = key,
            x0 = Q(x), z0 = Q(z), x1 = Q(x + w), z1 = Q(z + d),
        };
        _rooms.Add(r);
        _byKey[key] = r;
        return this;
    }

    /// <summary>
    /// Another rectangle of a room already declared with <see cref="Room"/>: the leg of an L, the
    /// alcove off a living room, the return past a chimney breast.
    ///
    /// WHY THIS IS NOT JUST TWO ROOMS: every rectangle contributes its four centerline edges to the
    /// wall derivation, so two rectangles that merely share a name get a full-height wall built along
    /// the edge between them. That wall is invisible in the authoring surface and completely silent
    /// downstream (it renders, it encloses, RoomRegions finds a face either side of it) so an
    /// L-shaped room described as two rooms is a room bisected by a wall that is not in the drawing.
    /// Declaring the second rectangle as a PART instead suppresses that one wall and emits a single
    /// RoomDef whose polygon is the union of the two, which is what the plan actually shows.
    ///
    /// The part keeps its own key and is addressed by it: <c>Against(partKey, edge, …)</c> puts a sofa
    /// along the alcove's own wall, which is the only way to say that at all. Its name and type are
    /// the parent's, because they are the room's rather than the rectangle's.
    ///
    /// Parts must not overlap each other or anything else, exactly as rooms must not.
    /// </summary>
    public PlanBuilder RoomPart(string key, string partOf, float x, float z, float w, float d)
    {
        if (string.IsNullOrEmpty(key)) { Warn("A room part was declared with no key."); return this; }
        if (_byKey.ContainsKey(key)) { Warn($"Duplicate room key '{key}'."); return this; }
        if (w <= TOL || d <= TOL) { Warn($"Room part '{key}' has no area."); return this; }

        if (!_byKey.TryGetValue(partOf ?? "", out var parent))
        {
            Warn($"Room part '{key}' belongs to unknown room '{partOf}'.");
            return this;
        }

        // One level only. A part of a part would still resolve to the right room through the parent's
        // own roomKey, but allowing it would make the declaration order load-bearing in a way nothing
        // checks, and no plan needs it.
        if (parent.roomKey != parent.key)
        {
            Warn($"Room part '{key}' belongs to '{partOf}', which is itself a part of "
               + $"'{parent.roomKey}'. Name the room itself.");
            return this;
        }

        var r = new RoomRect
        {
            key = key, name = parent.name, roomType = parent.roomType, roomKey = parent.key,
            x0 = Q(x), z0 = Q(z), x1 = Q(x + w), z1 = Q(z + d),
        };
        _rooms.Add(r);
        _byKey[key] = r;
        return this;
    }

    /// <summary>
    /// An opening on the wall two rooms share. The shared edge is found automatically and the opening
    /// is centered in the overlap (or placed at <paramref name="alongFraction"/> of it).
    /// </summary>
    public PlanBuilder DoorBetween(string roomA, string roomB, float width,
                                   string kind = OpeningKind.Door,
                                   float threshold = 0f, float alongFraction = 0.5f, float height = 0f)
    {
        if (!Lookup(roomA, out var a) || !Lookup(roomB, out var b)) return this;

        if (!SharedEdge(a, b, out bool vertical, out float coord, out float lo, out float hi))
        {
            Warn($"'{roomA}' and '{roomB}' do not share an edge, so no door was placed.");
            return this;
        }

        _openings.Add(new PendingOpening
        {
            label = $"{roomA}<->{roomB}",
            vertical = vertical,
            coord = coord,
            along = Mathf.Lerp(lo, hi, Mathf.Clamp01(alongFraction)),
            width = width > 0f ? width : HomeConventions.DEFAULT_DOOR_WIDTH,
            height = height > 0f ? height : HomeConventions.DEFAULT_DOOR_HEIGHT,
            sill = 0f,
            threshold = threshold,
            kind = kind,
        });
        return this;
    }

    /// <summary>A door in a room's exterior wall. Thresholds default to 0. Step-free.</summary>
    public PlanBuilder ExteriorDoor(string room, PlanEdge edge, float alongFraction, float width = 0f,
                                    float threshold = 0f)
    {
        if (!Lookup(room, out var r)) return this;
        EdgeLine(r, edge, out bool vertical, out float coord, out float lo, out float hi);

        _openings.Add(new PendingOpening
        {
            label = $"{room} entry",
            vertical = vertical,
            coord = coord,
            along = Mathf.Lerp(lo, hi, Mathf.Clamp01(alongFraction)),
            width = width > 0f ? width : HomeConventions.DEFAULT_WINDOW_WIDTH,
            height = HomeConventions.DEFAULT_DOOR_HEIGHT,
            sill = 0f,
            threshold = threshold,
            kind = OpeningKind.Door,
        });
        return this;
    }

    /// <summary>A window in a room's wall. Zero arguments fall back to the HomeConventions defaults.</summary>
    public PlanBuilder Window(string room, PlanEdge edge, float alongFraction,
                              float width = 0f, float height = 0f, float sill = 0f)
    {
        if (!Lookup(room, out var r)) return this;
        EdgeLine(r, edge, out bool vertical, out float coord, out float lo, out float hi);

        _openings.Add(new PendingOpening
        {
            label = $"{room} window",
            vertical = vertical,
            coord = coord,
            along = Mathf.Lerp(lo, hi, Mathf.Clamp01(alongFraction)),
            width = width > 0f ? width : HomeConventions.DEFAULT_WINDOW_WIDTH,
            height = height > 0f ? height : HomeConventions.DEFAULT_WINDOW_HEIGHT,
            sill = sill > 0f ? sill : HomeConventions.DEFAULT_WINDOW_SILL,
            threshold = 0f,
            kind = OpeningKind.Window,
        });
        return this;
    }

    /// <summary>
    /// Stands an item flush against one of the room's walls, facing into the room. The standoff is
    /// measured from the wall's inner FACE, so <paramref name="inset"/> is the visible gap.
    /// </summary>
    /// <param name="alongWall">Turns the item a quarter turn so its DEPTH runs along the wall instead
    /// of away from it. Baths and showers need this: the catalog models them 0.76 x 1.52 (narrow
    /// front, deep), which is the orientation of a fixture you approach head-on, whereas both are
    /// actually installed as an alcove. Long side against the wall. It is also the only way either
    /// fits a 1.8 m wide bathroom.</param>
    public PlanBuilder Against(string prefabType, string room, PlanEdge edge, float alongFraction,
                               float inset = 0.02f, bool alongWall = false)
    {
        if (!Lookup(room, out var r)) return this;

        var item = Resolve(prefabType, room);
        float yaw = YawFacingInto(edge);
        if (alongWall) yaw = Mathf.Repeat(yaw + 90f, 360f);
        Vector2 fp = SampleFurniture.FootprintXZ(item, yaw);
        float standoff = 0.5f * _wallThickness + Mathf.Max(0f, inset);

        float x, z;
        bool wallVertical = edge == PlanEdge.West || edge == PlanEdge.East;
        float wallCoord, alongLo, alongHi, alongSize;

        switch (edge)
        {
            case PlanEdge.South:
                z = r.z0 + standoff + 0.5f * fp.y;
                x = SlideAlong(r.x0, r.x1, alongFraction, fp.x, prefabType, room);
                wallCoord = r.z0; alongLo = r.x0; alongHi = r.x1; alongSize = fp.x;
                break;
            case PlanEdge.North:
                z = r.z1 - standoff - 0.5f * fp.y;
                x = SlideAlong(r.x0, r.x1, alongFraction, fp.x, prefabType, room);
                wallCoord = r.z1; alongLo = r.x0; alongHi = r.x1; alongSize = fp.x;
                break;
            case PlanEdge.West:
                x = r.x0 + standoff + 0.5f * fp.x;
                z = SlideAlong(r.z0, r.z1, alongFraction, fp.y, prefabType, room);
                wallCoord = r.x0; alongLo = r.z0; alongHi = r.z1; alongSize = fp.y;
                break;
            default:
                x = r.x1 - standoff - 0.5f * fp.x;
                z = SlideAlong(r.z0, r.z1, alongFraction, fp.y, prefabType, room);
                wallCoord = r.x1; alongLo = r.z0; alongHi = r.z1; alongSize = fp.y;
                break;
        }

        _items.Add(new PendingItem
        {
            prefabType = prefabType, item = item, x = x, z = z, yaw = yaw,
            againstWall = true, wallVertical = wallVertical, wallCoord = wallCoord,
            alongLo = alongLo, alongHi = alongHi, alongSize = alongSize,
            room = room, roomGroup = r.roomKey,
            crossSize = wallVertical ? fp.x : fp.y,
        });
        return this;
    }

    /// <summary>Places an item away from the walls, at a fraction across the room in each axis.</summary>
    public PlanBuilder Free(string prefabType, string room, float xFrac, float zFrac, float yaw = 0f)
    {
        if (!Lookup(room, out var r)) return this;

        var item = Resolve(prefabType, room);
        Vector2 fp = SampleFurniture.FootprintXZ(item, yaw);
        float standoff = 0.5f * _wallThickness;

        float x = SlideAlong(r.x0 + standoff, r.x1 - standoff, xFrac, fp.x, prefabType, room);
        float z = SlideAlong(r.z0 + standoff, r.z1 - standoff, zFrac, fp.y, prefabType, room);

        // `room` is carried so a free-standing item still counts as an obstacle for anything placed
        // against a wall afterwards: an island is exactly what a base cabinet must not slide into.
        _items.Add(new PendingItem
        {
            prefabType = prefabType, item = item, x = x, z = z, yaw = yaw,
            room = room, roomGroup = r.roomKey,
        });
        return this;
    }

    /// <summary>
    /// Hangs an item on one of the room's walls. The host wall, the offset along it, and which of its
    /// two faces to use are all derived: the face chosen is the one looking into this room.
    /// </summary>
    public PlanBuilder Mount(string prefabType, string room, PlanEdge edge, float alongFraction,
                             float mountHeight = 0f)
    {
        if (!Lookup(room, out var r)) return this;

        var item = Resolve(prefabType, room);
        if (!item.wallMounted)
            Warn($"'{prefabType}' was mounted in '{room}', but it is not a wall-mounted catalog item.");

        EdgeLine(r, edge, out bool vertical, out float coord, out float lo, out float hi);

        _mounts.Add(new PendingMount
        {
            prefabType = prefabType,
            item = item,
            vertical = vertical,
            coord = coord,
            along = Mathf.Lerp(lo, hi, Mathf.Clamp01(alongFraction)),
            interior = r.Center,
            mountHeight = mountHeight > 0f ? mountHeight : item.mountHeight,
            label = room,
        });
        return this;
    }

    /// <summary>
    /// The longest run on one of a room's edges that is clear of every opening an item of this height
    /// would collide with.
    ///
    /// Openings must already have been declared: every sample places its doors and windows before its
    /// furniture, and this is the reason that ordering matters.
    /// </summary>
    public float ClearRunOn(string room, PlanEdge edge, float itemHeight)
    {
        if (!Lookup(room, out var r)) return 0f;
        EdgeLine(r, edge, out bool vertical, out float coord, out float lo, out float hi);

        var blocked = OpeningSpans(vertical, coord, 0f, itemHeight);
        blocked.Sort((p, q) => p.x.CompareTo(q.x));

        float best = 0f, cursor = lo;
        foreach (var b in blocked)
        {
            if (b.y <= lo || b.x >= hi) continue;
            best = Mathf.Max(best, Mathf.Min(b.x, hi) - cursor);
            cursor = Mathf.Max(cursor, Mathf.Min(b.y, hi));
        }
        return Mathf.Max(best, hi - cursor);
    }

    /// <summary>
    /// The first edge in <paramref name="preference"/> with a clear run long enough for the item, or the
    /// roomiest of them if none qualifies.
    ///
    /// This is what lets a recipe say "put the tub on a wall that can actually take it" rather than
    /// hard-coding a compass direction, which is how a bath ended up across the bathroom door in three
    /// of the six plans, and a dresser across a bedroom door in two more.
    /// </summary>
    public PlanEdge BestEdgeFor(string room, float itemWidth, float itemHeight, params PlanEdge[] preference)
    {
        if (preference == null || preference.Length == 0) return PlanEdge.North;

        PlanEdge best = preference[0];
        float bestRun = -1f;
        foreach (var e in preference)
        {
            float run = ClearRunOn(room, e, itemHeight);
            if (run >= itemWidth) return e;
            if (run > bestRun) { bestRun = run; best = e; }
        }
        return best;
    }

    /// <summary>
    /// Declares someone who lives here. Occupants are built separately from the level (they hang off
    /// the variant, not the story). See BuildOccupants.
    /// </summary>
    public PlanBuilder Person(string key, string name, bool usesWheelchair = false, string note = null)
    {
        if (string.IsNullOrEmpty(key)) { Warn("An occupant was declared with no key."); return this; }
        if (_peopleByKey.ContainsKey(key)) { Warn($"Duplicate occupant key '{key}'."); return this; }

        var p = new PendingPerson { key = key, name = name, wheelchair = usesWheelchair, note = note };
        _people.Add(p);
        _peopleByKey[key] = p;
        return this;
    }

    /// <summary>
    /// Adds one block to a person's day. Times are anything Clock parses ("7:30", "7:30 AM", "0730").
    /// <paramref name="room"/> is a ROOM KEY as passed to Room(), not the emitted RoomDef.id: the same
    /// convention Against() and Mount() use. Null means away from home.
    ///
    /// <paramref name="anchor"/> names a catalog prefabType to stand beside ("range", "twin_bed"); it
    /// resolves to the first item of that type inside the named room. Authors do not see the f_n ids
    /// the builder assigns, so referring to the item by what it IS is the only workable handle.
    /// </summary>
    public PlanBuilder Does(string personKey, string kind, string start, string end,
                            string room = null, string anchor = null, string label = null)
    {
        if (!_peopleByKey.TryGetValue(personKey ?? "", out var person))
        {
            Warn($"Unknown occupant '{personKey}'.");
            return this;
        }

        if (!ActivityKind.IsKnown(kind))
            Warn($"'{person.name}' was given an activity of unknown kind '{kind}'.");

        if (!Clock.TryParse(start, out int s))
            Warn($"'{person.name}' has an unreadable start time \"{start}\".");
        if (!Clock.TryParse(end, out int e))
            Warn($"'{person.name}' has an unreadable end time \"{end}\".");

        // A room key is checked here rather than at build time, so the warning names the line that is
        // wrong instead of the id it produced.
        if (room != null && !_byKey.ContainsKey(room))
            Warn($"'{person.name}' is scheduled into unknown room '{room}'.");

        person.day.Add(new PendingActivity
        {
            kind = kind, label = label, start = s, end = e, roomKey = room, anchorType = anchor,
        });
        return this;
    }

    // -------------------------------------------------------------------------------------------
    // Build
    // -------------------------------------------------------------------------------------------

    public LevelDef Build(string levelName = "Ground floor")
    {
        var level = new LevelDef
        {
            id = "level_ground",
            name = levelName,
            elevation = 0f,
            ceilingHeight = _ceilingHeight,
            wallThickness = _wallThickness,
            walls = new List<WallDef>(),
            openings = new List<OpeningDef>(),
            rooms = new List<RoomDef>(),
            furniture = new List<ObjectInstance>(),
            wallMounted = new List<WallMountDef>(),
        };

        var segs = BuildWalls();
        foreach (var s in segs) level.walls.Add(s.def);

        BuildRooms(level);
        BuildOpenings(level, segs);
        BuildFurniture(level);
        BuildMounts(level, segs);

        return level;
    }

    // Derives the wall set from the room rectangles.
    //
    // Every room contributes its four centerline edges. Those are grouped by line (vertical walls by
    // x, horizontal by z), and within each group the 1-D spans are UNIONED and then re-split at every
    // significant point on that line. Doing both in one pass is what makes the result safe:
    //
    //   * union: two rooms sharing an edge, or overlapping partially because they have different
    //                 depths, collapse into a single non-overlapping run instead of coincident walls.
    //   * re-split: a perpendicular wall that ENDS on this line (a T-junction) or CROSSES it forces
    //                 a break, so both walls own that point as an endpoint. Without this the T gets no
    //                 corner extension from WallMeshBuilder and renders as a notch.
    private List<Seg> BuildWalls()
    {
        var vertical = new Dictionary<long, List<Vector2>>();    // x -> [zLo, zHi] spans
        var horizontal = new Dictionary<long, List<Vector2>>();  // z -> [xLo, xHi] spans

        foreach (var r in _rooms)
        {
            Add(vertical, r.x0, r.z0, r.z1);
            Add(vertical, r.x1, r.z0, r.z1);
            Add(horizontal, r.z0, r.x0, r.x1);
            Add(horizontal, r.z1, r.x0, r.x1);
        }

        var segs = new List<Seg>();
        Emit(segs, vertical, horizontal, isVertical: true);
        Emit(segs, vertical, horizontal, isVertical: false);

        DropInteriorEdges(segs);

        // Stable ids: vertical runs first, then by line, then along the line. Deterministic output
        // matters because these ids are what VariantDiff matches on once a user branches a proposal.
        segs.Sort((p, q) =>
        {
            int c = p.vertical.CompareTo(q.vertical);
            if (c != 0) return -c;
            c = p.coord.CompareTo(q.coord);
            if (c != 0) return c;
            return p.lo.CompareTo(q.lo);
        });

        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            s.def = new WallDef
            {
                id = "w_" + i,
                a = s.vertical ? new[] { s.coord, s.lo } : new[] { s.lo, s.coord },
                b = s.vertical ? new[] { s.coord, s.hi } : new[] { s.hi, s.coord },
                thickness = 0f,   // inherit LevelDef.wallThickness
                height = 0f,      // inherit LevelDef.ceilingHeight
                materialLeft = "paint_white",
                materialRight = "paint_white",
            };
        }

        return segs;
    }

    /// <summary>
    /// Removes the wall pieces that run BETWEEN two rectangles of one room.
    ///
    /// Every rectangle contributes all four of its edges, which is what makes the derivation safe for
    /// rooms that merely touch, but two parts of one room touching means the drawing has no wall
    /// there. The union-then-split pass has already run at this point, and that ordering is what makes
    /// this a filter rather than a special case: a shared-edge span's endpoints are rectangle
    /// coordinates, and every rectangle coordinate on a line is a break point on that line, so every
    /// emitted piece lies either wholly inside a shared span or wholly outside one. There is nothing
    /// to cut.
    ///
    /// A shared span can never be a wall the plan needs. Both rectangles belong to the same room, one
    /// lies either side of the line, and rooms may not overlap, so no third room can reach it.
    /// </summary>
    private void DropInteriorEdges(List<Seg> segs)
    {
        // Nothing to do for a plan of whole rooms, which is every sample plan. Worth the early return
        // for what it guarantees rather than for the time: the derivation is provably untouched.
        bool anyParts = false;
        foreach (var r in _rooms) if (r.roomKey != r.key) { anyParts = true; break; }
        if (!anyParts) return;

        var interior = new List<Seg>();
        for (int i = 0; i < _rooms.Count; i++)
        for (int j = i + 1; j < _rooms.Count; j++)
        {
            if (_rooms[i].roomKey != _rooms[j].roomKey) continue;
            if (!SharedEdge(_rooms[i], _rooms[j], out bool vertical, out float coord,
                            out float lo, out float hi)) continue;

            interior.Add(new Seg { vertical = vertical, coord = coord, lo = lo, hi = hi });
        }

        if (interior.Count == 0) return;

        segs.RemoveAll(s =>
        {
            foreach (var span in interior)
            {
                if (span.vertical != s.vertical) continue;
                if (Mathf.Abs(span.coord - s.coord) > TOL) continue;
                if (s.lo >= span.lo - TOL && s.hi <= span.hi + TOL) return true;
            }
            return false;
        });
    }

    private void Emit(List<Seg> into, Dictionary<long, List<Vector2>> vertical,
                      Dictionary<long, List<Vector2>> horizontal, bool isVertical)
    {
        var groups = isVertical ? vertical : horizontal;
        var others = isVertical ? horizontal : vertical;

        foreach (var kv in groups)
        {
            float coord = Unkey(kv.Key);

            // Endpoints of perpendicular walls that touch this line: a T-junction or a crossing.
            var breaks = new List<float>();
            foreach (var other in others)
            {
                float otherCoord = Unkey(other.Key);
                foreach (var span in other.Value)
                {
                    if (coord < span.x - TOL || coord > span.y + TOL) continue;
                    breaks.Add(otherCoord);
                    break;
                }
            }

            // Union and Split live in Spans.cs: the edit-time wall linker and the room stamp need the
            // same interval algebra, and PlanBuilder is authoring-time only.
            foreach (var run in Spans.Union(kv.Value))
                foreach (var piece in Spans.Split(run, breaks))
                    into.Add(new Seg { vertical = isVertical, coord = coord, lo = piece.x, hi = piece.y });
        }
    }

    // One RoomDef per ROOM, in declaration order. A room of one rectangle: every room in every
    // sample plan. Takes the rectangle path below and comes out exactly as it always has.
    private void BuildRooms(LevelDef level)
    {
        foreach (var r in _rooms)
        {
            if (r.roomKey != r.key) continue;      // a part; it is emitted with its parent

            var parts = new List<RoomRect>();
            foreach (var p in _rooms) if (p.roomKey == r.key) parts.Add(p);

            level.rooms.Add(new RoomDef
            {
                id = "r_" + r.key,
                name = r.name,
                roomType = r.roomType,
                polygon = parts.Count == 1 ? Corners(r) : Outline(parts, r),
                ceilingHeight = 0f,
            });
        }
    }

    /// <summary>CCW in (x, z). PolygonTriangulator.SignedArea reads positive for this ordering.</summary>
    private static float[][] Corners(RoomRect r) => new[]
    {
        new[] { r.x0, r.z0 },
        new[] { r.x1, r.z0 },
        new[] { r.x1, r.z1 },
        new[] { r.x0, r.z1 },
    };

    private float[][] Outline(List<RoomRect> parts, RoomRect parent)
    {
        if (RectilinearOutline(parts, out float[][] polygon)) return polygon;

        // The parts do not form one simple shape. They are disjoint, or they meet only at a corner,
        // which is a room pinched to nothing at that point. Neither is drawable, and inventing a
        // bounding box would claim floor the plan does not show. So the room falls back to the
        // rectangle it was declared with and says so: the missing piece is visible on screen, which
        // is the OpeningFit convention applied to a floor.
        Warn($"The pieces of '{parent.name}' do not join into one room, so only the first rectangle "
           + "was used. Two pieces have to share a whole edge.");
        return Corners(parent);
    }

    /// <summary>
    /// The boundary of a union of axis-aligned rectangles, CCW, with no redundant vertices.
    ///
    /// It works on the CELLS of the grid the rectangles' own coordinates induce rather than on the
    /// rectangles themselves, which is what makes it total: overlaps, shared edges and T-shaped
    /// meetings all reduce to the same question of which cells are inside. Each inside cell
    /// contributes its four edges wound CCW, an edge shared by two inside cells cancels against its
    /// own reverse, and what survives is the boundary already pointing the right way round.
    ///
    /// Returns false (rather than something plausible) when the survivors are not one simple loop:
    /// two rectangles that only touch at a corner give a vertex with two ways out, and disjoint ones
    /// give two loops. Both are a caller error, and both are worth a sentence rather than a shape.
    /// </summary>
    private static bool RectilinearOutline(List<RoomRect> parts, out float[][] polygon)
    {
        polygon = null;

        var xs = new List<float>();
        var zs = new List<float>();
        foreach (var p in parts)
        {
            AddCoord(xs, p.x0); AddCoord(xs, p.x1);
            AddCoord(zs, p.z0); AddCoord(zs, p.z1);
        }
        if (xs.Count < 2 || zs.Count < 2) return false;
        xs.Sort();
        zs.Sort();

        // Directed boundary edges over vertex indices, cancelling each time a reverse turns up.
        var edges = new HashSet<long>();
        for (int i = 0; i + 1 < xs.Count; i++)
        for (int j = 0; j + 1 < zs.Count; j++)
        {
            float cx = 0.5f * (xs[i] + xs[i + 1]);
            float cz = 0.5f * (zs[j] + zs[j + 1]);

            bool inside = false;
            foreach (var p in parts)
            {
                if (cx <= p.x0 || cx >= p.x1 || cz <= p.z0 || cz >= p.z1) continue;
                inside = true;
                break;
            }
            if (!inside) continue;

            int stride = xs.Count;
            int sw = j * stride + i,           se = j * stride + i + 1;
            int ne = (j + 1) * stride + i + 1, nw = (j + 1) * stride + i;

            AddEdge(edges, sw, se);            // CCW: south, east, north, west
            AddEdge(edges, se, ne);
            AddEdge(edges, ne, nw);
            AddEdge(edges, nw, sw);
        }
        if (edges.Count < 4) return false;

        var next = new Dictionary<int, int>(edges.Count);
        foreach (long e in edges)
        {
            int from = (int)(e >> 32), to = (int)(e & 0xffffffffL);
            if (next.ContainsKey(from)) return false;    // a pinch point: two ways out of one vertex
            next[from] = to;
        }

        var loop = new List<int>(next.Count);
        int start = int.MaxValue;
        foreach (int from in next.Keys) if (from < start) start = from;

        int at = start;
        do
        {
            loop.Add(at);
            if (!next.TryGetValue(at, out at)) return false;
            if (loop.Count > next.Count) return false;
        }
        while (at != start);

        if (loop.Count != next.Count) return false;      // more than one loop: disjoint, or a hole

        var pts = new List<float[]>(loop.Count);
        foreach (int v in loop) pts.Add(new[] { xs[v % xs.Count], zs[v / xs.Count] });

        polygon = StripCollinear(pts);
        return polygon.Length >= 4;
    }

    // Every cell edge lies on a grid line, so a vertex is redundant exactly when its neighbors share
    // one of its two coordinates. Keeping them would put a corner where the room has none.
    private static float[][] StripCollinear(List<float[]> pts)
    {
        var kept = new List<float[]>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
        {
            float[] prev = pts[(i - 1 + pts.Count) % pts.Count];
            float[] here = pts[i];
            float[] nextPt = pts[(i + 1) % pts.Count];

            bool flatX = Mathf.Abs(prev[0] - here[0]) < TOL && Mathf.Abs(here[0] - nextPt[0]) < TOL;
            bool flatZ = Mathf.Abs(prev[1] - here[1]) < TOL && Mathf.Abs(here[1] - nextPt[1]) < TOL;
            if (flatX || flatZ) continue;

            kept.Add(here);
        }
        return kept.ToArray();
    }

    private static void AddCoord(List<float> into, float v)
    {
        foreach (float have in into) if (Mathf.Abs(have - v) < TOL) return;
        into.Add(v);
    }

    private static void AddEdge(HashSet<long> edges, int from, int to)
    {
        long reverse = ((long)to << 32) | (uint)from;
        if (edges.Remove(reverse)) return;
        edges.Add(((long)from << 32) | (uint)to);
    }

    private void BuildOpenings(LevelDef level, List<Seg> segs)
    {
        int n = 0;
        foreach (var p in _openings)
        {
            var seg = Find(segs, p.vertical, p.coord, p.along);
            if (seg == null)
            {
                Warn($"No wall found for opening '{p.label}'.");
                continue;
            }

            OpeningFit.FitVertical(p.sill, p.height, _ceilingHeight, out float sill, out float height);
            if (Mathf.Abs(sill - p.sill) > TOL || Mathf.Abs(height - p.height) > TOL)
                Warn($"Opening '{p.label}' did not fit vertically and was clamped.");

            var def = new OpeningDef
            {
                id = "o_" + n++,
                wallId = seg.def.id,
                offset = p.along - seg.lo,
                width = p.width,
                height = height,
                clearWidth = 0f,      // unspecified; HomeMetrics derives it from width + kind
                sillHeight = sill,
                kind = p.kind,
                thresholdHeight = p.threshold,
            };

            level.openings.Add(def);

            if (!OpeningFit.IsValid(def, seg.def, level))
            {
                var fit = OpeningFit.Fit(def, seg.def, level, def.offset);
                Warn($"Opening '{p.label}' does not fit its wall: {fit.reason ?? "unknown"}");
            }
        }
    }

    private void BuildFurniture(LevelDef level)
    {
        int n = 0;

        // What each room already holds. Full footprints rather than along-wall spans, because the
        // door-aware recipes put the tub and the basin on PERPENDICULAR walls, and two items sliding
        // toward the same corner only conflict in two dimensions.
        var placed = new Dictionary<string, List<Rect>>();

        foreach (var p in _items)
        {
            float x = p.x, z = p.z;

            // Runs here rather than in Against() because openings only exist once BuildOpenings has
            // run. A wardrobe standing across a bedroom door is not something the renderer will object
            // to, so it has to be caught at authoring time or not at all.
            if (p.againstWall)
            {
                var blocked = OpeningSpans(p.wallVertical, p.wallCoord, 0f, p.item.height);

                float cross = p.wallVertical ? x : z;
                if (placed.TryGetValue(p.roomGroup ?? "", out var neighbors))
                {
                    foreach (var r in neighbors)
                    {
                        // Project a neighbor onto this wall's axis, but only if it actually reaches
                        // into the band this item occupies away from the wall.
                        float cLo = p.wallVertical ? r.xMin : r.yMin;
                        float cHi = p.wallVertical ? r.xMax : r.yMax;
                        if (cHi <= cross - 0.5f * p.crossSize + TOL) continue;
                        if (cLo >= cross + 0.5f * p.crossSize - TOL) continue;

                        blocked.Add(p.wallVertical ? new Vector2(r.yMin, r.yMax)
                                                   : new Vector2(r.xMin, r.xMax));
                    }
                }

                // NOTE: openings in the room's PERPENDICULAR walls are deliberately NOT considered.
                // Reserving an approach strip in front of them was tried and reverted: a kitchen run is
                // supposed to reach the corner next to a cased opening, and the rule pushed counters,
                // baths and wardrobes out of layouts that were correct. The handful of items that do
                // reach into a neighboring doorway are placed explicitly in SampleHomes instead.
                float want = p.wallVertical ? z : x;
                if (blocked.Count > 0 && !SpanIsClear(want, p.alongSize, blocked))
                {
                    if (TrySlideClear(want, p.alongSize, p.alongLo, p.alongHi, blocked, out float moved))
                    {
                        if (p.wallVertical) z = moved; else x = moved;
                    }
                    else
                    {
                        Warn($"'{p.prefabType}' in '{p.room}' has no clear span on its wall: " +
                             "it would stand in a door, a window, or another item.");
                    }
                }
            }

            if (!string.IsNullOrEmpty(p.roomGroup))
            {
                Vector2 fp = SampleFurniture.FootprintXZ(p.item, p.yaw);
                if (!placed.TryGetValue(p.roomGroup, out var list))
                    placed[p.roomGroup] = list = new List<Rect>();
                list.Add(new Rect(x - 0.5f * fp.x, z - 0.5f * fp.y, fp.x, fp.y));
            }

            level.furniture.Add(new ObjectInstance
            {
                instanceId = "f_" + n++,
                prefabType = p.prefabType,
                position = new[] { Q(x), 0f, Q(z) },
                rotationX = 0f,
                rotationY = p.yaw,
                rotationZ = 0f,
                scale = 1f,
                boxSizeMeters = p.item.BoxSize,
                included = true,
                brushPainted = false,
            });
        }
    }

    /// <summary>
    /// The household, resolved against a level that Build() already produced. Separate from Build
    /// because occupants live on VariantDef, and because anchors and room ids can only be resolved
    /// once the level's furniture and rooms exist.
    ///
    /// Runs OccupancyModel.Validate at the end, so an overlapping or incomplete day is a builder
    /// warning, which the sample tests assert is never present.
    /// </summary>
    public List<OccupantDef> BuildOccupants(LevelDef level)
    {
        var list = new List<OccupantDef>();
        if (_people.Count == 0) return list;

        int pn = 0, an = 0;
        foreach (var p in _people)
        {
            var occupant = new OccupantDef
            {
                id = "p_" + pn,
                name = p.name,
                note = p.note,
                usesWheelchair = p.wheelchair,
                color = OccupantPalette.At(pn),
                included = true,
                schedule = new List<ActivityDef>(),
            };
            pn++;

            foreach (var a in p.day)
            {
                // Through _byKey, so a schedule that names one PART of a room still resolves to the
                // room's own id, which is what OccupancyModel and every sensor host address.
                string roomId = a.roomKey != null && _byKey.TryGetValue(a.roomKey, out var scheduled)
                              ? "r_" + scheduled.roomKey
                              : null;
                occupant.schedule.Add(new ActivityDef
                {
                    id = "a_" + an++,
                    kind = a.kind,
                    label = a.label,
                    startMinutes = Clock.Wrap(a.start),
                    endMinutes = Clock.Wrap(a.end),
                    roomId = roomId,
                    anchorId = ResolveAnchor(level, roomId, a.anchorType, p.name),
                });
            }

            list.Add(occupant);
        }

        OccupancyModel.Validate(new VariantDef { levels = new List<LevelDef> { level }, occupants = list },
                                level, _warnings);
        return list;
    }

    // Finds the first item of the given catalog type standing inside the room. Returns null (a plain
    // room-center placement) rather than warning when no anchor was asked for.
    private string ResolveAnchor(LevelDef level, string roomId, string prefabType, string who)
    {
        if (string.IsNullOrEmpty(prefabType)) return null;

        var room = OccupancyModel.FindRoom(level, roomId);
        if (room == null)
        {
            Warn($"'{who}' is anchored to a '{prefabType}' but the room was not resolved.");
            return null;
        }

        var poly = PolygonTriangulator.ToVector2(room.polygon);
        foreach (var f in level.furniture)
        {
            if (f == null || f.prefabType != prefabType || f.position == null || f.position.Length < 3) continue;
            if (HomeMetrics.PointInPolygon(new Vector2(f.position[0], f.position[2]), poly)) return f.instanceId;
        }

        Warn($"'{who}' is anchored to a '{prefabType}', but '{room.name}' has none.");
        return null;
    }

    private void BuildMounts(LevelDef level, List<Seg> segs)
    {
        int n = 0;
        foreach (var p in _mounts)
        {
            // Find() only ever asked whether the mount's CENTRE landed on a wall segment. That let a
            // 0.91 m grab bar hang in a doorway, and let one poke 0.155 m past the end of its wall into
            // open air. Consider the whole line, the item's own width, and the openings on it.
            float width = Mathf.Max(0.02f, p.item.width);
            var blocked = OpeningSpans(p.vertical, p.coord,
                                       p.mountHeight - 0.5f * p.item.height,
                                       p.mountHeight + 0.5f * p.item.height);

            Seg seg = null;
            float along = p.along;
            float best = float.MaxValue;
            foreach (var s in segs)
            {
                if (s.vertical != p.vertical) continue;
                if (Mathf.Abs(s.coord - p.coord) > TOL) continue;
                if (!TrySlideClear(p.along, width, s.lo, s.hi, blocked, out float candidate)) continue;

                float d = Mathf.Abs(candidate - p.along);
                if (d >= best) continue;
                best = d; along = candidate; seg = s;
            }

            if (seg == null)
            {
                // Fall back to the old center-only resolution so the element still exists and the count
                // is stable; the warning is what fails the sample tests and sends someone back to the plan.
                seg = Find(segs, p.vertical, p.coord, p.along);
                if (seg == null)
                {
                    Warn($"No wall found for a '{p.prefabType}' mounted in '{p.label}'.");
                    continue;
                }
                Warn($"'{p.prefabType}' in '{p.label}' has no clear span on its wall: " +
                     "it would hang in a door or window.");
                along = Mathf.Clamp(p.along, seg.lo + 0.5f * width, Mathf.Max(seg.lo, seg.hi - 0.5f * width));
            }

            level.wallMounted.Add(new WallMountDef
            {
                instanceId = "m_" + n++,
                prefabType = p.prefabType,
                wallId = seg.def.id,
                offset = along - seg.lo,
                side = SideFacing(seg, p.interior),
                mountHeight = p.mountHeight,
                decorWidthFrac = p.item.decorWidthFrac,
                decorHeightFrac = p.item.decorHeightFrac,
                decorAnchor = 0,
                decorSurfaceOffset = p.item.decorSurfaceOffset,
                decorMountAxis = 0,
                decorFlipMount = false,
                included = true,
            });
        }
    }

    // -------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------

    // -------------------------------------------------------------------------------------------
    // Opening avoidance
    //
    // Nothing downstream objects to an item sitting in a doorway: WallLayout emits solid boxes only
    // BETWEEN openings, so a grab bar centered on a door renders as a bar floating in the hole, and a
    // dresser across a bedroom door renders as a dresser across a bedroom door. Neither Against() nor
    // Mount() knew openings existed. These three helpers are what they consult now.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The spans one wall line's openings occupy, in world coordinates along that line, but only the
    /// openings the item's own height actually reaches.
    ///
    /// A sofa in front of a window is not a mistake: the sill is at 0.914 m and the sofa is 0.84 m tall,
    /// so it passes underneath, and a kitchen run under a window is exactly where a kitchen run goes.
    /// A wardrobe across the same window is a mistake. Doors have a sill of 0, so everything conflicts
    /// with a door, which is the case that matters most.
    /// </summary>
    private List<Vector2> OpeningSpans(bool vertical, float coord, float itemBottom, float itemTop)
    {
        var spans = new List<Vector2>();
        foreach (var o in _openings)
        {
            if (o.vertical != vertical) continue;
            if (Mathf.Abs(o.coord - coord) > TOL) continue;
            // An opening runs from its sill to sill + height; overlap has to be genuine in BOTH axes.
            if (itemTop <= o.sill + TOL) continue;
            if (itemBottom >= o.sill + o.height - TOL) continue;
            spans.Add(new Vector2(o.along - 0.5f * o.width, o.along + 0.5f * o.width));
        }
        return spans;
    }

    // Demands a sliver of daylight rather than merely tolerating a hair of overlap. Parking two items
    // exactly flush leaves their footprints overlapping by float noise, which is a real test failure
    // (SampleHomesTests allows 1e-3 m²) even though it is invisible.
    private static bool SpanIsClear(float center, float size, List<Vector2> blocked)
    {
        float s = center - 0.5f * size, e = center + 0.5f * size;
        foreach (var b in blocked)
            if (e > b.x - TOL && s < b.y + TOL) return false;
        return true;
    }

    /// <summary>
    /// Slides a span of <paramref name="size"/> centered on <paramref name="want"/> to the nearest spot
    /// inside [lo, hi] that clears every blocked span. Sliding rather than refusing is the OpeningFit
    /// convention: an item that cannot be placed exactly should land somewhere legal, not vanish.
    /// </summary>
    private static bool TrySlideClear(float want, float size, float lo, float hi,
                                      List<Vector2> blocked, out float result)
    {
        result = want;
        float min = lo + 0.5f * size, max = hi - 0.5f * size;
        if (min > max + TOL) return false;

        float clamped = Mathf.Clamp(want, min, max);
        if (SpanIsClear(clamped, size, blocked)) { result = clamped; return true; }

        // The only positions worth testing are hard against one side of a blocker, or either end.
        var candidates = new List<float> { min, max };
        foreach (var b in blocked)
        {
            candidates.Add(b.x - 0.5f * size - 2f * TOL);
            candidates.Add(b.y + 0.5f * size + 2f * TOL);
        }

        bool found = false;
        float best = float.MaxValue;
        foreach (float c in candidates)
        {
            if (c < min - TOL || c > max + TOL) continue;
            float p = Mathf.Clamp(c, min, max);
            if (!SpanIsClear(p, size, blocked)) continue;
            float d = Mathf.Abs(p - want);
            if (d >= best) continue;
            best = d; result = p; found = true;
        }
        return found;
    }

    // Walls run in ascending coordinate order, so forward is +Z for a vertical wall and +X for a
    // horizontal one. left = Cross(forward, up), which puts "left" on -X for vertical walls and on
    // +Z for horizontal ones. WallSide and materialLeft/Right both follow that same frame.
    private static int SideFacing(Seg seg, Vector2 point)
    {
        if (seg.vertical) return point.x < seg.coord ? WallSide.Left : WallSide.Right;
        return point.y > seg.coord ? WallSide.Left : WallSide.Right;
    }

    private static Seg Find(List<Seg> segs, bool vertical, float coord, float along)
    {
        foreach (var s in segs)
        {
            if (s.vertical != vertical) continue;
            if (Mathf.Abs(s.coord - coord) > TOL) continue;
            if (along < s.lo - TOL || along > s.hi + TOL) continue;
            return s;
        }
        return null;
    }

    // True when the two rectangles share a stretch of one edge. Rooms only ever touch along a full
    // line here, so this is an axis test plus an overlap test rather than general polygon clipping.
    private static bool SharedEdge(RoomRect a, RoomRect b, out bool vertical, out float coord,
                                   out float lo, out float hi)
    {
        vertical = false; coord = 0f; lo = 0f; hi = 0f;

        if (Mathf.Abs(a.x1 - b.x0) < TOL || Mathf.Abs(b.x1 - a.x0) < TOL)
        {
            float overlapLo = Mathf.Max(a.z0, b.z0);
            float overlapHi = Mathf.Min(a.z1, b.z1);
            if (overlapHi - overlapLo <= TOL) return false;
            vertical = true;
            coord = Mathf.Abs(a.x1 - b.x0) < TOL ? a.x1 : a.x0;
            lo = overlapLo; hi = overlapHi;
            return true;
        }

        if (Mathf.Abs(a.z1 - b.z0) < TOL || Mathf.Abs(b.z1 - a.z0) < TOL)
        {
            float overlapLo = Mathf.Max(a.x0, b.x0);
            float overlapHi = Mathf.Min(a.x1, b.x1);
            if (overlapHi - overlapLo <= TOL) return false;
            vertical = false;
            coord = Mathf.Abs(a.z1 - b.z0) < TOL ? a.z1 : a.z0;
            lo = overlapLo; hi = overlapHi;
            return true;
        }

        return false;
    }

    private static void EdgeLine(RoomRect r, PlanEdge edge, out bool vertical, out float coord,
                                 out float lo, out float hi)
    {
        switch (edge)
        {
            case PlanEdge.South: vertical = false; coord = r.z0; lo = r.x0; hi = r.x1; break;
            case PlanEdge.North: vertical = false; coord = r.z1; lo = r.x0; hi = r.x1; break;
            case PlanEdge.West:  vertical = true;  coord = r.x0; lo = r.z0; hi = r.z1; break;
            default:         vertical = true;  coord = r.x1; lo = r.z0; hi = r.z1; break;
        }
    }

    // Yaw that makes an item standing on this edge face into the room. rotationY = 0 looks down +Z.
    private static float YawFacingInto(PlanEdge edge)
    {
        switch (edge)
        {
            case PlanEdge.South: return 0f;
            case PlanEdge.North: return 180f;
            case PlanEdge.West:  return 90f;
            default:         return 270f;
        }
    }

    // Positions a footprint of `size` at `fraction` between lo and hi, keeping it fully inside.
    private float SlideAlong(float lo, float hi, float fraction, float size, string what, string room)
    {
        float want = Mathf.Lerp(lo, hi, Mathf.Clamp01(fraction));
        float min = lo + 0.5f * size;
        float max = hi - 0.5f * size;
        if (min > max)
        {
            Warn($"'{what}' is too big for '{room}', so it will overhang.");
            return 0.5f * (lo + hi);
        }
        return Mathf.Clamp(want, min, max);
    }

    private SampleFurniture.Item Resolve(string prefabType, string room)
    {
        if (SampleFurniture.TryGet(prefabType, out var item)) return item;
        Warn($"'{prefabType}' (in '{room}') is not a furniture catalog id.");
        return SampleFurniture.Unknown;
    }

    private bool Lookup(string key, out RoomRect r)
    {
        if (_byKey.TryGetValue(key ?? "", out r)) return true;
        Warn($"Unknown room '{key}'.");
        return false;
    }

    private static void Add(Dictionary<long, List<Vector2>> map, float line, float lo, float hi)
    {
        long key = Key(line);
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<Vector2>();
        list.Add(new Vector2(Mathf.Min(lo, hi), Mathf.Max(lo, hi)));
    }

    private static long Key(float v) => (long)Mathf.Round(v / GRID);
    private static float Unkey(long k) => k * GRID;
    private static float Q(float v) => Mathf.Round(v / GRID) * GRID;

    private void Warn(string message) => _warnings.Add(message);

    // -------------------------------------------------------------------------------------------

    private sealed class RoomRect
    {
        public string key, name, roomType;

        /// <summary>
        /// The room this rectangle belongs to. Equal to <see cref="key"/> for a whole room, which is
        /// every rectangle in all six sample plans, and the parent's key for a part declared through
        /// RoomPart. Openings and furniture still address the RECTANGLE by its own key, because both
        /// are placed against one specific edge; only walls and the emitted RoomDef read this.
        /// </summary>
        public string roomKey;

        public float x0, z0, x1, z1;
        public Vector2 Center => new Vector2(0.5f * (x0 + x1), 0.5f * (z0 + z1));
    }

    private sealed class Seg
    {
        public bool vertical;
        public float coord;     // x for a vertical wall, z for a horizontal one
        public float lo, hi;    // extent along the wall's own axis
        public WallDef def;
    }

    private sealed class PendingOpening
    {
        public string label, kind;
        public bool vertical;
        public float coord, along, width, height, sill, threshold;
    }

    private sealed class PendingItem
    {
        public string prefabType;
        public SampleFurniture.Item item;
        public float x, z, yaw;

        // Set by Against(): which wall line the item is flush against, and its room's extent along it.
        // Recorded rather than resolved on the spot because openings do not exist until Build(): the
        // slide that keeps a dresser out of a doorway has to happen after BuildOpenings.
        public bool againstWall;
        public bool wallVertical;      // true => a vertical wall, so the item slides in z
        public float wallCoord;
        public float alongLo, alongHi; // the room's span along that wall
        public float alongSize;        // the item's footprint along that wall
        public float crossSize;        // and away from it, so a corner neighbor can be detected

        // The rectangle this was addressed to, which is what a warning should name...
        public string room;
        // ...and the ROOM that rectangle belongs to, which is what the placed-footprint bookkeeping
        // keys on. They differ only for a room built from parts, and there the distinction is the
        // whole point: a sofa in the alcove and a table in the main span are in one room and must
        // still be checked against each other.
        public string roomGroup;
    }

    private sealed class PendingMount
    {
        public string prefabType, label;
        public SampleFurniture.Item item;
        public bool vertical;
        public float coord, along, mountHeight;
        public Vector2 interior;
    }

    private sealed class PendingPerson
    {
        public string key, name, note;
        public bool wheelchair;
        public readonly List<PendingActivity> day = new List<PendingActivity>();
    }

    private sealed class PendingActivity
    {
        public string kind, label, roomKey, anchorType;
        public int start, end;
    }
}
