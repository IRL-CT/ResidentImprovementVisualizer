using System.Collections.Generic;
using System.Text;
using UnityEngine;

// Turns two variants into a ReportDoc: which rooms to photograph, what changed in each, what that
// does to the numbers, and a paragraph of English describing the whole thing.
//
// The metrics are the part that did not exist anywhere before. Every number here was already
// computable and already computed: one element at a time, in the inspector rail, for whatever
// happened to be selected. A report is the first thing that asks all of them at once and puts the
// before beside the after, which is the form the accessibility argument actually takes: not "this
// door is 36 inches" but "this door was 32 and is now 36".
//
// TURNING CIRCLES ARE NOT REPORTED HERE. They were, per room and as a whole-residence count, and they came
// out with the rest of the turning system. LargestInscribedCircle is computed on the BARE room, so
// every figure it produced described a room emptied of its furniture, which is a claim a reader would
// reasonably take at face value and be wrong about. The Measure tool is now the one place that answer
// is given, in front of someone who asked for it.
namespace ResidenceViz.Report
{
    public static class ReportBuilder
    {
        /// <summary>Rooms are photographed in this order; the whole-plan and overview shots lead.</summary>
        public const string PlanSection = "The plan";
        public const string OverviewSection = "The residence";

        /// <summary>
        /// Which rooms a proposal touches, in the order they should appear. Shared with the capture
        /// pass, which needs the list BEFORE any image exists so it can frame a shot per room.
        /// </summary>
        /// <summary>
        /// A room the proposal touches, and WHICH STOREY it is on. The story is not decoration: the
        /// capture pass has to render that level before it can photograph the room, and two floors of
        /// one dwelling occupy the same XZ, so a room identified without one is ambiguous.
        /// </summary>
        public struct ChangedRoom
        {
            public RoomDef room;
            public LevelDef level;
            public int levelIndex;
        }

        public static List<ChangedRoom> ChangedRooms(VariantDef from, VariantDef to,
                                                     List<VariantDiff.Change> changes)
        {
            var outList = new List<ChangedRoom>();
            var levels = to?.levels;
            if (levels == null || levels.Count == 0) return outList;

            var seen = new HashSet<string>();
            foreach (var change in changes)
            {
                if (!change.hasPos) continue;
                // A sensor is not a reason to photograph a room. A smart home package touches every
                // room in the plan, and a pair of shots of a bedroom that differ by a 70 mm grey box
                // on the ceiling is sixteen images nobody can read a difference in, while making the
                // report slower to produce and far larger to email. The Technology section carries
                // them, in the form that answers something: a list, a total, and a coverage figure.
                if (change.kind == VariantDiff.ElementKind.Sensor) continue;

                int li = LevelIndexOf(to, change);
                var level = li >= 0 && li < levels.Count ? levels[li] : null;
                var room = RoomAt(level, change.worldPos);
                if (room == null || !seen.Add(room.id)) continue;
                outList.Add(new ChangedRoom { room = room, level = level, levelIndex = li });
            }

            // A room whose own polygon changed may hold no other change: a room that was enlarged, or
            // one that is new. RoomAt would miss it, because the change's anchor is the centroid and
            // the room is what moved.
            foreach (var change in changes)
            {
                if (change.kind != VariantDiff.ElementKind.Room) continue;
                int li = LevelIndexOf(to, change);
                var level = li >= 0 && li < levels.Count ? levels[li] : null;
                var room = Find(level?.rooms, r => r.id, change.id);
                if (room == null || !seen.Add(room.id)) continue;
                outList.Add(new ChangedRoom { room = room, level = level, levelIndex = li });
            }
            return outList;
        }

        /// <summary>Which story a change was reported from. 0 for anything that names none.</summary>
        public static int LevelIndexOf(VariantDef v, VariantDiff.Change change)
        {
            var levels = v?.levels;
            if (levels == null || levels.Count == 0) return 0;

            if (!string.IsNullOrEmpty(change.levelId))
                for (int i = 0; i < levels.Count; i++)
                    if (levels[i] != null && levels[i].id == change.levelId) return i;

            return change.levelIndex >= 0 && change.levelIndex < levels.Count ? change.levelIndex : 0;
        }

