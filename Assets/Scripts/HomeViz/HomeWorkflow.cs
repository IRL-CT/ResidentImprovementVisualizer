using System.Collections.Generic;

// The order the work actually happens in: import a plan, trace it, furnish it, then compare options
// in front of the people who live there. HomeViz is a small tool used in a meeting, so the rail shows
// one stage's controls at a time rather than every control at once.
//
// This is HomeViz's answer to Brownfield's UIShell/UIMode, minus the machinery. Brownfield needs a
// MonoBehaviour and a Changed event because five separate panels each read the mode from their own
// OnGUI. HomeViz has one controller that owns the whole UI, so the stage is a field on it and this
// file is just the table.
public enum HomeStage
{
    Sketch,
    Draw,
    Furnish,
    Review,
    Outdoors,
}

public static class HomeWorkflow
{
    // Every stage begins with Select. It is the pointer, not a phase of work — you need to click a
    // door and read its clear width just as much while drawing walls as while reviewing.
    private const string SELECT = "select";

    private static readonly Dictionary<HomeStage, string[]> Tools = new Dictionary<HomeStage, string[]>
    {
        // Tracing a calibrated sketch is how every home gets into the model; nothing else can happen
        // until an underlay is imported and scaled.
        [HomeStage.Sketch]   = new[] { SELECT, "underlay" },
        [HomeStage.Draw]     = new[] { SELECT, "wall", "opening", "room" },
        [HomeStage.Furnish]  = new[] { SELECT, "furniture" },
        [HomeStage.Review]   = new[] { SELECT, "measure" },
        // Only reachable once the home's exterior layer is switched on. See VisibleStages.
        [HomeStage.Outdoors] = new[] { SELECT, "outdoor" },
    };

    public static string Label(HomeStage stage) => stage.ToString();

    public static string[] ToolIdsFor(HomeStage stage)
        => Tools.TryGetValue(stage, out var ids) ? ids : new[] { SELECT };

    /// <summary>
    /// The tool a stage lands on when you switch to it. Every stage opens on the tool it exists for —
    /// except Review, where the work is reading what is already there, so the pointer is the tool.
    /// </summary>
    public static string PrimaryToolId(HomeStage stage)
    {
        if (stage == HomeStage.Review) return SELECT;
        var ids = ToolIdsFor(stage);
        return ids.Length > 1 ? ids[1] : ids[0];
    }

    /// <summary>
    /// The stages offered for this document. Outdoors is absent unless the home has opted into the
    /// exterior layer, which is what keeps a tool for the inside of an apartment free of site work.
    /// </summary>
    public static List<HomeStage> VisibleStages(HomeDoc doc)
    {
        var stages = new List<HomeStage>
        {
            HomeStage.Sketch, HomeStage.Draw, HomeStage.Furnish, HomeStage.Review,
        };
        if (doc != null && doc.exteriorEnabled) stages.Add(HomeStage.Outdoors);
        return stages;
    }

    public static string[] LabelsFor(List<HomeStage> stages)
    {
        var labels = new string[stages.Count];
        for (int i = 0; i < stages.Count; i++) labels[i] = Label(stages[i]);
        return labels;
    }

    /// <summary>The stage a tool belongs to, so selecting a tool by any other route moves the rail with it.</summary>
    public static HomeStage StageOf(string toolId, HomeStage fallback)
    {
        if (string.IsNullOrEmpty(toolId) || toolId == SELECT) return fallback;
        foreach (var pair in Tools)
            foreach (var id in pair.Value)
                if (id == toolId) return pair.Key;
        return fallback;
    }
}
