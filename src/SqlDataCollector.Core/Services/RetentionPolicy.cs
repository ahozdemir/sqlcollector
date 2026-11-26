using System.Globalization;
using Hangfire;
using Microsoft.Extensions.Options;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;
using SqlDataCollector.Options;

namespace SqlDataCollector.Services;

/// <summary>
/// Organization-wide retention and cleanup policy (Settings page, Admin only). Enforced when an extract job is saved
/// and again when the retention job runs, so jobs saved before a policy change follow it too.
/// </summary>
public sealed record RetentionPolicy(
    int? DefaultDays,
    int? MaxDays,
    bool AllowKeepForever,
    bool EditorsMayChange,
    string Cron,
    int RunLogDays,
    int AuditDays,
    int PermissionCheckDays)
{
    /// <summary>
    /// The retention the retention job applies to a job: a job without retention gets the default (or the maximum)
    /// when "keep forever" is not allowed; a longer retention is capped at the maximum. null = keep forever.
    /// </summary>
    public int? EffectiveDays(int? jobDays, Models.LoadMode loadMode = Models.LoadMode.Append)
    {
        // Replace keeps only the latest successful load, Merge the current state: retention by age does not apply (and must never empty a paused job's table)
        if (loadMode.KeepsCurrentStateOnly()) return null;
        if (jobDays is null or <= 0) return AllowKeepForever ? null : DefaultDays ?? MaxDays;
        return MaxDays is { } max ? Math.Min(jobDays.Value, max) : jobDays;
    }

    /// <summary>Server-side check when an extract job is saved; null = allowed.</summary>
    public string? ValidateJob(int? requested, int? current, bool isNew, bool isAdmin, Models.LoadMode loadMode = Models.LoadMode.Append)
    {
        if (loadMode.KeepsCurrentStateOnly()) return null;   // retention does not apply; the editor clears it
        if (requested is <= 0) return "Retention must be at least 1 day (empty = keep forever).";
        if (!isAdmin && !EditorsMayChange)
        {
            var allowed = isNew ? DefaultDays : current;
            if (requested != allowed)
                return $"Only Admins can change the retention (policy). Allowed value: {(allowed is null ? "keep forever" : $"{allowed} days")}.";
            return null; // unchanged value of an existing job: the retention job still applies the current policy
        }
        if (requested is null && !AllowKeepForever)
            return $"Keep forever is not allowed by the retention policy; enter a number of days{(MaxDays is { } m ? $" (at most {m})" : "")}.";
        if (requested is { } r && MaxDays is { } max && r > max)
            return $"Retention cannot exceed {max} days (retention policy).";
        return null;
    }

    /// <summary>Validation of the policy itself (Settings page).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (DefaultDays is <= 0) errors.Add("Default retention must be at least 1 day (empty = none).");
        if (MaxDays is <= 0) errors.Add("Maximum retention must be at least 1 day (empty = no maximum).");
        if (DefaultDays is { } d && MaxDays is { } m && d > m) errors.Add("Default retention cannot be longer than the maximum.");
        if (!AllowKeepForever && DefaultDays is null && MaxDays is null)
            errors.Add("When keep forever is not allowed, set a default or a maximum retention.");
        if (RunLogDays <= 0) errors.Add("Run history retention must be at least 1 day.");
        if (AuditDays <= 0) errors.Add("Audit log retention must be at least 1 day.");
        if (PermissionCheckDays <= 0) errors.Add("Permission check retention must be at least 1 day.");
        if (!JobRegistrar.Preview(Cron, "UTC").Ok) errors.Add($"Invalid retention schedule (cron): '{Cron}'.");
        return errors;
    }
}

/// <summary>Alert repeat interval (same job + event + destination) and the grace before a scheduled run counts as missed.</summary>
public sealed record AlertSettings(int ThrottleMinutes, int MissedGraceMinutes);

/// <summary>Reads/writes etl.SystemSetting; settings without a row fall back to the process configuration.</summary>
public sealed class SystemSettingsService(MetadataRepository repo, IOptions<CollectorOptions> options)
{
    public const string DefaultDaysKey = "Retention.DefaultDays";
    public const string MaxDaysKey = "Retention.MaxDays";
    public const string AllowKeepForeverKey = "Retention.AllowKeepForever";
    public const string EditorsMayChangeKey = "Retention.EditorsMayChange";
    public const string CronKey = "Retention.Cron";
    public const string RunLogDaysKey = "Retention.RunLogDays";
    public const string AuditDaysKey = "Retention.AuditDays";
    public const string PermissionCheckDaysKey = "Retention.PermissionCheckDays";

    public static readonly string[] RetentionKeys =
        [DefaultDaysKey, MaxDaysKey, AllowKeepForeverKey, EditorsMayChangeKey, CronKey, RunLogDaysKey, AuditDaysKey, PermissionCheckDaysKey];

    public const string RetentionRecurringJobId = "system-retention";

    /// <summary>Policy from configuration only (used for settings that have no row yet).</summary>
    public RetentionPolicy ConfigurationDefaults()
    {
        var o = options.Value;
        return new RetentionPolicy(null, null, AllowKeepForever: true, EditorsMayChange: true, o.RetentionCron,
            o.RunLogRetentionDays, o.RunLogRetentionDays * 4, PermissionCheckDays: 30);
    }

    public async Task<RetentionPolicy> GetRetentionPolicyAsync() => (await GetAsync()).Policy;

