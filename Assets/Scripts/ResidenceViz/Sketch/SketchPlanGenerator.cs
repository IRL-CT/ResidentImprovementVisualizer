using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>What a generation run produced, and what it was asked for.</summary>
public sealed class SketchGenerationResult
{
    public LevelDef level;
    public List<string> problems = new List<string>();
    public string error;                 // a sentence, shown verbatim; null on success
    public string notes;                 // the model's own commentary on the sketch
    public int turns;
    public float seconds;

    /// <summary>Cache tokens read and written across the whole run. See ClaudeClient.Call.</summary>
    public int cacheRead, cacheWrite;

    /// <summary>
    /// Which document and story this was asked FOR. A call takes a minute, which is long enough for
    /// somebody to switch residence, switch floor, or close the document while it runs, so the result
    /// carries what it was for and the caller refuses to apply it anywhere else. The same discipline
    /// ModeBand follows for a held VariantDef, applied to a slower kind of staleness.
    /// </summary>
    public string forResidenceId;
    public string forLevelId;

    public bool Ok => level != null && error == null;
}

// Reads a floor-plan sketch and turns it into a story.
//
// The whole of the model-facing English lives here, beside the schema it describes, because the two
// have to agree and splitting them is how they stop agreeing. Everything geometric happens in
// CXRAuthoring: this file arranges the calls, and hands what comes back to SketchPlanCompiler.
//
// IT READS THE PLAN TWICE, AND THE SPLIT IS THE DESIGN. Reading a drawing is two jobs: where the
// rooms are, and what is in them, and the two are not equally recoverable. Every opening and every
// item is addressed BY ROOM KEY, so a room read wrongly takes the rest of the plan down with it,
// while a missed wardrobe costs a wardrobe. Asking for both at once makes the model trade one off
// against the other inside a single sampling pass, and leaves the geometry unchecked at the moment
// the fittings are being decided. So the rooms are settled first, checked on their own, and then
// handed back as FACT for the second pass to work against, which is also why the second pass never
// sees a room key it can get wrong.
//
// Both passes share one system prompt and one image, and that is deliberate rather than incidental:
// it is the stable prefix ClaudeClient's cache breakpoint sits on. Reading a plan twice, with a
// repair turn available to each, costs about what reading it once used to.
public static class SketchPlanGenerator
{
    /// <summary>
    /// Turns per pass: one read, then one informed correction.
    ///
    /// What a second turn actually fixes is referential slips: a mistyped room key, a catalog id
    /// recalled rather than read off the list, and it fixes most of them. What it does not fix is a
    /// sketch the model cannot read, and each turn is another wait and another charge against the
    /// user's key. A plan still broken after two turns is one to trace by hand, and saying so is a
    /// better answer than a third attempt.
    ///
    /// The correction is not trusted blindly, though. See ReadPass: both replies are scored and the
    /// better one wins, so a repair turn that makes a plan worse cannot make it worse.
    /// </summary>
    public const int TurnsPerPass = 2;

    /// <summary>Kept for the shape of the old single-pass contract: one read plus one repair.</summary>
    public const int MaxRepairTurns = TurnsPerPass - 1;

    /// <summary>Above this, PNG gives way to JPEG. See Encode.</summary>
    private const int PNG_CEILING_BYTES = 3_500_000;

