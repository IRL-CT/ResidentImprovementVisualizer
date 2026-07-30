using System;
using System.Collections.Generic;
using UnityEngine;

// The furniture and fixture catalog.
//
// This is a METADATA SIDECAR, not a replacement for PrefabRegistry. Entry ids live in the same key
// space as PrefabRegistry keys, and the renderer resolves art by asking PrefabRegistry for `id`:
//
//     PrefabRegistry has the key  ->  spawn the real prefab
//     it does not                ->  spawn a labeled box at the entry's true dimensions
//
// So the project ships useful today with zero interior art (there is none — every existing asset pack
// is exterior), and adding a model later is a PrefabRegistry edit. No code change, no schema change,
// no data migration: instances only ever store the key.
//
// Correct dimensions are the entire point. A visualization that gets a bed 15% too small answers the
// "does the wheelchair fit beside it" question wrongly, which is the question being asked.
[CreateAssetMenu(fileName = "FurnitureCatalog", menuName = "CXR/Furniture Catalog")]
public class FurnitureCatalog : ScriptableObject
{
    public enum MountType { Floor, Wall, Counter, Ceiling }

    [Serializable]
    public class Entry
    {
        [Tooltip("Catalog key. Matches a PrefabRegistry key when real art exists for this item.")]
        public string id;
        public string displayName;
        [Tooltip("Groups the picker: mobility, bedroom, bathroom, kitchen, living, fixtures.")]
        public string category = "living";

        [Header("True dimensions (meters)")]
        public float widthM = 0.6f;    // X, across the item's front
        public float depthM = 0.6f;    // Z, front to back
        public float heightM = 0.8f;   // Y

        [Header("Mounting")]
        public MountType mount = MountType.Floor;
        [Tooltip("Height above finished floor for Wall and Counter items.")]
        public float mountHeightM = 0.9f;

        [Header("Clearances (rules-ready; nothing reads these yet)")]
        [Tooltip("Approach space the item needs in front of it, meters.")]
        public float clearanceFrontM;
        [Tooltip("Space needed to one side, e.g. for a wheelchair transfer.")]
        public float clearanceSideM;

        [Header("Appearance")]
        [Tooltip("Colour of the placeholder box shown until real art exists under this id.")]
        public Color swatch = new Color(0.62f, 0.64f, 0.70f);

        // Wall-mount placement defaults, copied into WallMountDef at placement time so every helper
        // in DecorAlignment / DecorPlacement applies unchanged. Only meaningful for MountType.Wall.
        [Header("Wall-mount defaults")]
        [Range(0f, 1f)] public float decorWidthFrac = 0.6f;
        [Range(0f, 1f)] public float decorHeightFrac = 0.4f;
        public float decorSurfaceOffset = 0.01f;
        public int decorAnchor;        // (int)DecorAlignment.Anchor
        public int decorMountAxis;     // (int)DecorAlignment.MountAxis; 0 = Auto

        public Vector3 SizeMeters => new Vector3(widthM, heightM, depthM);
        public bool IsWallMounted => mount == MountType.Wall;
        public string Label => string.IsNullOrEmpty(displayName) ? id : displayName;
    }

    public List<Entry> entries = new List<Entry>();

    private Dictionary<string, Entry> _lookup;

    public Entry Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        BuildLookup();
        return _lookup.TryGetValue(id, out var e) ? e : null;
    }

    /// <summary>Entries in one category, for the picker grid.</summary>
    public List<Entry> InCategory(string category)
    {
        var list = new List<Entry>();
        foreach (var e in entries)
            if (e != null && !string.IsNullOrEmpty(e.id) &&
                (string.IsNullOrEmpty(category) || e.category == category))
                list.Add(e);
        return list;
    }

    public List<string> Categories()
    {
        var seen = new List<string>();
        foreach (var e in entries)
            if (e != null && !string.IsNullOrEmpty(e.category) && !seen.Contains(e.category))
                seen.Add(e.category);
        return seen;
    }

    /// <summary>
    /// Builds an ObjectInstance for a catalog entry. Free-standing furniture reuses ObjectInstance
    /// from AuthoringTypes.cs verbatim — `boxSizeMeters` is already exactly "the item's true size",
    /// which LayoutConverter has been using for generated massing boxes all along.
    /// </summary>
    public static ObjectInstance NewInstance(Entry entry, Vector3 position, float rotationY)
        => new ObjectInstance
        {
            instanceId = Guid.NewGuid().ToString(),
            prefabType = entry.id,
            position = new[] { position.x, position.y, position.z },
            rotationX = 0f,
            rotationY = rotationY,
            rotationZ = 0f,
            scale = 1f,
            boxSizeMeters = new[] { entry.widthM, entry.heightM, entry.depthM },
            included = true,
            brushPainted = false,
        };

    /// <summary>Builds a WallMountDef, carrying the entry's decor rules across so the pose can be
    /// re-derived whenever the host wall moves.</summary>
    public static WallMountDef NewWallMount(Entry entry, string wallId, float offset, int side)
        => new WallMountDef
        {
            instanceId = Guid.NewGuid().ToString(),
            prefabType = entry.id,
            wallId = wallId,
            offset = offset,
            side = side,
            mountHeight = entry.mountHeightM,
            decorWidthFrac = entry.decorWidthFrac,
            decorHeightFrac = entry.decorHeightFrac,
            decorAnchor = entry.decorAnchor,
            decorSurfaceOffset = entry.decorSurfaceOffset,
            decorMountAxis = entry.decorMountAxis,
            decorFlipMount = false,
            included = true,
        };

    private void BuildLookup()
    {
        if (_lookup != null) return;
        _lookup = new Dictionary<string, Entry>();
        foreach (var e in entries)
            if (e != null && !string.IsNullOrEmpty(e.id)) _lookup[e.id] = e;
    }

    private void OnValidate() => _lookup = null;
}
