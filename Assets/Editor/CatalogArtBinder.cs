using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Binds the furniture packs in this project to <see cref="FurnitureCatalog"/> ids, by generating one
/// wrapper prefab per bound id and registering it in ResidenceCatalogRegistry.
/// </summary>
/// <remarks>
/// <para><b>The table below is the source of truth, not the prefabs.</b> A wrapper under
/// Assets/Prefabs/ResidenceViz/Catalog/ is a derived artifact and <see cref="GenerateWrappers"/> overwrites
/// it, so a hand edit inside one is lost on the next run. Corrections belong in <see cref="Rows"/>;
/// the escape hatch for a genuine one-off is <see cref="CatalogArtFit.handTuned"/>, which makes the
/// generator refresh only the registry row and leave the prefab alone.</para>
///
/// <para><b>Ids with no plausible donor stay placeholders, deliberately.</b> They are listed at the
/// bottom of the table with the reason so the decision does not get re-litigated. ResidenceRenderer already
/// draws a correctly sized labeled box for anything the registry does not know, so "no art" is a
/// supported state rather than a gap.</para>
///
/// <para><b>Aspect is the selection criterion, appearance second.</b> The fit stretches each axis
/// independently: the deliberate choice, so the picture keeps agreeing with the numbers FurnitureFit
/// and ResidenceMetrics report, which means a donor whose footprint aspect is far from the catalog's looks
/// squashed however handsome it is. <see cref="MeasureFamily"/> exists to turn 50 candidates into a
/// dozen on that basis before a single screenshot is taken.</para>
/// </remarks>
public class CatalogArtBinder : EditorWindow
{
    private const string CatalogPath  = "Assets/Resources/FurnitureCatalog.asset";
    private const string RegistryPath = "Assets/Resources/ResidenceCatalogRegistry.asset";
    private const string WrapperDir   = "Assets/Prefabs/ResidenceViz/Catalog";

    private const string C = "Assets/Prefabs/Furniture/Cute_Furniture_Free/Prefabs/";
    private const string M = "Assets/Prefabs/Furniture 2/Prefabs/";

    private const string SheetRoot = "__CatalogContactSheet";

    /// <summary>Where the art's local Z=0 plane sits. Only wall mounts care.</summary>
    /// <remarks>
    /// MountPose puts the object origin on the wall face and the placeholder box straddles it, so half
    /// an item's depth is inside the wall. Invisible on a 0.09 m grab bar; a 0.33 m wall cabinet buries
    /// 165 mm and reads as a bug. <see cref="PivotZ.Back"/> is the bake-side answer: no code change,
    /// and the only divergence is from the translucent compare-mode ghost cube, which is still centered.
    /// </remarks>
    private enum PivotZ { Center, Back }

    private struct Row
    {
        public string id;
        public string source;
        public float  yaw;      // quarter turns only. See CatalogArtFit's remarks on shear
        public PivotZ pivotZ;
        public string note;

        public Row(string id, string source, float yaw = 0f, PivotZ pivotZ = PivotZ.Center, string note = "")
        {
            this.id = id; this.source = source; this.yaw = yaw; this.pivotZ = pivotZ; this.note = note;
        }
    }

