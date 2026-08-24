using System;
using System.Collections.Generic;

// Adding, removing and naming a story, and keeping each story's traced sketch attached to it.
//
// All of it is document surgery with no filesystem and no Unity in it, and it lives in CXRAuthoring
// for exactly the reason SampleRefresh does: so it can be tested at all. HomeStore is in
// Assembly-CSharp, which asmdefs cannot reference, and its static constructor touches
// Application.persistentDataPath, so anything left in that file is reachable only by running the
// Editor. HomeStore delegates here and keeps the parts that genuinely need a disk.
//
// THE RULE THIS FILE EXISTS TO HOLD: a story is a fact about the BUILDING, and what is in it is the
// design option. So a level is added to every variant at once, sharing ONE id. That is the same split
// HomeDoc.exteriorEnabled already makes against VariantDef.exterior, and it buys three things:
//
//   * Level ids match across variants, which is what VariantDiff.MatchLevel pairs stories by and what
//     UnderlayDef.levelId keys a sketch by. HomeStore.Clone is a JSON round trip, so a proposal
//     branched from the baseline already carries the same ids; adding a floor to one variant only
//     would break that on the very next comparison.
//   * The new level is empty on both sides of every diff, so adding one reports NO change.
//   * Because it asserts nothing about the home, it does not need the baseline unlocked. Drawing
//     anything on the new floor still does, which is where the lock should bite.
public static class Stories
{
    public static string DefaultName(int index) => index == 0 ? "Ground floor" : "Floor " + (index + 1);

    public static LevelDef NewLevel(string name, float elevation = 0f) => new LevelDef
    {
        id = Guid.NewGuid().ToString(),
        name = name,
        elevation = elevation,
        ceilingHeight = HomeConventions.DEFAULT_CEILING_HEIGHT,
        wallThickness = HomeConventions.DEFAULT_WALL_THICKNESS,
        walls = new List<WallDef>(),
        openings = new List<OpeningDef>(),
        rooms = new List<RoomDef>(),
        furniture = new List<ObjectInstance>(),
        wallMounted = new List<WallMountDef>(),
    };

    /// <summary>The variant the story list is counted from: the baseline, or the first there is.</summary>
    public static List<LevelDef> Reference(HomeDoc doc)
    {
        if (doc?.variants == null || doc.variants.Count == 0) return null;
        foreach (var v in doc.variants)
            if (v != null && v.isBaseline && v.levels != null) return v.levels;
        return doc.variants[0]?.levels;
    }

    public static int Count(HomeDoc doc) => Reference(doc)?.Count ?? 0;

    /// <summary>Adds a story to every variant, sharing one id, and returns its index.</summary>
    public static int Add(HomeDoc doc, string name = null)
    {
        var reference = Reference(doc);
        if (reference == null) return 0;

        int index = reference.Count;

        // Stacked on the story below, so walls, floors, sensors and the ground-plane raycast: all of
        // which read LevelDef.elevation. Land at the right height with nothing else to configure.
        float elevation = 0f;
        if (reference.Count > 0)
        {
            var below = reference[reference.Count - 1];
            float h = below.ceilingHeight > 0f ? below.ceilingHeight : HomeConventions.DEFAULT_CEILING_HEIGHT;
            elevation = below.elevation + h;
        }

        var template = NewLevel(name ?? DefaultName(index), elevation);

        foreach (var v in doc.variants)
        {
            if (v == null) continue;
            v.levels ??= new List<LevelDef>();
            var copy = NewLevel(template.name, template.elevation);
            copy.id = template.id;          // the SAME id in every variant. See the file header
            v.levels.Add(copy);
        }
        return index;
    }

