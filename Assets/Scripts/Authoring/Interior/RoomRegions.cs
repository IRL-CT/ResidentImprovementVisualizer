using System.Collections.Generic;
using UnityEngine;

// An area closed off by walls IS a room.
//
// This file is the inverse of PlanBuilder. That one takes room rectangles and derives a wall graph
// from them, because authoring six sample plans as raw coordinates would be thousands of unreviewable
// lines. This one takes the wall graph a USER drew and derives the rooms, because tracing a floor
// polygon over walls you have already drawn is drawing the same room twice, and nothing anywhere
// checked that the two agreed. A room polygon that had drifted off its walls, and a walled area with
// no room in it at all, were both completely silent, which is the class of failure PlanBuilder and
// WallLinker exist to break for walls.
//
// The one sentence everything here follows from:
//
//     An enclosed area is a room. Rooms stay first-class, stored, id-bearing, diffable records;
//     derivation is an EDITING-TIME rewrite of one field (polygon), never a render-time computation.
//
// That second clause is not a style preference, it is what keeps the rest of the app working.
// VariantDiff matches rooms purely by id, SensorDef hosts on a room id, every occupant's day addresses
// rooms by id, and ReportBuilder sections by room. Derive rooms at render time and a locked baseline,
// which has no editing session at all. Has no stable id for any of them to point at. So Sync rewrites
// `polygon` and NOTHING else; see its own comment.
//
// Find is a pure function of level.walls and never mutates them (pinned by RoomRegionsTests).
public static class RoomRegions
{
    /// <summary>
    /// Two wall ends closer than this are one vertex.
    ///
    /// Tied to WallLinker.WeldEps deliberately, and the justification is not "it is small enough": it
    /// is verified equal to WallMeshBuilder.Near, which is the distance at which ComputeExtensions
    /// actually closes a corner. Tying them makes "this area is closed" mean the same thing to the face
    /// finder and to the pixels: a gap you can SEE is a gap that makes no room. Using WallLinker's
    /// ContactEps (0.02) instead would report a room through a visible hole.
    /// </summary>
    public const float WeldEps = WallLinker.WeldEps;

    /// <summary>
    /// Faces below this are construction artifacts, not rooms.
    ///
    /// 0.35 m²: a 0.6 m service shaft is 0.36 and survives, the smallest room in any sample is 3.6 m²,
    /// and two walls 20 mm apart over 4 m come to 0.08, so slivers are swallowed by construction
    /// rather than by a separate sliver rule.
    /// </summary>
    public const float MinArea = 0.35f;

    /// <summary>One enclosed area, as a simple CCW ring of welded wall endpoints.</summary>
    public struct Region
    {
        public List<Vector2> ring;
        public float area;
        public Vector2 centroid;
        /// <summary>Largest-inscribed-circle center: the point Sync matches on, and by construction
        /// the point furthest from any wall. An L-shaped room's centroid can fall outside it.</summary>
        public Vector2 inside;
    }

    // ---- Find ----------------------------------------------------------------------------------