        public static ReportDoc Build(ResidenceDoc doc, VariantDef from, VariantDef to,
                                      List<VariantDiff.Change> changes)
        {
            var report = new ReportDoc
            {
                residenceName = doc?.name ?? "Residence",
                date = System.DateTime.Now.ToString("d MMMM yyyy"),
                fromName = from?.name ?? "Existing",
                toName = to?.name ?? "Proposal",
                authoredDescription = to?.description,
                generatedSummary = Summarize(changes),
                changeCount = changes.Count,
            };

            // Section 1 and 2 hold the orientation shots and nothing else; their images are filled in
            // by the capture pass. The whole change list rides on the plan, so a reader who never
            // scrolls past page one still gets everything.
            var plan = new ReportSection { title = PlanSection };
            // Devices are listed in the Technology section, grouped and counted, so they are left off
            // here. A smart home package is forty-odd changes and would bury the three that are about
            // the building, which is the half of the list a reader looking at a plan is reading.
            foreach (var c in changes)
                if (c.kind != VariantDiff.ElementKind.Sensor) plan.changes.Add(c.ToString());
            plan.metrics.AddRange(WholeResidenceMetrics(from, to));
            report.sections.Add(plan);

            report.sections.Add(new ReportSection { title = OverviewSection });

            bool manyStories = to?.levels != null && to.levels.Count > 1;
            foreach (var cr in ChangedRooms(from, to, changes))
            {
                // The story qualifies the heading only when there is more than one, so a single-
                // story report reads exactly as it always did.
                string title = manyStories
                    ? StoryName(cr) + " · " + RoomName(cr.room)
                    : RoomName(cr.room);
                var section = new ReportSection { title = title };

                var toLevel = cr.level;
                var fromLevel = MatchingLevel(from, cr);

                foreach (var change in changes)
                {
                    if (!change.hasPos) continue;
                    if (LevelIndexOf(to, change) != cr.levelIndex) continue;
                    if (RoomAt(toLevel, change.worldPos) != cr.room && change.id != cr.room.id) continue;
                    section.changes.Add(change.ToString());
                }

                section.metrics.AddRange(RoomMetrics(fromLevel, toLevel, cr.room));
                report.sections.Add(section);
            }

            var technology = TechnologySection(from, to, changes);
            if (technology != null) report.sections.Add(technology);

            return report;
        }

        // ---------------------------------------------------------------------------------------
        // Technology
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// The smart living section's title, so the capture pass knows to leave it unphotographed.
        /// ReportCapture compares against THIS const rather than its own copy of the string, so the
        /// two cannot drift and rename the section out of the skip list by accident.
        /// </summary>
        public const string TechnologySectionTitle = "Smart living";

