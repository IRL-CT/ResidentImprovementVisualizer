// What a room's floor is made of, from what the room IS.
//
// There used to be a floor-finish picker in the Rooms tool and a `RoomDef.floorMaterial` behind it.
// Both are gone, and the reason is that the finish was already the room type wearing a picker:
// RoomTool.SetType overwrote the chosen finish every time the type changed, PlanBuilder.FloorFor
// derived it from roomType for all six samples with the comment "Matches RoomTool's defaults, so a
// sample room is indistinguishable from a drawn one", and nothing between the two ever diverged. One
// table keyed on roomType is what both of them actually were.
//
// This lives in CXRAuthoring rather than on InteriorMaterialPalette for the split MeasureUI/Units
// already keeps: it is domain knowledge and it has to be testable, while the palette is a
// ScriptableObject in Assembly-CSharp that the EditMode tests cannot reach. An unknown roomType: an
// older file, or JSON somebody hand-edited. Falls through to floor_untyped, which is the graceful
// degradation that is the whole stated reason RoomType is string constants and not an enum.
//
// HUE IS THE SEPARATOR, AND THE BAND IS WHAT MAKES ROOM FOR IT. There is no tonemapping in this
// scene, so a shaded value over 1.0 is clipped rather than rolled off, and clipping is PER CHANNEL,
// no channel here may exceed 0.74. Floors must also sit clearly above Wall_Edge (0.38), because in
// Plan the wall cap is the entire visible surface of a wall and albedo is the only thing separating
// it from the floor beside it, and below Paint_White (0.72).
//
// The first version of this table put every floor in [0.52, 0.74] and let five of the twelve types
// stay NEUTRAL GREYS. Untyped, carpet, entry, storage, office. That is the arrangement that reads as
// "the floor is the same colour as the wall", and it is measurable: floor_carpet sat ΔE 7 from the
// wall cap and floor_entry ΔE 0.9 from floor_storage, i.e. the same colour twice. So every floor now
// carries a real hue, and the ladder is checked as a whole rather than eyeballed one row at a time:
// **no floor is within ΔE 20 of the rendered wall cap and no two floors are within ΔE 14 of each
// other** (CIELAB, on the colour as the scene's own lighting renders it, not on the raw albedo).
// Those two numbers are the contract. Change a row and re-check the pair table, because a floor is
// only ever seen beside another floor and beside a wall.
//
// Nothing here is white, either. Paint_White came down 0.93 → 0.72 and Ceiling_White 0.96 → 0.76:
// they used to render at L* 85 and 88 against a 0.94 camera background, so the walls, the ceilings
// and the page behind them were three shades of the same near-white. (Tile_Bath at 0.60/0.69/0.71 is
// a WALL finish and must never be used as a floor.)
public static class RoomFinish
{
    /// <summary>The palette material id for a room type's floor.</summary>
    public static string FloorMaterial(string roomType)
    {
        switch (roomType)
        {
            case RoomType.Bedroom:  return "floor_carpet";    // dusty rose carpet
            case RoomType.Bathroom: return "floor_bath";      // cool blue tile
            case RoomType.Kitchen:  return "floor_vinyl";     // pale yellow vinyl: the lightest floor
            case RoomType.Living:   return "floor_oak";       // oak
            case RoomType.Dining:   return "floor_dining";    // walnut, several steps darker than living
            case RoomType.Hall:     return "floor_hall";      // warm sand
            case RoomType.Entry:    return "floor_entry";     // clay brown, the mat at the door
            case RoomType.Laundry:  return "floor_laundry";   // mint green
            case RoomType.Storage:  return "floor_storage";   // muted violet, not a room you live in
            case RoomType.Office:   return "floor_office";    // slate blue

            // Other is a deliberate "none of these"; Untyped is "nobody has said yet". They look the
            // same on the floor because neither carries a claim about what happens in the room.
            case RoomType.Other:
            case RoomType.Untyped:
            default:                return "floor_untyped";   // cool grey: the dullest thing in the
                                                             // plan, and the only floor left neutral
        }
    }

    /// <summary>Every room type, in picker order. Untyped leads because it is where a room starts.</summary>
    public static readonly string[] All =
    {
        RoomType.Untyped,
        RoomType.Bedroom, RoomType.Bathroom, RoomType.Kitchen, RoomType.Living, RoomType.Dining,
        RoomType.Hall, RoomType.Entry, RoomType.Laundry, RoomType.Storage, RoomType.Office,
        RoomType.Other,
    };
}
