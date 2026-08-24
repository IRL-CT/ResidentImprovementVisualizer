using System;
using System.Collections.Generic;
using UnityEngine;

// Links walls into a network at commit time: where a newly drawn wall crosses or T-joins an existing
// one, BOTH are split so every junction is a shared endpoint. This is FenceLinker (Assets/Scripts/
// Authoring/FenceLinker.cs) re-expressed for walls: same rules, different container and one large
// addition.
//
// Why it has to exist at all: WallMeshBuilder.ComputeExtensions closes a corner by extending each wall
// half a neighbor-thickness past any endpoint that COINCIDES WITHIN ~1 mm. Two walls that merely
// cross, with no shared endpoint, get no extension at either side of the crossing, so the plan renders
// with a notch and nothing anywhere reports a problem. WallLayout also silently clamps, and HomeRenderer
// silently skips, which is the same reason PlanBuilder exists for the authored samples. This is that
// guarantee for walls a user draws.
//
// Rules carried over from FenceLinker unchanged, each earned the hard way:
//   * A junction is welded onto a nearby vertex of BOTH sides, so they split at a bit-identical point.
//   * A junction landing on a wall's own endpoint is a shared corner, not a cut.
//   * Cuts that would leave a piece shorter than MinSeg are skipped: the wall stays whole.
//   * Parallel or collinear contact is NOT a junction. Drawing along an existing wall must not chop it.
//   * Piece 0 keeps the original id, so VariantDiff reads a split as "wall shortened", not
//     "wall deleted + wall added".
//
// The addition fences have no analogue for: a wall CARRIES things. OpeningDef.offset and
// WallMountDef.offset are absolute meters along a -> b, so every split has to re-home them onto the
// right piece. And because a wall drawn through a doorway is a real thing a user will do, a cut that
// lands inside an opening is refused outright rather than bisecting the door: the wall T-joins at the
// jamb instead.
public static class WallLinker
{
    // Detection radius: "is this a junction?". Deliberately tighter than FenceLinker's 0.05 because
    // half a wall thickness is 0.057 and a coarser radius would weld junctions across the wall body.
    // WallSnapping already lands the user's click on an existing endpoint within 0.35 m, so this only
    // has to absorb float error and a Shift-held free-draw.
    public const float ContactEps = 0.02f;

    // The OUTPUT precision floor. Must not exceed WallMeshBuilder.Near (EPS*EPS*100 as a sqrMagnitude,
    // i.e. 1 mm) or corners stop welding.
    public const float WeldEps = HomeConventions.EPS * 10f;

    // A 100 mm stub is a legitimate pilaster or door return; below that it is junk that renders as a
    // cube. FenceLinker uses 0.5 because a fence panel is ~2 m.
    public const float MinSeg = 0.10f;

    // sin of the minimum contact angle for a T-junction; shallower is collinear overlap.
    public const float MinJunctionSin = 0.1f;   // ~5.7 degrees

    // Solid wall required beside an opening before a cut there counts as "clear of the door".
    public const float MinEdge = 0.05f;

    public struct Options
    {
        public float contactEps;
        public float minSeg;
        public float minJunctionSin;
        public float minEdge;

        public static Options Default => new Options
        {
            contactEps = ContactEps,
            minSeg = MinSeg,
            minJunctionSin = MinJunctionSin,
            minEdge = MinEdge,
        };
    }

    /// <summary>What <see cref="Link"/> would do, without mutating anything. Drives the draw ghost.</summary>
    public struct Plan
    {
        public List<Vector2> junctions;            // where posts/corners will land
        public List<string> wallsThatWouldSplit;
        public bool duplicatesExisting;            // the whole run already exists as collinear wall
    }

    // =======================================================================================
    // Preview
    // =======================================================================================

