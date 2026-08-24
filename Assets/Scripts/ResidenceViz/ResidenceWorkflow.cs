using System.Collections.Generic;

// The order the work actually happens in: import a plan, trace it, furnish it, then compare options
// in front of the people who live there. ResidenceViz is a small tool used in a meeting, so the rail shows
// one stage's controls at a time rather than every control at once.
//
// This is ResidenceViz's answer to the Site tool's UIShell/UIMode, minus the machinery. Site needs a
// MonoBehaviour and a Changed event because five separate panels each read the mode from their own
// OnGUI. ResidenceViz has one controller that owns the whole UI, so the stage is a field on it and this
// file is just the table.
public enum ResidenceStage
{
    // First, and its own tab rather than a chip repeated in every other stage's tool row. Inspecting
    // what is already there is not a phase of the work, but it IS a mode of the app, and as a chip it
    // was drawn six times, took a keyboard digit away from every stage, and still put the inspector
    // off to one side of whatever else was on screen.
    Select,
    // Import, not "Sketch": a plan arrives as a photo, a scan or a PDF, and calling the tab after one
    // of those three named the least common case. What the stage does is get a drawing into the model
    // and set its scale.
    Import,
    // Structure, not "Draw": the tab is named for what it produces (the shell of the dwelling) the
    // way Furnish and Smart living are, rather than for the gesture. Drawing is also no longer the only way
    // in, since "Read the plan" derives the same walls, doors and rooms without a click.
    Structure,
    Furnish,
    // Everything that makes living here easier: the sensing layer, the everyday aids that are not
    // sensing at all, and the accessibility fixtures that go with them. Named "Smart living" rather
    // than "Sense" because most of what it now holds does not watch anything: a rocker knife, a sock
    // aid and a grab bar change whether someone can live here far more than a motion cone does.
    //
    // Still after Furnish, because a device hosts on an ELEMENT: a pad on a bed, a stove sensor on
    // the range, so the thing it hangs off has to exist first. Still before People, because a worn
    // aid and a pendant both need a roster to belong to, and what a device notices is derived from
    // the household's day.
    SmartLiving,
    // Who lives here and what their day looks like. After Furnish because a person standing in an
    // unfurnished room says nothing; before Review because occupancy is one of the things a proposal
    // changes, and the change list is read in Review.
    People,
    Review,
    Outdoors,
}

public static class ResidenceWorkflow
{
    // Select used to lead every stage's array. It is the pointer, not a phase of work, so it is now a
    // stage of its own and appears exactly once, in the command bar.
    private const string SELECT = "select";

    private static readonly Dictionary<ResidenceStage, string[]> Tools = new Dictionary<ResidenceStage, string[]>
    {
        [ResidenceStage.Select]    = new[] { SELECT },
        // Tracing a calibrated sketch is how every residence gets into the model; nothing else can happen
        // until an underlay is imported and scaled.
        [ResidenceStage.Import]    = new[] { "underlay" },
        [ResidenceStage.Structure] = new[] { "wall", "opening", "room" },
        [ResidenceStage.Furnish]   = new[] { "furniture" },
        // Equipment leads, so the stage opens on placing things. Monitor is what you switch to once
        // there are some. It is a view of the day they produce, not a way to author anything.
        //
        // The ids are keys, not captions: "sensor" still names the tool that installs a device, an
        // everyday aid or a grab bar, because renaming it would break StageOf and every RequestTool
        // call for no gain a user can see.
        [ResidenceStage.SmartLiving] = new[] { "sensor", "monitor" },
        [ResidenceStage.People]    = new[] { "people" },
        // Compare leads, so Review opens on it: holding a proposal against the base environment IS
        // the work here, and measuring is what you reach for to check one of its claims.
        [ResidenceStage.Review]    = new[] { "compare", "measure" },
        // Only reachable once the residence's exterior layer is switched on. See VisibleStages.
        [ResidenceStage.Outdoors]  = new[] { "outdoor" },
    };

    /// <summary>
    /// What each tab does, shown on hover. This is where a stage's explanation lives now that no rail
    /// prints one: the active tool's own <see cref="IResidenceTool.Hint"/> overrides the entry for the stage
    /// you are actually in (see ResidenceEditController.StageTips).
    /// </summary>
    private static readonly Dictionary<ResidenceStage, string> Tips = new Dictionary<ResidenceStage, string>
    {
        [ResidenceStage.Select]    = "Pick a wall, room or item to inspect it, and to move or resize "
                              + "furniture. A wall lists the openings in it.",
        [ResidenceStage.Import]    = "Import a floor plan and set its scale. Everything traced afterwards "
                              + "comes out at true size.",
        [ResidenceStage.Structure] = "The shell of the dwelling: walls and the openings in them, meaning doors, "
                              + "windows, cased openings. Walls that close off an area make a room by "
                              + "themselves; the Rooms tool says what each one is.",
        [ResidenceStage.Furnish]   = "Place furniture, fixtures and wall-mounted items like grab bars.",
        [ResidenceStage.SmartLiving] = "Everything that makes living here easier: sensing devices, everyday "
                              + "aids and the fixtures that go with them: what it costs, what it "
                              + "covers, and what a caregiver would be told.",
        [ResidenceStage.People]    = "Who lives here and what their day is. Their schedules and the clock "
                              + "place them in the plan.",
        [ResidenceStage.Review]    = "Compare a proposal to the base environment, and measure.",
        [ResidenceStage.Outdoors]  = "Entry ramps, walkways and railings around the residence.",
    };

