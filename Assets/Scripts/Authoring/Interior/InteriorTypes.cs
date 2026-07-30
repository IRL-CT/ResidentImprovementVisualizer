using System;
using System.Collections.Generic;

// CXRHomeViz interior authoring schema — the home/apartment analogue of AuthoringTypes.cs.
//
// Where AuthoringTypes.cs models an outdoor SITE (terrain, paths, fences, building massing) at ~200 m
// scale, this file models the INSIDE of a dwelling at ~10 m scale: rooms as polygons, walls as
// centerline segments with real thickness, and openings as parametric cuts in a wall.
//
// Deliberate reuse — these types are NOT redefined here, they are used verbatim from AuthoringTypes.cs
// (same CXRAuthoring assembly, same global namespace):
//   * ObjectInstance — free-standing furniture. Its `boxSizeMeters` field is already "a correctly
//     dimensioned massing box" (LayoutConverter sets it from target_dimensions_ft), which is exactly
//     what a furniture catalog entry needs before real art exists.
//   * SiteDef        — the OPTIONAL exterior layer hanging off a variant (ramps as PathDef, railings
//     as FenceDef, patios as SurfaceStrokeDef, ...). Null by default; see VariantDef.exterior.
//
// Conventions, unchanged from AuthoringConventions: 1 Unity unit = 1 meter, XZ is the ground plane,
// rotations in euler degrees. IDs are stable GUID strings. Serialized with Newtonsoft.Json — JsonUtility
// cannot round-trip the float[][] fields used here (same constraint the generation pipeline hit).
//
// Display is feet-and-inches by default (US group-home / assisted-living context); storage is always
// meters. Every conversion goes through Units.cs — never inline a magic 3.28.

public static class HomeConventions
{
    public const float IN_TO_M = 0.0254f;
    public const float FT_TO_M = 0.3048f;   // mirrors AuthoringConventions.FT_TO_M

    // 4.5" — a 2x4 stud wall with drywall both sides. The common US interior partition.
    public const float DEFAULT_WALL_THICKNESS = 0.114f;
    // 8 ft — the standard US residential ceiling.
    public const float DEFAULT_CEILING_HEIGHT = 2.44f;

    public const float DEFAULT_DOOR_WIDTH  = 0.813f;   // 32" nominal leaf
    public const float DEFAULT_DOOR_HEIGHT = 2.032f;   // 80"
    public const float DEFAULT_WINDOW_WIDTH  = 0.914f; // 36"
    public const float DEFAULT_WINDOW_HEIGHT = 1.219f; // 48"
    public const float DEFAULT_WINDOW_SILL   = 0.914f; // 36" above finished floor

    // Camera eye heights for the walkthrough view. The seated value is the reason this constant exists:
    // toggling to it shows a wheelchair user's actual sightline over counters and through windows,
    // which is the cheapest meaningful accessibility insight the tool offers.
    public const float EYE_HEIGHT_STANDING = 1.60f;
    public const float EYE_HEIGHT_SEATED   = 1.19f;

    // Geometry tolerance in meters. Two points closer than this are "the same point" for junction
    // welding, snapping, and polygon degeneracy checks.
    public const float EPS = 1e-4f;
}

// ---------------------------------------------------------------------------------------------
// Walls
// ---------------------------------------------------------------------------------------------

// A single straight wall, stored as a CENTERLINE segment plus a thickness. Storing the centerline
// (rather than two face lines or a rectangle) is what makes openings, junctions, and measurement
// tractable: an opening is a 1-D interval along the centerline, and two walls meeting at a corner
// simply share an endpoint.
//
// The wall's own local frame, used everywhere downstream:
//   forward = normalize(b - a)              along the wall, a -> b
//   left    = (-forward.z, 0, forward.x)    the "left" face when walking a -> b
//   right   = -left
// `materialLeft` / `materialRight` follow that convention, so flipping a wall's direction swaps them.
[Serializable]
public class WallDef
{
    public string id;
    public float[] a;                 // [x, z] centerline start, meters
    public float[] b;                 // [x, z] centerline end, meters
    public float thickness;           // meters; <= 0 => LevelDef.wallThickness
    public float height;              // meters; <= 0 => LevelDef.ceilingHeight
    public string materialLeft;       // key into InteriorMaterialPalette; null => default
    public string materialRight;
    // Informational only — nothing enforces it. Surfaced in the inspector rail so a proposal that
    // moves a load-bearing wall is visibly flagged as "this one needs an engineer", which matters
    // when the audience is care staff rather than architects.
    public bool structural;
}

