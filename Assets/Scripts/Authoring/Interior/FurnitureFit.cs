using System.Collections.Generic;
using UnityEngine;

// Keeps a piece of furniture out of the doorway it was dropped in.
//
// Nothing downstream objects to an item standing in an opening. WallLayout emits solid boxes only
// BETWEEN openings, so there is no geometry to collide with and no warning to raise: a dresser across
// a bedroom door simply renders as a dresser across a bedroom door. PlanBuilder learned this the hard
// way and grew its own avoidance for the authored samples; this is the same rule for everything
// placed by hand, which until now had none at all. OpeningFit guarded doors while the Furniture tool
// wrote the cursor position straight into the level.
//
// The contract is OpeningFit's, deliberately: SLIDE to the nearest legal spot rather than refuse, and
// return a `reason` written to be shown verbatim in the rail. Someone dragging a wardrobe toward a
// wall should watch it settle beside the door, not have the click swallowed.
//
// Two rules are worth understanding before changing anything here:
//
//   * Only openings the item can actually REACH block it. A window sill is 0.914 m and a sofa 0.84 m,
//     so the sofa passes underneath and belongs there; a kitchen run belongs under a window too. Any
//     rule that treats every opening as solid pushes correct layouts apart, which is why the test for
//     this in SampleHomesTests is written against the sill and not against the opening.
//   * Only walls the item is actually AGAINST are considered. Openings in perpendicular walls are
//     left alone on purpose. PlanBuilder tried reserving an approach strip in front of them and it
//     was reverted, because a counter run is supposed to reach the corner beside a cased opening.
public static class FurnitureFit
{
    // Matches PlanBuilder's tolerance, and the ±TOL inflation below means "just touching" counts as
    // blocked: a slide has to leave a sliver of daylight, not a shared edge.
    private const float TOL = 0.002f;

    // How close the footprint has to come to a wall centerline to count as against it. The same gate
    // SampleHomesTests uses, and comfortably clear of the ~0.077 m a flush item sits at.
    private const float NEAR = 0.10f;

    // A corner can put an item against two walls, and clearing a door on one can slide it into a door
    // on the other. Settling takes very few passes; the cap is only there so a pathological plan
    // cannot spin.
    private const int MAX_PASSES = 4;

    public struct Result
    {
        public bool ok;             // false => nothing legal was found; the caller places anyway
        public Vector2 position;    // world XZ; always set, even when !ok
        public bool moved;          // true => the request was slid to make it fit
        public string reason;       // human-readable; null when the request was already legal
    }

    public struct MountResult
    {
        public bool ok;
        public float offset;        // meters along the wall from `a`, to the item's center
        public bool moved;
        public string reason;
    }

    /// <summary>
    /// The nearest position to <paramref name="desired"/> at which an item of
    /// <paramref name="footprintXZ"/> and <paramref name="height"/> stands clear of every opening it
    /// is tall enough to reach.
    /// </summary>
    /// <param name="footprintXZ">Axis-aligned world extent, i.e. already rotated. See
    /// <see cref="Footprint"/>.</param>
    public static Result Fit(Vector2 desired, Vector2 footprintXZ, float height, LevelDef level)
    {
        var result = new Result { ok = true, position = desired, moved = false, reason = null };
        if (level?.walls == null || level.openings == null || level.openings.Count == 0) return result;

        for (int pass = 0; pass < MAX_PASSES; pass++)
        {
            if (!WorstConflict(result.position, footprintXZ, height, level,
                               out WallDef wall, out List<Vector2> blocked))
                return result;

            Project(result.position, footprintXZ, wall, out Vector2 dir, out float tMin, out float tMax);
            float center = 0.5f * (tMin + tMax);
            float size = tMax - tMin;

            if (!TrySlide(center, size, blocked, WallLength(wall), out float slid))
            {
                // Nothing on this wall is wide enough. Leave the item where the caller put it and say
                // so. Refusing the placement outright would be worse, and the caller shows the reason.
                result.ok = false;
                result.reason = "No clear stretch of this wall is wide enough for that.";
                return result;
            }

            result.position += dir * (slid - center);
            result.moved = true;
            result.reason = "Moved clear of the opening.";
        }

        return result;
    }

