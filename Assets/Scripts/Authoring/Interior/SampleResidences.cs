using System.Collections.Generic;

// Six built-in sample dwellings, authored as data.
//
// The library is empty on a fresh install and the only way in is "import a floor plan and calibrate
// it": the hardest step of the workflow. These exist so the first launch lands on something you can
// walk through, measure and compare instead of a blank ground pad.
//
// Each is a single story with a locked "Existing" baseline and no proposal variants: branching one
// is what the Design options rail is for. Every plan is complete enough to live in. Somewhere to
// sleep, cook, wash and sit, for the stated number of residents. The two five-bedroom plans are the
// shared-residence / assisted-living cases the tool is aimed at, so their doors are all 36" and step-free
// and their bathrooms are roll-in.
//
// Geometry is derived by PlanBuilder from the room rectangles below; see that file for why nothing
// here names a wall id or an offset.
//
// Each plan is also OCCUPIED, by the number of people its blurb advertises. The schedules are written
// out in full rather than generated from a template, because the interesting part is where they do not
// line up: which two bedrooms share a bathroom, and therefore who is queueing at half past seven. Every
// day must cover all 1440 minutes with no overlap, which OccupancyModel.Validate checks and the sample
// tests assert.
public static class SampleResidences
{
    /// <summary>
    /// Bump this whenever a plan below changes: a room moves, an opening moves, a recipe places
    /// something differently, or PlanBuilder starts deriving different geometry from the same input.
    ///
    /// It exists because seeding is one-shot. `ResidenceSettings.samplesSeeded` deliberately stops the
    /// seeder ever running twice (archiving a sample has to keep it archived), so without a stamp
    /// every improvement made here is invisible on any machine that has already launched the app
    /// once. That is not hypothetical: the opening-avoidance work landed after the first seed, and
    /// the six residences sitting in the library still had a wardrobe across a cased opening, a bath
    /// across a bathroom door and a dresser across a bedroom door long after the tests were green.
    ///
    /// Every installed sample carries the generation it was built from (ResidenceDoc.sampleGeneration),
    /// and SampleResidenceInstaller.RefreshStaleSamples re-installs any that has fallen behind AND that
    /// nobody has started working on. See SampleRefresh for that second half.
    /// </summary>
    public const int Generation = 3;

    public struct Spec
    {
        public string key;
        public string displayName;
        public string blurb;      // the library row's second line
        public string summary;    // the baseline variant's description
    }

