using System.Collections.Generic;
using UnityEngine;

// What a smart home package costs, and what the report says it offsets.
//
// THE ONLY PLACE DEVICE COSTS ARE ADDED UP. The report does not price these as "sum of the parts":
// §5.4 prices a bundle as a hub plus three to five sensors plus ONE system fee of $79.95-$149.95, and
// every sensor row in §4.1 says "Monthly: Part of system fee". Adding a monthly per device would
// double-count that fee five times over on a care home. So the fee lives on `central_hub` in
// SensorDevices, only the pendant (§4.5.1) and the dispenser (§4.2.2) carry a monthly of their own,
// and this file is the one thing that knows it.
//
// THE OFFSET IS DERIVED, NOT QUOTED. The report's §4.1 table advertises per-device savings: "saving
// $500/month per resident", "cuts nursing visits by 50%", which are vendor figures with no method
// behind them, and reprinting them in a tool a care team uses to argue for funding would launder a
// marketing claim into an estimate. What is quoted instead is the single mechanical figure §5.2.2
// gives: a caregiver who answers an alert remotely instead of driving over saves $20-40 of labour,
// being an hour at the DSP wage §2.2.4 records. That is multiplied by the alerts THIS home's own
// simulated day actually produces, so a plan with no sensors in the bedrooms claims no saving for
// them, and the number moves when the plan does.
public static class SensorCost
{
    /// <summary>
    /// Labour saved per alert answered remotely rather than in person, USD. §5.2.2: "reducing the
    /// need for on-site staff and saving $20-40/hour in labor costs per incident."
    /// </summary>
    public const float RemoteResponseLow = 20f;
    public const float RemoteResponseHigh = 40f;

    /// <summary>Days per month used to turn a per-day rate into a monthly figure.</summary>
    public const float DaysPerMonth = 30f;

    /// <summary>
    /// The incident rate the monthly offset is quoted at. STATED rather than simulated, and every
    /// screen that prints an offset prints this beside it.
    /// </summary>
    /// <remarks>
    /// The obvious thing (count the alerts in SensorSim's demonstration day and multiply by 30) is
    /// wrong, and wrong in the flattering direction. That day deliberately acts out seven of the
    /// report's scenarios at once so a viewer can see what the package catches; treating it as a
    /// typical day would claim seven incidents EVERY day and produce a saving of several thousand
    /// dollars a month, which would discredit the whole figure the moment anyone checked it.
    ///
    /// So the simulated day is used qualitatively (WHICH scenarios this package would catch) and the
    /// money is quoted at an assumption a reader can disagree with in one number. Three a week is
    /// deliberately modest against §4.1's per-device claims, none of which are reproduced here.
    /// </remarks>
    public const float AssumedIncidentsPerWeek = 3f;

    public struct Estimate
    {
        public int deviceCount;
        public int speculativeCount;   // devices whose price the report does not stand behind

        public float upfrontLow;
        public float upfrontHigh;
        public float monthlyLow;
        public float monthlyHigh;

        /// <summary>
        /// True when the package has devices that need routing to staff but no hub to route them.
        /// The system fee is counted anyway: a package that cannot reach anyone is not cheaper, it
        /// is unfinished, and SensorCoverage.Gaps reports it as the gap it is.
        /// </summary>
        public bool hubMissing;

        public bool Any => deviceCount > 0;

        public float UpfrontTypical => 0.5f * (upfrontLow + upfrontHigh);
        public float MonthlyTypical => 0.5f * (monthlyLow + monthlyHigh);

        /// <summary>"$1,240-$2,180", the form the rail and the report both print.</summary>
        public string UpfrontRange => Range(upfrontLow, upfrontHigh);
        public string MonthlyRange => Range(monthlyLow, monthlyHigh);
    }

    /// <summary>
    /// Adds up an installed package. Devices excluded from the variant are skipped, matching
    /// everything else that reads `included`: a device switched off in a proposal is not bought.
    /// </summary>
    public static Estimate Of(LevelDef level) => Of(level?.sensors);

    /// <summary>
    /// The whole building, however many stories it has.
    ///
    /// This walks every level's devices in ONE pass rather than adding up per-level estimates, and
    /// that is the point rather than a tidiness preference: §5.4 prices a bundle as hub + sensors +
    /// ONE monthly system fee, so two summed estimates would charge a two-story home two
    /// subscriptions: the same per-system-not-per-device error this file exists to avoid, one level
    /// up. The hub-missing test has to see the whole building too: a hub downstairs routes a device
    /// upstairs perfectly well.
    /// </summary>
    public static Estimate Of(VariantDef variant)
    {
        if (variant?.levels == null) return new Estimate();

        var all = new List<SensorDef>();
        foreach (var l in variant.levels)
            if (l?.sensors != null) all.AddRange(l.sensors);
        return Of(all);
    }