    public static Plan Preview(LevelDef level, IReadOnlyList<Vector2> chain, Options o)
    {
        var plan = new Plan
        {
            junctions = new List<Vector2>(),
            wallsThatWouldSplit = new List<string>(),
        };

        var pts = Normalize(chain, o.contactEps);
        if (level?.walls == null || pts.Count < 2) return plan;

        var contacts = Collect(level, pts, null, o);
        plan.junctions.AddRange(contacts.reps);

        foreach (var kv in contacts.wallCuts)
            if (SurvivingCuts(level, FindWall(level, kv.Key), kv.Value, o, null).Count > 0)
                plan.wallsThatWouldSplit.Add(kv.Key);

        // Every piece already covered => drawing this adds nothing.
        bool anyNew = false;
        for (int i = 0; i < pts.Count - 1 && !anyNew; i++)
            if (Uncovered(level, pts[i], pts[i + 1], o).Count > 0) anyNew = true;
        plan.duplicatesExisting = !anyNew;

        return plan;
    }

    // =======================================================================================
    // Link: the commit
    // =======================================================================================

    /// <summary>
    /// Splits every existing wall the chain crosses or T-joins, splits the chain at each junction, and
    /// appends the resulting walls copying <paramref name="template"/>. Returns the walls added.
    /// The caller wraps the whole call in ONE undo snapshot.
    /// </summary>
    public static List<WallDef> Link(LevelDef level, IReadOnlyList<Vector2> chain, WallDef template,
                                     Options o, List<string> warnings = null)
    {
        var added = new List<WallDef>();
        var pts = Normalize(chain, o.contactEps);
        if (level == null || pts.Count < 2) return added;
        level.walls ??= new List<WallDef>();

        var contacts = Collect(level, pts, null, o);

        // 1. Split the existing walls first, so the candidates are measured against the final graph.
        foreach (var kv in contacts.wallCuts)
        {
            var w = FindWall(level, kv.Key);
            if (w != null) SplitWall(level, w, kv.Value, o, warnings);
        }

        // 2. Split each candidate at its own junctions, then emit only the parts not already walled.
        for (int i = 0; i < pts.Count - 1; i++)
        {
            Vector2 a = pts[i], b = pts[i + 1];
            float len = (b - a).magnitude;
            if (len < o.minSeg) continue;
            Vector2 dir = (b - a) / len;

            var breaks = new List<float>();
            if (contacts.segCuts.TryGetValue(i, out var cuts))
                foreach (var p in cuts)
                {
                    float s = Vector2.Dot(p - a, dir);
                    if (s > o.minSeg && s < len - o.minSeg) breaks.Add(s);
                }

            foreach (var piece in Spans.Split(new Vector2(0f, len), breaks))
            {
                Vector2 pa = a + dir * piece.x;
                Vector2 pb = a + dir * piece.y;

                // Weld the piece ends back onto the canonical junction points so the wall we emit
                // shares the EXACT coordinate the split above wrote into its neighbor.
                pa = Segments.Weld(pa, contacts.reps, o.contactEps);
                pb = Segments.Weld(pb, contacts.reps, o.contactEps);

                foreach (var gap in Uncovered(level, pa, pb, o))
                {
                    Vector2 ga = pa + (pb - pa).normalized * gap.x;
                    Vector2 gb = pa + (pb - pa).normalized * gap.y;
                    if ((gb - ga).magnitude < o.minSeg) continue;

                    ga = Segments.Weld(ga, contacts.reps, o.contactEps);
                    gb = Segments.Weld(gb, contacts.reps, o.contactEps);

                    var def = CopyOf(template);
                    def.id = Guid.NewGuid().ToString();
                    Segments.SetEnds(def, ga, gb);
                    level.walls.Add(def);
                    added.Add(def);
                }
            }
        }

        if (added.Count == 0 && warnings != null && contacts.wallCuts.Count == 0)
            warnings.Add("Nothing drawn. A wall already runs along that line.");

        return added;
    }

    // =======================================================================================
    // SplitWall: the workhorse
    // =======================================================================================

