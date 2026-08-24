using System;
using System.Collections.Generic;
using UnityEngine;

// materialId -> Material for interior surfaces. Same shape and role as the existing MaterialPalette
// (used by the tile building editor) and PathMaterialPalette. Deliberately, so the wiring pattern in
// the project stays uniform and an unknown id degrades the same way everywhere: a warning and a
// default material, never a pink error surface or a missing wall.
[CreateAssetMenu(fileName = "InteriorMaterialPalette", menuName = "CXR/Interior Material Palette")]
public class InteriorMaterialPalette : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        public string materialId;        // "paint_white", "tile_bath", "oak_floor", ...
        public Material material;
        public Surface surface = Surface.Any;   // filters the swatch list per tool
        public Color swatch = Color.white;      // shown when the material has no preview texture
    }

    public enum Surface { Any, Wall, Floor, Ceiling }

    public List<Entry> entries = new List<Entry>();

    [Header("Fallbacks")]
    [Tooltip("Used when a wall/floor/ceiling names a material id this palette does not contain.")]
    public Material defaultWall;
    public Material defaultFloor;
    public Material defaultCeiling;
    [Tooltip("Wall tops, end caps, and the reveals inside door and window openings.")]
    public Material defaultEdge;

    private Dictionary<string, Entry> _lookup;

    public Material Get(string materialId, Surface surface = Surface.Any)
    {
        if (!string.IsNullOrEmpty(materialId))
        {
            BuildLookup();
            if (_lookup.TryGetValue(materialId, out var e) && e.material != null) return e.material;
        }
        return Fallback(surface);
    }

    public Material Fallback(Surface surface) => surface switch
    {
        Surface.Floor => defaultFloor,
        Surface.Ceiling => defaultCeiling,
        Surface.Wall => defaultWall,
        _ => defaultEdge != null ? defaultEdge : defaultWall,
    };

    public bool Has(string materialId)
    {
        if (string.IsNullOrEmpty(materialId)) return false;
        BuildLookup();
        return _lookup.ContainsKey(materialId);
    }

    /// <summary>Entries usable on a given surface, for the material picker in the rail.</summary>
    public List<Entry> For(Surface surface)
    {
        var list = new List<Entry>();
        foreach (var e in entries)
            if (e != null && !string.IsNullOrEmpty(e.materialId) &&
                (e.surface == Surface.Any || surface == Surface.Any || e.surface == surface))
                list.Add(e);
        return list;
    }

    private void BuildLookup()
    {
        if (_lookup != null) return;
        _lookup = new Dictionary<string, Entry>();
        foreach (var e in entries)
            if (e != null && !string.IsNullOrEmpty(e.materialId)) _lookup[e.materialId] = e;
    }

    private void OnValidate() => _lookup = null;   // re-read after an inspector edit
}