    /// <summary>
    /// Every area the level's walls close off, as CCW single rings, in a deterministic order.
    /// Never mutates <paramref name="level"/>.
    /// </summary>
    public static List<Region> Find(LevelDef level, List<string> warnings = null)
    {
        var regions = new List<Region>();
        if (level?.walls == null || level.walls.Count < 3) return regions;

        // A. Raw centerline segments. Thickness is ignored: room polygons run along wall CENTERLINES,
        // which is PlanBuilder.Room's stated convention and what OccupancyModel.IsClear already
        // compensates for by half a wall thickness.
        //
        // segTol is each wall's interior-insertion tolerance for step C: a junction vertex counts as
        // "on this wall" when it lies inside the wall's rendered body (within half its thickness) 
        // capped at WallLinker.ContactEps, the bound within which the linker guarantees a junction it
        // detected but refused to cut actually sits. Floored at WeldEps so a degenerate thickness can
        // never make step C stricter than step B's welding.
        var segA = new List<Vector2>();
        var segB = new List<Vector2>();
        var segTol = new List<float>();
        foreach (var w in level.walls)
        {
            if (w == null || !Segments.TryEnds(w, out var a, out var b)) continue;
            segA.Add(a);
            segB.Add(b);
            segTol.Add(Mathf.Max(WeldEps,
                Mathf.Min(WallLinker.ContactEps, 0.5f * WallLayout.EffectiveThickness(w, level))));
        }
        if (segA.Count < 3) return regions;

        // B. Canonical vertices. Segments.CanonicalIndex is the shared weld-or-add rule, so the graph
        // this builds cannot disagree with WallLinker about which points are one junction.
        var verts = new List<Vector2>();
        var endA = new int[segA.Count];
        var endB = new int[segA.Count];
        for (int i = 0; i < segA.Count; i++)
        {
            endA[i] = Segments.CanonicalIndex(verts, segA[i], WeldEps);
            endB[i] = Segments.CanonicalIndex(verts, segB[i], WeldEps);
        }

        // C. Split each segment at every canonical vertex lying on its INTERIOR, then
        // D. de-duplicate by unordered vertex pair.
        //
        // C must run before D, and that ordering is the whole answer to collinear and overlapping
        // walls: a wall drawn twice along one line collapses to one edge, and a partial overlap
        // (A = [0,5], B = [3,8]) is split into A=[0,3]+[3,5] and B=[3,5]+[5,8], whose shared [3,5]
        // then de-dupes. Without D, two half-edges leave a vertex at an identical angle and the
        // angular walk in F becomes ambiguous.
        //
        // C is also not optional for a reason that has already shipped: WallLinker.SurvivingCuts
        // REFUSES a cut that lands inside a doorway, too near the wall's end, or too near another
        // junction, so a real T-junction can exist with no vertex on the through-wall, and the areas
        // either side of it are genuinely enclosed. And because the linker welds such a junction onto
        // the DRAWN endpoint (up to ContactEps off the through-wall's centerline), C accepts a vertex
        // within segTol[i] (min(ContactEps, half this wall's thickness)) not merely WeldEps. Inside
        // the wall's body there is no visible gap to falsely close, so "a gap you can SEE is a gap
        // that makes no room" still holds; a Shift free-drawn wall stopping short of the body still
        // closes nothing. Welding in B stays at WeldEps: junctions 20 mm apart remain distinct
        // vertices, and C only REUSES canonical vertices. It never invents one, which is what keeps
        // the bare-X property below true by construction.
        //
        // Note what is deliberately NOT done: no vertex is invented where two walls cross without
        // sharing one. That is only reachable via Shift free-draw, WallMeshBuilder renders it as a
        // visible notch, and inventing a vertex would report a room the plan does not draw.
        var edges = new HashSet<long>();
        var chain = new List<int>();
        var order = new List<float>();
        for (int i = 0; i < segA.Count; i++)
        {
            Vector2 a = verts[endA[i]], b = verts[endB[i]];
            if (endA[i] == endB[i]) continue;

            chain.Clear();
            order.Clear();
            for (int v = 0; v < verts.Count; v++)
            {
                if (v == endA[i] || v == endB[i]) continue;
                Vector2 on = Segments.ClosestOn(verts[v], a, b, out float t);
                if (t <= 0f || t >= 1f) continue;
                if (Vector2.Distance(on, verts[v]) > segTol[i]) continue;
                chain.Add(v);
                order.Add(t);
            }
            SortByKey(chain, order);

            int prev = endA[i];
            for (int k = 0; k < chain.Count; k++) { AddEdge(edges, prev, chain[k]); prev = chain[k]; }
            AddEdge(edges, prev, endB[i]);
        }
        if (edges.Count < 3) return regions;

        // E. Prune vertices of degree <= 1, to fixpoint.
        //
        // This does not change WHICH faces exist: a tree hanging into a face cannot separate it. It
        // exists to keep the rings SIMPLE: a spur left in makes the face walk out along it and back,
        // producing a zero-width slit and a repeated vertex, which PolygonTriangulator (ear clipping,
        // no hole support) turns into garbage.
        var adj = BuildAdjacency(verts.Count, edges);
        Prune(adj, edges);
        if (edges.Count < 3) return regions;

        // F. Half-edges, sorted by angle at each vertex.
        var he = new List<int>();          // he[2k] = tail, he[2k+1] = head, per half-edge pair
        foreach (long key in edges)
        {
            int u = (int)(key >> 32), v = (int)(key & 0xFFFFFFFFL);
            he.Add(u); he.Add(v);          // half-edge 2i   : u -> v
            he.Add(v); he.Add(u);          // half-edge 2i+1 : v -> u
        }
        int halfCount = he.Count / 2;

        var outgoing = new List<int>[verts.Count];
        for (int h = 0; h < halfCount; h++)
        {
            int tail = he[h * 2];
            (outgoing[tail] ??= new List<int>()).Add(h);
        }
        var slot = new int[halfCount];
        for (int v = 0; v < verts.Count; v++)
        {
            var list = outgoing[v];
            if (list == null) continue;
            var ang = new List<float>(list.Count);
            foreach (int h in list)
            {
                Vector2 d = verts[he[h * 2 + 1]] - verts[he[h * 2]];
                ang.Add(Mathf.Atan2(d.y, d.x));
            }
            SortByKey(list, ang);          // no epsilon needed: after D, identical angles cannot occur
            for (int k = 0; k < list.Count; k++) slot[list[k]] = k;
        }

        // G. Walk faces. next(h) is the outgoing half-edge at head(h) IMMEDIATELY CLOCKWISE from
        // twin(h). Equivalently, arriving at a vertex, take the leftmost available turn. On a unit
        // square A(0,0) B(1,0) C(1,1) D(0,1): from A->B, at B the outgoing set is B->A (180 deg) and
        // B->C (90 deg); one step clockwise from 180 is 90, so next is B->C, and the walk closes
        // A->B->C->D->A with POSITIVE signed area. The reverse half-edge walks the exterior, negative.
        //
        // So the outer face is exactly the cycle whose SignedArea is <= 0. That is a theorem of planar
        // subdivisions rather than a heuristic: after pruning, every bounded face walks CCW and each
        // connected component's outer boundary walks CW. It needs no bounding-box test, and it is
        // correct for DISCONNECTED components: two detached wall loops give two negative cycles,
        // both discarded, and both their interiors kept. A loop drawn INSIDE another loop therefore
        // leaves the enclosing face still containing the island's area. Step H2 below carves that
        // out again, so one room never claims another room's floor.
        var seen = new bool[halfCount];
        var cycle = new List<int>();
        for (int start = 0; start < halfCount; start++)
        {
            if (seen[start]) continue;

            cycle.Clear();
            int h = start;
            bool ok = true;
            for (int guard = 0; guard <= halfCount; guard++)
            {
                if (seen[h]) { ok = h == start && cycle.Count > 0; break; }
                seen[h] = true;
                cycle.Add(he[h * 2]);           // tail
                int twin = h ^ 1;
                var ring = outgoing[he[h * 2 + 1]];
                int idx = (slot[twin] - 1 + ring.Count) % ring.Count;
                h = ring[idx];
                if (h == start) break;
            }
            if (!ok || cycle.Count < 3) continue;

            EmitFace(cycle, verts, regions, warnings);
        }

        // H2. Regions must never overlap: one room, one space.
        CarveContainedRegions(regions);

        // I. Determinism. Without a canonical region order, reordering level.walls reorders the output
        // and every save rewrites the JSON for nothing.
        regions.Sort((x, y) =>
        {
            int c = x.ring[0].x.CompareTo(y.ring[0].x);
            if (c != 0) return c;
            c = x.ring[0].y.CompareTo(y.ring[0].y);
            if (c != 0) return c;
            return y.area.CompareTo(x.area);
        });
        return regions;
    }