        /// <summary>
        /// What the proposal installs, what it costs, what it would catch, and what it cannot see.
        /// Null when the proposal changes no devices at all, which is most proposals.
        /// </summary>
        /// <remarks>
        /// THE SECTION WITH NO PHOTOGRAPHS, deliberately: a door sensor is a 70 mm box on a frame and a
        /// picture of one says nothing a reader can act on. What this section carries instead is the
        /// four things a care manager takes to a funding meeting: the device list, the total, the
        /// coverage before and after, and the scenarios the package would have caught. None of them
        /// existed anywhere in this app before, and every one is derived from the plan rather than
        /// quoted from the report's marketing column.
        ///
        /// The scenarios come from SensorSim's demonstration day, and they are listed as KINDS rather
        /// than counted: "would have caught a stove left on, a night-time door opening, an unreturned
        /// bed exit" is a claim about the package, while "seven alerts a day" would be a claim about
        /// the household and a false one. SensorCost.AssumedIncidentsPerWeek carries the same argument
        /// for the money.
        /// </remarks>
        private static ReportSection TechnologySection(VariantDef from, VariantDef to,
                                                       List<VariantDiff.Change> changes)
        {
            // Every figure in this section is a statement about the BUILDING: what it costs, how much
            // of it is watched, how many ways out are covered, so all of them are taken over every
            // story. Read off one floor they would each be a number that flatters the plan, in the
            // section a funder reads most closely.
            if (to?.levels == null || to.levels.Count == 0) return null;

            bool touched = false;
            foreach (var c in changes)
                if (c.kind == VariantDiff.ElementKind.Sensor) { touched = true; break; }

            var after = SensorCost.Of(to);
            if (!touched && !after.Any) return null;

            var section = new ReportSection { title = TechnologySectionTitle };

            // Devices first, then everyday aids, each under its own heading. One undifferentiated
            // roster would put a sock aid between a hub and a stove sensor, and it would leave the
            // reader working out for themselves why the coverage figures below do not move when four
            // more "devices" appear.
            AppendRoster(section, to, everyday: false, "Devices");
            AppendRoster(section, to, everyday: true, "Everyday aids");

            var before = SensorCost.Of(from);

            // Counted separately for the same reason they are listed separately, and this is the half
            // that would actually mislead: aids raise no alerts and cover no floor, so folding them
            // into one count would show "devices installed" climbing while every coverage figure
            // beside it stood still, which reads as a package that got worse.
            int aidsBefore = EverydayCount(from), aidsAfter = EverydayCount(to);

            section.metrics.Add(new MetricRow
            {
                label = "Devices installed",
                before = (before.deviceCount - aidsBefore).ToString(),
                after = (after.deviceCount - aidsAfter).ToString(),
                improved = after.deviceCount - aidsAfter > before.deviceCount - aidsBefore,
            });

            if (aidsBefore > 0 || aidsAfter > 0)
                section.metrics.Add(new MetricRow
                {
                    label = "Everyday aids",
                    before = aidsBefore.ToString(),
                    after = aidsAfter.ToString(),
                    improved = aidsAfter > aidsBefore,
                });

            section.metrics.Add(new MetricRow
            {
                label = "Floor watched by a movement sensor",
                before = Percent(SensorCoverage.WholeResidenceCoverage(from)),
                after = Percent(SensorCoverage.WholeResidenceCoverage(to)),
                improved = SensorCoverage.WholeResidenceCoverage(to)
                         > SensorCoverage.WholeResidenceCoverage(from),
            });

            int exits = SensorCoverage.ExitCount(to);
            int watchedBefore = from == null ? 0
                : SensorCoverage.ExitCount(from) - SensorCoverage.UnmonitoredExitCount(from);
            int watchedAfter = exits - SensorCoverage.UnmonitoredExitCount(to);

            section.metrics.Add(new MetricRow
            {
                label = "Ways out that are watched",
                before = watchedBefore + " of " + exits,
                after = watchedAfter + " of " + exits,
                improved = watchedAfter > watchedBefore,
            });

            section.metrics.Add(new MetricRow
            {
                label = "Cost to install",
                before = before.Any ? before.UpfrontRange : "None",
                after = after.UpfrontRange,
                // A cost going UP is not an improvement, and marking it as one would be the report
                // arguing for itself. Nothing here is flagged; the reader decides.
                improved = false,
            });

            section.metrics.Add(new MetricRow
            {
                label = "Monthly",
                before = before.Any ? before.MonthlyRange : "None",
                after = after.MonthlyRange,
                improved = false,
            });

            SensorCost.MonthlySaving(out float low, out float high);
            section.metrics.Add(new MetricRow
            {
                label = $"Labor offset at {SensorCost.AssumedIncidentsPerWeek:0} incidents a week",
                before = "None",
                after = SensorCost.Money(low) + " - " + SensorCost.Money(high) + " a month",
                improved = after.Any,
            });

            AppendScenarios(to, section);
            AppendGaps(to, section);

            return section;
        }