    // ---------------------------------------------------------------------------------------
    // The mapping
    // ---------------------------------------------------------------------------------------
    //
    // Cute_Furniture_Free first (toon, low-poly, two materials for the whole pack), Furniture Mega Pack
    // as the fallback for anything it does not cover.
    //
    // Deliberately left as placeholder boxes, with the reason, so nobody re-derives this:
    //
    //   wheelchair, walker, hospital_bed, transfer_bench, patient_lift
    //       No medical equipment in either pack. Stretching a chair into a patient lift would be a
    //       worse lie than a box, and these are the items the tool's whole argument rests on.
    //   shower_seat            0.41 m cube; and no sample ships one, by design (it renders inside a shower).
    //   roll_in_shower         0.05 m pan: a floor tray. Any donor would need a 30:1 squash.
    //   grab_bar_24/36, handrail   0.04 m rails. A stretched anything reads worse than a bar.
    //   light_switch, outlet, thermostat   sub-decimetre plates.
    //   threshold_ramp         0.03 m wedge.
    //
    // 21 bound + 14 placeholder = 35.
    // Donors are the measured winners from Measure Family, best-fit-wins with the Cute pack preferred
    // wherever it lands within 1.30, which is where its own art actually is. The trailing figure on
    // each line is that row's squash under the exact-size fit: 1.00 is undistorted.
    //
    // EVERY yaw here carries a half turn, and that is not a coincidence. PlanBuilder.YawFacingInto is
    // explicit that "rotationY = 0 looks down +Z", so an item's front must be its +Z face, while both
    // packs are Blender exports, and Blender's front is -Y, which lands on -Z under the standard axis
    // conversion. So a donor dropped in unturned stands with its back to the room: the sample
    // apartment's sofa sat against the west wall with its cushions pressed into the wall and its
    // backrest facing the living room, and nothing anywhere would have complained. The squash figure
    // cannot catch this (a footprint is identical under a half turn) which is why the yaw column is
    // settled by looking at the thing and not by measuring it.
    //
    // 180 and 270 rather than 0 and 90, therefore. A donor that ever does face +Z would be the
    // exception and would carry 0.
    private static readonly Row[] Rows =
    {
        // ---- bedroom -----------------------------------------------------------------------
        // The Cute pack's beds carry a tall headboard, so fitting them to the catalog's 0.60 m (which
        // is mattress height, not headboard height) halves the whole bed: 1.78 against Bed09's 1.21.
        new Row("twin_bed",      M + "Beds/Bed09.prefab",               180f),   // 1.21
        new Row("full_bed",      M + "Beds/Bed09.prefab",               180f),   // 1.24
        new Row("nightstand",    C + "Furniture/Nightstand_02.prefab",  180f),   // 1.18
        new Row("wardrobe",      C + "Furniture/Closet_01.prefab",      180f),   // 1.10
        new Row("dresser",       M + "Drawers/Drawer38.prefab",         180f),   // 1.13
        new Row("tv_stand",      M + "Drawers/Drawer35.prefab",         180f),   // 1.26: the low, wide one

        // ---- living ------------------------------------------------------------------------
        new Row("sofa",          C + "Furniture/Couch_11.prefab",       180f),   // 1.10
        new Row("armchair",      C + "Furniture/Armchair_18.prefab",    270f),   // 1.13
        // Deliberately not the other Cute armchair, so the two ids read as different pieces.
        new Row("recliner",      M + "Sofas/Sofa45.prefab",             270f),   // 1.06
        new Row("coffee_table",  M + "Tables/Table46.prefab",           270f),   // 1.22
        // catalog dining_table is SQUARE (1.07 x 1.07); Table29 is the square one, hence 1.00.
        new Row("dining_table",  M + "Tables/Table29.prefab",           180f),   // 1.00

        // ---- bathroom ----------------------------------------------------------------------
        new Row("toilet",        C + "Bathroom/Toilet_03.prefab",       180f),   // 1.24
        new Row("sink_pedestal", C + "Bathroom/Wash_Basin_07.prefab",   180f),   // 1.30
        // The Cute pack's Bath_03 includes a shower screen, so it measures 2.14 m tall and fits at 3.4+.
        new Row("bathtub",       M + "Bathroom/BathTub05.prefab",       270f),   // 1.07
        new Row("vanity",        M + "Bathroom/BathroomVanity01.prefab",180f),   // 1.10

        // ---- kitchen -----------------------------------------------------------------------
        new Row("refrigerator",  C + "Kitchen/Fridge_01.prefab",        180f),   // 1.23
        // NOT a GasStove: those are cooktops, 0.3 m tall, and fit at 3.8-7.0.
        new Row("range",         M + "Kitchen/KitchenOven01.prefab",    180f),   // 1.21
        new Row("base_cabinet",  M + "Kitchen/CabinetA05.prefab",       270f),   // 1.05
        // Same family letter as base_cabinet, or a kitchen run is two different cabinets.
        new Row("sink_base",     M + "Kitchen/CabinetA_Sink.prefab",    180f),   // 1.13
        // An island is a wider run of the same cabinet, which is what it should read as.
        new Row("island",        M + "Kitchen/CabinetA05.prefab",       270f),   // 1.41
        // The one poor fit that is kept: a full cabinet squashed to 0.33 m deep. An upper cabinet is
        // too significant a part of a kitchen to leave as a grey box, and this is the only row that
        // exercises the wall-mount path at all.
        new Row("wall_cabinet",  M + "Kitchen/CabinetA03.prefab",       270f, PivotZ.Back),   // 1.61
    };

