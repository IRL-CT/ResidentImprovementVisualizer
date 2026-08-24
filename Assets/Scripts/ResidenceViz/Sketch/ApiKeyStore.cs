using System;
using System.IO;
using UnityEngine;

// Where the Anthropic API key lives, and (more to the point) where it does not.
//
// NOT IN settings.json. ResidenceSettings is serialized wholesale by ResidenceStore.SaveSettings, it holds
// preferences rather than secrets, and it is the first file anyone would paste into a bug report or
// hand over when asking for help. A key riding along in that dump is a key leaked by a helpful user.
// Its own file, named for what it is, can be deleted by hand, cannot be mistaken for a preference,
// and cannot be swept up by a serializer that does not know one field is different from the others.
//
// NOT IN PlayerPrefs either: on Windows that is the registry, which is no more private and much
// harder for someone to find and remove.
//
// NOT IN THE EXPORT, by construction rather than by care. ResidenceStore.ExportResidence zips residence.json plus
// each underlay image and nothing else, so a file sitting in RootDir cannot ride along in a
// .riv archive. It is not in ResidenceDoc, so it is not in the undo snapshots either.
//
// THE ENVIRONMENT WINS. ANTHROPIC_API_KEY is read first so a machine can supply the key without it
// ever being written to disk, and when it does, the app must not offer to "forget" something it did
// not write.
//
// THE RISK, PLAINLY: what this does write is PLAINTEXT. Anyone with the machine, or with a backup of
// it, has the key. Windows DPAPI would fix that properly and is perhaps twenty lines, but
// System.Security.Cryptography.ProtectedData is Windows-only and is not guaranteed present in
// Unity's .NET profile, so it is a deliberate not-yet rather than an oversight, and the rail says
// so where the key is entered rather than leaving the user to assume otherwise.
public static class ApiKeyStore
{
    public enum Origin { None, Environment, File }

    private const string ENV_VAR = "ANTHROPIC_API_KEY";
    private const string FILE_NAME = "anthropic.key";

    private static string Path_ => System.IO.Path.Combine(ResidenceStore.RootDir, FILE_NAME);

    /// <summary>Where the key in force came from. Drives what the rail offers.</summary>
    public static Origin Source
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(FromEnvironment())) return Origin.Environment;
            return string.IsNullOrWhiteSpace(FromFile()) ? Origin.None : Origin.File;
        }
    }

    public static bool Has => Source != Origin.None;

    /// <summary>The key, or null. Never logged, never shown, never put in a document.</summary>
    public static string Get()
    {
        string env = FromEnvironment();
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        string file = FromFile();
        return string.IsNullOrWhiteSpace(file) ? null : file.Trim();
    }

    /// <summary>
    /// Enough of the key to confirm the right one is in force, and not enough to use. Shown on
    /// hover; the stored key itself is never redisplayed, because a field you can read back is a
    /// field a screenshot leaks.
    /// </summary>
    public static string Masked
    {
        get
        {
            string k = Get();
            if (string.IsNullOrEmpty(k)) return "none";
            return k.Length <= 8 ? "set" : k.Substring(0, 7) + "…" + k.Substring(k.Length - 4);
        }
    }

    public static bool Set(string key, out string error)
    {
        error = null;
        key = key?.Trim();

        if (string.IsNullOrEmpty(key)) { error = "That is not a key."; return false; }

        try
        {
            Directory.CreateDirectory(ResidenceStore.RootDir);
            File.WriteAllText(Path_, key);
            return true;
        }
        catch (Exception e)
        {
            error = "The key could not be saved: " + e.Message;
            return false;
        }
    }

    /// <summary>Deletes the stored key. Never touches the environment variable, which is not ours.</summary>
    public static void Forget()
    {
        try { if (File.Exists(Path_)) File.Delete(Path_); }
        catch (Exception e) { Debug.LogWarning("[ApiKeyStore] Could not delete the key file: " + e.Message); }
    }

    private static string FromEnvironment()
    {
        try { return Environment.GetEnvironmentVariable(ENV_VAR); }
        catch { return null; }
    }

    private static string FromFile()
    {
        try { return File.Exists(Path_) ? File.ReadAllText(Path_) : null; }
        catch { return null; }
    }
}
