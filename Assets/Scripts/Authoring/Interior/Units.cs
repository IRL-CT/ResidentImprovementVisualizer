using System;
using System.Globalization;
using System.Text.RegularExpressions;

// The single place meters become text and text becomes meters.
//
// Storage is ALWAYS meters (1 Unity unit = 1 m, matching AuthoringConventions), and DISPLAY now
// defaults to meters too. It used to default to feet-and-inches, because the audience is US shared
// homes and assisted living, where every dimension that matters: a 32" clear doorway, a 60" turning
// circle, a 34" counter. Is spoken in inches. Those figures have not stopped mattering and the chip
// in the top bar is one click away; what changed is that numbers are now DRAGGED, and a value
// scrubbing under the cursor from 3' 11 5/8" to 4' 0 1/8" changes four glyphs at once where 1.21 m to
// 1.22 m changes one. A unit you can read while it moves beats one you can quote afterwards.
// Nothing outside this file may convert units; an inline "* 3.28" anywhere else is a bug.
//
// Parsing is deliberately forgiving, because the most important text field in the whole application
// is the calibration prompt ("how long is this line you just clicked?") and a refusal there blocks
// the user from doing anything at all. Everything below is accepted:
//
//     12' 6"     12'6"      12' 6      12'        6"        12 ft 6 in
//     12.5'      6 1/2"     1/2"       32"        0' 32"
//     3.8m       3.8 m      380cm      3810mm     3.8
//
// A bare number takes the caller-declared BareUnit, so a wall-length field can read "12" as 12 feet
// while a door-width field reads "32" as 32 inches: the two conventions users actually expect.
public static class Units
{
    // Named UnitSystem rather than System: a nested type called `System` would shadow the global
    // System namespace throughout this class, silently breaking any future `System.Xxx` reference.
    public enum UnitSystem { FeetInches, Metric }

    // Which system the UI renders in. Set once from settings; Format() honours it by default.
    public static UnitSystem Display = UnitSystem.Metric;

    // How a number with no unit marker is interpreted.
    public enum BareUnit { Feet, Inches, Meters, FollowDisplay }

    private const float IN_TO_M = HomeConventions.IN_TO_M;
    private const float FT_TO_M = HomeConventions.FT_TO_M;

    // Imperial output is rounded to the nearest 1/8", the finest fraction anyone reads off a tape.
    private const int FRACTION_DEN = 8;

    // ---------------------------------------------------------------------------------------
    // Formatting
    // ---------------------------------------------------------------------------------------

    public static string Format(float meters) => Format(meters, Display);

    public static string Format(float meters, UnitSystem system)
        => system == UnitSystem.Metric ? FormatMetric(meters) : FormatFeetInches(meters);

    // 3.81 -> 12' 6"   0.114 -> 4 1/2"   0 -> 0"
    public static string FormatFeetInches(float meters)
    {
        bool neg = meters < 0f;
        double totalIn = Math.Abs(meters) / IN_TO_M;

        // Round to the nearest 1/8" FIRST, so the feet/inches split can never disagree with the
        // fraction shown (e.g. 11.999" must render 1' 0", never 0' 12").
        long eighths = (long)Math.Round(totalIn * FRACTION_DEN, MidpointRounding.AwayFromZero);

        long feet       = eighths / (12 * FRACTION_DEN);
        long remEighths = eighths % (12 * FRACTION_DEN);
        long inches     = remEighths / FRACTION_DEN;
        long fracNum    = remEighths % FRACTION_DEN;

        string inchPart = FormatInchPart(inches, fracNum);
        string s;
        if (feet > 0 && inchPart != null) s = feet + "' " + inchPart;
        else if (feet > 0)                s = feet + "'";
        else                              s = inchPart ?? "0\"";

        return neg ? "-" + s : s;
    }

    // Returns null when the inch component is exactly zero, so callers can drop it entirely.
    private static string FormatInchPart(long inches, long fracNum)
    {
        if (inches == 0 && fracNum == 0) return null;
        if (fracNum == 0) return inches + "\"";

        long den = FRACTION_DEN;
        long g = Gcd(fracNum, den);
        fracNum /= g;
        den     /= g;

        return inches > 0
            ? inches + " " + fracNum + "/" + den + "\""
            : fracNum + "/" + den + "\"";
    }

    // Architectural metric would use millimetres throughout, but this tool mixes room-scale and
    // detail-scale dimensions in the same rail, so meters with adaptive precision reads better:
    // sub-metre values keep 3 decimals so a 0.114 m wall does not collapse to 0.11 m.
    public static string FormatMetric(float meters)
        => Math.Abs(meters) >= 1f
            ? meters.ToString("0.##", CultureInfo.InvariantCulture) + " m"
            : meters.ToString("0.###", CultureInfo.InvariantCulture) + " m";

    public static string FormatArea(float squareMeters) => FormatArea(squareMeters, Display);

    public static string FormatArea(float squareMeters, UnitSystem system)
    {
        if (system == UnitSystem.Metric)
            return squareMeters.ToString("0.#", CultureInfo.InvariantCulture) + " m²";
        double sqft = squareMeters / (FT_TO_M * FT_TO_M);
        return sqft.ToString("0", CultureInfo.InvariantCulture) + " sq ft";
    }

    // ---------------------------------------------------------------------------------------
    // Parsing
    // ---------------------------------------------------------------------------------------