    /// <summary>
    /// Splits <paramref name="wall"/> at each point in <paramref name="at"/> that survives the sliver,
    /// shared-corner and doorway rules. Piece 0 keeps the wall's id and object identity; the rest are
    /// inserted straight after it. Openings and wall mounts are re-homed onto the right piece.
    /// Returns every piece, in order along a -> b (just the wall itself when nothing was split).
    /// </summary>
    public static List<WallDef> SplitWall(LevelDef level, WallDef wall, IReadOnlyList<Vector2> at,
                                          Options o, List<string> warnings = null)
    {
        var pieces = new List<WallDef> { wall };
        if (level?.walls == null || !Segments.TryEnds(wall, out Vector2 a, out Vector2 b)) return pieces;

        var arcs = SurvivingCuts(level, wall, at, o, warnings);
        if (arcs.Count == 0) return pieces;

        float len = (b - a).magnitude;
        Vector2 dir = (b - a) / len;

        // Snapshot what the wall carries BEFORE any mutation.
        var openings = new List<OpeningDef>();
        if (level.openings != null)
            foreach (var op in level.openings)
                if (op != null && op.wallId == wall.id) openings.Add(op);

        var mounts = new List<WallMountDef>();
        if (level.wallMounted != null)
            foreach (var m in level.wallMounted)
                if (m != null && m.wallId == wall.id) mounts.Add(m);

        // Boundaries: 0, each cut, len. Cut points come from the caller's welded representatives, so
        // reconstruct each boundary POINT from the original list rather than from a*dir arithmetic,
        // that is what keeps the shared endpoint bit-identical with the neighbor's.
        var points = new List<Vector2> { a };
        for (int i = 0; i < arcs.Count; i++)
            points.Add(Segments.Weld(a + dir * arcs[i], at, o.contactEps));
        points.Add(b);

        // Derive the arc bounds FROM the welded points, not from the raw cut arcs: the weld may have
        // nudged a boundary by up to contactEps onto a neighbor's exact vertex, and an opening
        // re-homed against the un-nudged number would land off by that much.
        var bounds = new List<float>(points.Count);
        foreach (var p in points) bounds.Add(Vector2.Dot(p - a, dir));
        bounds[0] = 0f;
        bounds[bounds.Count - 1] = len;

        // Piece 0 reuses the original object: id, materials, thickness and its place in
        // level.walls all survive untouched, which is what VariantDiff needs.
        Segments.SetEnds(wall, points[0], points[1]);
        pieces.Clear();
        pieces.Add(wall);

        int insertAt = level.walls.IndexOf(wall);
        for (int k = 1; k < bounds.Count - 1; k++)
        {
            var piece = CopyOf(wall);
            piece.id = Guid.NewGuid().ToString();
            Segments.SetEnds(piece, points[k], points[k + 1]);
            if (insertAt >= 0) level.walls.Insert(insertAt + k, piece);
            else level.walls.Add(piece);
            pieces.Add(piece);
        }

        // --- re-home what the wall carried ---------------------------------------------------
        // Two passes: assign every opening to its piece first, THEN fit. OpeningFit gathers siblings
        // by wallId, so fitting mid-assignment would compare against the wrong neighbors.
        foreach (var op in openings)
        {
            int k = PieceIndex(bounds, op.offset);
            op.wallId = pieces[k].id;
            op.offset -= bounds[k];
        }

        foreach (var op in openings)
        {
            var host = FindWall(level, op.wallId);
            if (host == null) continue;
            var fit = OpeningFit.Fit(op, host, level, op.offset);
            if (!fit.ok)
            {
                level.openings.Remove(op);
                warnings?.Add($"Removed a {Pretty(op.kind)}. It no longer fits its wall. {fit.reason}");
                continue;
            }
            if (fit.clamped)
            {
                op.offset = fit.offset;
                warnings?.Add($"Moved a {Pretty(op.kind)} to keep it on its wall.");
            }
        }

        foreach (var m in mounts)
        {
            int k = PieceIndex(bounds, m.offset);
            m.wallId = pieces[k].id;
            m.offset = Mathf.Clamp(m.offset - bounds[k], 0f, bounds[k + 1] - bounds[k]);
        }

        return pieces;
    }

    // =======================================================================================
    // Relink. Restore the invariant after a drag
    // =======================================================================================

