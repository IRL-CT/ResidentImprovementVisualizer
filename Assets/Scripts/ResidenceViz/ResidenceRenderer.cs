using System.Collections.Generic;
using UnityEngine;

// Builds the scene from a ResidenceDoc. The interior counterpart of WorldRenderer, and it follows the same
// proven contract: RenderResidence is IDEMPOTENT. Call it any time, it tears down and rebuilds, and the
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
public class ResidenceRenderer : MonoBehaviour
{
    [Header("Palettes")]
    // USER WIRES THIS IN INSPECTOR:
    [SerializeField] private InteriorMaterialPalette materialPalette;
    // USER WIRES THIS IN INSPECTOR:
    [SerializeField] private FurnitureCatalog furnitureCatalog;
    // USER WIRES THIS IN INSPECTOR: Assets/Resources/SensorCatalog.asset: the smart home devices.
    [SerializeField] private SensorCatalog sensorCatalog;
    // USER WIRES THIS IN INSPECTOR: shared with the Site scene; furniture art lands here.
    [SerializeField] private PrefabRegistry prefabRegistry;

    [Header("Optional exterior layer (off by default)")]
    // USER WIRES THIS IN INSPECTOR: the disabled 'Exterior' subtree holding a Terrain + WorldRenderer.
    [SerializeField] private GameObject exteriorRoot;
    // USER WIRES THIS IN INSPECTOR:
    [SerializeField] private WorldRenderer worldRenderer;
    // USER WIRES THIS IN INSPECTOR: the flat 'GroundPad' the residence sits on indoors. The exterior brings
    // its own Terrain at the same height, so exactly one of the two is active at a time.
    [SerializeField] private GameObject groundPad;

    [Header("Ghost overlay (variant compare)")]
    [SerializeField] private Material ghostMaterial;
    [SerializeField] private Color ghostAdded = new Color(0.30f, 0.80f, 0.40f, 0.35f);
    [SerializeField] private Color ghostRemoved = new Color(0.90f, 0.35f, 0.35f, 0.35f);

    public InteriorMaterialPalette MaterialPalette => materialPalette;
    public FurnitureCatalog Catalog => furnitureCatalog;

    /// <summary>
    /// THE one way to turn an `ObjectInstance.prefabType` into an entry. The shipped catalog first,
    /// then this residence's own items, then null.
    /// </summary>
    /// <remarks>
    /// Every furniture lookup in the app goes through here, in the renderer and out of it, so that
    /// "what is this thing" has a single answer. Reaching for `Catalog.Get` directly is the bug: it
    /// finds the 35 shipped items and silently reports a custom one as unknown, which downstream
    /// reads as a nameless box with no size and no Reset button.
    ///
    /// Null is a legitimate answer, and callers already handle it: it is what a custom item whose
    /// definition has been deleted returns. The placement keeps its own stored `boxSizeMeters`, so
    /// nothing about it moves or resizes; it just loses the controls that need a definition.
    /// </remarks>
    public FurnitureCatalog.Entry EntryFor(string id)
        => (furnitureCatalog != null ? furnitureCatalog.Get(id) : null)
           ?? FurnitureCatalog.EntryFor(CustomItems.Find(_doc, id));
    public SensorCatalog Sensors => sensorCatalog;
    public PrefabRegistry Prefabs => prefabRegistry;

    /// <summary>
    /// The exterior renderer, so OutdoorTool can read its PathMaterialPalette / FencePalette and offer
    /// exactly the surfaces and railing types that will actually render. Null when no exterior is wired.
    /// </summary>
    public WorldRenderer World => worldRenderer;

    // ---------------------------------------------------------------------------------------

    private Transform _root, _wallRoot, _floorRoot, _ceilingRoot, _furnitureRoot, _mountRoot,
                      _sensorRoot, _occupantRoot, _ghostRoot;
    private readonly Dictionary<string, GameObject> _byId = new Dictionary<string, GameObject>();

    // Occupant markers are the one thing here that moves without a rebuild: the clock repositions them
    // every simulated minute, and a full teardown per minute would be absurd. Caching the pieces each
    // marker needs keeps the per-tick cost to a transform write and, rarely, a string compare.
    private sealed class OccupantView
    {
        public GameObject go;
        public TextMesh label;
        public string lastText;
    }

    private readonly List<OccupantView> _occupantViews = new List<OccupantView>();
    private readonly List<OccupantDef> _occupantOrder = new List<OccupantDef>();

    // Sensors are the second thing that changes without a rebuild: the clock walks a simulated day and
    // every device idles, activates or raises an alert as it goes. Caching the renderer and its own
    // material per device keeps a tick to a colour write, and the material has to be per-device
    // anyway, because tinting a shared one would light every door sensor in the residence at once.
    private sealed class SensorView
    {
        public GameObject go;
        public MeshRenderer box;
        public Material material;
        public Color baseColor;
        public string sensorId;
        public SensorSim.State lastState = SensorSim.State.Idle;
    }

    private readonly List<SensorView> _sensorViews = new List<SensorView>();

    /// <summary>
    /// The simulated day the plan is showing: which devices are active, and when a caregiver would be
    /// paged. Derived, cached, and invalidated on every rebuild: the same contract
    /// OccupancyModel.InvalidateCache has, and for the same reason. SensorSim walks 1,440 minutes, and
    /// TimelineBar.Draw runs twice a frame.
    /// </summary>
    public SensorSim.Day SensorDay
    {
        get
        {
            if (_sensorDayValid) return _sensorDay;
            _sensorDay = SensorSim.Simulate(_variant, _level, SensorDayMode, SensorDaySeed);
            _sensorDayValid = true;
            return _sensorDay;
        }
    }

    /// <summary>
    /// Which day the plan is showing. Routine is the household's ordinary one and a correct package
    /// raises nothing on it; Eventful acts out the report's scenarios. Switching invalidates the cache
    /// rather than recomputing, so a click costs nothing until something asks.
    /// </summary>
    public SensorSim.Mode SensorDayMode
    {
        get => _sensorDayMode;
        set { if (_sensorDayMode == value) return; _sensorDayMode = value; InvalidateSensorDay(); }
    }

    public int SensorDaySeed
    {
        get => _sensorDaySeed;
        set { if (_sensorDaySeed == value) return; _sensorDaySeed = value; InvalidateSensorDay(); }
    }

    private SensorSim.Day _sensorDay;
    private bool _sensorDayValid;
    private SensorSim.Mode _sensorDayMode = SensorSim.Mode.Eventful;
    private int _sensorDaySeed;

    public void InvalidateSensorDay() { _sensorDayValid = false; _sensorDay = default; }

    /// <summary>
    /// The time of day the occupant markers are placed at. Transient: never saved, never undone. See
    /// OccupancyClock for why it lives here rather than in a tool or in the document.
    /// </summary>
    public OccupancyClock Occupancy { get; } = new OccupancyClock();

    // Meshes are generated per render, so they are not owned by any asset and Unity will not collect
    // them. Tracking and explicitly destroying them is what stops a long editing session leaking a
    // mesh per wall per rebuild.
    private readonly List<Mesh> _ownedMeshes = new List<Mesh>();

    // The overlay's meshes are tracked separately because it is rebuilt on its own, without a full
    // teardown: BuildGhost clears _ghostRoot but nothing released what those GameObjects were drawing,
    // so every toggle of the chip orphaned a mesh per ghosted wall and room until the next Rebuild.
    private readonly List<Mesh> _ghostMeshes = new List<Mesh>();

    private ResidenceDoc _doc;
    private VariantDef _variant;
    private LevelDef _level;
    // Which story is on screen. The overlay has to read the same one out of the OTHER variant, and
    // a hardcoded levels[0] there would ghost the ground floor against whatever is being rendered.
    private int _levelIndex;
    private bool _ceilingsVisible;

