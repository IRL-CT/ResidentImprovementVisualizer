using System.Collections.Generic;
using UnityEngine;

// What is wrong with a plan the model just sent back, in sentences a person can read.
//
// This follows OpeningFit's convention rather than throwing: every problem becomes a line of text
// written to be shown verbatim, because these strings have two audiences and both need prose. They
// go back to the model as the repair turn: where "bedroom 2's door names a room that does not
// exist" is actionable and an exception type is not, and they go to the user in the rail when the
// repair turn does not fix them.
//
// It deliberately overlaps with PlanBuilder.Warnings rather than deferring to it. PlanBuilder
// reports what it could not RESOLVE while deriving geometry; this reports what is wrong with the
// REQUEST, before any geometry exists, which is both earlier and more specific. A door between two
// rooms that do not touch is one warning from PlanBuilder and a nameable mistake here.
//
// The schema already carries the enums (room types, catalog ids, opening kinds), so a first response
// cannot get those wrong. They are checked anyway, because the repair turn is a second response and
// because a plan that arrives some other way (a hand-edited file, a future importer) must not walk
// straight into PlanBuilder.
//
// CheckRooms IS SPLIT OUT ON PURPOSE. The generator reads a plan in two passes, and the first has
// only rooms in it, so the half of this file that needs no openings has to be callable on its own.
// It is also the half worth scoring candidates on: every opening and every item is addressed by a
// room key, so a room read wrongly takes the rest of the plan with it.
public static class SketchPlanValidator
{
    /// <summary>Two rooms must share at least this much wall before a door between them makes sense.</summary>
    private const float MIN_SHARED_EDGE = 0.30f;

    /// <summary>Rectangles are on centerlines and snapped to the millimetre, so contact is exact.</summary>
    private const float TOUCH = 0.01f;

    /// <summary>More overlap than this and two rooms are occupying the same floor.</summary>
    private const float MAX_OVERLAP_AREA = 0.05f;

    private const float MIN_FOOTPRINT = 10f;
    private const float MAX_FOOTPRINT = 1000f;
    private const float MAX_OPENING_WIDTH = 2.5f;

    /// <summary>
    /// How far a stated size may sit from the derived one before the two count as disagreeing.
    ///
    /// Deliberately loose. The rectangle carries the regularizer's whole envelope. About 0.15 m per
    /// coordinate, and the stated figure is an estimate off a drawing, so a tight bound would fire on
    /// honest readings constantly and spend the repair turn on noise. What this is for is the gross
    /// failure the normalised coordinates cannot show on their own: a plan traced into part of the
    /// 0-1000 range, or a room measured against the wrong dimension line. Those are wrong by a factor,
    /// not by a few centimetres.
    /// </summary>
    private const float SIZE_SLACK_METERS = 0.5f;
    private const float SIZE_SLACK_FRACTION = 0.25f;

    /// <summary>Openings you can walk through. A window is not a way into a room.</summary>
    private static bool IsPassable(string kind)
        => kind == OpeningKind.Door || kind == OpeningKind.PassThrough || kind == OpeningKind.CasedOpening;

    public static List<string> Check(SketchPlanSpec spec, IReadOnlyList<SketchRect> rooms,
                                     float ceilingHeight)
    {
        var issues = new List<string>();
        if (spec == null) { issues.Add("The response had no plan in it."); return issues; }

        if (rooms == null || rooms.Count == 0)
        {
            issues.Add("No rooms were found in the sketch. Every plan needs at least one room.");
            return issues;
        }

        float ceiling = ceilingHeight > 0f ? ceilingHeight : ResidenceConventions.DEFAULT_CEILING_HEIGHT;

        var byKey = RoomsByKey(rooms, issues);
        RoomIssues(spec, rooms, byKey, issues);

        CheckOpenings(spec, byKey, ceiling, issues);
        CheckFurniture(spec, byKey, issues);
        CheckReachable(spec, byKey, issues);

        return issues;
    }