    /// <summary>
    /// Splits every surviving crossing and T-junction among the level's walls. Idempotent: a second
    /// call changes nothing, which matters because this runs on every drag-release and churning ids
    /// would make VariantDiff read a move as a delete plus an add. Returns the number of splits made.
    /// </summary>
    public static int Relink(LevelDef level, Options o, List<string> warnings = null)
    {
        if (level?.walls == null || level.walls.Count < 2) return 0;

        int splits = 0;
        // Bounded: each pass either splits something (raising the wall count) or terminates. The cap
        // exists so a pathological geometry cannot spin here during a drag.
        for (int pass = 0; pass < 8; pass++)
        {
            var reps = new List<Vector2>();
            var cuts = new Dictionary<string, List<Vector2>>();

            for (int i = 0; i < level.walls.Count; i++)
            {
                if (!Segments.TryEnds(level.walls[i], out Vector2 a0, out Vector2 b0)) continue;
                for (int j = i + 1; j < level.walls.Count; j++)
                {
                    if (!Segments.TryEnds(level.walls[j], out Vector2 a1, out Vector2 b1)) continue;
                    if (!Contact(a0, b0, a1, b1, o, out Vector2 p)) continue;

                    p = Segments.Weld(p, new[] { a0, b0, a1, b1 }, o.contactEps);
                    p = Segments.Canonical(reps, p, o.contactEps);
                    Record(cuts, level.walls[i].id, p);
                    Record(cuts, level.walls[j].id, p);
                }
            }

            int before = splits;
            foreach (var kv in cuts)
            {
                var w = FindWall(level, kv.Key);
                if (w == null) continue;
                if (SplitWall(level, w, kv.Value, o, warnings).Count > 1) splits++;
            }
            if (splits == before) break;   // nothing changed => the invariant holds
        }
        return splits;
    }

    // =======================================================================================
    // Uncovered: what part of a run is not already walled
    // =======================================================================================

    /// <summary>
    /// The sub-spans of a -> b (in meters from a) NOT already covered by a collinear wall. Empty means
    /// the run is entirely redundant. This is how the rectangle room stamp shares its neighbor's wall
    /// instead of doubling it: the shared edge simply emits nothing.
    /// </summary>
    public static List<Vector2> Uncovered(LevelDef level, Vector2 a, Vector2 b, Options o)
    {
        float len = (b - a).magnitude;
        if (len < HomeConventions.EPS) return new List<Vector2>();
        if (level?.walls == null) return new List<Vector2> { new Vector2(0f, len) };

        var covered = new List<Vector2>();
        foreach (var w in level.walls)
        {
            if (!Segments.TryEnds(w, out Vector2 wa, out Vector2 wb)) continue;
            if (!Segments.CollinearOverlap(a, b, wa, wb, o.contactEps, o.minJunctionSin,
                                           out float t0, out float t1)) continue;
            covered.Add(new Vector2(t0 * len, t1 * len));
        }
        return Spans.Subtract(new Vector2(0f, len), covered);
    }

    // =======================================================================================
    // RefitWall, after the geometry moved under them
    // =======================================================================================

    /// <summary>
    /// Re-fits every opening and clamps every mount on one wall. Called on drag-release, never per
    /// frame: fitting mid-drag ratchets a door along its wall and never brings it back.
    /// </summary>
    public static void RefitWall(LevelDef level, WallDef wall, List<string> warnings = null)
    {
        if (level == null || wall == null) return;
        float len = WallLayout.WallLength(wall);

        if (level.openings != null)
            for (int i = level.openings.Count - 1; i >= 0; i--)
            {
                var op = level.openings[i];
                if (op == null || op.wallId != wall.id) continue;

                var fit = OpeningFit.Fit(op, wall, level, op.offset);
                if (!fit.ok)
                {
                    level.openings.RemoveAt(i);
                    warnings?.Add($"Removed a {Pretty(op.kind)}: {fit.reason}");
                }
                else if (fit.clamped)
                {
                    op.offset = fit.offset;
                    warnings?.Add($"Moved a {Pretty(op.kind)} to keep it on its wall.");
                }
            }

        if (level.wallMounted != null)
            foreach (var m in level.wallMounted)
                if (m != null && m.wallId == wall.id)
                    m.offset = Mathf.Clamp(m.offset, 0f, len);
    }