        // Both of these run per story and merge, because SensorSim and SensorCoverage.Gaps are each
        /// <summary>
        /// One half of the roster (the sensing devices or the everyday aids) under its own heading,
        /// skipped entirely when the proposal has none of that half.
        /// </summary>
        /// <remarks>
        /// Plain rows: name, count, price. The per-row provenance notes came out on request: the
        /// `speculative` flag and `provenance` sentence stay on the data for anything that wants
        /// them, but no user-facing surface prints them any more.
        /// </remarks>
        private static void AppendRoster(ReportSection section, VariantDef to, bool everyday, string heading)
        {
            bool wroteHeading = false;

            foreach (var row in SensorCost.ByDevice(to))
            {
                var device = SensorDevices.Get(row.Key);
                bool isEveryday = device.category == SensorDevices.SensorCategory.Everyday;
                if (isEveryday != everyday) continue;

                if (!wroteHeading) { section.changes.Add(heading); wroteHeading = true; }

                section.changes.Add($"{device.displayName} × {row.Value}"
                                    + $", {SensorCost.Money(device.purchaseLow)} to "
                                    + $"{SensorCost.Money(device.purchaseHigh)} each");
            }
        }

        /// <summary>How many installed items are everyday aids rather than sensing devices.</summary>
        private static int EverydayCount(VariantDef variant)
        {
            int n = 0;
            foreach (var level in variant?.levels ?? new List<LevelDef>())
                foreach (var s in level?.sensors ?? new List<SensorDef>())
                    if (s != null && s.included
                        && SensorDevices.Get(s.deviceType).category == SensorDevices.SensorCategory.Everyday)
                        n++;
            return n;
        }

        // scoped to one level's devices. The dedupe spans the whole building on purpose: this section
        // reports the KINDS of thing the package would catch and the kinds it would miss, and listing
        // "no movement sensor in a bathroom" once per floor is length, not information.
        private static void AppendScenarios(VariantDef to, ReportSection section)
        {
            var seen = new HashSet<string>();
            foreach (var level in to?.levels ?? new List<LevelDef>())
            {
                var day = SensorSim.Simulate(to, level, SensorSim.Mode.Eventful);
                if (day.alerts == null) continue;

                foreach (var alert in day.alerts)
                {
                    if (!seen.Add(alert.kind)) continue;
                    section.changes.Add("Would raise: " + SensorAlertKind.Title(alert.kind).ToLowerInvariant()
                                        + ", " + SensorAlertKind.SuggestedResponse(alert.kind).ToLowerInvariant());
                }
            }
        }

        // What it still does not cover, in the reader's own copy. A proposal that lists only its
        // strengths is an advertisement, and the one thing a care team must not be surprised by later
        // is the back door nobody watched.
        private static void AppendGaps(VariantDef variant, ReportSection section)
        {
            var seen = new HashSet<string>();
            foreach (var level in variant?.levels ?? new List<LevelDef>())
                foreach (var gap in SensorCoverage.Gaps(level, variant))
                    if (seen.Add(gap.text))
                        section.changes.Add("Not covered: " + gap.text);
        }

        private static string Percent(float fraction) => (fraction * 100f).ToString("0") + "%";

        // ---------------------------------------------------------------------------------------
        // The writeup
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// The change list as counted English. Sits BESIDE the author's own description rather than
        /// instead of it: this says what was done, and only a person can say why.
        /// </summary>
        public static string Summarize(List<VariantDiff.Change> changes)
        {
            if (changes == null || changes.Count == 0) return "Nothing has been changed yet.";

            var counts = new Dictionary<string, int>();
            var order = new List<string>();
            void Bump(string phrase)
            {
                if (!counts.ContainsKey(phrase)) { counts[phrase] = 0; order.Add(phrase); }
                counts[phrase]++;
            }

            foreach (var c in changes) Bump(Phrase(c));

            var sb = new StringBuilder();
            foreach (var phrase in order)
            {
                int n = counts[phrase];
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Count(n)).Append(' ').Append(n == 1 ? phrase : Plural(phrase)).Append('.');
            }
            return Capitalise(sb.ToString());
        }

