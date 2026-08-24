using System.Collections.Generic;
using System.Text;

// What the model is asked to emit when it reads a floor-plan sketch, and the JSON Schema that
// constrains it.
//
// WHY THIS SHAPE AND NOT LevelDef: nothing downstream of the schema complains about bad geometry,
// WallLayout clamps an opening that hangs off its wall, WallMeshBuilder leaves a ~57 mm notch
// wherever two wall endpoints miss each other, ResidenceRenderer skips an opening whose wallId does not
// resolve. Asking a model for raw WallDef/OpeningDef would produce exactly those failures, because
// OpeningDef.offset is meters along a SPECIFIC wall AFTER T-junction splitting: a number that does
// not exist until the wall graph has been derived, and therefore one nothing can be asked for up
// front. So the model fills in PlanBuilder's authoring surface instead. Rooms as rectangles, and
// openings and furniture by RELATIONSHIP, and the tested derivation does the error-prone half.
//
// COORDINATES ARE NORMALISED TO THE IMAGE, 0..1000, ORIGIN TOP-LEFT, x right and y DOWN. That is the
// frame the model actually sees, and it is right for two reasons: a model estimates position
// relative to what is in front of it far better than in absolute metres, and normalised coordinates
// are independent of calibration, so re-calibrating the underlay afterwards does not invalidate the
// reasoning that produced the plan. SketchPlanCompiler owns the conversion to world metres, and the
// vertical flip in it is the one thing there that is easy to get silently wrong.
//
// Lengths that are NOT positions (an opening's width, its sill) are plain metres. They are
// properties of the building rather than of the picture, and a door is 0.813 m wide whatever
// resolution the sketch was scanned at.
public sealed class SketchPlanSpec
{
    public List<SketchRoom> rooms;
    public List<SketchOpening> openings;
    public List<SketchItem> furniture;

    /// <summary>Whatever the model could not read off the sketch. Shown to the user, never parsed.</summary>
    public string notes;

    public IReadOnlyList<SketchRoom> Rooms => rooms ?? Empty.Rooms;
    public IReadOnlyList<SketchOpening> Openings => openings ?? Empty.Openings;
    public IReadOnlyList<SketchItem> Furniture => furniture ?? Empty.Items;

    private static class Empty
    {
        public static readonly List<SketchRoom> Rooms = new List<SketchRoom>();
        public static readonly List<SketchOpening> Openings = new List<SketchOpening>();
        public static readonly List<SketchItem> Items = new List<SketchItem>();
    }

    // -----------------------------------------------------------------------------------------
    // The JSON Schema
    // -----------------------------------------------------------------------------------------

    /// <summary>How a placement in <see cref="SketchItem.placement"/> is spelled.</summary>
    public static class SketchPlacement
    {
        public const string Against = "against";
        public const string Free = "free";
        public const string Mount = "mount";

        public static readonly string[] All = { Against, Free, Mount };
    }

    /// <summary>How an edge in <see cref="SketchOpening.edge"/> is spelled. Empty means "not used".</summary>
    public static readonly string[] Edges = { "south", "east", "north", "west", "" };

    public static readonly string[] OpeningKinds =
    {
        OpeningKind.Door, OpeningKind.Window, OpeningKind.PassThrough, OpeningKind.CasedOpening,
    };

    /// <summary>
    /// Which way alongFraction runs, said once and shared by both call sites.
    ///
    /// PlanBuilder.EdgeLine lerps from the MINIMUM coordinate on the edge to the maximum: west to
    /// east on a north or south wall, and SOUTH TO NORTH on an east or west one. That second case is
    /// the trap: nobody reading a picture measures a vertical wall from the bottom up, so a model
    /// left to guess will put every grab bar and every wardrobe at the wrong end of its wall. It is
    /// stated in terms of the same compass words the edge itself uses, so there is nothing to infer.
    /// </summary>
    private const string ALONG =
        "Where along that wall it sits, 0 to 1. On a north or south wall 0 is the west (left) end; "
        + "on an east or west wall 0 is the south (bottom) end. 0.5 centers it.";

