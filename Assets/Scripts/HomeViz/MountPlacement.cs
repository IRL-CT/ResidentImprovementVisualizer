using UnityEngine;

// Putting a wall-mounted catalog item on a wall. Hovering for the host, fitting it clear of the
// openings it can reach, writing the WallMountDef, and previewing where it will actually land.
//
// WHY THIS IS NOT JUST IN FurnitureTool ANY MORE: two rails now place grab bars. The Furnish rail
// always did, and the Smart living rail does too, because a grab bar beside a toilet is one of the
// things that stage exists to argue about, and it lives in FurnitureCatalog rather than in the
// device catalog, which is the right place for it and the reason nothing about the data moved.
//
// Two copies of this would be two chances for the two rails to disagree about which SIDE of a wall a
// click came from, or about whether a bar may overhang a doorway. That is the same reason
// HomeMetrics.NearestWall was lifted out of FurnitureTool when the wall-mount drag landed: placing
// and re-hosting must not each have their own opinion. This file is that call made once more.
//
// It deliberately covers the WALL half only. The floor half stays in FurnitureTool, because that is
// the only rail that places floor furniture and it carries the rotation state a floor item needs.
public static class MountPlacement
{
    /// <summary>
    /// The cursor for wall-aimed placement. A click on a wall's visible face projects to the floor
    /// BEHIND the wall under the angled camera, and NearestWall reads the mounting face from that
    /// point: the opposite one. The pick ray's hit on the wall itself lies on the clicked face, so
    /// it wins when there is one; the floor projection covers a cursor over open floor.
    /// </summary>
    public static bool WallCursor(HomeToolContext ctx, out Vector2 xz)
    {
        xz = Vector2.zero;
        if (ctx == null) return false;
        if (ctx.WallPoint(out xz)) return true;
        return ctx.GroundPoint(out xz);
    }

    /// <summary>Which wall a cursor is against, and where along it. Null when nothing is in reach.</summary>
    /// <remarks>
    /// HomeConventions.MOUNT_REACH (1.2 m) is deliberately generous. It has to cover half a wall
    /// thickness plus the slop of pointing at a 50 mm grab bar in a plan view, and the nearest wall
    /// wins regardless, so the reach only ever decides WHICH wall when there are two.
    /// </remarks>
    public static WallDef Hover(HomeToolContext ctx, Vector2 cursor, out float offset, out int side)
    {
        offset = 0f;
        side = WallSide.Left;
        if (ctx?.Level == null) return null;

        return HomeMetrics.NearestWall(cursor, ctx.Level.walls, HomeConventions.MOUNT_REACH,
                                       out offset, out side);
    }

    /// <summary>
    /// Slides the item to the nearest legal spot along its wall, clear of any opening its own
    /// vertical span actually reaches.
    /// </summary>
    /// <remarks>
    /// The bottom/top pair assumes a CENTER anchor, which is what every shipped catalog row uses
    /// (decorAnchor 0) and what FurnitureCatalog.NewWallMount stamps. A 36" grab bar at 0.84 m
    /// therefore spans 0.82 to 0.86, so it is blocked by a door and not by a window whose sill is at
    /// 0.914, which is the rule FurnitureFit's header states and the whole reason this is not a
    /// simple clamp.
    /// </remarks>
    public static FurnitureFit.MountResult Fit(FurnitureCatalog.Entry entry, float offset,
                                               WallDef wall, LevelDef level)
        => FurnitureFit.FitMount(offset,
                                 entry.widthM,
                                 entry.mountHeightM - 0.5f * entry.heightM,
                                 entry.mountHeightM + 0.5f * entry.heightM,
                                 wall,
                                 level?.openings);

    /// <summary>
    /// Records the edit, writes the mount, and selects it. Returns false when there is no wall in
    /// reach, having said so: the caller has nothing left to do in that case.
    /// </summary>
    /// <remarks>
    /// reveal: false is MANDATORY, for the reason FurnitureTool.Place gives: selecting is a side
    /// effect of placing rather than the point of it, and jumping to the Select tab after every bar
    /// would make fitting out a bathroom impossible.
    /// </remarks>
    public static bool Place(HomeToolContext ctx, FurnitureCatalog.Entry entry,
                             WallDef wall, float offset, int side)
    {
        if (ctx?.Level == null || entry == null) return false;
        if (wall == null)
        {
            ctx.Controller.Status("Move closer to a wall to mount this.");
            return false;
        }

        var fit = Fit(entry, offset, wall, ctx.Level);

        ctx.RecordEdit("Place " + entry.Label);
        var mount = FurnitureCatalog.NewWallMount(entry, wall.id, fit.offset, side);
        ctx.Level.wallMounted.Add(mount);
        ctx.Controller.Select(HomeElementMarker.Kind.WallMount, mount.instanceId, reveal: false);

        // The fit slides rather than refuses, so a placement that could not be made cleanly still
        // happens and says why. Swallowing a click is the one outcome that reads as a broken tool.
        if (!fit.ok)
            ctx.Controller.Status($"Placed {entry.Label}, but it does not fit cleanly: {fit.reason}");
        else if (fit.moved)
            ctx.Controller.Status($"Placed {entry.Label}: {fit.reason}");

        ctx.Changed();
        return true;
    }

    /// <summary>
    /// Where the item will actually land, for the ghost. Not the cursor: the fit may have slid it
    /// clear of a doorway, and a preview that ignored that would be a promise the click breaks.
    /// </summary>
    public static Vector2 Ghost(FurnitureCatalog.Entry entry, float offset, WallDef wall, LevelDef level)
        => HomeMetrics.PointOnWall(wall, Fit(entry, offset, wall, level).offset);
}