    public static IEnumerator Run(Texture2D sketch, SketchFrame frame, float ceilingHeight,
                                  float wallThickness, string residenceId, string levelId,
                                  ClaudeClient.Call call, Action<string> progress,
                                  Action<SketchGenerationResult> done)
    {
        var result = new SketchGenerationResult { forResidenceId = residenceId, forLevelId = levelId };

        if (!frame.valid) { result.error = frame.reason; done(result); yield break; }

        string apiKey = ApiKeyStore.Get();
        if (string.IsNullOrEmpty(apiKey))
        {
            result.error = "No API key is set.";
            done(result);
            yield break;
        }

        progress?.Invoke("Preparing the sketch…");
        if (!Encode(sketch, out string mediaType, out string imageBase64, out string encodeError))
        {
            result.error = encodeError;
            done(result);
            yield break;
        }

        var c = call ?? new ClaudeClient.Call();
        string system = SystemPrompt();

        // ---------------------------------------------------------------------------------------
        // Pass one: the rooms, and nothing else.
        // ---------------------------------------------------------------------------------------

        var rooms = new Pass();
        yield return ReadPass(rooms, c, system, mediaType, imageBase64, apiKey,
                              SketchPlanSpec.RoomsSchema(), RoomsPrompt(frame, ceilingHeight),
                              reply => ScoreRooms(reply, frame),
                              progress, "Reading the rooms…", "Correcting the rooms…");

        Tally(result, rooms, c);

        if (rooms.aborted) { done(null); yield break; }
        if (rooms.spec == null)
        {
            result.error = rooms.error ?? "The reply was not a plan.";
            done(result);
            yield break;
        }

        var settled = SketchPlanCompiler.CompileRooms(rooms.spec, frame);
        if (!settled.Ok)
        {
            result.error = settled.refusal ?? "No usable rooms came back for this sketch.";
            done(result);
            yield break;
        }

        // ---------------------------------------------------------------------------------------
        // Pass two: the openings and the furniture, against rooms that are now settled.
        // ---------------------------------------------------------------------------------------

        var detail = new Pass();
        yield return ReadPass(detail, c, system, mediaType, imageBase64, apiKey,
                              SketchPlanSpec.DetailSchema(), DetailPrompt(settled.rooms),
                              reply => ScoreDetail(reply, rooms.spec, frame, ceilingHeight, wallThickness),
                              progress, "Reading the doors and fittings…", "Correcting the fittings…");

        Tally(result, detail, c);

        if (detail.aborted) { done(null); yield break; }

        // A pass-two failure is not a run failure. The rooms are read, checked and worth having,
        // the OpeningFit convention applied to half a plan, so they are installed and the reason
        // the rest is missing is shown beside them.
        var merged = rooms.spec;
        if (detail.spec != null)
        {
            merged.openings = detail.spec.openings;
            merged.furniture = detail.spec.furniture;
            if (!string.IsNullOrWhiteSpace(detail.spec.notes)) merged.notes = detail.spec.notes;
        }
        else if (detail.error != null)
        {
            result.problems.Add("The rooms were read, but the doors and fittings were not: "
                              + detail.error);
        }

        var compiled = SketchPlanCompiler.Compile(merged, frame, ceilingHeight, wallThickness);
        result.notes = merged.notes;

        if (compiled.refusal != null) { result.error = compiled.refusal; done(result); yield break; }

        // Whatever survives is shown rather than thrown away. The geometry PlanBuilder derived is
        // valid by construction even where a relationship could not be resolved, so a plan with
        // eleven of its twelve doors is worth far more than nothing.
        result.level = compiled.level;
        result.problems.AddRange(compiled.issues);
        result.problems.AddRange(compiled.warnings);
        done(result);
    }

    // ===========================================================================================
    // One pass: read, score, correct, keep the better
    // ===========================================================================================

    /// <summary>The best reply a pass produced, and what was still wrong with it.</summary>
    private sealed class Pass
    {
        public SketchPlanSpec spec;
        public List<string> issues = new List<string>();
        public string error;              // no usable reply at all
        public bool aborted;
        public int turns;
        public float seconds;
        public int cacheRead, cacheWrite;
    }

    /// <summary>What one reply parsed to, and how wrong it was.</summary>
    private struct Scored
    {
        public SketchPlanSpec spec;
        public List<string> issues;
        public bool Usable => spec != null;
    }