    // H. A ring that repeats a vertex is not simple: two areas meeting only at a corner, or a stub
    // whose tip lands on the ring. Ear clipping produces garbage from those, so cut the ring at the
    // repeat into its two loops and recurse. The correct topological answer rather than a tolerance.
    private static void EmitFace(List<int> cycleIdx, List<Vector2> verts,
                                 List<Region> into, List<string> warnings)
    {
        for (int i = 0; i < cycleIdx.Count; i++)
        {
            for (int j = i + 1; j < cycleIdx.Count; j++)
            {
                if (cycleIdx[i] != cycleIdx[j]) continue;

                var inner = cycleIdx.GetRange(i, j - i);
                var outer = new List<int>(cycleIdx.GetRange(0, i));
                outer.AddRange(cycleIdx.GetRange(j, cycleIdx.Count - j));
                if (inner.Count >= 3) EmitFace(inner, verts, into, warnings);
                if (outer.Count >= 3) EmitFace(outer, verts, into, warnings);
                return;
            }
        }

        var ring = new List<Vector2>(cycleIdx.Count);
        foreach (int v in cycleIdx) ring.Add(verts[v]);
        DropCollinear(ring);
        if (ring.Count < 3) return;

        float signed = PolygonTriangulator.SignedArea(ring);
        if (signed <= 0f) return;                       // the outer face, per G
        if (signed < MinArea) return;

        Rotate(ring);
        into.Add(new Region
        {
            ring = ring,
            area = signed,
            centroid = Centroid(ring),
            inside = InsideOf(ring),
        });
    }