    public static string Tip(ResidenceStage stage) => Tips.TryGetValue(stage, out var t) ? t : null;

    /// <summary>
    /// The tab caption. Still the enum member itself. There is deliberately NO second table of
    /// display names to fall out of step with the enum, but split at each capital, so a two-word
    /// stage can be said at all: <c>SmartLiving</c> reads "Smart living".
    /// </summary>
    /// <remarks>
    /// Only the first word keeps its capital, because these are sentence-case labels rather than
    /// title-case ones ("Smart living", not "Smart Living"). Every other member is a single
    /// word, so this is the identity for all of them and the invariant is unchanged.
    /// <para>
    /// "Smart living" is 12 characters against Structure's 9, so it is now the first label
    /// UITheme.FitAll shortens on a narrow window. That is already handled: ResidenceEditController
    /// .StageTips leads a shortened tab's tooltip with the full stage name.
    /// </para>
    /// </remarks>
    public static string Label(ResidenceStage stage)
    {
        string name = stage.ToString();

        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c)) { sb.Append(' '); sb.Append(char.ToLowerInvariant(c)); }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static string[] ToolIdsFor(ResidenceStage stage)
        => Tools.TryGetValue(stage, out var ids) ? ids : new[] { SELECT };

    /// <summary>
    /// The tool a stage lands on when you switch to it. Now simply its first, since every stage lists
    /// only the tools it exists for. Review used to be the exception, opening on the pointer; with
    /// Select promoted to its own tab that exception has somewhere better to live, and Review opens on
    /// Measure.
    /// </summary>
    public static string PrimaryToolId(ResidenceStage stage) => ToolIdsFor(stage)[0];

    /// <summary>
    /// TEMPORARY: the outdoor layer's UI is switched off. Set this back to true to restore the
    /// Outdoors tab and the rail's "Include outdoor additions" toggle; nothing else has changed.
    /// <para>
    /// It hides controls only. No stored data is touched: a residence that already has
    /// <c>ResidenceDoc.exteriorEnabled</c> keeps the flag and keeps rendering its ramps and railings,
    /// <c>VariantDiff</c> keeps reporting outdoor changes, and the report keeps carrying them, so
    /// flipping this back leaves every residence exactly where its author left it.
    /// </para>
    /// <para>
    /// It is a <c>static readonly</c> rather than a <c>const</c> on purpose: a const false would fold
    /// at compile time and fill the console with CS0162 unreachable-code warnings at every site below.
    /// </para>
    /// </summary>
    public static readonly bool OutdoorsUI = false;

    /// <summary>
    /// The stages offered for this document. Outdoors is absent unless the residence has opted into the
    /// exterior layer, which is what keeps a tool for the inside of an apartment free of site work,
    /// and, while <see cref="OutdoorsUI"/> is off, absent regardless.
    /// </summary>
    public static List<ResidenceStage> VisibleStages(ResidenceDoc doc)
    {
        var stages = new List<ResidenceStage>
        {
            ResidenceStage.Select, ResidenceStage.Import, ResidenceStage.Structure, ResidenceStage.Furnish, ResidenceStage.SmartLiving,
            ResidenceStage.People, ResidenceStage.Review,
        };
        if (OutdoorsUI && doc != null && doc.exteriorEnabled) stages.Add(ResidenceStage.Outdoors);
        return stages;
    }

    public static string[] LabelsFor(List<ResidenceStage> stages)
    {
        var labels = new string[stages.Count];
        for (int i = 0; i < stages.Count; i++) labels[i] = Label(stages[i]);
        return labels;
    }

    /// <summary>The stage a tool belongs to, so selecting a tool by any other route moves the rail with it.</summary>
    // No SELECT special case any more: "select" now resolves through the table to ResidenceStage.Select,
    // which is what keeps SetTool's re-derivation agreeing with SetStage about where the pointer lives.
    public static ResidenceStage StageOf(string toolId, ResidenceStage fallback)
    {
        if (string.IsNullOrEmpty(toolId)) return fallback;
        foreach (var pair in Tools)
            foreach (var id in pair.Value)
                if (id == toolId) return pair.Key;
        return fallback;
    }
}