    public static Estimate Of(IEnumerable<SensorDef> sensors)
    {
        var e = new Estimate();
        if (sensors == null) return e;

        bool needsRouting = false;
        bool hasHub = false;
        bool feeCounted = false;

        foreach (var s in sensors)
        {
            if (s == null || !s.included) continue;
            if (!SensorDevices.TryGet(s.deviceType, out var d)) continue;

            e.deviceCount++;
            if (d.speculative) e.speculativeCount++;

            e.upfrontLow += d.purchaseLow;
            e.upfrontHigh += d.purchaseHigh;

            if (d.id == "central_hub")
            {
                // The system fee is per SYSTEM. A second hub in a large home is more hardware on one
                // subscription, not a second subscription, so only the first one's monthly counts.
                hasHub = true;
                if (!feeCounted)
                {
                    feeCounted = true;
                    e.monthlyLow += d.monthlyLow;
                    e.monthlyHigh += d.monthlyHigh;
                }
                continue;
            }

            e.monthlyLow += d.monthlyLow;
            e.monthlyHigh += d.monthlyHigh;

            if (s.monitored) needsRouting = true;
        }

        if (needsRouting && !hasHub)
        {
            e.hubMissing = true;
            var hub = SensorDevices.Get("central_hub");
            e.monthlyLow += hub.monthlyLow;
            e.monthlyHigh += hub.monthlyHigh;
        }

        return e;
    }

    /// <summary>
    /// What answering <paramref name="alertsPerDay"/> alerts remotely would save in a month, at
    /// §5.2.2's $20-40 per incident. Returns zeroes for a package that raises nothing, which is the
    /// honest answer rather than a floor.
    /// </summary>
    public static void RemoteResponseSaving(float alertsPerDay, out float low, out float high)
    {
        float perMonth = Mathf.Max(0f, alertsPerDay) * DaysPerMonth;
        low = perMonth * RemoteResponseLow;
        high = perMonth * RemoteResponseHigh;
    }

    /// <summary>
    /// The monthly labour offset at <see cref="AssumedIncidentsPerWeek"/>. What the console and the
    /// report print, always beside the assumption itself. See that constant for why this is not
    /// derived from the demonstration day.
    /// </summary>
    public static void MonthlySaving(out float low, out float high)
        => RemoteResponseSaving(AssumedIncidentsPerWeek / 7f, out low, out high);

    /// <summary>"$257-$514 a month": the offset as one string, for a rail that has no room for two.</summary>
    public static string MonthlySavingRange()
    {
        MonthlySaving(out float low, out float high);
        return Range(low, high);
    }

    /// <summary>Devices in the package, grouped by category and counted: the report's own grouping.</summary>
    public static List<KeyValuePair<string, int>> ByCategory(LevelDef level)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, int>();

        if (level?.sensors != null)
            foreach (var s in level.sensors)
            {
                if (s == null || !s.included) continue;
                if (!SensorDevices.TryGet(s.deviceType, out var d)) continue;
                if (!counts.ContainsKey(d.category)) { counts[d.category] = 0; order.Add(d.category); }
                counts[d.category]++;
            }

        var rows = new List<KeyValuePair<string, int>>(order.Count);
        foreach (var c in order) rows.Add(new KeyValuePair<string, int>(c, counts[c]));
        return rows;
    }

    /// <summary>Devices in the package, grouped by device type and counted: "Door sensor x4".</summary>
    public static List<KeyValuePair<string, int>> ByDevice(VariantDef variant)
    {
        var all = new List<SensorDef>();
        foreach (var l in variant?.levels ?? new List<LevelDef>())
            if (l?.sensors != null) all.AddRange(l.sensors);
        return ByDevice(all);
    }

    public static List<KeyValuePair<string, int>> ByDevice(LevelDef level) => ByDevice(level?.sensors);

    public static List<KeyValuePair<string, int>> ByDevice(IEnumerable<SensorDef> sensors)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, int>();

        if (sensors != null)
            foreach (var s in sensors)
            {
                if (s == null || !s.included || string.IsNullOrEmpty(s.deviceType)) continue;
                if (!counts.ContainsKey(s.deviceType)) { counts[s.deviceType] = 0; order.Add(s.deviceType); }
                counts[s.deviceType]++;
            }

        var rows = new List<KeyValuePair<string, int>>(order.Count);
        foreach (var t in order) rows.Add(new KeyValuePair<string, int>(t, counts[t]));
        return rows;
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>"$79.95" / "$850-$1,550" / ": ". Money, not measurement, so it does NOT go through
    /// Units: that file converts meters, and a dollar is a dollar in both unit systems.</summary>
    public static string Money(float usd)
    {
        if (usd <= 0f) return "$0";
        return usd >= 100f ? "$" + usd.ToString("#,0") : "$" + usd.ToString("0.00");
    }

    private static string Range(float low, float high)
    {
        if (high <= 0f) return "None";
        if (Mathf.Approximately(low, high) || low <= 0f) return Money(high);
        return Money(low) + " - " + Money(high);
    }
}
