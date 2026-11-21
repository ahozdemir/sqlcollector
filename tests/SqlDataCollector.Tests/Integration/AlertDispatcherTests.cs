using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlDataCollector.Alerts;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Services;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Alert dispatch end to end: Mailpit (SMTP) in Docker and an in-process webhook receiver.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AlertDispatcherTests(CollectorFixture fx) : IAsyncLifetime
{
    private IContainer _mailpit = null!;
    private HttpListener _listener = null!;
    private int _hookPort;
    private readonly ConcurrentQueue<(string Path, string Body)> _hooks = new();
    private readonly HttpClient _http = new();
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        _mailpit = new ContainerBuilder("axllent/mailpit:v1.33.0")
            .WithPortBinding(1025, true).WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/api/v1/messages")))
            .Build();
        await _mailpit.StartAsync();

        _hookPort = FreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_hookPort}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    using var r = new StreamReader(ctx.Request.InputStream);
                    _hooks.Enqueue((ctx.Request.Url!.AbsolutePath, await r.ReadToEndAsync()));
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                }
                catch { return; }
            }
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Metadata"] = fx.Get<SqlConnectionFactory>().MetadataConnectionString,
            ["Alerts:PublicUrl"] = "http://sdc.test",
            ["Alerts:Smtp:Host"] = "localhost",
            ["Alerts:Smtp:Port"] = _mailpit.GetMappedPublicPort(1025).ToString(),
            ["Alerts:Smtp:Security"] = "None",
            ["Alerts:Smtp:From"] = "sdc@test.local",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSqlDataCollectorCore(config);
        _sp = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await fx.ExecAsync("DataCollectorMeta", "DELETE etl.AlertChannel; DELETE etl.SystemSetting WHERE Name LIKE 'Alerts.%';");
        _listener.Stop();
        _http.Dispose();
        await _sp.DisposeAsync();
        await _mailpit.DisposeAsync();
    }

    private async Task<IReadOnlyList<(string Subject, string To)>> MailsAsync()
    {
        var json = await _http.GetStringAsync($"http://localhost:{_mailpit.GetMappedPublicPort(8025)}/api/v1/messages");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("Subject").GetString()!, string.Join(";", m.GetProperty("To").EnumerateArray().Select(t => t.GetProperty("Address").GetString()))))
            .ToList();
    }

    private Task InsertRunAsync(int jobId, string status, string? error = null) => fx.ExecAsync("DataCollectorMeta", """
        INSERT etl.LoadRun (ExtractJobId, TargetServerId, TargetDatabase, TargetSchema, TargetTable, Status, StartedAtUtc, FinishedAtUtc, RowsLoaded, HostName, ErrorMessage)
        VALUES (@jobId, 1, 'DWH', 'stage', 'T_Alert', @status, DATEADD(second, -5, SYSUTCDATETIME()), SYSUTCDATETIME(), 0, 'TESTHOST', @error);
        """, new { jobId, status, error });

    /// <summary>Alerting starts one minute ago (SQL Server's clock, so container/host clock drift does not matter).</summary>
    private Task SetBaselineAsync() => fx.ExecAsync("DataCollectorMeta", """
        DELETE etl.SystemSetting WHERE Name LIKE 'Alerts.%';
        INSERT etl.SystemSetting (Name, Value, ModifiedBy) VALUES ('Alerts.BaselineUtc', CONVERT(varchar(33), DATEADD(minute, -1, SYSUTCDATETIME()), 127), 'tests');
        """);

    [Fact]
    public async Task FailureRepeatAndRecovery_AreDeliveredOnce_ToEmailTeamsWebhookAndJobRecipients()
    {
        var dispatcher = _sp.GetRequiredService<AlertDispatcher>();
        var repo = fx.Repo;
        await SetBaselineAsync();

        var job = await fx.SaveAsync(fx.NewExtractJob("T_Alert", j => { j.Name = "alert_job_" + Guid.NewGuid().ToString("N")[..6]; j.NotifyEmails = "owner@test.local"; }));
        foreach (var (name, type, target) in new[]
        {
            ("dba-mail", AlertChannelTypes.Email, "dba@test.local"),
            ("teams", AlertChannelTypes.Teams, $"http://localhost:{_hookPort}/teams"),
            ("hook", AlertChannelTypes.Webhook, $"http://localhost:{_hookPort}/hook"),
            ("broken", AlertChannelTypes.Webhook, "http://localhost:1/unreachable"),
        })
            await repo.SaveAlertChannelAsync(new AlertChannelDef
            {
                Name = name, ChannelType = type, Target = target, Statuses = "Failed,Recovered", JobKinds = "Extract", JobNameFilter = "alert_job_*"
            }, "tests");

        // 1) failure -> dba + owner mail, Teams card, webhook JSON; the broken channel fails
        await InsertRunAsync(job.ExtractJobId, "Failed", "Login failed for user 'x'.\n   at stack");
        await dispatcher.RunOnceAsync(CancellationToken.None);
        var mails = await MailsAsync();
        Assert.Contains(mails, m => m.To == "dba@test.local" && m.Subject == $"[SqlDataCollector] FAILED: extract job '{job.Name}'");
        Assert.Contains(mails, m => m.To == "owner@test.local");
        Assert.Single(_hooks, h => h.Path == "/teams" && h.Body.Contains("application/vnd.microsoft.card.adaptive"));
        var hook = Assert.Single(_hooks, h => h.Path == "/hook");
        using (var doc = JsonDocument.Parse(hook.Body))
        {
            Assert.Equal("Failed", doc.RootElement.GetProperty("event").GetString());
            Assert.Equal(job.Name, doc.RootElement.GetProperty("jobName").GetString());
            Assert.Equal("Login failed for user 'x'.", doc.RootElement.GetProperty("error").GetString());
            Assert.Equal($"http://sdc.test/extract/{job.ExtractJobId}", doc.RootElement.GetProperty("url").GetString());
        }

        // 2) the same problem again within the repeat interval -> recorded as Suppressed, nothing sent
        await InsertRunAsync(job.ExtractJobId, "Failed", "Login failed again");
        await dispatcher.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, (await MailsAsync()).Count);
        Assert.Equal(2, _hooks.Count);
        Assert.Equal(4, await fx.ScalarAsync<int>("DataCollectorMeta",
            "SELECT COUNT(*) FROM etl.AlertLog WHERE JobId = @id AND Status = 'Suppressed'", new { id = job.ExtractJobId }));   // dba-mail, teams, hook, job recipients

        // 3) success after failures -> Recovered goes out at once
        await InsertRunAsync(job.ExtractJobId, "Succeeded");
        await dispatcher.RunOnceAsync(CancellationToken.None);
        mails = await MailsAsync();
        Assert.Equal(4, mails.Count);
        Assert.Contains(mails, m => m.To == "owner@test.local" && m.Subject.Contains("RECOVERED"));
        Assert.Equal(4, _hooks.Count);

        // 4) nothing new -> nothing sent; the broken channel stops after 3 attempts
        await dispatcher.RunOnceAsync(CancellationToken.None);
        await dispatcher.RunOnceAsync(CancellationToken.None);
        Assert.Equal(4, (await MailsAsync()).Count);
        Assert.Equal(4, _hooks.Count);
        var broken = (await repo.GetRecentAlertLogAsync(500)).Where(r => r.JobId == job.ExtractJobId && r.Destination == "broken" && r.EventType == "Failed").ToList();
        Assert.Equal(2, broken.Count);   // both failed runs
        Assert.All(broken, r => { Assert.Equal(AlertDispatcher.MaxAttempts, r.Attempts); Assert.Equal("Failed", r.Status); Assert.NotNull(r.LastError); });
    }

    [Fact]
    public async Task JobWithoutChannelsOrRecipients_ProducesNothing()
    {
        var dispatcher = _sp.GetRequiredService<AlertDispatcher>();
        await SetBaselineAsync();
        var job = await fx.SaveAsync(fx.NewExtractJob("T_Alert", j => j.Name = "quiet_" + Guid.NewGuid().ToString("N")[..6]));
        await InsertRunAsync(job.ExtractJobId, "Failed", "x");
        await dispatcher.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, await fx.ScalarAsync<int>("DataCollectorMeta", "SELECT COUNT(*) FROM etl.AlertLog WHERE JobId = @id AND JobKind = 'Extract'", new { id = job.ExtractJobId }));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
