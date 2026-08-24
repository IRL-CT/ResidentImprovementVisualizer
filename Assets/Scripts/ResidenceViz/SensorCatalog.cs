using System;
using System.Collections.Generic;
using UnityEngine;

// The Smart living catalog: the counterpart of FurnitureCatalog for everything installed rather than
// furnished. Sixteen sensing devices from the report, and nine Everyday living items the report does
// not cover at all: a rocker knife, a sock aid, a touch-free bin.
//
// The file is still called SensorCatalog, and so is the asset, because renaming either would move a
// GUID reference in ResidenceViz.unity for a word nobody sees. What the class name has to be right about is
// the SHAPE of the record (host, cost, privacy, coverage, rules) and an everyday aid is that record
// with no coverage and no rules. See the Everyday living banner in SensorDevices.cs.
//
// Same role, same shape, same key space: entry ids are PrefabRegistry keys, so the renderer resolves
// art by asking for `id` and falls back to a correctly sized labeled box when there is none. Every
// device ships as a box today, which is honest: these are small grey plastic objects and a labeled
// box at 70 x 20 x 90 mm is very nearly what a door sensor looks like.
//
// WHAT THIS CARRIES THAT FurnitureCatalog DOES NOT, and why:
//
//   * Cost, and vendors with it. The report (§4.1, and the "Various vendors" paragraph closing every
//     §4 subsection) prices each device with a range and names four suppliers. A care team decides
//     whether to install this by looking at the total, so the total has to be in the tool, and every
//     figure has to be attributable, which is what the vendor list and `reportSection` are for.
//   * Coverage, where there is any. It is what separates the five devices that have it: a PIR sensor
//     sees 9 m of a corridor and a door sensor sees one door, and it is the number the plan overlay
//     draws and the number a coverage gap is measured against. The other twenty have none, and the
//     picker tells them apart by footprint instead; see SensorTool's tile art.
//   * `provenance`, which is empty for everything the report specifies and set on everything it does
//     not. `speculative` says a price is unquoted; this says WHY, in the sentence the rail prints.
//   * A privacy tier. §5.5 asks for monitoring that does not compromise dignity and §5.3.3 records
//     "no constant cameras; optional entry-way only". The console's DSP / Family / Resident roles
//     filter on this, which is what turns a paragraph of ethics into something visible on screen.
//   * `detects` and `iddRationale`, both quoted from the report. They are the tooltips, so the reason
//     a device is in a plan is one hover away from the device.
//
// SensorDevices.cs mirrors the geometry, coverage, cost and default rules into CXRAuthoring, which
// cannot reference a ScriptableObject; SampleResidenceInstaller.VerifyAgainstCatalog compares the two on
// seed. That duplication is deliberate and identical to SampleFurniture's. See the header there.
[CreateAssetMenu(fileName = "SensorCatalog", menuName = "CXR/Sensor Catalog")]
public class SensorCatalog : ScriptableObject
{
    [Serializable]
    public class Vendor
    {
        public string name;
        [Tooltip("As printed in the report: a purchase price, a monthly fee, or both.")]
        public string price;
        public string url;
        [Tooltip("What the report says distinguishes this one, e.g. 'ADA opener', 'no monthly fee'.")]
        public string note;

        public string Line => string.IsNullOrEmpty(note) ? $"{name}: {price}" : $"{name}: {price} ({note})";
    }

    [Serializable]
    public class Entry
    {
        [Tooltip("Catalog key. Matches a PrefabRegistry key when real art exists, and the id in " +
                 "SensorDevices.cs, which SampleResidenceInstaller re-checks on seed.")]
        public string id;
        public string displayName;
        [Tooltip("safety, mobility, health, communication, hub, emerging. SensorDevices.SensorCategory.")]
        public string category = "safety";
        [Tooltip("Which element this installs on: opening, furniture, room, wall, point, occupant.")]
        public string hostKind = "room";

        [Header("True dimensions (meters)")]
        public float widthM = 0.08f;
        public float depthM = 0.03f;
        public float heightM = 0.08f;
        [Tooltip("Height above finished floor. 0 for anything sitting on the floor, a bed, or a person.")]
        public float mountHeightM = 1.2f;

        [Header("Detection envelope")]
        [Tooltip("Meters. 0 means it senses only the element it is attached to.")]
        public float coverageRadiusM;
        [Tooltip("Degrees of arc. 360 is omnidirectional.")]
        public float coverageAngleDeg = 360f;

        [Header("Cost (USD, the report's range)")]
        public float purchaseLowUsd;
        public float purchaseHighUsd;
        [Tooltip("Per-device monthly fee. 0 for anything covered by the hub's system fee. See " +
                 "SensorCost, which is the only place these are added up.")]
        public float monthlyLowUsd;
        public float monthlyHighUsd;

        public List<Vendor> vendors = new List<Vendor>();

        [Header("What it is for")]
        [Tooltip("passive, presence, audio, video. SensorPrivacy drives the console's role tiers.")]
        public string privacy = "passive";
        [Tooltip("One line: what this device actually notices.")]
        [TextArea(2, 3)] public string detects;
        [Tooltip("The report's 'How it helps IDD' paragraph, condensed to a sentence or two.")]
        [TextArea(2, 5)] public string iddRationale;
        [Tooltip("Where in SmartHomeReport.pdf this device is specified, e.g. '4.4.1'.")]
        public string reportSection;

        [Tooltip("This is not a price the report stands behind. Surfaced in the UI so no figure is " +
                 "mistaken for one it does. Set `provenance` too, or the UI says the report names " +
                 "this device without pricing it, which is only true of the Emerging four.")]
        public bool speculative;

