using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using Cronos;
using SqlDataCollector.Infrastructure;

namespace SqlDataCollector.Alerts;

/// <summary>Pure helpers: rule matching, recipients, masking, missed-schedule detection and message bodies.</summary>
public static partial class AlertFormat
{
    // ------------------------------------------------------------------ rules

    public static IReadOnlyList<string> SplitList(string? value) =>
        (value ?? "").Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Does the channel want this event?</summary>
    public static bool Matches(AlertChannelDef channel, AlertEvent e)
    {
        if (!channel.IsActive) return false;
        if (!SplitList(channel.Statuses).Contains(e.EventType, StringComparer.OrdinalIgnoreCase)) return false;
        if (e.JobKind == "System") return true;   // NoServer: not tied to a job
        if (!SplitList(channel.JobKinds).Contains(e.JobKind, StringComparer.OrdinalIgnoreCase)) return false;
        return string.IsNullOrWhiteSpace(channel.JobNameFilter) || WildcardMatch(channel.JobNameFilter.Trim(), e.JobName);
    }

    /// <summary>* and ? wildcards, case-insensitive; several patterns separated by , or ;</summary>
    public static bool WildcardMatch(string patterns, string text) =>
        SplitList(patterns).Any(p => Regex.IsMatch(text, "^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));

    /// <summary>Validates an e-mail list; returns the invalid entries.</summary>
    public static IReadOnlyList<string> InvalidEmails(string? list) =>
        SplitList(list).Where(a => !MailAddress.TryCreate(a, out var m) || m.Address != a).ToList();

    public static string? ValidateChannel(AlertChannelDef ch)
    {
        if (string.IsNullOrWhiteSpace(ch.Name)) return "Name is required.";
        if (!AlertChannelTypes.All.Contains(ch.ChannelType)) return "Unknown channel type.";
        if (string.IsNullOrWhiteSpace(ch.Target)) return ch.ChannelType == AlertChannelTypes.Email ? "Enter at least one e-mail address." : "Enter the webhook URL.";
        if (ch.ChannelType == AlertChannelTypes.Email && InvalidEmails(ch.Target) is { Count: > 0 } bad) return $"Invalid e-mail address(es): {string.Join(", ", bad)}";
        if (ch.ChannelType != AlertChannelTypes.Email
            && (!Uri.TryCreate(ch.Target.Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)))
            return "The webhook URL must be an absolute http(s) URL.";
        if (SplitList(ch.Statuses).Count == 0) return "Select at least one event.";
        if (SplitList(ch.Statuses).FirstOrDefault(s => !AlertEvents.All.Contains(s)) is { } unknown) return $"Unknown event: {unknown}";
        if (SplitList(ch.JobKinds).Count == 0) return "Select extract and/or export jobs.";
        return null;
    }

    /// <summary>Webhook URLs are secrets (whoever knows them can post): only scheme and host are shown.</summary>
    public static string MaskTarget(string channelType, string target)
    {
        if (channelType == AlertChannelTypes.Email) return target;
        return Uri.TryCreate(target, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}/…" : "…";
    }

    /// <summary>
    /// The last scheduled occurrence in (now - 25h, now - grace] that has no run starting at or after it (minus 2 minutes
    /// tolerance), or null. Occurrences before baseline or before the job was last modified are ignored.
    /// </summary>
    public static DateTime? MissedOccurrence(string cron, string timeZoneId, DateTime nowUtc, TimeSpan grace,
        DateTime? lastRunStartUtc, bool running, DateTime baselineUtc, DateTime jobModifiedUtc)
    {
        if (running) return null;
        CronExpression expr;
        try
        {
            var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            expr = CronExpression.Parse(cron.Trim(), parts == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
        }
        catch (CronFormatException) { return null; }
        var tz = TimeZones.Resolve(timeZoneId);
        var to = DateTime.SpecifyKind(nowUtc - grace, DateTimeKind.Utc);
        var from = DateTime.SpecifyKind(nowUtc.AddHours(-25), DateTimeKind.Utc);
        DateTime? last = expr.GetOccurrences(from, to, tz, fromInclusive: true, toInclusive: true)
            .Select(o => (DateTime?)o).LastOrDefault();
        if (last is null) return null;
        if (last < baselineUtc || last < jobModifiedUtc) return null;
        if (lastRunStartUtc is { } s && DateTime.SpecifyKind(s, DateTimeKind.Utc) >= last.Value.AddMinutes(-2)) return null;
        return last;
    }

    public static long UnixMinutes(DateTime utc) => (long)(DateTime.SpecifyKind(utc, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalMinutes;

    // ------------------------------------------------------------------ messages

    public static string Title(AlertEvent e)
    {
        var what = e.JobKind == "System" ? e.JobName : $"{e.JobKind.ToLowerInvariant()} job '{e.JobName}'";
        var tail = e.ConsecutiveFailures > 1 ? $" ({Ordinal(e.ConsecutiveFailures)} consecutive failure)" : "";
        return e.EventType switch
        {
            AlertEvents.Recovered => $"RECOVERED: {what} succeeded again",
            AlertEvents.Missed => $"MISSED: {what} did not run as scheduled",
            AlertEvents.NoServer => $"NO SERVER: {e.JobName}",
            AlertEvents.CertExpiring => "CERTIFICATE: the HTTPS certificate expires soon",
            _ => $"{e.EventType.ToUpperInvariant()}: {what}{tail}"
        };
    }

    public static string? Link(AlertEvent e, string? publicUrl)
    {
        if (string.IsNullOrWhiteSpace(publicUrl)) return null;
        var b = publicUrl.TrimEnd('/');
        return e.JobKind switch
        {
            "Extract" => $"{b}/extract/{e.JobId}",
            "Export" => $"{b}/export/{e.JobId}",
            _ => $"{b}/"
        };
    }

    public static IReadOnlyList<(string Name, string Value)> Facts(AlertEvent e)
    {
        var f = new List<(string, string)>();
        if (e.JobKind != "System") f.Add(("Job", $"{e.JobName} ({e.JobKind} #{e.JobId})"));
        f.Add(("Event", AlertEvents.Describe(e.EventType)));
        if (e.EventType is not (AlertEvents.Missed or AlertEvents.NoServer or AlertEvents.CertExpiring)) f.Add(("Run", e.RunId.ToString(CultureInfo.InvariantCulture)));
        if (e.StartedAtUtc is { } s) f.Add(("Started (UTC)", s.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        if (e.FinishedAtUtc is { } fin) f.Add(("Finished (UTC)", fin.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        if (e.StartedAtUtc is { } s2 && e.FinishedAtUtc is { } f2) f.Add(("Duration", $"{(f2 - s2).TotalSeconds:N1} s"));
        if (e.Rows is { } r) f.Add(("Rows", r.ToString("N0", CultureInfo.InvariantCulture)));
        if (e.ConsecutiveFailures > 1) f.Add(("Consecutive failures", e.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrEmpty(e.HostName)) f.Add(("Host", e.HostName));
        if (!string.IsNullOrEmpty(e.TriggeredBy)) f.Add(("Triggered by", e.TriggeredBy));
        if (!string.IsNullOrEmpty(e.Detail)) f.Add(("Detail", e.Detail));
        if (FirstLine(e.Error) is { } err) f.Add(("Error", err));
        return f;
    }

    public static string PlainText(AlertEvent e, string? publicUrl)
    {
        var sb = new StringBuilder().AppendLine(Title(e)).AppendLine();
        foreach (var (n, v) in Facts(e)) sb.Append(n).Append(": ").AppendLine(v);
        if (Link(e, publicUrl) is { } l) sb.AppendLine().AppendLine(l);
        return sb.ToString();
    }

    public static string Html(AlertEvent e, string? publicUrl)
    {
        var sb = new StringBuilder();
        sb.Append("<h3 style=\"font-family:Segoe UI,Arial\">").Append(WebUtility.HtmlEncode(Title(e))).Append("</h3>");
        sb.Append("<table style=\"font-family:Segoe UI,Arial;font-size:13px;border-collapse:collapse\">");
        foreach (var (n, v) in Facts(e))
            sb.Append("<tr><td style=\"padding:2px 12px 2px 0;color:#555\">").Append(WebUtility.HtmlEncode(n))
              .Append("</td><td style=\"padding:2px 0\">").Append(WebUtility.HtmlEncode(v)).Append("</td></tr>");
        sb.Append("</table>");
        if (Link(e, publicUrl) is { } l) sb.Append("<p><a href=\"").Append(WebUtility.HtmlEncode(l)).Append("\">Open in SqlDataCollector</a></p>");
        return sb.ToString();
    }

    /// <summary>Generic webhook body (also accepted by Slack incoming webhooks thanks to "text").</summary>
    public static object WebhookPayload(AlertEvent e, string? publicUrl) => new
    {
        source = "SqlDataCollector",
        @event = e.EventType,
        jobKind = e.JobKind,
        jobId = e.JobId,
        jobName = e.JobName,
        runId = e.RunId,
        startedAtUtc = e.StartedAtUtc,
        finishedAtUtc = e.FinishedAtUtc,
        rows = e.Rows,
        consecutiveFailures = e.ConsecutiveFailures,
        host = e.HostName,
        triggeredBy = e.TriggeredBy,
        error = FirstLine(e.Error),
        detail = e.Detail,
        url = Link(e, publicUrl),
        text = Title(e) + (FirstLine(e.Error) is { } err ? $" – {err}" : "")
    };

    /// <summary>Microsoft Teams (Workflows "post to a channel when a webhook request is received"): Adaptive Card message.</summary>
    public static object TeamsPayload(AlertEvent e, string? publicUrl)
    {
        var color = e.EventType == AlertEvents.Recovered ? "Good" : e.EventType is AlertEvents.Missed or AlertEvents.CleanupPending ? "Warning" : "Attention";
        var body = new List<object>
        {
            new { type = "TextBlock", text = Title(e), weight = "Bolder", size = "Medium", color, wrap = true },
            new { type = "FactSet", facts = Facts(e).Select(f => new { title = f.Name, value = f.Value }).ToArray() }
        };
        var actions = Link(e, publicUrl) is { } l ? new object[] { new { type = "Action.OpenUrl", title = "Open in SqlDataCollector", url = l } } : [];
        return new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new Dictionary<string, object>
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = "1.4",
                        ["body"] = body,
                        ["actions"] = actions
                    }
                }
            }
        };
    }

    public static string? FirstLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var line = s.Split('\n')[0].Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    private static string Ordinal(int n) => (n % 100) is 11 or 12 or 13 ? $"{n}th" : (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" };
}
