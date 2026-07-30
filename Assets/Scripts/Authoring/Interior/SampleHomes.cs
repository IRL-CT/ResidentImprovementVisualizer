using System.Collections.Generic;

// Six built-in sample dwellings, authored as data.
//
// The library is empty on a fresh install and the only way in is "import a floor plan and calibrate
// it" — the hardest step of the workflow. These exist so the first launch lands on something you can
// walk through, measure and compare instead of a blank ground pad.
//
// Each is a single storey with a locked "Existing" baseline and no proposal variants: branching one
// is what the Design options rail is for. Every plan is complete enough to live in — somewhere to
// sleep, cook, wash and sit, for the stated number of residents. The two five-bedroom plans are the
// group-home / assisted-living cases the tool is aimed at, so their doors are all 36" and step-free
// and their bathrooms are roll-in.
//
// Geometry is derived by PlanBuilder from the room rectangles below; see that file for why nothing
// here names a wall id or an offset.
public static class SampleHomes
{
    public struct Spec
    {
        public string key;
        public string displayName;
        public string blurb;      // the library row's second line
        public string summary;    // the baseline variant's description
    }

    // 36" — the width the two care-setting samples use throughout.
    private const float WIDE_DOOR = 0.914f;
    // 32" — a normal interior door.
    private const float DOOR = HomeConventions.DEFAULT_DOOR_WIDTH;

    public static readonly IReadOnlyList<Spec> All = new[]
    {
        new Spec
        {
            key = "studio_apartment",
            displayName = "Studio apartment",
            blurb = "38 m² · studio · 1 person",
            summary = "A one-person studio: bathroom, galley kitchen, entry, and one room that is "
                    + "both living and sleeping space.",
        },
        new Spec
        {
            key = "apartment_2b1b",
            displayName = "Apartment — 2 bed, 1 bath",
            blurb = "74 m² · 2 bed / 1 bath · 2–3 people",
            summary = "A two-bedroom apartment off a central hall, with the living and kitchen "
                    + "sharing the front of the plan.",
        },
        new Spec
        {
            key = "apartment_5b4b",
            displayName = "Group home apartment — 5 bed, 4 bath",
            blurb = "165 m² · 5 bed / 4 bath · 5 residents",
            summary = "A five-resident group home: bedrooms off a wide central corridor, four "
                    + "bathrooms, and shared living, dining and kitchen. All doors are 36\" and "
                    + "step-free; bathroom 4 is roll-in.",
        },
        new Spec
        {
            key = "house_2b1b",
            displayName = "House — 2 bed, 1 bath",
            blurb = "90 m² · 2 bed / 1 bath · 2 people",
            summary = "A small single-storey house: living, kitchen and dining across the front, "
                    + "two bedrooms, a bathroom and a laundry off the rear hall.",
        },
        new Spec
        {
            key = "house_3b2b",
            displayName = "House — 3 bed, 2 bath",
            blurb = "125 m² · 3 bed / 2 bath · 4 people",
            summary = "A family house with three bedrooms, an ensuite off the main bedroom, a "
                    + "second bathroom off the hall, and a separate laundry.",
        },
        new Spec
        {
            key = "house_5b4b",
            displayName = "Assisted living house — 5 bed, 4 bath",
            blurb = "210 m² · 5 bed / 4 bath · 5–6 residents",
            summary = "A five-resident assisted living house: paired bedrooms and bathrooms along a "
                    + "1.6 m corridor with handrails, plus a staffed kitchen, dining and common "
                    + "room. All doors are 36\" and step-free; two bathrooms are roll-in.",
        },
    };

    public static bool TryGetSpec(string key, out Spec spec)
    {
        foreach (var s in All)
            if (s.key == key) { spec = s; return true; }
        spec = default;
        return false;
    }

    /// <summary>
    /// Builds the sample as a complete HomeDoc with deterministic ids. The installer replaces
    /// <c>id</c> with a fresh GUID so the same sample can be added more than once.
    /// </summary>
    public static HomeDoc Build(string key)
    {
        if (!TryGetSpec(key, out var spec)) return null;

        var builder = Plan(key);
        if (builder == null) return null;

        var baseline = new VariantDef
        {
            id = key + "_existing",
            name = "Existing",
            description = spec.summary,
            isBaseline = true,
            // Locked for the same reason HomeStore.Create locks it: the baseline is the record of how
            // the home is. Design options -> Unlock is one click for anyone who wants to edit it here.
            locked = true,
            levels = new List<LevelDef> { builder.Build() },
        };

        return new HomeDoc
        {
            id = key,
            name = spec.displayName,
            version = 1,
            schemaVersion = HomeSchema.CURRENT,
            tags = new List<string> { "sample" },
            favorite = false,
            exteriorEnabled = false,
            underlay = null,
            variants = new List<VariantDef> { baseline },
            activeVariantId = baseline.id,
        };
    }