    /// <summary>
    /// Removes vertices that sit on the straight line between their neighbors.
    ///
    /// The face walk emits a vertex wherever ANY wall meets the boundary, so a room picks up a vertex
    /// mid-edge everywhere a neighboring room's partition T-joins it. PlanBuilder splits walls at
    /// exactly those points. Those are corners of the WALL GRAPH, not corners of this room, and
    /// keeping them means the derived polygon differs from the same room drawn in isolation, so Sync
    /// would rewrite every sample's stored polygon on first run for no change in shape.
    ///
    /// The tolerance is a perpendicular distance of 0.1 mm. It cannot merge two genuine corners: those
    /// are at least WallLinker.MinSeg (0.10 m) apart. And it is far below the angle at which
    /// WallLinker.MinJunctionSin already calls two walls one continuing run (~5.7 deg): 0.1 mm over a
    /// 4 m edge is 0.0014 deg. What it is above is float noise, which reaches ~1.5e-6 m across a
    /// 12.5 m wall at float32 precision.
    /// </summary>
    private static void DropCollinear(List<Vector2> ring)
    {
        const float Flat = 1e-4f;
        for (int i = 0; i < ring.Count && ring.Count > 3; )
        {
            Vector2 prev = ring[(i - 1 + ring.Count) % ring.Count];
            Vector2 next = ring[(i + 1) % ring.Count];
            if (ResidenceMetrics.PointSegmentDistance(ring[i], prev, next) <= Flat) ring.RemoveAt(i);
            else i++;
        }
    }

    /// <summary>
    /// Rotates a ring to start at its lexicographically smallest vertex.
    ///
    /// Without this the start depends on which half-edge the face walk happened to begin at, so an
    /// unrelated edit would rewrite every stored polygon. VariantDiff.CompareRooms compares AREA, not
    /// vertices, so that would not even be reported. It would churn the file silently, which is worse
    /// than reporting it.
    /// </summary>
    private static void Rotate(List<Vector2> ring)
    {
        int best = 0;
        for (int i = 1; i < ring.Count; i++)
            if (ring[i].x < ring[best].x ||
                (ring[i].x == ring[best].x && ring[i].y < ring[best].y)) best = i;
        if (best == 0) return;
        var head = ring.GetRange(0, best);
        ring.RemoveRange(0, best);
        ring.AddRange(head);
    }

    // ---- carve ---------------------------------------------------------------------------------

    // H2. A region wholly inside another is carved OUT of it, so regions never overlap.
    //
    // Two ways an island arises, both real: a closed loop drawn inside a room with no connection to
    // the enclosing walls (per G, both interiors are kept, so the enclosing ring still contains the
    // island's area), and a loop touching the boundary at a single vertex (H splits the keyhole face
    // at the repeated vertex, keeping the plain outer ring). Either way the island IS its own room,
    // what must not survive is the enclosing region claiming the island's floor too.
    //
    // RoomDef.polygon is a single ring with no hole support, so the carve is a BRIDGE CUT: the
    // island's ring is spliced into the containing ring in reverse, joined by two coincident edges.
    // Even-odd point tests then read the island as outside (the twin edges cancel in parity),
    // SignedArea comes out as outer minus island, and ear clipping. Inclusive containment, only
    // CONSECUTIVE duplicates dropped. Triangulates it. Pinned by RoomRegionsTests and
    // PolygonTriangulatorTests.
    //
    // Runs AFTER EmitFace (which would split the bridged ring at its repeated vertices, undoing the
    // carve) and BEFORE the step-I sort (the splice can move ring[0]). Deterministic by construction:
    // children are carved in canonical ring order and ties in the bridge pick break by index,
    // RoomsMatch compares floats exactly, so a wandering bridge would keep Detect rooms alive forever.
    private static void CarveContainedRegions(List<Region> regions)
    {
        if (regions.Count < 2) return;

        // The immediate parent of each region: the SMALLEST region containing its inside point, so a
        // room inside a room inside a room carves one level at a time. Decided on the uncarved rings.
        var parent = new int[regions.Count];
        for (int j = 0; j < regions.Count; j++)
        {
            parent[j] = -1;
            for (int i = 0; i < regions.Count; i++)
            {
                if (i == j || regions[i].area <= regions[j].area) continue;
                if (!ResidenceMetrics.PointInPolygon(regions[j].inside, regions[i].ring)) continue;
                if (parent[j] < 0 || regions[i].area < regions[parent[j]].area) parent[j] = i;
            }
        }

        var drop = new List<int>();
        for (int i = 0; i < regions.Count; i++)
        {
            // This parent's children, in canonical ring order. Rings are already Rotate()d, so
            // ring[0] is stable per ring no matter how level.walls happens to be ordered.
            var children = new List<int>();
            for (int j = 0; j < regions.Count; j++) if (parent[j] == i) children.Add(j);
            if (children.Count == 0) continue;
            children.Sort((x, y) =>
            {
                int c = regions[x].ring[0].x.CompareTo(regions[y].ring[0].x);
                if (c != 0) return c;
                return regions[x].ring[0].y.CompareTo(regions[y].ring[0].y);
            });

            var ring = regions[i].ring;
            for (int c = 0; c < children.Count; c++)
            {
                // Children already spliced are part of `ring`; the ones still to come block as rings
                // of their own, so no bridge is ever laid across a sibling.
                var pending = new List<List<Vector2>>();
                for (int d = c + 1; d < children.Count; d++) pending.Add(regions[children[d]].ring);
                ring = SpliceRing(ring, regions[children[c]].ring, pending);
            }

            float signed = PolygonTriangulator.SignedArea(ring);
            if (signed < MinArea) { drop.Add(i); continue; }   // an annulus thinner than a sliver

            Rotate(ring);
            var r = regions[i];
            r.ring = ring;
            r.area = signed;
            r.centroid = Centroid(ring);
            r.inside = InsideOf(ring);
            regions[i] = r;
        }

        for (int k = drop.Count - 1; k >= 0; k--) regions.RemoveAt(drop[k]);
    }

