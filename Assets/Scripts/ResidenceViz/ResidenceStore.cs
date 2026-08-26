using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;
// UnityEngine declares its own CompressionLevel (for AssetBundles), which collides with the one
// System.IO.Compression uses. Alias it so the zip code reads normally.
using CompressionLevel = System.IO.Compression.CompressionLevel;

// Local disk persistence for ResidenceViz. This is what replaces the Python server.
//
// The Residence Improvement Visualizer has to run on a care provider's laptop with no Python install, no localhost service, and
// no network, so LibraryClient's HTTP CRUD is gone and residences live in files under
// Application.persistentDataPath. LibraryClient itself is untouched and still serves the Site
// scene; nothing here calls it.
//
//     <persistentDataPath>/ResidenceImprovementVisualizer/
//         residences/<id>.json                  one ResidenceDoc per file
//         residences/_archive/<id>.json         soft-deleted, never destroyed
//         underlays/<id>/<image>           the traced sketch for that residence
//         settings.json
//
// Ported from server/server.py, because these are the parts that were actually protecting data:
//   * atomic write (temp file + replace): a torn write loses somebody's residence
//   * version bump on save
//   * name uniquing
//   * soft delete to _archive rather than File.Delete
//
// Deliberately NOT ported: the content-hash dedup (~115 lines of _canonical_signature /
// _find_duplicate / _dedup_existing_records). That existed only because layout generation re-POSTed
// identical BuildingDefs on every run. With AI generation out of scope there is no duplicate source,
// so carrying that machinery would be complexity guarding against nothing.
//
// The API is synchronous on purpose. LibraryClient's coroutine + callback shape exists to keep HTTP
// off the main thread; local file reads of a few hundred KB do not need it, and callbacks would make
// every call site harder to read for no benefit.
public static class ResidenceStore
{
    public const string FILE_EXT = ".json";
    public const string EXPORT_EXT = ".riv";

    // Archives written before the rename. Only the import file dialog cares: the reader itself
    // never looks at the extension, it validates by the document entry inside the zip.
    public const string LEGACY_EXPORT_EXT = ".homeviz";

    // The document inside an exported archive. Archives written before the rename carry the old
    // name, and are still read.
    public const string DOC_ENTRY = "residence.json";
    public const string LEGACY_DOC_ENTRY = "home.json";

    private static readonly JsonSerializerSettings JsonOpts = new JsonSerializerSettings
    {
        Formatting = Formatting.Indented,       // these files are meant to be inspectable by hand
        NullValueHandling = NullValueHandling.Include,
    };

    // ---------------------------------------------------------------------------------------
    // Paths
    // ---------------------------------------------------------------------------------------

    // The product name lands on disk TWICE: once as the persistentDataPath folder (Unity derives
    // that from productName) and once here. Keeping the inner one a const is what lets LegacyRoots
    // below pair an old product folder with the old folder inside it.
    private const string AppFolder = "ResidenceImprovementVisualizer";

    public static string RootDir => Path.Combine(Application.persistentDataPath, AppFolder);
    public static string ResidencesDir => Path.Combine(RootDir, "residences");
    public static string ArchiveDir => Path.Combine(ResidencesDir, "_archive");
    public static string UnderlaysDir => Path.Combine(RootDir, "underlays");
    public static string SettingsPath => Path.Combine(RootDir, "settings.json");

    public static string ResidencePath(string id) => Path.Combine(ResidencesDir, id + FILE_EXT);
    public static string UnderlayDir(string residenceId) => Path.Combine(UnderlaysDir, residenceId);

    /// <summary>Absolute path to a residence's underlay image, or null when it has none.</summary>
    public static string UnderlayPath(string residenceId, string fileName)
        => string.IsNullOrEmpty(fileName) ? null : Path.Combine(UnderlayDir(residenceId), fileName);

    private static void EnsureDirs()
    {
        Directory.CreateDirectory(ResidencesDir);
        Directory.CreateDirectory(ArchiveDir);
        Directory.CreateDirectory(UnderlaysDir);
    }

    // ---------------------------------------------------------------------------------------
    // Migration off older product names
    // ---------------------------------------------------------------------------------------

