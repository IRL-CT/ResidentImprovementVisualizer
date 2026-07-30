using System.Collections.Generic;
using UnityEngine;

// Builds the scene from a HomeDoc. The interior counterpart of WorldRenderer, and it follows the same
// proven contract: RenderHome is IDEMPOTENT — call it any time, it tears down and rebuilds, and the
// result depends only on the data. No incremental patching, no diffing, no drift between what is on
// screen and what will be saved.
//
// Only the ACTIVE variant's ACTIVE level is rendered. Variants are switched by re-rendering, which is
// fast enough at house scale (a few hundred walls) that it feels instant, and it means the compare
// feature needs no special machinery.
//
// The exterior is delegated, not reimplemented: when the optional layer is enabled, the variant's
// SiteDef is handed to the existing WorldRenderer through ExteriorBridge. Ramps, railings, and patios
// then render through code that already works and is already tested.
public class HomeRenderer : MonoBehaviour
{
    [Header("Palettes")]
    // USER WIRES THIS IN INSPECTOR:
    [SerializeField] private InteriorMaterialPalette materialPalette;
    // USER WIRES THIS IN INSPECTOR:
    [SerializeField] private FurnitureCatalog furnitureCatalog;
    // USER WIRES THIS IN INSPECTOR: shared with the Brownfield scene; furniture art lands here.
    [SerializeField] private PrefabRegistry prefabRegistry;

    [Header("Optional exterior layer (off by default)")]
    // USER WIRES THIS IN INSPECTOR: the disabled 'Exterior' subtree holding a Terrain + WorldRenderer.
    [SerializeField] private GameObject exteriorRoot;
    // USER WIRES THIS IN INSPECTOR:
    [SerializeField] private WorldRenderer worldRenderer;
    // USER WIRES THIS IN INSPECTOR: the flat 'GroundPad' the home sits on indoors. The exterior brings
    // its own Terrain at the same height, so exactly one of the two is active at a time.
    [SerializeField] private GameObject groundPad;

    [Header("Ghost overlay (variant compare)")]
    [SerializeField] private Material ghostMaterial;
    [SerializeField] private Color ghostAdded = new Color(0.30f, 0.80f, 0.40f, 0.35f);
    [SerializeField] private Color ghostRemoved = new Color(0.90f, 0.35f, 0.35f, 0.35f);

    public InteriorMaterialPalette MaterialPalette => materialPalette;
    public FurnitureCatalog Catalog => furnitureCatalog;
    public PrefabRegistry Prefabs => prefabRegistry;

    /// <summary>
    /// The exterior renderer, so OutdoorTool can read its PathMaterialPalette / FencePalette and offer
    /// exactly the surfaces and railing types that will actually render. Null when no exterior is wired.
    /// </summary>
    public WorldRenderer World => worldRenderer;

    // ---------------------------------------------------------------------------------------

    private Transform _root, _wallRoot, _floorRoot, _ceilingRoot, _furnitureRoot, _mountRoot, _ghostRoot;
    private readonly Dictionary<string, GameObject> _byId = new Dictionary<string, GameObject>();

    // Meshes are generated per render, so they are not owned by any asset and Unity will not collect
    // them. Tracking and explicitly destroying them is what stops a long editing session leaking a
    // mesh per wall per rebuild.
    private readonly List<Mesh> _ownedMeshes = new List<Mesh>();

    private HomeDoc _doc;
    private VariantDef _variant;
    private LevelDef _level;
    private bool _ceilingsVisible;

    public HomeDoc Doc => _doc;
    public VariantDef Variant => _variant;
    public LevelDef Level => _level;
    public bool CeilingsVisible => _ceilingsVisible;

    // ---------------------------------------------------------------------------------------
    // Rendering
    // ---------------------------------------------------------------------------------------

    public void RenderHome(HomeDoc doc, string variantId = null, int levelIndex = 0)
    {
        _doc = doc;
        _variant = doc == null
            ? null
            : (HomeStore.FindVariant(doc, variantId) ?? HomeStore.ActiveVariant(doc));

        _level = _variant?.levels != null && _variant.levels.Count > 0
            ? _variant.levels[Mathf.Clamp(levelIndex, 0, _variant.levels.Count - 1)]
            : null;

        Rebuild();
    }

    /// <summary>Full teardown and rebuild of the current level. Every edit ends here.</summary>
    public void Rebuild()
    {
        EnsureRoots();
        ClearRendered();

        if (_level == null) return;

        RenderWalls();
        RenderRooms();
        RenderFurniture();
        RenderWallMounts();

        SetCeilingsVisible(_ceilingsVisible);
        RenderExterior();
    }

