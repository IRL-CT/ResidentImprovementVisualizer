using UnityEngine;

// Writes the built-in samples from SampleResidences (CXRAuthoring) into the library (Assembly-CSharp).
//
// The split is deliberate: the plans themselves live in CXRAuthoring so EditModeTests can see them,
// and EditModeTests cannot reference Assembly-CSharp. So everything that touches ResidenceStore lives here
// and stays thin enough not to need tests of its own.
public static class SampleResidenceInstaller
{
    /// <summary>
    /// Adds one sample to the library as a NEW residence and returns it, or null if the key is unknown.
    /// A fresh GUID and a uniqued name mean the same sample can be installed repeatedly without
    /// overwriting the copy the user has already been editing.
    /// </summary>
    public static ResidenceDoc Install(string key)
    {
        var doc = SampleResidences.Build(key);
        if (doc == null)
        {
            Debug.LogWarning($"[SampleResidenceInstaller] Unknown sample '{key}'.");
            return null;
        }

        doc.id = System.Guid.NewGuid().ToString();
        doc.name = ResidenceStore.UniqueName(doc.name, null);
        doc.version = 0;   // Save assigns 1 when there is no file to bump from

        if (ResidenceStore.Save(doc, out string error)) return doc;

        Debug.LogError($"[SampleResidenceInstaller] Could not save sample '{key}': {error}");
        return null;
    }

    /// <summary>
    /// Installs every sample the first time this runs on a machine, then never again. Returns how
    /// many were written. Safe to call on every startup.
    /// </summary>
    public static int SeedIfNeeded()
    {
        if (ResidenceStore.Settings.samplesSeeded) return 0;

        int written = 0;
        foreach (var spec in SampleResidences.All)
            if (Install(spec.key) != null) written++;

        // Set the flag even on a partial failure: retrying every launch would keep re-adding whichever
        // samples did succeed, and a duplicated library is worse than a missing sample.
        ResidenceStore.Settings.samplesSeeded = true;
        ResidenceStore.SaveSettings();

        if (written > 0) Debug.Log($"[SampleResidenceInstaller] Added {written} sample residence(s).");
        return written;
    }

    /// <summary>
    /// Gives already-seeded samples the household they now ship with, once per machine.
    ///
    /// Occupants postdate the samples, and `samplesSeeded` deliberately stops the seeder ever running
    /// twice, so on any install that predates them, all six samples sit in the library with nobody in
    /// them and the People view opens empty. Re-seeding is not the fix: it would resurrect samples the
    /// user archived and duplicate the ones they kept.
    ///
    /// So this fills in the missing roster in place, under three conditions that together make it safe:
    /// the residence is tagged `sample`, its baseline roster is EMPTY (so nothing a user wrote is touched),
    /// and its room ids exactly match the sample it is named after. That last one is the real guard,
    /// the schedules address rooms by id, so if the plan has been reworked the ids will not match and
    /// the residence is left alone rather than having a household dropped into rooms that moved.
    /// </summary>
    public static int BackfillOccupants()
    {
        if (ResidenceStore.Settings.occupantsBackfilled) return 0;

        int filled = 0;
        foreach (var row in ResidenceStore.List())
        {
            if (row.tags == null || !row.tags.Contains("sample")) continue;

            var doc = ResidenceStore.Load(row.id);
            var baseline = ResidenceStore.Baseline(doc);
            if (baseline == null || (baseline.occupants != null && baseline.occupants.Count > 0)) continue;

            var fresh = MatchingSample(doc.name);
            if (fresh == null) continue;

            var freshBaseline = fresh.variants[0];
            if (!SameRooms(baseline, freshBaseline)) continue;

            baseline.occupants = freshBaseline.occupants;
            if (ResidenceStore.Save(doc, out string error)) filled++;
            else Debug.LogWarning($"[SampleResidenceInstaller] Could not update '{doc.name}': {error}");
        }

        ResidenceStore.Settings.occupantsBackfilled = true;
        ResidenceStore.SaveSettings();

        if (filled > 0) Debug.Log($"[SampleResidenceInstaller] Added the household to {filled} sample residence(s).");
        return filled;
    }

