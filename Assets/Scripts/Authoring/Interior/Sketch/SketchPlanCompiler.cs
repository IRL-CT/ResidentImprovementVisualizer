using System.Collections.Generic;
using UnityEngine;

/// <summary>How an edge is spelled on the wire, and what it means to PlanBuilder.</summary>
public static class SketchEdge
{
    public static bool TryParse(string s, out PlanEdge edge)
    {
        switch (s)
        {
            case "south": edge = PlanEdge.South; return true;
            case "east":  edge = PlanEdge.East;  return true;
            case "north": edge = PlanEdge.North; return true;
            case "west":  edge = PlanEdge.West;  return true;
            default:      edge = PlanEdge.South; return false;
        }
    }
}

/// <summary>
/// Where the sketch sits in the world, and how a point on it becomes a point on the floor.
///
/// THIS IS THE TRANSFORM WORTH BEING CAREFUL ABOUT. LayoutConverter's header documents the same
/// class of bug on the legacy site pipeline: an index transpose that reflected the ground plane and
/// silently flipped every rotation, and the cost of getting it wrong here is identical: a plan that
/// renders perfectly and is a mirror image of the sketch it was traced from, which reads as a
/// property of the source file rather than as a bug.
///
/// Two conventions meet here and they disagree about which way is up. UnderlayTool.ApplyTransform
/// places the quad with its BOTTOM-LEFT corner at originMeters, sized texW*mpp by texH*mpp, and
/// rotates it Euler(90, rotationDeg, 0) about its own center. The model, meanwhile, reads the image
/// the way anyone reads a picture. Origin top-left, y increasing DOWNWARD. So the vertical axis is
/// flipped exactly once, in ToWorld, and nowhere else.
/// </summary>
public struct SketchFrame
{
    public Vector2 origin;      // world XZ of the image's bottom-left corner
    public float metersW;
    public float metersH;
    public float rotationDeg;   // a multiple of 90; see Build
    public bool valid;
    public string reason;

    /// <summary>Normalised units the model works in, across the whole image in each axis.</summary>
    public const float SPAN = 1000f;

    /// <summary>
    /// Refuses anything but a quarter turn, and says why.
    ///
    /// PlanBuilder takes AXIS-ALIGNED rectangles: that is the whole basis of its wall derivation,
    /// which unions and re-splits spans along shared lines. A sketch pinned at 7° cannot produce
    /// axis-aligned world rooms, so there is nothing honest to generate from it. Refusing and saying
    /// so beats emitting a plan that is quietly skewed against the image it came from.
    /// </summary>
    public static SketchFrame Build(float[] originMeters, int texWidth, int texHeight,
                                    float metersPerPixel, float rotationDeg)
    {
        var f = new SketchFrame
        {
            origin = originMeters != null && originMeters.Length >= 2
                   ? new Vector2(originMeters[0], originMeters[1])
                   : Vector2.zero,
            metersW = texWidth * metersPerPixel,
            metersH = texHeight * metersPerPixel,
            rotationDeg = Mathf.Round(rotationDeg / 90f) * 90f,
            valid = true,
        };

        if (metersPerPixel <= 0f)
        {
            f.valid = false;
            f.reason = "This sketch has not been given a scale yet. Calibrate it first: click two "
                     + "points and type the real distance between them.";
            return f;
        }

        if (texWidth <= 0 || texHeight <= 0)
        {
            f.valid = false;
            f.reason = "The sketch image could not be measured.";
            return f;
        }

        if (Mathf.Abs(Mathf.DeltaAngle(rotationDeg, f.rotationDeg)) > 0.5f)
        {
            f.valid = false;
            f.reason = $"This sketch is turned {rotationDeg:0.#}°. A plan can only be generated from "
                     + "one that sits square. Set the angle back to a quarter turn first.";
            return f;
        }

        return f;
    }

    /// <summary>
    /// A point in normalised image coordinates (u across, v DOWN from the top, both 0..1000) as a
    /// point on the floor in world metres.
    /// </summary>
    public Vector2 ToWorld(float u, float v)
    {
        var p = new Vector2(
            origin.x + (u / SPAN) * metersW,
            origin.y + (1f - v / SPAN) * metersH);   // the one flip

        if (Mathf.Abs(rotationDeg) < 0.001f) return p;

        // The quad rotates about its own center, so the world point does too. Unity's Y rotation
        // turns +X toward -Z, which is the sign below.
        var center = new Vector2(origin.x + 0.5f * metersW, origin.y + 0.5f * metersH);
        Vector2 d = p - center;
        float r = rotationDeg * Mathf.Deg2Rad;
        float cos = Mathf.Cos(r), sin = Mathf.Sin(r);
        return center + new Vector2(d.x * cos + d.y * sin, -d.x * sin + d.y * cos);
    }
}

