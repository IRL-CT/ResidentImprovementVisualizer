using System.Collections.Generic;
using System.Text;
using UnityEngine;

// What changed between two design variants.
//
// The 3D ghost overlay is the eye-catching half of Compare, but this is the half that does the work:
// a plain list of sentences: "Bathroom door: 32\" → 36\"", "Added: grab bar x2", "Removed: threshold"
//. Is what gets read aloud when a proposal is discussed with a resident, their family, and the staff
// who will live with the result. So the detail strings here are user-facing prose, not debug output,
// and they are formatted through Units so they read in the same feet-and-inches as everything else.
//
// Matching is by element `id`. VariantPanel's "new proposal from…" deep-copies a variant while
// PRESERVING ids, which is precisely what makes a modification distinguishable from a delete plus an
// add. Break that and every comparison degenerates into "removed everything, added everything".
public static class VariantDiff
{
    public enum ChangeType { Added, Removed, Modified }

    public enum ElementKind { Wall, Opening, Room, Furniture, WallMount, Exterior, Occupant, Sensor }

    public struct Change
    {
        public ElementKind kind;
        public ChangeType type;
        public string id;
        public string label;      // what it is, e.g. "Bathroom door"
        public string detail;     // what changed, e.g. "width 32\" → 36\""
        public Vector2 worldPos;  // XZ anchor for a scene marker
        public bool hasPos;

        // WHICH STOREY this change is on. Null and -1 for the changes that belong to no level at all,
        // occupants and the exterior layer.
        //
        // worldPos alone is not an address. Two stories of one dwelling occupy the same XZ by
        // construction, so everything that groups changes by where they are. CompareTool's room
        // grouping, the report's room sections. Files an upstairs change under the ground-floor room
        // directly beneath it unless it can tell the two apart. And VariantRevert needs it to reach
        // the level the change was actually reported from.
        public string levelId;
        public int levelIndex;

        public override string ToString()
            => string.IsNullOrEmpty(detail) ? $"{Prefix(type)} {label}" : $"{Prefix(type)} {label}: {detail}";

        private static string Prefix(ChangeType t) => t switch
        {
            ChangeType.Added => "Added",
            ChangeType.Removed => "Removed",
            _ => "Changed",
        };
    }

    /// <summary>
    /// Compares <paramref name="from"/> (usually the baseline) against <paramref name="to"/> (the
    /// proposal). Levels are matched by id, falling back to position so a variant created before
    /// levels had stable ids still diffs sensibly.
    /// </summary>
    public static List<Change> Compare(VariantDef from, VariantDef to)
    {
        var changes = new List<Change>();
        if (from == null || to == null) return changes;

        var fromLevels = from.levels ?? new List<LevelDef>();
        var toLevels = to.levels ?? new List<LevelDef>();

        int levelCount = Mathf.Max(fromLevels.Count, toLevels.Count);
        for (int i = 0; i < levelCount; i++)
        {
            LevelDef fl = i < fromLevels.Count ? fromLevels[i] : null;
            LevelDef tl = MatchLevel(fl, toLevels, i);

            // Stamped here rather than passed down through the eight CompareX methods, so a method
            // added later cannot forget to carry it: every change a level produces is in this span
            // by construction.
            int first = changes.Count;
            CompareLevel(fl, tl, changes);
            string levelId = tl?.id ?? fl?.id;
            for (int c = first; c < changes.Count; c++)
            {
                var ch = changes[c];
                ch.levelId = levelId;
                ch.levelIndex = i;
                changes[c] = ch;
            }
        }

        // Occupants and the exterior belong to the variant, not to a story, so they keep levelId null
        // and levelIndex -1, which is what puts them under "Elsewhere" in the change list.
        int beforeVariantWide = changes.Count;
        CompareOccupants(from, to, changes);
        CompareExterior(from, to, changes);
        for (int c = beforeVariantWide; c < changes.Count; c++)
        {
            var ch = changes[c];
            ch.levelIndex = -1;
            changes[c] = ch;
        }

        return changes;
    }

    // ---------------------------------------------------------------------------------------

    private static LevelDef MatchLevel(LevelDef fl, List<LevelDef> toLevels, int index)
    {
        if (fl?.id != null)
            foreach (var l in toLevels)
                if (l != null && l.id == fl.id) return l;
        return index < toLevels.Count ? toLevels[index] : null;
    }

