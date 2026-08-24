using System.Collections.Generic;
using UnityEngine;

// Turns a wall's WallLayout boxes into renderable geometry.
//
// JUNCTIONS: the deliberate simplification. Where two walls meet, the textbook solution is a miter:
// solve the intersection of the two pairs of face lines and trim both walls to it. That is a lot of
// fiddly maths with degenerate cases at shallow angles, T-junctions, and three-way corners. Instead
// each wall is EXTENDED half a neighbor-thickness past any endpoint it shares with another wall, so
// the two boxes simply overlap inside the corner:
//
//        miter (not done)              overlap (done)
//        ┌─────┬─────                  ┌─────┬─────
//        │    ╱│                       │█████│
//        │   ╱ │                       ├─────┤
//        │  ╱  │                       │█████│
//
// For opaque solids the overlap is invisible (you cannot see inside a wall) and it is robust at
// every angle and valence with no special cases. The cost is deferred, not avoided: cutaway views,
// transparent walls, and exported geometry for a contractor would all reveal the overlap and would
// need real mitering. That trade is recorded in the plan under Deferred.
//
// "Invisible" holds only while the overlap is a genuine overlap. Two coplanar faces at the SAME depth
// are not hidden, they Z-FIGHT, and the extension above walked straight into that twice. See
// ComputeExtensions for both cases and for the two rules that fix them (a collinear run is not a
// corner and gets nothing; every other extension stops JunctionBias short of the face it would
// otherwise land on).
//
// Geometry is emitted in WORLD-AXIS offsets from the wall's start point, so the host GameObject sits
// at that point with identity rotation. No rotation maths at the call site, and normals need no
// transforming.
public static class WallMeshBuilder
{
    public const int SUB_LEFT  = 0;   // the face on the left when walking a -> b  (materialLeft)
    public const int SUB_RIGHT = 1;   // the face on the right                     (materialRight)
    public const int SUB_EDGE  = 2;   // top, bottom, end caps, and door/window reveals
    public const int SUB_COUNT = 3;

    // The wall's local frame in world space.
    public struct Frame
    {
        public Vector3 origin;      // world position of endpoint `a` at floor level
        public Vector3 forward;     // unit, a -> b
        public Vector3 left;        // unit, horizontal, 90° left of forward
        public float length;
        public float thickness;
        public float height;
    }

    public static Frame BuildFrame(WallDef w, LevelDef level)
    {
        float length = WallLayout.WallLength(w);
        Vector3 a = new Vector3(w.a[0], level?.elevation ?? 0f, w.a[1]);
        Vector3 fwd = Vector3.forward;
        if (length > HomeConventions.EPS)
            fwd = new Vector3(w.b[0] - w.a[0], 0f, w.b[1] - w.a[1]) / length;

        return new Frame
        {
            origin = a,
            forward = fwd,
            // left = Cross(forward, up) under Unity's left-hand rule; for forward (1,0,0) this is
            // (0,0,1), i.e. the wall's left side when walking a -> b.
            left = Vector3.Cross(fwd, Vector3.up),
            length = length,
            thickness = WallLayout.EffectiveThickness(w, level),
            height = WallLayout.EffectiveHeight(w, level),
        };
    }

    /// <summary>
    /// How far short of the neighbor's far face an extended end stops. Landing exactly ON that face
    /// is the one arrangement in this whole scheme that Z-FIGHTS, and it used to be the arrangement at
    /// every corner: the end cap is SUB_EDGE and the face it lands on is SUB_LEFT/RIGHT, so a corner
    /// drew two different materials at identical depth over a full-height strip a wall thick. A
    /// millimetre back and the cap is INSIDE the neighbor, which occludes it outright.
    ///
    /// 1 mm is ~10x the worst depth resolution this scene can produce (near 0.05, far 500) and small
    /// enough that the notch it leaves (1 mm square, at the tip of an outside corner) cannot be seen.
    /// </summary>
    public const float JunctionBias = 0.001f;