    /// <summary>
    /// Reads one thing, and if it came back wrong, asks again knowing why.
    ///
    /// THE SECOND REPLY DOES NOT AUTOMATICALLY WIN. A repair turn is another sample, and a model
    /// correcting three named problems can drop something that was right, so both are scored the
    /// same way and the better one is kept, ties going to the first. That makes the extra turn a
    /// strictly one-way bet: it can only improve the plan, never spoil one that was already good.
    ///
    /// The scorer is passed in because the two passes are wrong in different ways. Rooms are scored
    /// on the room checks alone, since there is nothing yet to open into them; the fittings are
    /// scored on the whole compile, PlanBuilder's own warnings included, which is the closest thing
    /// available to "what will this actually build as".
    /// </summary>
    private static IEnumerator ReadPass(Pass into, ClaudeClient.Call call, string system,
                                        string mediaType, string imageBase64, string apiKey,
                                        string schema, string firstPrompt,
                                        Func<string, Scored> score, Action<string> progress,
                                        string firstLabel, string repairLabel)
    {
        var prior = new List<ClaudeClient.Turn>();

        for (int turn = 0; turn < TurnsPerPass; turn++)
        {
            progress?.Invoke(turn == 0 ? firstLabel : repairLabel);

            // One Call object for the whole run, so Abort from the rail reaches whichever turn is in
            // flight. Only the per-turn fields are cleared; Aborted is not, because it must stick.
            call.Error = null;
            call.Text = null;
            call.StopReason = null;

            yield return ClaudeClient.Send(call, system, mediaType, imageBase64, firstPrompt,
                                           prior, schema, apiKey);

            into.turns++;
            into.seconds = call.Elapsed;
            into.cacheRead += call.CacheReadTokens;
            into.cacheWrite += call.CacheWriteTokens;

            if (call.Aborted) { into.aborted = true; yield break; }

            if (call.Error != null)
            {
                // Keep anything already in hand: a network failure on the repair turn must not throw
                // away a first reply that was merely imperfect.
                if (into.spec == null) into.error = call.Error;
                yield break;
            }

            var candidate = score(call.Text);
            if (!candidate.Usable)
            {
                if (into.spec == null) into.error = "The reply was not a plan.";
                yield break;
            }

            if (into.spec == null || candidate.issues.Count < into.issues.Count)
            {
                into.spec = candidate.spec;
                into.issues = candidate.issues;
                into.error = null;
            }

            if (into.issues.Count == 0) yield break;
            if (turn == TurnsPerPass - 1) yield break;

            prior.Add(new ClaudeClient.Turn { role = "assistant", text = call.Text });
            prior.Add(new ClaudeClient.Turn { role = "user", text = RepairPrompt(into.issues) });
        }
    }

    private static Scored ScoreRooms(string reply, SketchFrame frame)
    {
        var spec = Parse(reply);
        if (spec == null) return new Scored();

        var rooms = SketchPlanCompiler.CompileRooms(spec, frame);
        var issues = new List<string>(rooms.issues);
        if (rooms.refusal != null) issues.Add(rooms.refusal);

        return new Scored { spec = spec, issues = issues };
    }

    private static Scored ScoreDetail(string reply, SketchPlanSpec rooms, SketchFrame frame,
                                      float ceilingHeight, float wallThickness)
    {
        var spec = Parse(reply);
        if (spec == null) return new Scored();

        // Scored against the rooms already agreed, because that is what it will be compiled against.
        var merged = new SketchPlanSpec
        {
            rooms = rooms.rooms,
            openings = spec.openings,
            furniture = spec.furniture,
            notes = spec.notes,
        };

        var compiled = SketchPlanCompiler.Compile(merged, frame, ceilingHeight, wallThickness);
        var issues = new List<string>(compiled.issues);
        issues.AddRange(compiled.warnings);
        if (compiled.refusal != null) issues.Add(compiled.refusal);

        return new Scored { spec = spec, issues = issues };
    }

    private static SketchPlanSpec Parse(string reply)
    {
        try { return JsonConvert.DeserializeObject<SketchPlanSpec>(reply); }
        catch (Exception e)
        {
            Debug.LogWarning("[SketchPlanGenerator] Reply was not the expected JSON: " + e.Message);
            return null;
        }
    }

    private static void Tally(SketchGenerationResult result, Pass pass, ClaudeClient.Call call)
    {
        result.turns += pass.turns;
        result.seconds = call.Elapsed;
        result.cacheRead += pass.cacheRead;
        result.cacheWrite += pass.cacheWrite;
    }

    // ===========================================================================================
    // The prompts
    // ===========================================================================================

