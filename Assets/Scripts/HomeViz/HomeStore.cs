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

// Local disk persistence for HomeViz. This is what replaces the Python server.
//
// CXRHomeViz has to run on a care provider's laptop with no Python install, no localhost service, and
// no network — so LibraryClient's HTTP CRUD is gone and homes live in files under
// Application.persistentDataPath. LibraryClient itself is untouched and still serves the Brownfield
// scene; nothing here calls it.
//
//     <persistentDataPath>/CXRHomeViz/
//         homes/<id>.json                  one HomeDoc per file
//         homes/_archive/<id>.json         soft-deleted, never destroyed
//         underlays/<id>/<image>           the traced sketch for that home
//         settings.json
//
// Ported from server/server.py, because these are the parts that were actually protecting data:
//   * atomic write (temp file + replace) — a torn write loses somebody's home
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
public static class HomeStore
{
    public const string FILE_EXT = ".json";
    public const string EXPORT_EXT = ".homeviz";

    private static readonly JsonSerializerSettings JsonOpts = new JsonSerializerSettings
    {
        Formatting = Formatting.Indented,       // these files are meant to be inspectable by hand
        NullValueHandling = NullValueHandling.Include,
    };

    // ---------------------------------------------------------------------------------------
    // Paths
    // ---------------------------------------------------------------------------------------

    public static string RootDir => Path.Combine(Application.persistentDataPath, "CXRHomeViz");
    public static string HomesDir => Path.Combine(RootDir, "homes");
    public static string ArchiveDir => Path.Combine(HomesDir, "_archive");
    public static string UnderlaysDir => Path.Combine(RootDir, "underlays");
    public static string SettingsPath => Path.Combine(RootDir, "settings.json");

    public static string HomePath(string id) => Path.Combine(HomesDir, id + FILE_EXT);
    public static string UnderlayDir(string homeId) => Path.Combine(UnderlaysDir, homeId);

    /// <summary>Absolute path to a home's underlay image, or null when it has none.</summary>
    public static string UnderlayPath(string homeId, string fileName)
        => string.IsNullOrEmpty(fileName) ? null : Path.Combine(UnderlayDir(homeId), fileName);

    private static void EnsureDirs()
    {
        Directory.CreateDirectory(HomesDir);
        Directory.CreateDirectory(ArchiveDir);
        Directory.CreateDirectory(UnderlaysDir);
    }