    /// <summary>The policy plus the names of the settings that are stored (the others come from configuration).</summary>
    public async Task<(RetentionPolicy Policy, IReadOnlySet<string> Stored)> GetAsync()
    {
        var rows = await repo.GetSystemSettingsAsync();
        var d = ConfigurationDefaults();
        var p = new RetentionPolicy(
            Int(rows, DefaultDaysKey, d.DefaultDays), Int(rows, MaxDaysKey, d.MaxDays),
            Bool(rows, AllowKeepForeverKey, d.AllowKeepForever), Bool(rows, EditorsMayChangeKey, d.EditorsMayChange),
            rows.TryGetValue(CronKey, out var cron) && !string.IsNullOrWhiteSpace(cron) ? cron.Trim() : d.Cron,
            Int(rows, RunLogDaysKey, d.RunLogDays) ?? d.RunLogDays, Int(rows, AuditDaysKey, d.AuditDays) ?? d.AuditDays,
            Int(rows, PermissionCheckDaysKey, d.PermissionCheckDays) ?? d.PermissionCheckDays);
        return (p, rows.Keys.Where(k => RetentionKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Validates and stores the whole policy (audited) and re-registers the retention schedule.</summary>
    public async Task SaveRetentionPolicyAsync(RetentionPolicy p, string user, IRecurringJobManager recurring)
    {
        var errors = p.Validate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        await repo.SaveSystemSettingsAsync(new Dictionary<string, string?>
        {
            [DefaultDaysKey] = Str(p.DefaultDays), [MaxDaysKey] = Str(p.MaxDays),
            [AllowKeepForeverKey] = p.AllowKeepForever ? "true" : "false", [EditorsMayChangeKey] = p.EditorsMayChange ? "true" : "false",
            [CronKey] = p.Cron.Trim(), [RunLogDaysKey] = Str(p.RunLogDays), [AuditDaysKey] = Str(p.AuditDays),
            [PermissionCheckDaysKey] = Str(p.PermissionCheckDays)
        }, user);
        RegisterRetentionJob(recurring, p.Cron);
    }

    /// <summary>Registers the nightly retention job with the policy's schedule (idempotent).</summary>
    public void RegisterRetentionJob(IRecurringJobManager recurring, string cron) =>
        recurring.AddOrUpdate<Scheduling.RetentionRunner>(RetentionRecurringJobId, CollectorQueues.System, r => r.RunAsync(CancellationToken.None),
            cron, new RecurringJobOptions { TimeZone = TimeZones.Resolve(options.Value.SystemTimeZone) });

    // ------------------------------------------------------------------ alert settings

    public const string AlertThrottleKey = "Alerts.ThrottleMinutes";
    public const string AlertGraceKey = "Alerts.MissedGraceMinutes";
    public const string AlertBaselineKey = "Alerts.BaselineUtc";

    /// <summary>Days the export Workers keep their local copy of uploaded files (1–3) and whether the value is stored here.</summary>
    public async Task<(int Days, bool Stored)> GetExportArchiveDaysAsync()
    {
        var rows = await repo.GetSystemSettingsAsync();
        return (Export.ExportArchive.Days(rows, options.Value), rows.TryGetValue(Export.ExportArchive.DaysKey, out var v) && !string.IsNullOrWhiteSpace(v));
    }

    public async Task SaveExportArchiveDaysAsync(int days, string user)
    {
        if (days is < Export.ExportArchive.MinDays or > Export.ExportArchive.MaxDays)
            throw new InvalidOperationException($"The local export archive keeps {Export.ExportArchive.MinDays} to {Export.ExportArchive.MaxDays} days.");
        await repo.SaveSystemSettingsAsync(new Dictionary<string, string?> { [Export.ExportArchive.DaysKey] = Str(days) }, user);
    }

    public async Task<AlertSettings> GetAlertSettingsAsync()
    {
        var rows = await repo.GetSystemSettingsAsync();
        return new AlertSettings(Int(rows, AlertThrottleKey, 60) ?? 60, Int(rows, AlertGraceKey, 15) ?? 15);
    }

    public async Task SaveAlertSettingsAsync(AlertSettings s, string user)
    {
        if (s.ThrottleMinutes is < 1 or > 10080) throw new InvalidOperationException("Repeat interval must be between 1 minute and 7 days.");
        if (s.MissedGraceMinutes is < 1 or > 1440) throw new InvalidOperationException("Missed-run grace must be between 1 and 1440 minutes.");
        await repo.SaveSystemSettingsAsync(new Dictionary<string, string?>
        {
            [AlertThrottleKey] = Str(s.ThrottleMinutes), [AlertGraceKey] = Str(s.MissedGraceMinutes)
        }, user);
    }

    /// <summary>Alerting starts at this point: on the very first dispatcher run nothing older is reported (no flood of history).</summary>
    public async Task<DateTime> GetOrCreateAlertBaselineAsync()
    {
        var rows = await repo.GetSystemSettingsAsync();
        if (rows.TryGetValue(AlertBaselineKey, out var v) && DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var b))
            return b;
        var now = DateTime.UtcNow;
        await repo.SaveSystemSettingsAsync(new Dictionary<string, string?> { [AlertBaselineKey] = now.ToString("O", CultureInfo.InvariantCulture) }, "system");
        return now;
    }

    private static string? Str(int? v) => v?.ToString(CultureInfo.InvariantCulture);

    private static int? Int(IReadOnlyDictionary<string, string?> rows, string key, int? fallback) =>
        !rows.TryGetValue(key, out var v) ? fallback
        : int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static bool Bool(IReadOnlyDictionary<string, string?> rows, string key, bool fallback) =>
        rows.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;
}
