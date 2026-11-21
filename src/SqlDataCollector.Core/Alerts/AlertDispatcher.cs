using System.Collections.Concurrent;
using Hangfire;
using SqlDataCollector.Extract;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;
using SqlDataCollector.Services;

namespace SqlDataCollector.Alerts;

/// <summary>
/// Finds alert events (problem runs, recoveries, missed schedules, queues without a Hangfire server) and delivers them
/// to the matching channels and to the jobs' own recipients. Runs every minute in the Web role (AlertBackgroundService),
/// independent of Hangfire Workers, so it also reports when no Worker is running. One instance at a time (sp_getapplock).
/// Every delivery is recorded in etl.AlertLog: no duplicates, up to 3 attempts, repeats of the same problem throttled.
/// </summary>
public sealed class AlertDispatcher(
    MetadataRepository repo,
    SystemSettingsService settings,
    AlertSender sender,
    SqlConnectionFactory factory,
    IServiceProvider services,
    ILogger<AlertDispatcher> logger)
{
    public const int MaxAttempts = 3;
    /// <summary>A queue must be without a live server this long before NoServer is reported (restarts are not alerts).</summary>
    public static readonly TimeSpan NoServerDelay = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTime> _queueMissingSince = new();

    /// <summary>One dispatch cycle; returns the number of delivery attempts.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var l = await AppLock.TryAcquireAsync(factory.MetadataConnectionString, LockNames.Alerts, 0, ct);
        if (l is null) return 0;

        var now = DateTime.UtcNow;
        var baseline = await settings.GetOrCreateAlertBaselineAsync();
        var cfg = await settings.GetAlertSettingsAsync();
        var since = baseline > now.AddHours(-24) ? baseline : now.AddHours(-24);

        var events = new List<AlertEvent>();
        events.AddRange(await RunEventsAsync(since));
        events.AddRange(await MissedEventsAsync(now, baseline, TimeSpan.FromMinutes(cfg.MissedGraceMinutes)));
        events.AddRange(NoServerEvents(now, cfg.ThrottleMinutes));
        if (CertificateEvent(now) is { } certEvent) events.Add(certEvent);
        if (events.Count == 0) return 0;

        var channels = (await repo.GetAlertChannelsAsync()).Where(c => c.IsActive).ToList();
        var log = (await repo.GetAlertLogSinceAsync(since.AddDays(-1)))
            .ToDictionary(r => (r.EventType, r.JobKind, r.JobId, r.RunId, r.ChannelKey));

        int attempts = 0;
        foreach (var e0 in events)
        {
            var destinations = channels.Where(c => AlertFormat.Matches(c, e0))
                .Select(c => (Key: $"C{c.AlertChannelId}", Name: c.Name, Send: (Func<AlertEvent, Task>)(e => sender.SendAsync(c, e, ct))))
                .ToList();
            if (!string.IsNullOrWhiteSpace(e0.NotifyEmails) && AlertEvents.JobRecipients.Contains(e0.EventType))
            {
                var to = AlertFormat.SplitList(e0.NotifyEmails);
                destinations.Add(("J", "job recipients", e => sender.SendEmailAsync(to, e, ct)));
            }

            AlertEvent? enriched = null;
            foreach (var d in destinations)
            {
                log.TryGetValue((e0.EventType, e0.JobKind, e0.JobId, e0.RunId, d.Key), out var existing);
                if (existing is { Status: "Sent" or "Suppressed" } or { Attempts: >= MaxAttempts }) continue;

                var row = existing ?? new AlertLogRow
                {
                    EventType = e0.EventType, JobKind = e0.JobKind, JobId = e0.JobId, JobName = e0.JobName, RunId = e0.RunId,
                    ChannelKey = d.Key, Destination = d.Name
                };
                row.Destination = d.Name;

                // the same problem again within the repeat interval: recorded, not sent (a status change such as Recovered always goes out)
                if (existing is null && e0.EventType != AlertEvents.Recovered && e0.JobKind != "System"
                    && await repo.WasAlertSentSinceAsync(e0.JobKind, e0.JobId, e0.EventType, d.Key, e0.RunId, now.AddMinutes(-cfg.ThrottleMinutes)))
                {
                    row.Status = "Suppressed";
                    await repo.SaveAlertLogAsync(row);
                    log[(row.EventType, row.JobKind, row.JobId, row.RunId, row.ChannelKey)] = row;
                    continue;
                }

                enriched ??= await EnrichAsync(e0);
                row.Attempts++;
                attempts++;
                try
                {
                    await d.Send(enriched);
                    row.Status = "Sent";
                    row.SentAtUtc = DateTime.UtcNow;
                    row.LastError = null;
                    logger.LogInformation("Alert sent: {Title} -> {Destination}", AlertFormat.Title(enriched), d.Name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    row.Status = "Failed";
                    row.LastError = ex.Message;
                    logger.LogWarning(ex, "Alert delivery failed (attempt {Attempt}/{Max}): {Title} -> {Destination}",
                        row.Attempts, MaxAttempts, AlertFormat.Title(enriched), d.Name);
                }
                await repo.SaveAlertLogAsync(row);
                log[(row.EventType, row.JobKind, row.JobId, row.RunId, row.ChannelKey)] = row;
            }
        }
        return attempts;
    }

    private async Task<IReadOnlyList<AlertEvent>> RunEventsAsync(DateTime since)
    {
        var list = new List<AlertEvent>();
        foreach (var r in await repo.GetFinishedRunsAsync(since))
        {
            var at = r.FinishedAtUtc ?? r.StartedAtUtc;
            if (AlertEvents.Problems.Contains(r.Status))
                list.Add(new AlertEvent(r.Status, r.Kind, r.JobId, r.JobName, r.RunId, at, r.NotifyEmails, r.StartedAtUtc, r.FinishedAtUtc,
                    r.Rows, r.ErrorMessage, r.HostName, r.TriggeredBy));
            else if (r.Status == "Succeeded" && r.PrevStatus is not null && AlertEvents.Problems.Contains(r.PrevStatus))
                list.Add(new AlertEvent(AlertEvents.Recovered, r.Kind, r.JobId, r.JobName, r.RunId, at, r.NotifyEmails, r.StartedAtUtc, r.FinishedAtUtc,
                    r.Rows, null, r.HostName, r.TriggeredBy, Detail: $"Previous run: {r.PrevStatus}."));
        }
        return list;
    }

    private async Task<IReadOnlyList<AlertEvent>> MissedEventsAsync(DateTime now, DateTime baseline, TimeSpan grace)
    {
        var last = (await repo.GetLastRunStartsAsync()).ToDictionary(x => (x.Kind, x.JobId));
        var list = new List<AlertEvent>();

        void Check(string kind, int id, string name, string cron, string tz, DateTime modified, string? notify)
        {
            last.TryGetValue((kind, id), out var s);
            var occ = AlertFormat.MissedOccurrence(cron, tz, now, grace, s == default ? null : s.LastStartUtc, s.Running, baseline, modified);
            if (occ is { } o)
                list.Add(new AlertEvent(AlertEvents.Missed, kind, id, name, AlertFormat.UnixMinutes(o), now, notify,
                    Detail: $"Scheduled for {o:yyyy-MM-dd HH:mm} UTC ({cron}, {tz}); no run started within {grace.TotalMinutes:N0} minutes. Is a Worker running for this queue?"));
        }

        foreach (var j in (await repo.GetExtractJobsAsync()).Where(j => j.IsActive && j.TriggerMode == JobTriggerMode.Schedule))
            Check("Extract", j.ExtractJobId, j.Name, j.CronExpression, j.TimeZoneId, j.ModifiedAtUtc, j.NotifyEmails);
        foreach (var e in (await repo.GetExportJobsAsync()).Where(e => e.IsActive && e.TriggerMode == JobTriggerMode.Schedule))
            Check("Export", e.ExportJobId, e.Name, e.CronExpression, e.TimeZoneId, e.ModifiedAtUtc, e.NotifyEmails);
        return list;
    }

    private IReadOnlyList<AlertEvent> NoServerEvents(DateTime now, int throttleMinutes)
    {
        if (services.GetService(typeof(JobStorage)) is not JobStorage storage) return [];
        List<Hangfire.Storage.Monitoring.ServerDto> live;
        try
        {
            live = storage.GetMonitoringApi().Servers()
                .Where(s => s.Heartbeat is null || s.Heartbeat > now.AddMinutes(-5)).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Alerts: could not read the Hangfire servers.");
            return [];
        }

        var list = new List<AlertEvent>();
        for (int i = 0; i < CollectorQueues.All.Length; i++)
        {
            var q = CollectorQueues.All[i];
            if (live.Any(s => s.Queues.Contains(q, StringComparer.OrdinalIgnoreCase))) { _queueMissingSince.TryRemove(q, out _); continue; }
            var missingSince = _queueMissingSince.GetOrAdd(q, now);
            if (now - missingSince < NoServerDelay) continue;
            var what = q switch { CollectorQueues.Extract => "extract jobs", CollectorQueues.Export => "export jobs", _ => "sync, retention and cleanup" };
            list.Add(new AlertEvent(AlertEvents.NoServer, "System", i, $"queue {q}", AlertFormat.UnixMinutes(now) / Math.Max(1, throttleMinutes), now,
                Detail: $"No live Hangfire server has listened to queue {q} since {missingSince:yyyy-MM-dd HH:mm} UTC: {what} are not executed."));
        }
        return list;
    }

    /// <summary>Once a day while the HTTPS certificate expires within 30 days (or has expired).</summary>
    private AlertEvent? CertificateEvent(DateTime now)
    {
        if (services.GetService(typeof(ICertificateStatus)) is not ICertificateStatus status || status.Current is not { } c) return null;
        var days = (c.NotAfterUtc - now).TotalDays;
        if (days > AlertEvents.CertWarningDays) return null;
        return new AlertEvent(AlertEvents.CertExpiring, "System", 100, "HTTPS certificate", AlertFormat.UnixMinutes(now) / 1440, now,
            Detail: days > 0
                ? $"{c.Subject} expires on {c.NotAfterUtc:yyyy-MM-dd} ({Math.Floor(days):N0} days). Import a new certificate on Settings."
                : $"{c.Subject} expired on {c.NotAfterUtc:yyyy-MM-dd}. Browsers reject the site: import a new certificate on Settings.");
    }

    private async Task<AlertEvent> EnrichAsync(AlertEvent e) =>
        AlertEvents.Problems.Contains(e.EventType) && e.JobKind != "System"
            ? e with { ConsecutiveFailures = await repo.GetConsecutiveProblemCountAsync(e.JobKind, e.JobId, e.RunId) }
            : e;
}

/// <summary>Runs the alert dispatcher every minute (registered in the Web role).</summary>
public sealed class AlertBackgroundService(AlertDispatcher dispatcher, ILogger<AlertBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        do
        {
            try { await dispatcher.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Alert dispatch failed; retrying in a minute."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