    /// <summary>
    /// Brings already-seeded samples up to the current plans, whenever they can be brought up safely.
    ///
    /// Unlike BackfillOccupants this is not a one-shot patch for one drift: the generation each residence
    /// was built from is stored on the residence, so this keeps working for every future change to
    /// SampleResidences. It replaces the document wholesale rather than merging, which is why
    /// SampleRefresh.Evaluate is so conservative about which residences qualify. See that file.
    /// </summary>
    public static int RefreshStaleSamples()
    {
        int refreshed = 0;
        int skipped = 0;

        foreach (var row in ResidenceStore.List())
        {
            if (row.tags == null || !row.tags.Contains("sample")) continue;

            var doc = ResidenceStore.Load(row.id);
            var verdict = SampleRefresh.Evaluate(doc, SampleResidences.Generation);
            if (verdict == SampleRefresh.Verdict.UserEdited) { skipped++; continue; }
            if (verdict != SampleRefresh.Verdict.Refresh) continue;

            string key = ResolveKey(doc);
            if (string.IsNullOrEmpty(key)) continue;

            if (ReplaceWithSample(doc, key, out string error)) refreshed++;
            else Debug.LogWarning($"[SampleResidenceInstaller] Could not refresh '{doc.name}': {error}");
        }

        if (refreshed > 0)
            Debug.Log($"[SampleResidenceInstaller] Updated {refreshed} sample residence(s) to the latest plans.");
        if (skipped > 0)
            Debug.Log($"[SampleResidenceInstaller] Left {skipped} edited sample residence(s) alone. Use "
                    + "\"Reset to the latest sample\" to update one by hand.");

        return refreshed;
    }

    /// <summary>
    /// Rebuilds one residence from its sample, discarding whatever is in it. This is the explicit path,
    /// for a residence SampleRefresh refused to touch automatically; the caller is responsible for
    /// confirming, because proposals are destroyed.
    /// </summary>
    public static bool ResetToSample(ResidenceDoc doc, out string error)
    {
        error = null;
        if (doc == null) { error = "No residence."; return false; }

        string key = ResolveKey(doc);
        if (string.IsNullOrEmpty(key)) { error = "This residence did not come from a sample."; return false; }

        return ReplaceWithSample(doc, key, out error);
    }

    /// <summary>The sample key a residence can be rebuilt from, or null if it cannot be.</summary>
    public static string ResolveKey(ResidenceDoc doc)
    {
        if (doc == null) return null;
        if (!string.IsNullOrEmpty(doc.sampleKey) && SampleResidences.TryGetSpec(doc.sampleKey, out _))
            return doc.sampleKey;
        return KeyFromName(doc.name);
    }

    // Swaps in freshly built content while keeping everything that identifies the residence in the library:
    // its id (so the last-opened setting and any open reference survive), its name, its tags and its
    // favourite flag. ResidenceStore.Save owns the version bump.
    private static bool ReplaceWithSample(ResidenceDoc doc, string key, out string error)
    {
        error = null;

        var fresh = SampleResidences.Build(key);
        if (fresh == null) { error = $"Unknown sample '{key}'."; return false; }

        // The name is the user's. Except when it is verbatim a name we ourselves retired, in which
        // case keeping it would leave the library saying "Group home apartment" while the sample
        // picker beside it says "Shared home apartment". A name that matched the legacy table exactly
        // cannot have been typed by anyone, so following the rename is safe; any other name is left
        // alone, including "… (2)", whose suffix is carried across.
        doc.name = RenamedFromLegacy(doc.name, key) ?? doc.name;

        doc.variants = fresh.variants;
        doc.activeVariantId = fresh.activeVariantId;
        doc.exteriorEnabled = fresh.exteriorEnabled;
        doc.schemaVersion = fresh.schemaVersion;
        doc.sampleKey = fresh.sampleKey;
        doc.sampleGeneration = fresh.sampleGeneration;

        return ResidenceStore.Save(doc, out error);
    }

    // Samples are installed under their displayName, uniqued, so "Studio apartment" and any later
    // "Studio apartment (2)" both resolve to the studio.
    private static ResidenceDoc MatchingSample(string name)
    {
        string key = KeyFromName(name);
        return key == null ? null : SampleResidences.Build(key);
    }