    /// <summary>
    /// How far each end of this wall should push past its endpoint so shared corners close. Returns
    /// half the thickest connected wall at each end, less <see cref="JunctionBias"/>; 0 for a free end
    /// and 0 where the run simply continues. Using the NEIGHBOUR's thickness is what actually fills the
    /// notch: a thin wall meeting a thick one still has to reach the thick wall's outer face.
    ///
    /// A COLLINEAR neighbor is not a corner and gets no extension at all. That is the common case, not
    /// the odd one: every crossing in this app is split at the crossing point (PlanBuilder derives the
    /// walls that way, WallLinker enforces it for hand-drawn ones), so a plan is full of collinear
    /// pieces sharing an endpoint. Their boxes already abut exactly, and extending them only buries a
    /// wall's thickness of duplicate coplanar face inside the neighbor. Invisible while both pieces
    /// carry the same finish, and a flickering band the moment they do not.
    /// </summary>
    public static void ComputeExtensions(WallDef wall, LevelDef level, out float startExt, out float endExt)
    {
        startExt = 0f;
        endExt = 0f;
        if (wall?.a == null || wall.b == null || level?.walls == null) return;

        float length = WallLayout.WallLength(wall);
        if (length <= HomeConventions.EPS) return;

        var a = new Vector2(wall.a[0], wall.a[1]);
        var b = new Vector2(wall.b[0], wall.b[1]);
        Vector2 dir = (b - a) / length;

        bool startContinues = false, endContinues = false;

        foreach (var other in level.walls)
        {
            if (other == null || other.id == wall.id) continue;
            if (other.a == null || other.b == null) continue;
            float otherLength = WallLayout.WallLength(other);
            if (otherLength <= HomeConventions.EPS) continue;

            var oa = new Vector2(other.a[0], other.a[1]);
            var ob = new Vector2(other.b[0], other.b[1]);

            bool atStart = Near(a, oa) || Near(a, ob);
            bool atEnd   = Near(b, oa) || Near(b, ob);
            if (!atStart && !atEnd) continue;

            // Same test WallLinker uses to decide a contact is overlap rather than a junction, so the
            // two cannot drift apart on what counts as "the same run".
            Vector2 odir = (ob - oa) / otherLength;
            if (Mathf.Abs(dir.x * odir.y - dir.y * odir.x) < WallLinker.MinJunctionSin)
            {
                if (atStart) startContinues = true;
                if (atEnd) endContinues = true;
                continue;
            }

            float halfT = 0.5f * WallLayout.EffectiveThickness(other, level);
            if (atStart) startExt = Mathf.Max(startExt, halfT);
            if (atEnd)   endExt   = Mathf.Max(endExt, halfT);
        }

        startExt = startContinues ? 0f : Mathf.Max(0f, startExt - JunctionBias);
        endExt   = endContinues   ? 0f : Mathf.Max(0f, endExt   - JunctionBias);
    }

    /// <summary>Everything at once: layout, junctions, mesh. The normal entry point.</summary>
    public static Mesh Build(WallDef wall, LevelDef level)
    {
        var frame = BuildFrame(wall, level);
        if (frame.length <= HomeConventions.EPS) return null;

        var boxes = WallLayout.Build(frame.length, frame.height, WallLayout.OpeningsFor(wall, level));
        ComputeExtensions(wall, level, out float startExt, out float endExt);
        return Build(frame, boxes, startExt, endExt);
    }

    /// <summary>
    /// Mesh for pre-computed boxes. Split out so tests can drive exact box lists and so a future
    /// cutaway/section view can request geometry without junction extension.
    /// </summary>
    public static Mesh Build(Frame frame, IReadOnlyList<WallLayout.Box> boxes, float startExt, float endExt)
    {
        if (boxes == null || boxes.Count == 0) return null;

        var acc = new MeshAccum(SUB_COUNT);
        foreach (var box in boxes)
            AppendBox(acc, frame, box, startExt, endExt);

        return acc.ToMesh("Wall");
    }