    /// <summary>
    /// The nearest offset to <paramref name="desiredOffset"/> at which a wall-mounted item of
    /// <paramref name="width"/> spanning <paramref name="bottom"/>..<paramref name="top"/> clears
    /// every opening on <paramref name="wall"/>.
    /// </summary>
    /// <remarks>
    /// Unlike floor furniture this is bounded by the wall: a grab bar hanging off the end of its own
    /// wall is not a placement, it is a bug, which is the second half of what PlanBuilder.BuildMounts
    /// had to fix.
    /// </remarks>
    public static MountResult FitMount(float desiredOffset, float width, float bottom, float top,
                                       WallDef wall, IReadOnlyList<OpeningDef> openings)
    {
        var result = new MountResult { ok = true, offset = desiredOffset, moved = false, reason = null };

        float length = WallLength(wall);
        if (length <= HomeConventions.EPS) return result;

        float w = Mathf.Max(0.02f, width);
        float min = 0.5f * w;
        float max = length - 0.5f * w;
        if (min > max + TOL)
        {
            result.ok = false;
            result.offset = 0.5f * length;
            result.moved = true;
            result.reason = $"Wider than this wall ({Units.Format(length)}).";
            return result;
        }

        var blocked = BlockedOn(wall, openings, bottom, top);
        float clamped = Mathf.Clamp(desiredOffset, min, max);
        if (IsClear(clamped, w, blocked))
        {
            result.offset = clamped;
            result.moved = !Mathf.Approximately(clamped, desiredOffset);
            if (result.moved) result.reason = "Moved onto the wall.";
            return result;
        }

        if (!TrySlideWithin(desiredOffset, w, min, max, blocked, out float slid))
        {
            result.ok = false;
            result.offset = clamped;
            result.moved = true;
            result.reason = "No clear stretch of this wall is wide enough for that.";
            return result;
        }

        result.offset = slid;
        result.moved = true;
        result.reason = "Moved clear of the opening.";
        return result;
    }

    /// <summary>
    /// The axis-aligned world extent of a <paramref name="widthM"/> x <paramref name="depthM"/> item
    /// turned by <paramref name="yawDeg"/>.
    /// </summary>
    /// <remarks>
    /// The true bound of the rotated rectangle, not the quarter-turn swap HomeMetrics.FootprintOf
    /// does. Both agree on the axis-aligned cases every sample uses, but the Furniture tool hands out
    /// 15-degree steps and a continuous slider, and at 45 degrees the swap understates the extent by
    /// most of a diagonal. Overstating is the safe direction for a clearance test; understating puts
    /// a corner in a doorway.
    /// </remarks>
    public static Vector2 Footprint(float widthM, float depthM, float yawDeg)
    {
        float rad = yawDeg * Mathf.Deg2Rad;
        float c = Mathf.Abs(Mathf.Cos(rad));
        float s = Mathf.Abs(Mathf.Sin(rad));
        return new Vector2(widthM * c + depthM * s, widthM * s + depthM * c);
    }

    // ---------------------------------------------------------------------------------------------

    // The wall this item most badly conflicts with, and everything blocking it there. Picking the
    // worst rather than the first means a corner settles on the opening that actually matters.
    private static bool WorstConflict(Vector2 center, Vector2 fp, float height, LevelDef level,
                                      out WallDef wall, out List<Vector2> blocked)
    {
        wall = null;
        blocked = null;
        float worst = TOL;

        foreach (var w in level.walls)
        {
            if (w == null || WallLength(w) <= HomeConventions.EPS) continue;
            if (!IsAgainst(center, fp, w, out float tMin, out float tMax)) continue;

            var spans = BlockedOn(w, level.openings, 0f, height);
            if (spans.Count == 0) continue;

            foreach (var b in spans)
            {
                float overlap = Mathf.Min(tMax, b.y) - Mathf.Max(tMin, b.x);
                if (overlap <= worst) continue;
                worst = overlap;
                wall = w;
                blocked = spans;
            }
        }

        return wall != null;
    }

    // Whether the footprint reaches this wall's centerline, and where it sits along it. Projecting the
    // corners handles walls at any angle; for the axis-aligned case it reduces to the obvious test.
    private static bool IsAgainst(Vector2 center, Vector2 fp, WallDef w, out float tMin, out float tMax)
    {
        tMin = tMax = 0f;

        Project(center, fp, w, out _, out tMin, out tMax);
        float length = WallLength(w);
        if (tMax < -NEAR || tMin > length + NEAR) return false;

        var a = new Vector2(w.a[0], w.a[1]);
        var dir = Direction(w);
        var nrm = new Vector2(-dir.y, dir.x);

        float nMin = float.MaxValue, nMax = float.MinValue;
        foreach (var corner in Corners(center, fp))
        {
            float n = Vector2.Dot(corner - a, nrm);
            nMin = Mathf.Min(nMin, n);
            nMax = Mathf.Max(nMax, n);
        }

        float distance = nMin <= 0f && nMax >= 0f ? 0f : Mathf.Min(Mathf.Abs(nMin), Mathf.Abs(nMax));
        return distance <= NEAR;
    }