    // Targeted rebuilds. Walls and openings share geometry — an opening is a gap in its wall's box
    // list — so changing either has to rebuild both, which is why there is no RebuildOpenings.
    public void RebuildWalls() { EnsureRoots(); ClearGroup(_wallRoot); RenderWalls(); RenderWallMounts(); }
    public void RebuildRooms() { EnsureRoots(); ClearGroup(_floorRoot); ClearGroup(_ceilingRoot); RenderRooms(); SetCeilingsVisible(_ceilingsVisible); }
    public void RebuildFurniture() { EnsureRoots(); ClearGroup(_furnitureRoot); RenderFurniture(); }

    // ---------------------------------------------------------------------------------------

    private void RenderWalls()
    {
        if (_level?.walls == null) return;

        foreach (var wall in _level.walls)
        {
            if (wall == null || string.IsNullOrEmpty(wall.id)) continue;

            Mesh mesh = WallMeshBuilder.Build(wall, _level);
            if (mesh == null) continue;   // degenerate, or entirely consumed by an opening
            _ownedMeshes.Add(mesh);

            var frame = WallMeshBuilder.BuildFrame(wall, _level);
            var go = new GameObject($"Wall_{wall.id}");
            go.transform.SetParent(_wallRoot, false);
            // The mesh is built in world-axis offsets from endpoint `a`, so no rotation is needed.
            go.transform.position = frame.origin;

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = new[]
            {
                Mat(wall.materialLeft, InteriorMaterialPalette.Surface.Wall),
                Mat(wall.materialRight, InteriorMaterialPalette.Surface.Wall),
                materialPalette != null ? materialPalette.Fallback(InteriorMaterialPalette.Surface.Any) : null,
            };
            go.AddComponent<MeshCollider>().sharedMesh = mesh;

            Mark(go, HomeElementMarker.Kind.Wall, wall.id, null);
        }

        RenderOpeningHandles();
    }

    // Openings have no geometry of their own — they are the absence of wall. But they still need to be
    // clickable and draggable, so each gets an invisible collider filling its void. Without this an
    // opening could only be selected by clicking the wall around it, which is exactly backwards.
    private void RenderOpeningHandles()
    {
        if (_level?.openings == null || _level.walls == null) return;

        foreach (var opening in _level.openings)
        {
            if (opening == null || string.IsNullOrEmpty(opening.id)) continue;

            WallDef host = FindWall(opening.wallId);
            if (host == null) continue;

            var frame = WallMeshBuilder.BuildFrame(host, _level);
            float wallHeight = WallLayout.EffectiveHeight(host, _level);
            float sill = Mathf.Clamp(opening.sillHeight, 0f, wallHeight);
            float top = Mathf.Min(sill + (opening.height > 0f ? opening.height : wallHeight), wallHeight);

            var go = new GameObject($"Opening_{opening.id}");
            go.transform.SetParent(_wallRoot, false);
            go.transform.position = frame.origin
                                    + frame.forward * opening.offset
                                    + Vector3.up * (0.5f * (sill + top));
            go.transform.rotation = Quaternion.LookRotation(frame.forward, Vector3.up);

            var box = go.AddComponent<BoxCollider>();
            // Slightly proud of the wall on both faces so the opening wins a tie against the header
            // above it when both are under the cursor.
            box.size = new Vector3(frame.thickness + 0.02f,
                                   Mathf.Max(0.05f, top - sill),
                                   Mathf.Max(0.05f, opening.width));

            var mr = go.AddComponent<MeshRenderer>();
            mr.enabled = false;   // collider only — the void is the visual

            Mark(go, HomeElementMarker.Kind.Opening, opening.id, host.id);
        }
    }

    private void RenderRooms()
    {
        if (_level?.rooms == null) return;

        foreach (var room in _level.rooms)
        {
            if (room == null || string.IsNullOrEmpty(room.id)) continue;

            Mesh floor = RoomMeshBuilder.BuildFloor(room, _level);
            if (floor != null)
            {
                _ownedMeshes.Add(floor);
                var go = Surface($"Floor_{room.id}", _floorRoot, floor,
                                 Mat(room.floorMaterial, InteriorMaterialPalette.Surface.Floor));
                Mark(go, HomeElementMarker.Kind.Floor, room.id, room.id);
            }

            Mesh ceiling = RoomMeshBuilder.BuildCeiling(room, _level);
            if (ceiling != null)
            {
                _ownedMeshes.Add(ceiling);
                var go = Surface($"Ceiling_{room.id}", _ceilingRoot, ceiling,
                                 Mat(room.ceilingMaterial, InteriorMaterialPalette.Surface.Ceiling));
                Mark(go, HomeElementMarker.Kind.Ceiling, room.id, room.id);
            }
        }
    }