    /// <summary>
    /// Everything that can be said about a plan's rooms without seeing its openings or its furniture.
    ///
    /// This is what the first generation pass is scored on. A room list with no overlaps, no slivers,
    /// no orphaned pieces and no size it disagrees with itself about is one the second pass can
    /// address by key; anything else takes the rest of the plan down with it.
    /// </summary>
    public static List<string> CheckRooms(SketchPlanSpec spec, IReadOnlyList<SketchRect> rooms)
    {
        var issues = new List<string>();

        if (rooms == null || rooms.Count == 0)
        {
            issues.Add("No rooms were found in the sketch. Every plan needs at least one room.");
            return issues;
        }

        var byKey = RoomsByKey(rooms, issues);
        RoomIssues(spec, rooms, byKey, issues);
        return issues;
    }

    // -----------------------------------------------------------------------------------------

    private static void RoomIssues(SketchPlanSpec spec, IReadOnlyList<SketchRect> rooms,
                                   Dictionary<string, SketchRect> byKey, List<string> issues)
    {
        CheckFootprint(rooms, issues);
        CheckOverlaps(rooms, issues);
        CheckSlivers(rooms, issues);
        CheckParts(spec, rooms, byKey, issues);
        CheckStatedSize(rooms, issues);
    }

    private static Dictionary<string, SketchRect> RoomsByKey(IReadOnlyList<SketchRect> rooms,
                                                             List<string> issues)
    {
        var byKey = new Dictionary<string, SketchRect>(rooms.Count);

        foreach (var r in rooms)
        {
            if (string.IsNullOrWhiteSpace(r.key))
            {
                issues.Add("A room was sent with no key. Every room needs a short unique key.");
                continue;
            }

            if (byKey.ContainsKey(r.key))
            {
                issues.Add($"Two rooms share the key '{r.key}'. Room keys have to be unique.");
                continue;
            }

            if (!Known(RoomFinish.All, r.roomType))
                issues.Add($"Room '{r.key}' has the type '{r.roomType}', which is not one of the "
                         + "twelve room types.");

            if (string.IsNullOrWhiteSpace(r.name))
                issues.Add($"Room '{r.key}' has no name.");

            byKey[r.key] = r;
        }

        return byKey;
    }

    private static void CheckFootprint(IReadOnlyList<SketchRect> rooms, List<string> issues)
    {
        float total = 0f;
        foreach (var r in rooms) total += r.Area;

        if (total < MIN_FOOTPRINT)
            issues.Add($"The whole plan comes to {total:0.0} m², which is too small to be a dwelling. "
                     + "The room rectangles are probably not covering the sketch.");
        else if (total > MAX_FOOTPRINT)
            issues.Add($"The whole plan comes to {total:0} m², which is far larger than a residence. "
                     + "The room rectangles are probably not in 0-1000 image units.");
    }

    private static void CheckOverlaps(IReadOnlyList<SketchRect> rooms, List<string> issues)
    {
        for (int i = 0; i < rooms.Count; i++)
        for (int j = i + 1; j < rooms.Count; j++)
        {
            float area = OverlapArea(rooms[i], rooms[j]);
            if (area <= MAX_OVERLAP_AREA) continue;

            issues.Add($"'{rooms[i].key}' and '{rooms[j].key}' overlap by {area:0.0} m². Rooms have to "
                     + "tile the plan. They can share an edge, but they cannot sit on top of "
                     + "each other.");
        }
    }

    /// <summary>
    /// Two rooms facing each other across a gap too narrow to be anything.
    ///
    /// This is the failure the whole regularizer pass exists to prevent, caught on the far side of it.
    /// Snapping closes anything under its tolerance; what survives is a pair of boundaries far enough
    /// apart that the clustering was right to leave them alone and close enough that no building has
    /// them, and PlanBuilder will duly derive two parallel walls with a void between them. Nothing
    /// downstream says a word about that: both walls render, RoomRegions finds no face in the void, and
    /// on screen it reads as a wall drawn twice.
    ///
    /// The bar is SketchRegularizer.MinGenuineSeparation, which is the tightest separation any shipped
    /// plan actually expresses. Above it, a narrow gap could be a chase or a shallow closet the drawing
    /// shows and this code cannot see, so it says nothing.
    /// </summary>
    private static void CheckSlivers(IReadOnlyList<SketchRect> rooms, List<string> issues)
    {
        for (int i = 0; i < rooms.Count; i++)
        for (int j = i + 1; j < rooms.Count; j++)
        {
            // Two pieces of one room are allowed to be apart here. CheckParts owns that question,
            // and reporting it twice would spend the repair turn saying the same thing.
            if (rooms[i].Room == rooms[j].Room) continue;
            if (OverlapArea(rooms[i], rooms[j]) > MAX_OVERLAP_AREA) continue;
            if (!FacingGap(rooms[i], rooms[j], out float gap)) continue;
            if (gap <= TOUCH || gap >= SketchRegularizer.MinGenuineSeparation) continue;

            issues.Add($"'{rooms[i].key}' and '{rooms[j].key}' face each other across a {gap * 100f:0} cm "
                     + "gap, which is too narrow to be anything. It would build as two walls with a "
                     + "void between them. Either they share a wall, in which case give the shared edge "
                     + "the SAME coordinate in both, or something belongs in between.");
        }
    }