    // =======================================================================================
    // internals
    // =======================================================================================

    private sealed class Contacts
    {
        public readonly List<Vector2> reps = new List<Vector2>();
        public readonly Dictionary<string, List<Vector2>> wallCuts = new Dictionary<string, List<Vector2>>();
        public readonly Dictionary<int, List<Vector2>> segCuts = new Dictionary<int, List<Vector2>>();
    }

    // Every contact between the candidate chain and the level's existing walls, with each junction
    // reduced to ONE canonical Vector2 shared by both sides.
    private static Contacts Collect(LevelDef level, List<Vector2> pts, string ignoreWallId, Options o)
    {
        var c = new Contacts();
        if (level?.walls == null) return c;

        for (int i = 0; i < pts.Count - 1; i++)
        {
            Vector2 ca = pts[i], cb = pts[i + 1];
            if ((cb - ca).sqrMagnitude < 1e-9f) continue;

            foreach (var w in level.walls)
            {
                if (w == null || w.id == ignoreWallId) continue;
                if (!Segments.TryEnds(w, out Vector2 wa, out Vector2 wb)) continue;
                if (!Contact(ca, cb, wa, wb, o, out Vector2 p)) continue;

                // Weld to a real vertex of either side first: never invent a point 2 mm off a corner
                // that already exists. Then to the canonical set so a three-way meeting is one point.
                p = Segments.Weld(p, new[] { wa, wb, ca, cb }, o.contactEps);
                p = Segments.Canonical(c.reps, p, o.contactEps);

                Record(c.wallCuts, w.id, p);
                if (!c.segCuts.TryGetValue(i, out var list)) c.segCuts[i] = list = new List<Vector2>();
                if (!Segments.Contains(list, p, o.contactEps)) list.Add(p);
            }
        }
        return c;
    }

    // One contact test: a proper crossing, or an endpoint of either segment landing on the other.
    // Parallel contact is deliberately excluded on all three paths: that is overlap, not a junction.
    private static bool Contact(Vector2 a0, Vector2 b0, Vector2 a1, Vector2 b1, Options o, out Vector2 p)
    {
        if (Segments.Intersect(a0, b0, a1, b1, o.contactEps, out _, out _, out p)) return true;

        p = default;
        if (Segments.SinBetween(b0 - a0, b1 - a1) < o.minJunctionSin) return false;

        // Endpoint T, tested in both directions: a run that stops just short of a wall, and a wall
        // whose end lands just short of the run.
        if (TouchPoint(a0, a1, b1, o.contactEps, out p)) return true;
        if (TouchPoint(b0, a1, b1, o.contactEps, out p)) return true;
        if (TouchPoint(a1, a0, b0, o.contactEps, out p)) return true;
        if (TouchPoint(b1, a0, b0, o.contactEps, out p)) return true;
        return false;
    }

    private static bool TouchPoint(Vector2 end, Vector2 a, Vector2 b, float eps, out Vector2 p)
    {
        p = Segments.ClosestOn(end, a, b, out _);
        return Vector2.Distance(end, p) <= eps;
    }