    /// <summary>
    /// Removes a story from every variant, and the sketch traced for it. Refuses the last one: a home
    /// with no story has nowhere to draw and nothing to render.
    /// </summary>
    public static bool Remove(HomeDoc doc, int index, out string error)
    {
        error = null;
        var reference = Reference(doc);
        if (reference == null || index < 0 || index >= reference.Count)
        {
            error = "That floor is not there.";
            return false;
        }
        if (reference.Count <= 1)
        {
            error = "A home has to have at least one floor.";
            return false;
        }

        // Read the id BEFORE anything is removed: the reference list is one of the lists being edited.
        string levelId = reference[index].id;

        foreach (var v in doc.variants)
        {
            if (v?.levels == null) continue;
            int at = v.levels.FindIndex(l => l != null && l.id == levelId);
            if (at >= 0) v.levels.RemoveAt(at);
            else if (index < v.levels.Count) v.levels.RemoveAt(index);   // a variant with unstamped ids
        }

        // The sketch goes with the floor it was traced for. Its FILE is deliberately left on disk,
        // the same choice "Remove plan" already makes, so an accidental delete stays recoverable.
        doc.underlays?.RemoveAll(u => u == null || u.levelId == levelId);
        return true;
    }

    /// <summary>
    /// Renames a story in EVERY variant. They are one floor of one building wearing one name; letting
    /// a proposal rename its own copy would make "Floor 2" mean different things in the change list and
    /// in the report depending on which variant was open.
    /// </summary>
    public static void Rename(HomeDoc doc, string levelId, string name)
    {
        if (doc?.variants == null || string.IsNullOrEmpty(levelId)) return;
        foreach (var v in doc.variants)
            foreach (var l in v?.levels ?? new List<LevelDef>())
                if (l != null && l.id == levelId) l.name = name;
    }

    // ---------------------------------------------------------------------------------------
    // Sketches: one per story
    // ---------------------------------------------------------------------------------------

    /// <summary>The sketch traced for one story, or null.</summary>
    public static UnderlayDef UnderlayFor(HomeDoc doc, string levelId)
    {
        if (doc?.underlays == null || string.IsNullOrEmpty(levelId)) return null;
        foreach (var u in doc.underlays)
            if (u != null && u.levelId == levelId) return u;
        return null;
    }

    /// <summary>Replaces (or adds) the sketch for one story. Passing null removes it.</summary>
    public static void SetUnderlay(HomeDoc doc, string levelId, UnderlayDef underlay)
    {
        if (doc == null || string.IsNullOrEmpty(levelId)) return;
        doc.underlays ??= new List<UnderlayDef>();
        doc.underlays.RemoveAll(u => u == null || u.levelId == levelId);
        if (underlay == null) return;
        underlay.levelId = levelId;
        doc.underlays.Add(underlay);
    }

    /// <summary>True when any story of this home has a traced sketch.</summary>
    public static bool HasAnyUnderlay(HomeDoc doc)
    {
        if (doc == null) return false;
        if (doc.underlay != null && !string.IsNullOrEmpty(doc.underlay.imageFileName)) return true;
        foreach (var u in doc.underlays ?? new List<UnderlayDef>())
            if (u != null && !string.IsNullOrEmpty(u.imageFileName)) return true;
        return false;
    }

    /// <summary>
    /// Folds the pre-story single <see cref="HomeDoc.underlay"/> into the list, and stamps every entry
    /// with the story it belongs to.
    ///
    /// Called at the END of HomeStore.Migrate, once every level has an id: that id is the key. The
    /// legacy field is cleared once carried across, so the fold happens exactly once and the next save
    /// writes only the new shape. The field itself stays DECLARED, because removing it would make
    /// Newtonsoft silently drop the traced sketch of every home already on disk, which is the one
    /// thing in a home that cannot be reconstructed.
    ///
    /// Idempotent, because Migrate runs on every load and on every import.
    /// </summary>
    public static void MigrateUnderlays(HomeDoc doc)
    {
        if (doc == null) return;
        doc.underlays ??= new List<UnderlayDef>();

        if (doc.underlay != null && !string.IsNullOrEmpty(doc.underlay.imageFileName))
        {
            doc.underlays.Add(doc.underlay);
            doc.underlay = null;
        }

        // A sketch naming no story belongs to the ground floor: the only story any home that
        // predates this had.
        var reference = Reference(doc);
        string groundId = reference != null && reference.Count > 0 ? reference[0]?.id : null;
        if (string.IsNullOrEmpty(groundId)) return;

        foreach (var u in doc.underlays)
            if (u != null && string.IsNullOrEmpty(u.levelId)) u.levelId = groundId;
    }
}