    // ---------------------------------------------------------------------------------------
    // Listing and loading
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Every home in the library, favourites first then most-recently-updated. A file that fails to
    /// parse is skipped with a warning rather than aborting the list — one corrupt home must not
    /// make the whole library unopenable.
    /// </summary>
    public static List<HomeSummary> List()
    {
        EnsureDirs();
        var result = new List<HomeSummary>();

        foreach (string path in Directory.GetFiles(HomesDir, "*" + FILE_EXT))
        {
            HomeDoc doc = TryRead(path);
            if (doc == null) continue;

            result.Add(new HomeSummary
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

    public static HomeDoc Load(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var doc = TryRead(HomePath(id));
        if (doc != null) Migrate(doc);
        return doc;
    }

    public static bool Exists(string id) => !string.IsNullOrEmpty(id) && File.Exists(HomePath(id));

    private static HomeDoc TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var doc = JsonConvert.DeserializeObject<HomeDoc>(File.ReadAllText(path));
            if (doc == null || string.IsNullOrEmpty(doc.id)) return null;
            return doc;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[HomeStore] Could not read {path}: {e.Message}");
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Creating and saving
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A brand new home: one locked "Existing" baseline variant holding one empty ground floor. It is
    /// created ready to edit — an empty document with no variant and no level would make every tool
    /// null-check its way through the first click.
    /// </summary>
    public static HomeDoc Create(string name)
    {
        EnsureDirs();

        string id = Guid.NewGuid().ToString();
        var level = NewLevel("Ground floor");
        var baseline = new VariantDef
        {
            id = Guid.NewGuid().ToString(),
            name = "Existing",
            description = "The home as it is today.",
            isBaseline = true,
            // Locked by default: the baseline is the RECORD of how the home actually is, and
            // accidentally redesigning it would quietly destroy the thing every proposal is compared
            // against. Unlocking is one deliberate click.
            locked = true,
            levels = new List<LevelDef> { level },
        };

        var doc = new HomeDoc
        {
            id = id,
            name = UniqueName(string.IsNullOrWhiteSpace(name) ? "Untitled home" : name.Trim(), null),
            version = 1,
            schemaVersion = HomeSchema.CURRENT,
            tags = new List<string>(),
            exteriorEnabled = false,
            underlay = null,
            variants = new List<VariantDef> { baseline },
            activeVariantId = baseline.id,
        };

        Save(doc, out _);
        return doc;
    }

    public static LevelDef NewLevel(string name) => new LevelDef
    {
        id = Guid.NewGuid().ToString(),
        name = name,
        elevation = 0f,
        ceilingHeight = HomeConventions.DEFAULT_CEILING_HEIGHT,
        wallThickness = HomeConventions.DEFAULT_WALL_THICKNESS,
        walls = new List<WallDef>(),
        openings = new List<OpeningDef>(),
        rooms = new List<RoomDef>(),
        furniture = new List<ObjectInstance>(),
        wallMounted = new List<WallMountDef>(),
    };

    /// <summary>
    /// Writes the home, bumping its version. Atomic: the JSON goes to a temp file first and only
    /// replaces the real one once it is fully on disk, so killing the app mid-save leaves the
    /// previous version intact rather than a truncated file.
    /// </summary>
    public static bool Save(HomeDoc doc, out string error)
    {
        error = null;
        if (doc == null) { error = "Nothing to save."; return false; }
        if (string.IsNullOrEmpty(doc.id)) doc.id = Guid.NewGuid().ToString();

        try
        {
            EnsureDirs();
            string path = HomePath(doc.id);

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

            doc.schemaVersion = HomeSchema.CURRENT;
            Migrate(doc);

            WriteAtomic(path, JsonConvert.SerializeObject(doc, JsonOpts));
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            Debug.LogError($"[HomeStore] Save failed for {doc.id}: {e}");
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

    /// <summary>Soft delete. Nothing is ever destroyed — the file moves to homes/_archive/.</summary>
    public static bool Archive(string id)
    {
        try
        {
            string src = HomePath(id);
            if (!File.Exists(src)) return false;

            EnsureDirs();
            string dst = Path.Combine(ArchiveDir, id + FILE_EXT);
            if (File.Exists(dst)) File.Delete(dst);   // a previous archive of the same id
            File.Move(src, dst);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[HomeStore] Archive failed for {id}: {e}");
            return false;
        }
    }

    public static bool Restore(string id)
    {
        try
        {
            string src = Path.Combine(ArchiveDir, id + FILE_EXT);
            if (!File.Exists(src)) return false;
            File.Move(src, HomePath(id));
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[HomeStore] Restore failed for {id}: {e}");
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
            WriteAtomic(HomePath(id), JsonConvert.SerializeObject(doc, JsonOpts));
            return doc.favorite;
        }
        catch (Exception e)
        {
            Debug.LogError($"[HomeStore] Favorite toggle failed for {id}: {e}");
            return false;
        }
    }

    /// <summary>
    /// Deep-copies a home under a new id and name. This is the "design on top of the twin" flow —
    /// Save As — and it is also how a home is duplicated before a risky experiment.
    /// </summary>
    public static HomeDoc Duplicate(string id, string newName)
    {
        var src = Load(id);
        if (src == null) return null;

        var copy = Clone(src);
        copy.id = Guid.NewGuid().ToString();
        copy.name = UniqueName(string.IsNullOrWhiteSpace(newName) ? src.name + " copy" : newName, null);
        copy.version = 1;
        copy.favorite = false;

        // Carry the traced sketch across, otherwise the copy loses the thing everything was drawn over.
        if (src.underlay != null && !string.IsNullOrEmpty(src.underlay.imageFileName))
        {
            string from = UnderlayPath(src.id, src.underlay.imageFileName);
            if (File.Exists(from))
            {
                Directory.CreateDirectory(UnderlayDir(copy.id));
                File.Copy(from, Path.Combine(UnderlayDir(copy.id), src.underlay.imageFileName), true);
            }
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
        if (string.IsNullOrWhiteSpace(desired)) desired = "Untitled home";
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

    public static T Clone<T>(T value) where T : class
        => value == null ? null : JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));

    // ---------------------------------------------------------------------------------------
    // Underlay images
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Copies a sketch into this home's underlay folder and returns the stored filename. Copying
    /// rather than referencing the original matters: the source is usually a photo in Downloads that
    /// will be moved or deleted, and a home that loses its underlay loses the only record of what was
    /// traced.
    /// </summary>
    public static string ImportUnderlay(string homeId, string sourcePath, out string error)
    {
        error = null;
        try
        {
            if (string.IsNullOrEmpty(homeId)) { error = "No home is open."; return null; }
            if (!File.Exists(sourcePath)) { error = "That file no longer exists."; return null; }

            string dir = UnderlayDir(homeId);
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
            Debug.LogError($"[HomeStore] Underlay import failed: {e}");
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
    // Export / import — the replacement for "the server" as a sharing mechanism
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Writes a single self-contained archive holding the home JSON plus its underlay image, so a
    /// home can be emailed to a colleague. This is the whole sharing story now that there is no
    /// server, which is why the underlay travels with it.
    /// </summary>
    public static bool ExportHome(string id, string destPath, out string error)
    {
        error = null;
        try
        {
            var doc = Load(id);
            if (doc == null) { error = "That home could not be read."; return false; }

            if (File.Exists(destPath)) File.Delete(destPath);
            using var stream = new FileStream(destPath, FileMode.CreateNew);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

            WriteEntry(zip, "home.json", JsonConvert.SerializeObject(doc, JsonOpts));

            string image = doc.underlay?.imageFileName;
            string imagePath = UnderlayPath(id, image);
            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
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
            Debug.LogError($"[HomeStore] Export failed for {id}: {e}");
            return false;
        }
    }

    /// <summary>
    /// Reads an exported archive back in under a FRESH id, so importing a home a colleague sent never
    /// overwrites a local one that happens to share its id (which it will, if they exported a copy of
    /// yours).
    /// </summary>
    public static HomeDoc ImportHome(string zipPath, out string error)
    {
        error = null;
        try
        {
            if (!File.Exists(zipPath)) { error = "That file no longer exists."; return null; }
            EnsureDirs();

            using var zip = ZipFile.OpenRead(zipPath);

            var docEntry = zip.GetEntry("home.json");
            if (docEntry == null) { error = "Not a CXRHomeViz home file."; return null; }

            HomeDoc doc;
            using (var reader = new StreamReader(docEntry.Open()))
                doc = JsonConvert.DeserializeObject<HomeDoc>(reader.ReadToEnd());

            if (doc == null) { error = "The home file could not be read."; return null; }

            doc.id = Guid.NewGuid().ToString();
            doc.name = UniqueName(doc.name ?? "Imported home", null);
            doc.version = 1;
            doc.favorite = false;
            Migrate(doc);

            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith("underlay/", StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(entry.Name)) continue;

                string dir = UnderlayDir(doc.id);
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, SanitizeFileName(entry.Name));

                using var es = entry.Open();
                using var fs = File.Create(dest);
                es.CopyTo(fs);

                if (doc.underlay != null) doc.underlay.imageFileName = Path.GetFileName(dest);
            }

            Save(doc, out _);
            return doc;
        }
        catch (Exception e)
        {
            error = e.Message;
            Debug.LogError($"[HomeStore] Import failed: {e}");
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
    public class HomeSettings
    {
        public bool metricUnits;
        public float defaultWallThickness = HomeConventions.DEFAULT_WALL_THICKNESS;
        public float defaultCeilingHeight = HomeConventions.DEFAULT_CEILING_HEIGHT;
        public bool showExteriorLayer;      // master UI toggle; per-home data still gates rendering
        public string lastOpenedHomeId;
        // Set once SampleHomeInstaller has written the built-in samples. It exists so that archiving a
        // sample keeps it archived — without the flag, seeding would restore it on every launch.
        public bool samplesSeeded;
    }

    private static HomeSettings _settings;

    public static HomeSettings Settings
    {
        get
        {
            if (_settings != null) return _settings;
            try
            {
                if (File.Exists(SettingsPath))
                    _settings = JsonConvert.DeserializeObject<HomeSettings>(File.ReadAllText(SettingsPath));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HomeStore] Settings unreadable, using defaults: {e.Message}");
            }
            _settings ??= new HomeSettings();
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
            Debug.LogError($"[HomeStore] Could not save settings: {e}");
        }
    }

    private static void ApplySettings()
    {
        Units.Display = _settings.metricUnits ? Units.UnitSystem.Metric : Units.UnitSystem.FeetInches;
    }

    // ---------------------------------------------------------------------------------------

    // Fills in anything a hand-edited or older file may be missing, so the rest of the code can rely
    // on the collections existing. Cheap insurance: every tool would otherwise need its own null guard.
    public static void Migrate(HomeDoc doc)
    {
        if (doc == null) return;

        doc.tags ??= new List<string>();
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

            foreach (var l in v.levels)
            {
                if (l == null) continue;
                l.id ??= Guid.NewGuid().ToString();
                l.walls ??= new List<WallDef>();
                l.openings ??= new List<OpeningDef>();
                l.rooms ??= new List<RoomDef>();
                l.furniture ??= new List<ObjectInstance>();
                l.wallMounted ??= new List<WallMountDef>();
                if (l.ceilingHeight <= 0f) l.ceilingHeight = HomeConventions.DEFAULT_CEILING_HEIGHT;
                if (l.wallThickness <= 0f) l.wallThickness = HomeConventions.DEFAULT_WALL_THICKNESS;
            }
        }

        // Make sure exactly one variant is flagged as the baseline.
        bool anyBaseline = false;
        foreach (var v in doc.variants) if (v.isBaseline) { anyBaseline = true; break; }
        if (!anyBaseline) doc.variants[0].isBaseline = true;

        if (string.IsNullOrEmpty(doc.activeVariantId) || FindVariant(doc, doc.activeVariantId) == null)
            doc.activeVariantId = doc.variants[0].id;
    }

    public static VariantDef FindVariant(HomeDoc doc, string variantId)
    {
        if (doc?.variants == null || string.IsNullOrEmpty(variantId)) return null;
        foreach (var v in doc.variants) if (v != null && v.id == variantId) return v;
        return null;
    }

    public static VariantDef ActiveVariant(HomeDoc doc)
        => doc == null ? null : FindVariant(doc, doc.activeVariantId) ?? (doc.variants?.Count > 0 ? doc.variants[0] : null);

    public static VariantDef Baseline(HomeDoc doc)
    {
        if (doc?.variants == null) return null;
        foreach (var v in doc.variants) if (v != null && v.isBaseline) return v;
        return doc.variants.Count > 0 ? doc.variants[0] : null;
    }
}