    /// <summary>Exposed so the tests can read the builder's warnings, which Build discards.</summary>
    public static PlanBuilder Plan(string key)
    {
        switch (key)
        {
            case "studio_apartment": return Studio();
            case "apartment_2b1b":   return Apartment2B1B();
            case "apartment_5b4b":   return Apartment5B4B();
            case "house_2b1b":       return House2B1B();
            case "house_3b2b":       return House3B2B();
            case "house_5b4b":       return House5B4B();
            default: return null;
        }
    }

    // ===========================================================================================
    // 1. Studio apartment — 6.6 x 5.8 = 38.3 m²
    // ===========================================================================================

    private static PlanBuilder Studio()
    {
        var b = new PlanBuilder();

        b.Room("bath",    "Bathroom",          RoomType.Bathroom, 0.0f, 0.0f, 2.6f, 2.2f);
        b.Room("kitchen", "Kitchen",           RoomType.Kitchen,  2.6f, 0.0f, 2.4f, 2.2f);
        b.Room("entry",   "Entry",             RoomType.Entry,    5.0f, 0.0f, 1.6f, 2.2f);
        b.Room("living",  "Living / sleeping", RoomType.Living,   0.0f, 2.2f, 6.6f, 3.6f);

        b.ExteriorDoor("entry", PlanEdge.South, 0.5f, WIDE_DOOR);
        b.DoorBetween("bath", "living", DOOR, alongFraction: 0.78f);
        b.DoorBetween("kitchen", "living", 1.8f, OpeningKind.CasedOpening);
        b.DoorBetween("entry", "living", 1.0f, OpeningKind.CasedOpening);

        b.Window("living", PlanEdge.North, 0.3f);
        b.Window("living", PlanEdge.North, 0.7f);
        b.Window("living", PlanEdge.East, 0.5f);
        b.Window("kitchen", PlanEdge.South, 0.5f);
        b.Window("bath", PlanEdge.West, 0.5f, 0.61f, 0.61f, 1.37f);

        // Bathroom — tub in the west alcove, toilet and basin along the far wall.
        b.Free("bathtub", "bath", 0f, 0.6f);
        b.Against("toilet", "bath", PlanEdge.South, 0.8f);
        b.Against("sink_pedestal", "bath", PlanEdge.East, 0.55f);
        b.Mount("grab_bar_36", "bath", PlanEdge.West, 0.5f);
        b.Mount("grab_bar_24", "bath", PlanEdge.East, 0.18f);

        // Kitchen — a single run under the window, fridge on the end wall, north side left open so
        // the pass-through to the living room stays clear.
        b.Against("sink_base", "kitchen", PlanEdge.South, 0.25f);
        b.Against("range", "kitchen", PlanEdge.South, 0.75f);
        b.Against("refrigerator", "kitchen", PlanEdge.East, 0.72f);
        b.Mount("wall_cabinet", "kitchen", PlanEdge.South, 0.25f);
        b.Mount("wall_cabinet", "kitchen", PlanEdge.South, 0.75f);

        b.Against("wardrobe", "entry", PlanEdge.East, 0.5f);
        b.Mount("light_switch", "entry", PlanEdge.West, 0.15f);

        // Living / sleeping — bed at the far end, seating and dining toward the windows.
        b.Against("full_bed", "living", PlanEdge.West, 0.75f);
        b.Free("nightstand", "living", 0.34f, 0.93f);
        b.Against("sofa", "living", PlanEdge.North, 0.62f);
        b.Free("coffee_table", "living", 0.62f, 0.59f);
        b.Free("armchair", "living", 0.44f, 0.59f);
        b.Against("tv_stand", "living", PlanEdge.South, 0.10f);
        b.Against("dining_table", "living", PlanEdge.East, 0.35f);
        b.Mount("thermostat", "living", PlanEdge.West, 0.6f);
        b.Mount("outlet", "living", PlanEdge.South, 0.85f);

        return b;
    }

    // ===========================================================================================
    // 2. Apartment, 2 bed / 1 bath — 10.0 x 7.4 = 74.0 m²
    // ===========================================================================================

