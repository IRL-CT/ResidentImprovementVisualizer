using System.Collections.Generic;

// What a before/after report IS, separated from how it is rendered.
//
// The one thing this file buys: a second output format is a second renderer over this model, not a
// second pass over the ResidenceDoc. HTML ships now because it needs no dependency and prints to PDF from
// any browser; a real PDF writer is a known follow-up, and when it arrives it reads ReportDoc and
// nothing else. Capturing images, deciding which rooms changed, and turning a change list into
// English are all done exactly once, here-adjacent, rather than per format.
//
// Everything in it is already presentation-ready: metres became feet and inches on the way in, via
// Units, because that is the app's rule and a renderer that formatted numbers itself would be the
// place the rule broke.
namespace ResidenceViz.Report
{
    public class ReportDoc
    {
        public string residenceName;
        public string date;
        public string fromName;          // usually "Existing"
        public string toName;            // the proposal
        public string authoredDescription;   // the user's own words, from VariantDef.description
        public string generatedSummary;      // counted prose from the change list
        public int changeCount;

        public readonly List<ReportSection> sections = new List<ReportSection>();
    }

    public class ReportSection
    {
        public string title;

        /// <summary>
        /// JPEG bytes, or null when the shot could not be taken. Both come from ONE camera pose, which
        /// is the only reason a reader can lay them side by side and trust the difference.
        /// </summary>
        public byte[] beforeImage;
        public byte[] afterImage;

        public readonly List<string> changes = new List<string>();
        public readonly List<MetricRow> metrics = new List<MetricRow>();
    }

    /// <summary>One measured claim, before and after. Strings, already formatted. See the file header.</summary>
    public struct MetricRow
    {
        public string label;
        public string before;
        public string after;
        /// <summary>True when the proposal moves this number the right way. Renderers mark it.</summary>
        public bool improved;
    }
}
