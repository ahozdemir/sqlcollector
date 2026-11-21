using System.Net.Http.Json;
using System.Text.Json;
using MailKit;   // MailKit 5: SendAsync(MimeMessage, ...) is an extension method here
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace SqlDataCollector.Alerts;

/// <summary>Delivers one alert to one destination: e-mail (SMTP via MailKit), Microsoft Teams or a generic webhook.</summary>
public sealed class AlertSender(IOptions<AlertOptions> options)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string? PublicUrl => options.Value.PublicUrl;

    public Task SendAsync(AlertChannelDef channel, AlertEvent e, CancellationToken ct) => channel.ChannelType switch
    {
        AlertChannelTypes.Email => SendEmailAsync(AlertFormat.SplitList(channel.Target), e, ct),
        AlertChannelTypes.Teams => PostAsync(channel.Target, AlertFormat.TeamsPayload(e, PublicUrl), ct),
        AlertChannelTypes.Webhook => PostAsync(channel.Target, AlertFormat.WebhookPayload(e, PublicUrl), ct),
        _ => throw new InvalidOperationException($"Unknown channel type '{channel.ChannelType}'.")
    };

    public async Task SendEmailAsync(IReadOnlyList<string> recipients, AlertEvent e, CancellationToken ct)
    {
        var smtp = options.Value.Smtp;
        if (string.IsNullOrWhiteSpace(smtp.Host))
            throw new InvalidOperationException("E-mail alerts need an SMTP server: set Alerts:Smtp:Host (and Port, Security, From) in appsettings.");
        if (recipients.Count == 0) throw new InvalidOperationException("No e-mail recipients.");

        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(smtp.From));
        foreach (var r in recipients) msg.To.Add(MailboxAddress.Parse(r));
        msg.Subject = $"[SqlDataCollector] {AlertFormat.Title(e)}";
        msg.Body = new BodyBuilder { TextBody = AlertFormat.PlainText(e, PublicUrl), HtmlBody = AlertFormat.Html(e, PublicUrl) }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 20_000 };
        var security = smtp.Security?.Trim().ToLowerInvariant() switch
        {
            "none" => SecureSocketOptions.None,
            "starttls" => SecureSocketOptions.StartTls,
            "sslonconnect" or "ssl" => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.Auto
        };
        await client.ConnectAsync(smtp.Host, smtp.Port, security, ct);
        if (!string.IsNullOrEmpty(smtp.UserName)) await client.AuthenticateAsync(smtp.UserName, smtp.Password ?? "", ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
    }

    private static async Task PostAsync(string url, object payload, CancellationToken ct)
    {
        using var resp = await Http.PostAsJsonAsync(url.Trim(), payload, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Webhook returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {(body.Length > 300 ? body[..300] : body)}");
        }
    }

    /// <summary>A sample event for the "Send test" button.</summary>
    public static AlertEvent TestEvent(string user) => new(AlertEvents.Failed, "Extract", 0, "test-alert", 0, DateTime.UtcNow,
        StartedAtUtc: DateTime.UtcNow.AddSeconds(-42), FinishedAtUtc: DateTime.UtcNow, Rows: 0, Error: "This is a test alert – no job failed.",
        HostName: Environment.MachineName, TriggeredBy: user);
}
