using System.Collections.Generic;
using UnityEngine;

// The 25 things that can be installed in a home, mirrored into the CXRAuthoring assembly.
//
// Sixteen of them sense, and every figure on those comes from the report cited below. The other nine
// are the Everyday living category and come from nowhere but ordinary retail. See the banner over
// them, and `provenance`, which is what says so on screen rather than leaving a reader to assume.
//
// WHY THIS DUPLICATES THE CATALOG: SensorCatalog is a ScriptableObject in Assembly-CSharp, and
// CXRAuthoring has no references, so SensorFit, SensorCoverage, SensorSim, SensorPackages and
// PlanBuilder, all of which live here so they can be unit tested without a scene, cannot read the
// .asset. This is the same unavoidable duplication SampleFurniture.cs carries against
// FurnitureCatalog, and it is handled the same way: SampleHomeInstaller.VerifyAgainstCatalog compares
// the two on seed, so drift is reported rather than discovered.
//
// EVERY SENSING NUMBER HERE COMES FROM THE REPORT, and the section is cited on the row. That is not
// decoration: the costs are read off the screen in a funding meeting, so an unattributable figure is
// worse than no figure. docs/SMARTHOME.md is the full map back to the PDF.
//
// The nine Everyday living rows are the exception, and they are marked rather than mixed in: every one
// carries `speculative` AND a `provenance` sentence saying in as many words that the report does not
// cover it. The rule has not been relaxed: a figure still has to say where it came from. What changed
// is that "typical retail, check it" is now one of the answers it may give.
//
// COSTS FOLLOW THE REPORT'S OWN STRUCTURE, which is not "every device has a monthly fee". §5.4 prices
// a bundle as hub + 3-5 sensors + ONE system fee of $79.95-$149.95, so a per-device monthly on a door
// sensor would double-count it: the sensors' own rows say "Monthly: Part of system fee". The system
// fee therefore sits on `central_hub`, and only the two devices the report prices separately (the
// pendant, §4.5.1, and the dispenser, §4.2.2) carry a monthly of their own. SensorCost is the only
// thing that adds these up, so the rule lives in one place.
public static class SensorDevices
{
    public struct Device
    {
        public string id;
        public string displayName;
        public string category;          // SensorCategory.*
        public string hostKind;          // SensorHost.*: where this device installs

        public float width;              // local X, meters
        public float depth;              // local Z
        public float height;             // local Y
        public float mountHeight;        // meters AFF; 0 for anything sitting on the floor or a bed

        public float coverageRadius;     // meters; 0 => it senses only what it is attached to
        public float coverageAngle;      // degrees; 360 => omnidirectional

        public float purchaseLow;        // USD, the report's range
        public float purchaseHigh;
        public float monthlyLow;         // USD/month; 0 unless the report prices it separately
        public float monthlyHigh;

        public string privacy;           // SensorPrivacy.*
        public bool speculative;         // not a price the report stands behind

        // WHY this is not a report figure, in the words the rail and the report print verbatim.
        //
        // `speculative` says a price is not quoted; it does not say why, and there are now two
        // different reasons. The four Emerging devices are ones the report NAMES without pricing a
        // product; the everyday aids are ones it does not mention at all. Both need the reader warned
        // and they need different sentences, so the sentence is data rather than a branch.
        //
        // Empty means "from the report". SensorTool and SensorCatalog then print the section number
        // as they always did. Before this, a device with no reportSection printed the literal
        // "Report §." in its own tooltip.
        public string provenance;

        public float[] BoxSize => new[] { width, height, depth };

        /// <summary>Midpoint of the report's purchase range: what a single-number total uses.</summary>
        public float PurchaseTypical => 0.5f * (purchaseLow + purchaseHigh);
        public float MonthlyTypical => 0.5f * (monthlyLow + monthlyHigh);
        public bool HasCoverage => coverageRadius > 0f;
    }

    public static class SensorCategory
    {
        public const string Safety = "safety";              // §4.4
        public const string Mobility = "mobility";          // §4.3
        public const string Health = "health";              // §4.2
        public const string Communication = "communication";// §4.5
        public const string Hub = "hub";                    // §3.1.3
        public const string Emerging = "emerging";          // §2.2.4, unspecified in the report