    /// <summary>
    /// How an L-shaped room is said, and the reason it cannot just be two rooms with one name.
    ///
    /// Every rectangle contributes its four edges to the wall derivation, so two rectangles that only
    /// share a name get a wall built along the edge between them: a wall that renders, encloses and
    /// is reported by nothing, in the middle of a room the drawing shows as open. Naming the parent
    /// here is what suppresses it and merges the two into one floor with one id.
    /// </summary>
    private const string PART_OF =
        "Empty for a normal room. An L-shaped or irregular room is described as two or more "
        + "rectangles: give the largest one a key and leave its \"partOf\" empty, then for each "
        + "other piece put that key here. Pieces must meet along a whole edge, and "
        + "they become ONE room with no wall between them, so only set this when the drawing shows "
        + "no wall there. Two rooms that are genuinely separate get a door instead.";

    /// <summary>
    /// The redundant metric channel, and why it earns its two fields.
    ///
    /// Everything else about a room's position is normalised to the image, which is the frame the
    /// model can actually see, but it means a plan traced into half the 0..1000 range, or a room read
    /// off the wrong dimension line, produces coordinates that are internally consistent and wrong at
    /// a scale nothing downstream can detect. Asking for the size a second way, in the unit the
    /// building is actually built in, gives SketchPlanValidator something to disagree with.
    /// </summary>
    private const string MEASURED =
        "In meters, read off the drawing or estimated from what the room is. NEVER convert these from the "
        + "0-1000 numbers above. This is a deliberate second opinion and is checked against them.";

    /// <summary>Rooms, openings and furniture together: the whole plan in one reply.</summary>
    public static string JsonSchema() => Schema(rooms: true, detail: true);

    /// <summary>
    /// The first pass: rooms and nothing else.
    ///
    /// Reading a plan is really two jobs (where the rooms are, and what is in them) and asking for
    /// both at once makes the model trade one off against the other in a single sampling pass. The
    /// split is worth it because the two are not equally recoverable: EVERY opening and every item is
    /// addressed by a room key, so a room read wrongly takes the rest of the plan with it, while a
    /// missed wardrobe costs a wardrobe. So the geometry is settled first, checked, and then handed
    /// back as fact for the second pass to work against.
    /// </summary>
    public static string RoomsSchema() => Schema(rooms: true, detail: false);

    /// <summary>The second pass: openings and furniture, against rooms already agreed.</summary>
    public static string DetailSchema() => Schema(rooms: false, detail: true);

    /// <summary>
    /// The schema handed to the API's structured-output mode, so the response is valid by
    /// construction rather than by hope.
    ///
    /// Two properties of that mode shape everything below. Every object must carry
    /// `additionalProperties: false` and list EVERY property in `required`, so fields that do not
    /// apply to a given row carry a sentinel ("" or 0) rather than being omitted. And numeric
    /// constraints (`minimum`, `maximum`) are among the keywords it does not support, which is why
    /// the 0..1000 and 0..1 ranges are stated in the prompt and enforced by SketchPlanValidator
    /// instead. What the schema CAN enforce is enums, and that is why the room types and the catalog
    /// ids are carried as enums: generated from RoomFinish.All and SampleFurniture.All, so the model
    /// is unable to name a room type or a catalog item that does not exist. That deletes a whole
    /// class of validation rather than implementing it.
    ///
    /// Built as a string rather than through a serializer because CXRAuthoring deliberately has no
    /// references at all: the same constraint that puts SampleFurniture here rather than reading
    /// the catalog asset.
    /// </summary>
    private static string Schema(bool rooms, bool detail)
    {
        var sb = new StringBuilder(4096);
        sb.Append("{\"type\":\"object\",\"additionalProperties\":false,\"required\":[");
        if (rooms) sb.Append("\"rooms\",");
        if (detail) sb.Append("\"openings\",\"furniture\",");
        sb.Append("\"notes\"],\"properties\":{");

        if (rooms) { RoomsFragment(sb); sb.Append(','); }
        if (detail) { OpeningsFragment(sb); sb.Append(','); FurnitureFragment(sb); sb.Append(','); }

        Str(sb, "notes", "Anything you could not read off the sketch, or had to guess at. One or two sentences.");
        sb.Append("}}");
        return sb.ToString();
    }