        [Tooltip("Why this is not a report figure, printed verbatim in the rail and the report. " +
                 "Leave EMPTY for anything the report specifies, so the section number is shown then. " +
                 "Set it on everything in Everyday living, which the report does not mention at all.")]
        [TextArea(2, 3)] public string provenance;

        [Header("Appearance")]
        [Tooltip("Color of the placeholder box, and of this device's coverage in the plan overlay.")]
        public Color swatch = new Color(0.40f, 0.55f, 0.72f);

        public Vector3 SizeMeters => new Vector3(widthM, heightM, depthM);
        public string Label => string.IsNullOrEmpty(displayName) ? id : displayName;
        public bool HasCoverage => coverageRadiusM > 0f;
        public bool IsWorn => hostKind == SensorHost.Occupant;

        /// <summary>
        /// One sentence saying where this entry's figures come from: the report's section number, or
        /// the <see cref="provenance"/> line for anything the report does not cover.
        /// </summary>
        /// <remarks>
        /// Every surface that prints an attribution goes through this, which is what stops an entry
        /// with no <see cref="reportSection"/> rendering the literal "Report §." in its own tooltip,
        /// exactly what the picker did the moment the catalog held anything outside the report.
        /// </remarks>
        public string Attribution => string.IsNullOrEmpty(provenance)
            ? "Report §" + reportSection + "."
            : provenance;

        /// <summary>"$50-$100" or "$149.99", the way the rail and the report both show it.</summary>
        public string PurchaseRange => Range(purchaseLowUsd, purchaseHighUsd);
        /// <summary>"" when this device has no monthly fee of its own.</summary>
        public string MonthlyRange
            => monthlyHighUsd <= 0f ? "" : Range(monthlyLowUsd, monthlyHighUsd) + " / mo";

        /// <summary>
        /// "$850-$1,550 + $80-$150/mo". Whole dollars on one line, for the rail's cost row. The
        /// exact figures stay in the tooltip and everywhere else PurchaseRange is printed; the hub's
        /// full string with cents wraps a 310 px rail, and a wrapped price row reads as two prices.
        /// </summary>
        public string CostLine
            => RangeWhole(purchaseLowUsd, purchaseHighUsd)
             + (monthlyHighUsd <= 0f ? "" : " + " + RangeWhole(monthlyLowUsd, monthlyHighUsd) + "/mo");

        private static string RangeWhole(float low, float high)
        {
            if (high <= 0f) return "None";
            string a = MoneyWhole(low), b = MoneyWhole(high);
            return Mathf.Approximately(low, high) || low <= 0f ? b : a + " - " + b;
        }

        private static string MoneyWhole(float usd) => "$" + Mathf.Round(usd).ToString("#,0");

        private static string Range(float low, float high)
        {
            if (high <= 0f) return "None";
            string a = Money(low), b = Money(high);
            return Mathf.Approximately(low, high) || low <= 0f ? b : a + " - " + b;
        }

        private static string Money(float usd)
            => "$" + (Mathf.Approximately(usd, Mathf.Round(usd)) ? usd.ToString("0") : usd.ToString("0.00"));
    }

    public List<Entry> entries = new List<Entry>();

    private Dictionary<string, Entry> _lookup;

    public Entry Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        BuildLookup();
        return _lookup.TryGetValue(id, out var e) ? e : null;
    }

    /// <summary>Entries in one category, for the picker grid. Null or empty returns everything.</summary>
    public List<Entry> InCategory(string category)
    {
        var list = new List<Entry>();
        foreach (var e in entries)
            if (e != null && !string.IsNullOrEmpty(e.id) &&
                (string.IsNullOrEmpty(category) || e.category == category))
                list.Add(e);
        return list;
    }

    public List<string> Categories()
    {
        var seen = new List<string>();
        foreach (var e in entries)
            if (e != null && !string.IsNullOrEmpty(e.category) && !seen.Contains(e.category))
                seen.Add(e.category);
        return seen;
    }

    /// <summary>
    /// Builds an installed device from a catalog entry. The host is resolved by SensorFit before this
    /// is called. Placing is "which element does this go on", never "where in the room is it".
    /// </summary>
    /// <remarks>
    /// Coverage, mount height and privacy are copied across at placement time, exactly as
    /// FurnitureCatalog.NewWallMount copies the decor fields: the render and simulation paths then
    /// re-derive everything from the SensorDef alone, and a device keeps the envelope it was installed
    /// with if the catalog is later revised. Rules are deliberately left EMPTY. SensorDevices
    /// .EffectiveRules reads the defaults, so a residence that never touched a threshold picks up an
    /// improved one, and a residence that did keeps its own.
    /// </remarks>
    public static SensorDef NewInstance(Entry entry, string hostKind, string hostId,
                                        Vector2? point = null, float facingYaw = 0f)
        => new SensorDef
        {
            id = Guid.NewGuid().ToString(),
            deviceType = entry.id,
            hostKind = hostKind,
            hostId = hostId,
            position = point.HasValue ? new[] { point.Value.x, point.Value.y } : null,
            mountHeight = entry.mountHeightM,
            coverageRadius = entry.coverageRadiusM,
            coverageAngle = entry.coverageAngleDeg,
            facingYaw = facingYaw,
            privacy = entry.privacy,
            monitored = true,
            included = true,
            rules = null,
        };

    private void BuildLookup()
    {
        if (_lookup != null) return;
        _lookup = new Dictionary<string, Entry>();
        foreach (var e in entries)
            if (e != null && !string.IsNullOrEmpty(e.id)) _lookup[e.id] = e;
    }

    private void OnValidate() => _lookup = null;
}