    private GameObject Surface(string name, Transform parent, Mesh mesh, Material material)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
        return go;
    }

    private void RenderFurniture()
    {
        if (_level?.furniture == null) return;

        foreach (var item in _level.furniture)
        {
            if (item == null || !item.included || string.IsNullOrEmpty(item.instanceId)) continue;

            var go = SpawnCatalogItem(item.prefabType, ItemSize(item), _furnitureRoot);
            if (go == null) continue;

            go.name = $"Furniture_{item.instanceId}";
            go.transform.position = item.position != null && item.position.Length >= 3
                ? new Vector3(item.position[0], item.position[1], item.position[2])
                : Vector3.zero;
            go.transform.rotation = Quaternion.Euler(item.rotationX, item.rotationY, item.rotationZ);

            Mark(go, HomeElementMarker.Kind.Furniture, item.instanceId, null);
        }
    }

    // Wall-mounted items derive their pose from the host wall rather than storing it, so moving a wall
    // carries its grab bars with it. Same guarantee DecorPlacement.TryReseat gives tile decor.
    private void RenderWallMounts()
    {
        if (_level?.wallMounted == null) return;

        foreach (var mount in _level.wallMounted)
        {
            if (mount == null || !mount.included || string.IsNullOrEmpty(mount.instanceId)) continue;

            WallDef host = FindWall(mount.wallId);
            if (host == null) continue;

            var entry = furnitureCatalog != null ? furnitureCatalog.Get(mount.prefabType) : null;
            Vector3 size = entry != null ? entry.SizeMeters : new Vector3(0.4f, 0.05f, 0.05f);

            var go = SpawnCatalogItem(mount.prefabType, size, _mountRoot);
            if (go == null) continue;

            var frame = WallMeshBuilder.BuildFrame(host, _level);
            // side 0 = the wall's left face; push out by half the thickness plus the z-fight epsilon.
            Vector3 outward = mount.side == WallSide.Left ? frame.left : -frame.left;
            float push = 0.5f * frame.thickness + Mathf.Max(0.001f, mount.decorSurfaceOffset);

            go.name = $"WallMount_{mount.instanceId}";
            go.transform.position = frame.origin
                                    + frame.forward * mount.offset
                                    + Vector3.up * mount.mountHeight
                                    + outward * push;
            go.transform.rotation = Quaternion.LookRotation(outward, Vector3.up);

            Mark(go, HomeElementMarker.Kind.WallMount, mount.instanceId, host.id);
        }
    }

    /// <summary>
    /// Resolves a catalog key to a GameObject: the real prefab when PrefabRegistry has art under that
    /// key, otherwise a correctly sized labeled box. This one branch is what lets the tool be useful
    /// with no interior art at all, and lets art appear later item by item with no other change.
    /// </summary>
    private GameObject SpawnCatalogItem(string key, Vector3 sizeMeters, Transform parent)
    {
        // PrefabRegistry.GetPrefab dereferences both `entries` and each entry's `key` without
        // guarding, so an unpopulated registry or a blank row would throw. Screen for that here
        // rather than touching the shared Brownfield file.
        GameObject prefab = null;
        if (prefabRegistry != null && prefabRegistry.entries != null && !string.IsNullOrEmpty(key))
        {
            foreach (var e in prefabRegistry.entries)
            {
                if (e == null || string.IsNullOrEmpty(e.key)) continue;
                if (string.Equals(e.key, key, System.StringComparison.OrdinalIgnoreCase))
                {
                    prefab = e.prefab;
                    break;
                }
            }
        }

        if (prefab != null)
        {
            var go = Instantiate(prefab, parent);
            if (go.GetComponent<Collider>() == null) AddFittedCollider(go, sizeMeters);
            return go;
        }

        return BuildPlaceholderBox(key, sizeMeters, parent);
    }

    private GameObject BuildPlaceholderBox(string key, Vector3 size, Transform parent)
    {
        var entry = furnitureCatalog != null ? furnitureCatalog.Get(key) : null;

        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.transform.SetParent(parent, false);
        go.transform.localScale = new Vector3(
            Mathf.Max(0.02f, size.x), Mathf.Max(0.02f, size.y), Mathf.Max(0.02f, size.z));

        // The cube primitive is centred on its origin; furniture sits ON the floor, so lift it by
        // half its height. Otherwise every item is buried to its waist.
        var pivot = new GameObject("Item");
        pivot.transform.SetParent(parent, false);
        go.transform.SetParent(pivot.transform, false);
        go.transform.localPosition = new Vector3(0f, 0.5f * Mathf.Max(0.02f, size.y), 0f);

        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.color = entry != null ? entry.swatch : new Color(0.62f, 0.64f, 0.70f);
            mr.sharedMaterial = mat;
        }

        AddLabel(pivot.transform, entry != null ? entry.Label : key, size.y);
        return pivot;
    }

    // A floating name over each placeholder. Without it a room of grey boxes is unreadable — the label
    // is what makes "bed / wheelchair / toilet" legible before any art exists.
    private void AddLabel(Transform parent, string text, float aboveHeight)
    {
        if (string.IsNullOrEmpty(text)) return;

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) return;   // font name changed across Unity versions; skip rather than throw

        var go = new GameObject("Label");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, aboveHeight + 0.12f, 0f);

        var tm = go.AddComponent<TextMesh>();
        tm.text = text;
        tm.font = font;
        tm.fontSize = 48;
        tm.characterSize = 0.02f;
        tm.anchor = TextAnchor.LowerCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = new Color(0.15f, 0.16f, 0.18f);
        go.GetComponent<MeshRenderer>().sharedMaterial = font.material;

        // Without this the label is edge-on from directly above, i.e. invisible in plan view.
        go.AddComponent<LabelBillboard>();
    }

    private void AddFittedCollider(GameObject go, Vector3 size)
    {
        var box = go.AddComponent<BoxCollider>();
        box.size = size;
        box.center = new Vector3(0f, 0.5f * size.y, 0f);
    }

    // ---------------------------------------------------------------------------------------
    // View state
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Ceilings hide in plan and dollhouse views and show in walkthrough. Kept as separate meshes
    /// from floors precisely so this is a SetActive rather than a geometry rebuild.
    /// </summary>
    public void SetCeilingsVisible(bool visible)
    {
        _ceilingsVisible = visible;
        if (_ceilingRoot != null) _ceilingRoot.gameObject.SetActive(visible);
    }

    /// <summary>
    /// Renders another variant as a translucent overlay for Compare. Added elements tint green,
    /// removed tint red — but the textual change list is the half that gets read aloud in a meeting,
    /// so this is the supporting view, not the primary one.
    /// </summary>
    public void SetGhostVariant(string variantId, bool on)
    {
        EnsureRoots();
        ClearGroup(_ghostRoot);
        if (!on || _doc == null || _variant == null) return;

        var other = HomeStore.FindVariant(_doc, variantId);
        if (other?.levels == null || other.levels.Count == 0) return;

        var changes = VariantDiff.Compare(other, _variant);
        var addedIds = new HashSet<string>();
        var removedIds = new HashSet<string>();
        foreach (var c in changes)
        {
            if (c.type == VariantDiff.ChangeType.Added) addedIds.Add(c.id);
            else if (c.type == VariantDiff.ChangeType.Removed) removedIds.Add(c.id);
        }

        var ghostLevel = other.levels[0];
        foreach (var wall in ghostLevel.walls ?? new List<WallDef>())
        {
            if (wall == null || !removedIds.Contains(wall.id)) continue;   // only what this variant lost

            Mesh mesh = WallMeshBuilder.Build(wall, ghostLevel);
            if (mesh == null) continue;
            _ownedMeshes.Add(mesh);

            var frame = WallMeshBuilder.BuildFrame(wall, ghostLevel);
            var go = new GameObject($"Ghost_{wall.id}");
            go.transform.SetParent(_ghostRoot, false);
            go.transform.position = frame.origin;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var mr = go.AddComponent<MeshRenderer>();
            var mat = ghostMaterial != null
                ? new Material(ghostMaterial)
                : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.color = ghostRemoved;
            mr.sharedMaterials = new[] { mat, mat, mat };
        }
    }

    // ---------------------------------------------------------------------------------------
    // Optional exterior layer
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the outdoor layer, or takes it down. The whole feature is a nullable SiteDef plus
    /// ExteriorBridge — WorldRenderer already knows how to draw paths, fences, surface strokes and
    /// outdoor objects, so an entry ramp costs no new geometry code.
    ///
    /// Nothing appears until the home has BOTH opted in and had something drawn: an enabled but empty
    /// exterior would otherwise swap the tidy indoor ground pad for a bare 60 m terrain and look like
    /// a bug.
    /// </summary>
    private void RenderExterior()
    {
        bool wanted = _doc != null && _doc.exteriorEnabled && ExteriorBridge.HasContent(_variant);

        if (exteriorRoot != null) exteriorRoot.SetActive(wanted);

        // The exterior Terrain sits at the same height as the indoor ground pad, so leaving both on
        // z-fights across the whole floor. Exactly one owns the ground.
        if (groundPad != null) groundPad.SetActive(!wanted);

        if (!wanted || worldRenderer == null) return;

        var env = ExteriorBridge.ToEnvironmentDef(_doc, _variant);
        if (env != null)
            worldRenderer.RenderEnvironment(env, new Dictionary<string, BuildingDef>(), true);
    }

    // ---------------------------------------------------------------------------------------
    // Lookup and teardown
    // ---------------------------------------------------------------------------------------

    public GameObject GetGO(string id)
        => !string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out var go) ? go : null;

    public Transform Root { get { EnsureRoots(); return _root; } }

    public WallDef FindWall(string wallId)
    {
        if (_level?.walls == null || string.IsNullOrEmpty(wallId)) return null;
        foreach (var w in _level.walls) if (w != null && w.id == wallId) return w;
        return null;
    }

    private Material Mat(string id, InteriorMaterialPalette.Surface surface)
        => materialPalette != null ? materialPalette.Get(id, surface) : null;

    /// <summary>
    /// The true size of a furniture instance, in priority order: the size stored on the instance,
    /// then the catalog default, then a small fallback.
    ///
    /// `boxSizeMeters` wins because a user may have resized this particular item (a built-in counter
    /// run, an unusually narrow bed), and that override must survive a catalog edit. The field is the
    /// existing ObjectInstance one, carrying [w, h, d] exactly as LayoutConverter writes it.
    /// </summary>
    private Vector3 ItemSize(ObjectInstance item)
    {
        if (item?.boxSizeMeters != null && item.boxSizeMeters.Length >= 3)
            return new Vector3(item.boxSizeMeters[0], item.boxSizeMeters[1], item.boxSizeMeters[2]);

        var entry = furnitureCatalog != null ? furnitureCatalog.Get(item?.prefabType) : null;
        if (entry != null) return entry.SizeMeters;

        return new Vector3(0.6f, 0.8f, 0.6f);
    }

    private void Mark(GameObject go, HomeElementMarker.Kind kind, string id, string parentId)
    {
        var marker = go.AddComponent<HomeElementMarker>();
        marker.kind = kind;
        marker.id = id;
        marker.parentId = parentId;
        if (!string.IsNullOrEmpty(id)) _byId[id] = go;
    }

    private void EnsureRoots()
    {
        if (_root != null) return;

        _root = new GameObject("HomeRender").transform;
        _root.SetParent(transform, false);

        _wallRoot = Group("Walls");
        _floorRoot = Group("Floors");
        _ceilingRoot = Group("Ceilings");
        _furnitureRoot = Group("Furniture");
        _mountRoot = Group("WallMounted");
        _ghostRoot = Group("Ghost");
    }

    private Transform Group(string name)
    {
        var t = new GameObject(name).transform;
        t.SetParent(_root, false);
        return t;
    }

    public void ClearRendered()
    {
        _byId.Clear();
        ClearGroup(_wallRoot);
        ClearGroup(_floorRoot);
        ClearGroup(_ceilingRoot);
        ClearGroup(_furnitureRoot);
        ClearGroup(_mountRoot);
        ClearGroup(_ghostRoot);
        ReleaseMeshes();
    }

    private void ClearGroup(Transform group)
    {
        if (group == null) return;
        for (int i = group.childCount - 1; i >= 0; i--)
        {
            var child = group.GetChild(i).gameObject;
            if (Application.isPlaying) Destroy(child); else DestroyImmediate(child);
        }
    }

    private void ReleaseMeshes()
    {
        foreach (var m in _ownedMeshes)
        {
            if (m == null) continue;
            if (Application.isPlaying) Destroy(m); else DestroyImmediate(m);
        }
        _ownedMeshes.Clear();
    }

    private void OnDestroy() => ReleaseMeshes();
}