    // Display names are not a stable key: "Group home apartment" was renamed to "Shared home
    // apartment", so a residence seeded before ResidenceDoc.sampleKey existed can no longer be matched against
    // the live spec list. Those are the residences carrying the oldest geometry, so losing them to a rename
    // would mean the worst offender is the one thing never fixed. Every retired name maps here.
    private static readonly System.Collections.Generic.Dictionary<string, string> LegacyNames =
        new System.Collections.Generic.Dictionary<string, string>
        {
            { "Group home apartment — 5 bed, 4 bath", "apartment_5b4b" },
            { "Shared home apartment — 5 bed, 4 bath", "apartment_5b4b" },
            { "Apartment — 2 bed, 1 bath",             "apartment_2b1b" },
            { "House — 2 bed, 1 bath",                 "house_2b1b" },
            { "House — 3 bed, 2 bath",                 "house_3b2b" },
            { "Assisted living house — 5 bed, 4 bath",  "house_5b4b" },
        };

    // The current display name for a residence still carrying a retired one, or null if it is not one.
    private static string RenamedFromLegacy(string name, string key)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (!SampleResidences.TryGetSpec(key, out var spec)) return null;

        foreach (var pair in LegacyNames)
        {
            if (pair.Value != key) continue;
            if (name == pair.Key) return spec.displayName;
            if (name.StartsWith(pair.Key + " (")) return spec.displayName + name.Substring(pair.Key.Length);
        }