    /// <summary>
    /// The pieces of an L-shaped room have to name a real room and actually join it.
    ///
    /// Both failures are silent otherwise. A piece naming a room that is not in the plan owns itself
    /// instead, so the drawing gains a room nobody meant and a wall where the plan shows none; a piece
    /// that touches its room only at a corner, or not at all, is a floor PlanBuilder cannot draw as one
    /// shape and will fall back on.
    /// </summary>
    private static void CheckParts(SketchPlanSpec spec, IReadOnlyList<SketchRect> rooms,
                                   Dictionary<string, SketchRect> byKey, List<string> issues)
    {
        if (spec != null)
        {
            foreach (var r in spec.Rooms)
            {
                if (r == null || !r.IsPart) continue;
                if (byKey.ContainsKey(r.partOf)) continue;

                issues.Add($"Room '{r.key}' is a piece of '{r.partOf}', which is not a room in this "
                         + "plan. \"partOf\" has to name another room's key.");
            }
        }

        // Group the rectangles by the room they belong to, then check each group hangs together.
        var groups = new Dictionary<string, List<SketchRect>>();
        foreach (var r in rooms)
        {
            if (string.IsNullOrWhiteSpace(r.key)) continue;
            if (!groups.TryGetValue(r.Room, out var list)) groups[r.Room] = list = new List<SketchRect>();
            list.Add(r);
        }

        foreach (var kv in groups)
        {
            var parts = kv.Value;
            if (parts.Count < 2) continue;

            // Connected through shared edges, not merely through touching corners: a corner meeting
            // pinches the room to nothing and is not a shape that can be walked through.
            var reached = new HashSet<string> { parts[0].key };
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var a in parts)
                {
                    if (!reached.Contains(a.key)) continue;
                    foreach (var b in parts)
                    {
                        if (reached.Contains(b.key)) continue;
                        if (SharedEdge(a, b) < MIN_SHARED_EDGE) continue;
                        reached.Add(b.key);
                        grew = true;
                    }
                }
            }

            if (reached.Count == parts.Count) continue;

