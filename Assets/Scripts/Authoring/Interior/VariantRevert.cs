using System.Collections.Generic;

// Taking one proposed change back out, without undoing everything after it.
//
// VariantDiff answers "what is different". This answers "make that one difference go away": the
// operation the change list implied and never offered. Before it, dropping a single proposed change
// meant finding the element by hand and reversing the edit yourself, or undoing back past every good
// change you had made since.
//
// It is the exact inverse of VariantDiff and it MUST stay that way, which is what the tests assert:
// revert every change in a diff and the diff must come back empty. That property is the whole
// specification, and it is why the two files are read together.
//
// The OpeningFit convention throughout: do the reasonable thing where one exists, refuse only where
// nothing is legal, and hand back a `reason` written to be shown to a user verbatim rather than
// logged. There is exactly one refusal here and it is real. See Removed, below.
//
// IDS ARE PRESERVED ON EVERY PATH. Matching is by element id, so a revert that minted a fresh one
// would turn "put that door back the way it was" into "delete a door, add a different door" on the
// very next comparison. That is also why the copies below are written out by hand rather than routed
// through a serializer: "keep the id, copy everything else" is the invariant this whole feature
// rests on, and it should be readable rather than implied.
//
// This lives in CXRAuthoring so it can be tested at all: the same reason SampleRefresh does, and
// the same reason it cannot reach ResidenceStore.Clone (Assembly-CSharp, which asmdefs cannot reference).
public static class VariantRevert
{
    /// <summary>
    /// Undoes one change, so that <paramref name="proposal"/> matches <paramref name="baseline"/> for
    /// that element and nothing else moves.
    /// </summary>
    /// <returns>
    /// False when the change cannot be taken back, with <paramref name="reason"/> set to a sentence
    /// for the user. Callers show it; they do not log it.
    /// </returns>
    public static bool Revert(VariantDef baseline, VariantDef proposal, VariantDiff.Change change,
                              out string reason)
    {
        reason = null;
        if (baseline == null || proposal == null)
        {
            reason = "There is nothing to compare this against.";
            return false;
        }

        if (change.kind == VariantDiff.ElementKind.Occupant) return RevertOccupant(baseline, proposal, change);
        if (change.kind == VariantDiff.ElementKind.Exterior) return RevertExterior(baseline, proposal);

        // Levels are matched the way VariantDiff matches them, by id then by position, so a revert
        // addresses the same level the change was reported from.
        //
        // This used to say exactly that and then call FirstLevel on both sides, which was the same
        // thing only while every residence had one story. On two, reverting an upstairs change reached
        // into the ground floor, found no element with that id, and silently did nothing. Breaking
        // the property this file is specified by: revert every change in a diff and the diff comes
        // back empty. VariantDiff.Change now carries the level it was reported from.
        var to = LevelFor(proposal, change);
        var from = MatchingLevel(baseline, to, change);
        if (to == null)
        {
            reason = "This design option has no plan to change.";
            return false;
        }

        switch (change.kind)
        {
            case VariantDiff.ElementKind.Wall:      return RevertWall(from, to, change, out reason);
            case VariantDiff.ElementKind.Opening:   return RevertOpening(from, to, change, out reason);
            case VariantDiff.ElementKind.Room:      return RevertRoom(from, to, change);
            case VariantDiff.ElementKind.Furniture: return RevertFurniture(from, to, change);
            case VariantDiff.ElementKind.WallMount: return RevertMount(from, to, change, out reason);
            case VariantDiff.ElementKind.Sensor:    return RevertSensor(proposal, from, to, change, out reason);
        }

        reason = "That change cannot be taken back.";
        return false;
    }

    /// <summary>
    /// Puts the whole plan back to the baseline, keeping the proposal's own identity: its id, name,
    /// description, provenance and lock. What it is stays; what it says goes.
    /// </summary>
    public static void RevertAll(VariantDef baseline, VariantDef proposal)
    {
        if (baseline == null || proposal == null) return;

        proposal.levels = new List<LevelDef>();
        foreach (var l in baseline.levels ?? new List<LevelDef>())
            if (l != null) proposal.levels.Add(Copy(l));

        proposal.occupants = CopyList(baseline.occupants, Copy);
        proposal.exterior = Copy(baseline.exterior);
        proposal.exteriorObjects = CopyList(baseline.exteriorObjects, Copy);
    }

    // ---------------------------------------------------------------------------------------
    // Per-kind reverts
    // ---------------------------------------------------------------------------------------

