using System.Globalization;
using SqlDataCollector.Models;

namespace SqlDataCollector.Components.Shared;

/// <summary>Pure geometry for the inline charts (timeline bars, sparklines); no rendering, unit tested.</summary>
public static class ChartMath
{
    /// <summary>
    /// Position of a run in the timeline window as percentages of its width. A running run ends at <paramref name="nowUtc"/>;
    /// very short runs get <paramref name="minWidthPct"/> so they stay visible. Null when the run is outside the window.
    /// </summary>
    public static (double Left, double Width)? Bar(DateTime startUtc, DateTime? endUtc, DateTime fromUtc, DateTime toUtc, DateTime nowUtc, double minWidthPct = 0.4)
    {
        var span = (toUtc - fromUtc).TotalSeconds;
        if (span <= 0) return null;
        var end = endUtc ?? nowUtc;
        if (end < fromUtc || startUtc > toUtc) return null;
        var left = Math.Clamp((startUtc - fromUtc).TotalSeconds / span * 100, 0, 100);
        var right = Math.Clamp((end - fromUtc).TotalSeconds / span * 100, 0, 100);
        var width = Math.Max(right - left, minWidthPct);
        if (left + width > 100) left = Math.Max(0, 100 - width);
        return (Math.Round(left, 3), Math.Round(width, 3));
    }

    /// <summary>Hour ticks of the window in the display time zone: every hour up to 12 hours, then every 2, 6 or 12 hours.</summary>
    public static IReadOnlyList<(double Pct, string Label)> Ticks(DateTime fromUtc, DateTime toUtc, TimeZoneInfo tz)
    {
        var span = toUtc - fromUtc;
        if (span <= TimeSpan.Zero) return [];
        int step = span.TotalHours <= 12 ? 1 : span.TotalHours <= 24 ? 2 : span.TotalHours <= 72 ? 6 : 12;
        var localFrom = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc), tz);
        var first = new DateTime(localFrom.Year, localFrom.Month, localFrom.Day, localFrom.Hour, 0, 0, DateTimeKind.Unspecified).AddHours(1);
        while (first.Hour % step != 0) first = first.AddHours(1);
        var ticks = new List<(double, string)>();
        for (var t = first; ; t = t.AddHours(step))
        {
            var utc = TimeZoneInfo.ConvertTimeToUtc(t, tz);
            if (utc >= toUtc) break;
            var label = t.Hour == 0 && span.TotalHours > 24 ? t.ToString("dd MMM", CultureInfo.InvariantCulture) : t.ToString("HH:mm", CultureInfo.InvariantCulture);
            ticks.Add((Math.Round((utc - fromUtc).TotalSeconds / span.TotalSeconds * 100, 3), label));
        }
        return ticks;
    }

    /// <summary>Colour class of a run: quality warnings and partial failures amber, failures red, running blue.</summary>
    public static string Tone(RunPoint r) => r.Status switch
    {
        "Running" => "run",
        "Failed" or "CleanupPending" or "Abandoned" => "err",
        "PartiallyFailed" => "warn",
        "Skipped" => "skip",
        _ => r.QualityStatus is "Failed" ? "err" : r.QualityStatus is "Warning" ? "warn" : "ok"
    };

    /// <summary>
    /// Sparkline points (x, y) in a width × height box with <paramref name="pad"/> inset; missing values are skipped,
    /// a flat series is drawn in the middle. x follows the position in the series, so gaps keep their place.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Sparkline(IReadOnlyList<double?> values, double width, double height, double pad = 2)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (present.Count == 0) return [];
        double min = present.Min(), max = present.Max();
        double innerW = width - 2 * pad, innerH = height - 2 * pad;
        var points = new List<(double, double)>();
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } v) continue;
            double x = values.Count == 1 ? width / 2 : pad + innerW * i / (values.Count - 1);
            double y = max == min ? height / 2 : pad + innerH * (1 - (v - min) / (max - min));
            points.Add((Math.Round(x, 1), Math.Round(y, 1)));
        }
        return points;
    }

    /// <summary>
    /// True when the last value differs from the median of the earlier ones by more than half (less than 50 % or more than 200 %).
    /// Needs at least five earlier values; zero medians only flag a non-zero last value.
    /// </summary>
    public static bool LastIsOutlier(IReadOnlyList<double?> values)
    {
        if (values.Count < 6 || values[^1] is not { } last) return false;
        var earlier = values.Take(values.Count - 1).Where(v => v.HasValue).Select(v => v!.Value).Order().ToList();
        if (earlier.Count < 5) return false;
        var median = earlier.Count % 2 == 1 ? earlier[earlier.Count / 2] : (earlier[earlier.Count / 2 - 1] + earlier[earlier.Count / 2]) / 2;
        if (median == 0) return last > 0 && earlier.Count(v => v == 0) == earlier.Count;
        var ratio = last / median;
        return ratio < 0.5 || ratio > 2;
    }

    public static string Points(IEnumerable<(double X, double Y)> points) =>
        string.Join(" ", points.Select(p => $"{p.X.ToString(CultureInfo.InvariantCulture)},{p.Y.ToString(CultureInfo.InvariantCulture)}"));

    public static string Pct(double v) => v.ToString("0.###", CultureInfo.InvariantCulture) + "%";
}