        return null;
    }

    private static string KeyFromName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        foreach (var spec in SampleResidences.All)
            if (name == spec.displayName || name.StartsWith(spec.displayName + " ("))
                return spec.key;

        foreach (var pair in LegacyNames)
            if (name == pair.Key || name.StartsWith(pair.Key + " ("))
                return pair.Value;

        return null;
    }

    // Every room id the schedules reference, present and unchanged. Anything less and the backfill
    // would be guessing.
    private static bool SameRooms(VariantDef stored, VariantDef fresh)
    {
        // Story for story, not levels[0] against levels[0]. This guard is what decides whether a
        // roster addressed by room id can safely be dropped into a plan on disk, so a stored residence
        // that has gained a floor since must read as "not the same rooms" rather than as a match on
        // its ground floor.
        if (stored.levels == null || fresh.levels == null) return false;
        if (stored.levels.Count != fresh.levels.Count) return false;

        for (int i = 0; i < fresh.levels.Count; i++)
        {
            var storedLevel = stored.levels[i];
            var freshLevel = fresh.levels[i];
            if (storedLevel?.rooms == null || freshLevel?.rooms == null) return false;
            if (storedLevel.rooms.Count != freshLevel.rooms.Count) return false;

            var ids = new System.Collections.Generic.HashSet<string>();
            foreach (var r in storedLevel.rooms) if (r != null) ids.Add(r.id);
            foreach (var r in freshLevel.rooms) if (r == null || !ids.Contains(r.id)) return false;
        }
        return true;
    }

    /// <summary>
    /// <summary>
    /// Warns if any room type maps to a floor material the palette does not hold.
    ///
    /// Same cross-assembly problem as SampleFurniture, solved the same way: RoomFinish is in
    /// CXRAuthoring so it can be tested, InteriorMaterialPalette is a ScriptableObject in
    /// Assembly-CSharp so it cannot be reached from there, and the two are compared once at seed. The
    /// failure this catches is silent: an unknown id falls through Get to defaultFloor, so a room
    /// type whose material was never added simply renders as vinyl and nothing says so.
    /// </summary>
    public static void VerifyFloorFinishes(InteriorMaterialPalette palette)
    {
        if (palette == null) return;

        foreach (string roomType in RoomFinish.All)
        {
            string id = RoomFinish.FloorMaterial(roomType);
            if (!palette.Has(id))
                Debug.LogWarning($"[SampleResidenceInstaller] room type '{roomType}' wants floor material "
                               + $"'{id}', which InteriorMaterialPalette does not contain. Every "
                               + "room of that type will silently fall back to the default floor.");
        }
    }

    /// Reports any drift between SampleFurniture (the copy PlanBuilder measures against) and the real
    /// catalog. SampleFurniture exists only because CXRAuthoring cannot reach a ScriptableObject in
    /// Assembly-CSharp, so this is the one place the two can be compared.
    /// </summary>
    public static void VerifyAgainstCatalog(FurnitureCatalog catalog)
    {
        if (catalog == null) return;

        foreach (var mirrored in SampleFurniture.All)
        {
            var entry = catalog.Get(mirrored.id);
            if (entry == null)
            {
                Debug.LogWarning($"[SampleResidenceInstaller] '{mirrored.id}' is in SampleFurniture but not "
                               + "in FurnitureCatalog, so sample items using it will be mis-sized.");
                continue;
            }

            if (!Mathf.Approximately(entry.widthM, mirrored.width)
             || !Mathf.Approximately(entry.depthM, mirrored.depth)
             || !Mathf.Approximately(entry.heightM, mirrored.height))
                Debug.LogWarning($"[SampleResidenceInstaller] '{mirrored.id}' size drifted: catalog has "
                               + $"{entry.widthM}x{entry.depthM}x{entry.heightM}, SampleFurniture has "
                               + $"{mirrored.width}x{mirrored.depth}x{mirrored.height}.");

            if (entry.IsWallMounted != mirrored.wallMounted)
                Debug.LogWarning($"[SampleResidenceInstaller] '{mirrored.id}' mount type drifted.");
        }
    }

    /// <summary>
    /// The same drift check for the smart home devices. SensorDevices mirrors SensorCatalog for the
    /// identical reason SampleFurniture mirrors FurnitureCatalog. CXRAuthoring cannot reach a
    /// ScriptableObject, and SensorFit, SensorSim and SensorPackages all live there.
    /// </summary>
    /// <remarks>
    /// Coverage and cost are checked as well as size, because those are the two the duplication would
    /// go wrong in a way nobody notices: a radius that drifts changes every coverage figure in the
    /// rail and the report against a catalog nothing reads, and a price that drifts is a number a care
    /// team takes to a funder.
    /// </remarks>
    public static void VerifyAgainstCatalog(SensorCatalog catalog)
    {
        if (catalog == null) return;

        foreach (var mirrored in SensorDevices.All)
        {
            var entry = catalog.Get(mirrored.id);
            if (entry == null)
            {
                Debug.LogWarning($"[SampleResidenceInstaller] '{mirrored.id}' is in SensorDevices but not "
                               + "in SensorCatalog, so the picker will not offer it.");
                continue;
            }

            if (!Mathf.Approximately(entry.widthM, mirrored.width)
             || !Mathf.Approximately(entry.depthM, mirrored.depth)
             || !Mathf.Approximately(entry.heightM, mirrored.height))
                Debug.LogWarning($"[SampleResidenceInstaller] '{mirrored.id}' size drifted.");

            if (!Mathf.Approximately(entry.coverageRadiusM, mirrored.coverageRadius)
             || !Mathf.Approximately(entry.coverageAngleDeg, mirrored.coverageAngle))
                Debug.LogWarning($"[SampleResidenceInstaller] '{mirrored.id}' coverage drifted: catalog has "
                               + $"{entry.coverageRadiusM} m / {entry.coverageAngleDeg}°, SensorDevices "
                               + $"has {mirrored.coverageRadius} m / {mirrored.coverageAngle}°.");

            if (!Mathf.Approximately(entry.purchaseLowUsd, mirrored.purchaseLow)
             || !Mathf.Approximately(entry.purchaseHighUsd, mirrored.purchaseHigh)
             || !Mathf.Approximately(entry.monthlyLowUsd, mirrored.monthlyLow)
             || !Mathf.Approximately(entry.monthlyHighUsd, mirrored.monthlyHigh))
                Debug.LogWarning($"[SampleResidenceInstaller] '{mirrored.id}' cost drifted.");

            if (entry.hostKind != mirrored.hostKind)
                Debug.LogWarning($"[SampleResidenceInstaller] '{mirrored.id}' host kind drifted: catalog "
                               + $"says {entry.hostKind}, SensorDevices says {mirrored.hostKind}.");

            if (entry.privacy != mirrored.privacy)
                Debug.LogWarning($"[SampleResidenceInstaller] '{mirrored.id}' privacy tier drifted. The "
                               + "console's role filters read the catalog's.");
        }

        foreach (var entry in catalog.entries)
            if (entry != null && !string.IsNullOrEmpty(entry.id) && !SensorDevices.Exists(entry.id))
                Debug.LogWarning($"[SampleResidenceInstaller] '{entry.id}' is in SensorCatalog but not in "
                               + "SensorDevices, so it can be placed, but nothing will simulate it.");
    }
}
