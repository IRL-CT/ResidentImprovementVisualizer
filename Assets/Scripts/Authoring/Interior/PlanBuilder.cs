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
// Anything that could not be resolved lands in Warnings instead of throwing — the sample tests assert
// that list is empty, which is what turns a silent geometry bug into a failing test.
public sealed class PlanBuilder
{
    // Coordinates are quantised to this, comfortably inside WallMeshBuilder.Near's 1 mm weld radius.
    private const float GRID = 0.001f;
    private const float TOL  = 0.002f;

    private readonly float _ceilingHeight;
    private readonly float _wallThickness;

    private readonly List<RoomRect> _rooms = new List<RoomRect>();
    private readonly Dictionary<string, RoomRect> _byKey = new Dictionary<string, RoomRect>();
    private readonly List<PendingOpening> _openings = new List<PendingOpening>();
    private readonly List<PendingItem> _items = new List<PendingItem>();
    private readonly List<PendingMount> _mounts = new List<PendingMount>();
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
    /// A room as an axis-aligned rectangle on WALL CENTERLINES — (x, z) is the min corner, (w, d) the
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
            key = key, name = name, roomType = roomType,
            x0 = Q(x), z0 = Q(z), x1 = Q(x + w), z1 = Q(z + d),
        };
        _rooms.Add(r);
        _byKey[key] = r;
        return this;
    }

    /// <summary>
    /// An opening on the wall two rooms share. The shared edge is found automatically and the opening
    /// is centred in the overlap (or placed at <paramref name="alongFraction"/> of it).
    /// </summary>
    public PlanBuilder DoorBetween(string roomA, string roomB, float width,
                                   string kind = OpeningKind.Door, string swing = OpeningSwing.LeftIn,
                                   float threshold = 0f, float alongFraction = 0.5f, float height = 0f)
    {
        if (!Lookup(roomA, out var a) || !Lookup(roomB, out var b)) return this;

        if (!SharedEdge(a, b, out bool vertical, out float coord, out float lo, out float hi))
        {
            Warn($"'{roomA}' and '{roomB}' do not share an edge — no door placed.");
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
            swing = kind == OpeningKind.Door ? swing : OpeningSwing.None,
        });
        return this;
    }

    /// <summary>A door in a room's exterior wall. Thresholds default to 0 — step-free.</summary>
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
            swing = OpeningSwing.LeftIn,
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
            swing = OpeningSwing.None,
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
    /// actually installed as an alcove — long side against the wall. It is also the only way either
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
        switch (edge)
        {
            case PlanEdge.South:
                z = r.z0 + standoff + 0.5f * fp.y;
                x = SlideAlong(r.x0, r.x1, alongFraction, fp.x, prefabType, room);
                break;
            case PlanEdge.North:
                z = r.z1 - standoff - 0.5f * fp.y;
                x = SlideAlong(r.x0, r.x1, alongFraction, fp.x, prefabType, room);
                break;
            case PlanEdge.West:
                x = r.x0 + standoff + 0.5f * fp.x;
                z = SlideAlong(r.z0, r.z1, alongFraction, fp.y, prefabType, room);
                break;
            default:
                x = r.x1 - standoff - 0.5f * fp.x;
                z = SlideAlong(r.z0, r.z1, alongFraction, fp.y, prefabType, room);
                break;
        }

        _items.Add(new PendingItem { prefabType = prefabType, item = item, x = x, z = z, yaw = yaw });
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

        _items.Add(new PendingItem { prefabType = prefabType, item = item, x = x, z = z, yaw = yaw });
        return this;
    }

    /// <summary>
    /// Hangs an item on one of the room's walls. The host wall, the offset along it, and which of its
    /// two faces to use are all derived — the face chosen is the one looking into this room.
    /// </summary>
    public PlanBuilder Mount(string prefabType, string room, PlanEdge edge, float alongFraction,
                             float mountHeight = 0f)
    {
        if (!Lookup(room, out var r)) return this;

        var item = Resolve(prefabType, room);
        if (!item.wallMounted)
            Warn($"'{prefabType}' is not a wall-mounted catalog item but was mounted in '{room}'.");

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
    //   * union     — two rooms sharing an edge, or overlapping partially because they have different
    //                 depths, collapse into a single non-overlapping run instead of coincident walls.
    //   * re-split  — a perpendicular wall that ENDS on this line (a T-junction) or CROSSES it forces
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
                structural = false,
            };
        }

        return segs;
    }

    private void Emit(List<Seg> into, Dictionary<long, List<Vector2>> vertical,
                      Dictionary<long, List<Vector2>> horizontal, bool isVertical)
    {
        var groups = isVertical ? vertical : horizontal;
        var others = isVertical ? horizontal : vertical;

        foreach (var kv in groups)
        {
            float coord = Unkey(kv.Key);

            // Endpoints of perpendicular walls that touch this line — a T-junction or a crossing.
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

            foreach (var run in Union(kv.Value))
                foreach (var piece in Split(run, breaks))
                    into.Add(new Seg { vertical = isVertical, coord = coord, lo = piece.x, hi = piece.y });
        }
    }

    private static List<Vector2> Union(List<Vector2> spans)
    {
        spans.Sort((p, q) => p.x.CompareTo(q.x));
        var merged = new List<Vector2>();
        foreach (var s in spans)
        {
            if (merged.Count > 0 && s.x <= merged[merged.Count - 1].y + TOL)
            {
                var last = merged[merged.Count - 1];
                merged[merged.Count - 1] = new Vector2(last.x, Mathf.Max(last.y, s.y));
            }
            else merged.Add(s);
        }
        return merged;
    }

    private static List<Vector2> Split(Vector2 run, List<float> breaks)
    {
        var cuts = new List<float>();
        foreach (float b in breaks)
            if (b > run.x + TOL && b < run.y - TOL) cuts.Add(b);

        cuts.Sort();

        var pieces = new List<Vector2>();
        float start = run.x;
        foreach (float c in cuts)
        {
            if (c - start > TOL) pieces.Add(new Vector2(start, c));
            start = c;
        }
        if (run.y - start > TOL) pieces.Add(new Vector2(start, run.y));
        return pieces;
    }

    private void BuildRooms(LevelDef level)
    {
        foreach (var r in _rooms)
            level.rooms.Add(new RoomDef
            {
                id = "r_" + r.key,
                name = r.name,
                roomType = r.roomType,
                // CCW in (x, z) — PolygonTriangulator.SignedArea reads positive for this ordering.
                polygon = new[]
                {
                    new[] { r.x0, r.z0 },
                    new[] { r.x1, r.z0 },
                    new[] { r.x1, r.z1 },
                    new[] { r.x0, r.z1 },
                },
                floorMaterial = FloorFor(r.roomType),
                ceilingMaterial = "ceiling_white",
                ceilingHeight = 0f,
            });
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
                clearWidth = 0f,      // unspecified; HomeMetrics derives it from width + swing
                sillHeight = sill,
                kind = p.kind,
                swing = p.swing,
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
        foreach (var p in _items)
            level.furniture.Add(new ObjectInstance
            {
                instanceId = "f_" + n++,
                prefabType = p.prefabType,
                position = new[] { Q(p.x), 0f, Q(p.z) },
                rotationX = 0f,
                rotationY = p.yaw,
                rotationZ = 0f,
                scale = 1f,
                boxSizeMeters = p.item.BoxSize,
                included = true,
                brushPainted = false,
            });
    }

    private void BuildMounts(LevelDef level, List<Seg> segs)
    {
        int n = 0;
        foreach (var p in _mounts)
        {
            var seg = Find(segs, p.vertical, p.coord, p.along);
            if (seg == null)
            {
                Warn($"No wall found for a '{p.prefabType}' mounted in '{p.label}'.");
                continue;
            }

            level.wallMounted.Add(new WallMountDef
            {
                instanceId = "m_" + n++,
                prefabType = p.prefabType,
                wallId = seg.def.id,
                offset = p.along - seg.lo,
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
            Warn($"'{what}' is too big for '{room}' — it will overhang.");
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

    // Matches RoomTool's defaults, so a sample room is indistinguishable from a drawn one.
    private static string FloorFor(string roomType)
    {
        switch (roomType)
        {
            case RoomType.Bathroom:
            case RoomType.Kitchen:
            case RoomType.Laundry: return "floor_vinyl";
            case RoomType.Bedroom: return "floor_carpet";
            default: return "floor_oak";
        }
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
        public string label, kind, swing;
        public bool vertical;
        public float coord, along, width, height, sill, threshold;
    }

    private sealed class PendingItem
    {
        public string prefabType;
        public SampleFurniture.Item item;
        public float x, z, yaw;
    }

    private sealed class PendingMount
    {
        public string prefabType, label;
        public SampleFurniture.Item item;
        public bool vertical;
        public float coord, along, mountHeight;
        public Vector2 interior;
    }
}