    // 36": the width the two care-setting samples use throughout.
    private const float WIDE_DOOR = 0.914f;
    // 32": a standard interior door.
    private const float DOOR = ResidenceConventions.DEFAULT_DOOR_WIDTH;

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
            displayName = "Apartment, 2 bed 1 bath",
            blurb = "74 m² · 2 bed / 1 bath · 2 to 3 people",
            summary = "A two-bedroom apartment off a central hall, with the living and kitchen "
                    + "sharing the front of the plan.",
        },
        new Spec
        {
            key = "apartment_5b4b",
            displayName = "Shared home apartment, 5 bed 4 bath",
            blurb = "165 m² · 5 bed / 4 bath · 5 residents",
            summary = "A five-resident shared home: bedrooms off a wide central corridor, four "
                    + "bathrooms, and shared living, dining and kitchen. All doors are 36\" and "
                    + "step-free; bathroom 4 is roll-in.",
        },
        new Spec
        {
            key = "house_2b1b",
            displayName = "House, 2 bed 1 bath",
            blurb = "90 m² · 2 bed / 1 bath · 2 people",
            summary = "A small single-story house: living, kitchen and dining across the front, "
                    + "two bedrooms, a bathroom and a laundry off the rear hall.",
        },
        new Spec
        {
            key = "house_3b2b",
            displayName = "House, 3 bed 2 bath",
            blurb = "125 m² · 3 bed / 2 bath · 4 people",
            summary = "A family house with three bedrooms, an ensuite off the main bedroom, a "
                    + "second bathroom off the hall, and a separate laundry.",
        },
        new Spec
        {
            key = "house_5b4b",
            displayName = "Assisted living house, 5 bed 4 bath",
            blurb = "210 m² · 5 bed / 4 bath · 5 to 6 residents",
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
    /// Builds the sample as a complete ResidenceDoc with deterministic ids. The installer replaces
    /// <c>id</c> with a fresh GUID so the same sample can be added more than once.
    /// </summary>
    public static ResidenceDoc Build(string key)
    {
        if (!TryGetSpec(key, out var spec)) return null;

        var builder = Plan(key);
        if (builder == null) return null;

        // Occupants resolve against the built level: their rooms and the items they stand beside only
        // have ids once the geometry exists, so the level is built first and handed back in.
        var level = builder.Build();

        var baseline = new VariantDef
        {
            id = key + "_existing",
            name = "Existing",
            description = spec.summary,
            isBaseline = true,
            // Locked for the same reason ResidenceStore.Create locks it: the baseline is the record of how
            // the residence is. Design options -> Unlock is one click for anyone who wants to edit it here.
            locked = true,
            // Ours, not the user's. SampleRefresh reads this to tell a proposal the app shipped from
            // one someone branched, which is what lets a sample carry more than one variant and still
            // be refreshable. See the header on SampleRefresh.Evaluate.
            fromSample = true,
            levels = new List<LevelDef> { level },
            occupants = builder.BuildOccupants(level),
        };

        var variants = new List<VariantDef> { baseline };

        var technology = TechnologyProposal(key, baseline);
        if (technology != null) variants.Add(technology);

        return new ResidenceDoc
        {
            id = key,
            name = spec.displayName,
            version = 1,
            schemaVersion = ResidenceSchema.CURRENT,
            tags = new List<string> { "sample" },
            favorite = false,
            exteriorEnabled = false,
            underlay = null,
            variants = variants,
            // Always the baseline, even where there is a proposal beside it. A sample opens on the
            // residence as it is; the proposal is one click away in the mode band, and Compare is what it
            // is for.
            activeVariantId = baseline.id,
            sampleKey = key,
            sampleGeneration = Generation,
        };
    }

    // ---------------------------------------------------------------------------------------
    // The smart home proposal the two care samples ship
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The samples that ship a second, locked variant: a full smart home package against the bare
    /// baseline.
    /// </summary>
    /// <remarks>
    /// Only the two five-bedroom care settings, and that is the whole argument of
    /// SmartHomeReport.pdf: §2.2.2's group homes, §5.3's CFS family residences, five residents and the
    /// Direct Support Professionals whose burden §2.2.4 is about. A studio with one occupant is where
    /// someone should see the tool's ordinary self first, so the other four stay bare.
    ///
    /// It exists at all because the alternative (ship the sensors and let a user find them) has no
    /// before. ResidenceViz is a before/after tool, and "what does this residence look like with the technology
    /// in it, against how it is now" is a question it can already answer completely, for free, the
    /// moment there are two variants to compare.
    /// </remarks>
    private static bool ShipsTechnology(string key)
        => key == "apartment_5b4b" || key == "house_5b4b";

    private static VariantDef TechnologyProposal(string key, VariantDef baseline)
    {
        if (!ShipsTechnology(key)) return null;

        // A deep copy PRESERVING every id, exactly as NewProposalFrom does for a user's proposal,
        // which is what makes VariantDiff report this as "42 devices added" rather than as an entire
        // residence removed and an entire residence added.
        var level = VariantRevert.Copy(baseline.levels[0]);

        var proposal = new VariantDef
        {
            id = key + "_smart_home",
            name = "Smart home package",
            description =
                "The sensing layer from the Center for Family Support's technology assessment, sized "
                + "for this residence: every way out watched, movement sensing through the corridor and "
                + "every room, a pad under each bed, the stove, water at each fixture, a pendant for "
                + "each resident, and spoken prompts where they will be heard. Compare it against "
                + "Existing to see what it costs and what it would catch.",
            basedOnVariantId = baseline.id,
            isBaseline = false,
            // Locked like the baseline. A sample is something to look at and branch from, and an
            // unlocked variant is the signal SampleRefresh reads as "someone is working on this".
            locked = true,
            fromSample = true,
            levels = new List<LevelDef> { level },
            occupants = CopyRoster(baseline.occupants),
        };

        // Recommend reads the roster for the worn devices, so it runs against the PROPOSAL's own
        // occupants: a pendant hosts on an occupant id, and pointing it at the baseline's roster
        // would leave five devices referencing people in a different variant.
        level.sensors = SensorPackages.Recommend(level, proposal, SensorPackages.Tier.Care, "sn_");

        return proposal;
    }

    private static List<OccupantDef> CopyRoster(List<OccupantDef> roster)
    {
        if (roster == null) return null;
        var copy = new List<OccupantDef>(roster.Count);
        foreach (var person in roster) copy.Add(VariantRevert.Copy(person));
        return copy;
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
    // 1. Studio apartment: 6.6 x 5.8 = 38.3 m²
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

        // Bathroom. Tub in the west alcove, toilet and basin along the far wall.
        b.Free("bathtub", "bath", 0f, 0.6f);
        b.Against("toilet", "bath", PlanEdge.South, 0.8f);
        b.Against("sink_pedestal", "bath", PlanEdge.East, 0.55f);
        b.Mount("grab_bar_36", "bath", PlanEdge.West, 0.5f);
        b.Mount("grab_bar_24", "bath", PlanEdge.East, 0.18f);

        // Kitchen: a single run under the window, fridge on the end wall, north side left open so
        // the pass-through to the living room stays clear.
        b.Against("sink_base", "kitchen", PlanEdge.South, 0.25f);
        b.Against("range", "kitchen", PlanEdge.South, 0.75f);
        b.Against("refrigerator", "kitchen", PlanEdge.East, 0.72f);
        b.Mount("wall_cabinet", "kitchen", PlanEdge.South, 0.25f);
        b.Mount("wall_cabinet", "kitchen", PlanEdge.South, 0.75f);

        b.Against("wardrobe", "entry", PlanEdge.East, 0.5f);
        b.Mount("light_switch", "entry", PlanEdge.West, 0.15f);

        // Living / sleeping. Bed at the far end, seating and dining toward the windows.
        b.Against("full_bed", "living", PlanEdge.West, 0.75f);
        b.Free("nightstand", "living", 0.34f, 0.93f);
        b.Against("sofa", "living", PlanEdge.North, 0.62f);
        b.Free("coffee_table", "living", 0.62f, 0.59f);
        b.Free("armchair", "living", 0.44f, 0.59f);
        b.Against("tv_stand", "living", PlanEdge.South, 0.10f);
        b.Against("dining_table", "living", PlanEdge.East, 0.35f);
        b.Mount("thermostat", "living", PlanEdge.West, 0.6f);
        b.Mount("outlet", "living", PlanEdge.South, 0.85f);

        // One resident, one of everything: the simplest possible day, and the one that shows what a
        // timeline is before any of the contention in the larger plans matters.
        b.Person("maya", "Maya");
        b.Does("maya", ActivityKind.Sleep,   "22:30", "7:00",  "living", anchor: "full_bed");
        b.Does("maya", ActivityKind.Hygiene, "7:00",  "7:45",  "bath", anchor: "sink_pedestal");
        b.Does("maya", ActivityKind.Eat,     "7:45",  "8:15",  "living", anchor: "dining_table", label: "Breakfast");
        b.Does("maya", ActivityKind.Out,     "8:15",  "17:30", null,     label: "At work");
        b.Does("maya", ActivityKind.Cook,    "17:30", "18:30", "kitchen", anchor: "range");
        b.Does("maya", ActivityKind.Eat,     "18:30", "19:15", "living", anchor: "dining_table", label: "Dinner");
        b.Does("maya", ActivityKind.Relax,   "19:15", "22:30", "living");

        return b;
    }

    // ===========================================================================================
    // 2. Apartment, 2 bed / 1 bath: 10.0 x 7.4 = 74.0 m²
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
        // 0.21 rather than 0.3 leaves a 1.4 m run at the north end of the living room's east wall,
        // which is what the TV stand needs; at 0.3 the opening left only 1.18 m and nothing fitted.
        b.DoorBetween("living", "hall", 1.0f, OpeningKind.CasedOpening, alongFraction: 0.21f);
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
        // North, not West: the west wall is the one with the door in it, and a 1.02 m wardrobe does not
        // fit either of the 0.69 m stubs the door leaves.
        b.Against("wardrobe", "bed2", PlanEdge.North, 0.3f);
        b.Free("nightstand", "bed2", 0.55f, 0.15f);

        // The bathroom is 3.8 x 1.5, so a 1.52 m tub only fits ACROSS it. It goes at the FAR end: the
        // door is in the west wall and spans almost the room's whole depth, so anything in the
        // south-west corner is standing in the doorway.
        b.Against("bathtub", "bath", PlanEdge.South, 1.0f, alongWall: true);
        b.Against("toilet", "bath", PlanEdge.South, 0.35f);
        b.Against("vanity", "bath", PlanEdge.North, 0.8f);
        b.Mount("grab_bar_24", "bath", PlanEdge.South, 0.35f);
        b.Mount("grab_bar_36", "bath", PlanEdge.South, 1.0f);

        // Two people, one bathroom. Their morning slots run back to back on purpose: this is the
        // smallest plan where the timeline says something the floor plan does not.
        b.Person("dan", "Dan");
        b.Does("dan", ActivityKind.Sleep,   "22:45", "6:45",  "bed1", anchor: "full_bed");
        b.Does("dan", ActivityKind.Hygiene, "6:45",  "7:20",  "bath", anchor: "vanity");
        b.Does("dan", ActivityKind.Eat,     "7:20",  "7:50",  "kitchen", anchor: "dining_table", label: "Breakfast");
        b.Does("dan", ActivityKind.Out,     "7:50",  "17:45", null,      label: "At work");
        b.Does("dan", ActivityKind.Cook,    "17:45", "18:30", "kitchen", anchor: "range");
        b.Does("dan", ActivityKind.Eat,     "18:30", "19:15", "kitchen", anchor: "dining_table", label: "Dinner");
        b.Does("dan", ActivityKind.Relax,   "19:15", "22:45", "living");

        b.Person("priya", "Priya");
        b.Does("priya", ActivityKind.Sleep,   "23:15", "7:20",  "bed2", anchor: "twin_bed");
        b.Does("priya", ActivityKind.Hygiene, "7:20",  "7:55",  "bath", anchor: "vanity");
        b.Does("priya", ActivityKind.Eat,     "7:55",  "8:25",  "kitchen", anchor: "dining_table", label: "Breakfast");
        b.Does("priya", ActivityKind.Out,     "8:25",  "18:15", null,      label: "At work");
        b.Does("priya", ActivityKind.Relax,   "18:15", "19:15", "living");
        b.Does("priya", ActivityKind.Eat,     "19:15", "20:00", "kitchen", anchor: "dining_table", label: "Dinner");
        b.Does("priya", ActivityKind.Relax,   "20:00", "23:15", "living");

        return b;
    }

    // ===========================================================================================
    // 3. Shared home apartment, 5 bed / 4 bath: 16.5 x 10.0 = 165.0 m²
    //
    // South rooms z 0-4.4, corridor z 4.4-6.0, north rooms z 6.0-10.0; the west block is full-depth
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
        b.Room("bath4",   "Bathroom 4, roll-in", RoomType.Bathroom, 12.2f, 8.0f, 1.8f, 2.0f);
        b.Room("bed5",    "Bedroom 5",       RoomType.Bedroom,  14.0f, 6.0f, 2.5f, 4.0f);

        b.ExteriorDoor("living", PlanEdge.West, 0.5f, WIDE_DOOR);
        b.DoorBetween("living", "dining", 2.0f, OpeningKind.CasedOpening);
        b.DoorBetween("dining", "kitchen", 1.6f, OpeningKind.CasedOpening);
        b.DoorBetween("living", "hall", 1.2f, OpeningKind.CasedOpening);

        b.DoorBetween("hall", "bed1", WIDE_DOOR, alongFraction: 0.4f);
        // Bathroom 1 sits behind bathroom 2, with no corridor frontage, so it is bedroom 1's
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
        // Parked in the gap between the foot of the bed and the wardrobe, clear of the ensuite door
        // that 0.95 reached across. The gap is only 1.02 m deep, so the lift has to sit inboard of the
        // wardrobe rather than beside it.
        b.Free("patient_lift", "bed1", 0.62f, 0.6f);

        Bedroom(b, "bed2", PlanEdge.West);
        Bedroom(b, "bed3", PlanEdge.West);
        Bedroom(b, "bed4", PlanEdge.West);
        Bedroom(b, "bed5", PlanEdge.South);   // 2.5 m wide: the bed has to run down the long axis

        Bathroom(b, "bath1", rollIn: false);
        Bathroom(b, "bath2", rollIn: false);
        Bathroom(b, "bath3", rollIn: false);
        Bathroom(b, "bath4", rollIn: true);

        b.Mount("handrail", "hall", PlanEdge.South, 0.2f);
        b.Mount("handrail", "hall", PlanEdge.South, 0.8f);
        b.Mount("handrail", "hall", PlanEdge.North, 0.3f);

        // Five residents. Bathrooms 2 and 3 each serve two bedrooms, so those mornings are queued
        // rather than simultaneous, which is the thing a plan drawing cannot show you.
        b.Person("alice", "Alice", usesWheelchair: true, note: "Bedroom 1 with ensuite; uses a lift for transfers.");
        b.Does("alice", ActivityKind.Sleep,   "21:30", "7:00",  "bed1", anchor: "hospital_bed");
        b.Does("alice", ActivityKind.Care,    "7:00",  "8:00",  "bed1", anchor: "hospital_bed", label: "Morning care");
        b.Does("alice", ActivityKind.Hygiene, "8:00",  "8:45",  "bath1", anchor: "sink_pedestal");
        b.Does("alice", ActivityKind.Eat,     "8:45",  "9:30",  "dining", anchor: "dining_table", label: "Breakfast");
        b.Does("alice", ActivityKind.Relax,   "9:30",  "11:30", "living");
        b.Does("alice", ActivityKind.Eat,     "11:30", "12:15", "dining", anchor: "dining_table", label: "Lunch");
        b.Does("alice", ActivityKind.Relax,   "12:15", "14:30", "bed1",   label: "Rest");
        b.Does("alice", ActivityKind.Relax,   "14:30", "17:30", "living");
        b.Does("alice", ActivityKind.Eat,     "17:30", "18:30", "dining", anchor: "dining_table", label: "Dinner");
        b.Does("alice", ActivityKind.Relax,   "18:30", "20:30", "living");
        b.Does("alice", ActivityKind.Care,    "20:30", "21:30", "bed1",   label: "Evening care");

        b.Person("bernard", "Bernard", note: "Day program four days a week.");
        b.Does("bernard", ActivityKind.Sleep,   "22:00", "6:30",  "bed2", anchor: "twin_bed");
        b.Does("bernard", ActivityKind.Hygiene, "6:30",  "7:15",  "bath2", anchor: "sink_pedestal");
        b.Does("bernard", ActivityKind.Relax,   "7:15",  "8:00",  "bed2");
        b.Does("bernard", ActivityKind.Eat,     "8:00",  "8:45",  "dining", anchor: "dining_table", label: "Breakfast");
        b.Does("bernard", ActivityKind.Relax,   "8:45",  "9:15",  "living");
        b.Does("bernard", ActivityKind.Out,     "9:15",  "15:00", null,     label: "Day program");
        b.Does("bernard", ActivityKind.Relax,   "15:00", "17:30", "living");
        b.Does("bernard", ActivityKind.Eat,     "17:30", "18:30", "dining", anchor: "dining_table", label: "Dinner");
        b.Does("bernard", ActivityKind.Relax,   "18:30", "20:45", "living");
        b.Does("bernard", ActivityKind.Hygiene, "20:45", "21:30", "bath2", anchor: "sink_pedestal");
        b.Does("bernard", ActivityKind.Relax,   "21:30", "22:00", "bed2");

        b.Person("carol", "Carol", note: "Helps with the midday meal.");
        b.Does("carol", ActivityKind.Sleep,   "22:30", "7:15",  "bed3", anchor: "twin_bed");
        b.Does("carol", ActivityKind.Hygiene, "7:15",  "8:00",  "bath2", anchor: "sink_pedestal");
        b.Does("carol", ActivityKind.Eat,     "8:00",  "8:45",  "dining", anchor: "dining_table", label: "Breakfast");
        b.Does("carol", ActivityKind.Relax,   "8:45",  "9:30",  "bed3");
        b.Does("carol", ActivityKind.Cook,    "9:30",  "11:00", "kitchen", anchor: "range");
        b.Does("carol", ActivityKind.Eat,     "11:00", "12:00", "dining", anchor: "dining_table", label: "Lunch");
        b.Does("carol", ActivityKind.Relax,   "12:00", "15:00", "living");
        b.Does("carol", ActivityKind.Relax,   "15:00", "17:00", "bed3",   label: "Rest");
        b.Does("carol", ActivityKind.Eat,     "17:00", "18:30", "dining", anchor: "dining_table", label: "Dinner");
        b.Does("carol", ActivityKind.Relax,   "18:30", "21:45", "living");
        b.Does("carol", ActivityKind.Hygiene, "21:45", "22:30", "bath2", anchor: "sink_pedestal");

        b.Person("dinah", "Dinah");
        b.Does("dinah", ActivityKind.Sleep,   "22:15", "6:45",  "bed4", anchor: "twin_bed");
        b.Does("dinah", ActivityKind.Hygiene, "6:45",  "7:30",  "bath3", anchor: "sink_pedestal");
        b.Does("dinah", ActivityKind.Eat,     "7:30",  "8:15",  "dining", anchor: "dining_table", label: "Breakfast");
        b.Does("dinah", ActivityKind.Relax,   "8:15",  "9:00",  "living");
        b.Does("dinah", ActivityKind.Out,     "9:00",  "15:30", null,     label: "Work placement");
        b.Does("dinah", ActivityKind.Relax,   "15:30", "17:15", "living");
        b.Does("dinah", ActivityKind.Eat,     "17:15", "18:30", "dining", anchor: "dining_table", label: "Dinner");
        b.Does("dinah", ActivityKind.Relax,   "18:30", "21:30", "living");
        b.Does("dinah", ActivityKind.Hygiene, "21:30", "22:15", "bath3", anchor: "sink_pedestal");

        b.Person("ellis", "Ellis", note: "Uses the roll-in shower in bathroom 4.");
        b.Does("ellis", ActivityKind.Sleep,   "21:45", "7:30",  "bed5", anchor: "twin_bed");
        b.Does("ellis", ActivityKind.Hygiene, "7:30",  "8:15",  "bath4", anchor: "sink_pedestal");
        b.Does("ellis", ActivityKind.Eat,     "8:15",  "9:00",  "dining", anchor: "dining_table", label: "Breakfast");
        b.Does("ellis", ActivityKind.Relax,   "9:00",  "11:30", "living");
        b.Does("ellis", ActivityKind.Eat,     "11:30", "12:30", "dining", anchor: "dining_table", label: "Lunch");
        b.Does("ellis", ActivityKind.Relax,   "12:30", "15:00", "bed5",   label: "Rest");
        b.Does("ellis", ActivityKind.Relax,   "15:00", "17:30", "living");
        b.Does("ellis", ActivityKind.Eat,     "17:30", "18:45", "dining", anchor: "dining_table", label: "Dinner");
        b.Does("ellis", ActivityKind.Relax,   "18:45", "21:00", "living");
        b.Does("ellis", ActivityKind.Hygiene, "21:00", "21:45", "bath4", anchor: "sink_pedestal");

        return b;
    }

    // ===========================================================================================
    // 4. House, 2 bed / 1 bath: 10.0 x 9.0 = 90.0 m²
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
        // A sideboard, not a wardrobe. The dining room's north wall is the cased opening to the hall,
        // and a 1.83 m wardrobe stood right across it. Besides being odd furniture for a dining room.
        b.Against("base_cabinet", "dining", PlanEdge.East, 0.5f);

        b.Against("full_bed", "bed1", PlanEdge.North, 0.4f);
        b.Free("nightstand", "bed1", 0.72f, 0.82f);
        b.Against("dresser", "bed1", PlanEdge.South, 0.75f);
        b.Against("twin_bed", "bed2", PlanEdge.North, 0.35f);
        b.Against("wardrobe", "bed2", PlanEdge.South, 0.7f);

        Bathroom(b, "bath", rollIn: false);

        b.Against("base_cabinet", "laundry", PlanEdge.North, 0.5f);

        b.Person("ruth", "Ruth");
        b.Does("ruth", ActivityKind.Sleep,   "22:30", "6:30",  "bed1", anchor: "full_bed");
        b.Does("ruth", ActivityKind.Hygiene, "6:30",  "7:10",  "bath", anchor: "sink_pedestal");
        b.Does("ruth", ActivityKind.Eat,     "7:10",  "7:40",  "dining", anchor: "dining_table", label: "Breakfast");
        b.Does("ruth", ActivityKind.Out,     "7:40",  "16:30", null,     label: "At work");
        b.Does("ruth", ActivityKind.Relax,   "16:30", "17:30", "living");
        b.Does("ruth", ActivityKind.Cook,    "17:30", "18:30", "kitchen", anchor: "range");
        b.Does("ruth", ActivityKind.Eat,     "18:30", "19:15", "dining", anchor: "dining_table", label: "Dinner");
        b.Does("ruth", ActivityKind.Relax,   "19:15", "22:30", "living");

        b.Person("tom", "Tom");
        b.Does("tom", ActivityKind.Sleep,   "23:00", "7:10",  "bed2", anchor: "twin_bed");
        b.Does("tom", ActivityKind.Hygiene, "7:10",  "7:45",  "bath", anchor: "sink_pedestal");
        b.Does("tom", ActivityKind.Eat,     "7:45",  "8:15",  "dining", anchor: "dining_table", label: "Breakfast");
        b.Does("tom", ActivityKind.Out,     "8:15",  "18:30", null,     label: "At work");
        b.Does("tom", ActivityKind.Eat,     "18:30", "19:15", "dining", anchor: "dining_table", label: "Dinner");
        b.Does("tom", ActivityKind.Relax,   "19:15", "20:30", "living");
        b.Does("tom", ActivityKind.Hygiene, "20:30", "21:15", "bath", anchor: "sink_pedestal");
        b.Does("tom", ActivityKind.Relax,   "21:15", "23:00", "living");

        return b;
    }

    // ===========================================================================================
    // 5. House, 3 bed / 2 bath: 12.5 x 10.0 = 125.0 m²
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
        // Near the corner, not a third of the way along: the laundry's north wall has to carry two
        // 0.91 m cabinets as well, and 0.3 split it into stubs that took neither.
        b.DoorBetween("laundry", "hall", DOOR, alongFraction: 0.15f);
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
        // Moved out of the entry: that vestibule is 2.0 x 1.4 with three openings in it, so a 1.02 m
        // wardrobe stood across one of them wherever it went. Bedroom 1 was the room without one.
        b.Against("wardrobe", "bed1", PlanEdge.East, 0.3f);

        Bedroom(b, "bed2", PlanEdge.North);
        Bedroom(b, "bed3", PlanEdge.South);   // 2.5 m wide: the bed runs down the long axis

        Bathroom(b, "bath1", rollIn: false);
        Bathroom(b, "bath2", rollIn: false);

        // Four people: two adults sharing bedroom 1 and its ensuite, two teenagers sharing bathroom 2.
        // Both adults ARE anchored to the bed: co-anchored people now spread along the item's width, so
        // a couple reads as two markers side by side on it rather than one capsule inside the other.
        b.Person("ana", "Ana");
        b.Does("ana", ActivityKind.Sleep,   "22:45", "6:15",  "bed1", anchor: "full_bed");
        b.Does("ana", ActivityKind.Hygiene, "6:15",  "6:50",  "bath1", anchor: "sink_pedestal");
        b.Does("ana", ActivityKind.Cook,    "6:50",  "7:30",  "kitchen", anchor: "range");
        b.Does("ana", ActivityKind.Eat,     "7:30",  "8:00",  "dining", anchor: "dining_table", label: "Breakfast");
        b.Does("ana", ActivityKind.Out,     "8:00",  "17:00", null,     label: "At work");
        b.Does("ana", ActivityKind.Cook,    "17:00", "18:15", "kitchen", anchor: "range");
        b.Does("ana", ActivityKind.Eat,     "18:15", "19:00", "dining", anchor: "dining_table", label: "Dinner");
        b.Does("ana", ActivityKind.Relax,   "19:00", "22:45", "living");

        b.Person("marco", "Marco");
        b.Does("marco", ActivityKind.Sleep,   "23:15", "6:50",  "bed1", anchor: "full_bed");
        b.Does("marco", ActivityKind.Hygiene, "6:50",  "7:25",  "bath1", anchor: "sink_pedestal");
        b.Does("marco", ActivityKind.Eat,     "7:25",  "8:00",  "dining", anchor: "dining_table", label: "Breakfast");
        b.Does("marco", ActivityKind.Out,     "8:00",  "18:00", null,     label: "At work");
        b.Does("marco", ActivityKind.Relax,   "18:00", "19:00", "living");
        b.Does("marco", ActivityKind.Eat,     "19:00", "19:45", "dining", anchor: "dining_table", label: "Dinner");
        b.Does("marco", ActivityKind.Relax,   "19:45", "23:15", "living");

        b.Person("sofia", "Sofia");
        b.Does("sofia", ActivityKind.Sleep,   "21:30", "7:10",  "bed2", anchor: "twin_bed");
        b.Does("sofia", ActivityKind.Hygiene, "7:10",  "7:40",  "bath2", anchor: "sink_pedestal");
        b.Does("sofia", ActivityKind.Eat,     "7:40",  "8:10",  "dining", anchor: "dining_table", label: "Breakfast");
        b.Does("sofia", ActivityKind.Out,     "8:10",  "15:30", null,     label: "School");
        b.Does("sofia", ActivityKind.Work,    "15:30", "17:00", "bed2",   label: "Homework");
        b.Does("sofia", ActivityKind.Relax,   "17:00", "18:15", "living");
        b.Does("sofia", ActivityKind.Eat,     "18:15", "19:00", "dining", anchor: "dining_table", label: "Dinner");
        b.Does("sofia", ActivityKind.Relax,   "19:00", "20:45", "living");
        b.Does("sofia", ActivityKind.Hygiene, "20:45", "21:30", "bath2", anchor: "sink_pedestal");

        b.Person("leo", "Leo");
        b.Does("leo", ActivityKind.Sleep,   "20:45", "6:40",  "bed3", anchor: "twin_bed");
        b.Does("leo", ActivityKind.Hygiene, "6:40",  "7:10",  "bath2", anchor: "sink_pedestal");
        b.Does("leo", ActivityKind.Eat,     "7:10",  "7:45",  "dining", anchor: "dining_table", label: "Breakfast");
        b.Does("leo", ActivityKind.Out,     "7:45",  "15:15", null,     label: "School");
        b.Does("leo", ActivityKind.Relax,   "15:15", "16:45", "living");
        b.Does("leo", ActivityKind.Work,    "16:45", "17:45", "bed3",   label: "Homework");
        b.Does("leo", ActivityKind.Relax,   "17:45", "19:00", "living");
        b.Does("leo", ActivityKind.Eat,     "19:00", "19:45", "dining", anchor: "dining_table", label: "Dinner");
        b.Does("leo", ActivityKind.Hygiene, "19:45", "20:15", "bath2", anchor: "sink_pedestal");
        b.Does("leo", ActivityKind.Relax,   "20:15", "20:45", "bed3");

        return b;
    }

    // ===========================================================================================
    // 6. Assisted living house, 5 bed / 4 bath: 17.5 x 12.0 = 210.0 m²
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
        b.Room("bath4",   "Bathroom 4, roll-in", RoomType.Bathroom, 13.6f, 0.0f, 1.8f, 2.6f);
        b.Room("laundry", "Laundry",             RoomType.Laundry, 13.6f, 2.6f, 1.8f, 2.8f);
        b.Room("bed5",    "Bedroom 5",           RoomType.Bedroom, 15.4f, 0.0f, 2.1f, 5.4f);

        b.Room("hall",    "Corridor",            RoomType.Hall,     0.0f, 5.4f, 17.5f, 1.6f);

        b.Room("bed1",    "Bedroom 1",           RoomType.Bedroom,  0.0f, 7.0f, 3.2f, 5.0f);
        b.Room("bath1",   "Bathroom 1, roll-in", RoomType.Bathroom, 3.2f, 7.0f, 1.8f, 5.0f);
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
        Bedroom(b, "bed4", PlanEdge.South);   // 2.5 m and 2.1 m wide respectively: the beds have to
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

        // Five residents, and the reason the People view exists. Bathroom 3 serves bedrooms 3 and 4,
        // so Nadia is out of it at 7:30 exactly as Omar goes in. Scrub there and the handoff is
        // visible. At 8:40 five people are in the dining room at once, which is the other thing worth
        // seeing: whether the room and the route to it actually take them.
        b.Person("rosa", "Rosa", usesWheelchair: true, note: "Bedroom 1 with a roll-in ensuite; lift transfers.");
        b.Does("rosa", ActivityKind.Sleep,   "21:15", "6:45",  "bed1", anchor: "hospital_bed");
        b.Does("rosa", ActivityKind.Care,    "6:45",  "7:45",  "bed1", anchor: "hospital_bed", label: "Morning care");
        b.Does("rosa", ActivityKind.Hygiene, "7:45",  "8:30",  "bath1", anchor: "sink_pedestal");
        b.Does("rosa", ActivityKind.Eat,     "8:30",  "9:15",  "dining", anchor: "dining_table", label: "Breakfast");
        b.Does("rosa", ActivityKind.Relax,   "9:15",  "11:45", "living");
        b.Does("rosa", ActivityKind.Eat,     "11:45", "12:45", "dining", anchor: "dining_table", label: "Lunch");
        b.Does("rosa", ActivityKind.Relax,   "12:45", "15:00", "bed1",   label: "Rest");
        b.Does("rosa", ActivityKind.Relax,   "15:00", "17:45", "living");
        b.Does("rosa", ActivityKind.Eat,     "17:45", "18:45", "dining", anchor: "dining_table", label: "Dinner");
        b.Does("rosa", ActivityKind.Relax,   "18:45", "20:15", "living");
        b.Does("rosa", ActivityKind.Care,    "20:15", "21:15", "bed1",   label: "Evening care");

        b.Person("gil", "Gil");
        b.Does("gil", ActivityKind.Sleep,   "22:00", "6:30",  "bed2", anchor: "twin_bed");
        b.Does("gil", ActivityKind.Hygiene, "6:30",  "7:15",  "bath2", anchor: "sink_pedestal");
        b.Does("gil", ActivityKind.Relax,   "7:15",  "8:00",  "bed2");
        b.Does("gil", ActivityKind.Eat,     "8:00",  "8:45",  "dining", anchor: "dining_table", label: "Breakfast");
        b.Does("gil", ActivityKind.Relax,   "8:45",  "9:30",  "living");
        b.Does("gil", ActivityKind.Out,     "9:30",  "15:00", null,     label: "Day program");
        b.Does("gil", ActivityKind.Relax,   "15:00", "17:30", "living");
        b.Does("gil", ActivityKind.Eat,     "17:30", "18:45", "dining", anchor: "dining_table", label: "Dinner");
        b.Does("gil", ActivityKind.Relax,   "18:45", "20:45", "living");
        b.Does("gil", ActivityKind.Hygiene, "20:45", "21:30", "bath2", anchor: "sink_pedestal");
        b.Does("gil", ActivityKind.Relax,   "21:30", "22:00", "bed2");

        b.Person("nadia", "Nadia", note: "Shares bathroom 3 with Omar.");
        b.Does("nadia", ActivityKind.Sleep,   "22:30", "6:50",  "bed3", anchor: "twin_bed");
        b.Does("nadia", ActivityKind.Hygiene, "6:50",  "7:30",  "bath3", anchor: "sink_pedestal");
        b.Does("nadia", ActivityKind.Relax,   "7:30",  "8:15",  "bed3");
        b.Does("nadia", ActivityKind.Eat,     "8:15",  "9:00",  "dining", anchor: "dining_table", label: "Breakfast");
        b.Does("nadia", ActivityKind.Relax,   "9:00",  "12:00", "living");
        b.Does("nadia", ActivityKind.Eat,     "12:00", "12:45", "dining", anchor: "dining_table", label: "Lunch");
        b.Does("nadia", ActivityKind.Relax,   "12:45", "15:30", "bed3",   label: "Rest");
        b.Does("nadia", ActivityKind.Relax,   "15:30", "17:45", "living");
        b.Does("nadia", ActivityKind.Eat,     "17:45", "18:45", "dining", anchor: "dining_table", label: "Dinner");
        b.Does("nadia", ActivityKind.Relax,   "18:45", "21:15", "living");
        b.Does("nadia", ActivityKind.Hygiene, "21:15", "21:55", "bath3", anchor: "sink_pedestal");
        b.Does("nadia", ActivityKind.Relax,   "21:55", "22:30", "bed3");

        b.Person("omar", "Omar", note: "Shares bathroom 3 with Nadia.");
        b.Does("omar", ActivityKind.Sleep,   "22:45", "7:30",  "bed4", anchor: "twin_bed");
        b.Does("omar", ActivityKind.Hygiene, "7:30",  "8:15",  "bath3", anchor: "sink_pedestal");
        b.Does("omar", ActivityKind.Eat,     "8:15",  "9:00",  "dining", anchor: "dining_table", label: "Breakfast");
        b.Does("omar", ActivityKind.Out,     "9:00",  "15:45", null,     label: "Work placement");
        b.Does("omar", ActivityKind.Relax,   "15:45", "17:30", "living");
        b.Does("omar", ActivityKind.Eat,     "17:30", "18:45", "dining", anchor: "dining_table", label: "Dinner");
        b.Does("omar", ActivityKind.Relax,   "18:45", "20:30", "living");
        b.Does("omar", ActivityKind.Hygiene, "20:30", "21:15", "bath3", anchor: "sink_pedestal");
        b.Does("omar", ActivityKind.Relax,   "21:15", "22:45", "living");

        b.Person("pearl", "Pearl", note: "Cooks for the house.");
        b.Does("pearl", ActivityKind.Sleep,   "21:45", "7:00",  "bed5", anchor: "twin_bed");
        b.Does("pearl", ActivityKind.Hygiene, "7:00",  "7:45",  "bath4", anchor: "sink_pedestal");
        b.Does("pearl", ActivityKind.Cook,    "7:45",  "8:30",  "kitchen", anchor: "range");
        b.Does("pearl", ActivityKind.Eat,     "8:30",  "9:15",  "dining", anchor: "dining_table", label: "Breakfast");
        b.Does("pearl", ActivityKind.Relax,   "9:15",  "11:30", "living");
        b.Does("pearl", ActivityKind.Cook,    "11:30", "12:30", "kitchen", anchor: "range");
        b.Does("pearl", ActivityKind.Eat,     "12:30", "13:15", "dining", anchor: "dining_table", label: "Lunch");
        b.Does("pearl", ActivityKind.Relax,   "13:15", "16:00", "bed5",   label: "Rest");
        b.Does("pearl", ActivityKind.Cook,    "16:00", "17:30", "kitchen", anchor: "range");
        b.Does("pearl", ActivityKind.Eat,     "17:30", "18:45", "dining", anchor: "dining_table", label: "Dinner");
        b.Does("pearl", ActivityKind.Relax,   "18:45", "21:00", "living");
        b.Does("pearl", ActivityKind.Hygiene, "21:00", "21:45", "bath4", anchor: "sink_pedestal");

        return b;
    }

    // ===========================================================================================
    // Shared room recipes: the bare necessities, identical wherever a room plays the same role.
    // ===========================================================================================

    /// <summary>
    /// A bed with its head against <paramref name="bedWall"/>, so pick the wall PERPENDICULAR to the
    /// room's long axis: a 2.03 m bed laid across a 2.5 m room leaves nothing either side of it.
    /// The wardrobe goes on a side wall rather than beside the dresser, because dresser + wardrobe is
    /// 2.24 m and the narrower bedrooms here are 2.1 m wide.
    /// </summary>
    private static void Bedroom(PlanBuilder b, string room, PlanEdge bedWall)
    {
        b.Against("twin_bed", room, bedWall, 0.3f);
        b.Against("nightstand", room, bedWall, 0.72f);

        // The dresser used to go on the wall opposite the bed and the wardrobe on a side wall, both by
        // compass. In a corridor plan the wall opposite the bed is usually the one with the DOOR in it,
        // which is how a 1.83 m wardrobe ended up standing in a doorway. Ask for a wall that fits.
        var dresser = SampleFurniture.Get("dresser");
        PlanEdge dresserWall = b.BestEdgeFor(room, dresser.width, dresser.height,
                                             Opposite(bedWall), SideOf(bedWall), Opposite(SideOf(bedWall)));
        b.Against("dresser", room, dresserWall, 0.3f);

        var wardrobe = SampleFurniture.Get("wardrobe");
        PlanEdge wardrobeWall = b.BestEdgeFor(room, wardrobe.width, wardrobe.height,
                                              SideOf(bedWall), Opposite(SideOf(bedWall)), Opposite(bedWall));
        b.Against("wardrobe", room, wardrobeWall, wardrobeWall == dresserWall ? 0.85f : 0.6f);

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
        string bathing = rollIn ? "roll_in_shower" : "bathtub";
        var tub = SampleFurniture.Get(bathing);

        // North and South used to be hard-coded. Several of these bathrooms have their door on one of
        // those walls (bath3 opens straight into bath4) so the tub landed across the doorway and the
        // grab bar hung in the hole. alongWall turns the fixture a quarter turn, so it is its DEPTH that
        // has to fit the wall.
        PlanEdge tubWall = b.BestEdgeFor(room, tub.depth, tub.height,
                                         PlanEdge.North, PlanEdge.South, PlanEdge.East, PlanEdge.West);
        b.Against(bathing, room, tubWall, 0.5f, alongWall: true);
        b.Mount("grab_bar_36", room, tubWall, 0.5f);

        // Toilet and basin used to share one wall. In the 1.8 m care bathrooms the tub eats most of a
        // side wall, so the pair no longer fits end to end and the basin is given its own wall, which
        // is what a narrow bathroom does in reality anyway.
        var toilet = SampleFurniture.Get("toilet");
        var basin = SampleFurniture.Get("sink_pedestal");

        PlanEdge toiletWall = b.BestEdgeFor(room, toilet.width, toilet.height,
                                            Opposite(tubWall), SideOf(tubWall), Opposite(SideOf(tubWall)));
        b.Against("toilet", room, toiletWall, 0.25f);
        b.Mount("grab_bar_24", room, toiletWall, 0.25f);

        // Facing the toilet where that is free, beside it where it is not.
        PlanEdge acrossFromToilet = Opposite(toiletWall);
        PlanEdge basinWall = acrossFromToilet == tubWall
            ? b.BestEdgeFor(room, basin.width, basin.height, toiletWall, SideOf(toiletWall))
            : b.BestEdgeFor(room, basin.width, basin.height, acrossFromToilet, toiletWall);
        b.Against("sink_pedestal", room, basinWall, basinWall == toiletWall ? 0.78f : 0.5f);

        if (rollIn) b.Mount("grab_bar_36", room, SideOf(tubWall), 0.85f);
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

    /// <summary>A wall perpendicular to this one: where something goes when both facing walls are taken.</summary>
    private static PlanEdge SideOf(PlanEdge e)
        => e == PlanEdge.West || e == PlanEdge.East ? PlanEdge.North : PlanEdge.East;
}