    private static PlanBuilder Apartment2B1B()
    {
        var b = new PlanBuilder();

        b.Room("living",  "Living room",      RoomType.Living,   0.0f, 0.0f, 4.4f, 4.6f);
        b.Room("kitchen", "Kitchen / dining", RoomType.Kitchen,  0.0f, 4.6f, 4.4f, 2.8f);
        b.Room("entry",   "Entry",            RoomType.Entry,    4.4f, 0.0f, 1.8f, 2.2f);
        b.Room("hall",    "Hall",             RoomType.Hall,     4.4f, 2.2f, 1.8f, 5.2f);
        b.Room("bed1",    "Bedroom 1",        RoomType.Bedroom,  6.2f, 0.0f, 3.8f, 3.7f);
        b.Room("bed2",    "Bedroom 2",        RoomType.Bedroom,  6.2f, 3.7f, 3.8f, 2.2f);
        b.Room("bath",    "Bathroom",         RoomType.Bathroom, 6.2f, 5.9f, 3.8f, 1.5f);

        b.ExteriorDoor("entry", PlanEdge.South, 0.5f, WIDE_DOOR);
        b.DoorBetween("living", "entry", 1.1f, OpeningKind.CasedOpening);
        b.DoorBetween("living", "hall", 1.0f, OpeningKind.CasedOpening, alongFraction: 0.3f);
        b.DoorBetween("kitchen", "hall", 0.9f, OpeningKind.CasedOpening);
        b.DoorBetween("hall", "bed1", DOOR, alongFraction: 0.35f);
        b.DoorBetween("hall", "bed2", DOOR, alongFraction: 0.5f);
        b.DoorBetween("hall", "bath", DOOR, alongFraction: 0.5f);

        b.Window("living", PlanEdge.South, 0.3f);
        b.Window("living", PlanEdge.West, 0.5f);
        b.Window("kitchen", PlanEdge.North, 0.5f);
        b.Window("bed1", PlanEdge.South, 0.5f);
        b.Window("bed1", PlanEdge.East, 0.5f);
        b.Window("bed2", PlanEdge.East, 0.5f);
        b.Window("bath", PlanEdge.North, 0.75f, 0.61f, 0.61f, 1.37f);

        b.Against("sofa", "living", PlanEdge.West, 0.55f);
        b.Against("tv_stand", "living", PlanEdge.East, 0.55f);
        b.Free("coffee_table", "living", 0.5f, 0.55f);
        b.Against("armchair", "living", PlanEdge.North, 0.2f);
        b.Mount("thermostat", "living", PlanEdge.East, 0.85f);

        b.Against("sink_base", "kitchen", PlanEdge.North, 0.2f);
        b.Against("range", "kitchen", PlanEdge.North, 0.5f);
        b.Against("refrigerator", "kitchen", PlanEdge.North, 0.82f);
        b.Against("base_cabinet", "kitchen", PlanEdge.West, 0.3f);
        b.Free("dining_table", "kitchen", 0.62f, 0.3f);
        b.Mount("wall_cabinet", "kitchen", PlanEdge.North, 0.35f);

        b.Against("full_bed", "bed1", PlanEdge.North, 0.35f);
        b.Free("nightstand", "bed1", 0.62f, 0.86f);
        b.Against("dresser", "bed1", PlanEdge.West, 0.35f);
        b.Mount("outlet", "bed1", PlanEdge.North, 0.7f);

        b.Against("twin_bed", "bed2", PlanEdge.East, 0.5f);
        b.Against("wardrobe", "bed2", PlanEdge.West, 0.3f);
        b.Free("nightstand", "bed2", 0.55f, 0.15f);

        b.Against("bathtub", "bath", PlanEdge.West, 0.5f);
        b.Against("toilet", "bath", PlanEdge.South, 0.55f);
        b.Against("vanity", "bath", PlanEdge.North, 0.8f);
        b.Mount("grab_bar_24", "bath", PlanEdge.South, 0.72f);
        b.Mount("grab_bar_36", "bath", PlanEdge.West, 0.5f);

        return b;
    }

    // ===========================================================================================
    // 3. Group home apartment, 5 bed / 4 bath — 16.5 x 10.0 = 165.0 m²
    //
    // South rooms z 0–4.4, corridor z 4.4–6.0, north rooms z 6.0–10.0; the west block is full-depth
    // common space. Every resident door opens onto the one corridor.
    // ===========================================================================================

