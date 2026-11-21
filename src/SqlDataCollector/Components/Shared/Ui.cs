using System.Globalization;
using SqlDataCollector.Infrastructure;

namespace SqlDataCollector.Components.Shared;

public static class Ui
{
    /// <summary>Time zone used for display (Collector:SystemTimeZone).</summary>
    public static TimeZoneInfo Tz { get; set; } = TimeZones.Resolve("Europe/Istanbul");

    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-GB");

    public static string Local(DateTime? utc) =>
        utc is null ? "–" : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), Tz).ToString("yyyy-MM-dd HH:mm:ss", Culture);

    public static string Duration(DateTime start, DateTime? end)
    {
        if (end is null) return "…";
        var d = end.Value - start;
        return d.TotalSeconds < 60 ? $"{d.TotalSeconds:N1} s" : d.TotalMinutes < 60 ? $"{d.TotalMinutes:N1} min" : $"{d.TotalHours:N1} h";
    }

    public static string StatusClass(string? status) => status switch
    {
        "Succeeded" => "bg-success",
        "Running" => "bg-primary",
        "Failed" or "CleanupPending" => "bg-danger",
        "PartiallyFailed" => "bg-warning text-dark",
        "Abandoned" => "bg-warning text-dark",
        "Skipped" => "bg-secondary",
        _ => "bg-light text-dark"
    };

    /// <summary>Tone of the soft status pill (.sdc-status ok|run|err|warn|neutral).</summary>
    public static string StatusTone(string? status) => status switch
    {
        "Succeeded" or Services.CheckStatus.Ok or "Sent" => "ok",
        "Running" => "run",
        "Failed" or "CleanupPending" or Services.CheckStatus.Missing or Services.CheckStatus.Error => "err",
        "PartiallyFailed" or "Abandoned" or Services.CheckStatus.Excess or Services.CheckStatus.Warning => "warn",
        _ => "neutral"
    };

    public static string CheckStatusClass(string? status) => "sdc-status " + StatusTone(status);

    public static string ShortError(string? err)
    {
        if (string.IsNullOrEmpty(err)) return "";
        var line = err.Split('\n')[0];
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    public static readonly (string Label, string Cron)[] CronPresets =
    [
        ("Every night at 02:00", "0 2 * * *"),
        ("Every night at 00:30", "30 0 * * *"),
        ("Every hour", "0 * * * *"),
        ("Every 15 minutes", "*/15 * * * *"),
        ("Weekdays at 07:00", "0 7 * * 1-5"),
        ("Sundays at 03:00", "0 3 * * 0"),
        ("1st of month at 04:00", "0 4 1 * *")
    ];

    public static readonly string[] TimeZoneChoices = ["Europe/Istanbul", "UTC", "Europe/Podgorica", "Europe/Berlin", "Europe/London"];
}