    private static readonly Regex FeetRx = new Regex(
        @"([0-9]*\.?[0-9]+)\s*(?:'|ft\b\.?|feet\b|foot\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Ordered alternation: "6 1/2" must win over a bare "6" before the fraction is reached.
    private static readonly Regex InchRx = new Regex(
        "([0-9]+\\s+[0-9]+\\s*/\\s*[0-9]+|[0-9]+\\s*/\\s*[0-9]+|[0-9]*\\.?[0-9]+)\\s*(?:\"|''|in\\b\\.?|inch\\b|inches\\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareRx = new Regex(
        @"^\s*([0-9]+\s+[0-9]+\s*/\s*[0-9]+|[0-9]+\s*/\s*[0-9]+|[0-9]*\.?[0-9]+)\s*$",
        RegexOptions.Compiled);

    public static bool TryParse(string text, out float meters)
        => TryParse(text, BareUnit.FollowDisplay, out meters);

    public static bool TryParse(string text, BareUnit bare, out float meters)
    {
        meters = 0f;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim().ToLowerInvariant();

        // Explicit metric suffixes first. Longest match wins so "mm" is never read as "m".
        if (TryStripSuffix(s, "mm", out string num) && TryNumber(num, out double mm))
        { meters = (float)(mm * 0.001); return true; }
        if (TryStripSuffix(s, "cm", out num) && TryNumber(num, out double cm))
        { meters = (float)(cm * 0.01); return true; }
        if (TryStripSuffix(s, "m", out num) && TryNumber(num, out double m))
        { meters = (float)m; return true; }

        double total = 0;
        bool matched = false;
        string rest = s;

        // Inches BEFORE feet. The `''` inch marker starts with a `'`, so running the feet pattern
        // first would read 6'' as six feet. Consuming the inch part up front removes the ambiguity,
        // and no feet-only input can be mistaken for inches (a lone `'` is not an inch marker).
        Match im = InchRx.Match(rest);
        if (im.Success && TryNumber(im.Groups[1].Value, out double inch))
        {
            total += inch * IN_TO_M;
            matched = true;
            rest = rest.Remove(im.Index, im.Length);
        }

        Match fm = FeetRx.Match(rest);
        if (fm.Success && TryNumber(fm.Groups[1].Value, out double ft))
        {
            total += ft * FT_TO_M;
            matched = true;
            rest = rest.Remove(fm.Index, fm.Length);
        }

        if (matched)
        {
            // A trailing unmarked number after a feet part is the inches everyone forgets to close:
            // "12' 6" means 12 feet 6 inches. Only applies when no inch part was already found.
            if (!im.Success)
            {
                Match leftover = BareRx.Match(rest);
                if (leftover.Success && TryNumber(leftover.Groups[1].Value, out double trailing))
                    total += trailing * IN_TO_M;
            }
            meters = (float)total;
            return true;
        }

        // No unit markers at all. Fall back to the caller's declared convention.
        Match bm = BareRx.Match(s);
        if (!bm.Success || !TryNumber(bm.Groups[1].Value, out double bareVal)) return false;

        BareUnit effective = bare == BareUnit.FollowDisplay
            ? (Display == UnitSystem.Metric ? BareUnit.Meters : BareUnit.Feet)
            : bare;

        meters = effective switch
        {
            BareUnit.Inches => (float)(bareVal * IN_TO_M),
            BareUnit.Meters => (float)bareVal,
            _               => (float)(bareVal * FT_TO_M),
        };
        return true;
    }

    // Convenience for inspector fields that must always produce a value.
    public static float Parse(string text, float fallbackMeters)
        => TryParse(text, out float m) ? m : fallbackMeters;

    public static float Parse(string text, BareUnit bare, float fallbackMeters)
        => TryParse(text, bare, out float m) ? m : fallbackMeters;

    // ---------------------------------------------------------------------------------------

    // Accepts "6", "6.5", "1/2", and "6 1/2".
    private static bool TryNumber(string raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim();

        int slash = s.IndexOf('/');
        if (slash >= 0)
        {
            string left = s.Substring(0, slash).Trim();
            string den  = s.Substring(slash + 1).Trim();

            double whole = 0;
            int sp = left.LastIndexOfAny(new[] { ' ', '\t' });
            string numer = left;
            if (sp >= 0)
            {
                if (!double.TryParse(left.Substring(0, sp).Trim(), NumberStyles.Float,
                                     CultureInfo.InvariantCulture, out whole)) return false;
                numer = left.Substring(sp + 1).Trim();
            }

            if (!double.TryParse(numer, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return false;
            if (!double.TryParse(den,   NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return false;
            if (Math.Abs(d) < 1e-9) return false;

            value = whole + n / d;
            return true;
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryStripSuffix(string s, string suffix, out string number)
    {
        number = null;
        if (!s.EndsWith(suffix, StringComparison.Ordinal)) return false;
        number = s.Substring(0, s.Length - suffix.Length).Trim();
        // Guard against "cm"/"mm" being read as a bare "m" suffix, and against an empty numeral.
        return number.Length > 0 && !number.EndsWith("m", StringComparison.Ordinal);
    }

    private static long Gcd(long a, long b)
    {
        a = Math.Abs(a); b = Math.Abs(b);
        while (b != 0) { long t = b; b = a % b; a = t; }
        return a == 0 ? 1 : a;
    }
}