    // ---------------------------------------------------------------------------------------

    private static void AppendBox(MeshAccum acc, in Frame f, in WallLayout.Box box,
                                  float startExt, float endExt)
    {
        // Only boxes actually touching an end get extended, so a door reveal in the middle of a wall
        // is never stretched and a header that reaches the corner still closes it.
        float t0 = box.t0 <= HomeConventions.EPS ? -startExt : box.t0;
        float t1 = box.t1 >= f.length - HomeConventions.EPS ? f.length + endExt : box.t1;
        if (t1 - t0 <= HomeConventions.EPS) return;

        float h = 0.5f * f.thickness;
        float y0 = box.y0, y1 = box.y1;

        Vector3 F = f.forward, L = f.left, U = Vector3.up;

        // Corner helper: (along, across, up) -> world offset from the wall's start point.
        Vector3 P(float t, float x, float y) => F * t + L * x + U * y;

        // Each face below picks in-plane axes u, v with Cross(u, v) == its outward normal, then lists
        // corners as p0, p0+u, p0+u+v, p0+v. See the winding note in MeshAccum.
        // Relations used: Cross(F,U) = L, Cross(U,F) = -L, Cross(L,F) = U, Cross(F,L) = -U,
        //                 Cross(U,L) = F, Cross(L,U) = -F.

        // LEFT face (normal = L), at x = +h. u = F, v = U.
        acc.AddQuad(
            P(t0, h, y0), P(t1, h, y0), P(t1, h, y1), P(t0, h, y1),
            L,
            new Vector2(t0, y0), new Vector2(t1, y0), new Vector2(t1, y1), new Vector2(t0, y1),
            SUB_LEFT);

        // RIGHT face (normal = -L), at x = -h. u = U, v = F.
        acc.AddQuad(
            P(t0, -h, y0), P(t0, -h, y1), P(t1, -h, y1), P(t1, -h, y0),
            -L,
            new Vector2(t0, y0), new Vector2(t0, y1), new Vector2(t1, y1), new Vector2(t1, y0),
            SUB_RIGHT);

        // TOP (normal = U), at y = y1. u = L, v = F.
        acc.AddQuad(
            P(t0, -h, y1), P(t0, h, y1), P(t1, h, y1), P(t1, -h, y1),
            U,
            new Vector2(-h, t0), new Vector2(h, t0), new Vector2(h, t1), new Vector2(-h, t1),
            SUB_EDGE);

        // BOTTOM (normal = -U), at y = y0. u = F, v = L.
        acc.AddQuad(
            P(t0, -h, y0), P(t1, -h, y0), P(t1, h, y0), P(t0, h, y0),
            -U,
            new Vector2(t0, -h), new Vector2(t1, -h), new Vector2(t1, h), new Vector2(t0, h),
            SUB_EDGE);

        // START cap (normal = -F), at t = t0. u = L, v = U. This is the door reveal on one side.
        acc.AddQuad(
            P(t0, -h, y0), P(t0, h, y0), P(t0, h, y1), P(t0, -h, y1),
            -F,
            new Vector2(-h, y0), new Vector2(h, y0), new Vector2(h, y1), new Vector2(-h, y1),
            SUB_EDGE);

        // END cap (normal = +F), at t = t1. u = U, v = L.
        acc.AddQuad(
            P(t1, -h, y0), P(t1, -h, y1), P(t1, h, y1), P(t1, h, y0),
            F,
            new Vector2(-h, y0), new Vector2(-h, y1), new Vector2(h, y1), new Vector2(h, y0),
            SUB_EDGE);
    }

    private static bool Near(Vector2 p, Vector2 q)
        => (p - q).sqrMagnitude <= HomeConventions.EPS * HomeConventions.EPS * 100f;
}