        private static string Phrase(VariantDiff.Change c) => (c.kind, c.type) switch
        {
            (VariantDiff.ElementKind.Opening, VariantDiff.ChangeType.Added) => "doorway or window added",
            (VariantDiff.ElementKind.Opening, VariantDiff.ChangeType.Removed) => "doorway or window removed",
            (VariantDiff.ElementKind.Opening, _) => "doorway or window altered",
            (VariantDiff.ElementKind.Wall, VariantDiff.ChangeType.Added) => "wall added",
            (VariantDiff.ElementKind.Wall, VariantDiff.ChangeType.Removed) => "wall removed",
            (VariantDiff.ElementKind.Wall, _) => "wall altered",
            (VariantDiff.ElementKind.Room, VariantDiff.ChangeType.Added) => "room added",
            (VariantDiff.ElementKind.Room, VariantDiff.ChangeType.Removed) => "room removed",
            (VariantDiff.ElementKind.Room, _) => "room altered",
            (VariantDiff.ElementKind.Furniture, VariantDiff.ChangeType.Added) => "item of furniture added",
            (VariantDiff.ElementKind.Furniture, VariantDiff.ChangeType.Removed) => "item of furniture removed",
            (VariantDiff.ElementKind.Furniture, _) => "item of furniture moved or resized",
            (VariantDiff.ElementKind.WallMount, VariantDiff.ChangeType.Added) => "wall-mounted fitting added",
            (VariantDiff.ElementKind.WallMount, VariantDiff.ChangeType.Removed) => "wall-mounted fitting removed",
            (VariantDiff.ElementKind.WallMount, _) => "wall-mounted fitting altered",
            (VariantDiff.ElementKind.Occupant, VariantDiff.ChangeType.Added) => "resident added",
            (VariantDiff.ElementKind.Occupant, VariantDiff.ChangeType.Removed) => "resident removed",
            (VariantDiff.ElementKind.Occupant, _) => "resident's day changed",

            (VariantDiff.ElementKind.Sensor, VariantDiff.ChangeType.Added) => "smart home device installed",
            (VariantDiff.ElementKind.Sensor, VariantDiff.ChangeType.Removed) => "smart home device removed",
            (VariantDiff.ElementKind.Sensor, _) => "smart home device altered",
            _ => "change made outdoors",
        };

        // "item of furniture added" -> "items of furniture added". Pluralising the HEAD noun, not the
        // last word, which is the whole reason this is a method and not string + "s".
        //
        // The head is the first word for most of these, and is NOT for the three below: "wall-mounted
        // fitting" and "smart home device" are adjective-first, and the heuristic turns them into
        // "wall-mounteds fitting" and "smarts residence device". A table for the exceptions is honest about
        // there being exceptions; a cleverer rule would only move the surprise somewhere else.
        private static readonly Dictionary<string, string> IrregularHeads = new Dictionary<string, string>
        {
            { "wall-mounted fitting", "wall-mounted fittings" },
            { "smart home device", "smart home devices" },
            { "resident's day", "residents' days" },
        };

        private static string Plural(string phrase)
        {
            foreach (var pair in IrregularHeads)
                if (phrase.StartsWith(pair.Key))
                    return pair.Value + phrase.Substring(pair.Key.Length);

            int space = phrase.IndexOf(' ');
            if (space < 0) return phrase + "s";
            string head = phrase.Substring(0, space);
            string tail = phrase.Substring(space);
            if (head.EndsWith("y")) head = head.Substring(0, head.Length - 1) + "ies";
            else if (head.EndsWith("s") || head.EndsWith("x") || head.EndsWith("ch")) head += "es";
            else head += "s";
            return head + tail;
        }