    /// <summary>
    /// Splices <paramref name="island"/> (CCW) into <paramref name="outer"/> (CCW) in reverse order,
    /// joined at the closest mutually visible vertex pair: the bridge cut. Returns the new ring.
    /// </summary>
    private static List<Vector2> SpliceRing(List<Vector2> outer, List<Vector2> island,
                                            List<List<Vector2>> blockers)
    {
        int bestP = 0, bestQ = 0;
        float bestD = float.MaxValue;
        bool haveClear = false;
        for (int p = 0; p < outer.Count; p++)
        for (int q = 0; q < island.Count; q++)
        {
            float d = Vector2.Distance(outer[p], island[q]);
            bool clear = BridgeClear(outer, p, island, q, blockers);
            // A clear pair always beats a blocked one; within a class, strictly nearer wins, so ties
            // keep the earliest (p, q). Index order is the determinism guarantee. The blocked-pair
            // fallback should be unreachable (a hole in a simple polygon always sees the boundary),
            // but degenerate input must still splice SOMETHING rather than leave the overlap.
            bool better = (clear && !haveClear) || (clear == haveClear && d < bestD);
            if (better) { bestD = d; bestP = p; bestQ = q; haveClear = clear; }
        }

        // outer[0..p], the island in reverse from q all the way around back to q, then p again, then
        // the rest of the outer ring. The shared-vertex case (a zero-length bridge) would leave
        // consecutive duplicates, so those are dropped: the vertex is still visited twice, just with
        // no bridge edge between the visits.
        int n = island.Count;
        var next = new List<Vector2>(outer.Count + n + 2);
        for (int k = 0; k <= bestP; k++) next.Add(outer[k]);
        for (int k = 0; k <= n; k++) next.Add(island[((bestQ - k) % n + n) % n]);
        next.Add(outer[bestP]);
        for (int k = bestP + 1; k < outer.Count; k++) next.Add(outer[k]);

        for (int k = next.Count - 1; k > 0; k--)
            if (Segments.Near(next[k], next[k - 1], WeldEps)) next.RemoveAt(k);
        if (next.Count > 1 && Segments.Near(next[0], next[next.Count - 1], WeldEps))
            next.RemoveAt(next.Count - 1);
        return next;
    }

    private static bool BridgeClear(List<Vector2> outer, int p, List<Vector2> island, int q,
                                    List<List<Vector2>> blockers)
    {
        Vector2 a = outer[p], b = island[q];
        if (Segments.Near(a, b, WeldEps)) return true;   // the shared vertex needs no bridge at all
        if (RingBlocks(outer, a, b, p)) return false;
        if (RingBlocks(island, a, b, q)) return false;
        if (blockers != null)
            foreach (var ring in blockers)
                if (RingBlocks(ring, a, b, -1)) return false;
        return true;
    }