    private static PlanBuilder Apartment5B4B()
    {
        var b = new PlanBuilder();

        b.Room("living",  "Living / common", RoomType.Living,   0.0f, 0.0f, 5.4f, 6.0f);
        b.Room("dining",  "Dining",          RoomType.Dining,   0.0f, 6.0f, 5.4f, 4.0f);
        b.Room("hall",    "Corridor",        RoomType.Hall,     5.4f, 4.4f, 11.1f, 1.6f);

        b.Room("bed1",    "Bedroom 1",       RoomType.Bedroom,  5.4f, 0.0f, 3.0f, 4.4f);
        b.Room("bath1",   "Bathroom 1",      RoomType.Bathroom, 8.4f, 0.0f, 1.8f, 2.2f);
        b.Room("bath2",   "Bathroom 2",      RoomType.Bathroom, 8.4f, 2.2f, 1.8f, 2.2f);
        b.Room("bed2",    "Bedroom 2",       RoomType.Bedroom, 10.2f, 0.0f, 3.2f, 4.4f);
        b.Room("bed3",    "Bedroom 3",       RoomType.Bedroom, 13.4f, 0.0f, 3.1f, 4.4f);

        b.Room("kitchen", "Kitchen",         RoomType.Kitchen,  5.4f, 6.0f, 3.6f, 4.0f);
        b.Room("bed4",    "Bedroom 4",       RoomType.Bedroom,  9.0f, 6.0f, 3.2f, 4.0f);
        b.Room("bath3",   "Bathroom 3",      RoomType.Bathroom, 12.2f, 6.0f, 1.8f, 2.0f);
        b.Room("bath4",   "Bathroom 4 — roll-in", RoomType.Bathroom, 12.2f, 8.0f, 1.8f, 2.0f);
        b.Room("bed5",    "Bedroom 5",       RoomType.Bedroom,  14.0f, 6.0f, 2.5f, 4.0f);

        b.ExteriorDoor("living", PlanEdge.West, 0.5f, WIDE_DOOR);
        b.DoorBetween("living", "dining", 2.0f, OpeningKind.CasedOpening);
        b.DoorBetween("dining", "kitchen", 1.6f, OpeningKind.CasedOpening);
        b.DoorBetween("living", "hall", 1.2f, OpeningKind.CasedOpening);

        b.DoorBetween("hall", "bed1", WIDE_DOOR, alongFraction: 0.4f);
        // Bathroom 1 sits behind bathroom 2, with no corridor frontage — so it is bedroom 1's
        // ensuite, which is what the accessible room wants anyway.
        b.DoorBetween("bed1", "bath1", WIDE_DOOR);
        b.DoorBetween("hall", "bath2", WIDE_DOOR);
        b.DoorBetween("hall", "bed2", WIDE_DOOR, alongFraction: 0.4f);
        b.DoorBetween("hall", "bed3", WIDE_DOOR, alongFraction: 0.4f);
        b.DoorBetween("hall", "bed4", WIDE_DOOR, alongFraction: 0.6f);
        b.DoorBetween("hall", "bath3", WIDE_DOOR);
        b.DoorBetween("bath3", "bath4", WIDE_DOOR);
        b.DoorBetween("hall", "bed5", WIDE_DOOR, alongFraction: 0.6f);
        b.DoorBetween("hall", "kitchen", WIDE_DOOR, alongFraction: 0.25f);

        b.Window("living", PlanEdge.West, 0.25f);
        b.Window("living", PlanEdge.West, 0.75f);
        b.Window("dining", PlanEdge.West, 0.5f);
        b.Window("dining", PlanEdge.North, 0.5f);
        b.Window("kitchen", PlanEdge.North, 0.5f);
        b.Window("bed1", PlanEdge.South, 0.5f);
        b.Window("bed2", PlanEdge.South, 0.5f);
        b.Window("bed3", PlanEdge.South, 0.5f);
        b.Window("bed3", PlanEdge.East, 0.5f);
        b.Window("bed4", PlanEdge.North, 0.5f);
        b.Window("bed5", PlanEdge.North, 0.5f);
        b.Window("bed5", PlanEdge.East, 0.5f);

        // Common rooms.
        b.Against("sofa", "living", PlanEdge.South, 0.5f);
        b.Against("recliner", "living", PlanEdge.West, 0.25f);
        b.Against("recliner", "living", PlanEdge.East, 0.25f);
        b.Against("armchair", "living", PlanEdge.East, 0.75f);
        b.Free("coffee_table", "living", 0.5f, 0.42f);
        b.Against("tv_stand", "living", PlanEdge.North, 0.5f);
        b.Mount("thermostat", "living", PlanEdge.South, 0.85f);

        b.Free("dining_table", "dining", 0.35f, 0.4f);
        b.Free("dining_table", "dining", 0.35f, 0.8f);
        b.Against("wardrobe", "dining", PlanEdge.East, 0.8f);

        b.Against("sink_base", "kitchen", PlanEdge.North, 0.2f);
        b.Against("range", "kitchen", PlanEdge.North, 0.5f);
        b.Against("refrigerator", "kitchen", PlanEdge.North, 0.8f);
        b.Against("base_cabinet", "kitchen", PlanEdge.West, 0.7f);
        b.Free("island", "kitchen", 0.5f, 0.42f);
        b.Mount("wall_cabinet", "kitchen", PlanEdge.North, 0.35f);

        // Bedroom 1 is the accessible room.
        b.Against("hospital_bed", "bed1", PlanEdge.West, 0.35f);
        b.Free("nightstand", "bed1", 0.85f, 0.12f);
        b.Against("wardrobe", "bed1", PlanEdge.East, 0.8f);
        b.Free("wheelchair", "bed1", 0.30f, 0.80f);
        b.Free("patient_lift", "bed1", 0.95f, 0.45f);

        Bedroom(b, "bed2", PlanEdge.West);
        Bedroom(b, "bed3", PlanEdge.West);
        Bedroom(b, "bed4", PlanEdge.West);
        Bedroom(b, "bed5", PlanEdge.South);   // 2.5 m wide — the bed has to run down the long axis

        Bathroom(b, "bath1", rollIn: false);
        Bathroom(b, "bath2", rollIn: false);
        Bathroom(b, "bath3", rollIn: false);
        Bathroom(b, "bath4", rollIn: true);

        b.Mount("handrail", "hall", PlanEdge.South, 0.2f);
        b.Mount("handrail", "hall", PlanEdge.South, 0.8f);
        b.Mount("handrail", "hall", PlanEdge.North, 0.3f);

        return b;
    }

    // ===========================================================================================
    // 4. House, 2 bed / 1 bath — 10.0 x 9.0 = 90.0 m²
    // ===========================================================================================

