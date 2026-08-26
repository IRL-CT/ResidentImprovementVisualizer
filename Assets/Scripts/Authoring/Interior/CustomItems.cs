using System;
using System.Collections.Generic;
using System.Text;

// Furniture a resident owns that the catalog does not ship.
//
// WHY THIS EXISTS: the catalog is a fixed list, and every real residence has something outside it. The
// only recourse was to place the nearest catalog item and resize it, which leaves the plan asserting
// that a chest freezer is a wardrobe. One name and three dimensions is enough to answer the question
// the tool exists to answer ("does the wheelchair get past it"), so that is all a custom item carries.
//
// WHERE THE DEFINITION LIVES: on ResidenceDoc, beside `underlays`, for the same reason the underlay
// sits there. What the household owns is a fact about the dwelling, not a design option, so every
// variant sees the same list and the same item can stand in both Existing and a proposal. It travels
// inside the .riv export because the export is the whole document.
//
// THE ID IS THE INTERESTING PART. ObjectInstance.prefabType is the only durable link from something
// standing in a room back to what it is, and two readers of it cannot reach this list:
//
//   VariantDiff  labels furniture `Pretty(prefabType)` and lives in CXRAuthoring, holding two
//                VariantDefs and no document, so it can never look a definition up.
//   the renderer falls back to the raw key when the definition is gone, which is exactly what
//                deleting a custom item does to anything already placed.
//
// So the id EMBEDS the name as a slug ("Reading chair" -> "custom:reading_chair"). A Compare row, the
// HTML report and an orphaned placeholder box then all read "Reading chair" with no lookup at all. The
// prefix is what keeps the key space clean: no FurnitureCatalog id and no PrefabRegistry key contains
// a colon, so ResidenceRenderer.FindPrefab misses and the labeled box takes over, which is the whole
// intent. Names are never edited, so the slug never goes stale.
[Serializable]
public class CustomItemDef
{
    public string id;                 // "custom:reading_chair"; see CustomItems for the convention
    public string name;               // what the rail, the label and the report show
    public float widthM;              // local X, across the front
    public float depthM;              // local Z, front to back
    public float heightM;             // local Y
}

/// <summary>
/// The id convention for <see cref="CustomItemDef"/>, and the lookup off a document.
/// </summary>
/// <remarks>
/// Deliberately here rather than next to FurnitureCatalog: the catalog is a ScriptableObject in
/// Assembly-CSharp, which no test can reach, and these rules are the part worth testing.
/// </remarks>
public static class CustomItems
{
    /// <summary>Marks an id as one of these. No catalog id and no PrefabRegistry key contains a colon.</summary>
    public const string Prefix = "custom:";

    /// <summary>What a name of nothing but punctuation slugs to, so an id is never bare "custom:".</summary>
    public const string FallbackSlug = "item";

    public static bool IsCustom(string id)
        => !string.IsNullOrEmpty(id) && id.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// The key part of an id: lower case, runs of anything unhelpful collapsed to one underscore.
    /// Never empty, so <see cref="NameFromId"/> always has something to give back.
    /// </summary>
    public static string Slug(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return FallbackSlug;

        var sb = new StringBuilder(name.Length);
        bool pendingBreak = false;
        foreach (char c in name)
        {
            // An apostrophe is INSIDE a word, so it vanishes rather than splitting one: "Mom's
            // recliner" is "moms_recliner", which reads back as "Moms recliner". Breaking on it
            // instead gives "mom_s_recliner", and the name that comes back out of that is "Mom s
            // recliner", which is the one thing NameFromId exists to avoid.
            if (c == '\'' || c == '’') continue;

            if (char.IsLetterOrDigit(c))
            {
                // The break is spent only once something follows it, so a slug never leads or
                // trails with an underscore and "reading  chair!" is not "reading__chair_".
                if (pendingBreak && sb.Length > 0) sb.Append('_');
                pendingBreak = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            else pendingBreak = true;
        }

        return sb.Length > 0 ? sb.ToString() : FallbackSlug;
    }

    /// <summary>
    /// A fresh id for <paramref name="name"/>, unique against everything already in
    /// <paramref name="existing"/>. Two items honestly named the same get distinct keys, because the
    /// id is what a placed instance stores and one shared key would make them the same object.
    /// </summary>
    public static string NewId(string name, List<CustomItemDef> existing)
    {
        string stem = Prefix + Slug(name);
        if (!Taken(stem, existing)) return stem;

        // Starts at 2 so the first collision reads "chair" and "chair_2", the way a duplicated
        // residence name does in ResidenceStore.
        for (int n = 2; n < 10000; n++)
        {
            string candidate = stem + "_" + n;
            if (!Taken(candidate, existing)) return candidate;
        }
        return stem + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
    }

    /// <summary>
    /// A readable name recovered from an id alone. The fallback for anything placed whose definition
    /// has since been deleted, and the reason the slug carries the name in the first place.
    /// </summary>
    public static string NameFromId(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        string body = IsCustom(id) ? id.Substring(Prefix.Length) : id;
        body = body.Replace('_', ' ').Trim();
        if (body.Length == 0) return "";
        return char.ToUpperInvariant(body[0]) + body.Substring(1);
    }

    /// <summary>The definition behind an id, or null once it has been deleted.</summary>
    public static CustomItemDef Find(ResidenceDoc doc, string id)
    {
        if (doc?.customItems == null || string.IsNullOrEmpty(id)) return null;
        foreach (var def in doc.customItems)
            if (def != null && def.id == id) return def;
        return null;
    }

    private static bool Taken(string id, List<CustomItemDef> existing)
    {
        if (existing == null) return false;
        foreach (var def in existing)
            if (def != null && def.id == id) return true;
        return false;
    }
}
