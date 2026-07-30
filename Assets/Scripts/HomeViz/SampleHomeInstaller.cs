using UnityEngine;

// Writes the built-in samples from SampleHomes (CXRAuthoring) into the library (Assembly-CSharp).
//
// The split is deliberate: the plans themselves live in CXRAuthoring so EditModeTests can see them,
// and EditModeTests cannot reference Assembly-CSharp. So everything that touches HomeStore lives here
// and stays thin enough not to need tests of its own.
public static class SampleHomeInstaller
{
    /// <summary>
    /// Adds one sample to the library as a NEW home and returns it, or null if the key is unknown.
    /// A fresh GUID and a uniqued name mean the same sample can be installed repeatedly without
    /// overwriting the copy the user has already been editing.
    /// </summary>
    public static HomeDoc Install(string key)
    {
        var doc = SampleHomes.Build(key);
        if (doc == null)
        {
            Debug.LogWarning($"[SampleHomeInstaller] Unknown sample '{key}'.");
            return null;
        }

        doc.id = System.Guid.NewGuid().ToString();
        doc.name = HomeStore.UniqueName(doc.name, null);
        doc.version = 0;   // Save assigns 1 when there is no file to bump from

        if (HomeStore.Save(doc, out string error)) return doc;

        Debug.LogError($"[SampleHomeInstaller] Could not save sample '{key}': {error}");
        return null;
    }

    /// <summary>
    /// Installs every sample the first time this runs on a machine, then never again. Returns how
    /// many were written. Safe to call on every startup.
    /// </summary>
    public static int SeedIfNeeded()
    {
        if (HomeStore.Settings.samplesSeeded) return 0;

        int written = 0;
        foreach (var spec in SampleHomes.All)
            if (Install(spec.key) != null) written++;

        // Set the flag even on a partial failure: retrying every launch would keep re-adding whichever
        // samples did succeed, and a duplicated library is worse than a missing sample.
        HomeStore.Settings.samplesSeeded = true;
        HomeStore.SaveSettings();

        if (written > 0) Debug.Log($"[SampleHomeInstaller] Added {written} sample home(s).");
        return written;
    }

    /// <summary>
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
                Debug.LogWarning($"[SampleHomeInstaller] '{mirrored.id}' is in SampleFurniture but not "
                               + "in FurnitureCatalog — sample items using it will be mis-sized.");
                continue;
            }

            if (!Mathf.Approximately(entry.widthM, mirrored.width)
             || !Mathf.Approximately(entry.depthM, mirrored.depth)
             || !Mathf.Approximately(entry.heightM, mirrored.height))
                Debug.LogWarning($"[SampleHomeInstaller] '{mirrored.id}' size drifted: catalog has "
                               + $"{entry.widthM}x{entry.depthM}x{entry.heightM}, SampleFurniture has "
                               + $"{mirrored.width}x{mirrored.depth}x{mirrored.height}.");

            if (entry.IsWallMounted != mirrored.wallMounted)
                Debug.LogWarning($"[SampleHomeInstaller] '{mirrored.id}' mount type drifted.");
        }
    }
}
