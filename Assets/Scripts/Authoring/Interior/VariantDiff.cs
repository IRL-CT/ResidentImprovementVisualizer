using System.Collections.Generic;
using System.Text;
using UnityEngine;

// What changed between two design variants.
//
// The 3D ghost overlay is the eye-catching half of Compare, but this is the half that does the work:
// a plain list of sentences — "Bathroom door: 32\" → 36\"", "Added: grab bar x2", "Removed: threshold"
// — is what gets read aloud when a proposal is discussed with a resident, their family, and the staff
// who will live with the result. So the detail strings here are user-facing prose, not debug output,
// and they are formatted through Units so they read in the same feet-and-inches as everything else.
//
// Matching is by element `id`. VariantPanel's "new proposal from…" deep-copies a variant while
// PRESERVING ids, which is precisely what makes a modification distinguishable from a delete plus an
// add. Break that and every comparison degenerates into "removed everything, added everything".
public static class VariantDiff
{
    public enum ChangeType { Added, Removed, Modified }

    public enum ElementKind { Wall, Opening, Room, Furniture, WallMount, Exterior }

    public struct Change
    {
        public ElementKind kind;
        public ChangeType type;
        public string id;
        public string label;      // what it is, e.g. "Bathroom door"
        public string detail;     // what changed, e.g. "width 32\" → 36\""
        public Vector2 worldPos;  // XZ anchor for a scene marker
        public bool hasPos;

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
            CompareLevel(fl, tl, changes);
        }

        CompareExterior(from, to, changes);
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
    }

    private static void CompareWalls(LevelDef from, LevelDef to, List<Change> changes)
    {
        var a = Index(from?.walls, w => w.id);
        var b = Index(to?.walls, w => w.id);

        foreach (var kv in b)
            if (!a.ContainsKey(kv.Key))
                changes.Add(Make(ElementKind.Wall, ChangeType.Added, kv.Key, WallLabel(kv.Value),
                                 Units.Format(HomeMetrics.WallLength(kv.Value)) + " long",
                                 HomeMetrics.WallMidpoint(kv.Value)));

        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var w2))
            {
                changes.Add(Make(ElementKind.Wall, ChangeType.Removed, kv.Key, WallLabel(kv.Value),
                                 null, HomeMetrics.WallMidpoint(kv.Value)));
                continue;
            }

            var d = new DetailWriter();
            d.Length("length", HomeMetrics.WallLength(kv.Value), HomeMetrics.WallLength(w2));
            d.Length("thickness", kv.Value.thickness, w2.thickness);
            d.Length("height", kv.Value.height, w2.height);
            if (!Same(kv.Value.a, w2.a) || !Same(kv.Value.b, w2.b)) d.Add("moved");
            if (kv.Value.structural != w2.structural)
                d.Add(w2.structural ? "marked structural" : "no longer marked structural");

            if (d.Any)
                changes.Add(Make(ElementKind.Wall, ChangeType.Modified, kv.Key, WallLabel(w2),
                                 d.ToString(), HomeMetrics.WallMidpoint(w2)));
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
            d.Length("clear width", HomeMetrics.ClearWidth(kv.Value), HomeMetrics.ClearWidth(o2));
            d.Length("height", kv.Value.height, o2.height);
            d.Length("sill", kv.Value.sillHeight, o2.sillHeight);

            // Called out explicitly rather than as a number: going to a zero threshold is the whole
            // point of a lot of these proposals, and "threshold removed (step-free)" says that.
            if (!Approximately(kv.Value.thresholdHeight, o2.thresholdHeight))
            {
                if (o2.thresholdHeight <= HomeConventions.EPS) d.Add("threshold removed (step-free)");
                else if (kv.Value.thresholdHeight <= HomeConventions.EPS)
                    d.Add("threshold added (" + Units.Format(o2.thresholdHeight) + ")");
                else d.Length("threshold", kv.Value.thresholdHeight, o2.thresholdHeight);
            }

            if (kv.Value.kind != o2.kind) d.Add($"type {Pretty(kv.Value.kind)} → {Pretty(o2.kind)}");
            if (kv.Value.swing != o2.swing) d.Add($"swing {Pretty(kv.Value.swing)} → {Pretty(o2.swing)}");
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
                                 Units.FormatArea(HomeMetrics.RoomArea(kv.Value)),
                                 HomeMetrics.RoomCentroid(kv.Value)));

        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var r2))
            {
                changes.Add(Make(ElementKind.Room, ChangeType.Removed, kv.Key, RoomLabel(kv.Value),
                                 null, HomeMetrics.RoomCentroid(kv.Value)));
                continue;
            }

            var d = new DetailWriter();
            float aFrom = HomeMetrics.RoomArea(kv.Value), aTo = HomeMetrics.RoomArea(r2);
            if (!Approximately(aFrom, aTo, 0.01f))
                d.Add($"area {Units.FormatArea(aFrom)} → {Units.FormatArea(aTo)}");
            if (kv.Value.name != r2.name) d.Add($"renamed to \"{r2.name}\"");
            if (kv.Value.roomType != r2.roomType) d.Add($"type {Pretty(kv.Value.roomType)} → {Pretty(r2.roomType)}");
            if (kv.Value.floorMaterial != r2.floorMaterial) d.Add("new floor finish");
            d.Length("ceiling", kv.Value.ceilingHeight, r2.ceilingHeight);

            if (d.Any)
                changes.Add(Make(ElementKind.Room, ChangeType.Modified, kv.Key, RoomLabel(r2),
                                 d.ToString(), HomeMetrics.RoomCentroid(r2)));
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

    // Exterior is summarised rather than diffed element-by-element. The outdoor layer is off by
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

    private static string WallLabel(WallDef w) => w.structural ? "Structural wall" : "Wall";

    private static string RoomLabel(RoomDef r)
        => !string.IsNullOrEmpty(r.name) ? r.name : Pretty(r.roomType);

    // A door is named for the room it opens into where possible — "Bathroom door" is what people
    // actually call it, and it is far more useful in a change list than an id.
    private static string OpeningLabel(OpeningDef o, LevelDef level)
    {
        string kind = Pretty(o.kind);
        var room = level != null ? HomeMetrics.RoomAt(OpeningPos(o, level), level) : null;
        return room != null ? $"{RoomLabel(room)} {kind.ToLowerInvariant()}" : kind;
    }

    private static Vector2 OpeningPos(OpeningDef o, LevelDef level)
    {
        if (level?.walls == null) return Vector2.zero;
        foreach (var w in level.walls)
            if (w != null && w.id == o.wallId) return HomeMetrics.PointOnWall(w, o.offset);
        return Vector2.zero;
    }

    private static Vector2 MountPos(WallMountDef m, LevelDef level)
    {
        if (level?.walls == null) return Vector2.zero;
        foreach (var w in level.walls)
            if (w != null && w.id == m.wallId) return HomeMetrics.PointOnWall(w, m.offset);
        return Vector2.zero;
    }

    private static Vector2 Pos(float[] p)
        => p != null && p.Length >= 3 ? new Vector2(p[0], p[2]) : Vector2.zero;

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