    // Turns raw cut points into sorted arc positions, dropping every one that must not become a split:
    // too near an end (shared corner), too near another cut, or inside a doorway.
    private static List<float> SurvivingCuts(LevelDef level, WallDef wall, IReadOnlyList<Vector2> at,
                                             Options o, List<string> warnings)
    {
        var arcs = new List<float>();
        if (wall == null || at == null || !Segments.TryEnds(wall, out Vector2 a, out Vector2 b)) return arcs;

        float len = (b - a).magnitude;
        Vector2 dir = (b - a) / len;

        var blocked = OpeningSpans(level, wall, o.minEdge);
        bool warnedDoor = false;
        bool warnedNear = false;

        var raw = new List<float>();
        foreach (var p in at)
        {
            // Only a point actually ON this wall is a cut; a welded representative may belong to a
            // different wall of the same junction.
            Vector2 foot = Segments.ClosestOn(p, a, b, out _);
            if (Vector2.Distance(foot, p) > o.contactEps) continue;
            raw.Add(Vector2.Dot(p - a, dir));
        }
        raw.Sort();

        foreach (float s in raw)
        {
            // A refusal at a genuine T (not an ordinary shared-corner draw, s ~ 0 or s ~ len) used to
            // vanish without trace; RoomRegions.Find step C still closes the room across the unsplit
            // wall, so the warning documents the wall topology, not a failure.
            bool realJunction = s > o.contactEps && len - s > o.contactEps;

            if (s < o.minSeg || s > len - o.minSeg)                                 // shared corner / sliver
            {
                if (realJunction && !warnedNear)
                {
                    warnings?.Add("Wall not split there: the junction is too close to the wall's end.");
                    warnedNear = true;
                }
                continue;
            }
            if (arcs.Count > 0 && s - arcs[arcs.Count - 1] < o.minSeg)              // duplicate junction
            {
                if (realJunction && !warnedNear)
                {
                    warnings?.Add("Wall not split there: the junction is too close to another junction.");
                    warnedNear = true;
                }
                continue;
            }
            if (len - s < o.minSeg) continue;

            if (InsideAnOpening(blocked, s))
            {
                if (!warnedDoor)
                {
                    warnings?.Add("Wall not split there: the junction lands in a doorway.");
                    warnedDoor = true;
                }
                continue;
            }
            arcs.Add(s);
        }
        return arcs;
    }

    private static List<Vector2> OpeningSpans(LevelDef level, WallDef wall, float minEdge)
    {
        var spans = new List<Vector2>();
        if (level?.openings == null) return spans;
        foreach (var op in level.openings)
        {
            if (op == null || op.wallId != wall.id || op.width <= HomeConventions.EPS) continue;
            float half = 0.5f * op.width + minEdge;
            spans.Add(new Vector2(op.offset - half, op.offset + half));
        }
        return spans;
    }

    private static bool InsideAnOpening(List<Vector2> spans, float s)
    {
        foreach (var sp in spans)
            if (s > sp.x && s < sp.y) return true;
        return false;
    }

    private static int PieceIndex(List<float> bounds, float offset)
    {
        for (int k = 0; k < bounds.Count - 1; k++)
            if (offset <= bounds[k + 1] || k == bounds.Count - 2) return k;
        return 0;
    }

    private static void Record(Dictionary<string, List<Vector2>> map, string id, Vector2 p)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!map.TryGetValue(id, out var list)) map[id] = list = new List<Vector2>();
        if (!Segments.Contains(list, p, WeldEps)) list.Add(p);
    }

    private static List<Vector2> Normalize(IReadOnlyList<Vector2> pts, float eps)
    {
        var outPts = new List<Vector2>();
        if (pts == null) return outPts;
        foreach (var p in pts)
            if (outPts.Count == 0 || Vector2.Distance(outPts[outPts.Count - 1], p) > eps) outPts.Add(p);
        return outPts;
    }

    private static WallDef FindWall(LevelDef level, string id)
    {
        if (level?.walls == null || string.IsNullOrEmpty(id)) return null;
        foreach (var w in level.walls)
            if (w != null && w.id == id) return w;
        return null;
    }

    // Everything except id and endpoints. Direction is preserved by every caller, which is what keeps
    // materialLeft/Right and WallMountDef.side meaning what they meant.
    private static WallDef CopyOf(WallDef src) => new WallDef
    {
        a = new[] { 0f, 0f },
        b = new[] { 0f, 0f },
        thickness = src?.thickness ?? 0f,
        height = src?.height ?? 0f,
        materialLeft = src?.materialLeft,
        materialRight = src?.materialRight,
    };

    private static string Pretty(string kind) => string.IsNullOrEmpty(kind) ? "opening" : kind.Replace('_', ' ');
}