        // Not in the report at all, and the only category here that is not. Adaptive equipment and
        // small conveniences: a rocker knife, a sock aid, a touch-free bin, a smart bulb. They sense
        // nothing, raise nothing and cover nothing: what they do is make one daily task possible
        // without help, which is the same argument the sensing layer makes by a different route.
        //
        // They are a category rather than a second catalog because everything a device needs is
        // already here: a host to hang off, a cost, a privacy tier, a place in the diff, the revert
        // and the report. An aid is that record with no envelope and no rules.
        public const string Everyday = "everyday";

        public static readonly string[] All =
            { Safety, Mobility, Health, Communication, Hub, Emerging, Everyday };

        public static string Label(string category) => category switch
        {
            Safety => "Safety",
            Mobility => "Mobility",
            Health => "Health",
            Communication => "Staff communication",
            Hub => "Hub",
            Emerging => "Emerging",
            Everyday => "Everyday living",
            _ => "Other",
        };
    }

    /// <summary>Fallback for an unknown key: a small passive box that senses only its own host.</summary>
    public static readonly Device Unknown = new Device
    {
        id = null, displayName = "Unknown device", category = SensorCategory.Safety,
        hostKind = SensorHost.Room,
        width = 0.08f, depth = 0.03f, height = 0.08f, mountHeight = 1.2f,
        coverageRadius = 0f, coverageAngle = 360f,
        purchaseLow = 0f, purchaseHigh = 0f, monthlyLow = 0f, monthlyHigh = 0f,
        privacy = SensorPrivacy.Passive, speculative = false,
    };

    public static bool TryGet(string id, out Device device) => Table.TryGetValue(id ?? "", out device);

    public static Device Get(string id) => Table.TryGetValue(id ?? "", out var d) ? d : Unknown;

    public static bool Exists(string id) => !string.IsNullOrEmpty(id) && Table.ContainsKey(id);

    public static IEnumerable<Device> All => Table.Values;

    public static int Count => Table.Count;

    // -------------------------------------------------------------------------------------------
    // The devices
    // -------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, Device> Table = Build();