/// <summary>What came back from a compile: the level, the rooms it was built from, and every complaint.</summary>
public sealed class SketchCompileResult
{
    public LevelDef level;
    public List<SketchRect> rooms = new List<SketchRect>();
    public List<string> issues = new List<string>();      // from the validator. Worth a repair turn
    public List<string> warnings = new List<string>();    // from PlanBuilder. Worth showing
    public string refusal;                                // set when nothing could be built at all

    public bool Ok => level != null && refusal == null;
    public bool Clean => Ok && issues.Count == 0 && warnings.Count == 0;
}

/// <summary>What came back from compiling the rooms alone.</summary>
public sealed class SketchRoomsResult
{
    public List<SketchRect> rooms = new List<SketchRect>();
    public List<string> issues = new List<string>();
    public string refusal;

    public bool Ok => refusal == null && rooms.Count > 0;
}

// Turns what the model sent into a LevelDef, by driving PlanBuilder.
//
// Nothing here computes geometry. Every coordinate that ends up in a WallDef, every opening offset
// and every furniture position is derived by PlanBuilder from room rectangles and relationships,
// which is the entire point of routing generation through that file rather than emitting the schema
// directly. What this does is the three things PlanBuilder cannot: put the rectangles in the right
// place on the floor, make almost-shared edges actually shared, and give the result ids that cannot
// collide with anything already in the home.
public static class SketchPlanCompiler
{
    public static SketchCompileResult Compile(SketchPlanSpec spec, SketchFrame frame,
                                              float ceilingHeight, float wallThickness,
                                              string idPrefix = null,
                                              float tolerance = SketchRegularizer.DefaultTolerance)
    {
        var result = new SketchCompileResult();

        if (!frame.valid) { result.refusal = frame.reason; return result; }
        if (spec == null) { result.refusal = "The response had no plan in it."; return result; }

        // 1 & 2. Image coordinates to the floor, then almost-shared edges made shared.
        result.rooms = Rects(spec, frame, tolerance, result.issues);

        // 3. Say what is wrong before building anything, so the repair turn has something specific.
        result.issues.AddRange(SketchPlanValidator.Check(spec, result.rooms, ceilingHeight));

        if (result.rooms.Count == 0)
        {
            result.refusal = "No usable rooms came back for this sketch.";
            return result;
        }

        // 4. Drive PlanBuilder. Whole rooms first, then the extra pieces of them. PlanBuilder.RoomPart
        //    resolves its parent on the spot, so a part declared before its room would be dropped.
        var builder = new PlanBuilder(ceilingHeight, wallThickness);
        var known = new HashSet<string>();

        foreach (var r in result.rooms)
        {
            if (r.IsPart) continue;
            if (string.IsNullOrWhiteSpace(r.key) || !known.Add(r.key)) continue;
            builder.Room(r.key, string.IsNullOrWhiteSpace(r.name) ? r.key : r.name,
                         Known(RoomFinish.All, r.roomType) ? r.roomType : RoomType.Untyped,
                         r.x0, r.z0, r.Width, r.Depth);
        }

        foreach (var r in result.rooms)
        {
            if (!r.IsPart) continue;
            if (string.IsNullOrWhiteSpace(r.key) || !known.Contains(r.roomKey) || !known.Add(r.key)) continue;
            builder.RoomPart(r.key, r.roomKey, r.x0, r.z0, r.Width, r.Depth);
        }

        // Openings BEFORE furniture, always. PlanBuilder.ClearRunOn reads the pending opening list,
        // so anything placed first would be sliding clear of an empty one, which is how a bath ends
        // up across the only way into a bathroom.
        AddOpenings(builder, spec, known);
        AddFurniture(builder, spec, known);

        result.level = builder.Build();
        result.warnings.AddRange(builder.Warnings);

        Reid(result.level, idPrefix ?? NewPrefix());
        return result;
    }