// A door / window / pass-through cut into exactly one wall.
//
// `offset` is the distance along the host wall's centerline from `a` to the opening's CENTER, which
// makes an opening independent of the wall's thickness and of which face you are looking at. Openings
// are never subtracted from wall geometry with CSG: WallLayout emits the solid boxes BETWEEN them
// (plus headers above and sills below), so an opening is simply a gap the box list skips.
[Serializable]
public class OpeningDef
{
    public string id;
    public string wallId;             // ref to WallDef.id
    public float offset;              // meters along a -> b to the opening's center
    public float width;               // rough opening width, meters
    public float height;              // rough opening height, meters
    // The ACTUAL clear passage once a door leaf and stops are in place — always less than `width`.
    // <= 0 means "not specified"; HomeMetrics.ClearWidth then derives it from width + swing. Stored
    // separately because clear width is the number an accessibility rule would test, and a user who
    // measured it on site should be able to enter the real value rather than a derived guess.
    public float clearWidth;
    public float sillHeight;          // meters above finished floor; 0 for doors
    public string kind;               // OpeningKind.*
    public string swing;              // OpeningSwing.*
    // Height of the threshold strip at the floor, meters. 0 = step-free. A non-zero threshold is one
    // of the most common trip / wheelchair obstacles in an existing home, so it is a first-class field.
    public float thresholdHeight;
}

public static class OpeningKind
{
    public const string Door         = "door";
    public const string Window       = "window";
    public const string PassThrough  = "pass_through";   // no door, full height, no header trim
    public const string CasedOpening = "cased_opening";  // no door, headered and trimmed
}

public static class OpeningSwing
{
    public const string LeftIn   = "left_in";
    public const string LeftOut  = "left_out";
    public const string RightIn  = "right_in";
    public const string RightOut = "right_out";
    public const string Slider   = "slider";
    public const string Pocket   = "pocket";
    public const string None     = "none";   // windows, pass-throughs
}

// ---------------------------------------------------------------------------------------------
// Rooms
// ---------------------------------------------------------------------------------------------

// A room's floor area as a polygon in world meters. Rooms are authored independently of walls rather
// than derived from them: deriving enclosed regions from a wall graph is fragile while a plan is still
// half-traced, and a half-traced plan is the normal state of this tool. (Auto-detection is a deferred
// convenience, not a prerequisite.)
[Serializable]
public class RoomDef
{
    public string id;
    public string name;               // free text — "Resident bedroom 2"
    public string roomType;           // RoomType.*; drives default materials and rule applicability
    public float[][] polygon;         // [[x, z], ...] counter-clockwise, meters
    public string floorMaterial;
    public string ceilingMaterial;
    public float ceilingHeight;       // meters; <= 0 => LevelDef.ceilingHeight
}

public static class RoomType
{
    public const string Bedroom  = "bedroom";
    public const string Bathroom = "bathroom";
    public const string Kitchen  = "kitchen";
    public const string Living   = "living";
    public const string Dining   = "dining";
    public const string Hall     = "hall";
    public const string Entry    = "entry";
    public const string Laundry  = "laundry";
    public const string Storage  = "storage";
    public const string Office   = "office";
    public const string Other    = "other";
}

// ---------------------------------------------------------------------------------------------
// Wall-mounted items
// ---------------------------------------------------------------------------------------------