    public ResidenceDoc Doc => _doc;
    public VariantDef Variant => _variant;
    public LevelDef Level => _level;
    public bool CeilingsVisible => _ceilingsVisible;

    // ---------------------------------------------------------------------------------------
    // Rendering
    // ---------------------------------------------------------------------------------------

    public void RenderResidence(ResidenceDoc doc, string variantId = null, int levelIndex = 0)
    {
        _doc = doc;
        _variant = doc == null
            ? null
            : (ResidenceStore.FindVariant(doc, variantId) ?? ResidenceStore.ActiveVariant(doc));

        _levelIndex = Mathf.Max(0, levelIndex);
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

        // The variant or the level may have changed under us (a variant switch comes through here) 
        // and a simulated day belongs to exactly one of them.
        InvalidateSensorDay();

        if (_level == null) return;

        RenderWalls();
        RenderRooms();
        RenderFurniture();
        RenderWallMounts();
        RenderSensors();
        RenderOccupants();

        SetCeilingsVisible(_ceilingsVisible);
        SetOccupantsVisible(_occupantsVisible);
        RenderExterior();

        // Last, and the reason the ghost has state at all: ClearRendered above wiped _ghostRoot, so
        // without this every edit, undo and variant switch would silently take the overlay away while
        // the UI went on claiming it was showing.
        BuildGhost();
    }

    // Targeted rebuilds. Walls and openings share geometry: an opening is a gap in its wall's box
    // list, so changing either has to rebuild both, which is why there is no RebuildOpenings.
    //
    // Mounts derive their pose from their host wall, so rebuilding walls has to rebuild them too, and
    // CLEAR them first, or every call leaves a second copy of every grab bar in the scene.
    //
    // Each ends in BuildGhost for the same reason Rebuild does: the overlay is a picture of a diff, so
    // an edit routed through here without it leaves the ghost describing the residence as it was before the
    // edit. It costs one bool test while the overlay is off.
    public void RebuildWalls() { EnsureRoots(); ClearGroup(_wallRoot); RenderWalls(); RebuildMounts(); RebuildSensors(); }   // both end in BuildGhost
    // Ends in InvalidateCache because OccupancyModel's free-floor search is memoised against the room
    // POLYGON while its key is the room ID, and RoomRegions.Sync reshapes a room without changing its
    // id, which is the whole point of it. Every caller today also runs a full Rebuild, which invalidates
    // anyway; this is here so the targeted path cannot become the one that leaves people standing where
    // the old polygon put them.
    public void RebuildRooms() { EnsureRoots(); ClearGroup(_floorRoot); ClearGroup(_ceilingRoot); RenderRooms(); SetCeilingsVisible(_ceilingsVisible); OccupancyModel.InvalidateCache(); RebuildSensors(); }
    public void RebuildFurniture() { EnsureRoots(); ClearGroup(_furnitureRoot); RenderFurniture(); RebuildSensors(); }
    public void RebuildMounts() { EnsureRoots(); ClearGroup(_mountRoot); RenderWallMounts(); BuildGhost(); }
    public void RebuildOccupants() { EnsureRoots(); ClearOccupants(); RenderOccupants(); }

    // Sensors derive their pose from the element they watch, so a wall, opening or furniture rebuild
    // has to re-place them: the same reason RebuildWalls ends in RebuildMounts. And every one of
    // those edits can change what the day looks like (a door moved is a door someone passes through
    // at a different minute), so the simulated day goes with it.
    public void RebuildSensors()
    {
        EnsureRoots();
        ClearSensors();
        RenderSensors();
        InvalidateSensorDay();
        BuildGhost();
    }

    // The only per-frame work this renderer does. Advance returns true at most once per simulated
    // minute, and while paused never, so a still scene costs one float compare a frame.
    private void Update()
    {
        if (!Occupancy.Advance(Time.deltaTime)) return;

        UpdateOccupantPoses();
        UpdateSensorStates();
    }

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

