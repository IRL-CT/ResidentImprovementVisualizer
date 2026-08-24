// Decides whether an installed sample residence may be re-installed from the current SampleResidences plans.
//
// The problem this solves: seeding is one-shot. `ResidenceSettings.samplesSeeded` deliberately stops the
// seeder ever running twice, because archiving a sample has to keep it archived, so a fix to a plan
// never reaches a machine that has already launched the app. `SampleResidenceInstaller.BackfillOccupants`
// was a hand-written patch for one instance of exactly that drift, and it only ever fixes the one
// thing it was written for. `SampleResidences.Generation` plus this verdict is the general form.
//
// It lives in CXRAuthoring, apart from the code that actually rewrites files, for one reason: the
// installer is in Assembly-CSharp and EditMode tests cannot reference that assembly. The rule about
// when a residence may be overwritten is the part worth pinning, so it is the part that lives here. The
// installer stays a thin shell around ResidenceStore.
public static class SampleRefresh
{
    public enum Verdict
    {
        /// <summary>Built from the current plans. Nothing to do.</summary>
        UpToDate,

        /// <summary>Behind, and untouched. Safe to re-install in place.</summary>
        Refresh,

        /// <summary>Behind, but someone has started working on it. Leave it completely alone.</summary>
        UserEdited,

        /// <summary>Not a sample, or not enough of one to reason about.</summary>
        NotASample,
    }

    /// <summary>
    /// Whether <paramref name="stored"/> may be silently replaced with a freshly built copy.
    ///
    /// The bar for <see cref="Verdict.Refresh"/> is deliberately high, because being wrong here
    /// destroys work: a refresh is a whole-document overwrite, not a merge. A sample qualifies only
    /// while it is still exactly what was seeded: every variant one WE authored, all still locked,
    /// nothing traced over it. The moment someone branches a proposal, unlocks a variant or imports a
    /// floor plan, the residence stops being a sample and starts being theirs, and the automatic path
    /// gives up in favour of the explicit "Reset to the latest sample" action.
    ///
    /// Note that this does NOT compare geometry. `BackfillOccupants` had to, because it wrote a
    /// roster addressed by room id into a plan it was not sure still had those rooms. A refresh
    /// replaces the plan wholesale, so the only question worth asking is whether anything would be
    /// lost, and on an untouched sample the answer is no.
    /// </summary>
    /// <remarks>
    /// This used to ask a simpler question ("is there exactly one variant") and that was the same
    /// question while every sample shipped only its baseline. It stopped being the same question the
    /// day the two care samples began shipping a smart home proposal beside it: a residence is born with
    /// two variants, trips the count on the first launch, and is frozen at whatever generation it was
    /// installed at forever. Which is precisely the staleness trap SampleResidences.Generation exists to
    /// close, reintroduced by the mechanism meant to close it.
    ///
    /// <see cref="VariantDef.fromSample"/> asks it properly. It defaults to false, so a residence already
    /// on disk keeps taking the old path exactly (one variant, locked, no stamp) with no migration,
    /// and anything a user branches is false by construction, which is the signal, not a heuristic
    /// about it.
    /// </remarks>
    private static bool HasUnderlay(ResidenceDoc doc)
    {
        // The legacy single field is read too: Evaluate can be handed a document straight off disk,
        // before ResidenceStore.Migrate has folded it into the list.
        if (doc.underlay != null && !string.IsNullOrEmpty(doc.underlay.imageFileName)) return true;
        foreach (var u in doc.underlays ?? new System.Collections.Generic.List<UnderlayDef>())
            if (u != null && !string.IsNullOrEmpty(u.imageFileName)) return true;
        return false;
    }

    public static Verdict Evaluate(ResidenceDoc stored, int currentGeneration)
    {
        if (stored == null) return Verdict.NotASample;
        if (!IsSample(stored)) return Verdict.NotASample;
        if (stored.variants == null || stored.variants.Count == 0) return Verdict.NotASample;

        if (stored.sampleGeneration >= currentGeneration) return Verdict.UpToDate;

        // A traced underlay is real work even when the plan on top of it is untouched, and it is the
        // one thing a rebuilt sample cannot carry forward. Build() never returns any. ANY story
        // having one counts: importing a plan for an upper floor is exactly as much work as importing
        // one for the ground floor, and a refresh would discard both along with the story itself.
        if (HasUnderlay(stored)) return Verdict.UserEdited;

        // A sample ships one story. More than one means somebody added a floor, which is the same
        // kind of signal as a traced sketch: a refresh replaces the whole document, so it would take
        // that floor with it.
        foreach (var v in stored.variants)
            if (v?.levels != null && v.levels.Count > 1) return Verdict.UserEdited;

        // The pre-stamp case: one variant, no fromSample flag anywhere, because the field did not
        // exist when it was written. Judged exactly as it always was.
        bool anyStamped = false;
        foreach (var v in stored.variants) if (v != null && v.fromSample) { anyStamped = true; break; }

        if (!anyStamped) return stored.variants.Count == 1 && stored.variants[0]?.locked == true
            ? Verdict.Refresh
            : Verdict.UserEdited;

        foreach (var v in stored.variants)
        {
            if (v == null) return Verdict.UserEdited;
            // A variant the user added, or one of ours they have unlocked to edit. Either way, work.
            if (!v.fromSample || !v.locked) return Verdict.UserEdited;
        }

        return Verdict.Refresh;
    }

    /// <summary>True when this residence came from SampleResidences, by stamp or by the `sample` tag.</summary>
    /// <remarks>
    /// The tag is the fallback for everything seeded before <c>sampleKey</c> existed. Those are the
    /// residences with the oldest geometry, so a check that only trusted the stamp would skip precisely
    /// the ones that need refreshing.
    /// </remarks>
    public static bool IsSample(ResidenceDoc doc)
    {
        if (doc == null) return false;
        if (!string.IsNullOrEmpty(doc.sampleKey)) return true;
        return doc.tags != null && doc.tags.Contains("sample");
    }
}