    private static Dictionary<string, Device> Build()
    {
        // The two sentences every Everyday living row carries. They are stated once here rather than
        // nine times below, and they are DATA rather than a branch in the UI because the four Emerging
        // devices are unquoted for a different reason: the report names those without pricing them,
        // and it does not mention these at all. A reader deciding whether to trust a number needs to
        // be told which of the two they are looking at.
        //
        // The Emerging four deliberately leave `provenance` empty, which keeps their existing wording
        // exactly as it was.
        const string AID =
            "Outside the smart home report. Standard adaptive equipment, priced at typical US retail. "
          + "The report evaluated no source for this price, so check it before quoting it.";
        const string CONSUMER =
            "Outside the smart home report. An ordinary consumer product, priced at typical US retail. "
          + "The report evaluated no source for this price, so check it before quoting it.";

        var devices = new[]
        {
            // --- Safety (§4.4) ------------------------------------------------------------------
            //
            // §4.4.1: a magnet and a sensor on the frame and the leaf. It senses the DOOR, so its
            // coverage is the opening it hosts on and nothing else; radius 0 says exactly that.
            // Vendors: Aqara $18, Simply Home $50-100, YoLink $20.
            new Device
            {
                id = "door_sensor", displayName = "Door sensor",
                category = SensorCategory.Safety, hostKind = SensorHost.Opening,
                width = 0.07f, depth = 0.02f, height = 0.09f, mountHeight = 2.0f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 18f, purchaseHigh = 100f,
                privacy = SensorPrivacy.Passive,
            },

            // §4.4.2. Current sensing on the stove's supply, not heat, and explicitly NOT gas.
            // Vendors: Simply Home $150-250, FireAvert $149.99, Innohome SGK510 $399, iGuardStove $495.
            new Device
            {
                id = "stove_sensor", displayName = "Stove sensor",
                category = SensorCategory.Safety, hostKind = SensorHost.Furniture,
                width = 0.10f, depth = 0.04f, height = 0.10f, mountHeight = 1.1f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 149.99f, purchaseHigh = 495f,
                privacy = SensorPrivacy.Passive,
            },

            // §4.4.3. Conductive probes on the floor. It notices water that reaches it, so its
            // "coverage" is a puddle's reach rather than a room: 0.3 m, not the 9 m a PIR sees.
            // Vendors: YoLink $18, Aqara $19.99, Kidde $39.99, Honeywell Lyric $79.
            new Device
            {
                id = "water_sensor", displayName = "Water sensor",
                category = SensorCategory.Safety, hostKind = SensorHost.Point,
                width = 0.07f, depth = 0.07f, height = 0.03f, mountHeight = 0f,
                coverageRadius = 0.3f, coverageAngle = 360f,
                purchaseLow = 18f, purchaseHigh = 100f,
                privacy = SensorPrivacy.Passive,
            },

            // §4.4.4. Deadbolt plus, in the ADA case, a powered opener. The $849 OlideSmart is the
            // ADA opener and it is why this range is so wide; the report keeps them in one row.
            // Vendors: Sesame $149.99, Schlage Encode $224, August $229.99, OlideSmart $849.
            new Device
            {
                id = "smart_lock", displayName = "Smart lock / opener",
                category = SensorCategory.Safety, hostKind = SensorHost.Opening,
                width = 0.08f, depth = 0.06f, height = 0.16f, mountHeight = 1.0f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 149.99f, purchaseHigh = 849f,
                privacy = SensorPrivacy.Passive,
            },

            // §4.4.5: a smart plug or an in-wall switch. This is the actuator the stove rule reaches
            // for ("cutting power to the outlet if integrated with a smart switch", §4.4.2).
            // Vendors: Enabling Devices $29.95 (adapted, big-button), TP-Link Kasa $44, Brilliant $399.
            //
            // "Smart plug" leads the name because that is the half people ask for, and there is
            // deliberately no separate smart_plug row: the report prices both forms in ONE range, so
            // splitting them would turn one attributable figure into two and count it twice in
            // SensorCost. The catalog's `detects` text carries the word so search finds it either way.
            new Device
            {
                id = "smart_switch", displayName = "Smart plug / switch",
                category = SensorCategory.Safety, hostKind = SensorHost.Wall,
                width = 0.08f, depth = 0.02f, height = 0.12f, mountHeight = 1.12f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 29.95f, purchaseHigh = 399f,
                privacy = SensorPrivacy.Passive,
            },

            // --- Mobility (§4.3) ----------------------------------------------------------------
            //
            // §4.3.1. PIR, "up to 30-40 feet with a 90-120 degree field of view", mounted 6-8 ft.
            // 9.1 m is 30 ft, the conservative end; 110 degrees is the middle of the stated range.
            // Vendors: TP-Link Kasa $20, Aqara P1 $25, SmartThings $25, Simply Home $40-80.
            new Device
            {
                id = "motion_sensor", displayName = "Motion sensor",
                category = SensorCategory.Mobility, hostKind = SensorHost.Wall,
                width = 0.06f, depth = 0.06f, height = 0.08f, mountHeight = 2.1f,
                coverageRadius = 9.1f, coverageAngle = 110f,
                purchaseLow = 20f, purchaseHigh = 80f,
                privacy = SensorPrivacy.Presence,
            },

            // §4.3.2: a pad under the mattress or the seat. It senses the item it is under, which is
            // why it hosts on furniture and has no radius.
            // Vendors: Smart Caregiver $30, VitalBase VB-100 $120, Rest Assured $150+, Simply Home $100-200.
            new Device
            {
                id = "bed_chair_pad", displayName = "Bed / chair pad",
                category = SensorCategory.Mobility, hostKind = SensorHost.Furniture,
                width = 0.51f, depth = 0.25f, height = 0.01f, mountHeight = 0f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 30f, purchaseHigh = 200f,
                privacy = SensorPrivacy.Presence,
            },

            // --- Health (§4.2) ------------------------------------------------------------------
            //
            // §4.2.1: "placed on a wall in a central location like a hallway or living room".
            // Vendors: Sensi Touch 2 $149, Honeywell T9 $169, Nest Learning $249, Ecobee Premium $250.
            new Device
            {
                id = "smart_thermostat", displayName = "Smart thermostat",
                category = SensorCategory.Health, hostKind = SensorHost.Wall,
                width = 0.13f, depth = 0.03f, height = 0.13f, mountHeight = 1.5f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 100f, purchaseHigh = 250f,
                privacy = SensorPrivacy.Passive,
            },

            // §4.2.2. Locked tray, "up to 4 doses/day", on a countertop or nightstand. One of the two
            // devices the report prices with its own monthly fee ($24.95-$29.95).
            // Vendors: Livi $199+$19.99/mo, Hero Health $99+$29.99/mo, e-pill MedSmart PLUS $299.
            new Device
            {
                id = "med_dispenser", displayName = "Medication dispenser",
                category = SensorCategory.Health, hostKind = SensorHost.Furniture,
                width = 0.24f, depth = 0.24f, height = 0.20f, mountHeight = 0.91f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 219.95f, purchaseHigh = 299.95f,
                monthlyLow = 24.95f, monthlyHigh = 29.95f,
                privacy = SensorPrivacy.Passive,
            },

            // --- Staff communication (§4.5) -----------------------------------------------------
            //
            // §4.5.1. Worn, so it has no place in the plan at all: SensorHost.Occupant, and the
            // console shows it against a person rather than a room. The other separately-priced
            // monthly ($27.95-$51.95). Fall detection is the reason for the upper purchase figure.
            // Vendors: Assistive Technology Services $119.99 (no fee), Bay Alarm $29.95/mo,
            // Medical Guardian Mini $124.95+$39.95/mo, Life Alert $197+$49.95/mo.
            new Device
            {
                id = "panic_pendant", displayName = "Panic pendant",
                category = SensorCategory.Communication, hostKind = SensorHost.Occupant,
                width = 0.05f, depth = 0.02f, height = 0.05f, mountHeight = 0f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 50f, purchaseHigh = 284.95f,
                monthlyLow = 27.95f, monthlyHigh = 51.95f,
                privacy = SensorPrivacy.Audio,
            },

            // §4.5.2: "at the front door at eye level". The ONLY Video device in this catalog, and
            // that is the report's own position: §5.3.3 records "no constant cameras; optional
            // entry-way only". The console's role tiers are what make that legible.
            // Vendors: Ring $49.99-$149.99, Arlo Essential $79.99, Eufy $99.99+, Nest $179.99.
            new Device
            {
                id = "video_doorbell", displayName = "Video doorbell",
                category = SensorCategory.Communication, hostKind = SensorHost.Opening,
                width = 0.06f, depth = 0.03f, height = 0.13f, mountHeight = 1.2f,
                coverageRadius = 5.0f, coverageAngle = 160f,
                purchaseLow = 49.99f, purchaseHigh = 329.95f,
                privacy = SensorPrivacy.Video,
            },

            // --- Hub (§3.1.3) -------------------------------------------------------------------
            //
            // "a wall-mounted tablet or dedicated device", "placed in a central location like the
            // living room". §5.4 prices the two SimplyHome bundles (Firefly $849.95-$1,149.90,
            // Butler $1,249.90-$1,549.90) and ONE system fee of $79.95-$149.95, which is why the
            // whole system's monthly sits on this row and on nothing else.
            new Device
            {
                id = "central_hub", displayName = "Central hub",
                category = SensorCategory.Hub, hostKind = SensorHost.Wall,
                width = 0.25f, depth = 0.04f, height = 0.18f, mountHeight = 1.4f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 849.95f, purchaseHigh = 1549.90f,
                monthlyLow = 79.95f, monthlyHigh = 149.95f,
                privacy = SensorPrivacy.Audio,
            },

            // --- Emerging (§2.2.4) --------------------------------------------------------------
            //
            // The report names these as directions: "assistive wearables (e.g., health-tracking
            // watches)", "computer vision AI", "environmental monitors for temperature/humidity",
            // "voice-activated assistants". WITHOUT naming a product or a price. They are marked
            // speculative so nothing quotes a figure the report did not stand behind; the costs below
            // are typical retail, and the UI says so on hover.
            new Device
            {
                id = "health_wearable", displayName = "Health wearable",
                category = SensorCategory.Emerging, hostKind = SensorHost.Occupant,
                width = 0.04f, depth = 0.02f, height = 0.04f, mountHeight = 0f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 99f, purchaseHigh = 299f,
                monthlyLow = 0f, monthlyHigh = 9.99f,
                privacy = SensorPrivacy.Presence, speculative = true,
            },
            new Device
            {
                id = "air_quality_monitor", displayName = "Air quality monitor",
                category = SensorCategory.Emerging, hostKind = SensorHost.Furniture,
                width = 0.09f, depth = 0.09f, height = 0.14f, mountHeight = 1.2f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 79f, purchaseHigh = 229f,
                privacy = SensorPrivacy.Passive, speculative = true,
            },
            // The thing every alert in the report actually reaches for: "activate an audio prompt
            // remotely", "issues prompt via hub speaker", as a device that can be placed where the
            // resident will hear it, rather than assumed to exist wherever the hub is.
            new Device
            {
                id = "voice_prompt_speaker", displayName = "Voice prompt speaker",
                category = SensorCategory.Emerging, hostKind = SensorHost.Furniture,
                width = 0.14f, depth = 0.14f, height = 0.10f, mountHeight = 1.0f,
                coverageRadius = 6.0f, coverageAngle = 360f,
                purchaseLow = 49f, purchaseHigh = 129f,
                privacy = SensorPrivacy.Audio, speculative = true,
            },
            // Radar rather than a camera: it reports a fall without seeing a body, which is the whole
            // reason to prefer it in a bedroom or a bathroom where a camera is not acceptable.
            new Device
            {
                id = "fall_radar", displayName = "Fall detection radar",
                category = SensorCategory.Emerging, hostKind = SensorHost.Wall,
                width = 0.10f, depth = 0.03f, height = 0.10f, mountHeight = 1.8f,
                coverageRadius = 6.0f, coverageAngle = 120f,
                purchaseLow = 199f, purchaseHigh = 449f,
                monthlyLow = 0f, monthlyHigh = 19.99f,
                privacy = SensorPrivacy.Presence, speculative = true,
            },

            // --- Everyday living (NOT from the report) ------------------------------------------
            //
            // Nothing below senses, reports or connects to the hub, and that is what makes them cheap
            // to add: coverageRadius 0 and no DefaultRules case means SensorOverlay skips them,
            // SensorSim emits nothing for them, SensorRules sees no events and SensorCoverage counts
            // nothing, so every existing figure in the app is untouched by construction. What they DO
            // get, for free, is a host, a price in the total, a line in the change list, a revert and
            // a row in the report.
            //
            // THE HOST IS THE WHOLE DESIGN HERE. Something used on your own body belongs to a PERSON,
            // SensorHost.Occupant, the pendant's path, which has no pose at all, so a 2 cm zipper pull
            // is never drawn in a plan it would only clutter. Something kept in a place belongs to a
            // ROOM and renders as a small labeled box, at or above the scale of a motion sensor.
            //
            // Sizes are recorded for the worn items even though nothing draws them, because the tile
            // art and any later report of what was bought should not have to invent one.

            // Weighted, contoured or self-levelling cutlery for a tremor or a weak grip. Priced as a
            // family: an off-the-shelf weighted set is ~$30, a stabilising powered handle ~$195.
            new Device
            {
                id = "stability_utensils", displayName = "Stabilizing cutlery",
                category = SensorCategory.Everyday, hostKind = SensorHost.Occupant,
                width = 0.04f, depth = 0.04f, height = 0.20f, mountHeight = 0f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 30f, purchaseHigh = 200f,
                privacy = SensorPrivacy.None, speculative = true, provenance = AID,
            },
            // Cuts with a rocking press rather than a saw, so it works one-handed.
            new Device
            {
                id = "rocker_knife", displayName = "Rocker knife",
                category = SensorCategory.Everyday, hostKind = SensorHost.Occupant,
                width = 0.03f, depth = 0.03f, height = 0.20f, mountHeight = 0f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 10f, purchaseHigh = 35f,
                privacy = SensorPrivacy.None, speculative = true, provenance = AID,
            },
            // A lever arm on a key, turning a pinch grip into a whole-hand one.
            new Device
            {
                id = "key_turner", displayName = "Key turner",
                category = SensorCategory.Everyday, hostKind = SensorHost.Occupant,
                width = 0.05f, depth = 0.02f, height = 0.10f, mountHeight = 0f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 10f, purchaseHigh = 30f,
                privacy = SensorPrivacy.None, speculative = true, provenance = AID,
            },
            // Dressing without bending to the foot: the largest of the personal aids, and the one
            // most often the difference between dressing alone and waiting for someone.
            new Device
            {
                id = "sock_aid", displayName = "Sock aid",
                category = SensorCategory.Everyday, hostKind = SensorHost.Occupant,
                width = 0.12f, depth = 0.10f, height = 0.30f, mountHeight = 0f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 10f, purchaseHigh = 35f,
                privacy = SensorPrivacy.None, speculative = true, provenance = AID,
            },
            // Fastens a button one-handed. Kept separate from the zipper pull rather than sold as the
            // usual combo tool, because a plan should be able to say a resident needs only one.
            new Device
            {
                id = "button_hook", displayName = "Button hook",
                category = SensorCategory.Everyday, hostKind = SensorHost.Occupant,
                width = 0.03f, depth = 0.02f, height = 0.20f, mountHeight = 0f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 8f, purchaseHigh = 25f,
                privacy = SensorPrivacy.None, speculative = true, provenance = AID,
            },
            new Device
            {
                id = "zipper_pull", displayName = "Zipper pull",
                category = SensorCategory.Everyday, hostKind = SensorHost.Occupant,
                width = 0.02f, depth = 0.01f, height = 0.10f, mountHeight = 0f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 6f, purchaseHigh = 18f,
                privacy = SensorPrivacy.None, speculative = true, provenance = AID,
            },

            // High-contrast, large-figure measuring cups and jugs. Hosted on the counter it sits on,
            // not on a person: this is kitchen equipment anyone cooking there uses, and where it is
            // kept is the useful fact.
            new Device
            {
                id = "large_print_measures", displayName = "High-contrast measuring set",
                category = SensorCategory.Everyday, hostKind = SensorHost.Furniture,
                width = 0.15f, depth = 0.15f, height = 0.14f, mountHeight = 0.91f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 12f, purchaseHigh = 45f,
                privacy = SensorPrivacy.None, speculative = true, provenance = AID,
            },
            // Opens on a wave, so it needs neither a free hand nor a foot on a pedal. It has an
            // infrared sensor in the lid and is still SensorPrivacy.None: nothing it notices leaves
            // the bin, and the tier is about what reaches a caregiver, not about what has a sensor.
            new Device
            {
                id = "auto_trash_can", displayName = "Touch-free bin",
                category = SensorCategory.Everyday, hostKind = SensorHost.Room,
                width = 0.32f, depth = 0.32f, height = 0.66f, mountHeight = 0f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 40f, purchaseHigh = 130f,
                privacy = SensorPrivacy.None, speculative = true, provenance = CONSUMER,
            },
            // Voice or app dimming and colour temperature, and light that comes on without crossing a
            // dark room to a switch. Passive rather than None: it IS on the network, even though
            // nothing it does is reported anywhere. Mounted at 2.30 m, a ceiling fitting rather than
            // a lamp, which is the case worth drawing.
            new Device
            {
                id = "smart_bulb", displayName = "Smart light bulb",
                category = SensorCategory.Everyday, hostKind = SensorHost.Room,
                width = 0.06f, depth = 0.06f, height = 0.11f, mountHeight = 2.30f,
                coverageRadius = 0f, coverageAngle = 360f,
                purchaseLow = 10f, purchaseHigh = 50f,
                privacy = SensorPrivacy.Passive, speculative = true, provenance = CONSUMER,
            },
        };

        var map = new Dictionary<string, Device>(devices.Length);
        foreach (var d in devices) map[d.id] = d;
        return map;
    }