    private static PlanBuilder House2B1B()
    {
        var b = new PlanBuilder();

        b.Room("living",  "Living room", RoomType.Living,   0.0f, 0.0f, 4.8f, 4.4f);
        b.Room("kitchen", "Kitchen",     RoomType.Kitchen,  4.8f, 0.0f, 2.8f, 4.4f);
        b.Room("dining",  "Dining",      RoomType.Dining,   7.6f, 0.0f, 2.4f, 4.4f);
        b.Room("hall",    "Hall",        RoomType.Hall,     0.0f, 4.4f, 10.0f, 1.2f);
        b.Room("bed1",    "Bedroom 1",   RoomType.Bedroom,  0.0f, 5.6f, 4.0f, 3.4f);
        b.Room("bath",    "Bathroom",    RoomType.Bathroom, 4.0f, 5.6f, 2.4f, 3.4f);
        b.Room("laundry", "Laundry",     RoomType.Laundry,  6.4f, 5.6f, 1.2f, 3.4f);
        b.Room("bed2",    "Bedroom 2",   RoomType.Bedroom,  7.6f, 5.6f, 2.4f, 3.4f);

        b.ExteriorDoor("living", PlanEdge.South, 0.22f, WIDE_DOOR);
        b.DoorBetween("living", "kitchen", 1.2f, OpeningKind.CasedOpening, alongFraction: 0.35f);
        b.DoorBetween("kitchen", "dining", 1.2f, OpeningKind.CasedOpening, alongFraction: 0.35f);
        b.DoorBetween("living", "hall", 1.0f, OpeningKind.CasedOpening, alongFraction: 0.75f);
        b.DoorBetween("dining", "hall", 0.9f, OpeningKind.CasedOpening);
        b.DoorBetween("hall", "bed1", DOOR, alongFraction: 0.6f);
        b.DoorBetween("hall", "bath", DOOR, alongFraction: 0.35f);
        b.DoorBetween("hall", "laundry", DOOR);
        b.DoorBetween("hall", "bed2", DOOR, alongFraction: 0.4f);

        b.Window("living", PlanEdge.South, 0.65f);
        b.Window("living", PlanEdge.West, 0.5f);
        b.Window("kitchen", PlanEdge.South, 0.5f);
        b.Window("dining", PlanEdge.East, 0.5f);
        b.Window("bed1", PlanEdge.North, 0.5f);
        b.Window("bed1", PlanEdge.West, 0.5f);
        b.Window("bed2", PlanEdge.North, 0.5f);
        b.Window("bed2", PlanEdge.East, 0.5f);
        b.Window("bath", PlanEdge.North, 0.5f, 0.61f, 0.61f, 1.37f);

        b.Against("sofa", "living", PlanEdge.West, 0.55f);
        b.Against("tv_stand", "living", PlanEdge.East, 0.55f);
        b.Free("coffee_table", "living", 0.5f, 0.55f);
        b.Against("armchair", "living", PlanEdge.North, 0.25f);
        b.Mount("thermostat", "living", PlanEdge.North, 0.7f);

        b.Against("sink_base", "kitchen", PlanEdge.West, 0.25f);
        b.Against("range", "kitchen", PlanEdge.West, 0.65f);
        b.Against("refrigerator", "kitchen", PlanEdge.East, 0.2f);
        b.Against("base_cabinet", "kitchen", PlanEdge.East, 0.7f);
        b.Mount("wall_cabinet", "kitchen", PlanEdge.West, 0.45f);

        b.Free("dining_table", "dining", 0.5f, 0.35f);
        b.Against("wardrobe", "dining", PlanEdge.North, 0.5f);

        b.Against("full_bed", "bed1", PlanEdge.North, 0.4f);
        b.Free("nightstand", "bed1", 0.72f, 0.82f);
        b.Against("dresser", "bed1", PlanEdge.South, 0.75f);
        b.Against("twin_bed", "bed2", PlanEdge.North, 0.35f);
        b.Against("wardrobe", "bed2", PlanEdge.South, 0.7f);

        Bathroom(b, "bath", rollIn: false);

        b.Against("base_cabinet", "laundry", PlanEdge.North, 0.5f);

        return b;
    }

    // ===========================================================================================
    // 5. House, 3 bed / 2 bath — 12.5 x 10.0 = 125.0 m²
    // ===========================================================================================