    // persistentDataPath is DERIVED from the Unity productName (<...>/LocalLow/<company>/<product>),
    // so every rename of the product moves the whole library out from under RootDir: every residence,
    // underlay and settings.json is still on disk but at an address nothing looks at any more. Move
    // the tree once instead.
    //
    // That has now happened twice (CXRBrownfield, then CXRHomeViz), so what used to be a single
    // legacy name is a CHAIN, newest first. Each entry carries its own INNER folder name as well:
    // under the old names the product folder and the folder inside it were the same word, and they
    // are not any more, so the two can no longer be the same string typed twice.
    //
    // This runs from a STATIC CONSTRUCTOR rather than from EnsureDirs because the Settings getter
    // reads SettingsPath without ever calling EnsureDirs, so a migration hung off EnsureDirs would
    // sometimes run after the first read. A static constructor is guaranteed to run before any static
    // member of this class is touched.
    private static readonly (string product, string app)[] LegacyRoots =
    {
        ("CXRHomeViz",    "CXRHomeViz"),
        ("CXRBrownfield", "CXRHomeViz"),
    };

    // Folders INSIDE the root that were renamed along with the product, old name first. The left
    // string is a LEGACY name and must stay spelled the old way: it is an address on disk, not a
    // word in the codebase, so a search-and-replace that renames it silently turns this into a no-op.
    private static readonly (string from, string to)[] LegacySubDirs =
    {
        ("homes", "residences"),
    };

    static ResidenceStore()
    {
        MigrateLegacyRoot();
        MigrateLegacySubDirs();
    }

    private static void MigrateLegacyRoot()
    {
        try
        {
            if (Directory.Exists(RootDir)) return;

            string companyDir = Directory.GetParent(Application.persistentDataPath)?.FullName;
            if (string.IsNullOrEmpty(companyDir)) return;

            foreach (var legacy in LegacyRoots)
            {
                string legacyRoot = Path.Combine(companyDir, legacy.product, legacy.app);
                if (!Directory.Exists(legacyRoot)) continue;

                Directory.CreateDirectory(Directory.GetParent(RootDir).FullName);
                Directory.Move(legacyRoot, RootDir);
                Debug.Log($"[ResidenceStore] Moved the library from the previous product folder: {legacyRoot} to {RootDir}");
                return;
            }
        }
        catch (Exception e)
        {
            // A failed migration must not stop the app booting. The library reads as empty and
            // re-seeds; the old folder is untouched and can be moved by hand.
            Debug.LogWarning($"[ResidenceStore] Could not move the library off the previous product folder: {e.Message}");
        }
    }

