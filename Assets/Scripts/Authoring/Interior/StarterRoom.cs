using System.Collections.Generic;
using UnityEngine;

// The one plain room a residence you start yourself opens on.
//
// WHY THIS EXISTS: ResidenceStore.Create used to hand back an empty ground floor, so a new residence
// was an empty grid with nothing to render and nothing to click. The only way forward was the Import
// stage, which is the hardest step in the whole workflow: import a sketch, calibrate it, trace it.
// That is the same complaint the six sample residences were invented to answer, and the samples only
// answer half of it: they are somewhere to look around, never somewhere to start something of your
// own. One room to stand in makes drawing a live alternative to importing rather than the thing you
// find afterwards.
//
// It is authored through PlanBuilder like every sample plan, for the reason PlanBuilder's own header
// gives: nothing downstream of the schema complains about bad geometry, so the walls are DERIVED
// (unioned, split, welded) rather than typed as four WallDefs that might miss each other by a
// millimetre and render with a notch.
public static class StarterRoom
{
    /// <summary>The room's side, in meters, on wall CENTERLINES: the project's convention, and the
    /// number the Select tool reports an area from (9.00 m2). Clear floor is a wall thickness less.</summary>
    public const float Side = 3f;

    public const string RoomKey  = "living";
    public const string RoomName = "Living room";

    /// <summary>
    /// Stems every id this level carries. FIXED, unlike SketchPlanCompiler.NewPrefix's random stem,
    /// because <see cref="IsUntouched"/> recognises a starter room by rebuilding one and comparing:
    /// a fresh stem per residence would make that impossible. It still keeps these ids clear of
    /// PlanBuilder's bare w_ / r_ namespace.
    /// </summary>
    public const string IdPrefix = "s_";

    /// <summary>
    /// A <see cref="Side"/> x <see cref="Side"/> living room centered on the origin: four walls, one
    /// room, and nothing else. No door, deliberately: the Openings tool is how one gets there, and a
    /// starter room with an arbitrary door in an arbitrary wall is a decision nobody asked for.
    ///
    /// Centered rather than cornered at the origin so a fresh scene frames symmetrically; +/-1.5
    /// lands on every grid WallTool offers.
    /// </summary>
    public static LevelDef Build()
    {
        var b = new PlanBuilder();
        b.Room(RoomKey, RoomName, RoomType.Living, -0.5f * Side, -0.5f * Side, Side, Side);

        var level = b.Build();
        SketchPlanCompiler.Reid(level, IdPrefix);
        return level;
    }

    // What IsUntouched compares against, built once. SketchInstall.IsEmpty is read every frame the
    // Import rail is open, and deriving a plan per frame to answer it would be a needless allocation
    // in OnGUI. Nothing hands this out and IsUntouched only reads it, so the one copy stays pristine;
    // Build keeps handing callers a fresh level of their own to adopt and edit.
    private static LevelDef _reference;
    private static LevelDef Reference => _reference ??= Build();

    /// <summary>
    /// True exactly when this storey still holds what <see cref="Build"/> makes and nothing has been
    /// done to it. Rename the room, retype it, nudge a wall, cut a door, put a chair down or hang a
    /// device and this goes false, which is the whole point: from that instant the floor holds work,
    /// and SketchInstall.IsEmpty says so.
    /// </summary>
    public static bool IsUntouched(LevelDef level)
    {
        if (level == null) return false;
        if (Count(level.walls) != 4 || Count(level.rooms) != 1) return false;
        if (Count(level.openings) != 0 || Count(level.furniture) != 0
            || Count(level.wallMounted) != 0 || Count(level.sensors) != 0) return false;

        var starter = Reference;
        for (int i = 0; i < starter.walls.Count; i++)
            if (!SameWall(level.walls[i], starter.walls[i])) return false;

        for (int i = 0; i < starter.rooms.Count; i++)
            if (!SameRoom(level.rooms[i], starter.rooms[i])) return false;

        return true;
    }

    // Geometry and identity only. thickness and height are stored as 0 (inherit from the storey), so
    // a storey whose wall thickness or ceiling height was edited still compares equal here: those are
    // facts about the floor rather than work done on the room.
    private static bool SameWall(WallDef a, WallDef b)
        => a != null && b != null
        && a.id == b.id
        && SamePoint(a.a, b.a)
        && SamePoint(a.b, b.b);

    private static bool SameRoom(RoomDef a, RoomDef b)
    {
        if (a == null || b == null) return false;
        if (a.id != b.id || a.name != b.name || a.roomType != b.roomType) return false;
        if (a.polygon == null || b.polygon == null || a.polygon.Length != b.polygon.Length) return false;
        for (int i = 0; i < a.polygon.Length; i++)
            if (!SamePoint(a.polygon[i], b.polygon[i])) return false;
        return true;
    }

    private static bool SamePoint(float[] a, float[] b)
        => a != null && b != null && a.Length >= 2 && b.Length >= 2
        && Mathf.Abs(a[0] - b[0]) <= ResidenceConventions.EPS
        && Mathf.Abs(a[1] - b[1]) <= ResidenceConventions.EPS;

    private static int Count<T>(List<T> list) => list?.Count ?? 0;
}