    private static PlanBuilder House3B2B()
    {
        var b = new PlanBuilder();

        b.Room("living",  "Living room", RoomType.Living,   0.0f, 0.0f, 5.2f, 5.0f);
        // The entry is a vestibule so the dining room reaches back far enough to share a real wall
        // with the kitchen; at 2.6 m deep it met the kitchen at a 0.2 m corner and nothing fitted.
        b.Room("entry",   "Entry",       RoomType.Entry,    5.2f, 0.0f, 2.0f, 1.4f);
        b.Room("dining",  "Dining",      RoomType.Dining,   5.2f, 1.4f, 2.0f, 3.6f);
        b.Room("kitchen", "Kitchen",     RoomType.Kitchen,  7.2f, 0.0f, 2.8f, 3.6f);
        b.Room("laundry", "Laundry",     RoomType.Laundry,  7.2f, 3.6f, 2.8f, 1.4f);
        b.Room("bed3",    "Bedroom 3",   RoomType.Bedroom, 10.0f, 0.0f, 2.5f, 5.0f);
        b.Room("hall",    "Hall",        RoomType.Hall,     0.0f, 5.0f, 12.5f, 1.4f);
        b.Room("bed1",    "Bedroom 1",   RoomType.Bedroom,  0.0f, 6.4f, 4.4f, 3.6f);
        b.Room("bath1",   "Ensuite",     RoomType.Bathroom, 4.4f, 6.4f, 2.2f, 3.6f);
        b.Room("bath2",   "Bathroom 2",  RoomType.Bathroom, 6.6f, 6.4f, 2.0f, 3.6f);
        b.Room("bed2",    "Bedroom 2",   RoomType.Bedroom,  8.6f, 6.4f, 3.9f, 3.6f);

        b.ExteriorDoor("entry", PlanEdge.South, 0.5f, WIDE_DOOR);
        b.DoorBetween("entry", "living", 1.1f, OpeningKind.CasedOpening);
        b.DoorBetween("entry", "dining", 1.2f, OpeningKind.CasedOpening);
        b.DoorBetween("dining", "living", 1.2f, OpeningKind.CasedOpening);
        b.DoorBetween("kitchen", "dining", 1.0f, OpeningKind.CasedOpening);
        b.DoorBetween("kitchen", "laundry", DOOR);
        b.DoorBetween("laundry", "hall", DOOR, alongFraction: 0.3f);
        b.DoorBetween("hall", "bed3", DOOR, alongFraction: 0.5f);
        b.DoorBetween("hall", "living", 1.0f, OpeningKind.CasedOpening, alongFraction: 0.4f);
        b.DoorBetween("hall", "bed1", DOOR, alongFraction: 0.6f);
        b.DoorBetween("bed1", "bath1", DOOR, alongFraction: 0.7f);
        b.DoorBetween("hall", "bath2", DOOR);
        b.DoorBetween("hall", "bed2", DOOR, alongFraction: 0.35f);

        b.Window("living", PlanEdge.South, 0.35f);
        b.Window("living", PlanEdge.West, 0.5f);
        // The kitchen's only exterior wall is the front: north is the laundry, east is bedroom 3,
        // west is the dining room.
        b.Window("kitchen", PlanEdge.South, 0.5f);
        b.Window("bed3", PlanEdge.East, 0.35f);
        b.Window("bed3", PlanEdge.South, 0.5f);
        b.Window("bed1", PlanEdge.North, 0.5f);
        b.Window("bed1", PlanEdge.West, 0.5f);
        b.Window("bed2", PlanEdge.North, 0.5f);
        b.Window("bed2", PlanEdge.East, 0.5f);
        b.Window("bath1", PlanEdge.North, 0.5f, 0.61f, 0.61f, 1.37f);
        b.Window("bath2", PlanEdge.North, 0.5f, 0.61f, 0.61f, 1.37f);

        b.Against("sofa", "living", PlanEdge.West, 0.5f);
        b.Against("tv_stand", "living", PlanEdge.East, 0.35f);
        b.Free("coffee_table", "living", 0.45f, 0.5f);
        b.Against("armchair", "living", PlanEdge.North, 0.2f);
        b.Against("recliner", "living", PlanEdge.South, 0.75f);
        b.Mount("thermostat", "living", PlanEdge.North, 0.65f);

        b.Against("wardrobe", "entry", PlanEdge.East, 0.35f);
        b.Free("dining_table", "dining", 0.5f, 0.5f);

        b.Against("sink_base", "kitchen", PlanEdge.North, 0.25f);
        b.Against("range", "kitchen", PlanEdge.North, 0.7f);
        b.Against("refrigerator", "kitchen", PlanEdge.East, 0.25f);
        b.Against("base_cabinet", "kitchen", PlanEdge.South, 0.5f);
        b.Mount("wall_cabinet", "kitchen", PlanEdge.North, 0.45f);

        b.Against("base_cabinet", "laundry", PlanEdge.North, 0.3f);
        b.Against("base_cabinet", "laundry", PlanEdge.North, 0.75f);

        b.Against("full_bed", "bed1", PlanEdge.North, 0.35f);
        b.Free("nightstand", "bed1", 0.62f, 0.85f);
        b.Against("dresser", "bed1", PlanEdge.South, 0.7f);

        Bedroom(b, "bed2", PlanEdge.North);
        Bedroom(b, "bed3", PlanEdge.South);   // 2.5 m wide — the bed runs down the long axis

        Bathroom(b, "bath1", rollIn: false);
        Bathroom(b, "bath2", rollIn: false);

        return b;
    }

    // ===========================================================================================
    // 6. Assisted living house, 5 bed / 4 bath — 17.5 x 12.0 = 210.0 m²
    //
    // The corridor is 1.6 m so two wheelchairs can pass, and carries handrails both sides.
    // ===========================================================================================

