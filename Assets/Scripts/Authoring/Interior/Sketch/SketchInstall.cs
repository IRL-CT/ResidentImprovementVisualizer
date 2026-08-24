using System.Collections.Generic;

// Moving a generated level into a story that already exists.
//
// A STOREY IS A FACT ABOUT THE BUILDING; WHAT IS IN IT IS THE DESIGN. That is the sentence the whole
// Stories feature rests on, and it decides everything here: the destination keeps its id, its name
// and its elevation, because those identify the floor rather than describe it. VariantDiff.MatchLevel
// pairs stories by id and UnderlayDef.levelId keys a sketch by it, so a generated level that brought
// its own id along would orphan the very sketch it was generated from.
//
// REPLACE, NEVER MERGE. Dropping a second self-consistent wall graph on top of an existing one
// produces crossings only WallLinker.Relink can resolve (which mints new wall ids) and rooms that
// then disagree with the graph, which only RoomRegions.Sync can resolve, which mints fresh guids and
// makes the next comparison report "removed and added" instead of "changed". That is the failure
// VariantRevertTests has a dedicated case for. Two derivations of one truth is how this codebase
// gets its notches, so there is one derivation: PlanBuilder's.
public static class SketchInstall
{
    /// <summary>Nothing has been drawn on this floor yet, so generating onto it costs nothing.</summary>
    public static bool IsEmpty(LevelDef level)
    {
        if (level == null) return true;
        return Count(level.walls) == 0
            && Count(level.rooms) == 0
            && Count(level.furniture) == 0
            && Count(level.wallMounted) == 0
            && Count(level.openings) == 0;
    }

    /// <summary>
    /// What replacing this floor would discard, for the button's own label.
    ///
    /// A destructive button that states its price beats one that does not: the rule the
    /// reset-to-sample confirmation already follows.
    /// </summary>
    public static string ContentSummary(LevelDef level)
    {
        if (level == null) return "nothing";

        var parts = new List<string>(4);
        Add(parts, Count(level.walls), "wall", "walls");
        Add(parts, Count(level.rooms), "room", "rooms");
        Add(parts, Count(level.furniture) + Count(level.wallMounted), "item", "items");
        Add(parts, Count(level.sensors), "device", "devices");

        if (parts.Count == 0) return "nothing";
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Copies the generated contents into <paramref name="destination"/>, keeping everything that
    /// identifies the floor and re-stemming every id so two generated stories in one residence cannot
    /// collide in ResidenceRenderer's single flat id dictionary.
    ///
    /// Sensors are left alone deliberately. A device hosts on an element, so most of them will not
    /// resolve against the new geometry, but that is the caller's decision to make in front of the
    /// user, not a silent consequence of tracing the floor again.
    /// </summary>
    public static void Adopt(LevelDef destination, LevelDef generated, string idPrefix)
    {
        if (destination == null || generated == null) return;

        SketchPlanCompiler.Reid(generated, idPrefix);

        destination.walls = generated.walls ?? new List<WallDef>();
        destination.openings = generated.openings ?? new List<OpeningDef>();
        destination.rooms = generated.rooms ?? new List<RoomDef>();
        destination.furniture = generated.furniture ?? new List<ObjectInstance>();
        destination.wallMounted = generated.wallMounted ?? new List<WallMountDef>();

        // id, name, elevation, ceilingHeight and wallThickness all stay: they are the floor, and the
        // generated level only ever borrowed PlanBuilder's defaults for them.
    }

    private static int Count<T>(List<T> list) => list?.Count ?? 0;

    private static void Add(List<string> parts, int n, string one, string many)
    {
        if (n > 0) parts.Add(n + " " + (n == 1 ? one : many));
    }
}