    // -------------------------------------------------------------------------------------------
    // Default rules
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The rules a device arrives with, straight from the report's own thresholds. A fresh list every
    /// call, because the caller owns what it gets and a shared SensorRuleDef edited through one
    /// sensor would move the threshold on every other sensor of that type.
    /// </summary>
    /// <remarks>
    /// Five of the sixteen devices have no rule at all, and that is correct rather than unfinished:
    /// a lock, a switch, a speaker and a hub ACT, they do not notice, and a doorbell's motion is a
    /// notification rather than an alert (§4.5.2: "screen visitors", not "raise the alarm").
    /// </remarks>
    public static List<SensorRuleDef> DefaultRules(string deviceType)
    {
        var rules = new List<SensorRuleDef>();
        switch (deviceType)
        {
            // §3.1 scenario: sessions run 15-20 minutes, and 45 is where the hub calls the DSP.
            case "stove_sensor":
                rules.Add(Rule(SensorAlertKind.UnattendedCooktop, 45, 0, 0, SensorSeverity.Urgent));
                break;

            // §4.1: "If front door opens after 9 PM, alert caregiver and play verbal prompt."
            // Runs to 06:00, which is a window that wraps midnight. Hence Clock.Spans.
            case "door_sensor":
                rules.Add(Rule(SensorAlertKind.NightExit, 0, 21 * 60, 6 * 60, SensorSeverity.Warning));
                break;

            // §4.3.2: "alerts for prolonged absence (e.g., after 10-30 minutes) or immediate exits."
            case "bed_chair_pad":
                rules.Add(Rule(SensorAlertKind.BedExit, 10, 0, 5 * 60, SensorSeverity.Warning));
                break;

            // §4.1 motion row: "no motion for 10 min triggers alert to caregiver's phone".
            case "motion_sensor":
                rules.Add(Rule(SensorAlertKind.PossibleFall, 10, 0, 0, SensorSeverity.Urgent));
                break;

            // Radar reports a fall as an event rather than inferring one from stillness, so it fires
            // in a minute rather than after ten.
            case "fall_radar":
                rules.Add(Rule(SensorAlertKind.PossibleFall, 1, 0, 0, SensorSeverity.Urgent));
                break;

            // §4.2.2: a missed dose is what raises the alert; half an hour is the grace period.
            case "med_dispenser":
                rules.Add(Rule(SensorAlertKind.MissedMedication, 30, 0, 0, SensorSeverity.Warning));
                break;

            // §4.4.3: "reacts instantly upon contact with water".
            case "water_sensor":
                rules.Add(Rule(SensorAlertKind.WaterLeak, 0, 0, 0, SensorSeverity.Urgent));
                break;

            // §4.5.1: one press, straight through.
            case "panic_pendant":
                rules.Add(Rule(SensorAlertKind.Panic, 0, 0, 0, SensorSeverity.Urgent));
                break;

            // §4.2.1: "triggering alerts if temperatures deviate from safe ranges". Half an hour of
            // deviation, not a spike, because opening a window in July is not an emergency.
            case "smart_thermostat":
            case "air_quality_monitor":
                rules.Add(Rule(SensorAlertKind.Temperature, 30, 0, 0, SensorSeverity.Warning));
                break;
        }
        return rules;
    }