    private static PlanBuilder House5B4B()
    {
        var b = new PlanBuilder();

        b.Room("living",  "Living / common",     RoomType.Living,   0.0f, 0.0f, 5.6f, 5.4f);
        b.Room("entry",   "Entry",               RoomType.Entry,    5.6f, 0.0f, 1.8f, 5.4f);
        b.Room("dining",  "Dining",              RoomType.Dining,   7.4f, 0.0f, 3.2f, 5.4f);
        b.Room("kitchen", "Kitchen",             RoomType.Kitchen, 10.6f, 0.0f, 3.0f, 5.4f);
        b.Room("bath4",   "Bathroom 4 — roll-in", RoomType.Bathroom, 13.6f, 0.0f, 1.8f, 2.6f);
        b.Room("laundry", "Laundry",             RoomType.Laundry, 13.6f, 2.6f, 1.8f, 2.8f);
        b.Room("bed5",    "Bedroom 5",           RoomType.Bedroom, 15.4f, 0.0f, 2.1f, 5.4f);

        b.Room("hall",    "Corridor",            RoomType.Hall,     0.0f, 5.4f, 17.5f, 1.6f);

        b.Room("bed1",    "Bedroom 1",           RoomType.Bedroom,  0.0f, 7.0f, 3.2f, 5.0f);
        b.Room("bath1",   "Bathroom 1 — roll-in", RoomType.Bathroom, 3.2f, 7.0f, 1.8f, 5.0f);
        b.Room("bed2",    "Bedroom 2",           RoomType.Bedroom,  5.0f, 7.0f, 3.2f, 5.0f);
        b.Room("bath2",   "Bathroom 2",          RoomType.Bathroom, 8.2f, 7.0f, 1.8f, 5.0f);
        b.Room("bed3",    "Bedroom 3",           RoomType.Bedroom, 10.0f, 7.0f, 3.2f, 5.0f);
        b.Room("bath3",   "Bathroom 3",          RoomType.Bathroom, 13.2f, 7.0f, 1.8f, 5.0f);
        b.Room("bed4",    "Bedroom 4",           RoomType.Bedroom, 15.0f, 7.0f, 2.5f, 5.0f);

        b.ExteriorDoor("entry", PlanEdge.South, 0.5f, WIDE_DOOR);
        b.DoorBetween("entry", "living", 1.6f, OpeningKind.CasedOpening);
        b.DoorBetween("entry", "dining", 1.6f, OpeningKind.CasedOpening);
        b.DoorBetween("dining", "kitchen", 1.6f, OpeningKind.CasedOpening);
        b.DoorBetween("entry", "hall", 1.6f, OpeningKind.CasedOpening);
        b.DoorBetween("living", "hall", 1.6f, OpeningKind.CasedOpening, alongFraction: 0.6f);
        b.DoorBetween("kitchen", "hall", WIDE_DOOR, alongFraction: 0.3f);
        b.DoorBetween("kitchen", "bath4", WIDE_DOOR);
        b.DoorBetween("kitchen", "laundry", WIDE_DOOR);
        b.DoorBetween("hall", "bed5", WIDE_DOOR);

        b.DoorBetween("hall", "bed1", WIDE_DOOR, alongFraction: 0.6f);
        b.DoorBetween("bed1", "bath1", WIDE_DOOR, alongFraction: 0.25f);
        b.DoorBetween("hall", "bed2", WIDE_DOOR, alongFraction: 0.4f);
        b.DoorBetween("hall", "bath2", WIDE_DOOR);
        b.DoorBetween("hall", "bed3", WIDE_DOOR, alongFraction: 0.6f);
        b.DoorBetween("hall", "bath3", WIDE_DOOR);
        b.DoorBetween("hall", "bed4", WIDE_DOOR, alongFraction: 0.4f);

        b.Window("living", PlanEdge.West, 0.3f);
        b.Window("living", PlanEdge.West, 0.7f);
        b.Window("living", PlanEdge.South, 0.5f);
        b.Window("dining", PlanEdge.South, 0.5f);
        b.Window("kitchen", PlanEdge.South, 0.5f);
        b.Window("bed5", PlanEdge.East, 0.35f);
        b.Window("bed5", PlanEdge.South, 0.5f);
        b.Window("bed1", PlanEdge.North, 0.5f);
        b.Window("bed1", PlanEdge.West, 0.5f);
        b.Window("bed2", PlanEdge.North, 0.5f);
        b.Window("bed3", PlanEdge.North, 0.5f);
        b.Window("bed4", PlanEdge.North, 0.5f);
        b.Window("bed4", PlanEdge.East, 0.5f);
        b.Window("bath1", PlanEdge.North, 0.5f, 0.61f, 0.61f, 1.37f);
        b.Window("bath3", PlanEdge.North, 0.5f, 0.61f, 0.61f, 1.37f);

        b.Against("sofa", "living", PlanEdge.South, 0.35f);
        b.Against("recliner", "living", PlanEdge.West, 0.3f);
        b.Against("recliner", "living", PlanEdge.West, 0.7f);
        b.Against("armchair", "living", PlanEdge.North, 0.25f);
        b.Free("coffee_table", "living", 0.5f, 0.42f);
        b.Against("tv_stand", "living", PlanEdge.North, 0.7f);
        b.Mount("thermostat", "living", PlanEdge.North, 0.5f);

        b.Against("wardrobe", "entry", PlanEdge.East, 0.2f);

        b.Free("dining_table", "dining", 0.4f, 0.28f);
        b.Free("dining_table", "dining", 0.4f, 0.72f);
        b.Against("base_cabinet", "dining", PlanEdge.East, 0.5f);

        b.Against("sink_base", "kitchen", PlanEdge.South, 0.25f);
        b.Against("range", "kitchen", PlanEdge.South, 0.7f);
        b.Against("refrigerator", "kitchen", PlanEdge.West, 0.75f);
        b.Against("base_cabinet", "kitchen", PlanEdge.North, 0.75f);
        b.Free("island", "kitchen", 0.45f, 0.55f);
        b.Mount("wall_cabinet", "kitchen", PlanEdge.South, 0.45f);

        b.Against("base_cabinet", "laundry", PlanEdge.North, 0.5f);

        // Bedroom 1 is the fully accessible room.
        b.Against("hospital_bed", "bed1", PlanEdge.West, 0.3f);
        b.Free("nightstand", "bed1", 0.85f, 0.1f);
        b.Against("wardrobe", "bed1", PlanEdge.East, 0.85f);
        b.Free("wheelchair", "bed1", 0.78f, 0.55f);
        b.Free("patient_lift", "bed1", 0.25f, 0.85f);

        Bedroom(b, "bed2", PlanEdge.West);
        Bedroom(b, "bed3", PlanEdge.West);
        Bedroom(b, "bed4", PlanEdge.South);   // 2.5 m and 2.1 m wide respectively — the beds have to
        Bedroom(b, "bed5", PlanEdge.South);   // run down the long axis of each room

        Bathroom(b, "bath1", rollIn: true);
        Bathroom(b, "bath2", rollIn: false);
        Bathroom(b, "bath3", rollIn: false);
        Bathroom(b, "bath4", rollIn: true);

        b.Mount("handrail", "hall", PlanEdge.South, 0.15f);
        b.Mount("handrail", "hall", PlanEdge.South, 0.5f);
        b.Mount("handrail", "hall", PlanEdge.South, 0.85f);
        b.Mount("handrail", "hall", PlanEdge.North, 0.3f);
        b.Mount("handrail", "hall", PlanEdge.North, 0.7f);

        return b;
    }