    private static void RoomsFragment(StringBuilder sb)
    {
        sb.Append("\"rooms\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,");
        sb.Append("\"required\":[\"key\",\"name\",\"roomType\",\"partOf\",\"x\",\"y\",\"w\",\"h\",");
        sb.Append("\"widthMeters\",\"depthMeters\"],\"properties\":{");
        Str(sb, "key", "Short lowercase slug, unique across rooms, e.g. \"bed1\" or \"hall\"."); sb.Append(',');
        Str(sb, "name", "What to call the room on screen, e.g. \"Bedroom 1\"."); sb.Append(',');
        Enum(sb, "roomType", RoomFinish.All, "What kind of room this is."); sb.Append(',');
        Str(sb, "partOf", PART_OF); sb.Append(',');
        Int(sb, "x", "Left edge, 0-1000 across the image."); sb.Append(',');
        Int(sb, "y", "Top edge, 0-1000 DOWN the image from its top."); sb.Append(',');
        Int(sb, "w", "Width in the same 0-1000 units."); sb.Append(',');
        Int(sb, "h", "Height in the same 0-1000 units."); sb.Append(',');
        Num(sb, "widthMeters", MEASURED + " How wide this rectangle is, west to east."); sb.Append(',');
        Num(sb, "depthMeters", MEASURED + " How deep this rectangle is, south to north.");
        sb.Append("}}}");
    }