    /// <summary>
    /// The rules actually in force for an installed sensor: its own when it has any, the device's
    /// defaults otherwise. Every reader goes through this rather than touching SensorDef.rules, so
    /// "empty means default" is decided in one place.
    /// </summary>
    public static List<SensorRuleDef> EffectiveRules(SensorDef sensor)
    {
        if (sensor == null) return new List<SensorRuleDef>();
        if (sensor.rules != null && sensor.rules.Count > 0) return sensor.rules;
        return DefaultRules(sensor.deviceType);
    }

    private static SensorRuleDef Rule(string kind, int threshold, int from, int to, string severity)
        => new SensorRuleDef
        {
            kind = kind,
            thresholdMinutes = threshold,
            windowStart = from,
            windowEnd = to,
            severity = severity,
            enabled = true,
        };

    // -------------------------------------------------------------------------------------------
    // Resolved values: every reader asks here rather than testing for zero itself
    // -------------------------------------------------------------------------------------------

    public static float RadiusOf(SensorDef sensor)
    {
        if (sensor == null) return 0f;
        return sensor.coverageRadius > 0f ? sensor.coverageRadius : Get(sensor.deviceType).coverageRadius;
    }

    /// <summary>Degrees of arc. Anything at or over 360 (and anything unset) is omnidirectional.</summary>
    public static float AngleOf(SensorDef sensor)
    {
        if (sensor == null) return 360f;
        float a = sensor.coverageAngle > 0f ? sensor.coverageAngle : Get(sensor.deviceType).coverageAngle;
        return a <= 0f ? 360f : Mathf.Min(a, 360f);
    }

    public static float MountHeightOf(SensorDef sensor)
    {
        if (sensor == null) return 0f;
        return sensor.mountHeight > 0f ? sensor.mountHeight : Get(sensor.deviceType).mountHeight;
    }

    /// <summary>A ceiling fitting: SensorPose puts it flush under the ceiling, whatever the mount
    /// height says: a bulb at a fixed 2.3 m floats below a 2.7 m ceiling and clips a 2.2 m one.</summary>
    public static bool CeilingMounted(string id) => id == "smart_bulb";

    public static string PrivacyOf(SensorDef sensor)
    {
        if (sensor == null) return SensorPrivacy.Passive;
        return !string.IsNullOrEmpty(sensor.privacy) ? sensor.privacy : Get(sensor.deviceType).privacy;
    }

    /// <summary>The device's display name, falling back to the raw key rather than to "Unknown
    /// device": a home carrying a key this build does not know should still say which key.</summary>
    public static string LabelOf(SensorDef sensor)
    {
        if (sensor == null) return "Device";
        if (TryGet(sensor.deviceType, out var d)) return d.displayName;
        return string.IsNullOrEmpty(sensor.deviceType) ? "Device" : sensor.deviceType;
    }
}