    // ===========================================================================================
    // Shared room recipes — the bare necessities, identical wherever a room plays the same role.
    // ===========================================================================================

    /// <summary>
    /// A bed with its head against <paramref name="bedWall"/>, so pick the wall PERPENDICULAR to the
    /// room's long axis — a 2.03 m bed laid across a 2.5 m room leaves nothing either side of it.
    /// The wardrobe goes on a side wall rather than beside the dresser, because dresser + wardrobe is
    /// 2.24 m and the narrower bedrooms here are 2.1 m wide.
    /// </summary>
    private static void Bedroom(PlanBuilder b, string room, PlanEdge bedWall)
    {
        b.Against("twin_bed", room, bedWall, 0.3f);
        b.Against("nightstand", room, bedWall, 0.72f);
        b.Against("dresser", room, Opposite(bedWall), 0.3f);
        b.Against("wardrobe", room, SideOf(bedWall), 0.6f);
        b.Mount("outlet", room, bedWall, 0.55f);
    }

    // Bathing fixture in an alcove across the far wall, toilet and basin along the near wall at
    // opposite ends. That is the only arrangement that fits the 1.8 m wide bathrooms in the two care
    // plans, and it reads correctly in the roomier ones too.
    //
    // No shower_seat: these render as massing boxes, and a seat inside a shower would be one box
    // buried in another. The grab bars carry the accessibility story instead.
    private static void Bathroom(PlanBuilder b, string room, bool rollIn)
    {
        b.Against(rollIn ? "roll_in_shower" : "bathtub", room, PlanEdge.North, 0.5f, alongWall: true);
        b.Mount("grab_bar_36", room, PlanEdge.North, 0.5f);
        if (rollIn) b.Mount("grab_bar_36", room, PlanEdge.West, 0.85f);

        b.Against("toilet", room, PlanEdge.South, 0.25f);
        b.Against("sink_pedestal", room, PlanEdge.South, 0.78f);
        b.Mount("grab_bar_24", room, PlanEdge.South, 0.25f);
    }

    private static PlanEdge Opposite(PlanEdge e)
    {
        switch (e)
        {
            case PlanEdge.South: return PlanEdge.North;
            case PlanEdge.North: return PlanEdge.South;
            case PlanEdge.West:  return PlanEdge.East;
            default:             return PlanEdge.West;
        }
    }

    /// <summary>A wall perpendicular to this one — where something goes when both facing walls are taken.</summary>
    private static PlanEdge SideOf(PlanEdge e)
        => e == PlanEdge.West || e == PlanEdge.East ? PlanEdge.North : PlanEdge.East;
}