    /// <summary>
    /// A short unique stem for one generation's ids.
    ///
    /// PlanBuilder authors w_0, r_bath, o_3, f_7. Stable, readable, and identical every time. That
    /// is right for the samples, where each plan is its own document, and wrong the moment two
    /// stories of ONE home are both generated: HomeRenderer.Mark keeps a single flat dictionary
    /// across every element type, so a second w_0 would take the first one's place and selection
    /// would start picking the wrong wall.
    /// </summary>
    /// <summary>
    /// The rooms alone, put on the floor and checked: no openings, no furniture, no PlanBuilder.
    ///
    /// This is what the generator's first pass compiles. It stops short of building geometry on
    /// purpose: at this point there is nothing to host an opening and nothing to stand furniture in,
    /// so a full build would derive walls only to throw them away, and the full validator would report
    /// that every room in the plan has no way into it.
    /// </summary>
    public static SketchRoomsResult CompileRooms(SketchPlanSpec spec, SketchFrame frame,
                                                 float tolerance = SketchRegularizer.DefaultTolerance)
    {
        var result = new SketchRoomsResult();

        if (!frame.valid) { result.refusal = frame.reason; return result; }
        if (spec == null) { result.refusal = "The response had no plan in it."; return result; }

        result.rooms = Rects(spec, frame, tolerance, result.issues);
        result.issues.AddRange(SketchPlanValidator.CheckRooms(spec, result.rooms));

        if (result.rooms.Count == 0)
            result.refusal = "No usable rooms came back for this sketch.";

        return result;
    }

    /// <summary>Normalised image rectangles as world rectangles, with near-misses closed up.</summary>
    private static List<SketchRect> Rects(SketchPlanSpec spec, SketchFrame frame, float tolerance,
                                          List<string> issues)
    {
        var raw = new List<SketchRect>();
        foreach (var r in spec.Rooms)
        {
            if (r == null) continue;
            if (r.w <= 0 || r.h <= 0)
            {
                issues.Add($"Room '{r.key}' has no size on the sketch.");
                continue;
            }

            Vector2 a = frame.ToWorld(r.x, r.y);
            Vector2 b = frame.ToWorld(r.x + r.w, r.y + r.h);

            raw.Add(new SketchRect
            {
                key = r.key, name = r.name, roomType = r.roomType,
                statedWidth = r.widthMeters, statedDepth = r.depthMeters,
                x0 = Mathf.Min(a.x, b.x), x1 = Mathf.Max(a.x, b.x),
                z0 = Mathf.Min(a.y, b.y), z1 = Mathf.Max(a.y, b.y),
            });
        }

        ResolveParts(spec, raw);

        // Without this every near-miss becomes two parallel walls a few centimetres apart, which
        // nothing downstream would report.
        return SketchRegularizer.Snap(raw, tolerance, issues);
    }

    public static string NewPrefix() => "g" + System.Guid.NewGuid().ToString("N").Substring(0, 4) + "_";

    /// <summary>
    /// Points every rectangle at the room it belongs to, FLATTENING a chain of parts to its root.
    ///
    /// A part whose parent is itself a part is a mistake the validator reports, but it is not one to
    /// act on by dropping floor: the pieces are still one room and still meant to join, so following
    /// the chain to the room at the end of it keeps the shape. A cycle, or a parent that is not in
    /// the plan at all, leaves the rectangle owning itself: a whole room, which is the only other
    /// thing it could be.
    /// </summary>
    private static void ResolveParts(SketchPlanSpec spec, List<SketchRect> rects)
    {
        var parent = new Dictionary<string, string>();
        foreach (var r in spec.Rooms)
            if (r != null && r.IsPart && !string.IsNullOrWhiteSpace(r.key))
                parent[r.key] = r.partOf;

        if (parent.Count == 0) return;

        var byKey = new HashSet<string>();
        foreach (var r in rects) if (!string.IsNullOrWhiteSpace(r.key)) byKey.Add(r.key);

        for (int i = 0; i < rects.Count; i++)
        {
            var rect = rects[i];
            string at = rect.key;

            // Bounded by the number of rectangles, so a cycle exits rather than spinning.
            for (int hop = 0; hop < rects.Count; hop++)
            {
                if (!parent.TryGetValue(at, out string up)) break;
                if (string.IsNullOrWhiteSpace(up) || !byKey.Contains(up) || up == rect.key) { at = rect.key; break; }
                at = up;
            }

            rect.roomKey = at;
            rects[i] = rect;
        }
    }