        // Words up to ten, numerals after: the convention prose uses, and a report is prose.
        private static string Count(int n) => n switch
        {
            1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five",
            6 => "six", 7 => "seven", 8 => "eight", 9 => "nine", 10 => "ten",
            _ => n.ToString(),
        };

        private static string Capitalise(string s)
            => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // ---------------------------------------------------------------------------------------
        // The numbers
        // ---------------------------------------------------------------------------------------

        private static List<MetricRow> WholeResidenceMetrics(VariantDef from, VariantDef to)
        {
            var rows = new List<MetricRow>();
            if (to?.levels == null || to.levels.Count == 0) return rows;

            // Step-free doorways, and doorways wide enough for a wheelchair. Both are counts of the
            // whole residence, and both are the sentence a funder asks for, so both are summed over every
            // story. A stairs-only upper floor is exactly the residence where the ground-floor count on
            // its own would read best and mean least.
            CountOpenings(from, out int aStepFree, out int aWide, out int aTotal);
            CountOpenings(to, out int bStepFree, out int bWide, out int bTotal);

            rows.Add(new MetricRow
            {
                label = "Step-free doorways",
                before = aStepFree + " of " + aTotal,
                after = bStepFree + " of " + bTotal,
                improved = bStepFree > aStepFree,
            });
            rows.Add(new MetricRow
            {
                label = "Doorways with 32\" clear or more",
                before = aWide + " of " + aTotal,
                after = bWide + " of " + bTotal,
                improved = bWide > aWide,
            });

            return rows;
        }

        private static List<MetricRow> RoomMetrics(LevelDef from, LevelDef to, RoomDef room)
        {
            var rows = new List<MetricRow>();
            var before = Find(from?.rooms, r => r.id, room.id);

            float areaAfter = ResidenceMetrics.RoomArea(room);
            float areaBefore = before != null ? ResidenceMetrics.RoomArea(before) : 0f;
            rows.Add(new MetricRow
            {
                label = "Floor area",
                before = before != null ? Units.FormatArea(areaBefore) : "None",
                after = Units.FormatArea(areaAfter),
                improved = areaAfter > areaBefore + 0.01f,
            });

            // Every doorway on this room's boundary, narrowest first: the one that decides whether
            // the room can be entered at all.
            var openingAfter = NarrowestInto(to, room);
            var openingBefore = before != null ? NarrowestInto(from, before) : null;
            if (openingAfter != null || openingBefore != null)
            {
                float wAfter = openingAfter != null ? ResidenceMetrics.ClearWidth(openingAfter) : 0f;
                float wBefore = openingBefore != null ? ResidenceMetrics.ClearWidth(openingBefore) : 0f;
                rows.Add(new MetricRow
                {
                    label = "Narrowest way in (clear)",
                    before = openingBefore != null ? Units.Format(wBefore) : "None",
                    after = openingAfter != null ? Units.Format(wAfter) : "None",
                    improved = wAfter > wBefore + 0.005f,
                });
                rows.Add(new MetricRow
                {
                    label = "That doorway's threshold",
                    before = openingBefore != null ? Threshold(openingBefore) : "None",
                    after = openingAfter != null ? Threshold(openingAfter) : "None",
                    improved = openingAfter != null && !ResidenceMetrics.HasThreshold(openingAfter)
                               && openingBefore != null && ResidenceMetrics.HasThreshold(openingBefore),
                });
            }
            return rows;
        }

        private static string Threshold(OpeningDef o)
            => ResidenceMetrics.HasThreshold(o) ? Units.Format(o.thresholdHeight) + " step" : "Step-free";