    // Runs on EVERY start, not only just after a root move: a root that an earlier build already
    // moved still carries the old subfolder names, and hanging this off the move would strand it.
    // It is a no-op once the rename has happened.
    private static void MigrateLegacySubDirs()
    {
        try
        {
            foreach (var dir in LegacySubDirs)
            {
                string from = Path.Combine(RootDir, dir.from);
                string to = Path.Combine(RootDir, dir.to);
                if (!Directory.Exists(from) || Directory.Exists(to)) continue;

                Directory.Move(from, to);
                Debug.Log($"[ResidenceStore] Renamed {dir.from} to {dir.to} inside the library.");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ResidenceStore] Could not rename a folder inside the library: {e.Message}");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Listing and loading
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Every residence in the library, favourites first then most-recently-updated. A file that fails to
    /// parse is skipped with a warning rather than aborting the list: one corrupt residence must not
    /// make the whole library unopenable.
    /// </summary>
    public static List<ResidenceSummary> List()
    {
        EnsureDirs();
        var result = new List<ResidenceSummary>();

        foreach (string path in Directory.GetFiles(ResidencesDir, "*" + FILE_EXT))
        {
            ResidenceDoc doc = TryRead(path);
            if (doc == null) continue;

            result.Add(new ResidenceSummary
            {
                id = doc.id,
                name = doc.name,
                version = doc.version,
                tags = doc.tags,
                updated = File.GetLastWriteTimeUtc(path).ToString("o"),
                favorite = doc.favorite,
                variantCount = doc.variants?.Count ?? 0,
                exteriorEnabled = doc.exteriorEnabled,
            });
        }

        result.Sort((a, b) =>
        {
            if (a.favorite != b.favorite) return b.favorite.CompareTo(a.favorite);
            return string.CompareOrdinal(b.updated, a.updated);
        });
        return result;
    }

    public static ResidenceDoc Load(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var doc = TryRead(ResidencePath(id));
        if (doc != null) Migrate(doc);
        return doc;
    }

    public static bool Exists(string id) => !string.IsNullOrEmpty(id) && File.Exists(ResidencePath(id));

    private static ResidenceDoc TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var doc = JsonConvert.DeserializeObject<ResidenceDoc>(File.ReadAllText(path));
            if (doc == null || string.IsNullOrEmpty(doc.id)) return null;
            return doc;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ResidenceStore] Could not read {path}: {e.Message}");
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Creating and saving
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A brand new residence: one "Existing" baseline variant holding a ground floor with the starter
    /// room already on it. It is created ready to edit: an empty document with no variant and no level
    /// would make every tool null-check its way through the first click.
    /// </summary>
    public static ResidenceDoc Create(string name)
    {
        EnsureDirs();

        string id = Guid.NewGuid().ToString();
        var level = NewLevel("Ground floor");

        // One plain room to build from rather than an empty grid, so drawing is a live alternative to
        // importing a plan, which is the harder half of the workflow. Adopt keeps the storey's own id,
        // name and elevation; StarterRoom.Build has already stemmed its ids, so no prefix is passed.
        SketchInstall.Adopt(level, StarterRoom.Build(), null);

        var baseline = new VariantDef
        {
            id = Guid.NewGuid().ToString(),
            name = "Existing",
            description = "The residence as it is today.",
            isBaseline = true,
            // UNLOCKED, where a sample's baseline is locked. Locking protects the RECORD of how a
            // residence actually is, and a residence started from scratch has no record yet: it has a
            // starter room somebody is about to move. The mode band opens amber on EDITING BASE
            // ENVIRONMENT with Done beside it, a state that already has a design. Every other baseline
            // (SampleResidences.Build) still ships locked.
            locked = false,
            levels = new List<LevelDef> { level },
            occupants = new List<OccupantDef>(),
        };

        var doc = new ResidenceDoc
        {
            id = id,
            name = UniqueName(string.IsNullOrWhiteSpace(name) ? "Untitled residence" : name.Trim(), null),
            version = 1,
            schemaVersion = ResidenceSchema.CURRENT,
            tags = new List<string>(),
            exteriorEnabled = false,
            underlays = new List<UnderlayDef>(),
            variants = new List<VariantDef> { baseline },
            activeVariantId = baseline.id,
        };

        Save(doc, out _);
        return doc;
    }

    public static LevelDef NewLevel(string name, float elevation = 0f) => Stories.NewLevel(name, elevation);

    /// <summary>
    /// Writes the residence, bumping its version. Atomic: the JSON goes to a temp file first and only
    /// replaces the real one once it is fully on disk, so killing the app mid-save leaves the
    /// previous version intact rather than a truncated file.
    /// </summary>
    public static bool Save(ResidenceDoc doc, out string error)
    {
        error = null;
        if (doc == null) { error = "Nothing to save."; return false; }
        if (string.IsNullOrEmpty(doc.id)) doc.id = Guid.NewGuid().ToString();

        try
        {
            EnsureDirs();
            string path = ResidencePath(doc.id);

            // Read the on-disk copy for the fields that other code paths write directly.
            var existing = TryRead(path);
            if (existing != null)
            {
                doc.version = existing.version + 1;
                // ToggleFavorite writes straight to disk, so an in-memory doc loaded before that
                // toggle would otherwise silently revert it.
                doc.favorite = existing.favorite;
            }
            else if (doc.version <= 0)
            {
                doc.version = 1;
            }

            doc.schemaVersion = ResidenceSchema.CURRENT;
            Migrate(doc);

            WriteAtomic(path, JsonConvert.SerializeObject(doc, JsonOpts));
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            Debug.LogError($"[ResidenceStore] Save failed for {doc.id}: {e}");
            return false;
        }
    }

    // Temp-then-replace. File.Replace preserves the destination's identity and is atomic on NTFS;
    // when there is no destination yet a plain move is already atomic.
    private static void WriteAtomic(string path, string contents)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);

        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    /// <summary>Soft delete. Nothing is ever destroyed: the file moves to residences/_archive/.</summary>
    public static bool Archive(string id)
    {
        try
        {
            string src = ResidencePath(id);
            if (!File.Exists(src)) return false;

            EnsureDirs();
            string dst = Path.Combine(ArchiveDir, id + FILE_EXT);
            if (File.Exists(dst)) File.Delete(dst);   // a previous archive of the same id
            File.Move(src, dst);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[ResidenceStore] Archive failed for {id}: {e}");
            return false;
        }
    }