    /// <summary>
    /// Shared by both passes, byte for byte, and that is what makes the cache work.
    ///
    /// It carries the catalog even on the pass that cannot place furniture. Splitting it would save
    /// nothing worth having: after the first turn the whole thing is read back at a tenth of the
    /// price, whereas two different system prompts would be two separate cache entries, each written
    /// at a premium and each read half as often.
    /// </summary>
    private static string SystemPrompt()
    {
        var sb = new StringBuilder(4096);

        sb.AppendLine("You read floor plans and describe what is in them.");
        sb.AppendLine();
        sb.AppendLine("You are given one image of a floor plan: an architect's drawing, an "
                    + "estate agent's listing, or a hand sketch. You describe its rooms, its "
                    + "doors and windows, and its furniture, as JSON matching the schema you have "
                    + "been given. You are asked for these in two steps: the rooms first, then "
                    + "everything that sits in them. Answer only what the current step asks for.");
        sb.AppendLine();
        sb.AppendLine("COORDINATES. Positions are integers from 0 to 1000 across the image: x runs "
                    + "left to right and y runs DOWNWARD from the top, so a room's y is the "
                    + "distance from the top of the image to its top edge. Use the whole range. If "
                    + "the plan fills the sheet, its rooms should reach 0 and 1000.");
        sb.AppendLine();
        sb.AppendLine("ROOMS ARE RECTANGLES THAT TILE THE PLAN. Each room is an axis-aligned "
                    + "rectangle measured to the CENTER of the walls around it. Every edge lands on a "
                    + "wall's centerline. Neighboring rooms therefore meet exactly: where two rooms share a "
                    + "wall, give the shared edge the SAME integer in both of them. Do not leave a "
                    + "gap between rooms for the wall's thickness. The wall is drawn on the line "
                    + "they share. Rooms must not overlap.");
        sb.AppendLine();
        sb.AppendLine("AN L-SHAPED OR IRREGULAR ROOM is described as two or more rectangles that "
                    + "meet along an edge. Give the largest one a key and leave its \"partOf\" "
                    + "empty; on every other piece, put that key in \"partOf\". They become ONE "
                    + "room with NO WALL between them, so only do this where the drawing shows no "
                    + "wall. Two areas with a wall between them are two rooms with a door.");
        sb.AppendLine();
        sb.AppendLine("SAY EACH ROOM'S SIZE TWICE. \"widthMeters\" and \"depthMeters\" are read off "
                    + "the drawing: from a dimension line, a scale bar, or what the room plainly "
                    + "is. NEVER convert them from the 0-1000 numbers. They are checked against "
                    + "them, and the check is the only thing that can catch a plan traced into the "
                    + "wrong part of the range.");
        sb.AppendLine();
        sb.AppendLine("OPENINGS. Every room needs at least one door, pass-through or cased opening, "
                    + "or there is no way into it. A window never counts as a way in. An opening between "
                    + "two rooms names both of them in \"between\". An opening in an outside wall "
                    + "names one room and which of its walls, where north is the top of the image "
                    + "and south the bottom. Every room has to reach a door in an outside wall, "
                    + "through other rooms if need be.");
        sb.AppendLine();
        sb.AppendLine("FURNITURE. Place what you can see, using only the catalog below. Stand things "
                    + "against the wall they are drawn against. Do not invent items to fill a room "
                    + "out, and do not place anything you cannot see.");
        sb.AppendLine();
        sb.AppendLine("Lengths other than positions, meaning a room's measured size, an opening's width, "
                    + "and its sill, are in meters.");
        sb.AppendLine();
        sb.AppendLine("If part of the plan is unreadable, leave it out and say so in \"notes\". A "
                    + "smaller plan that is right beats a complete one that is guessed.");
        sb.AppendLine();
        sb.AppendLine("THE CATALOG. Width x depth x height in meters:");
        sb.Append(Catalog());

        return sb.ToString();
    }