    private static bool RevertWall(LevelDef from, LevelDef to, VariantDiff.Change change,
                                   out string reason)
    {
        reason = null;
        var original = Find(from?.walls, w => w.id, change.id);

        if (original == null)
        {
            // The proposal added this wall: take it away, and with it everything it hosts. The cascade
            // mirrors SelectTool.DeleteSelected exactly, so the two cannot disagree about what
            // removing a wall means: an opening whose wallId no longer resolves is silently skipped
            // by ResidenceRenderer, with no warning anywhere.
            //
            // Sensors ride two levels down: a door sensor hosts on an OPENING, and that opening is
            // about to go with the wall. Collecting the doomed openings before removing them is what
            // stops a device being left pointing at an id nothing resolves.
            var orphaned = IdsOf(to.openings, o => o.wallId == change.id, o => o.id);

            Remove(to.walls, w => w.id, change.id);
            RemoveAll(to.openings, o => o.wallId == change.id);
            RemoveAll(to.wallMounted, m => m.wallId == change.id);
            RemoveAll(to.sensors, s => (s.hostKind == SensorHost.Wall && s.hostId == change.id)
                                    || (s.hostKind == SensorHost.Opening && orphaned.Contains(s.hostId)));
            return true;
        }

        Replace(ref to.walls, w => w.id, change.id, Copy(original));
        return true;
    }

    private static bool RevertOpening(LevelDef from, LevelDef to, VariantDiff.Change change,
                                      out string reason)
    {
        reason = null;
        var original = Find(from?.openings, o => o.id, change.id);

        if (original == null)
        {
            Remove(to.openings, o => o.id, change.id);
            RemoveAll(to.sensors, s => s.hostKind == SensorHost.Opening && s.hostId == change.id);
            return true;
        }

        // The one genuine refusal in this file. Restoring an opening onto a wall the proposal has
        // since removed would write an OpeningDef whose wallId resolves to nothing, which
        // WallLayout clamps and ResidenceRenderer skips, silently, producing a door that exists in the
        // data and nowhere on screen. Refusing and saying why is the only honest answer, and the
        // user's route out is the obvious one.
        if (Find(to.walls, w => w.id, original.wallId) == null)
        {
            reason = "The wall this " + Noun(original.kind) + " was in has been removed. "
                     + "Put that wall back first.";
            return false;
        }

        Replace(ref to.openings, o => o.id, change.id, Copy(original));
        return true;
    }

    private static bool RevertRoom(LevelDef from, LevelDef to, VariantDiff.Change change)
    {
        var original = Find(from?.rooms, r => r.id, change.id);
        if (original == null)
        {
            // Room-hosted devices only. A water sensor names its room but lives at a coordinate, so
            // it survives its room being removed. One cascade, shared with SelectTool and Sync.
            RoomRegions.RemoveRoom(to, change.id);
            return true;
        }
        Replace(ref to.rooms, r => r.id, change.id, Copy(original));
        return true;
    }

    private static bool RevertFurniture(LevelDef from, LevelDef to, VariantDiff.Change change)
    {
        var original = Find(from?.furniture, f => f.instanceId, change.id);
        if (original == null)
        {
            // A pad hosts on a bed and a stove sensor on a range, so taking the item back out has to
            // take its device with it: the same cascade a wall runs, one host down.
            Remove(to.furniture, f => f.instanceId, change.id);
            RemoveAll(to.sensors, s => s.hostKind == SensorHost.Furniture && s.hostId == change.id);
            return true;
        }
        Replace(ref to.furniture, f => f.instanceId, change.id, Copy(original));
        return true;
    }

    private static bool RevertMount(LevelDef from, LevelDef to, VariantDiff.Change change,
                                    out string reason)
    {
        reason = null;
        var original = Find(from?.wallMounted, m => m.instanceId, change.id);

        if (original == null)
        {
            Remove(to.wallMounted, m => m.instanceId, change.id);
            return true;
        }

        // Same hazard as an opening, same answer: a mount is parameterised by (wallId, offset, side,
        // mountHeight) and has no meaning without its wall.
        if (Find(to.walls, w => w.id, original.wallId) == null)
        {
            reason = "The wall this was mounted on has been removed. Put that wall back first.";
            return false;
        }

        Replace(ref to.wallMounted, m => m.instanceId, change.id, Copy(original));
        return true;
    }