    // Does any edge of the ring cross a→b? The two edges incident to vertex `skipIncident` share an
    // endpoint with the bridge and are excluded; everything else blocks even on a touch, because a
    // bridge grazing a vertex would put three coincident edges through one point of the spliced ring.
    private static bool RingBlocks(List<Vector2> ring, Vector2 a, Vector2 b, int skipIncident)
    {
        int n = ring.Count;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            if (i == skipIncident || j == skipIncident) continue;
            if (Segments.Intersect(a, b, ring[i], ring[j], 0f, out _, out _, out _)) return true;
        }
        return false;
    }

    // ---- Sync ----------------------------------------------------------------------------------

    /// <summary>
    /// Brings <paramref name="level"/>.rooms into step with its walls, and returns how many rooms were
    /// added, removed or reshaped.
    ///
    /// THE CONTRACT: for a matched room this writes `polygon` and NOTHING else. Not id, not name, not
    /// roomType, not ceilingHeight. That one sentence is what keeps VariantDiff.CompareRooms, which
    /// matches purely by id. Reporting "area 74 sq ft -> 96 sq ft" instead of degenerating into
    /// "removed everything, added everything", and it is what lets a SensorDef, an ActivityDef's
    /// roomId and the report's sections go on pointing at the same room across a wall edit.
    ///
    /// Idempotent: a second call changes nothing. The same property WallLinker.Relink is specified by,
    /// and for the same reason. It runs on every wall edit, and a version that churned ids would
    /// wreck every open comparison.
    ///
    /// Deliberately NOT called from VariantRevert, and not on load. See the call-site notes in
    /// WallTool.CommitSegment.
    /// </summary>
    public static int Sync(LevelDef level, List<string> warnings = null)
    {
        if (level == null) return 0;
        level.rooms ??= new List<RoomDef>();

        var regions = Find(level, warnings);
        int changes = 0;
        int n = level.rooms.Count;

        // Which region, if any, each existing room belongs to. Three cheap passes rather than a
        // polygon intersection: the decision only has to be STABLE, not measured.
        var claim = new int[n];
        for (int i = 0; i < n; i++) claim[i] = -1;

        // Pass 1: the room's largest-inscribed-circle center. NOT the centroid: an L- or U-shaped
        // room's centroid can fall outside it, while the inscribed center is by construction the point
        // furthest from any wall, which is why OccupancyModel and ViewController already use it.
        for (int i = 0; i < n; i++)
        {
            var poly = PolygonTriangulator.ToVector2(level.rooms[i]?.polygon);
            if (poly.Count < 3) continue;
            var c = ResidenceMetrics.LargestInscribedCircle(poly);
            claim[i] = RegionAt(regions, c.valid ? c.center : Centroid(poly));
        }

        // Pass 2: the centroid, for rooms pass 1 could not place.
        for (int i = 0; i < n; i++)
        {
            if (claim[i] >= 0) continue;
            var poly = PolygonTriangulator.ToVector2(level.rooms[i]?.polygon);
            if (poly.Count < 3) continue;
            claim[i] = RegionAt(regions, Centroid(poly));
        }

        // Pass 3. Whichever region holds the most of the room's own corners.
        for (int i = 0; i < n; i++)
        {
            if (claim[i] >= 0) continue;
            var poly = PolygonTriangulator.ToVector2(level.rooms[i]?.polygon);
            if (poly.Count < 3) continue;
            int best = -1, bestHits = 0;
            for (int r = 0; r < regions.Count; r++)
            {
                int hits = 0;
                foreach (var v in poly) if (ResidenceMetrics.PointInPolygon(v, regions[r].ring)) hits++;
                bool better = hits > bestHits
                           || (hits == bestHits && hits > 0 && best >= 0 && regions[r].area > regions[best].area);
                if (better) { bestHits = hits; best = r; }
            }
            if (bestHits > 0) claim[i] = best;
        }

        // Resolve each region to at most one room.
        //
        // Two rooms claiming one region is a MERGE (the wall between them was deleted) and the
        // LARGER claimant keeps its identity, so removing the wall between a living room and a nook
        // leaves the living room. Ties go to the earlier index, for determinism.
        //
        // One room claiming two regions is a SPLIT: a wall was drawn across it. It falls out of the
        // same rule with no extra code: the room owns whichever region claimed it, and the other
        // region is owned by nobody and becomes an Untyped newcomer below. So drawing a wall across a
        // bedroom leaves the bedroom plus the remainder you then click to type.
        var owner = new int[regions.Count];
        for (int r = 0; r < regions.Count; r++) owner[r] = -1;
        for (int i = 0; i < n; i++)
        {
            int r = claim[i];
            if (r < 0) continue;
            if (owner[r] < 0) { owner[r] = i; continue; }

            int keep = owner[r], drop = i;
            if (AreaOf(level.rooms[drop]) > AreaOf(level.rooms[keep])) { keep = i; drop = owner[r]; }
            owner[r] = keep;
            claim[drop] = -1;
            warnings?.Add($"Two rooms became one. {Label(level.rooms[keep])} absorbed {Label(level.rooms[drop])}.");
        }

        // Rooms nothing claims are gone: their walls no longer enclose an area. This is also the path
        // that fires the first time somebody edits a wall in a residence whose rooms were traced by hand
        // under the old tool. It is destructive, and it is undoable only because Sync runs inside the
        // caller's RecordEdit, which every call site must therefore keep doing.
        for (int i = 0; i < n; i++)
        {
            if (claim[i] >= 0) continue;
            var room = level.rooms[i];
            if (room == null) continue;
            warnings?.Add($"{Label(room)} is gone. Its walls no longer enclose an area.");
            DropRoomSensors(level, room.id);
            changes++;
        }

        // Surviving rooms keep their EXISTING ORDER. Rebuilding the list in region order instead was
        // the obvious thing and it is wrong: it reshuffles level.rooms on any wall edit anywhere, which
        // rewrites the stored document and reorders every list built by walking it, for no visible
        // change. Order is not identity, so there is nothing to gain by canonicalising it.
        var survivors = new List<RoomDef>(regions.Count);
        for (int i = 0; i < n; i++)
        {
            int r = claim[i];
            if (r < 0 || owner[r] != i) continue;
            var room = level.rooms[i];
            var next = PolygonTriangulator.ToArray(regions[r].ring);
            if (!SamePolygon(room.polygon, next)) { room.polygon = next; changes++; }
            survivors.Add(room);
        }

        // A region nothing claims is a new room, and it is a REAL one from this instant: it has a
        // floor, an id and an area, it holds furniture, it places people and it is picked up by sensor
        // coverage. Naming it is a later refinement, not a prerequisite. New rooms go on the end, in
        // Find's canonical region order.
        for (int r = 0; r < regions.Count; r++)
        {
            if (owner[r] >= 0) continue;
            survivors.Add(new RoomDef
            {
                id = System.Guid.NewGuid().ToString(),
                name = "",
                roomType = RoomType.Untyped,
                polygon = PolygonTriangulator.ToArray(regions[r].ring),
                ceilingHeight = 0f,
            });
            changes++;
        }

        level.rooms = survivors;
        return changes;
    }

    /// <summary>
    /// Removes a room and the devices that can only be hosted on it.
    ///
    /// This cascade already existed in two places. SelectTool.DeleteSelected and
    /// VariantRevert.RevertRoom, which the notes call "two places that must not disagree". Sync would
    /// have made a third, so it lives here once and the others call it. A SensorHost.Point water
    /// sensor names a room but lives at a coordinate, so it survives its room, exactly as it always
    /// has.
    /// </summary>
    public static void RemoveRoom(LevelDef level, string roomId)
    {
        if (level == null || string.IsNullOrEmpty(roomId)) return;
        level.rooms?.RemoveAll(r => r != null && r.id == roomId);
        DropRoomSensors(level, roomId);
    }

    /// <summary>
    /// The device half of the cascade, for Sync, which removes the room by rebuilding the list rather
    /// than by RemoveAll, but must drop exactly the same devices.
    /// </summary>
    public static void DropRoomSensors(LevelDef level, string roomId)
    {
        if (level == null || string.IsNullOrEmpty(roomId)) return;
        level.sensors?.RemoveAll(s => s != null && s.hostKind == SensorHost.Room && s.hostId == roomId);
    }

    /// <summary>
    /// "Bedroom", then "Bedroom 2", "Bedroom 3": the next free name for a type on this level.
    /// An untyped room is just "Room", because that is all anyone has said about it.
    /// </summary>
    public static string AutoName(LevelDef level, string roomType)
    {
        string stem = Pretty(roomType);
        int n = 1;
        if (level?.rooms != null)
            foreach (var r in level.rooms)
                if (r != null && r.roomType == roomType) n++;

        for (int guard = 0; guard < 512; guard++)
        {
            string candidate = n <= 1 ? stem : stem + " " + n;
            if (!NameTaken(level, candidate)) return candidate;
            n++;
        }
        return stem;
    }

    /// <summary>
    /// True when a name looks auto-generated for <paramref name="roomType"/>: "Bedroom", "Bedroom 2".
    ///
    /// This is how a retype knows whether it may rename: a name the user typed is never overwritten,
    /// an auto-generated one always is. Derived rather than flagged, so it costs no schema field, and
    /// clearing the name field is then a discoverable way to get the auto-name back, with no UI for it.
    /// </summary>
    public static bool IsAutoName(string name, string roomType)
    {
        if (string.IsNullOrEmpty(name)) return true;
        string stem = Pretty(roomType);
        if (name == stem) return true;
        if (!name.StartsWith(stem + " ")) return false;
        string tail = name.Substring(stem.Length + 1);
        if (tail.Length == 0) return false;
        foreach (char c in tail) if (c < '0' || c > '9') return false;
        return true;
    }

    /// <summary>"bedroom" -> "Bedroom". Mirrors UITheme.PrettyId, which CXRAuthoring cannot reach.</summary>
    public static string Pretty(string roomType)
    {
        if (string.IsNullOrEmpty(roomType)) return "Room";
        if (roomType == RoomType.Untyped) return "Room";
        var chars = roomType.Replace('_', ' ').ToCharArray();
        chars[0] = char.ToUpperInvariant(chars[0]);
        return new string(chars);
    }

    private static bool NameTaken(LevelDef level, string name)
    {
        if (level?.rooms == null) return false;
        foreach (var r in level.rooms) if (r != null && r.name == name) return true;
        return false;
    }

    private static string Label(RoomDef r)
        => !string.IsNullOrEmpty(r?.name) ? r.name : Pretty(r?.roomType);

    private static float AreaOf(RoomDef r)
        => PolygonTriangulator.Area(PolygonTriangulator.ToVector2(r?.polygon));

    private static int RegionAt(List<Region> regions, Vector2 p)
    {
        for (int r = 0; r < regions.Count; r++)
            if (ResidenceMetrics.PointInPolygon(p, regions[r].ring)) return r;
        return -1;
    }

    /// <summary>
    /// True exactly when <see cref="Sync"/> would leave every stored polygon untouched: same count,
    /// and each region's ring is float-identical to some distinct stored room polygon. The Rooms
    /// rail's Detect button is gated on this rather than on counts alone, so stored rooms that have
    /// drifted in SHAPE (a pre-fix save, a hand-traced residence) still surface the repair handle.
    /// </summary>
    public static bool RoomsMatch(LevelDef level, List<Region> regions)
    {
        int rooms = level?.rooms?.Count ?? 0;
        if (regions == null || regions.Count != rooms) return false;

        var taken = new bool[rooms];
        foreach (var region in regions)
        {
            var ring = PolygonTriangulator.ToArray(region.ring);
            int hit = -1;
            for (int i = 0; i < rooms; i++)
            {
                if (taken[i]) continue;
                if (SamePolygon(level.rooms[i]?.polygon, ring)) { hit = i; break; }
            }
            if (hit < 0) return false;
            taken[hit] = true;
        }
        return true;
    }

    private static bool SamePolygon(float[][] a, float[][] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == null || b[i] == null || a[i].Length < 2 || b[i].Length < 2) return false;
            if (a[i][0] != b[i][0] || a[i][1] != b[i][1]) return false;
        }
        return true;
    }

    // ---- graph helpers -------------------------------------------------------------------------

    private static void AddEdge(HashSet<long> edges, int u, int v)
    {
        if (u == v) return;
        int lo = Mathf.Min(u, v), hi = Mathf.Max(u, v);
        edges.Add(((long)lo << 32) | (uint)hi);
    }

    private static List<int>[] BuildAdjacency(int count, HashSet<long> edges)
    {
        var adj = new List<int>[count];
        foreach (long key in edges)
        {
            int u = (int)(key >> 32), v = (int)(key & 0xFFFFFFFFL);
            (adj[u] ??= new List<int>()).Add(v);
            (adj[v] ??= new List<int>()).Add(u);
        }
        return adj;
    }

    private static void Prune(List<int>[] adj, HashSet<long> edges)
    {
        var work = new Queue<int>();
        for (int v = 0; v < adj.Length; v++)
            if (adj[v] != null && adj[v].Count == 1) work.Enqueue(v);

        while (work.Count > 0)
        {
            int v = work.Dequeue();
            if (adj[v] == null || adj[v].Count != 1) continue;
            int n = adj[v][0];
            adj[v].Clear();
            adj[n].Remove(v);
            int lo = Mathf.Min(v, n), hi = Mathf.Max(v, n);
            edges.Remove(((long)lo << 32) | (uint)hi);
            if (adj[n].Count == 1) work.Enqueue(n);
        }
    }

    private static void SortByKey<T>(List<T> items, List<float> keys)
    {
        for (int i = 1; i < items.Count; i++)
        {
            T item = items[i];
            float key = keys[i];
            int j = i - 1;
            while (j >= 0 && keys[j] > key) { items[j + 1] = items[j]; keys[j + 1] = keys[j]; j--; }
            items[j + 1] = item;
            keys[j + 1] = key;
        }
    }

    // ---- polygon helpers -----------------------------------------------------------------------
    // Local rather than ResidenceMetrics' RoomDef-shaped overloads: a Region has no RoomDef yet.

    private static Vector2 Centroid(List<Vector2> poly)
    {
        float a = 0f, cx = 0f, cy = 0f;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 p = poly[i], q = poly[(i + 1) % poly.Count];
            float cross = p.x * q.y - q.x * p.y;
            a += cross;
            cx += (p.x + q.x) * cross;
            cy += (p.y + q.y) * cross;
        }
        if (Mathf.Abs(a) < ResidenceConventions.EPS)
        {
            Vector2 sum = Vector2.zero;
            foreach (var p in poly) sum += p;
            return sum / Mathf.Max(1, poly.Count);
        }
        return new Vector2(cx / (3f * a), cy / (3f * a));
    }

    private static Vector2 InsideOf(List<Vector2> poly)
    {
        var c = ResidenceMetrics.LargestInscribedCircle(poly);
        return c.valid ? c.center : Centroid(poly);
    }
}
