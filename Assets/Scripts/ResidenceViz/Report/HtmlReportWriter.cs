using System.Text;
using UnityEngine;

// A ReportDoc as one self-contained HTML file.
//
// There is no PDF library in this project and adding one is a real dependency for a feature that
// runs once per meeting. A single .html with the images base64'd into it needs nothing, opens on any
// machine, emails as one attachment, and prints to a real PDF through the browser's own Save-as-PDF
//, so the print stylesheet at the bottom is not a nicety, it is the PDF half of the deliverable.
//
// The palette is lifted from UITheme, whose own values came from Assets/Redesign.html, so a report
// looks like the app that made it rather than like a database dump.
namespace ResidenceViz.Report
{
    public static class HtmlReportWriter
    {
        public const string DIR = "reports";

        /// <summary>Writes the report and returns its full path, or null with a reason.</summary>
        public static string Write(ReportDoc report, ResidenceDoc doc, VariantDef to, out string error)
        {
            error = null;
            try
            {
                string dir = System.IO.Path.Combine(ResidenceStore.RootDir, DIR);
                System.IO.Directory.CreateDirectory(dir);

                string name = Sanitize(doc?.name ?? "Residence") + " - " + Sanitize(to?.name ?? "Proposal")
                              + " - " + System.DateTime.Now.ToString("yyyy-MM-dd") + ".html";
                string path = System.IO.Path.Combine(dir, name);

                // Temp file then replace, the same discipline ResidenceStore.WriteAtomic uses: a report
                // half-written because the app was closed mid-save is worse than no report, because
                // it opens and looks almost right.
                string tmp = path + ".tmp";
                System.IO.File.WriteAllText(tmp, Render(report), new UTF8Encoding(false));
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                System.IO.File.Move(tmp, path);
                return path;
            }
            catch (System.Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        // ---------------------------------------------------------------------------------------

        public static string Render(ReportDoc r)
        {
            var sb = new StringBuilder(1 << 20);
            sb.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">\n");
            sb.Append("<title>").Append(Esc(r.residenceName)).Append(": ").Append(Esc(r.toName))
              .Append("</title>\n<style>\n").Append(Css()).Append("</style></head><body>\n");

            sb.Append("<header class=\"cover\">\n");
            sb.Append("<h1>").Append(Esc(r.residenceName)).Append("</h1>\n");
            sb.Append("<p class=\"lede\">").Append(Esc(r.toName)).Append(", compared against ")
              .Append(Esc(r.fromName)).Append("</p>\n");
            sb.Append("<p class=\"meta\">").Append(Esc(r.date)).Append(" · ")
              .Append(r.changeCount).Append(r.changeCount == 1 ? " change" : " changes")
              .Append("</p>\n");

            if (!string.IsNullOrWhiteSpace(r.authoredDescription))
                sb.Append("<blockquote>").Append(Esc(r.authoredDescription)).Append("</blockquote>\n");

            sb.Append("<p class=\"summary\">").Append(Esc(r.generatedSummary)).Append("</p>\n");
            sb.Append("</header>\n");

            bool first = true;
            foreach (var section in r.sections)
            {
                sb.Append("<section class=\"sheet").Append(first ? "" : " break").Append("\">\n");
                first = false;
                sb.Append("<h2>").Append(Esc(section.title)).Append("</h2>\n");

                if (section.beforeImage != null || section.afterImage != null)
                {
                    sb.Append("<div class=\"pair\">\n");
                    // Before/After, the same words the Compare rail uses. The reader meets this
                    // document after the meeting the rail was driven in.
                    Figure(sb, "Before: " + r.fromName, section.beforeImage);
                    Figure(sb, "After: " + r.toName, section.afterImage);
                    sb.Append("</div>\n");
                }

                if (section.metrics.Count > 0)
                {
                    sb.Append("<table><thead><tr><th></th><th>Before</th><th>After</th></tr></thead><tbody>\n");
                    foreach (var m in section.metrics)
                    {
                        sb.Append("<tr><th scope=\"row\">").Append(Esc(m.label)).Append("</th>");
                        sb.Append("<td>").Append(Esc(m.before)).Append("</td>");
                        sb.Append("<td class=\"").Append(m.improved ? "better" : "same").Append("\">")
                          .Append(Esc(m.after));
                        if (m.improved) sb.Append(" <span class=\"tick\" aria-label=\"improved\">✓</span>");
                        sb.Append("</td></tr>\n");
                    }
                    sb.Append("</tbody></table>\n");
                }

                if (section.changes.Count > 0)
                {
                    sb.Append("<ul class=\"changes\">\n");
                    foreach (var c in section.changes)
                        sb.Append("<li>").Append(Esc(c)).Append("</li>\n");
                    sb.Append("</ul>\n");
                }

                sb.Append("</section>\n");
            }

            sb.Append("<footer><p>Residence Improvement Visualizer. Measurements come from the model."
                      + "</p></footer>\n");
            sb.Append("</body></html>\n");
            return sb.ToString();
        }

        private static void Figure(StringBuilder sb, string caption, byte[] jpeg)
        {
            sb.Append("<figure>");
            if (jpeg != null && jpeg.Length > 0)
            {
                sb.Append("<img alt=\"").Append(Esc(caption)).Append("\" src=\"data:image/jpeg;base64,")
                  .Append(System.Convert.ToBase64String(jpeg)).Append("\">");
            }
            else sb.Append("<div class=\"missing\"></div>");
            sb.Append("<figcaption>").Append(Esc(caption)).Append("</figcaption></figure>\n");
        }

        // ---------------------------------------------------------------------------------------

        private static string Css() => @"
:root {
  --ink:#1F2228; --ink2:#6B7177; --ink3:#9AA0A6;
  --paper:#FCFCFB; --line:rgba(20,22,28,.12); --tint:#EAF1FC; --accent:#1C4BA0; --ok:#2E9E6B;
}
* { box-sizing:border-box; }
body {
  margin:0; padding:32px; background:#F3F2EE; color:var(--ink);
  font:15px/1.55 'Public Sans',-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;
}
.cover, .sheet {
  max-width:1000px; margin:0 auto 22px; background:var(--paper); padding:34px 38px;
  border:1px solid var(--line); border-radius:10px;
}
h1 { margin:0 0 6px; font-size:31px; letter-spacing:-.02em; }
h2 {
  margin:0 0 18px; font-size:12px; font-weight:700; letter-spacing:.10em;
  text-transform:uppercase; color:var(--accent);
}
.lede { margin:0 0 4px; font-size:18px; color:var(--ink); }
.meta { margin:0; color:var(--ink3); font-size:13px; }
blockquote {
  margin:22px 0 0; padding:14px 18px; background:var(--tint); border-radius:8px;
  border-left:3px solid var(--accent); font-size:16px;
}
.summary { margin:16px 0 0; color:var(--ink2); }

/* The pair is the report. Both halves share one camera pose, so they must share one width. */
.pair { display:grid; grid-template-columns:1fr 1fr; gap:16px; margin-bottom:20px; }
figure { margin:0; }
figure img, .missing {
  display:block; width:100%; aspect-ratio:8/5; object-fit:cover;
  border:1px solid var(--line); border-radius:8px; background:#E6E5DF;
}
figcaption { margin-top:7px; font-size:12px; color:var(--ink3); }

table { width:100%; border-collapse:collapse; margin-bottom:18px; font-size:14px; }
th, td { text-align:left; padding:8px 10px; border-bottom:1px solid var(--line); }
thead th { font-size:11px; text-transform:uppercase; letter-spacing:.06em; color:var(--ink3); }
tbody th { font-weight:500; color:var(--ink2); }
td { font-variant-numeric:tabular-nums; }
td.better { color:var(--ok); font-weight:600; }
.tick { font-weight:400; }

ul.changes { margin:0; padding-left:20px; }
ul.changes li { margin-bottom:5px; }
footer { max-width:1000px; margin:0 auto; color:var(--ink3); font-size:12px; }

@media print {
  @page { size:A4; margin:14mm; }
  body { background:#fff; padding:0; font-size:11pt; }
  .cover, .sheet { max-width:none; margin:0; border:0; border-radius:0; padding:0 0 12mm; }
  /* Never split a before/after pair, a table or a section across a page: the whole point of a
     pair is that the two halves are seen together. */
  .sheet, .pair, figure, table { break-inside:avoid; page-break-inside:avoid; }
  .break { break-before:page; page-break-before:always; }
  footer { display:none; }
}
";

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(System.Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString().Trim();
        }
    }
}