    private static bool RevertSensor(VariantDef proposal, LevelDef from, LevelDef to,
                                     VariantDiff.Change change, out string reason)
    {
        reason = null;
        var original = Find(from?.sensors, s => s.id, change.id);

        if (original == null)
        {
            Remove(to.sensors, s => s.id, change.id);
            return true;
        }

        // The same hazard as an opening and a mount, one host wider. A sensor is parameterised by the
        // element it watches, and restoring one whose host the proposal has removed writes a hostId
        // that resolves to nothing: SensorPose returns an unresolved pose, so the device renders
        // nowhere, covers nothing and reports nothing. Present in the data and absent everywhere
        // else. Refusing and naming the host is the only honest answer, and it is a detour rather
        // than a dead end: put the host back, then put the device back.
        if (!HostExists(original, proposal, to))
        {
            reason = "The " + SensorHost.Label(original.hostKind) + " this "
                   + SensorDevices.LabelOf(original).ToLowerInvariant()
                   + " was installed on has been removed. Put that back first.";
            return false;
        }

        var list = to.sensors ??= new List<SensorDef>();
        Replace(ref list, s => s.id, change.id, Copy(original));
        to.sensors = list;
        return true;
    }

    /// <summary>Whether the element a sensor hosts on still exists in the variant being restored into.</summary>
    private static bool HostExists(SensorDef sensor, VariantDef proposal, LevelDef to)
        => sensor.hostKind switch
        {
            SensorHost.Opening => Find(to.openings, o => o.id, sensor.hostId) != null,
            SensorHost.Furniture => Find(to.furniture, f => f.instanceId, sensor.hostId) != null,
            SensorHost.Wall => Find(to.walls, w => w.id, sensor.hostId) != null,
            SensorHost.Room => Find(to.rooms, r => r.id, sensor.hostId) != null,
            // A worn device hangs off the roster on the VARIANT, not the level.
            SensorHost.Occupant => Find(proposal?.occupants, p => p.id, sensor.hostId) != null,
            // A point sensor names the room it sits in, but its position is its own: a room removed
            // under it leaves it on a patch of floor, which is exactly where it was.
            _ => true,
        };

    private static bool RevertOccupant(VariantDef baseline, VariantDef proposal,
                                       VariantDiff.Change change)
    {
        var original = Find(baseline.occupants, p => p.id, change.id);
        if (original == null)
        {
            Remove(proposal.occupants, p => p.id, change.id);
            // A pendant is worn, so removing the person removes the device. There is nobody left to
            // wear it, and it would report against an id no roster resolves.
            foreach (var level in proposal.levels ?? new List<LevelDef>())
                RemoveAll(level?.sensors, s => s.hostKind == SensorHost.Occupant && s.hostId == change.id);
            return true;
        }

        // No wall check here, and none is wanted: an activity naming a room the proposal removed
        // reads as "away from residence" rather than as broken geometry (OccupancyModel treats an
        // unresolvable roomId exactly as it treats a null one), so restoring the person is always
        // better than refusing to.
        var list = proposal.occupants ??= new List<OccupantDef>();
        Replace(ref list, p => p.id, change.id, Copy(original));
        proposal.occupants = list;
        return true;
    }

    private static bool RevertExterior(VariantDef baseline, VariantDef proposal)
    {
        // VariantDiff reports the whole outdoor layer as one change under a synthetic id, so this
        // reverts it whole. Copied rather than shared: handing the proposal the baseline's own
        // SiteDef would make a later edit to one silently edit the other.
        proposal.exterior = Copy(baseline.exterior);
        proposal.exteriorObjects = CopyList(baseline.exteriorObjects, Copy);
        return true;
    }

    private static string Noun(string openingKind) => openingKind switch
    {
        OpeningKind.Window => "window",
        OpeningKind.PassThrough => "opening",
        OpeningKind.CasedOpening => "opening",
        _ => "door",
    };

    // ---------------------------------------------------------------------------------------
    // List helpers. Kept tiny and explicit: every one of them has to preserve position as well as
    // content, because Replace writes back in place and a revert that reordered a list would show up
    // as churn in the JSON on every save.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The story a change belongs to: by id, then by the index the diff reported, then the first one.
    ///
    /// The last fallback is what keeps every change recorded before Change carried a level, and every
    /// hand-built Change in a test fixture. Reverting exactly as it used to.
    /// </summary>
    private static LevelDef LevelFor(VariantDef v, VariantDiff.Change change)
    {
        var levels = v?.levels;
        if (levels == null || levels.Count == 0) return null;

        if (!string.IsNullOrEmpty(change.levelId))
            foreach (var l in levels)
                if (l != null && l.id == change.levelId) return l;

        if (change.levelIndex >= 0 && change.levelIndex < levels.Count) return levels[change.levelIndex];
        return levels[0];
    }