    // ---------------------------------------------------------------------------------------
    // Window
    // ---------------------------------------------------------------------------------------

    private string _familyFolder = M + "Drawers";
    private string _familyTarget = "dresser";
    private int    _familyTop    = 12;
    private Vector2 _scroll;

    [MenuItem("Tools/ResidenceViz/Catalog Art Binder")]
    private static void Open() => GetWindow<CatalogArtBinder>("Catalog Art Binder");

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.LabelField("Bind furniture art to catalog ids", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            $"{Rows.Length} of the catalog's ids are mapped. Everything else renders as a labeled box, " +
            "which is a supported state. Edit the Rows table in this file to change a donor.",
            MessageType.None);

        EditorGUILayout.Space();
        if (GUILayout.Button("1 · Measure & Report  (writes nothing)")) MeasureAndReport();
        if (GUILayout.Button("2 · Generate Wrappers"))                  GenerateWrappers();
        if (GUILayout.Button("3 · Update Registry"))                    UpdateRegistry();
        if (GUILayout.Button("4 · Verify"))                             Verify();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Donor selection", EditorStyles.boldLabel);
        _familyFolder = EditorGUILayout.TextField("Folder", _familyFolder);
        _familyTarget = EditorGUILayout.TextField("Target catalog id", _familyTarget);
        _familyTop    = EditorGUILayout.IntField("Top N", _familyTop);
        if (GUILayout.Button("Measure Family  (ranks donors by squash)")) MeasureFamily(_familyFolder, _familyTarget);
        if (GUILayout.Button("Build Contact Sheet  (top N, in the open scene)"))
            BuildContactSheet(_familyFolder, _familyTarget, _familyTop);
        if (GUILayout.Button("Clear Contact Sheet")) ClearContactSheet();

