using System.Collections.Generic;
using NUnit.Framework;

// The id convention behind "Make your own".
//
// What is pinned here is the reason the id is a slug of the name rather than a guid: two readers of
// ObjectInstance.prefabType cannot reach the definition list. VariantDiff holds two VariantDefs and
// no document, and the renderer has nothing to look up once a definition is deleted. Both recover
// the name from the key alone, so a change list and an orphaned box read "Reading chair" instead of
// a raw key. Break the round trip below and both go quietly wrong: nothing throws, the names just
// turn into rubbish.
[TestFixture]
public class CustomItemsTests
{
    [Test]
    public void Slug_LowersAndJoinsOnUnderscores()
    {
        Assert.AreEqual("reading_chair", CustomItems.Slug("Reading chair"));
        Assert.AreEqual("reading_chair", CustomItems.Slug("READING CHAIR"));
        Assert.AreEqual("chest_freezer", CustomItems.Slug("  chest   freezer  "),
                        "Runs of whitespace collapse, and a slug never leads or trails with one.");
        Assert.AreEqual("moms_recliner", CustomItems.Slug("Mom's recliner"),
                        "An apostrophe sits inside a word, so it vanishes rather than splitting one.");
        Assert.AreEqual("chair_v2", CustomItems.Slug("Chair (v2)"),
                        "Other punctuation is a break.");
        Assert.AreEqual("bed_2", CustomItems.Slug("Bed 2"), "Digits are kept.");
    }

    [Test]
    public void Slug_NeverReturnsEmpty()
    {
        // Otherwise the id is a bare "custom:", NameFromId has nothing to give back, and every item
        // named only in punctuation collides with every other one.
        Assert.AreEqual(CustomItems.FallbackSlug, CustomItems.Slug("!!!"));
        Assert.AreEqual(CustomItems.FallbackSlug, CustomItems.Slug("   "));
        Assert.AreEqual(CustomItems.FallbackSlug, CustomItems.Slug(null));
    }

    [Test]
    public void NewId_UniquesAgainstWhatIsAlreadyThere()
    {
        var existing = new List<CustomItemDef>();

        string first = CustomItems.NewId("Reading chair", existing);
        Assert.AreEqual("custom:reading_chair", first);
        existing.Add(new CustomItemDef { id = first, name = "Reading chair" });

        // Two items honestly named the same is a real case, and one shared key would make them the
        // same object everywhere a placement stores it.
        string second = CustomItems.NewId("Reading chair", existing);
        Assert.AreEqual("custom:reading_chair_2", second);
        existing.Add(new CustomItemDef { id = second, name = "Reading chair" });

        Assert.AreEqual("custom:reading_chair_3", CustomItems.NewId("Reading chair", existing));
    }

    [Test]
    public void NewId_TreatsANullListAsEmpty()
        => Assert.AreEqual("custom:desk", CustomItems.NewId("Desk", null));

    [Test]
    public void NameFromId_RoundTripsAReadableName()
    {
        // THE contract. This is what a Compare row and a deleted item's box both fall back to.
        foreach (string name in new[] { "Reading chair", "Chest freezer", "Mom's recliner", "Bed 2" })
        {
            string id = CustomItems.NewId(name, null);
            string recovered = CustomItems.NameFromId(id);

            Assert.IsFalse(string.IsNullOrEmpty(recovered), $"'{name}' recovered as nothing.");
            Assert.AreEqual(char.ToUpperInvariant(recovered[0]), recovered[0],
                            "A recovered name leads with a capital.");
            Assert.IsFalse(recovered.Contains("_"), $"'{recovered}' still reads as a key.");
            Assert.IsFalse(recovered.Contains(":"), $"'{recovered}' still carries the prefix.");
        }

        Assert.AreEqual("Reading chair", CustomItems.NameFromId("custom:reading_chair"));
        Assert.AreEqual("Reading chair 2", CustomItems.NameFromId("custom:reading_chair_2"));
    }

    [Test]
    public void IsCustom_SeparatesTheTwoKeySpaces()
    {
        Assert.IsTrue(CustomItems.IsCustom("custom:reading_chair"));
        Assert.IsFalse(CustomItems.IsCustom("sofa"));
        Assert.IsFalse(CustomItems.IsCustom(""));
        Assert.IsFalse(CustomItems.IsCustom(null));
    }

    [Test]
    public void NoCatalogIdLooksCustom()
    {
        // The prefix is what makes ResidenceRenderer.FindPrefab miss and the labeled box take over.
        // A catalog id that started with it would be resolved as a custom item and lose its art.
        foreach (var item in SampleFurniture.All)
            Assert.IsFalse(CustomItems.IsCustom(item.id),
                           $"Catalog id '{item.id}' collides with the custom key space.");
    }

    [Test]
    public void Find_ResolvesAndReportsADeletedDefinitionAsGone()
    {
        var def = new CustomItemDef
        {
            id = CustomItems.NewId("Reading chair", null),
            name = "Reading chair",
            widthM = 0.8f, depthM = 0.9f, heightM = 1.0f,
        };
        var doc = new ResidenceDoc { customItems = new List<CustomItemDef> { def } };

        Assert.AreSame(def, CustomItems.Find(doc, def.id));

        // Null is the answer the renderer and the Select rail are written around: the placement
        // keeps its own stored size and simply loses the controls that need a definition.
        doc.customItems.Remove(def);
        Assert.IsNull(CustomItems.Find(doc, def.id));
        Assert.IsNull(CustomItems.Find(doc, null));
        Assert.IsNull(CustomItems.Find(null, def.id));

        // And the name still comes back out of the key, which is the whole point.
        Assert.AreEqual("Reading chair", CustomItems.NameFromId(def.id));
    }
}