            Mark(go, ResidenceElementMarker.Kind.Wall, wall.id, null);
        }

        RenderOpeningHandles();
    }

    // Openings have no geometry of their own. They are the absence of wall. But they still need to be
    // findable, so each gets an invisible collider filling its void, registered in _byId by Mark.
    //
    // IT IS NO LONGER A SELECTION HANDLE. Clicking a doorway now selects the WALL that hosts it,
    // SelectTool reads marker.parentId and redirects, because an opening is not a thing you can point
    // at, and the wall's rail is where the list of its openings lives. The handle survives for three
    // things that still need it: ResidenceEditController.FocusElement resolves ids through _byId, so F and
    // CompareTool's opening rows would print "Nothing to focus on." without it; the redirect needs
    // SOMETHING under the cursor in the void, since PickElement returns the first marker along the ray
    // and stops; and FocusPoint's degenerate-bounds fallback was written for this renderer.
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
            // above it when both are under the cursor, which is what makes the redirect to its host
            // wall fire reliably rather than depending on which surface the ray happened to reach.
            box.size = new Vector3(frame.thickness + 0.02f,
                                   Mathf.Max(0.05f, top - sill),
                                   Mathf.Max(0.05f, opening.width));
            // See PickOnly: this box is exactly as wide as the doorway, so leaving it solid walls off
            // every room in the walkthrough.
            PickOnly(go);

            var mr = go.AddComponent<MeshRenderer>();
            mr.enabled = false;   // collider only: the void is the visual

            Mark(go, ResidenceElementMarker.Kind.Opening, opening.id, host.id);
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
                                 Mat(RoomFinish.FloorMaterial(room.roomType), InteriorMaterialPalette.Surface.Floor));
                Mark(go, ResidenceElementMarker.Kind.Floor, room.id, room.id);
            }

            Mesh ceiling = RoomMeshBuilder.BuildCeiling(room, _level);
            if (ceiling != null)
            {
                _ownedMeshes.Add(ceiling);
                var go = Surface($"Ceiling_{room.id}", _ceilingRoot, ceiling,
                                 Mat(null, InteriorMaterialPalette.Surface.Ceiling));
                Mark(go, ResidenceElementMarker.Kind.Ceiling, room.id, room.id);
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
            Mark(go, ResidenceElementMarker.Kind.Furniture, item.instanceId, null);

            // Spawn and re-pose go through the same call, so an item that a drag has resized looks
            // identical whether it was just rebuilt or is being dragged right now.
            PoseGO(go, item);
        }
    }

    /// <summary>
    /// Re-poses an already-spawned furniture GameObject from its def (position, yaw and size) 
    /// without respawning it.
    /// </summary>
    /// <remarks>
    /// This is what makes dragging an item affordable. The obvious alternative, mutating the def and
    /// calling <see cref="Rebuild"/>, destroys and recreates every GameObject in the residence each frame,
    /// and <see cref="BuildPlaceholderBox"/> does a <c>Shader.Find</c> and a <c>new Material</c> per
    /// item, so a drag over a furnished plan would allocate hundreds of materials a second. Worse, it
    /// would destroy the very object the transform gizmo is holding, mid-gesture.
    ///
    /// So a drag writes the def AND this, and only the release rebuilds: the same split
    /// EditController has used for site instances all along.
    /// </remarks>
    public void PoseFurnitureGO(ObjectInstance item)
    {
        if (item == null) return;
        var go = GetGO(item.instanceId);
        if (go != null) PoseGO(go, item);
    }

    private void PoseGO(GameObject go, ObjectInstance item)
    {
        go.transform.position = item.position != null && item.position.Length >= 3
            ? new Vector3(item.position[0], item.position[1], item.position[2])
            : Vector3.zero;
        go.transform.rotation = Quaternion.Euler(item.rotationX, item.rotationY, item.rotationZ);

        Vector3 size = ItemSize(item);

        // A placeholder is a cube in a floor-pivoted parent, so resizing it means re-scaling the box,
        // re-seating it half its own height above the floor, and lifting the label to clear the new
        // top. BuildPlaceholderBox names the child so this can find it without guessing.
        var box = go.transform.Find(BOX_CHILD);
        if (box != null)
        {
            box.localScale = new Vector3(Mathf.Max(0.02f, size.x),
                                         Mathf.Max(0.02f, size.y),
                                         Mathf.Max(0.02f, size.z));
            box.localPosition = new Vector3(0f, 0.5f * Mathf.Max(0.02f, size.y), 0f);

            var label = go.transform.Find(LABEL_CHILD);
            if (label != null) label.localPosition = new Vector3(0f, size.y + 0.12f, 0f);
            return;
        }

        // Real art with a baked fit: scale the ART child, never the root. Scaling the root would
        // stretch the label's glyphs and re-scale the pick box a second time on top of the size
        // FitCollider just gave it. Same three moves as the placeholder above, one level down.
        var fit = go.GetComponent<CatalogArtFit>();
        if (fit != null)
        {
            fit.Apply(size);
            FitCollider(go, size);

            var lab = go.transform.Find(LABEL_CHILD);
            if (lab != null) lab.localPosition = new Vector3(0f, size.y + 0.12f, 0f);
            return;
        }

        // Real art: scale it RELATIVE to the catalog size, so an item nobody resized renders exactly
        // as the prefab was authored (ratio 1) and a resized one grows by the same factor the numbers
        // did. Normalizing against the prefab's own bounds instead would silently re-size every model
        // on the first render, which is not this method's business.
        var entry = EntryFor(item.prefabType);
        if (entry == null) return;

        Vector3 nominal = entry.SizeMeters;
        if (nominal.x <= 0f || nominal.y <= 0f || nominal.z <= 0f) return;

        Vector3 authored = PrefabScale(item.prefabType);
        go.transform.localScale = new Vector3(authored.x * size.x / nominal.x,
                                              authored.y * size.y / nominal.y,
                                              authored.z * size.z / nominal.z);
    }

    private Vector3 PrefabScale(string key)
    {
        var prefab = FindPrefab(key);
        return prefab != null ? prefab.transform.localScale : Vector3.one;
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

            var entry = EntryFor(mount.prefabType);
            Vector3 size = entry != null ? entry.SizeMeters : new Vector3(0.4f, 0.05f, 0.05f);

            var go = SpawnCatalogItem(mount.prefabType, size, _mountRoot);
            if (go == null) continue;

            go.name = $"WallMount_{mount.instanceId}";
            Mark(go, ResidenceElementMarker.Kind.WallMount, mount.instanceId, host.id);

            // Spawn and re-pose go through one method, exactly as furniture does, so a mount a drag has
            // slid or re-hosted looks identical whether it was just rebuilt or is being dragged now.
            PoseMount(go, mount, size);
        }
    }

    /// <summary>
    /// Re-poses an already-spawned wall mount from its def, without respawning it.
    /// </summary>
    /// <remarks>
    /// The mount equivalent of <see cref="PoseFurnitureGO"/>, and it exists for the same reason:
    /// dragging one used to end in <c>Ctx.Changed()</c>, i.e. a full <see cref="Rebuild"/> per frame,
    /// which destroys and respawns every GameObject in the residence (including the one being dragged) 
    /// and re-runs a <c>Shader.Find</c> and a <c>new Material</c> per placeholder box.
    /// </remarks>
    public void PoseWallMountGO(WallMountDef mount)
    {
        if (mount == null) return;

        var go = GetGO(mount.instanceId);
        if (go == null) return;

        var entry = EntryFor(mount.prefabType);
        PoseMount(go, mount, entry != null ? entry.SizeMeters : new Vector3(0.4f, 0.05f, 0.05f));
    }

    private void PoseMount(GameObject go, WallMountDef mount, Vector3 size)
    {
        WallDef host = FindWall(mount.wallId);
        if (host == null) return;

        // side 0 = the wall's left face, pushed out by half the thickness plus the z-fight epsilon;
        // and mountHeight is the item's ANCHOR height (decorAnchor: Center / Bottom / Top) while
        // SpawnCatalogItem hands back a FLOOR-PIVOTED object, so the anchor is converted to the
        // bottom that pivot wants. Get that wrong and every mount hangs half its own height too
        // high: a wall cabinet anchored at 1.37 m would reach 2.13 m, which is the ceiling.
        //
        // Both live in MountPose, shared with the ghost overlay so the two cannot drift apart.
        MountPose(mount, size, host, _level, out Vector3 pos, out Quaternion rot);
        go.transform.position = pos;
        go.transform.rotation = rot;

        // A mount's size is always the catalog size and its wrapper is baked to exactly that, so this
        // is the identity today. It runs anyway because it costs one call and because the alternative
        // is the trap this method used to be: PoseMount touched position and rotation and never scale,
        // so art that did not already happen to be the right size rendered wrong with no warning.
        var fit = go.GetComponent<CatalogArtFit>();
        if (fit != null)
        {
            fit.Apply(size);
            FitCollider(go, size);
        }

        // A drag can re-host a mount onto a different wall, and the marker's parent has to follow it,
        // the marker is what a click resolves back to, and a stale parent points at the old wall.
        var marker = go.GetComponent<ResidenceElementMarker>();
        if (marker != null) marker.parentId = host.id;
    }

    // ---------------------------------------------------------------------------------------
    // Smart home devices
    // ---------------------------------------------------------------------------------------

    // Like wall mounts, a sensor derives its pose from the element it watches rather than storing one,
    // so widening a doorway carries its door sensor with it and moving a bed carries its pad. Unlike a
    // wall mount, the host may be an opening, a room, a piece of furniture or a person. SensorPose is
    // where that fans out, and it is shared with the ghost so the two cannot drift.
    //
    // A worn device gets NO GameObject at all. A pendant is on a resident, not in the plan, and drawing
    // it at their current position would make it slide around the residence every simulated minute while
    // pretending to be installed somewhere.
    private void RenderSensors()
    {
        if (_level?.sensors == null) return;

        foreach (var sensor in _level.sensors)
        {
            if (sensor == null || !sensor.included || string.IsNullOrEmpty(sensor.id)) continue;

            var pose = SensorPose.Resolve(sensor, _level, _variant);
            if (!pose.resolved) continue;

            var entry = sensorCatalog != null ? sensorCatalog.Get(sensor.deviceType) : null;
            var device = SensorDevices.Get(sensor.deviceType);
            Vector3 size = entry != null ? entry.SizeMeters
                                         : new Vector3(device.width, device.height, device.depth);

            var view = BuildSensorMarker(sensor, entry, size, pose);
            if (view == null) continue;

            Mark(view.go, ResidenceElementMarker.Kind.Sensor, sensor.id, sensor.hostId);
            _sensorViews.Add(view);
        }

        UpdateSensorStates();
    }

    /// <summary>
    /// One device: a small tinted box at its true size, labeled, pick-only.
    /// </summary>
    /// <remarks>
    /// Deliberately not routed through SpawnCatalogItem. That reads the FURNITURE catalog for its
    /// swatch and label, so every sensor would come out grey and named by its raw key, and it hands
    /// back a shared material, which is exactly what a per-device tint cannot use. These are 70 mm
    /// grey plastic boxes in life, so a labeled box at true size is very nearly a portrait.
    /// </remarks>
    private SensorView BuildSensorMarker(SensorDef sensor, SensorCatalog.Entry entry, Vector3 size,
                                         SensorPose.Pose pose)
    {
        var pivot = new GameObject($"Sensor_{sensor.id}");
        pivot.transform.SetParent(_sensorRoot, false);
        pivot.transform.position = pose.position;
        pivot.transform.rotation = Quaternion.Euler(0f, pose.yaw, 0f);

        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = BOX_CHILD;
        box.transform.SetParent(pivot.transform, false);
        box.transform.localScale = new Vector3(Mathf.Max(0.03f, size.x),
                                               Mathf.Max(0.03f, size.y),
                                               Mathf.Max(0.03f, size.z));
        // Pick-only, like every other non-shell collider here: a motion sensor that stopped you
        // walking down the corridor would be absurd, and PickElement's raycast still finds a trigger.
        PickOnly(box);

        var mr = box.GetComponent<MeshRenderer>();
        Color tint = entry != null ? entry.swatch : new Color(0.40f, 0.55f, 0.72f);

        // A material per device, tracked so it can be destroyed. BuildPlaceholderBox leaks one per
        // item per rebuild and its own header says so; there is no reason to repeat that here, and the
        // per-device tint means these cannot be shared anyway.
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = tint };
        if (mr != null) mr.sharedMaterial = mat;
        _sensorMaterials.Add(mat);

        AddLabel(pivot.transform, entry != null ? entry.Label : SensorDevices.LabelOf(sensor), size.y);

        return new SensorView
        {
            go = pivot, box = mr, material = mat, baseColor = tint, sensorId = sensor.id,
        };
    }

    /// <summary>
    /// Re-poses an already-spawned device from its def, without respawning it: the sensor form of
    /// PoseFurnitureGO, and there for the same reason: dragging one onto a different wall, or nudging
    /// its offset in the rail, must not destroy and rebuild every GameObject in the residence per frame.
    /// </summary>
    public void PoseSensorGO(SensorDef sensor)
    {
        if (sensor == null) return;

        var go = GetGO(sensor.id);
        if (go == null) return;

        var pose = SensorPose.Resolve(sensor, _level, _variant);
        if (!pose.resolved) return;

        go.transform.position = pose.position;
        go.transform.rotation = Quaternion.Euler(0f, pose.yaw, 0f);

        // Re-hosting changes what the marker hangs off, and a stale parentId points the inspector at
        // the wall this device used to be on. Same trap PoseMount closes.
        var marker = go.GetComponent<ResidenceElementMarker>();
        if (marker != null) marker.parentId = sensor.hostId;
    }

    /// <summary>
    /// Re-tints every device to what it is doing at the clock's current minute. Called from Update
    /// beside UpdateOccupantPoses, and once at spawn.
    /// </summary>
    /// <remarks>
    /// THIS IS WHAT MAKES PLAYING THE DAY LEGIBLE IN THE PLAN rather than only on the timeline. The
    /// timeline says an alert lands at 03:20; the plan says WHERE, which is the question a care team
    /// is actually looking at the plan to answer.
    ///
    /// A colour write on a cached material, guarded by a state compare: no teardown, no respawn, and
    /// nothing at all while the state has not changed.
    /// </remarks>
    public void UpdateSensorStates()
    {
        if (_sensorViews.Count == 0) return;

        // Forced idle during a report capture: a device lit red in the "after" photograph is the clock
        // having moved, not the proposal, and a reader will try to read it as part of the proposal.
        // Same reasoning that hides the occupants and the ghost for the duration.
        var day = _sensorStatesLive ? SensorDay : default;
        int minute = Occupancy.Now;

        foreach (var view in _sensorViews)
        {
            var state = _sensorStatesLive
                ? SensorSim.StateAt(day, view.sensorId, minute)
                : SensorSim.State.Idle;

            if (state == view.lastState) continue;
            view.lastState = state;

            if (view.material == null) continue;
            view.material.color = state switch
            {
                SensorSim.State.Alerting => AlertingTint,
                SensorSim.State.Active => ActiveTint,
                _ => view.baseColor,
            };
        }
    }

    // UITheme's Danger and Accent, as scene colours. Named rather than inlined because the console and
    // the timeline paint the same three states and all three have to agree.
    private static readonly Color AlertingTint = new Color(0.70f, 0.15f, 0.12f);
    private static readonly Color ActiveTint = new Color(0.18f, 0.39f, 0.78f);

    private bool _sensorStatesLive = true;

    /// <summary>
    /// Freezes every device at idle, for a report capture. Returns the previous setting so the caller
    /// can restore it: a read must not leave the editor somewhere else.
    /// </summary>
    public bool SetSensorStatesLive(bool live)
    {
        bool was = _sensorStatesLive;
        _sensorStatesLive = live;
        UpdateSensorStates();
        return was;
    }

    private readonly List<Material> _sensorMaterials = new List<Material>();

    private void ClearSensors()
    {
        _sensorViews.Clear();
        ClearGroup(_sensorRoot);

        foreach (var mat in _sensorMaterials) if (mat != null) Destroy(mat);
        _sensorMaterials.Clear();
    }

    // ---------------------------------------------------------------------------------------
    // Occupants
    // ---------------------------------------------------------------------------------------

    // One marker per person. Deliberately low fidelity: a capsule the height of a person, tinted, with
    // their name and what they are doing floating over it. There is no walking and no animation: an
    // occupant is wherever their schedule says at the current minute, and the value is in seeing five
    // of them want one bathroom, not in watching anyone traverse a corridor.
    private void RenderOccupants()
    {
        _occupantViews.Clear();
        _occupantOrder.Clear();
        if (_variant?.occupants == null || _level == null) return;

        int index = 0;
        foreach (var person in _variant.occupants)
        {
            if (person == null || string.IsNullOrEmpty(person.id)) continue;
            if (!person.included) { index++; continue; }

            var view = BuildOccupantMarker(person, index++);
            if (view == null) continue;

            Mark(view.go, ResidenceElementMarker.Kind.Occupant, person.id, null);
            _occupantViews.Add(view);
            _occupantOrder.Add(person);
        }

        // Poses are not part of the build: they depend on the clock, which moves without a rebuild.
        Occupancy.Invalidate();
        // The free-floor search is memoised against this level's geometry, which has just changed.
        OccupancyModel.InvalidateCache();
        UpdateOccupantPoses();
    }

    private OccupantView BuildOccupantMarker(OccupantDef person, int index)
    {
        bool seated = person.usesWheelchair;

        // A seated marker tops out at the seated eye height, which is the number the walkthrough view's
        // Seated toggle already uses, so the two views agree about how tall a wheelchair user is.
        float height = seated ? ResidenceConventions.EYE_HEIGHT_SEATED : 1.70f;
        float width = 0.45f;

        var pivot = new GameObject("Person");
        pivot.transform.SetParent(_occupantRoot, false);

        float[] rgb = OccupantPalette.For(person, index);
        var tint = new Color(rgb[0], rgb[1], rgb[2]);

        // The wheelchair's own footprint from the catalog, so a seated marker reads as taking a
        // wheelchair's worth of floor rather than a person's.
        if (seated)
        {
            var pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pad.name = "Chair";
            pad.transform.SetParent(pivot.transform, false);
            pad.transform.localScale = new Vector3(0.66f, 0.10f, 1.22f);
            pad.transform.localPosition = new Vector3(0f, 0.05f, 0f);
            Paint(pad, tint * 0.55f);
            // Its collider stays: both pieces resolve to the same marker on the pivot, and a seated
            // person is easier to hit in plan view by their footprint than by a short capsule.
        }

        // A capsule primitive is 2 m tall with its origin at the center, so the scale is the height
        // over two and the lift is half the height: the same floor-pivot convention as the furniture
        // placeholder boxes.
        float torso = seated ? height - 0.35f : height;
        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        body.transform.SetParent(pivot.transform, false);
        body.transform.localScale = new Vector3(width, 0.5f * torso, width);
        body.transform.localPosition = new Vector3(0f, (seated ? 0.35f : 0f) + 0.5f * torso, 0f);
        Paint(body, tint);

        // Both primitives keep their colliders for picking, but pick-only. Walking the plan should
        // not mean shouldering past the residents. See PickOnly.
        PickOnly(pivot);

        var view = new OccupantView { go = pivot };

        AddLabel(pivot.transform, person.name, height);
        var labelGO = pivot.transform.Find("Label");
        if (labelGO != null) view.label = labelGO.GetComponent<TextMesh>();

        return view;
    }

    private void Paint(GameObject go, Color color)
    {
        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) return;
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.color = color;
        mr.sharedMaterial = mat;
    }

    /// <summary>
    /// Moves every occupant marker to where the clock says they are. Transform writes only: no
    /// teardown, no allocation once the labels have settled, because this runs every simulated minute
    /// and playback would otherwise rebuild the whole house sixty times a second.
    /// </summary>
    public void UpdateOccupantPoses()
    {
        if (_occupantViews.Count == 0) return;

        var poses = OccupancyModel.PoseAll(_variant, _level, Occupancy.Now);
        float y = _level?.elevation ?? 0f;

        for (int i = 0; i < _occupantViews.Count; i++)
        {
            var view = _occupantViews[i];
            var person = _occupantOrder[i];
            if (view?.go == null || person == null) continue;

            if (!poses.TryGetValue(person.id, out var pose) || !pose.present)
            {
                // Out of the house, or scheduled into a room this variant no longer has. Hidden rather
                // than parked at the origin, which would put a stranger in the front garden.
                if (view.go.activeSelf) view.go.SetActive(false);
                continue;
            }

            if (!view.go.activeSelf) view.go.SetActive(true);
            view.go.transform.position = new Vector3(pose.xz.x, y, pose.xz.y);
            view.go.transform.rotation = Quaternion.Euler(0f, pose.yaw, 0f);

            if (view.label == null) continue;
            string text = person.name + "\n" + OccupancyModel.Describe(pose);
            if (text == view.lastText) continue;
            view.lastText = text;
            view.label.text = text;
        }
    }

    /// <summary>Where each occupant is right now, for the rail and the People view.</summary>
    public Dictionary<string, OccupancyModel.Pose> CurrentPoses()
        => OccupancyModel.PoseAll(_variant, _level, Occupancy.Now);

    private void ClearOccupants()
    {
        _occupantViews.Clear();
        _occupantOrder.Clear();
        ClearGroup(_occupantRoot);
    }

    /// <summary>
    /// Resolves a catalog key to a GameObject: the real prefab when PrefabRegistry has art under that
    /// key, otherwise a correctly sized labeled box. This one branch is what lets the tool be useful
    /// with no interior art at all, and lets art appear later item by item with no other change.
    /// </summary>
    /// <remarks>
    /// Real art comes back in the SAME outward shape a placeholder has: a floor-pivoted, unscaled
    /// root carrying the pick collider and the floating name, with everything that gets stretched
    /// underneath it. That is what lets Mark, GetGO, PickElement, PoseGO and the transform gizmo see
    /// one kind of object rather than two.
    /// </remarks>
    private GameObject SpawnCatalogItem(string key, Vector3 sizeMeters, Transform parent)
    {
        GameObject prefab = FindPrefab(key);
        if (prefab == null) return BuildPlaceholderBox(key, sizeMeters, parent);

        var go = Instantiate(prefab, parent);

        // A wrapper prefab from CatalogArtBinder: the fit is baked, so the stretch lands on the Art
        // child and the root stays at scale 1 for the collider and the label.
        var fit = go.GetComponent<CatalogArtFit>();
        if (fit != null)
        {
            fit.Apply(sizeMeters);
            FitCollider(go, sizeMeters);

            var entry = EntryFor(key);
            AddLabel(go.transform, LabelFor(entry, key), sizeMeters.y);
            return go;
        }

        // A raw prefab registered under a catalog key by hand. Left exactly as it always behaved:
        // PoseGO scales its root against the catalog size, and it gets no label.
        if (go.GetComponent<Collider>() == null) AddFittedCollider(go, sizeMeters);
        else PickOnly(go);   // real art brings its own colliders; they are still pick-only here
        return go;
    }

    /// <summary>
    /// The prefab registered under <paramref name="key"/>, or null when there is no art for it.
    /// </summary>
    /// <remarks>
    /// PrefabRegistry.GetPrefab dereferences both `entries` and each entry's `key` without guarding,
    /// so an unpopulated registry or a blank row would throw. Screen for that here rather than
    /// touching the shared Site file.
    /// </remarks>
    public GameObject FindPrefab(string key)
    {
        if (prefabRegistry?.entries == null || string.IsNullOrEmpty(key)) return null;

        foreach (var e in prefabRegistry.entries)
        {
            if (e == null || string.IsNullOrEmpty(e.key)) continue;
            if (string.Equals(e.key, key, System.StringComparison.OrdinalIgnoreCase)) return e.prefab;
        }
        return null;
    }

    // PoseGO finds these by name to tell a placeholder from real art and to re-seat the box when an
    // item is resized. Renaming either one silently breaks resize, so they are named once here.
    private const string BOX_CHILD   = "Box";
    private const string LABEL_CHILD = "Label";

    // The scaled child inside a catalog wrapper prefab. PoseGO reaches it through CatalogArtFit.art
    // rather than by name (the fit needs the baked pivot offset too, which a name cannot carry) so
    // this exists for CatalogArtBinder to build against and for anyone reading a wrapper's hierarchy.
    public const string ART_CHILD = "Art";

    private GameObject BuildPlaceholderBox(string key, Vector3 size, Transform parent)
    {
        var entry = EntryFor(key);

        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = BOX_CHILD;
        go.transform.SetParent(parent, false);
        go.transform.localScale = new Vector3(
            Mathf.Max(0.02f, size.x), Mathf.Max(0.02f, size.y), Mathf.Max(0.02f, size.z));
        PickOnly(go);   // the primitive's own BoxCollider. Clickable, not walkable-into

        // The cube primitive is centered on its origin; furniture sits ON the floor, so lift it by
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

        AddLabel(pivot.transform, LabelFor(entry, key), size.y);
        return pivot;
    }

    /// <summary>What a box calls itself when there is no entry to ask.</summary>
    /// <remarks>
    /// Deleting a custom item leaves everything already placed exactly where it stands, so this is
    /// the case that decides what those objects are called from then on. `CustomItems.NameFromId`
    /// recovers "Reading chair" from "custom:reading_chair", which is the entire reason the id
    /// carries the name rather than a guid. Anything else falls back to the raw key, which for a
    /// catalog item is already a readable word.
    /// </remarks>
    private static string LabelFor(FurnitureCatalog.Entry entry, string key)
    {
        if (entry != null) return entry.Label;
        if (CustomItems.IsCustom(key))
        {
            string name = CustomItems.NameFromId(key);
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return key;
    }

    // A floating name over each placeholder. Without it a room of grey boxes is unreadable: the label
    // is what makes "bed / wheelchair / toilet" legible before any art exists.
    //
    // Light text with a dark stroke (LabelOutline), not ink. The label floats over whatever happens to
    // be behind it (wall grey, a floor finish, the dark ground pad, a catalog swatch) and the near-
    // black it used to be disappeared against the most common of those, the wall directly behind the
    // furniture it names.
    private void AddLabel(Transform parent, string text, float aboveHeight)
    {
        if (string.IsNullOrEmpty(text)) return;

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) return;   // font name changed across Unity versions; skip rather than throw

        var go = new GameObject(LABEL_CHILD);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, aboveHeight + 0.12f, 0f);

        var tm = go.AddComponent<TextMesh>();
        tm.text = text;
        tm.font = font;
        tm.fontSize = 48;
        tm.characterSize = 0.02f;
        tm.anchor = TextAnchor.LowerCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = new Color(0.98f, 0.98f, 0.96f);
        go.GetComponent<MeshRenderer>().sharedMaterial = font.material;

        // Without this the label is edge-on from directly above, i.e. invisible whenever the overview
        // is looking down at the plan.
        go.AddComponent<LabelBillboard>();
        // …and without this it is white-on-white the moment it drifts over a light wall.
        go.AddComponent<LabelOutline>();
    }

    /// <summary>
    /// Sizes the pick box on an item's root to its real dimensions, adding one if it has none.
    /// </summary>
    /// <remarks>
    /// Called at spawn AND from every re-pose, which is the whole point. AddFittedCollider ran once,
    /// at spawn, and PoseGO then wrote a scale onto the very transform the collider was sitting on,
    /// so a resized item with real art had its pick box multiplied a second time, and a bed widened a
    /// fifth got a pick box widened by 44%. A wrapper's root is unscaled, so the size written here is
    /// in meters and stays in meters; re-running it on re-pose is what keeps it honest.
    /// </remarks>
    private static void FitCollider(GameObject go, Vector3 size)
    {
        var box = go.GetComponent<BoxCollider>();
        if (box == null) box = go.AddComponent<BoxCollider>();

        box.size   = new Vector3(Mathf.Max(0.02f, size.x),
                                 Mathf.Max(0.02f, size.y),
                                 Mathf.Max(0.02f, size.z));
        box.center = new Vector3(0f, 0.5f * Mathf.Max(0.02f, size.y), 0f);
        PickOnly(go);
    }

    // The raw-prefab path's collider, unchanged in behaviour.
    private void AddFittedCollider(GameObject go, Vector3 size) => FitCollider(go, size);

    /// <summary>
    /// Marks a subtree's colliders as existing for PICKING only. Clickable, but not something the
    /// walkthrough body can walk into.
    ///
    /// Most colliders in a ResidenceViz scene are not physics at all. An opening has no geometry, furniture
    /// renders as a labeled massing box, an occupant is a tinted capsule; each carries a collider
    /// purely so ResidenceToolContext.PickElement's raycast can find it. Left solid, they are what makes the
    /// walkthrough unusable: the opening handle fills the doorway exactly, so every door is a wall.
    ///
    /// A trigger is the whole fix: CharacterController ignores triggers, and Physics.RaycastAll still
    /// hits them (m_QueriesHitTriggers is 1 in DynamicsManager, and PickElement is the only physics
    /// query in ResidenceViz). What stays solid is the shell (wall meshes, floors, ceilings) which is the
    /// only thing that should ever stop you.
    /// </summary>
    private static void PickOnly(GameObject go)
    {
        foreach (var col in go.GetComponentsInChildren<Collider>(true))
            col.isTrigger = true;
    }

    // ---------------------------------------------------------------------------------------
    // View state
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Ceilings hide in plan and overview views and show in walkthrough. Kept as separate meshes
    /// from floors precisely so this is a SetActive rather than a geometry rebuild.
    /// </summary>
    public void SetCeilingsVisible(bool visible)
    {
        _ceilingsVisible = visible;
        if (_ceilingRoot != null) _ceilingRoot.gameObject.SetActive(visible);
    }

    /// <summary>
    /// Hides the occupant markers. Same shape as <see cref="SetCeilingsVisible"/> and for the same
    /// reason the markers have their own root: it is a SetActive, not a rebuild.
    /// </summary>
    /// <remarks>
    /// Added for the before/after report, which photographs architecture. A capsule standing in the
    /// bathroom in the "before" shot and beside the bed in the "after" one is a difference the reader
    /// will try to read as part of the proposal, and it is not. It is the clock having moved.
    /// </remarks>
    public void SetOccupantsVisible(bool visible)
    {
        _occupantsVisible = visible;
        if (_occupantRoot != null) _occupantRoot.gameObject.SetActive(visible);
    }

    private bool _occupantsVisible = true;

    public bool OccupantsVisible => _occupantsVisible;

    /// <summary>
    /// Overlays another variant for Compare: HOW IT WAS in red, drawn from the other variant's
    /// geometry, and HOW IT IS NOW in green, drawn from this one's. Turning it on is sticky.
    /// </summary>
    /// <remarks>
    /// The diff is against the variant being RENDERED, not against anything the caller names as the
    /// far end, which is why CompareTool switches the view to its After before turning this on.
    /// Passing a variantId equal to the rendered one leaves nothing to draw and is a no-op.
    ///
    /// STICKY IS THE POINT, and it is why this is two fields plus a builder rather than one method.
    /// The first version built into _ghostRoot and stopped, but ClearRendered clears that group and
    /// every Rebuild calls ClearRendered, so any edit, any undo and any variant switch silently
    /// dropped the overlay. That is what forced the UI to offer "Show ghost" and "Hide ghost" as two
    /// separate buttons: a toggle would have gone on lying about its own state within one click. The
    /// state lives here now and <see cref="Rebuild"/> re-applies it, so the UI is one chip.
    ///
    /// The green half is ghosted from the CURRENT level rather than by tinting the live GameObjects.
    /// Tinting would mean holding every touched renderer's original materials and restoring them,
    /// and getting that wrong leaves a residence permanently green. Building both halves into _ghostRoot
    /// keeps ClearGroup(_ghostRoot) the one and only teardown.
    ///
    /// Openings are deliberately absent, and the KNOWN COST of that is worth stating plainly: an
    /// opening has no geometry of its own (it is a gap its host wall's box list skips) but the host
    /// wall's own fields do not change when a door is widened, so VariantDiff reports no wall change
    /// either and the overlay stays empty. A proposal that only widens doorways and drops thresholds
    /// therefore draws nothing here. The change list, the markers and the report all still carry it.
    /// </remarks>
    public void SetGhostVariant(string variantId, bool on)
    {
        _ghostVariantId = variantId;
        _ghostOn = on;
        BuildGhost();
    }

    /// <summary>Which variant is ghosted, and whether it is showing. Survives a rebuild.</summary>
    private string _ghostVariantId;
    private bool _ghostOn;

    public bool GhostOn => _ghostOn;

    private void BuildGhost()
    {
        EnsureRoots();
        ClearGroup(_ghostRoot);
        ReleaseGhostMeshes();
        if (!_ghostOn || _doc == null || _variant == null || _level == null) return;

        var other = ResidenceStore.FindVariant(_doc, _ghostVariantId);
        if (other == null || other == _variant) return;

        var otherLevel = other.levels != null && other.levels.Count > 0
            ? other.levels[Mathf.Clamp(_levelIndex, 0, other.levels.Count - 1)]
            : null;
        if (otherLevel == null) return;

        // A MODIFIED element belongs to both halves: the other variant holds where it was, this one
        // holds where it is now, and leaving it out is what made this overlay draw nothing at all on
        // any realistic proposal. NewProposalFrom preserves every element id on purpose, precisely so
        // that moving a wall or a dresser reports as a modification rather than a delete plus an add;
        // Added and Removed alone therefore described almost none of what a proposal actually does.
        var before = new HashSet<string>();
        var after = new HashSet<string>();
        foreach (var c in VariantDiff.Compare(other, _variant))
        {
            if (c.type == VariantDiff.ChangeType.Added) after.Add(c.id);
            else if (c.type == VariantDiff.ChangeType.Removed) before.Add(c.id);
            else if (c.type == VariantDiff.ChangeType.Modified && GeometryDiffers(c.id, otherLevel, _level))
            {
                before.Add(c.id);
                after.Add(c.id);
            }
        }

        GhostLevel(otherLevel, before, added: false);   // how it was
        GhostLevel(_level, after, added: true);         // how it is now
    }

    /// <summary>
    /// Whether a MODIFIED element moved, grew or changed shape. i.e. whether ghosting it would show
    /// anything at all.
    /// </summary>
    /// <remarks>
    /// A rename, a new floor finish or a note is a modification with identical geometry, and its
    /// before-ghost lands exactly on its after-ghost: two coincident translucent copies of one floor,
    /// stacked, saying nothing. The rail still reports the change; there is simply nothing to draw.
    ///
    /// An element whose id belongs to none of the four kinds this overlay builds: an occupant, the
    /// exterior layer's literal "exterior" id. Falls through to true and is dropped by GhostLevel's
    /// own id filter a moment later.
    /// </remarks>
    private static bool GeometryDiffers(string id, LevelDef a, LevelDef b)
    {
        WallDef wa = FindWall(id, a), wb = FindWall(id, b);
        if (wa != null && wb != null)
            return !Same(wa.a, wb.a) || !Same(wa.b, wb.b)
                   || !Near(wa.thickness, wb.thickness) || !Near(wa.height, wb.height);

        RoomDef ra = FindRoom(id, a), rb = FindRoom(id, b);
        if (ra != null && rb != null) return !Same(ra.polygon, rb.polygon);

        ObjectInstance fa = FindItem(id, a), fb = FindItem(id, b);
        if (fa != null && fb != null)
            return !Same(fa.position, fb.position) || !Near(fa.rotationY, fb.rotationY)
                   || !Same(fa.boxSizeMeters, fb.boxSizeMeters);

        WallMountDef ma = FindMount(id, a), mb = FindMount(id, b);
        if (ma != null && mb != null)
            return ma.wallId != mb.wallId || !Near(ma.offset, mb.offset)
                   || ma.side != mb.side || !Near(ma.mountHeight, mb.mountHeight);

        SensorDef sa = FindSensor(id, a), sb = FindSensor(id, b);
        if (sa != null && sb != null)
            // Only what the ghost box actually draws from. Re-aiming a cone or changing a threshold is
            // a real change the rail reports and there is nothing to see here: the box is in the same
            // place, so ghosting it would stack a red copy on a green one.
            return sa.hostId != sb.hostId || sa.hostKind != sb.hostKind
                   || !Near(sa.hostOffset, sb.hostOffset) || sa.hostSide != sb.hostSide
                   || !Near(sa.mountHeight, sb.mountHeight) || !Same(sa.position, sb.position);

        return true;
    }

    private void GhostLevel(LevelDef level, HashSet<string> ids, bool added)
    {
        if (ids.Count == 0) return;

        foreach (var wall in level.walls ?? new List<WallDef>())
        {
            if (wall == null || !ids.Contains(wall.id)) continue;
            Mesh mesh = WallMeshBuilder.Build(wall, level);
            if (mesh == null) continue;
            _ghostMeshes.Add(mesh);
            var frame = WallMeshBuilder.BuildFrame(wall, level);
            GhostMesh($"Ghost_{wall.id}", mesh, frame.origin, Quaternion.identity, added, 3);
        }

        foreach (var room in level.rooms ?? new List<RoomDef>())
        {
            if (room == null || !ids.Contains(room.id)) continue;
            Mesh mesh = RoomMeshBuilder.BuildFloor(room, level);
            if (mesh == null) continue;
            _ghostMeshes.Add(mesh);
            // Lifted a millimetre so it does not z-fight the real floor underneath it.
            GhostMesh($"Ghost_{room.id}", mesh, new Vector3(0f, 0.001f, 0f), Quaternion.identity, added, 1);
        }

        foreach (var item in level.furniture ?? new List<ObjectInstance>())
        {
            if (item == null || !ids.Contains(item.instanceId)) continue;
            Vector3 size = ItemSize(item);
            Vector3 pos = item.position != null && item.position.Length >= 3
                ? new Vector3(item.position[0], item.position[1], item.position[2])
                : Vector3.zero;
            GhostBox($"Ghost_{item.instanceId}", size,
                     pos + Vector3.up * (0.5f * size.y), Quaternion.Euler(0f, item.rotationY, 0f), added);
        }

        foreach (var mount in level.wallMounted ?? new List<WallMountDef>())
        {
            if (mount == null || !ids.Contains(mount.instanceId)) continue;
            var host = FindWall(mount.wallId, level);
            if (host == null) continue;

            var entry = EntryFor(mount.prefabType);
            Vector3 size = entry != null ? entry.SizeMeters : new Vector3(0.4f, 0.05f, 0.05f);
            MountPose(mount, size, host, level, out Vector3 pos, out Quaternion rot);
            GhostBox($"Ghost_{mount.instanceId}", size, pos + Vector3.up * (0.5f * size.y), rot, added);
        }

        // Sensors, from the level that HAS them, which is the whole reason SensorPose takes an
        // explicit level rather than reading the renderer's current one. A device the proposal removed
        // has to be placed from the variant that still holds the wall or the bed it was on.
        //
        // A worn device draws nothing: SensorPose returns an unresolved pose for it, and a pendant
        // has no place in the plan to ghost. The change list still reports it.
        foreach (var sensor in level.sensors ?? new List<SensorDef>())
        {
            if (sensor == null || !ids.Contains(sensor.id)) continue;

            var pose = SensorPose.Resolve(sensor, level, added ? _variant : null);
            if (!pose.resolved) continue;

            var device = SensorDevices.Get(sensor.deviceType);
            var size = new Vector3(device.width, device.height, device.depth);
            GhostBox($"Ghost_{sensor.id}", size, pose.position, Quaternion.Euler(0f, pose.yaw, 0f), added);
        }
    }

    private void GhostBox(string name, Vector3 size, Vector3 center, Quaternion rot, bool added)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        // A ghost is a picture of something that is not there. It must never be walked into, and it
        // must never win a selection raycast in front of the real element it is describing.
        var col = go.GetComponent<Collider>();
        if (col != null) { if (Application.isPlaying) Destroy(col); else DestroyImmediate(col); }

        go.transform.SetParent(_ghostRoot, false);
        go.transform.position = center;
        go.transform.rotation = rot;
        go.transform.localScale = new Vector3(Mathf.Max(0.02f, size.x), Mathf.Max(0.02f, size.y),
                                              Mathf.Max(0.02f, size.z));
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null) mr.sharedMaterial = GhostMaterial(added);
    }

    private void GhostMesh(string name, Mesh mesh, Vector3 pos, Quaternion rot, bool added, int subMeshes)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_ghostRoot, false);
        go.transform.position = pos;
        go.transform.rotation = rot;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;

        var mat = GhostMaterial(added);
        var mats = new Material[Mathf.Max(1, subMeshes)];
        for (int i = 0; i < mats.Length; i++) mats[i] = mat;
        go.AddComponent<MeshRenderer>().sharedMaterials = mats;
    }

    /// <summary>
    /// The overlay is drawn with exactly two materials, one per tint, shared by every ghost in it.
    /// </summary>
    /// <remarks>
    /// This used to be a <c>new Material</c> per element, destroyed by nothing, so every rebuild
    /// while the overlay was on leaked one per ghosted wall, room and item.
    ///
    /// It also used to be OPAQUE, which is the second reason the overlay did not read as one. URP/Lit
    /// defaults to an opaque surface, so <c>mat.color = tint</c> at alpha 0.35 rendered a solid box:
    /// the green half buried the live element it was meant to tint, and the red half read as real
    /// geometry rather than as a picture of something that is not there. Transparency in URP is a
    /// surface-type switch plus the blend state, exactly the configuration UnderlayTool and
    /// TileBuildingEditor already write out by hand in this project.
    /// </remarks>
    private Material GhostMaterial(bool added)
    {
        var cached = added ? _ghostMatAdded : _ghostMatRemoved;
        if (cached != null) return cached;

        // A material wired in the inspector is the author's, and is taken as authored: only tinted.
        var mat = ghostMaterial != null ? new Material(ghostMaterial) : TranslucentMaterial();
        mat.name = added ? "GhostAdded" : "GhostRemoved";

        var tint = added ? ghostAdded : ghostRemoved;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);

        if (added) _ghostMatAdded = mat; else _ghostMatRemoved = mat;
        return mat;
    }

    // ZWrite off matters as much as the blend does: an added ghost is the same mesh at the same
    // transform as the element it describes, so with depth writes on the two fight for the pixel.
    // Off, in the transparent queue, the ghost simply draws after the opaque pass and reads as a tint.
    private static Material TranslucentMaterial()
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Sprites/Default");
        var mat = new Material(shader);
        mat.SetOverrideTag("RenderType", "Transparent");
        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);   // URP: 0 opaque, 1 transparent
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        return mat;
    }

    private Material _ghostMatAdded, _ghostMatRemoved;

    // The pose half of PoseMount, over an explicit level so a ghost of a REMOVED mount can be placed
    // from the variant that still has its wall. PoseMount delegates here rather than duplicating it,
    // which is what keeps a ghost and its real counterpart in exactly the same place.
    private static void MountPose(WallMountDef mount, Vector3 size, WallDef host, LevelDef level,
                                  out Vector3 position, out Quaternion rotation)
    {
        var frame = WallMeshBuilder.BuildFrame(host, level);
        Vector3 outward = mount.side == WallSide.Left ? frame.left : -frame.left;
        float push = 0.5f * frame.thickness + Mathf.Max(0.001f, mount.decorSurfaceOffset);

        float bottom = mount.decorAnchor == (int)DecorAlignment.Anchor.Bottom ? mount.mountHeight
                     : mount.decorAnchor == (int)DecorAlignment.Anchor.Top    ? mount.mountHeight - size.y
                     :                                                          mount.mountHeight - 0.5f * size.y;

        position = frame.origin + frame.forward * mount.offset + Vector3.up * bottom + outward * push;
        rotation = Quaternion.LookRotation(outward, Vector3.up);
    }

    // ---------------------------------------------------------------------------------------
    // Optional exterior layer
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the outdoor layer, or takes it down. The whole feature is a nullable SiteDef plus
    /// ExteriorBridge. WorldRenderer already knows how to draw paths, fences, surface strokes and
    /// outdoor objects, so an entry ramp costs no new geometry code.
    ///
    /// Nothing appears until the residence has BOTH opted in and had something drawn: an enabled but empty
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

    public WallDef FindWall(string wallId) => FindWall(wallId, _level);

    // Level-explicit form, so the ghost overlay can resolve a host wall in the OTHER variant: the
    // wall a removed grab bar was mounted on does not exist in the level being rendered.
    private static WallDef FindWall(string wallId, LevelDef level)
    {
        if (level?.walls == null || string.IsNullOrEmpty(wallId)) return null;
        foreach (var w in level.walls) if (w != null && w.id == wallId) return w;
        return null;
    }

    // The other three, in the same form and for the same reason: GeometryDiffers has to look one id up
    // in TWO levels, only one of which is the one being rendered.
    private static RoomDef FindRoom(string roomId, LevelDef level)
    {
        if (level?.rooms == null || string.IsNullOrEmpty(roomId)) return null;
        foreach (var r in level.rooms) if (r != null && r.id == roomId) return r;
        return null;
    }

    private static ObjectInstance FindItem(string itemId, LevelDef level)
    {
        if (level?.furniture == null || string.IsNullOrEmpty(itemId)) return null;
        foreach (var f in level.furniture) if (f != null && f.instanceId == itemId) return f;
        return null;
    }

    private static WallMountDef FindMount(string mountId, LevelDef level)
    {
        if (level?.wallMounted == null || string.IsNullOrEmpty(mountId)) return null;
        foreach (var m in level.wallMounted) if (m != null && m.instanceId == mountId) return m;
        return null;
    }

    public static SensorDef FindSensor(string sensorId, LevelDef level)
    {
        if (level?.sensors == null || string.IsNullOrEmpty(sensorId)) return null;
        foreach (var s in level.sensors) if (s != null && s.id == sensorId) return s;
        return null;
    }

    public SensorDef FindSensor(string sensorId) => FindSensor(sensorId, _level);

    // A tenth of a millimetre: below anything this app can express and far below anything it can draw.
    private static bool Near(float x, float y) => Mathf.Abs(x - y) < 1e-4f;

    private static bool Same(float[] x, float[] y)
    {
        if (x == null || y == null) return x == null && y == null;
        if (x.Length != y.Length) return false;
        for (int i = 0; i < x.Length; i++) if (!Near(x[i], y[i])) return false;
        return true;
    }

    private static bool Same(float[][] x, float[][] y)
    {
        if (x == null || y == null) return x == null && y == null;
        if (x.Length != y.Length) return false;
        for (int i = 0; i < x.Length; i++) if (!Same(x[i], y[i])) return false;
        return true;
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

        var entry = EntryFor(item?.prefabType);
        if (entry != null) return entry.SizeMeters;

        return new Vector3(0.6f, 0.8f, 0.6f);
    }

    private void Mark(GameObject go, ResidenceElementMarker.Kind kind, string id, string parentId)
    {
        var marker = go.AddComponent<ResidenceElementMarker>();
        marker.kind = kind;
        marker.id = id;
        marker.parentId = parentId;
        if (!string.IsNullOrEmpty(id)) _byId[id] = go;
    }

    private void EnsureRoots()
    {
        if (_root != null) return;

        _root = new GameObject("ResidenceRender").transform;
        _root.SetParent(transform, false);

        _wallRoot = Group("Walls");
        _floorRoot = Group("Floors");
        _ceilingRoot = Group("Ceilings");
        _furnitureRoot = Group("Furniture");
        _mountRoot = Group("WallMounted");
        _sensorRoot = Group("Sensors");
        _occupantRoot = Group("Occupants");
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
        ClearSensors();
        ClearOccupants();
        ClearGroup(_ghostRoot);
        ReleaseMeshes();
        ReleaseGhostMeshes();
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

    private void ReleaseMeshes() => Release(_ownedMeshes);

    private void ReleaseGhostMeshes() => Release(_ghostMeshes);

    private void Release(List<Mesh> meshes)
    {
        foreach (var m in meshes)
        {
            if (m == null) continue;
            if (Application.isPlaying) Destroy(m); else DestroyImmediate(m);
        }
        meshes.Clear();
    }

    private void OnDestroy()
    {
        ReleaseMeshes();
        ReleaseGhostMeshes();
        DestroyObj(_ghostMatAdded);
        DestroyObj(_ghostMatRemoved);
        _ghostMatAdded = _ghostMatRemoved = null;
    }

    private static void DestroyObj(UnityEngine.Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
    }
}
