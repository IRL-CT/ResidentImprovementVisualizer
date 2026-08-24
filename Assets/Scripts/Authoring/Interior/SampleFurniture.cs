using System.Collections.Generic;
using UnityEngine;

// Footprints for the FurnitureCatalog ids, mirrored into the CXRAuthoring assembly.
//
// WHY THIS DUPLICATES THE CATALOG: FurnitureCatalog is a ScriptableObject in Assembly-CSharp, and
// CXRAuthoring has no references, so PlanBuilder, which lives here so the sample plans can be unit
// tested, cannot read the .asset. The numbers below are transcribed from Assets/Resources/
// FurnitureCatalog.asset and SampleHomeInstaller re-checks them against the live catalog on seed, so
// drift is reported rather than discovered.
//
// Field order matches FurnitureCatalog.Entry: widthM is across the item's front (local X), depthM is
// front-to-back (local Z), heightM is up (local Y). Note that ObjectInstance.boxSizeMeters wants
// [w, h, d]. See BoxSize, which does that reorder so callers never get it wrong.
public static class SampleFurniture
{
    public struct Item
    {
        public string id;
        public float width;          // local X, across the front
        public float depth;          // local Z, front to back
        public float height;         // local Y
        public bool  wallMounted;
        public float mountHeight;    // meters AFF; the anchor for wall-mounted items
        public float decorWidthFrac;
        public float decorHeightFrac;
        public float decorSurfaceOffset;

        /// <summary>The [w, h, d] triple ObjectInstance.boxSizeMeters expects.</summary>
        public float[] BoxSize => new[] { width, height, depth };
    }

    /// <summary>Fallback for an unknown key. Matches HomeRenderer.ItemSize's own last resort.</summary>
    public static readonly Item Unknown = new Item
    {
        id = null, width = 0.6f, depth = 0.6f, height = 0.8f,
        wallMounted = false, mountHeight = 0.9f,
        decorWidthFrac = 0.6f, decorHeightFrac = 0.4f, decorSurfaceOffset = 0.01f,
    };

    public static bool TryGet(string id, out Item item) => Table.TryGetValue(id ?? "", out item);

    public static Item Get(string id) => Table.TryGetValue(id ?? "", out var item) ? item : Unknown;

    public static bool Exists(string id) => !string.IsNullOrEmpty(id) && Table.ContainsKey(id);

    public static IEnumerable<Item> All => Table.Values;

    // -------------------------------------------------------------------------------------------

    private static Item Floor(string id, float w, float d, float h) => new Item
    {
        id = id, width = w, depth = d, height = h,
        wallMounted = false, mountHeight = 0.9f,
        decorWidthFrac = 0.6f, decorHeightFrac = 0.4f, decorSurfaceOffset = 0.01f,
    };

    private static Item Wall(string id, float w, float d, float h, float mountHeight) => new Item
    {
        id = id, width = w, depth = d, height = h,
        wallMounted = true, mountHeight = mountHeight,
        decorWidthFrac = 0.5f, decorHeightFrac = 0.25f, decorSurfaceOffset = 0.01f,
    };

    private static readonly Dictionary<string, Item> Table = Build();

    private static Dictionary<string, Item> Build()
    {
        var items = new[]
        {
            // mobility
            Floor("wheelchair",      0.66f, 1.22f, 0.95f),
            Floor("walker",          0.61f, 0.66f, 0.90f),
            Floor("hospital_bed",    0.91f, 2.13f, 0.65f),
            Floor("transfer_bench",  0.41f, 0.86f, 0.48f),
            Floor("patient_lift",    0.66f, 1.19f, 1.35f),

            // bedroom
            Floor("twin_bed",        0.99f, 2.03f, 0.60f),
            Floor("full_bed",        1.37f, 1.91f, 0.60f),
            Floor("nightstand",      0.46f, 0.41f, 0.61f),
            Floor("dresser",         1.22f, 0.51f, 0.81f),
            Floor("wardrobe",        1.02f, 0.61f, 1.83f),

            // bathroom
            Floor("toilet",          0.51f, 0.71f, 0.79f),
            Floor("sink_pedestal",   0.56f, 0.46f, 0.84f),
            Floor("vanity",          0.91f, 0.53f, 0.84f),
            Floor("bathtub",         0.76f, 1.52f, 0.56f),
            Floor("roll_in_shower",  0.91f, 1.52f, 0.05f),
            Floor("shower_seat",     0.41f, 0.41f, 0.48f),
            Wall ("grab_bar_24",     0.61f, 0.09f, 0.04f, 0.84f),
            Wall ("grab_bar_36",     0.91f, 0.09f, 0.04f, 0.84f),

            // kitchen
            Floor("base_cabinet",    0.91f, 0.61f, 0.91f),
            Floor("sink_base",       0.76f, 0.61f, 0.91f),
            Floor("refrigerator",    0.91f, 0.76f, 1.78f),
            Floor("range",           0.76f, 0.66f, 0.91f),
            Floor("island",          1.22f, 0.76f, 0.91f),
            // 1.75 is the CENTRE, which is what mountHeight means everywhere. That puts the cabinet's
            // bottom back at the standard 1.37 m: 0.46 m of clear splashback over a 0.91 m counter.
            Wall ("wall_cabinet",    0.76f, 0.33f, 0.76f, 1.75f),

            // living
            Floor("sofa",            1.83f, 0.89f, 0.84f),
            Floor("armchair",        0.85f, 0.85f, 0.84f),
            Floor("recliner",        0.89f, 0.97f, 1.02f),
            Floor("dining_table",    1.07f, 1.07f, 0.76f),
            Floor("coffee_table",    1.07f, 0.53f, 0.46f),
            Floor("tv_stand",        1.22f, 0.41f, 0.61f),

            // fixtures
            Wall ("handrail",        1.22f, 0.08f, 0.04f, 0.91f),
            Wall ("light_switch",    0.08f, 0.02f, 0.12f, 1.12f),
            Wall ("outlet",          0.08f, 0.02f, 0.12f, 0.38f),
            Wall ("thermostat",      0.12f, 0.03f, 0.09f, 1.32f),
            Floor("threshold_ramp",  0.91f, 0.30f, 0.03f),
        };

        var map = new Dictionary<string, Item>(items.Length);
        foreach (var i in items) map[i.id] = i;
        return map;
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The item's axis-aligned footprint in world XZ once rotated by <paramref name="yaw"/> degrees.
    /// Only multiples of 90 are used by the samples, so this snaps rather than building a full OBB,
    /// an approximate box would make the "furniture is inside its room" test approximate too.
    /// </summary>
    public static Vector2 FootprintXZ(Item item, float yaw)
    {
        int quarter = Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / 90f) % 4;
        bool swapped = quarter == 1 || quarter == 3;
        return swapped ? new Vector2(item.depth, item.width) : new Vector2(item.width, item.depth);
    }
}
