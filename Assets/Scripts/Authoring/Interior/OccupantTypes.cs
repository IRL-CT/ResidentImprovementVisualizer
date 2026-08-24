using System;
using System.Collections.Generic;

// The people who live in the residence, and what they are doing at a given hour.
//
// Everything else in this schema describes what a dwelling IS. This describes what it is FOR, which is
// where every accessibility argument the tool exists to make actually lives: two wheelchairs needing to
// pass in a corridor, one roll-in shower serving five residents between seven and eight, the accessible
// bedroom being at the far end from the only accessible bathroom. None of that is visible in a plan
// with nobody in it.
//
// Occupants hang off VariantDef, not LevelDef: a household belongs to a design option, so "Alice
// sleeps in Bedroom 3" and "Alice sleeps in Bedroom 1" are two options to compare, and VariantDiff
// reports the move because NewProposalFrom deep-copies a variant PRESERVING ids.
//
// Fidelity here is deliberately low: a person is a labelled capsule standing in a room, with no path,
// no animation and no walking between activities. Positions are derived from the schedule, never
// stored. See OccupancyModel.

[Serializable]
public class OccupantDef
{
    public string id;                 // "p_1" from PlanBuilder; a Guid from PeopleTool
    public string name;               // "Alice": what appears on the marker and in the change list
    public string note;               // free text: "transfers independently", "night carer"

    // Drives a seated marker at ResidenceConventions.EYE_HEIGHT_SEATED over a wheelchair-sized pad. This is
    // the whole point of the seated view, so it is a field on the person rather than a note to read.
    public bool usesWheelchair;

    public float[] color;             // [r,g,b] marker tint; null => OccupantPalette by roster index

    // Field initializer, matching WallMountDef.included: JSON written before this existed has no key,
    // so the person loads as present rather than silently vanishing.
    public bool included = true;

    public List<ActivityDef> schedule;
}

// One block of a repeating day. There is no date and no weekday: the timeline is 24 hours long and it
// repeats, which is the granularity a meeting about a doorway needs.
[Serializable]
public class ActivityDef
{
    public string id;                 // "a_7" from PlanBuilder; a Guid from PeopleTool
    public string kind;               // ActivityKind.*. Colours the timeline and suggests a room
    public string label;              // free text; empty => ActivityKind.Label(kind)

    // Minutes from midnight, 0..1439. `end` BEFORE `start` wraps past midnight, which is how sleep is
    // expressed (22:30 → 07:00). Equal ends mean the block covers the whole day. See Clock.Spans.
    public int startMinutes;
    public int endMinutes;

    // RoomDef.id. authoritative. `kind` only suggests a default when the activity is created; what
    // actually places the person is this. Null or empty means away from residence, and the marker hides.
    public string roomId;

    // Optional ObjectInstance.instanceId to stand beside: the range while cooking, the bed while
    // sleeping. Null falls back to the room's largest inscribed circle.
    public string anchorId;
}

// Activity kinds, as string constants rather than an enum for the same reason RoomType is: the schema
// is JSON that a person may read and hand-edit, and an unknown value degrades to "other" instead of
// deserializing as a meaningless integer.
public static class ActivityKind
{
    public const string Sleep = "sleep";
    public const string Hygiene = "hygiene";
    public const string Cook = "cook";
    public const string Eat = "eat";
    public const string Relax = "relax";
    public const string Work = "work";
    public const string Care = "care";
    public const string Out = "out";
    public const string Other = "other";

    public static readonly string[] All =
    {
        Sleep, Hygiene, Cook, Eat, Relax, Work, Care, Out, Other,
    };

    /// <summary>What the timeline block and the marker label say when the activity has no label.</summary>
    public static string Label(string kind) => kind switch
    {
        Sleep => "Sleeping",
        Hygiene => "Getting ready",
        Cook => "Cooking",
        Eat => "Eating",
        Relax => "Relaxing",
        Work => "Working",
        Care => "Care",
        Out => "Out",
        _ => "At residence",
    };

    /// <summary>
    /// The RoomType a new activity of this kind should default to. Only a suggestion. ActivityDef.roomId
    /// is what places the person, precisely because a five-bathroom care home has no single right answer.
    /// </summary>
    public static string DefaultRoomType(string kind) => kind switch
    {
        Sleep => RoomType.Bedroom,
        Hygiene => RoomType.Bathroom,
        Cook => RoomType.Kitchen,
        Eat => RoomType.Dining,
        Relax => RoomType.Living,
        Work => RoomType.Office,
        Care => RoomType.Bedroom,
        _ => null,
    };

    /// <summary>True when this kind means "not in the dwelling", so no room is expected.</summary>
    public static bool IsAway(string kind) => kind == Out;

    /// <summary>[r,g,b] for the dashboard timeline block.</summary>
    public static float[] Swatch(string kind) => kind switch
    {
        Sleep => new[] { 0.42f, 0.45f, 0.68f },
        Hygiene => new[] { 0.36f, 0.63f, 0.65f },
        Cook => new[] { 0.85f, 0.62f, 0.33f },
        Eat => new[] { 0.80f, 0.51f, 0.36f },
        Relax => new[] { 0.52f, 0.68f, 0.48f },
        Work => new[] { 0.45f, 0.55f, 0.72f },
        Care => new[] { 0.76f, 0.48f, 0.55f },
        Out => new[] { 0.74f, 0.76f, 0.80f },
        _ => new[] { 0.62f, 0.64f, 0.70f },
    };

    public static bool IsKnown(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return false;
        foreach (var k in All) if (k == kind) return true;
        return false;
    }
}

// Marker tints assigned by roster position when OccupantDef.color is null. Chosen to stay legible
// against the pale floor and to remain distinguishable from each other at plan-view scale.
public static class OccupantPalette
{
    private static readonly float[][] Colors =
    {
        new[] { 0.29f, 0.47f, 0.78f },   // blue
        new[] { 0.83f, 0.47f, 0.30f },   // terracotta
        new[] { 0.35f, 0.62f, 0.44f },   // green
        new[] { 0.64f, 0.42f, 0.72f },   // violet
        new[] { 0.85f, 0.66f, 0.25f },   // ochre
        new[] { 0.30f, 0.63f, 0.68f },   // teal
        new[] { 0.78f, 0.40f, 0.52f },   // rose
        new[] { 0.45f, 0.50f, 0.58f },   // slate
    };

    public static float[] At(int index)
    {
        if (Colors.Length == 0) return new[] { 0.5f, 0.5f, 0.5f };
        int i = index % Colors.Length;
        if (i < 0) i += Colors.Length;
        return Colors[i];
    }

    /// <summary>The occupant's own colour when set, otherwise the palette entry for their position.</summary>
    public static float[] For(OccupantDef occupant, int index)
        => occupant?.color != null && occupant.color.Length >= 3 ? occupant.color : At(index);

    public static int Count => Colors.Length;
}