// A prop mounted on a wall FACE rather than standing on the floor: grab bar, wall cabinet, handrail,
// light switch, thermostat.
//
// This is deliberately a near-clone of EmbeddedObjectDef (AuthoringTypes.cs) with the tile coupling
// removed. It carries the identical `decor*` field set, so every helper in DecorAlignment.cs and
// DecorPlacement.cs applies unchanged — AnalyzeProp, AlignRotation, SeatDistance, FitScaleBox and
// AnchorOffsetInBand all work here verbatim. The only substitution is the host: EmbeddedObjectDef
// hosts on (hostGridX, hostGridZ, hostFloor, hostFace) in a tile grid; a WallMountDef hosts on
// (wallId, offset, side) along a wall centerline.
//
// Because the pose is DERIVED from the host wall rather than baked, moving a wall re-seats everything
// mounted on it — the same guarantee DecorPlacement.TryReseat gives for tile decor.
[Serializable]
public class WallMountDef
{
    public string instanceId;
    public string prefabType;         // catalog key; same key space as PrefabRegistry
    public string wallId;             // ref to WallDef.id
    public float offset;              // meters along the host wall's centerline, a -> b
    public int side;                  // WallSide.Left (0) or WallSide.Right (1)
    public float mountHeight;         // meters above finished floor to the item's anchor point

    // Copied from the FurnitureCatalog entry at placement time, so the render path can re-derive the
    // pose without consulting the catalog (and so an item keeps its authored look if the catalog
    // later changes). Semantics are identical to EmbeddedObjectDef's fields of the same name.
    public float decorWidthFrac;      // fraction of the available wall band the item spans
    public float decorHeightFrac;
    public int   decorAnchor;         // (int)DecorAlignment.Anchor — 0 Center, 1 Bottom, 2 Top
    public float decorSurfaceOffset;  // push along the wall normal to avoid z-fighting, meters
    public int   decorMountAxis;      // (int)DecorAlignment.MountAxis — 0 = Auto
    public bool  decorFlipMount;

    public bool included = true;      // per-variant show/hide, mirrors ObjectInstance.included
    public string note;               // free text, e.g. "existing — resident already has this"
}

public static class WallSide
{
    public const int Left  = 0;       // the +left face when walking the wall a -> b
    public const int Right = 1;
}

// ---------------------------------------------------------------------------------------------
// Levels
// ---------------------------------------------------------------------------------------------

// One storey of the dwelling. Multi-level homes are representable (elevation is real and per-level),
// but this pass edits and renders exactly one level at a time — stairs and inter-level circulation
// are deferred. The schema is shaped so adding them later does not migrate anything.
[Serializable]
public class LevelDef
{
    public string id;
    public string name;               // "Ground floor"
    public float elevation;           // meters above the building datum; 0 for the ground floor
    public float ceilingHeight;       // meters; <= 0 => HomeConventions.DEFAULT_CEILING_HEIGHT
    public float wallThickness;       // meters; <= 0 => HomeConventions.DEFAULT_WALL_THICKNESS

    public List<WallDef>    walls;
    public List<OpeningDef> openings;
    public List<RoomDef>    rooms;

    // Free-standing furniture. REUSED verbatim from AuthoringTypes.cs — `prefabType` is the catalog
    // key, `boxSizeMeters` carries the item's true [w, h, d] so it renders as a correctly sized
    // labeled box until real art exists under that key in PrefabRegistry.
    public List<ObjectInstance> furniture;

    public List<WallMountDef> wallMounted;
}

// ---------------------------------------------------------------------------------------------
// Sketch underlay
// ---------------------------------------------------------------------------------------------