    /// <summary>
    /// The baseline story that corresponds to a proposal story: by id, then by position, which is
    /// exactly how VariantDiff.MatchLevel pairs them up. Anything else would revert an element onto
    /// the wrong floor's copy of itself.
    /// </summary>
    private static LevelDef MatchingLevel(VariantDef baseline, LevelDef to, VariantDiff.Change change)
    {
        var levels = baseline?.levels;
        if (levels == null || levels.Count == 0) return null;

        if (!string.IsNullOrEmpty(to?.id))
            foreach (var l in levels)
                if (l != null && l.id == to.id) return l;

        return LevelFor(baseline, change);
    }

    private static T Find<T>(List<T> list, System.Func<T, string> key, string id) where T : class
    {
        if (list == null || string.IsNullOrEmpty(id)) return null;
        foreach (var item in list)
            if (item != null && key(item) == id) return item;
        return null;
    }

    private static void Remove<T>(List<T> list, System.Func<T, string> key, string id) where T : class
    {
        if (list == null) return;
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i] != null && key(list[i]) == id) list.RemoveAt(i);
    }

    /// <summary>The ids of the items about to be removed, gathered BEFORE they are, so a cascade can
    /// follow them one host further down (a wall's openings, and their sensors).</summary>
    private static HashSet<string> IdsOf<T>(List<T> list, System.Func<T, bool> match,
                                            System.Func<T, string> key) where T : class
    {
        var ids = new HashSet<string>();
        if (list == null) return ids;
        foreach (var item in list) if (item != null && match(item)) ids.Add(key(item));
        return ids;
    }

    private static void RemoveAll<T>(List<T> list, System.Func<T, bool> match) where T : class
    {
        if (list == null) return;
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i] != null && match(list[i])) list.RemoveAt(i);
    }

    // Overwrites in place where the element is already present, appends where it is not, which is
    // the Removed case, where the proposal deleted it and it has to come back.
    private static void Replace<T>(ref List<T> list, System.Func<T, string> key, string id, T value)
        where T : class
    {
        list ??= new List<T>();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == null || key(list[i]) != id) continue;
            list[i] = value;
            return;
        }
        list.Add(value);
    }

    private static List<T> CopyList<T>(List<T> src, System.Func<T, T> copy) where T : class
    {
        if (src == null) return null;
        var outList = new List<T>(src.Count);
        foreach (var item in src) outList.Add(item == null ? null : copy(item));
        return outList;
    }

    private static float[] Copy(float[] src) => src == null ? null : (float[])src.Clone();

    private static float[][] Copy(float[][] src)
    {
        if (src == null) return null;
        var outArr = new float[src.Length][];
        for (int i = 0; i < src.Length; i++) outArr[i] = Copy(src[i]);
        return outArr;
    }

    private static List<string> Copy(List<string> src) => src == null ? null : new List<string>(src);

    // ---------------------------------------------------------------------------------------
    // Deep copies. Every field, spelled out. Including the id, which is the point.
    //
    // These are hand-written rather than serializer round-trips for two reasons. CXRAuthoring is
    // dependency-free and has no serializer to reach for. And a new field added to one of these defs
    // should be a visible omission here rather than something a reflection-based copy picks up
    // silently and gets subtly wrong (a shared float[] reference is the failure mode: two variants
    // pointing at one array, so moving a wall in the proposal moves it in the baseline too).
    // ---------------------------------------------------------------------------------------

    public static WallDef Copy(WallDef s) => s == null ? null : new WallDef
    {
        id = s.id,
        a = Copy(s.a),
        b = Copy(s.b),
        thickness = s.thickness,
        height = s.height,
        materialLeft = s.materialLeft,
        materialRight = s.materialRight,
    };

    public static OpeningDef Copy(OpeningDef s) => s == null ? null : new OpeningDef
    {
        id = s.id,
        wallId = s.wallId,
        offset = s.offset,
        width = s.width,
        height = s.height,
        clearWidth = s.clearWidth,
        sillHeight = s.sillHeight,
        kind = s.kind,
        thresholdHeight = s.thresholdHeight,
    };

    public static RoomDef Copy(RoomDef s) => s == null ? null : new RoomDef
    {
        id = s.id,
        name = s.name,
        roomType = s.roomType,
        polygon = Copy(s.polygon),
        ceilingHeight = s.ceilingHeight,
    };

    public static ObjectInstance Copy(ObjectInstance s) => s == null ? null : new ObjectInstance
    {
        instanceId = s.instanceId,
        prefabType = s.prefabType,
        position = Copy(s.position),
        rotationX = s.rotationX,
        rotationY = s.rotationY,
        rotationZ = s.rotationZ,
        scale = s.scale,
        boxSizeMeters = Copy(s.boxSizeMeters),
        included = s.included,
        brushPainted = s.brushPainted,
    };

    public static WallMountDef Copy(WallMountDef s) => s == null ? null : new WallMountDef
    {
        instanceId = s.instanceId,
        prefabType = s.prefabType,
        wallId = s.wallId,
        offset = s.offset,
        side = s.side,
        mountHeight = s.mountHeight,
        decorWidthFrac = s.decorWidthFrac,
        decorHeightFrac = s.decorHeightFrac,
        decorAnchor = s.decorAnchor,
        decorSurfaceOffset = s.decorSurfaceOffset,
        decorMountAxis = s.decorMountAxis,
        decorFlipMount = s.decorFlipMount,
        included = s.included,
        note = s.note,
    };

    public static SensorRuleDef Copy(SensorRuleDef s) => s?.Copy();

    public static SensorDef Copy(SensorDef s) => s == null ? null : new SensorDef
    {
        id = s.id,
        deviceType = s.deviceType,
        hostKind = s.hostKind,
        hostId = s.hostId,
        position = Copy(s.position),
        hostOffset = s.hostOffset,
        hostSide = s.hostSide,
        mountHeight = s.mountHeight,
        coverageRadius = s.coverageRadius,
        coverageAngle = s.coverageAngle,
        facingYaw = s.facingYaw,
        privacy = s.privacy,
        monitored = s.monitored,
        included = s.included,
        note = s.note,
        rules = CopyList(s.rules, Copy),
    };

    public static ActivityDef Copy(ActivityDef s) => s == null ? null : new ActivityDef
    {
        id = s.id,
        kind = s.kind,
        label = s.label,
        startMinutes = s.startMinutes,
        endMinutes = s.endMinutes,
        roomId = s.roomId,
        anchorId = s.anchorId,
    };

    public static OccupantDef Copy(OccupantDef s) => s == null ? null : new OccupantDef
    {
        id = s.id,
        name = s.name,
        note = s.note,
        usesWheelchair = s.usesWheelchair,
        color = Copy(s.color),
        included = s.included,
        schedule = CopyList(s.schedule, Copy),
    };

    public static LevelDef Copy(LevelDef s) => s == null ? null : new LevelDef
    {
        id = s.id,
        name = s.name,
        elevation = s.elevation,
        ceilingHeight = s.ceilingHeight,
        wallThickness = s.wallThickness,
        walls = CopyList(s.walls, Copy),
        openings = CopyList(s.openings, Copy),
        rooms = CopyList(s.rooms, Copy),
        furniture = CopyList(s.furniture, Copy),
        wallMounted = CopyList(s.wallMounted, Copy),
        sensors = CopyList(s.sensors, Copy),
    };

    // ---- the exterior layer, reused verbatim from the Site schema ----

    public static PathDef Copy(PathDef s) => s == null ? null : new PathDef
    {
        id = s.id, material = s.material, width = s.width,
        points = Copy(s.points), smoothing = s.smoothing,
    };

    public static FenceDef Copy(FenceDef s) => s == null ? null : new FenceDef
    {
        id = s.id, fenceType = s.fenceType, points = Copy(s.points),
        smoothing = s.smoothing, height = s.height,
    };

    public static SurfaceStrokeDef Copy(SurfaceStrokeDef s) => s == null ? null : new SurfaceStrokeDef
    {
        id = s.id, terrainType = s.terrainType, radius = s.radius,
        points = Copy(s.points), shape = s.shape, angleDeg = s.angleDeg,
    };

    public static TerrainZoneDef Copy(TerrainZoneDef s) => s == null ? null : new TerrainZoneDef
    {
        terrainType = s.terrainType, rectMeters = Copy(s.rectMeters),
    };

    public static GradePointDef Copy(GradePointDef s) => s == null ? null : new GradePointDef
    {
        x = s.x, z = s.z, height = s.height,
    };

    public static SiteDef Copy(SiteDef s) => s == null ? null : new SiteDef
    {
        terrainSize = Copy(s.terrainSize),
        terrainZones = CopyList(s.terrainZones, Copy),
        paths = CopyList(s.paths, Copy),
        fences = CopyList(s.fences, Copy),
        surfaceStrokes = CopyList(s.surfaceStrokes, Copy),
        scaleNote = s.scaleNote,
        gradePoints = CopyList(s.gradePoints, Copy),
        maxGradeHeight = s.maxGradeHeight,
        lotBoundary = Copy(s.lotBoundary),
        outsideTerrainType = s.outsideTerrainType,
    };
}
