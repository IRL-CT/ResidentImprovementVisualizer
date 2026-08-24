using System;
using System.Globalization;
using System.Text.RegularExpressions;

// The single place a time of day becomes text and text becomes a time of day.
//
// Storage is ALWAYS minutes from midnight, 0..1439: an int, so a schedule round-trips through JSON
// with no precision or timezone questions to answer. There is no date here and there is deliberately
// no DateTime: an occupant's day repeats, it does not sit on a calendar.
//
// This is a separate file from Units for the reason Units states about itself: one conversion lives
// in one place, and an inline "/ 60" anywhere else is a bug. Display follows Units.Display, because a
// tool that reads out 32" doorways should read out 7:30 AM, and one that reads out 813 mm should read
// out 07:30.
//
// Parsing is forgiving for the same reason Units.Parse is: these fields are typed by hand at a table,
// mid-conversation, and a refusal stops the meeting. Everything below is accepted:
//
//     7:30 AM    7:30am    7:30 a    7:30    07:30    0730    730
//     7 PM       7p        19:00     19      noon     midnight
public static class Clock
{
    public const int MinutesPerDay = 1440;
    public const int MinutesPerHour = 60;

    // ---------------------------------------------------------------------------------------
    // Formatting
    // ---------------------------------------------------------------------------------------

    /// <summary>"7:30 AM", or "07:30" when the app is displaying metric.</summary>
    public static string Format(int minutes) => Format(minutes, Units.Display);

    public static string Format(int minutes, Units.UnitSystem system)
    {
        int m = Wrap(minutes);
        int hour = m / MinutesPerHour, min = m % MinutesPerHour;

        if (system == Units.UnitSystem.Metric)
            return hour.ToString("00", CultureInfo.InvariantCulture) + ":" +
                   min.ToString("00", CultureInfo.InvariantCulture);

        return Hour12(hour) + ":" + min.ToString("00", CultureInfo.InvariantCulture) +
               (hour < 12 ? " AM" : " PM");
    }

    /// <summary>Tight form for timeline tick labels: "7a", "7:30a", "12p", "18" in metric.</summary>
    public static string FormatShort(int minutes)
    {
        int m = Wrap(minutes);
        int hour = m / MinutesPerHour, min = m % MinutesPerHour;

        if (Units.Display == Units.UnitSystem.Metric)
            return min == 0
                ? hour.ToString(CultureInfo.InvariantCulture)
                : hour.ToString("00", CultureInfo.InvariantCulture) + ":" + min.ToString("00", CultureInfo.InvariantCulture);

        string suffix = hour < 12 ? "a" : "p";
        return min == 0
            ? Hour12(hour) + suffix
            : Hour12(hour) + ":" + min.ToString("00", CultureInfo.InvariantCulture) + suffix;
    }

    /// <summary>
    /// "7:00-8:00 AM". The meridiem is dropped from the start when both ends share it, which is how
    /// a range is actually spoken.
    /// </summary>
    public static string FormatRange(int start, int end)
    {
        string a = Format(start), b = Format(end);
        if (Units.Display == Units.UnitSystem.Metric) return a + " - " + b;

        // "7:00 AM" / "8:00 AM" -> "7:00-8:00 AM"
        string sa = Meridiem(a), sb = Meridiem(b);
        if (sa != null && sa == sb) a = a.Substring(0, a.Length - 3);
        return a + " - " + b;
    }

    /// <summary>How long an activity running start→end lasts. Equal ends mean it covers the whole day.</summary>
    public static int DurationBetween(int start, int end)
    {
        int s = Wrap(start), e = Wrap(end);
        if (s == e) return MinutesPerDay;
        return e > s ? e - s : MinutesPerDay - s + e;
    }

    /// <summary>
    /// True when <paramref name="minutes"/> falls in [start, end), wrapping past midnight when end is
    /// before start. Equal ends cover the whole day, which is how an all-day activity is expressed.
    /// </summary>
    public static bool Spans(int start, int end, int minutes)
    {
        int s = Wrap(start), e = Wrap(end), m = Wrap(minutes);
        if (s == e) return true;
        return e > s ? m >= s && m < e : m >= s || m < e;
    }

    /// <summary>Folds any integer into 0..1439, including negatives.</summary>
    public static int Wrap(int minutes)
    {
        int m = minutes % MinutesPerDay;
        return m < 0 ? m + MinutesPerDay : m;
    }

    /// <summary>Folds a fractional minute count into [0, 1440), for the playback clock.</summary>
    public static float Wrap(float minutes)
    {
        float m = minutes % MinutesPerDay;
        return m < 0f ? m + MinutesPerDay : m;
    }

    public static int Of(int hour, int minute) => Wrap(hour * MinutesPerHour + minute);

    // ---------------------------------------------------------------------------------------
    // Parsing
    // ---------------------------------------------------------------------------------------

    // Hour, optional :minutes (or a bare 3-4 digit military form handled separately), optional meridiem.
    private static readonly Regex TimeRx = new Regex(
        @"^\s*([0-9]{1,2})\s*[:.]\s*([0-9]{2})\s*(a|am|p|pm)?\s*\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DigitsRx = new Regex(
        @"^\s*([0-9]{1,4})\s*(a|am|p|pm)?\s*\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParse(string text, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim().ToLowerInvariant();

        if (s == "noon" || s == "midday") { minutes = 12 * MinutesPerHour; return true; }
        if (s == "midnight") { minutes = 0; return true; }

        Match m = TimeRx.Match(s);
        if (m.Success)
        {
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) return false;
            if (!int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mi)) return false;
            return Combine(h, mi, m.Groups[3].Value, out minutes);
        }

        Match d = DigitsRx.Match(s);
        if (!d.Success) return false;

        string digits = d.Groups[1].Value;
        string mer = d.Groups[2].Value;

        // 3 or 4 digits with no separator is the military form everyone types: 0730, 730, 1900.
        if (digits.Length >= 3)
        {
            int cut = digits.Length - 2;
            if (!int.TryParse(digits.Substring(0, cut), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hh)) return false;
            if (!int.TryParse(digits.Substring(cut), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mm)) return false;
            return Combine(hh, mm, mer, out minutes);
        }

        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hourOnly)) return false;
        return Combine(hourOnly, 0, mer, out minutes);
    }

    public static int Parse(string text, int fallbackMinutes)
        => TryParse(text, out int m) ? m : fallbackMinutes;

    // ---------------------------------------------------------------------------------------

    private static bool Combine(int hour, int minute, string meridiem, out int minutes)
    {
        minutes = 0;
        if (minute < 0 || minute > 59) return false;

        bool hasMeridiem = !string.IsNullOrEmpty(meridiem);
        if (hasMeridiem)
        {
            // 12 AM is midnight and 12 PM is noon: the one case a naive h % 12 gets backwards.
            if (hour < 1 || hour > 12) return false;
            bool pm = meridiem[0] == 'p';
            hour = hour % 12 + (pm ? 12 : 0);
        }
        else if (hour == 24 && minute == 0)
        {
            // "24:00" is how a day-ending block is often written; it means midnight.
            hour = 0;
        }
        else if (hour < 0 || hour > 23) return false;

        minutes = hour * MinutesPerHour + minute;
        return true;
    }

    private static string Hour12(int hour24)
    {
        int h = hour24 % 12;
        return (h == 0 ? 12 : h).ToString(CultureInfo.InvariantCulture);
    }

    private static string Meridiem(string formatted)
        => formatted.EndsWith(" AM", StringComparison.Ordinal) ? "AM"
         : formatted.EndsWith(" PM", StringComparison.Ordinal) ? "PM"
         : null;
}