    private static void Project(Vector2 center, Vector2 fp, WallDef w,
                                out Vector2 dir, out float tMin, out float tMax)
    {
        var a = new Vector2(w.a[0], w.a[1]);
        dir = Direction(w);

        tMin = float.MaxValue;
        tMax = float.MinValue;
        foreach (var corner in Corners(center, fp))
        {
            float t = Vector2.Dot(corner - a, dir);
            tMin = Mathf.Min(tMin, t);
            tMax = Mathf.Max(tMax, t);
        }
    }

    private static IEnumerable<Vector2> Corners(Vector2 center, Vector2 fp)
    {
        float hx = 0.5f * fp.x, hz = 0.5f * fp.y;
        yield return new Vector2(center.x - hx, center.y - hz);
        yield return new Vector2(center.x + hx, center.y - hz);
        yield return new Vector2(center.x + hx, center.y + hz);
        yield return new Vector2(center.x - hx, center.y + hz);
    }

    // The runs of this wall an item spanning `bottom`..`top` cannot stand in, in wall-local meters.
    // The height test is the whole reason a sofa may sit under a window: overlap has to be genuine in
    // BOTH axes before an opening counts.
    private static List<Vector2> BlockedOn(WallDef wall, IReadOnlyList<OpeningDef> openings,
                                           float bottom, float top)
    {
        var spans = new List<Vector2>();
        if (wall == null || openings == null) return spans;

        foreach (var o in openings)
        {
            if (o == null || o.wallId != wall.id) continue;
            if (o.width <= HomeConventions.EPS) continue;

            float sill = o.sillHeight;
            float head = sill + (o.height > HomeConventions.EPS ? o.height : float.MaxValue);
            if (top <= sill + TOL) continue;
            if (bottom >= head - TOL) continue;

            spans.Add(new Vector2(o.offset - 0.5f * o.width, o.offset + 0.5f * o.width));
        }

        return spans;
    }

    private static bool IsClear(float center, float size, List<Vector2> blocked)
    {
        float s = center - 0.5f * size, e = center + 0.5f * size;
        foreach (var b in blocked)
            if (e > b.x - TOL && s < b.y + TOL) return false;
        return true;
    }

    // Unbounded along the wall, on purpose: a counter run legitimately overhangs the end of the wall
    // segment it sits against, and clamping the slide to the segment would drag correct placements
    // back from corners. `length` only breaks ties toward staying on the wall.
    private static bool TrySlide(float want, float size, List<Vector2> blocked, float length,
                                 out float result)
    {
        result = want;
        if (IsClear(want, size, blocked)) return true;

        float best = float.MaxValue;
        bool found = false;

        foreach (var b in blocked)
        {
            foreach (float c in new[] { b.x - 0.5f * size - 2f * TOL, b.y + 0.5f * size + 2f * TOL })
            {
                if (!IsClear(c, size, blocked)) continue;

                // Distance moved decides, with a nudge toward candidates that keep the item on the
                // wall so a tie at a corner does not throw it outside.
                float d = Mathf.Abs(c - want) + (c < 0f || c > length ? 0.001f : 0f);
                if (d >= best) continue;
                best = d;
                result = c;
                found = true;
            }
        }

        return found;
    }

    // The bounded form, for wall mounts. Same candidate set as PlanBuilder.TrySlideClear: the only
    // positions worth testing are hard against one side of a blocker, or either end of the run.
    private static bool TrySlideWithin(float want, float size, float min, float max,
                                       List<Vector2> blocked, out float result)
    {
        result = want;

        var candidates = new List<float> { min, max };
        foreach (var b in blocked)
        {
            candidates.Add(b.x - 0.5f * size - 2f * TOL);
            candidates.Add(b.y + 0.5f * size + 2f * TOL);
        }

        float best = float.MaxValue;
        bool found = false;
        foreach (float c in candidates)
        {
            if (c < min - TOL || c > max + TOL) continue;
            float p = Mathf.Clamp(c, min, max);
            if (!IsClear(p, size, blocked)) continue;

            float d = Mathf.Abs(p - want);
            if (d >= best) continue;
            best = d;
            result = p;
            found = true;
        }

        return found;
    }

    private static float WallLength(WallDef w) => WallLayout.WallLength(w);

    private static Vector2 Direction(WallDef w)
    {
        var a = new Vector2(w.a[0], w.a[1]);
        var b = new Vector2(w.b[0], w.b[1]);
        float len = WallLength(w);
        return len <= HomeConventions.EPS ? Vector2.right : (b - a) / len;
    }
}
