using UnityEngine;

// Ties a rendered GameObject back to the schema element it came from, so a raycast can answer "what
// did the user just click?".
//
// The Site renderer uses one tiny marker component per element type (InstanceMarker, PathMarker,
// FenceMarker, TileInstanceMarker). One component with a Kind enum is used here instead: picking in
// this tool has to resolve a mixed hit list: a click can land on a wall, the door in it, the floor
// beneath, or a chair on top, and a single type means one GetComponent call and one switch rather
// than four probes per hit.
public class ResidenceElementMarker : MonoBehaviour
{
    public enum Kind { Wall, Opening, Room, Floor, Ceiling, Furniture, WallMount, Occupant, Sensor }

    public Kind kind;
    public string id;

    // The element this one hangs off, when that differs from `id`:
    //   Opening / WallMount -> the host wall's id
    //   Floor / Ceiling     -> the room's id
    //   Sensor              -> the id of whatever it watches: an opening, a bed, a room, a wall
    // Lets the inspector jump from a door to its wall without searching the level.
    public string parentId;
}