    public static bool Restore(string id)
    {
        try
        {
            string src = Path.Combine(ArchiveDir, id + FILE_EXT);
            if (!File.Exists(src)) return false;
            File.Move(src, ResidencePath(id));
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[ResidenceStore] Restore failed for {id}: {e}");
            return false;
        }
    }

    public static bool ToggleFavorite(string id)
    {
        var doc = Load(id);
        if (doc == null) return false;

        doc.favorite = !doc.favorite;
        try
        {
            WriteAtomic(ResidencePath(id), JsonConvert.SerializeObject(doc, JsonOpts));
            return doc.favorite;
        }
        catch (Exception e)
        {
            Debug.LogError($"[ResidenceStore] Favorite toggle failed for {id}: {e}");
            return false;
        }
    }

    /// <summary>
    /// Deep-copies a residence under a new id and name. This is the "design on top of the twin" flow,
    /// Save As, and it is also how a residence is duplicated before a risky experiment.
    /// </summary>
    public static ResidenceDoc Duplicate(string id, string newName)
    {
        var src = Load(id);
        if (src == null) return null;

        var copy = Clone(src);
        copy.id = Guid.NewGuid().ToString();
        copy.name = UniqueName(string.IsNullOrWhiteSpace(newName) ? src.name + " copy" : newName, null);
        copy.version = 1;
        copy.favorite = false;

        // Carry the traced sketches across, otherwise the copy loses the thing everything was drawn
        // over. One per story now, so the whole folder moves rather than a single named file.
        foreach (var u in copy.underlays ?? new List<UnderlayDef>())
        {
            if (u == null || string.IsNullOrEmpty(u.imageFileName)) continue;
            string from = UnderlayPath(src.id, u.imageFileName);
            if (!File.Exists(from)) continue;
            Directory.CreateDirectory(UnderlayDir(copy.id));
            File.Copy(from, Path.Combine(UnderlayDir(copy.id), u.imageFileName), true);
        }

        Save(copy, out _);
        return copy;
    }

    /// <summary>
    /// Appends " (2)", " (3)", … until the name is unused. The Python original produced "Test2"; the
    /// parenthesised form reads better and matches what users expect from Save As.
    /// </summary>
    public static string UniqueName(string desired, string ignoreId)
    {
        if (string.IsNullOrWhiteSpace(desired)) desired = "Untitled residence";
        desired = desired.Trim();

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in List())
            if (s.id != ignoreId && !string.IsNullOrEmpty(s.name)) taken.Add(s.name);