    private static void CompareLevel(LevelDef from, LevelDef to, List<Change> changes)
    {
        CompareWalls(from, to, changes);
        CompareOpenings(from, to, changes);
        CompareRooms(from, to, changes);
        CompareFurniture(from, to, changes);
        CompareWallMounts(from, to, changes);
        CompareSensors(from, to, changes);
    }

    private static void CompareWalls(LevelDef from, LevelDef to, List<Change> changes)
    {
        var a = Index(from?.walls, w => w.id);
        var b = Index(to?.walls, w => w.id);

        foreach (var kv in b)
            if (!a.ContainsKey(kv.Key))
                changes.Add(Make(ElementKind.Wall, ChangeType.Added, kv.Key, "Wall",
                                 Units.Format(ResidenceMetrics.WallLength(kv.Value)) + " long",
                                 ResidenceMetrics.WallMidpoint(kv.Value)));

        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var w2))
            {
                changes.Add(Make(ElementKind.Wall, ChangeType.Removed, kv.Key, "Wall",
                                 null, ResidenceMetrics.WallMidpoint(kv.Value)));
                continue;
            }

            var d = new DetailWriter();
            d.Length("length", ResidenceMetrics.WallLength(kv.Value), ResidenceMetrics.WallLength(w2));
            d.Length("thickness", kv.Value.thickness, w2.thickness);
            d.Length("height", kv.Value.height, w2.height);
            if (!Same(kv.Value.a, w2.a) || !Same(kv.Value.b, w2.b)) d.Add("moved");

            if (d.Any)
                changes.Add(Make(ElementKind.Wall, ChangeType.Modified, kv.Key, "Wall",
                                 d.ToString(), ResidenceMetrics.WallMidpoint(w2)));
        }
    }

    private static void CompareOpenings(LevelDef from, LevelDef to, List<Change> changes)
    {
        var a = Index(from?.openings, o => o.id);
        var b = Index(to?.openings, o => o.id);

        foreach (var kv in b)
            if (!a.ContainsKey(kv.Key))
                changes.Add(Make(ElementKind.Opening, ChangeType.Added, kv.Key, OpeningLabel(kv.Value, to),
                                 Units.Format(kv.Value.width) + " wide",
                                 OpeningPos(kv.Value, to)));

        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var o2))
            {
                changes.Add(Make(ElementKind.Opening, ChangeType.Removed, kv.Key, OpeningLabel(kv.Value, from),
                                 null, OpeningPos(kv.Value, from)));
                continue;
            }

            var d = new DetailWriter();
            d.Length("width", kv.Value.width, o2.width);
            d.Length("clear width", ResidenceMetrics.ClearWidth(kv.Value), ResidenceMetrics.ClearWidth(o2));
            d.Length("height", kv.Value.height, o2.height);
            d.Length("sill", kv.Value.sillHeight, o2.sillHeight);

            // Called out explicitly rather than as a number: going to a zero threshold is the whole
            // point of a lot of these proposals, and "threshold removed (step-free)" says that.
            if (!Approximately(kv.Value.thresholdHeight, o2.thresholdHeight))
            {
                if (o2.thresholdHeight <= ResidenceConventions.EPS) d.Add("threshold removed (step-free)");
                else if (kv.Value.thresholdHeight <= ResidenceConventions.EPS)
                    d.Add("threshold added (" + Units.Format(o2.thresholdHeight) + ")");
                else d.Length("threshold", kv.Value.thresholdHeight, o2.thresholdHeight);
            }

            if (kv.Value.kind != o2.kind) d.Add($"type {Pretty(kv.Value.kind)} → {Pretty(o2.kind)}");
            if (!Approximately(kv.Value.offset, o2.offset)) d.Add("moved along wall");
            if (kv.Value.wallId != o2.wallId) d.Add("moved to another wall");

            if (d.Any)
                changes.Add(Make(ElementKind.Opening, ChangeType.Modified, kv.Key, OpeningLabel(o2, to),
                                 d.ToString(), OpeningPos(o2, to)));
        }
    }

    private static void CompareRooms(LevelDef from, LevelDef to, List<Change> changes)
    {
        var a = Index(from?.rooms, r => r.id);
        var b = Index(to?.rooms, r => r.id);

        foreach (var kv in b)
            if (!a.ContainsKey(kv.Key))
                changes.Add(Make(ElementKind.Room, ChangeType.Added, kv.Key, RoomLabel(kv.Value),
                                 Units.FormatArea(ResidenceMetrics.RoomArea(kv.Value)),
                                 ResidenceMetrics.RoomCentroid(kv.Value)));

        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var r2))
            {
                changes.Add(Make(ElementKind.Room, ChangeType.Removed, kv.Key, RoomLabel(kv.Value),
                                 null, ResidenceMetrics.RoomCentroid(kv.Value)));
                continue;
            }

            var d = new DetailWriter();
            float aFrom = ResidenceMetrics.RoomArea(kv.Value), aTo = ResidenceMetrics.RoomArea(r2);
            if (!Approximately(aFrom, aTo, 0.01f))
                d.Add($"area {Units.FormatArea(aFrom)} → {Units.FormatArea(aTo)}");
            if (kv.Value.name != r2.name) d.Add($"renamed to \"{r2.name}\"");
            if (kv.Value.roomType != r2.roomType) d.Add($"type {Pretty(kv.Value.roomType)} → {Pretty(r2.roomType)}");
            d.Length("ceiling", kv.Value.ceilingHeight, r2.ceilingHeight);

            if (d.Any)
                changes.Add(Make(ElementKind.Room, ChangeType.Modified, kv.Key, RoomLabel(r2),
                                 d.ToString(), ResidenceMetrics.RoomCentroid(r2)));
        }
    }

    private static void CompareFurniture(LevelDef from, LevelDef to, List<Change> changes)
    {
        var a = Index(from?.furniture, f => f.instanceId);
        var b = Index(to?.furniture, f => f.instanceId);

        foreach (var kv in b)
            if (!a.ContainsKey(kv.Key))
                changes.Add(Make(ElementKind.Furniture, ChangeType.Added, kv.Key,
                                 Pretty(kv.Value.prefabType), null, Pos(kv.Value.position)));

        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var f2))
            {
                changes.Add(Make(ElementKind.Furniture, ChangeType.Removed, kv.Key,
                                 Pretty(kv.Value.prefabType), null, Pos(kv.Value.position)));
                continue;
            }

            var d = new DetailWriter();
            if (!Same(kv.Value.position, f2.position)) d.Add("moved");
            if (!Approximately(kv.Value.rotationY, f2.rotationY, 0.5f)) d.Add("rotated");
            // Size is the item's REAL size: what the renderer draws, what FurnitureFit tests against
            // a doorway, what the occupancy checks stand people clear of. The transform handles exist
            // to change it, so a change list that omitted it let a proposal widen a bed and say
            // nothing. Reported as the two plan dimensions; height is the third and rarely the point.
            if (!Same(kv.Value.boxSizeMeters, f2.boxSizeMeters))
                d.Add($"resized to {Units.Format(Dim(f2.boxSizeMeters, 0))}"
                      + $" × {Units.Format(Dim(f2.boxSizeMeters, 2))}");
            if (kv.Value.included != f2.included) d.Add(f2.included ? "shown" : "hidden");
            if (kv.Value.prefabType != f2.prefabType)
                d.Add($"replaced with {Pretty(f2.prefabType)}");

            if (d.Any)
                changes.Add(Make(ElementKind.Furniture, ChangeType.Modified, kv.Key,
                                 Pretty(f2.prefabType), d.ToString(), Pos(f2.position)));
        }
    }

    private static void CompareWallMounts(LevelDef from, LevelDef to, List<Change> changes)
    {
        var a = Index(from?.wallMounted, m => m.instanceId);
        var b = Index(to?.wallMounted, m => m.instanceId);

        foreach (var kv in b)
            if (!a.ContainsKey(kv.Key))
                changes.Add(Make(ElementKind.WallMount, ChangeType.Added, kv.Key,
                                 Pretty(kv.Value.prefabType),
                                 "at " + Units.Format(kv.Value.mountHeight) + " AFF",
                                 MountPos(kv.Value, to)));

        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var m2))
            {
                changes.Add(Make(ElementKind.WallMount, ChangeType.Removed, kv.Key,
                                 Pretty(kv.Value.prefabType), null, MountPos(kv.Value, from)));
                continue;
            }

            var d = new DetailWriter();
            d.Length("height", kv.Value.mountHeight, m2.mountHeight);
            if (!Approximately(kv.Value.offset, m2.offset)) d.Add("moved along wall");
            if (kv.Value.wallId != m2.wallId || kv.Value.side != m2.side) d.Add("moved to another wall");
            if (kv.Value.included != m2.included) d.Add(m2.included ? "shown" : "hidden");

            if (d.Any)
                changes.Add(Make(ElementKind.WallMount, ChangeType.Modified, kv.Key,
                                 Pretty(m2.prefabType), d.ToString(), MountPos(m2, to)));
        }
    }

    // Installing sensing IS a proposal, which is why this is here rather than a panel of its own: a
    // smart home package reads as "42 devices added" in the same list as a widened doorway, gets the
    // same markers in the plan, and comes back out one device at a time through VariantRevert.
    //
    // The label is what the device is and where it is ("Door sensor on the entry door") because
    // that list is read aloud, and "sn_17" is not something anyone can agree or disagree with.
    private static void CompareSensors(LevelDef from, LevelDef to, List<Change> changes)
    {
        var a = Index(from?.sensors, s => s.id);
        var b = Index(to?.sensors, s => s.id);

        foreach (var kv in b)
            if (!a.ContainsKey(kv.Key))
                changes.Add(MakeAt(ElementKind.Sensor, ChangeType.Added, kv.Key,
                                   SensorLabel(kv.Value, to), SensorDetail(kv.Value), SensorPos(kv.Value, to)));

        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var s2))
            {
                changes.Add(MakeAt(ElementKind.Sensor, ChangeType.Removed, kv.Key,
                                   SensorLabel(kv.Value, from), null, SensorPos(kv.Value, from)));
                continue;
            }

            var d = new DetailWriter();
            if (kv.Value.hostId != s2.hostId || kv.Value.hostKind != s2.hostKind) d.Add("moved");
            // The point a room- or floor-hosted device sits at. Not comparing it made sliding a motion
            // sensor from one corner of a bedroom to the other, which is the placement decision, and
            // what changes its coverage: a change nothing reported and nothing could revert.
            else if (!Same(kv.Value.position, s2.position)) d.Add("moved");
            if (!Approximately(kv.Value.hostOffset, s2.hostOffset)) d.Add("moved along wall");
            if (!Approximately(kv.Value.coverageRadius, s2.coverageRadius))
                d.Length("range", kv.Value.coverageRadius, s2.coverageRadius);
            if (!Approximately(kv.Value.coverageAngle, s2.coverageAngle, 0.5f)) d.Add("re-aimed");
            if (!Approximately(kv.Value.facingYaw, s2.facingYaw, 0.5f)) d.Add("turned");
            if (kv.Value.hostSide != s2.hostSide) d.Add("moved to the other face");
            d.Length("height", kv.Value.mountHeight, s2.mountHeight);
            if (kv.Value.monitored != s2.monitored)
                d.Add(s2.monitored ? "now reports to staff" : "no longer reports to staff");
            if (kv.Value.included != s2.included) d.Add(s2.included ? "shown" : "hidden");
            if (!SameRules(kv.Value.rules, s2.rules)) d.Add("thresholds changed");

            if (d.Any)
                changes.Add(MakeAt(ElementKind.Sensor, ChangeType.Modified, kv.Key,
                                   SensorLabel(s2, to), d.ToString(), SensorPos(s2, to)));
        }
    }

    private static string SensorLabel(SensorDef s, LevelDef level)
    {
        string device = SensorDevices.LabelOf(s);
        var pose = SensorPose.Resolve(s, level);
        return string.IsNullOrEmpty(pose.hostLabel) ? device : $"{device} on {pose.hostLabel}";
    }

    // What it costs, because that is the question a change list of forty devices raises and the one
    // the rest of the list cannot answer. Money is not a measurement, so it does NOT go through Units.
    private static string SensorDetail(SensorDef s)
    {
        if (!SensorDevices.TryGet(s.deviceType, out var device) || device.purchaseHigh <= 0f) return null;
        return SensorCost.Money(device.purchaseLow) + " - " + SensorCost.Money(device.purchaseHigh);
    }

    // A worn device has no place in the plan at all, so it gets no marker. MakeAt's whole purpose,
    // and the same treatment an occupant change already gets.
    private static Vector2? SensorPos(SensorDef s, LevelDef level)
    {
        var pose = SensorPose.Resolve(s, level);
        return pose.resolved ? pose.xz : (Vector2?)null;
    }

    private static bool SameRules(List<SensorRuleDef> a, List<SensorRuleDef> b)
    {
        int na = a?.Count ?? 0, nb = b?.Count ?? 0;
        if (na != nb) return false;
        for (int i = 0; i < na; i++)
        {
            var x = a[i]; var y = b[i];
            if (x == null || y == null) { if (x != y) return false; continue; }
            if (x.kind != y.kind || x.enabled != y.enabled || x.severity != y.severity) return false;
            if (x.thresholdMinutes != y.thresholdMinutes) return false;
            if (x.windowStart != y.windowStart || x.windowEnd != y.windowEnd) return false;
        }
        return true;
    }

    // Occupants hang off the VARIANT, not the level, so this is called from Compare rather than from
    // CompareLevel. It is also the half of the change list that a resident actually reacts to: "your
    // bedroom moves to the accessible one" lands differently from "opening o_12: width 32\" → 36\"".
    private static void CompareOccupants(VariantDef from, VariantDef to, List<Change> changes)
    {
        var a = Index(from?.occupants, o => o.id);
        var b = Index(to?.occupants, o => o.id);
        if (a.Count == 0 && b.Count == 0) return;

        // The VARIANTS, not their first levels. A person's bedroom is wherever it is in the residence;
        // asking levels[0] for it named "a room that is gone" for everybody upstairs.

        foreach (var kv in b)
            if (!a.ContainsKey(kv.Key))
                changes.Add(MakeAt(ElementKind.Occupant, ChangeType.Added, kv.Key, PersonLabel(kv.Value),
                                   BaseRoom(kv.Value, to), Anchor(kv.Value, to)));

        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var p2))
            {
                changes.Add(MakeAt(ElementKind.Occupant, ChangeType.Removed, kv.Key,
                                   PersonLabel(kv.Value), null, Anchor(kv.Value, from)));
                continue;
            }

            var d = new DetailWriter();
            if (kv.Value.name != p2.name) d.Add($"renamed to \"{p2.name}\"");
            if (kv.Value.usesWheelchair != p2.usesWheelchair)
                d.Add(p2.usesWheelchair ? "now uses a wheelchair" : "no longer uses a wheelchair");
            if (kv.Value.included != p2.included) d.Add(p2.included ? "shown" : "hidden");
            // BY REF, and it has to be. DetailWriter is a struct whose StringBuilder is allocated
            // lazily, so a by-value call let CompareDay write into a copy: when the ONLY thing that
            // changed about a person was their day, `_sb ??= new StringBuilder()` assigned to the
            // copy's field and the caller still saw Any == false. The result was that moving someone
            // to a different bedroom (the example this file's own header leads with) reported
            // nothing at all, unless it happened to be accompanied by a rename.
            CompareDay(kv.Value, p2, from, to, ref d);

            if (d.Any)
                changes.Add(MakeAt(ElementKind.Occupant, ChangeType.Modified, kv.Key,
                                   PersonLabel(p2), d.ToString(), Anchor(p2, to)));
        }
    }

    // Activities are matched by id, same as everything else. A moved room is reported in full because
    // it is the point; retimed and added/removed blocks are summarized, because a day has a dozen of
    // them and a change list is meant to be read out, not audited.
    private static void CompareDay(OccupantDef from, OccupantDef to, VariantDef fv, VariantDef tv,
                                   ref DetailWriter d)
    {
        var a = Index(from?.schedule, x => x.id);
        var b = Index(to?.schedule, x => x.id);

        int added = 0, removed = 0, retimed = 0;

        foreach (var kv in b) if (!a.ContainsKey(kv.Key)) added++;

        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var t)) { removed++; continue; }

            if (kv.Value.roomId != t.roomId)
            {
                string now = RoomName(t.roomId, tv), was = RoomName(kv.Value.roomId, fv);
                d.Add($"{Verb(t.kind)} {now} (was {was})");
            }

            if (kv.Value.startMinutes != t.startMinutes || kv.Value.endMinutes != t.endMinutes) retimed++;
        }

        if (retimed > 0) d.Add(retimed == 1 ? "one activity retimed" : $"{retimed} activities retimed");
        if (added > 0) d.Add(added == 1 ? "one activity added" : $"{added} activities added");
        if (removed > 0) d.Add(removed == 1 ? "one activity dropped" : $"{removed} activities dropped");
    }

    private static string PersonLabel(OccupantDef p)
        => !string.IsNullOrEmpty(p?.name) ? p.name : "Occupant";

    // A one-line summary for an added person, so the row says something more than their name.
    private static string BaseRoom(OccupantDef p, VariantDef variant)
    {
        if (p?.schedule == null) return null;
        foreach (var act in p.schedule)
            if (act != null && act.kind == ActivityKind.Sleep && !string.IsNullOrEmpty(act.roomId))
                return "sleeps in " + RoomName(act.roomId, variant);
        return null;
    }

    // Anchored at the room they sleep in, falling back to the first room they are ever in. Somewhere
    // meaningful matters here: a person has no fixed position to point a scene marker at.
    private static Vector2? Anchor(OccupantDef p, VariantDef variant)
    {
        if (p?.schedule == null || variant == null) return null;

        RoomDef best = null;
        foreach (var act in p.schedule)
        {
            if (act == null || string.IsNullOrEmpty(act.roomId)) continue;
            var room = OccupancyModel.FindRoomAnyLevel(variant, act.roomId);
            if (room == null) continue;
            if (act.kind == ActivityKind.Sleep) return ResidenceMetrics.RoomCentroid(room);
            best ??= room;
        }
        return best != null ? ResidenceMetrics.RoomCentroid(best) : (Vector2?)null;
    }

    private static string RoomName(string roomId, VariantDef variant)
    {
        if (string.IsNullOrEmpty(roomId)) return "out of the house";
        var room = OccupancyModel.FindRoomAnyLevel(variant, roomId);
        return room != null ? OccupancyModel.RoomLabel(room) : "a room that is gone";
    }

    // Reads as a sentence about a person: "Alice: sleeps in Bedroom 1 (was Bedroom 3)".
    private static string Verb(string kind) => kind switch
    {
        ActivityKind.Sleep => "sleeps in",
        ActivityKind.Hygiene => "gets ready in",
        ActivityKind.Cook => "cooks in",
        ActivityKind.Eat => "eats in",
        ActivityKind.Relax => "relaxes in",
        ActivityKind.Work => "works in",
        ActivityKind.Care => "receives care in",
        ActivityKind.Out => "goes to",
        _ => "moves to",
    };

    // Exterior is summarized rather than diffed element-by-element. The outdoor layer is off by
    // default and edited elsewhere, so "added an entry ramp" is the useful granularity here; a
    // per-path diff would be noise in a list meant to be read out loud.
    private static void CompareExterior(VariantDef from, VariantDef to, List<Change> changes)
    {
        bool hadAny = HasExterior(from);
        bool hasAny = HasExterior(to);
        if (!hadAny && !hasAny) return;

        string fromSummary = ExteriorSummary(from);
        string toSummary = ExteriorSummary(to);
        if (fromSummary == toSummary) return;

        ChangeType type = !hadAny ? ChangeType.Added : !hasAny ? ChangeType.Removed : ChangeType.Modified;
        changes.Add(new Change
        {
            kind = ElementKind.Exterior,
            type = type,
            id = "exterior",
            label = "Exterior",
            detail = type == ChangeType.Removed ? null : toSummary,
            hasPos = false,
        });
    }

    private static bool HasExterior(VariantDef v)
    {
        if (v == null) return false;
        var s = v.exterior;
        int objs = v.exteriorObjects?.Count ?? 0;
        if (s == null) return objs > 0;
        return objs > 0
            || (s.paths?.Count ?? 0) > 0
            || (s.fences?.Count ?? 0) > 0
            || (s.surfaceStrokes?.Count ?? 0) > 0
            || (s.terrainZones?.Count ?? 0) > 0;
    }

    private static string ExteriorSummary(VariantDef v)
    {
        if (v == null) return "";
        var parts = new List<string>();
        var s = v.exterior;
        if (s != null)
        {
            Count(parts, s.paths?.Count ?? 0, "walkway", "walkways");
            Count(parts, s.fences?.Count ?? 0, "railing", "railings");
            Count(parts, s.surfaceStrokes?.Count ?? 0, "paved area", "paved areas");
            Count(parts, s.terrainZones?.Count ?? 0, "ground zone", "ground zones");
        }
        Count(parts, v.exteriorObjects?.Count ?? 0, "outdoor item", "outdoor items");
        return string.Join(", ", parts);
    }

    private static void Count(List<string> into, int n, string one, string many)
    {
        if (n > 0) into.Add($"{n} {(n == 1 ? one : many)}");
    }

    // ---------------------------------------------------------------------------------------

    private static Dictionary<string, T> Index<T>(List<T> list, System.Func<T, string> keyOf) where T : class
    {
        var map = new Dictionary<string, T>();
        if (list == null) return map;
        foreach (var item in list)
        {
            if (item == null) continue;
            string k = keyOf(item);
            if (string.IsNullOrEmpty(k)) continue;
            map[k] = item;
        }
        return map;
    }

    private static Change Make(ElementKind kind, ChangeType type, string id, string label,
                               string detail, Vector2 pos)
        => new Change { kind = kind, type = type, id = id, label = label, detail = detail,
                        worldPos = pos, hasPos = true };

    // As Make, but for elements that may have nowhere sensible to point a scene marker.
    private static Change MakeAt(ElementKind kind, ChangeType type, string id, string label,
                                 string detail, Vector2? pos)
        => new Change { kind = kind, type = type, id = id, label = label, detail = detail,
                        worldPos = pos ?? Vector2.zero, hasPos = pos.HasValue };

    private static string RoomLabel(RoomDef r)
        => !string.IsNullOrEmpty(r.name) ? r.name : Pretty(r.roomType);

    // A door is named for the room it opens into where possible: "Bathroom door" is what people
    // actually call it, and it is far more useful in a change list than an id.
    private static string OpeningLabel(OpeningDef o, LevelDef level)
    {
        string kind = Pretty(o.kind);
        var room = level != null ? ResidenceMetrics.RoomAt(OpeningPos(o, level), level) : null;
        return room != null ? $"{RoomLabel(room)} {kind.ToLowerInvariant()}" : kind;
    }

    private static Vector2 OpeningPos(OpeningDef o, LevelDef level)
    {
        if (level?.walls == null) return Vector2.zero;
        foreach (var w in level.walls)
            if (w != null && w.id == o.wallId) return ResidenceMetrics.PointOnWall(w, o.offset);
        return Vector2.zero;
    }

    private static Vector2 MountPos(WallMountDef m, LevelDef level)
    {
        if (level?.walls == null) return Vector2.zero;
        foreach (var w in level.walls)
            if (w != null && w.id == m.wallId) return ResidenceMetrics.PointOnWall(w, m.offset);
        return Vector2.zero;
    }

    private static Vector2 Pos(float[] p)
        => p != null && p.Length >= 3 ? new Vector2(p[0], p[2]) : Vector2.zero;

    private static float Dim(float[] size, int i)
        => size != null && size.Length > i ? size[i] : 0f;

    private static bool Same(float[] a, float[] b)
    {
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!Approximately(a[i], b[i])) return false;
        return true;
    }

    private static bool Approximately(float a, float b, float eps = 0.002f) => Mathf.Abs(a - b) <= eps;

    // "pass_through" -> "Pass through"; also tidies catalog keys like "grab_bar_36".
    private static string Pretty(string token)
    {
        if (string.IsNullOrEmpty(token)) return "item";
        string s = token.Replace('_', ' ').Trim();
        return s.Length == 0 ? "item" : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    // Accumulates "a, b, c" detail fragments, formatting length changes through Units.
    private struct DetailWriter
    {
        private StringBuilder _sb;

        public bool Any => _sb != null && _sb.Length > 0;

        public void Add(string fragment)
        {
            if (string.IsNullOrEmpty(fragment)) return;
            _sb ??= new StringBuilder();
            if (_sb.Length > 0) _sb.Append(", ");
            _sb.Append(fragment);
        }

        public void Length(string name, float from, float to)
        {
            if (Approximately(from, to)) return;
            Add($"{name} {Units.Format(from)} → {Units.Format(to)}");
        }

        public override string ToString() => _sb?.ToString() ?? "";
    }
}