    private static void OpeningsFragment(StringBuilder sb)
    {
        sb.Append("\"openings\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,");
        sb.Append("\"required\":[\"kind\",\"between\",\"room\",\"edge\",\"alongFraction\",");
        sb.Append("\"widthMeters\",\"sillMeters\",\"heightMeters\"],\"properties\":{");
        Enum(sb, "kind", OpeningKinds, "What sort of opening this is."); sb.Append(',');
        sb.Append("\"between\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":");
        sb.Append(Esc("The two room keys this opening connects. Use for anything between two rooms. "
                    + "Empty array for an opening in an exterior wall."));
        sb.Append("},");
        Str(sb, "room", "The one room key, for an opening in an exterior wall. Empty when \"between\" is used."); sb.Append(',');
        Enum(sb, "edge", Edges, "Which exterior wall of that room, as drawn: south is the bottom of the image, north the top. Empty when \"between\" is used."); sb.Append(',');
        Num(sb, "alongFraction", ALONG); sb.Append(',');
        Num(sb, "widthMeters", "Rough opening width in meters. A standard interior door is 0.813; an accessible one 0.914."); sb.Append(',');
        Num(sb, "sillMeters", "Height of a window's sill above the floor in meters. 0 for any kind of door."); sb.Append(',');
        Num(sb, "heightMeters", "Opening height in meters. 0 means use the usual size for this kind.");
        sb.Append("}}}");
    }

    private static void FurnitureFragment(StringBuilder sb)
    {
        var catalogIds = new List<string>();
        foreach (var item in SampleFurniture.All) catalogIds.Add(item.id);
        catalogIds.Sort(string.CompareOrdinal);

        sb.Append("\"furniture\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,");
        sb.Append("\"required\":[\"catalogId\",\"room\",\"placement\",\"edge\",\"alongFraction\",");
        sb.Append("\"xFraction\",\"zFraction\",\"yawDegrees\",\"alongWall\"],\"properties\":{");
        Enum(sb, "catalogId", catalogIds.ToArray(), "Which catalog item this is."); sb.Append(',');
        Str(sb, "room", "The key of the room it stands in."); sb.Append(',');
        Enum(sb, "placement", SketchPlacement.All,
             "\"against\" stands it flush against a wall facing into the room; \"free\" places it away "
             + "from the walls; \"mount\" hangs it on a wall (grab bars, wall cabinets, switches)."); sb.Append(',');
        Enum(sb, "edge", Edges, "Which wall, for \"against\" and \"mount\". Empty for \"free\"."); sb.Append(',');
        Num(sb, "alongFraction", ALONG + " Ignored for \"free\"."); sb.Append(',');
        Num(sb, "xFraction", "Across the room west to east, 0 to 1. Only for \"free\"."); sb.Append(',');
        Num(sb, "zFraction", "Across the room south to north, 0 to 1. Only for \"free\"."); sb.Append(',');
        Num(sb, "yawDegrees", "Which way it faces, degrees. Only for \"free\"; 0 looks north."); sb.Append(',');
        sb.Append("\"alongWall\":{\"type\":\"boolean\",\"description\":");
        sb.Append(Esc("Turn the item a quarter turn so its long side runs along the wall. Baths and "
                    + "showers need this, because they are installed as an alcove."));
        sb.Append('}');
        sb.Append("}}}");
    }

    private static void Str(StringBuilder sb, string name, string description)
    {
        sb.Append(Esc(name)).Append(":{\"type\":\"string\",\"description\":").Append(Esc(description)).Append('}');
    }

    private static void Int(StringBuilder sb, string name, string description)
    {
        sb.Append(Esc(name)).Append(":{\"type\":\"integer\",\"description\":").Append(Esc(description)).Append('}');
    }

    private static void Num(StringBuilder sb, string name, string description)
    {
        sb.Append(Esc(name)).Append(":{\"type\":\"number\",\"description\":").Append(Esc(description)).Append('}');
    }

    private static void Enum(StringBuilder sb, string name, IReadOnlyList<string> values, string description)
    {
        sb.Append(Esc(name)).Append(":{\"type\":\"string\",\"enum\":[");
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Esc(values[i]));
        }
        sb.Append("],\"description\":").Append(Esc(description)).Append('}');
    }

    /// <summary>A JSON string literal, quotes included.</summary>
    private static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}

/// <summary>One rectangle of a room, in normalised image coordinates.</summary>
public sealed class SketchRoom
{
    public string key;
    public string name;
    public string roomType;

    /// <summary>Empty for a whole room; the parent's key for another piece of one. See PART_OF.</summary>
    public string partOf;

    public int x;       // left, 0..1000
    public int y;       // top, 0..1000 measured DOWN from the image top
    public int w;
    public int h;

    /// <summary>Stated in metres, independently of x/y/w/h. See MEASURED. 0 means not stated.</summary>
    public float widthMeters, depthMeters;

    public bool IsPart => !string.IsNullOrWhiteSpace(partOf) && partOf != key;
}

/// <summary>One door, window, pass-through or cased opening, placed by relationship.</summary>
public sealed class SketchOpening
{
    public string kind;
    public List<string> between;   // two room keys, or empty for an exterior wall
    public string room;            // the one room, when `between` is empty
    public string edge;            // which exterior wall of that room
    public float alongFraction;
    public float widthMeters;
    public float sillMeters;
    public float heightMeters;

    public bool IsInterior => between != null && between.Count == 2;
}

/// <summary>One catalog item, placed by relationship to its room.</summary>
public sealed class SketchItem
{
    public string catalogId;
    public string room;
    public string placement;
    public string edge;
    public float alongFraction;
    public float xFraction;
    public float zFraction;
    public float yawDegrees;
    public bool alongWall;
}