// The imported floor-plan sketch, shown flat on the ground plane and traced over. With LLM layout
// parsing out of scope, this is the ONLY route from a paper plan into the model — which is why the
// calibration it carries matters more than anything else in this schema.
//
// `metersPerPixel` is set by the two-point calibration gesture: the user clicks two points on the
// image and types the real distance between them. Everything traced afterwards is at true scale, so
// a photo of a hand-drawn plan produces a dimensionally correct model.
[Serializable]
public class UnderlayDef
{
    public string imageFileName;      // bare filename; resolved under <storage>/underlays/<homeId>/
    public float[] originMeters;      // [x, z] world position of the image's bottom-left corner
    public float metersPerPixel;      // <= 0 => not yet calibrated; tracing is blocked until it is
    public float rotationDeg;         // to square up a photographed / skewed plan
    public float opacity = 0.6f;
    public bool locked;               // stop accidental nudging while tracing over it
}

// ---------------------------------------------------------------------------------------------
// Variants
// ---------------------------------------------------------------------------------------------

// One design option for a home: the as-built baseline, or a named proposal such as "widen bathroom
// door + add grab bars".
//
// A variant holds a FULL copy of the levels rather than a delta against its parent. A home is a few
// hundred walls at most, so a copy costs kilobytes, while a delta would need conflict handling every
// time the baseline changed — which is exactly the drift problem variants exist to prevent. Compare
// works by matching element `id`s between two variants (see VariantDiff), and duplication preserves
// those ids precisely so that matching stays meaningful.
[Serializable]
public class VariantDef
{
    public string id;
    public string name;               // "Existing", "Proposal A — widen bath door"
    public string description;        // free text shown in the variant list and the compare rail
    public string basedOnVariantId;   // provenance; null for the baseline
    public bool isBaseline;
    // Read-only guard, same semantics as EnvironmentDef.locked: the baseline is the record of how the
    // home actually is, so it is locked by default and every tool refuses to edit it until unlocked.
    public bool locked;

    public List<LevelDef> levels;

    // OPTIONAL exterior layer, null by default. Reuses SiteDef from AuthoringTypes.cs verbatim, which
    // means an outdoor additive costs no new geometry code: PathDef renders a ramp or walkway,
    // FenceDef a railing, SurfaceStrokeDef/TerrainZoneDef a patio or deck, gradePoints the slope the
    // ramp has to overcome. Rendered through ExteriorBridge -> the existing WorldRenderer.
    //
    // This lives on the VARIANT rather than the home because an outdoor addition IS a proposed
    // improvement: the baseline has no ramp, Proposal B does.
    public SiteDef exterior;

    // Outdoor free-standing objects (bench, planter, shed). Kept beside `exterior` rather than inside
    // it so it mirrors how EnvironmentDef separates `site` from `objectInstances`.
    public List<ObjectInstance> exteriorObjects;
}

// ---------------------------------------------------------------------------------------------
// The document
// ---------------------------------------------------------------------------------------------

// One home or apartment, with all of its design variants. This is the unit of save/load, of the
// library list, and of export/import — the whole file is what you email to a colleague.
[Serializable]
public class HomeDoc
{
    public string id;
    public string name;               // "Maple St. — Unit 2"
    public int version;               // bumped by HomeStore.Save
    public string schemaVersion = HomeSchema.CURRENT;
    public List<string> tags;
    public bool favorite;

    // Master switch for the optional exterior layer. False by default: the tool is interior-first,
    // and a care worker planning a bathroom retrofit should never have to think about site geometry.
    // Flipping it on reveals the exterior view toggle and renders VariantDef.exterior.
    public bool exteriorEnabled;

    public UnderlayDef underlay;
    public List<VariantDef> variants;
    public string activeVariantId;
}

public static class HomeSchema
{
    public const string CURRENT = "homeviz/1";
}

// Lightweight row for the library list — mirrors EnvironmentSummary's role, but built locally by
// HomeStore from the file's header rather than returned by a server.
[Serializable]
public class HomeSummary
{
    public string id;
    public string name;
    public int version;
    public List<string> tags;
    public string updated;            // ISO 8601, from the file's last-write time
    public bool favorite;
    public int variantCount;
    public bool exteriorEnabled;
}