            foreach (var p in parts)
            {
                if (reached.Contains(p.key)) continue;
                issues.Add($"Room '{p.key}' is a piece of '{kv.Key}' but does not share a wall with the "
                         + "rest of it. Pieces of one room have to meet along an edge. If this is "
                         + "somewhere separate, make it its own room with a door.");
            }
        }
    }

    /// <summary>
    /// The rectangle and the stated measurement have to describe the same room.
    ///
    /// They come from different places on purpose: the rectangle from normalised image coordinates,
    /// the measurement from reading the drawing, so a disagreement is the one signal available that
    /// the coordinates are wrong at a scale the plan's own internal consistency cannot reveal.
    /// </summary>
    private static void CheckStatedSize(IReadOnlyList<SketchRect> rooms, List<string> issues)
    {
        foreach (var r in rooms)
        {
            if (string.IsNullOrWhiteSpace(r.key)) continue;

            Disagrees(r.key, "wide", r.Width, r.statedWidth, issues);
            Disagrees(r.key, "deep", r.Depth, r.statedDepth, issues);
        }
    }

    private static void Disagrees(string key, string axis, float derived, float stated,
                                  List<string> issues)
    {
        if (stated <= 0f) return;      // not stated

        float slack = Mathf.Max(SIZE_SLACK_METERS, SIZE_SLACK_FRACTION * stated);
        if (Mathf.Abs(derived - stated) <= slack) return;

        issues.Add($"Room '{key}' is {derived:0.00} m {axis} where it sits on the sketch, but you said "
                 + $"it measures {stated:0.00} m. One of the two is wrong. Check the room's corners "
                 + "against the 0-1000 grid, and check the whole plan is using the full range.");
    }

    private static void CheckOpenings(SketchPlanSpec spec, Dictionary<string, SketchRect> byKey,
                                      float ceiling, List<string> issues)
    {
        int n = 0;
        foreach (var o in spec.Openings)
        {
            n++;
            string what = $"Opening {n}";

            if (!Known(SketchPlanSpec.OpeningKinds, o.kind))
                issues.Add($"{what} has the kind '{o.kind}', which is not a door, window, "
                         + "pass_through or cased_opening.");

            if (o.between != null && o.between.Count != 0 && o.between.Count != 2)
            {
                issues.Add($"{what} lists {o.between.Count} rooms in \"between\". It has to name "
                         + "exactly two, or none at all for an opening in an exterior wall.");
            }
            else if (o.IsInterior)
            {
                string a = o.between[0], b = o.between[1];
                if (!byKey.ContainsKey(a) || !byKey.ContainsKey(b))
                {
                    issues.Add($"{what} is between '{a}' and '{b}', and "
                             + $"{Missing(byKey, a, b)} is not a room in this plan.");
                }
                else if (a == b)
                {
                    issues.Add($"{what} is between '{a}' and itself.");
                }
                else if (byKey[a].Room == byKey[b].Room)
                {
                    // Two pieces of one room. There is no wall between them to put a door in: that is
                    // the whole reason they were declared as pieces rather than as rooms.
                    issues.Add($"{what} is between '{a}' and '{b}', which are two pieces of the same "
                             + $"room ('{byKey[a].Room}'). There is no wall between them to open.");
                }
                else if (SharedEdge(byKey[a], byKey[b]) < MIN_SHARED_EDGE)
                {
                    issues.Add($"{what} is between '{a}' and '{b}', but those two rooms do not share "
                             + "a wall. An opening can only go where two rooms actually touch.");
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(o.room))
                    issues.Add($"{what} names neither two rooms in \"between\" nor one room in "
                             + "\"room\", so there is nowhere to put it.");
                else if (!byKey.ContainsKey(o.room))
                    issues.Add($"{what} is in room '{o.room}', which is not a room in this plan.");
                else if (!SketchEdge.TryParse(o.edge, out _))
                    issues.Add($"{what} is in an exterior wall of '{o.room}' but its edge is "
                             + $"'{o.edge}'. It has to be south, east, north or west.");
            }

            if (o.alongFraction < -0.001f || o.alongFraction > 1.001f)
                issues.Add($"{what} sits at {o.alongFraction:0.00} along its wall. That has to be "
                         + "between 0 and 1.");

            if (o.widthMeters <= 0f)
                issues.Add($"{what} has no width.");
            else if (o.widthMeters > MAX_OPENING_WIDTH)
                issues.Add($"{what} is {o.widthMeters:0.00} m wide, which is wider than any opening "
                         + "in a residence. Widths are in meters.");

            if (o.sillMeters < 0f)
                issues.Add($"{what} has a sill below the floor.");

            float height = o.heightMeters > 0f ? o.heightMeters : ResidenceConventions.DEFAULT_DOOR_HEIGHT;
            if (o.sillMeters + height > ceiling + 0.001f)
                issues.Add($"{what} reaches {o.sillMeters + height:0.00} m, which is through a "
                         + $"{ceiling:0.00} m ceiling.");
        }
    }

    private static void CheckFurniture(SketchPlanSpec spec, Dictionary<string, SketchRect> byKey,
                                       List<string> issues)
    {
        int n = 0;
        foreach (var f in spec.Furniture)
        {
            n++;
            string what = $"Item {n} ('{f.catalogId}')";

            if (!SampleFurniture.Exists(f.catalogId))
            {
                issues.Add($"{what} is not a catalog item.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(f.room) || !byKey.ContainsKey(f.room))
            {
                issues.Add($"{what} is in room '{f.room}', which is not a room in this plan.");
                continue;
            }

            bool needsEdge = f.placement == SketchPlanSpec.SketchPlacement.Against
                          || f.placement == SketchPlanSpec.SketchPlacement.Mount;

            if (!Known(SketchPlanSpec.SketchPlacement.All, f.placement))
            {
                issues.Add($"{what} has the placement '{f.placement}'. It has to be against, free "
                         + "or mount.");
            }
            else if (needsEdge && !SketchEdge.TryParse(f.edge, out _))
            {
                issues.Add($"{what} is placed '{f.placement}' but its edge is '{f.edge}'. It has to be "
                         + "south, east, north or west.");
            }

            // Whether an item hangs on a wall or stands on the floor is not a judgement call: the
            // catalog already knows, and SketchPlanCompiler corrects a mismatch on the way through.
            // Reporting it here as well would spend a repair turn asking the model to fix something
            // that is already fixed, and two computations of one truth is how this codebase gets its
            // notches.

            if (needsEdge && (f.alongFraction < -0.001f || f.alongFraction > 1.001f))
                issues.Add($"{what} sits at {f.alongFraction:0.00} along its wall. That has to be "
                         + "between 0 and 1.");

            if (f.placement == SketchPlanSpec.SketchPlacement.Free
                && (f.xFraction < -0.001f || f.xFraction > 1.001f
                 || f.zFraction < -0.001f || f.zFraction > 1.001f))
            {
                issues.Add($"{what} sits at ({f.xFraction:0.00}, {f.zFraction:0.00}) in its room. "
                         + "Both have to be between 0 and 1.");
            }
        }
    }

    /// <summary>
    /// Every room has a way in, and the ways in join up.
    ///
    /// Two failures, and they are worth separating because they read differently to whoever has to
    /// fix them. A room with no passable opening at all is an isolated box: the plan still renders
    /// perfectly, four walls and a floor, and the mistake only shows when somebody tries to walk
    /// through it. A room that HAS doors but whose doors lead only to other rooms in the same
    /// predicament is a wing of the house nobody can reach from the front door, which is the same
    /// mistake one level up and completely invisible per-opening.
    ///
    /// A WINDOW IS NOT A WAY IN, and that is the correction this check most needed: it used to count
    /// every opening regardless of kind, so a bedroom with a window and no door passed while the
    /// sentence it would have printed said "no door, pass-through or cased opening".
    ///
    /// When nothing opens to the outside at all, the plan is not condemned: an upper story has no
    /// exterior door and stairs are not modelled here. What is still required in that case is that the
    /// rooms form ONE connected group rather than two unrelated islands.
    /// </summary>
    private static void CheckReachable(SketchPlanSpec spec, Dictionary<string, SketchRect> byKey,
                                       List<string> issues)
    {
        var served = new HashSet<string>();       // rooms with at least one passable opening
        var rooted = new HashSet<string>();       // rooms with a passable opening to the outside
        var links = new List<KeyValuePair<string, string>>();

        foreach (var o in spec.Openings)
        {
            if (!IsPassable(o.kind)) continue;

            if (o.IsInterior)
            {
                if (!byKey.TryGetValue(o.between[0] ?? "", out var a)) continue;
                if (!byKey.TryGetValue(o.between[1] ?? "", out var b)) continue;
                if (a.Room == b.Room) continue;

                served.Add(a.Room);
                served.Add(b.Room);
                links.Add(new KeyValuePair<string, string>(a.Room, b.Room));
            }
            else if (!string.IsNullOrWhiteSpace(o.room) && byKey.TryGetValue(o.room, out var only))
            {
                served.Add(only.Room);
                rooted.Add(only.Room);
            }
        }

        var allRooms = new List<string>();
        foreach (var r in byKey.Values) if (!allRooms.Contains(r.Room)) allRooms.Add(r.Room);

        var isolated = new HashSet<string>();
        foreach (string room in allRooms)
        {
            if (served.Contains(room)) continue;
            isolated.Add(room);
            issues.Add($"Nothing opens into '{room}'. It has no door, pass-through or cased opening, "
                     + "so there is no way in.");
        }

        // Flood out from whatever counts as a starting point: the front door if there is one, and
        // otherwise the first room, since a plan with no way out still has to hang together.
        var start = new List<string>();
        foreach (string room in allRooms) if (rooted.Contains(room)) start.Add(room);

        bool hasOutside = start.Count > 0;
        if (!hasOutside)
        {
            foreach (string room in allRooms)
            {
                if (isolated.Contains(room)) continue;
                start.Add(room);
                break;
            }
        }
        if (start.Count == 0) return;

        var reached = new HashSet<string>(start);
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var link in links)
            {
                if (reached.Contains(link.Key) && reached.Add(link.Value)) grew = true;
                if (reached.Contains(link.Value) && reached.Add(link.Key)) grew = true;
            }
        }

        foreach (string room in allRooms)
        {
            if (reached.Contains(room) || isolated.Contains(room)) continue;

            issues.Add(hasOutside
                ? $"'{room}' has doors, but none of them lead back to a way out of the residence. Every "
                + "room has to connect, through other rooms, to a door in an exterior wall."
                : $"'{room}' is cut off from the rest of the plan. Its doors only reach rooms that "
                + "are cut off too. Every room has to connect to the others.");
        }
    }

    // -----------------------------------------------------------------------------------------

    private static bool Known(IReadOnlyList<string> set, string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var s in set) if (s == value) return true;
        return false;
    }

    private static string Missing(Dictionary<string, SketchRect> byKey, string a, string b)
    {
        if (!byKey.ContainsKey(a) && !byKey.ContainsKey(b)) return $"neither '{a}' nor '{b}'";
        return byKey.ContainsKey(a) ? $"'{b}'" : $"'{a}'";
    }

    private static float OverlapArea(SketchRect a, SketchRect b)
    {
        float w = Mathf.Min(a.x1, b.x1) - Mathf.Max(a.x0, b.x0);
        float d = Mathf.Min(a.z1, b.z1) - Mathf.Max(a.z0, b.z0);
        return w <= 0f || d <= 0f ? 0f : w * d;
    }

    /// <summary>
    /// The gap between two rectangles that face each other along a real length of wall, or false.
    ///
    /// "Facing" is the load-bearing half: two rooms at opposite corners of a plan are far apart in both
    /// axes and share no wall line, so the distance between them means nothing. Only a pair that is
    /// separated in ONE axis while overlapping in the other has a gap worth measuring.
    /// </summary>
    private static bool FacingGap(SketchRect a, SketchRect b, out float gap)
    {
        gap = 0f;

        float zOverlap = Mathf.Min(a.z1, b.z1) - Mathf.Max(a.z0, b.z0);
        if (zOverlap >= MIN_SHARED_EDGE)
        {
            if (a.x1 <= b.x0) { gap = b.x0 - a.x1; return true; }
            if (b.x1 <= a.x0) { gap = a.x0 - b.x1; return true; }
        }

        float xOverlap = Mathf.Min(a.x1, b.x1) - Mathf.Max(a.x0, b.x0);
        if (xOverlap >= MIN_SHARED_EDGE)
        {
            if (a.z1 <= b.z0) { gap = b.z0 - a.z1; return true; }
            if (b.z1 <= a.z0) { gap = a.z0 - b.z1; return true; }
        }

        return false;
    }

    /// <summary>How much wall two rooms share, or 0 if they do not touch.</summary>
    public static float SharedEdge(SketchRect a, SketchRect b)
    {
        bool verticalContact = Mathf.Abs(a.x1 - b.x0) <= TOUCH || Mathf.Abs(a.x0 - b.x1) <= TOUCH;
        if (verticalContact)
        {
            float overlap = Mathf.Min(a.z1, b.z1) - Mathf.Max(a.z0, b.z0);
            if (overlap > 0f) return overlap;
        }

        bool horizontalContact = Mathf.Abs(a.z1 - b.z0) <= TOUCH || Mathf.Abs(a.z0 - b.z1) <= TOUCH;
        if (horizontalContact)
        {
            float overlap = Mathf.Min(a.x1, b.x1) - Mathf.Max(a.x0, b.x0);
            if (overlap > 0f) return overlap;
        }

        return 0f;
    }
}