        if (!taken.Contains(desired)) return desired;

        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{desired} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }
        return $"{desired} ({Guid.NewGuid().ToString().Substring(0, 6)})";
    }

    /// <summary>
    /// The default name for a new proposal: "Proposal 08/23/2026" for the first, then
    /// "Proposal 2 08/24/2026", "Proposal 3 …": the ordinal is the residence's overall proposal count
    /// (baseline excluded), the date is the day it was made. Uniqued against the residence's existing
    /// variant names by bumping the ordinal, so deleting one and adding another never repeats a
    /// name. It replaced "Proposal A" / "B" / "C", which walked past Z into punctuation and collided
    /// the moment a proposal was deleted.
    /// </summary>
    public static string NewProposalName(ResidenceDoc doc, DateTime when)
    {
        string date = when.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture);

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int proposals = 0;
        if (doc?.variants != null)
            foreach (var v in doc.variants)
            {
                if (v == null) continue;
                if (!v.isBaseline) proposals++;
                if (!string.IsNullOrEmpty(v.name)) taken.Add(v.name.Trim());
            }

        int n = proposals + 1;
        string candidate = n == 1 ? $"Proposal {date}" : $"Proposal {n} {date}";
        while (taken.Contains(candidate))
        {
            n++;
            candidate = $"Proposal {n} {date}";
        }
        return candidate;
    }

    public static T Clone<T>(T value) where T : class
        => value == null ? null : JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));

    // ---------------------------------------------------------------------------------------
    // Underlay images
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Copies a sketch into this residence's underlay folder and returns the stored filename. Copying
    /// rather than referencing the original matters: the source is usually a photo in Downloads that
    /// will be moved or deleted, and a residence that loses its underlay loses the only record of what was
    /// traced.
    /// </summary>
    public static string ImportUnderlay(string residenceId, string sourcePath, out string error)
    {
        error = null;
        try
        {
            if (string.IsNullOrEmpty(residenceId)) { error = "No residence is open."; return null; }
            if (!File.Exists(sourcePath)) { error = "That file no longer exists."; return null; }

            string dir = UnderlayDir(residenceId);
            Directory.CreateDirectory(dir);

            string name = SanitizeFileName(Path.GetFileName(sourcePath));
            string dest = Path.Combine(dir, name);

            // Re-importing the same filename replaces it; a different sketch gets a suffix so the
            // previous one is still recoverable.
            if (File.Exists(dest) && !SameFile(sourcePath, dest))
            {
                string stem = Path.GetFileNameWithoutExtension(name);
                string ext = Path.GetExtension(name);
                for (int i = 2; i < 1000; i++)
                {
                    string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
                    if (!File.Exists(candidate)) { dest = candidate; name = Path.GetFileName(candidate); break; }
                }
            }

            File.Copy(sourcePath, dest, true);
            return name;
        }
        catch (Exception e)
        {
            error = e.Message;
            Debug.LogError($"[ResidenceStore] Underlay import failed: {e}");
            return null;
        }
    }

    /// <summary>
    /// The same thing for a sketch that has no file of its own: a PDF page PdfRaster has just
    /// rasterized. `desiredName` is a suggestion; the returned filename is what was actually written,
    /// which may carry a "(2)" suffix under the same rule as the path form.
    ///
    /// A page is written straight from memory rather than through a temp file because the temp file
    /// would be the only copy for the moment between rendering and importing, and a crash there would
    /// leave a residence pointing at a sketch that never existed.
    /// </summary>
    public static string ImportUnderlayBytes(string residenceId, byte[] bytes, string desiredName,
                                             out string error)
    {
        error = null;
        try
        {
            if (string.IsNullOrEmpty(residenceId)) { error = "No residence is open."; return null; }
            if (bytes == null || bytes.Length == 0) { error = "That page produced no image."; return null; }

            string dir = UnderlayDir(residenceId);
            Directory.CreateDirectory(dir);

            string name = SanitizeFileName(desiredName);
            string dest = Path.Combine(dir, name);

            // Unlike the path form there is no source file to compare lengths against, so a collision
            // always takes a suffix. Re-importing the same PDF therefore keeps the previous render
            // rather than overwriting a page someone may already have traced against.
            if (File.Exists(dest))
            {
                string stem = Path.GetFileNameWithoutExtension(name);
                string ext = Path.GetExtension(name);
                for (int i = 2; i < 1000; i++)
                {
                    string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
                    if (!File.Exists(candidate)) { dest = candidate; name = Path.GetFileName(candidate); break; }
                }
            }

            File.WriteAllBytes(dest, bytes);
            return name;
        }
        catch (Exception e)
        {
            error = e.Message;
            Debug.LogError($"[ResidenceStore] Underlay page write failed: {e}");
            return null;
        }
    }

    private static bool SameFile(string a, string b)
    {
        try { return new FileInfo(a).Length == new FileInfo(b).Length; }
        catch { return false; }
    }

    // Keeps a filename from escaping its folder or carrying characters the OS rejects. The Python
    // server needed this against remote uploads; here it guards against odd names on a USB stick.
    public static string SanitizeFileName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "sketch.png";
        string cleaned = Regex.Replace(raw, @"[^A-Za-z0-9._ \-()]+", "_").Trim();
        return string.IsNullOrEmpty(cleaned) ? "sketch.png" : cleaned;
    }

    // ---------------------------------------------------------------------------------------
    // Export / import: the replacement for "the server" as a sharing mechanism
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Writes a single self-contained archive holding the residence JSON plus its underlay image, so a
    /// residence can be emailed to a colleague. This is the whole sharing story now that there is no
    /// server, which is why the underlay travels with it.
    /// </summary>
    public static bool ExportResidence(string id, string destPath, out string error)
    {
        error = null;
        try
        {
            var doc = Load(id);
            if (doc == null) { error = "That residence could not be read."; return false; }

            if (File.Exists(destPath)) File.Delete(destPath);
            using var stream = new FileStream(destPath, FileMode.CreateNew);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

            WriteEntry(zip, DOC_ENTRY, JsonConvert.SerializeObject(doc, JsonOpts));

            var written = new HashSet<string>();
            foreach (var u in doc.underlays ?? new List<UnderlayDef>())
            {
                string image = u?.imageFileName;
                if (string.IsNullOrEmpty(image) || !written.Add(image)) continue;

                string imagePath = UnderlayPath(id, image);
                if (!File.Exists(imagePath)) continue;

                var entry = zip.CreateEntry("underlay/" + image, CompressionLevel.Fastest);
                using var es = entry.Open();
                using var fs = File.OpenRead(imagePath);
                fs.CopyTo(es);
            }

            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            Debug.LogError($"[ResidenceStore] Export failed for {id}: {e}");
            return false;
        }
    }

    /// <summary>
    /// Reads an exported archive back in under a FRESH id, so importing a residence a colleague sent never
    /// overwrites a local one that happens to share its id (which it will, if they exported a copy of
    /// yours).
    /// </summary>
    public static ResidenceDoc ImportResidence(string zipPath, out string error)
    {
        error = null;
        try
        {
            if (!File.Exists(zipPath)) { error = "That file no longer exists."; return null; }
            EnsureDirs();

            using var zip = ZipFile.OpenRead(zipPath);

            var docEntry = zip.GetEntry(DOC_ENTRY) ?? zip.GetEntry(LEGACY_DOC_ENTRY);
            if (docEntry == null) { error = "Not a Residence Improvement Visualizer residence file."; return null; }

            ResidenceDoc doc;
            using (var reader = new StreamReader(docEntry.Open()))
                doc = JsonConvert.DeserializeObject<ResidenceDoc>(reader.ReadToEnd());

            if (doc == null) { error = "The residence file could not be read."; return null; }

            doc.id = Guid.NewGuid().ToString();
            doc.name = UniqueName(doc.name ?? "Imported residence", null);
            doc.version = 1;
            doc.favorite = false;
            Migrate(doc);

            // Old name -> the name it actually landed under, because SanitizeFileName may rewrite it.
            // This used to assign the LAST extracted entry's name to the one underlay, which was
            // indistinguishable from correct while a residence could only have one sketch and is simply
            // wrong the moment a two-story residence is shared: both floors would end up pointing at
            // whichever page the zip happened to enumerate second.
            var renamed = new Dictionary<string, string>();
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith("underlay/", StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(entry.Name)) continue;

                string dir = UnderlayDir(doc.id);
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, SanitizeFileName(entry.Name));

                using (var es = entry.Open())
                using (var fs = File.Create(dest))
                    es.CopyTo(fs);

                renamed[entry.Name] = Path.GetFileName(dest);
            }

            foreach (var u in doc.underlays ?? new List<UnderlayDef>())
            {
                if (u == null || string.IsNullOrEmpty(u.imageFileName)) continue;
                if (renamed.TryGetValue(u.imageFileName, out string landed)) u.imageFileName = landed;
            }

            Save(doc, out _);
            return doc;
        }
        catch (Exception e)
        {
            error = e.Message;
            Debug.LogError($"[ResidenceStore] Import failed: {e}");
            return null;
        }
    }

    private static void WriteEntry(ZipArchive zip, string name, string contents)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = new StreamWriter(entry.Open());
        s.Write(contents);
    }

    // ---------------------------------------------------------------------------------------
    // Settings
    // ---------------------------------------------------------------------------------------

    [Serializable]
    public class ResidenceSettings
    {
        // Metric, and it is the default. Storage was always metres; what changed is that the app now
        // SHOWS them, because the drag-number fields made feet-and-inches actively hard to read while
        // a value moves: "3' 11 5/8"" ticking to "4' 0 1/8"" is four glyphs changing at once where
        // "1.21 m" changes one digit. Every ADA figure the tool argues with is still one chip away.
        public bool metricUnits = true;

        // Which generation of the units DEFAULT this file has seen. A plain default only reaches new
        // installs; every settings.json already on disk records the old imperial default explicitly,
        // so without a version to compare against those installs would never move.
        public int unitsDefaultVersion;
        public float defaultWallThickness = ResidenceConventions.DEFAULT_WALL_THICKNESS;
        public float defaultCeilingHeight = ResidenceConventions.DEFAULT_CEILING_HEIGHT;
        public bool showExteriorLayer;      // top-level UI toggle; per-residence data still gates rendering
        public string lastOpenedResidenceId;
        // Set once SampleResidenceInstaller has given already-seeded samples the household they now ship with.
    // Separate from samplesSeeded because re-seeding is not an option. See BackfillOccupants.
    public bool occupantsBackfilled;

    // Set once SampleResidenceInstaller has written the built-in samples. It exists so that archiving a
        // sample keeps it archived. Without the flag, seeding would restore it on every launch.
        public bool samplesSeeded;
    }

    private static ResidenceSettings _settings;

    public static ResidenceSettings Settings
    {
        get
        {
            if (_settings != null) return _settings;
            try
            {
                if (File.Exists(SettingsPath))
                    _settings = JsonConvert.DeserializeObject<ResidenceSettings>(File.ReadAllText(SettingsPath));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ResidenceStore] Settings unreadable, using defaults: {e.Message}");
            }
            _settings ??= new ResidenceSettings();
            if (MigrateSettings()) SaveSettings();
            ApplySettings();
            return _settings;
        }
    }

    public static void SaveSettings()
    {
        try
        {
            EnsureDirs();
            ApplySettings();
            WriteAtomic(SettingsPath, JsonConvert.SerializeObject(Settings, JsonOpts));
        }
        catch (Exception e)
        {
            Debug.LogError($"[ResidenceStore] Could not save settings: {e}");
        }
    }

    private static void ApplySettings()
    {
        Units.Display = _settings.metricUnits ? Units.UnitSystem.Metric : Units.UnitSystem.FeetInches;
    }

    /// <summary>Brings an older settings file up to the current defaults. True if anything changed.</summary>
    // One version counter rather than a flag per setting, so the next default that has to move is a
    // case here rather than another bool. It runs from the Settings getter and not from EnsureDirs for
    // the reason MigrateLegacyRoot is a static constructor: the getter is reachable without EnsureDirs
    // ever having been called, so a migration hung off EnsureDirs would sometimes run after the first
    // read and hand the caller values it was supposed to have replaced.
    private static bool MigrateSettings()
    {
        if (_settings.unitsDefaultVersion >= UnitsDefaultVersion) return false;

        // Deliberately overwrites a stored preference, which nothing else here does. It is a one-time
        // move of the DEFAULT, not of the user's choice: the chip in the top bar still switches back,
        // and once it has been written the version guard means this never fires again.
        _settings.metricUnits = true;
        _settings.unitsDefaultVersion = UnitsDefaultVersion;
        return true;
    }

    private const int UnitsDefaultVersion = 1;

    // ---------------------------------------------------------------------------------------

    // Fills in anything a hand-edited or older file may be missing, so the rest of the code can rely
    // on the collections existing. Cheap insurance: every tool would otherwise need its own null guard.
    public static void Migrate(ResidenceDoc doc)
    {
        if (doc == null) return;

        doc.tags ??= new List<string>();
        doc.customItems ??= new List<CustomItemDef>();
        doc.variants ??= new List<VariantDef>();

        if (doc.variants.Count == 0)
            doc.variants.Add(new VariantDef
            {
                id = Guid.NewGuid().ToString(),
                name = "Existing",
                isBaseline = true,
                locked = true,
                levels = new List<LevelDef> { NewLevel("Ground floor") },
            });

        foreach (var v in doc.variants)
        {
            if (v == null) continue;
            v.id ??= Guid.NewGuid().ToString();
            v.levels ??= new List<LevelDef>();
            if (v.levels.Count == 0) v.levels.Add(NewLevel("Ground floor"));

            // Occupants postdate the first release, so every file written before them simply has no
            // key and lands here as null. Times are folded into the day rather than rejected: a
            // hand-edited 25:00 should become 1:00, not break the document.
            v.occupants ??= new List<OccupantDef>();
            foreach (var o in v.occupants)
            {
                if (o == null) continue;
                o.id ??= Guid.NewGuid().ToString();
                o.schedule ??= new List<ActivityDef>();
                foreach (var a in o.schedule)
                {
                    if (a == null) continue;
                    a.id ??= Guid.NewGuid().ToString();
                    a.kind = ActivityKind.IsKnown(a.kind) ? a.kind : ActivityKind.Other;
                    a.startMinutes = Clock.Wrap(a.startMinutes);
                    a.endMinutes = Clock.Wrap(a.endMinutes);
                }
            }

            foreach (var l in v.levels)
            {
                if (l == null) continue;
                l.id ??= Guid.NewGuid().ToString();
                l.walls ??= new List<WallDef>();
                l.openings ??= new List<OpeningDef>();
                l.rooms ??= new List<RoomDef>();
                l.furniture ??= new List<ObjectInstance>();
                l.wallMounted ??= new List<WallMountDef>();
                if (l.ceilingHeight <= 0f) l.ceilingHeight = ResidenceConventions.DEFAULT_CEILING_HEIGHT;
                if (l.wallThickness <= 0f) l.wallThickness = ResidenceConventions.DEFAULT_WALL_THICKNESS;
            }
        }

        // Make sure exactly one variant is flagged as the baseline.
        bool anyBaseline = false;
        foreach (var v in doc.variants) if (v.isBaseline) { anyBaseline = true; break; }
        if (!anyBaseline) doc.variants[0].isBaseline = true;

        if (string.IsNullOrEmpty(doc.activeVariantId) || FindVariant(doc, doc.activeVariantId) == null)
            doc.activeVariantId = doc.variants[0].id;

        Stories.MigrateUnderlays(doc);
    }

    // Stories and their sketches are pure document surgery, so they live in Stories.cs (CXRAuthoring)
    // where the EditMode suite can reach them: this file cannot be referenced by an asmdef, and its
    // static constructor touches Application.persistentDataPath, so anything left here is testable
    // only by running the Editor. These stay as the names the rest of the app already calls.

    public static UnderlayDef UnderlayFor(ResidenceDoc doc, string levelId) => Stories.UnderlayFor(doc, levelId);

    public static void SetUnderlay(ResidenceDoc doc, string levelId, UnderlayDef underlay)
        => Stories.SetUnderlay(doc, levelId, underlay);

    public static bool HasAnyUnderlay(ResidenceDoc doc) => Stories.HasAnyUnderlay(doc);

    public static int AddLevel(ResidenceDoc doc, string name = null) => Stories.Add(doc, name);

    public static bool RemoveLevel(ResidenceDoc doc, int index, out string error)
        => Stories.Remove(doc, index, out error);

    public static void RenameLevel(ResidenceDoc doc, string levelId, string name)
        => Stories.Rename(doc, levelId, name);

    public static string DefaultLevelName(int index) => Stories.DefaultName(index);

    public static VariantDef FindVariant(ResidenceDoc doc, string variantId)
    {
        if (doc?.variants == null || string.IsNullOrEmpty(variantId)) return null;
        foreach (var v in doc.variants) if (v != null && v.id == variantId) return v;
        return null;
    }

    public static VariantDef ActiveVariant(ResidenceDoc doc)
        => doc == null ? null : FindVariant(doc, doc.activeVariantId) ?? (doc.variants?.Count > 0 ? doc.variants[0] : null);

    public static VariantDef Baseline(ResidenceDoc doc)
    {
        if (doc?.variants == null) return null;
        foreach (var v in doc.variants) if (v != null && v.isBaseline) return v;
        return doc.variants.Count > 0 ? doc.variants[0] : null;
    }
}