    // -----------------------------------------------------------------------------------------

    private static void AddOpenings(PlanBuilder builder, SketchPlanSpec spec, HashSet<string> rooms)
    {
        foreach (var o in spec.Openings)
        {
            if (o == null) continue;

            string kind = Known(SketchPlanSpec.OpeningKinds, o.kind) ? o.kind : OpeningKind.Door;
            float width = o.widthMeters > 0f ? o.widthMeters : HomeConventions.DEFAULT_DOOR_WIDTH;
            float along = Mathf.Clamp01(o.alongFraction);

            if (o.IsInterior)
            {
                string a = o.between[0], b = o.between[1];
                if (!rooms.Contains(a) || !rooms.Contains(b) || a == b) continue;
                builder.DoorBetween(a, b, width, kind, 0f, along, o.heightMeters);
                continue;
            }

            if (string.IsNullOrWhiteSpace(o.room) || !rooms.Contains(o.room)) continue;
            if (!SketchEdge.TryParse(o.edge, out PlanEdge edge)) continue;

            if (kind == OpeningKind.Window)
                builder.Window(o.room, edge, along, width, o.heightMeters, o.sillMeters);
            else
                builder.ExteriorDoor(o.room, edge, along, width);
        }
    }

    private static void AddFurniture(PlanBuilder builder, SketchPlanSpec spec, HashSet<string> rooms)
    {
        foreach (var f in spec.Furniture)
        {
            if (f == null) continue;
            if (!SampleFurniture.Exists(f.catalogId)) continue;
            if (string.IsNullOrWhiteSpace(f.room) || !rooms.Contains(f.room)) continue;

            var item = SampleFurniture.Get(f.catalogId);
            string placement = f.placement;

            // A wall-mounted item placed any other way would be written into the furniture list and
            // render as a box on the floor. The validator says so; this makes it right anyway,
            // because the catalog already knows the answer.
            if (item.wallMounted) placement = SketchPlanSpec.SketchPlacement.Mount;
            else if (placement == SketchPlanSpec.SketchPlacement.Mount)
                placement = SketchPlanSpec.SketchPlacement.Against;

            if (placement == SketchPlanSpec.SketchPlacement.Free)
            {
                builder.Free(f.catalogId, f.room, Mathf.Clamp01(f.xFraction),
                             Mathf.Clamp01(f.zFraction), f.yawDegrees);
                continue;
            }

            if (!SketchEdge.TryParse(f.edge, out PlanEdge edge)) continue;
            float along = Mathf.Clamp01(f.alongFraction);

            if (placement == SketchPlanSpec.SketchPlacement.Mount)
                builder.Mount(f.catalogId, f.room, edge, along);
            else
                builder.Against(f.catalogId, f.room, edge, along, 0.02f, f.alongWall);
        }
    }

    /// <summary>
    /// Re-stems every id in the level, keeping every cross-reference pointing at the same thing.
    ///
    /// The wall map is the part that matters: OpeningDef.wallId and WallMountDef.wallId are the only
    /// references in a LevelDef, and an opening whose wallId no longer resolves is skipped by
    /// HomeRenderer without a word.
    /// </summary>
    public static void Reid(LevelDef level, string prefix)
    {
        if (level == null || string.IsNullOrEmpty(prefix)) return;

        var wallIds = new Dictionary<string, string>();
        foreach (var w in level.walls ?? new List<WallDef>())
        {
            string next = prefix + w.id;
            wallIds[w.id] = next;
            w.id = next;
        }

        foreach (var o in level.openings ?? new List<OpeningDef>())
        {
            o.id = prefix + o.id;
            if (o.wallId != null && wallIds.TryGetValue(o.wallId, out string w)) o.wallId = w;
        }

        foreach (var m in level.wallMounted ?? new List<WallMountDef>())
        {
            m.instanceId = prefix + m.instanceId;
            if (m.wallId != null && wallIds.TryGetValue(m.wallId, out string w)) m.wallId = w;
        }

        foreach (var r in level.rooms ?? new List<RoomDef>()) r.id = prefix + r.id;
        foreach (var f in level.furniture ?? new List<ObjectInstance>()) f.instanceId = prefix + f.instanceId;
    }

    private static bool Known(IReadOnlyList<string> set, string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var s in set) if (s == value) return true;
        return false;
    }
}