        // An opening "into" a room is one whose position on its host wall lies on that room's
        // boundary. Openings store an offset along a wall, not a room, so this is the only way to ask.
        private static OpeningDef NarrowestInto(LevelDef level, RoomDef room)
        {
            if (level?.openings == null) return null;

            var poly = PolygonTriangulator.ToVector2(room.polygon);
            if (poly == null || poly.Count < 3) return null;

            OpeningDef best = null;
            float bestWidth = float.MaxValue;

            foreach (var o in level.openings)
            {
                if (o == null || o.kind == OpeningKind.Window) continue;   // a window is not a way in
                var wall = Find(level.walls, w => w.id, o.wallId);
                if (wall == null) continue;

                Vector2 p = ResidenceMetrics.PointOnWall(wall, o.offset);
                if (DistanceToBoundary(p, poly) > 0.35f) continue;   // ~ half a wall + slop

                float w = ResidenceMetrics.ClearWidth(o);
                if (w >= bestWidth) continue;
                bestWidth = w;
                best = o;
            }
            return best;
        }

        // Distance to the polygon's outline, inside or out. ResidenceMetrics.SignedDistanceInside answers
        // only for interior points, and a doorway sits ON the boundary, where it may fall either side
        // of the centerline by half a wall.
        private static float DistanceToBoundary(Vector2 p, IReadOnlyList<Vector2> poly)
        {
            float best = float.MaxValue;
            for (int i = 0; i < poly.Count; i++)
                best = Mathf.Min(best, ResidenceMetrics.PointSegmentDistance(p, poly[i], poly[(i + 1) % poly.Count]));
            return best;
        }

        /// <summary>Every doorway in the building, however many stories it has.</summary>
        private static void CountOpenings(VariantDef v, out int stepFree, out int wide, out int total)
        {
            stepFree = 0; wide = 0; total = 0;
            foreach (var level in v?.levels ?? new List<LevelDef>())
            {
                CountOpenings(level, out int s1, out int w1, out int t1);
                stepFree += s1; wide += w1; total += t1;
            }
        }

        private static void CountOpenings(LevelDef level, out int stepFree, out int wide, out int total)
        {
            stepFree = 0; wide = 0; total = 0;
            if (level?.openings == null) return;

            foreach (var o in level.openings)
            {
                if (o == null || o.kind == OpeningKind.Window) continue;
                total++;
                if (!ResidenceMetrics.HasThreshold(o)) stepFree++;
                if (ResidenceMetrics.ClearWidth(o) >= 0.8128f) wide++;   // 32 inches
            }
        }


        // ---------------------------------------------------------------------------------------


        /// <summary>The baseline story matching a changed room's: by id, then by position.</summary>
        public static LevelDef MatchingLevel(VariantDef v, ChangedRoom cr)
        {
            var levels = v?.levels;
            if (levels == null || levels.Count == 0) return null;

            if (!string.IsNullOrEmpty(cr.level?.id))
                foreach (var l in levels)
                    if (l != null && l.id == cr.level.id) return l;

            return cr.levelIndex >= 0 && cr.levelIndex < levels.Count ? levels[cr.levelIndex] : levels[0];
        }

        public static string StoryName(ChangedRoom cr)
            => string.IsNullOrEmpty(cr.level?.name) ? "Floor " + (cr.levelIndex + 1) : cr.level.name;

        public static string RoomName(RoomDef room)
            => string.IsNullOrEmpty(room?.name) ? UITheme.PrettyId(room?.roomType) : room.name;

        public static RoomDef RoomAt(LevelDef level, Vector2 p)
        {
            if (level?.rooms == null) return null;
            foreach (var room in level.rooms)
            {
                if (room == null) continue;
                var poly = PolygonTriangulator.ToVector2(room.polygon);
                if (poly == null || poly.Count < 3) continue;
                if (ResidenceMetrics.PointInPolygon(p, poly)) return room;
            }
            return null;
        }

        private static T Find<T>(List<T> list, System.Func<T, string> key, string id) where T : class
        {
            if (list == null || string.IsNullOrEmpty(id)) return null;
            foreach (var item in list)
                if (item != null && key(item) == id) return item;
            return null;
        }
    }
}