        EditorGUILayout.EndScrollView();
    }

    // ---------------------------------------------------------------------------------------
    // Measurement
    // ---------------------------------------------------------------------------------------

    private struct Fit
    {
        public Bounds  local;         // donor bounds in wrapper-root space, at yaw, unscaled
        public Vector3 baseScale;
        public Vector3 basePosition;
        public bool    ok;
    }

    private static FurnitureCatalog LoadCatalog()
    {
        var cat = AssetDatabase.LoadAssetAtPath<FurnitureCatalog>(CatalogPath);
        if (cat == null) Debug.LogError($"[CatalogArtBinder] No FurnitureCatalog at {CatalogPath}.");
        return cat;
    }

    /// <summary>
    /// Bounds of a donor, expressed in the space of the node that will hold the fit scale.
    /// </summary>
    /// <remarks>
    /// Computed from each mesh's own bounds pushed through the relative matrix, rather than read off
    /// Renderer.bounds, for two reasons: it is a LOCAL box (Renderer.bounds is a world AABB, which
    /// would have to be converted back anyway), and it does not depend on the renderer having been
    /// culled or drawn: the whole measurement happens in a preview scene that never renders.
    /// Pack A ships isReadable:0, which blocks Mesh.vertices but not Mesh.bounds.
    /// </remarks>
    private static bool LocalBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        bool any = false;

        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;

            Mesh mesh = r is SkinnedMeshRenderer smr
                ? smr.sharedMesh
                : r.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null) continue;

            Matrix4x4 m = root.worldToLocalMatrix * r.transform.localToWorldMatrix;
            Vector3 c = mesh.bounds.center, e = mesh.bounds.extents;

            for (int i = 0; i < 8; i++)
            {
                var corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x,
                                             (i & 2) == 0 ? -e.y : e.y,
                                             (i & 4) == 0 ? -e.z : e.z);
                var p = m.MultiplyPoint3x4(corner);
                if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                else bounds.Encapsulate(p);
            }
        }
        return any;
    }

    /// <summary>Measures one donor at one yaw and solves the bake. See CatalogArtFit for the algebra.</summary>
    private static Fit Measure(Scene scene, GameObject donor, Vector3 target, float yaw, PivotZ pivotZ)
    {
        var fit = new Fit();

        var holder = new GameObject("__measure");
        SceneManager.MoveGameObjectToScene(holder, scene);
        holder.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(donor, scene);
        inst.transform.SetParent(holder.transform, false);
        inst.transform.localPosition = Vector3.zero;
        inst.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

        try
        {
            if (!LocalBounds(holder.transform, out fit.local)) return fit;

            Vector3 s = fit.local.size;
            if (s.x < 1e-4f || s.y < 1e-4f || s.z < 1e-4f) return fit;

            fit.baseScale = new Vector3(target.x / s.x, target.y / s.y, target.z / s.z);
            fit.basePosition = new Vector3(
                -fit.baseScale.x * fit.local.center.x,
                -fit.baseScale.y * fit.local.min.y,
                -fit.baseScale.z * (pivotZ == PivotZ.Back ? fit.local.min.z : fit.local.center.z));
            fit.ok = true;
            return fit;
        }
        finally { DestroyImmediate(holder); }
    }

    private static float Squash(Vector3 fitScale)
    {
        float lo = Mathf.Min(fitScale.x, Mathf.Min(fitScale.y, fitScale.z));
        float hi = Mathf.Max(fitScale.x, Mathf.Max(fitScale.y, fitScale.z));
        return lo <= 0f ? float.MaxValue : hi / lo;
    }

    private static string Dim(Vector3 v) => $"{v.x:0.00}w × {v.z:0.00}d × {v.y:0.00}h";

    private void MeasureAndReport()
    {
        var cat = LoadCatalog();
        if (cat == null) return;

        var scene = EditorSceneManager.NewPreviewScene();
        var sb = new StringBuilder("[CatalogArtBinder] Measure & Report\n");
        sb.AppendLine($"{"id",-15} {"catalog",-24} {"donor measured",-24} {"fit x/y/z",-22} squash  verdict");

        try
        {
            foreach (var row in Rows)
            {
                var entry = cat.Get(row.id);
                if (entry == null) { sb.AppendLine($"{row.id,-15} !! not in catalog (ids are case-sensitive)"); continue; }

                var donor = AssetDatabase.LoadAssetAtPath<GameObject>(row.source);
                if (donor == null) { sb.AppendLine($"{row.id,-15} !! donor missing: {row.source}"); continue; }

                Vector3 target = entry.SizeMeters;
                var fit  = Measure(scene, donor, target, row.yaw, row.pivotZ);
                if (!fit.ok) { sb.AppendLine($"{row.id,-15} !! degenerate bounds"); continue; }

                // The yaw hint: would swapping the footprint's X and Z bring it closer to the catalog's
                // aspect? It never auto-applies. Aspect cannot tell a sofa's front from its back.
                var turned = Measure(scene, donor, target, row.yaw + 90f, row.pivotZ);
                float sq = Squash(fit.baseScale);
                float sqT = turned.ok ? Squash(turned.baseScale) : float.MaxValue;

                var verdict = new List<string>();
                if (sq > 1.35f) verdict.Add("WARN squash");
                if (sqT < sq * 0.95f) verdict.Add($"WARN yaw? ({row.yaw + 90f}° gives {sqT:0.00})");
                float mn = Mathf.Min(fit.baseScale.x, Mathf.Min(fit.baseScale.y, fit.baseScale.z));
                float mx = Mathf.Max(fit.baseScale.x, Mathf.Max(fit.baseScale.y, fit.baseScale.z));
                if (mn < 0.4f || mx > 2.5f) verdict.Add("WARN scale");
                if (!string.IsNullOrEmpty(row.note)) verdict.Add(row.note);

                sb.AppendLine($"{row.id,-15} {Dim(target),-24} {Dim(fit.local.size),-24} " +
                              $"{fit.baseScale.x:0.00}/{fit.baseScale.y:0.00}/{fit.baseScale.z:0.00}".PadRight(22) +
                              $" {sq:0.00}   {string.Join("; ", verdict)}");
            }
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }

        Debug.Log(sb.ToString());
    }

    // ---------------------------------------------------------------------------------------
    // Generation
    // ---------------------------------------------------------------------------------------

    private static string WrapperPath(string id) => $"{WrapperDir}/{id}.prefab";

    private void GenerateWrappers()
    {
        var cat = LoadCatalog();
        if (cat == null) return;

        EnsureFolder(WrapperDir);

        var scene = EditorSceneManager.NewPreviewScene();
        int made = 0, skipped = 0, failed = 0;
        var sb = new StringBuilder("[CatalogArtBinder] Generate Wrappers\n");

        try
        {
            foreach (var row in Rows)
            {
                var entry = cat.Get(row.id);
                if (entry == null) { sb.AppendLine($"  FAIL {row.id}: not in catalog"); failed++; continue; }

                var donor = AssetDatabase.LoadAssetAtPath<GameObject>(row.source);
                if (donor == null) { sb.AppendLine($"  FAIL {row.id}: no donor at {row.source}"); failed++; continue; }

                if (Mathf.Abs(Mathf.Repeat(row.yaw, 90f)) > 0.01f)
                {
                    // A non-quarter yaw under a non-uniform parent scale is a shear, not a rotation.
                    sb.AppendLine($"  FAIL {row.id}: yaw {row.yaw}° is not a quarter turn"); failed++; continue;
                }

                string path = WrapperPath(row.id);
                var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (existing != null && existing.GetComponent<CatalogArtFit>()?.handTuned == true)
                {
                    sb.AppendLine($"  skip {row.id}: handTuned"); skipped++; continue;
                }

                var fit = Measure(scene, donor, entry.SizeMeters, row.yaw, row.pivotZ);
                if (!fit.ok) { sb.AppendLine($"  FAIL {row.id}: degenerate bounds"); failed++; continue; }

                // Built in the preview scene, so generating never dirties whatever the user has open.
                var root = new GameObject(row.id);
                SceneManager.MoveGameObjectToScene(root, scene);

                var art = new GameObject(ResidenceRenderer.ART_CHILD);
                art.transform.SetParent(root.transform, false);
                art.transform.localPosition = fit.basePosition;
                art.transform.localRotation = Quaternion.identity;
                art.transform.localScale    = fit.baseScale;

                var inst = (GameObject)PrefabUtility.InstantiatePrefab(donor, scene);
                inst.transform.SetParent(art.transform, false);
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = Quaternion.Euler(0f, row.yaw, 0f);

                var comp = root.AddComponent<CatalogArtFit>();
                comp.art          = art.transform;
                comp.nominalSize  = entry.SizeMeters;
                comp.baseScale    = fit.baseScale;
                comp.basePosition = fit.basePosition;

                // Overwriting the same path preserves the asset GUID, so registry rows and any scene
                // instances survive a regeneration.
                PrefabUtility.SaveAsPrefabAsset(root, path);
                DestroyImmediate(root);

                sb.AppendLine($"  ok   {row.id}  ← {System.IO.Path.GetFileNameWithoutExtension(row.source)}");
                made++;
            }
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        sb.AppendLine($"  {made} written, {skipped} skipped, {failed} failed.");
        if (failed > 0) Debug.LogError(sb.ToString()); else Debug.Log(sb.ToString());
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parts = path.Split('/');
        string acc = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = acc + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(acc, parts[i]);
            acc = next;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Registry
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Rewrites ResidenceCatalogRegistry's rows from the wrappers on disk.
    /// </summary>
    /// <remarks>
    /// This asset is generator-owned, which is why a wholesale rebuild is safe. The Site tool's
    /// Assets/Resources/PrefabRegistry.asset is a different file and is never touched. EditController
    /// renders its entries as the Place → Objects grid and the Paint Objects brush list, so interior
    /// rows there would show up as furniture in the site editor's object palette.
    ///
    /// A blank key or a null prefab is never emitted: PrefabRegistry.GetPrefab dereferences both
    /// without guarding.
    /// </remarks>
    private void UpdateRegistry()
    {
        var reg = AssetDatabase.LoadAssetAtPath<PrefabRegistry>(RegistryPath);
        if (reg == null) { Debug.LogError($"[CatalogArtBinder] No PrefabRegistry at {RegistryPath}."); return; }

        var list = new List<PrefabRegistry.Entry>();
        var sb = new StringBuilder("[CatalogArtBinder] Update Registry\n");

        foreach (var row in Rows)
        {
            var wrapper = AssetDatabase.LoadAssetAtPath<GameObject>(WrapperPath(row.id));
            if (wrapper == null) { sb.AppendLine($"  miss {row.id}: no wrapper, generate first"); continue; }
            list.Add(new PrefabRegistry.Entry { key = row.id, prefab = wrapper });
            sb.AppendLine($"  {row.id}");
        }

        reg.entries = list;
        EditorUtility.SetDirty(reg);
        AssetDatabase.SaveAssets();

        sb.AppendLine($"  {list.Count} rows registered.");
        Debug.Log(sb.ToString());
    }

    // ---------------------------------------------------------------------------------------
    // Verify
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Instantiates every wrapper, applies the fit at nominal size and again at a non-unit factor, and
    /// asserts the resulting bounds. The second half is the point: it is what proves basePosition has
    /// to scale componentwise alongside baseScale, and it is the shape of check that would have caught
    /// the collider being scaled twice.
    /// </summary>
    private void Verify()
    {
        var cat = LoadCatalog();
        if (cat == null) return;

        const float Tol = 0.001f;
        const float Factor = 1.37f;

        var scene = EditorSceneManager.NewPreviewScene();
        var sb = new StringBuilder("[CatalogArtBinder] Verify\n");
        int pass = 0, fail = 0;

        try
        {
            foreach (var row in Rows)
            {
                var wrapper = AssetDatabase.LoadAssetAtPath<GameObject>(WrapperPath(row.id));
                if (wrapper == null) { sb.AppendLine($"  MISS {row.id}: no wrapper"); fail++; continue; }

                var entry = cat.Get(row.id);
                if (entry == null) { sb.AppendLine($"  FAIL {row.id}: not in catalog"); fail++; continue; }

                var inst = (GameObject)PrefabUtility.InstantiatePrefab(wrapper, scene);
                inst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var fitc = inst.GetComponent<CatalogArtFit>();

                var problems = new List<string>();
                foreach (float f in new[] { 1f, Factor })
                {
                    Vector3 want = entry.SizeMeters * f;
                    fitc.Apply(want);

                    if (!LocalBounds(inst.transform, out Bounds b)) { problems.Add($"@{f:0.00} no bounds"); continue; }

                    if (Mathf.Abs(b.size.x - want.x) > Tol * f ||
                        Mathf.Abs(b.size.y - want.y) > Tol * f ||
                        Mathf.Abs(b.size.z - want.z) > Tol * f)
                        problems.Add($"@{f:0.00} size {Dim(b.size)} want {Dim(want)}");

                    if (Mathf.Abs(b.min.y) > Tol * f)      problems.Add($"@{f:0.00} min.y {b.min.y:0.0000}");
                    if (Mathf.Abs(b.center.x) > Tol * f)   problems.Add($"@{f:0.00} center.x {b.center.x:0.0000}");

                    float z = row.pivotZ == PivotZ.Back ? b.min.z : b.center.z;
                    if (Mathf.Abs(z) > Tol * f)            problems.Add($"@{f:0.00} {row.pivotZ} z {z:0.0000}");
                }

                DestroyImmediate(inst);

                if (problems.Count == 0) { sb.AppendLine($"  pass {row.id}"); pass++; }
                else { sb.AppendLine($"  FAIL {row.id}: {string.Join("; ", problems)}"); fail++; }
            }
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }

        sb.AppendLine($"  {pass} passed, {fail} failed.");
        if (fail > 0) Debug.LogError(sb.ToString()); else Debug.Log(sb.ToString());
    }

    // ---------------------------------------------------------------------------------------
    // Donor selection
    // ---------------------------------------------------------------------------------------

    private struct Candidate { public string path; public string name; public float yaw; public float squash; public Vector3 size; }

    /// <summary>
    /// Ranks every prefab in a folder as a donor for one catalog id, by how badly the exact-size fit
    /// would squash it. Trying both yaw 0 and yaw 90 and keeping the better.
    /// </summary>
    /// <remarks>
    /// This is the step that makes the visual pass affordable. Rendering all 50 Drawers or all 511
    /// Mega Pack prefabs to choose one by eye is the most expensive thing in this whole job; measuring
    /// them takes seconds and typically leaves 6-12 that would not look stretched.
    /// </remarks>
    private static List<Candidate> RankFamily(string folder, string id, out Vector3 target)
    {
        target = Vector3.zero;
        var results = new List<Candidate>();

        var cat = LoadCatalog();
        var entry = cat != null ? cat.Get(id) : null;
        if (entry == null) { Debug.LogError($"[CatalogArtBinder] '{id}' is not a catalog id."); return results; }
        target = entry.SizeMeters;

        var guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            foreach (var g in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                var donor = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (donor == null) continue;

                Candidate best = default; bool has = false;
                foreach (float yaw in new[] { 0f, 90f })
                {
                    var fit = Measure(scene, donor, target, yaw, PivotZ.Center);
                    if (!fit.ok) continue;
                    float sq = Squash(fit.baseScale);
                    if (!has || sq < best.squash)
                    {
                        best = new Candidate { path = path, name = donor.name, yaw = yaw, squash = sq, size = fit.local.size };
                        has = true;
                    }
                }
                if (has) results.Add(best);
            }
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }

        results.Sort((a, b) => a.squash.CompareTo(b.squash));
        return results;
    }

    private void MeasureFamily(string folder, string id) => MeasureFamily(folder, id, 30);

    private static void MeasureFamily(string folder, string id, int top)
    {
        var ranked = RankFamily(folder, id, out Vector3 target);
        if (ranked.Count == 0) return;

        var sb = new StringBuilder($"[CatalogArtBinder] {folder} ranked as '{id}' ({Dim(target)}), best first\n");
        foreach (var c in ranked.Take(top))
            sb.AppendLine($"  {c.squash,6:0.00}  yaw {c.yaw,3:0}  {c.name,-22} {Dim(c.size)}");
        sb.AppendLine($"  ({ranked.Count} candidates)");
        Debug.Log(sb.ToString());
    }

    /// <summary>
    /// Lays the top N candidates out in the open scene, each already stretched to the target catalog
    /// size (the only honest preview, since that is exactly how it will render) for a screenshot.
    /// </summary>
    private void BuildContactSheet(string folder, string id, int top)
    {
        ClearContactSheet();

        var ranked = RankFamily(folder, id, out Vector3 target);
        if (ranked.Count == 0) return;

        var root = new GameObject(SheetRoot);
        float pitch = Mathf.Max(target.x, target.z) + 0.8f;

        int n = Mathf.Min(top, ranked.Count);
        for (int i = 0; i < n; i++)
        {
            var c = ranked[i];
            var donor = AssetDatabase.LoadAssetAtPath<GameObject>(c.path);
            if (donor == null) continue;

            var cell = new GameObject(c.name);
            cell.transform.SetParent(root.transform, false);
            cell.transform.localPosition = new Vector3((i % 4) * pitch, 0f, -(i / 4) * pitch);

            var art = new GameObject(ResidenceRenderer.ART_CHILD);
            art.transform.SetParent(cell.transform, false);

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(donor);
            inst.transform.SetParent(art.transform, false);
            inst.transform.localRotation = Quaternion.Euler(0f, c.yaw, 0f);

            // Same solve GenerateWrappers uses, measured in place rather than in a preview scene.
            if (LocalBounds(cell.transform, out Bounds b) && b.size.x > 1e-4f && b.size.y > 1e-4f && b.size.z > 1e-4f)
            {
                var s = new Vector3(target.x / b.size.x, target.y / b.size.y, target.z / b.size.z);
                art.transform.localScale = s;
                art.transform.localPosition = new Vector3(-s.x * b.center.x, -s.y * b.min.y, -s.z * b.center.z);
            }

            var lab = new GameObject("Caption");
            lab.transform.SetParent(cell.transform, false);
            lab.transform.localPosition = new Vector3(0f, target.y + 0.15f, 0f);
            var tm = lab.AddComponent<TextMesh>();
            tm.text = $"{i + 1}. {c.name}\n{c.squash:0.00} · yaw {c.yaw:0}";
            tm.characterSize = 0.03f;
            tm.fontSize = 48;
            tm.anchor = TextAnchor.LowerCenter;
            tm.alignment = TextAlignment.Center;
        }

        Selection.activeGameObject = root;
        Debug.Log($"[CatalogArtBinder] Contact sheet for '{id}': {n} candidates from {folder}. " +
                  "Screenshot it (Composite shows Top, which is the yaw answer), then Clear Contact Sheet.");
    }

    private void ClearContactSheet()
    {
        var existing = GameObject.Find(SheetRoot);
        if (existing != null) DestroyImmediate(existing);
    }
}