    private static string Catalog()
    {
        var sb = new StringBuilder(2048);
        foreach (var item in SampleFurniture.All)
        {
            sb.Append("  ").Append(item.id);
            sb.Append("  ").Append(item.width.ToString("0.00")).Append(" x ")
              .Append(item.depth.ToString("0.00")).Append(" x ")
              .Append(item.height.ToString("0.00"));
            if (item.wallMounted) sb.Append("  (hangs on a wall)");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string RoomsPrompt(SketchFrame frame, float ceilingHeight)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("Here is the floor plan.");
        sb.AppendLine();
        sb.AppendLine($"It has been measured: the image is {frame.metersW:0.00} m wide and "
                    + $"{frame.metersH:0.00} m tall in real terms, and the ceiling height is "
                    + $"{ceilingHeight:0.00} m. Use that to check yourself: a bedroom is usually "
                    + "3 to 4 m across, a corridor 1 to 1.6 m, an interior door 0.81 m wide.");
        sb.AppendLine();
        sb.Append("STEP ONE: read the ROOMS. Every room in the plan, as rectangles that tile it. "
                + "Do not list doors, windows or furniture yet. You will be asked for those next, "
                + "against the rooms you give now.");
        return sb.ToString();
    }

    /// <summary>
    /// The second pass, handed the rooms as settled fact.
    ///
    /// The rectangles are quoted back in METRES rather than in the 0-1000 units they were given in,
    /// because they have been through the regularizer by this point and are no longer quite what was
    /// sent. Quoting the originals would invite the model to place a door against an edge that has
    /// since moved; quoting what was actually built keeps the two halves talking about one plan.
    /// </summary>
    private static string DetailPrompt(IReadOnlyList<SketchRect> rooms)
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine("These are the rooms, now settled. Use these keys exactly; do not add, remove "
                    + "or rename a room.");
        sb.AppendLine();

        foreach (var r in rooms)
        {
            sb.Append("  ").Append(r.key).Append("  ").Append(r.name)
              .Append("  (").Append(r.roomType).Append(")  ")
              .Append(r.Width.ToString("0.00")).Append(" x ").Append(r.Depth.ToString("0.00"))
              .Append(" m");
            if (r.IsPart) sb.Append("  a piece of ").Append(r.Room);
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.Append("STEP TWO: read the DOORS, WINDOWS and FURNITURE. Two pieces of one room have no "
                + "wall between them, so never put an opening there.");
        return sb.ToString();
    }

    private static string RepairPrompt(IReadOnlyList<string> problems)
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine("That has problems:");
        sb.AppendLine();
        for (int i = 0; i < problems.Count && i < 40; i++)
            sb.Append(i + 1).Append(". ").AppendLine(problems[i]);
        sb.AppendLine();
        sb.Append("Return the complete corrected answer in full. Keep everything that was right.");
        return sb.ToString();
    }

    // ===========================================================================================
    // The image
    // ===========================================================================================

    /// <summary>
    /// PNG unless it is too big, and then JPEG.
    ///
    /// This is deliberately the OPPOSITE conclusion to HtmlReportWriter's, and the reason is the
    /// content rather than the format. That file is choosing between sixteen photographs of shaded
    /// geometry, where PNG makes a report nobody can email. Here the image is line art: a wall is
    /// one or two dark pixels wide, and JPEG ringing around a 1 px line is precisely the detail the
    /// model has to read. PNG is both smaller and lossless on line art, so it wins on its own terms
    ///, and the fallback exists only because a photographed plan is a photograph, not line art, and
    /// those do get large.
    /// </summary>
    private static bool Encode(Texture2D sketch, out string mediaType, out string base64, out string error)
    {
        mediaType = null;
        base64 = null;
        error = null;

        if (sketch == null)
        {
            error = "The sketch image could not be read.";
            return false;
        }

        Texture2D scaled = null;
        try
        {
            Texture2D source = sketch;

            if (SketchImageResample.Target(sketch.width, sketch.height,
                                           SketchImageResample.LongEdgeCap, out int w, out int h))
            {
                var pixels = SketchImageResample.Box(sketch.GetPixels32(), sketch.width, sketch.height, w, h);
                scaled = new Texture2D(w, h, TextureFormat.RGBA32, false);
                scaled.SetPixels32(pixels);
                scaled.Apply(false, false);
                source = scaled;
            }

            byte[] bytes = source.EncodeToPNG();
            mediaType = "image/png";

            if (bytes == null || bytes.Length > PNG_CEILING_BYTES)
            {
                bytes = source.EncodeToJPG(88);
                mediaType = "image/jpeg";
            }

            if (bytes == null)
            {
                error = "The sketch image could not be encoded.";
                return false;
            }

            base64 = Convert.ToBase64String(bytes);
            return true;
        }
        catch (Exception e)
        {
            error = "The sketch image could not be prepared: " + e.Message;
            return false;
        }
        finally
        {
            if (scaled != null) UnityEngine.Object.Destroy(scaled);
        }
    }
}
