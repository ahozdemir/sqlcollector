using Hangfire;
using SqlDataCollector;
using Hangfire.Dashboard;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Data.SqlClient;
using SqlDataCollector.Components;
using SqlDataCollector.Export;
using SqlDataCollector.Extract;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;
using SqlDataCollector.Options;
using SqlDataCollector.Scheduling;
using SqlDataCollector.Security;
using SqlDataCollector.Services;

// ---- Helper command: generate the local admin password hash ----
//   SqlDataCollector.exe hash-password
if (args.Length > 0 && args[0] == "hash-password")
{
    Console.Write("New local admin password: ");
    var pwd = ReadSecret();
    if (pwd.Length < 12) { Console.Error.WriteLine("Must be at least 12 characters."); return 1; }
    Console.WriteLine();
    Console.WriteLine("Value for appsettings.json -> LocalAdmin:PasswordHash:");
    Console.WriteLine(PasswordHasher.Hash(pwd));
    return 0;
}

// ---- Helper command: install + permission script for the central Hangfire database (hand to the DBA) ----
//   SqlDataCollector.exe hangfire-install-script [schema] [DOMAIN\appSvc$] [DOMAIN\workerSvc$]
if (args.Length > 0 && args[0] == "hangfire-install-script")
{
    var schema = args.Length > 1 ? args[1] : "hangfire_collector";
    Console.WriteLine($"-- Hangfire {typeof(Hangfire.JobStorage).Assembly.GetName().Version} SQL Server schema: [{schema}]");
    Console.WriteLine("-- Run once in the central Hangfire database with a db_owner account (SSMS or sqlcmd).");
    Console.WriteLine("SET ANSI_NULLS ON;\nSET QUOTED_IDENTIFIER ON;  -- sqlcmd defaults to OFF; required for filtered indexes\nGO");
    Console.WriteLine(Hangfire.SqlServer.SqlServerObjectsInstaller.GetInstallScript(schema, false));
    Console.WriteLine("GO");
    Console.WriteLine($"-- Least privilege for the application service accounts (Web and Worker need the same):");
    Console.WriteLine("IF DATABASE_PRINCIPAL_ID('SqlDataCollectorHangfire') IS NULL CREATE ROLE SqlDataCollectorHangfire;");
    Console.WriteLine($"GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[{schema}] TO SqlDataCollectorHangfire;");
    foreach (var acc in args.Skip(2))
    {
        Console.WriteLine($"IF SUSER_ID(N'{acc}') IS NULL CREATE LOGIN [{acc}] FROM WINDOWS;");
        Console.WriteLine($"IF DATABASE_PRINCIPAL_ID(N'{acc}') IS NULL CREATE USER [{acc}] FOR LOGIN [{acc}];");
        Console.WriteLine($"ALTER ROLE SqlDataCollectorHangfire ADD MEMBER [{acc}];");
    }
    return 0;
}

// ---- Helper command: apply the Hangfire schema directly (DBA / docker init) ----
//   SqlDataCollector.exe hangfire-install "<connection string>" [schema]
if (args.Length > 1 && args[0] == "hangfire-install")
{
    var schema = args.Length > 2 ? args[2] : "hangfire_collector";
    using var conn = new SqlConnection(args[1]);
    conn.Open();
    Hangfire.SqlServer.SqlServerObjectsInstaller.Install(conn, schema, enableHeavyMigrations: true);
    Console.WriteLine($"Hangfire schema [{schema}] installed/upgraded on {conn.DataSource}/{conn.Database}.");
    return 0;
}

// ---- Helper command: apply the etl metadata schema directly ----
//   SqlDataCollector.exe metadata-install "<connection string>"
if (args.Length > 1 && args[0] == "metadata-install")
{
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Metadata"] = args[1] }).Build();
    using var lf = LoggerFactory.Create(b => b.AddSimpleConsole());
    await SchemaInstaller.InstallAsync(new SqlConnectionFactory(config), lf.CreateLogger("metadata-install"), CancellationToken.None);
    return 0;
}

// ---- Installer helper commands ----
//   SqlDataCollector.exe create-cert --host sdc.corp.local [--dir C:\ProgramData\SqlDataCollector\certs]
//   SqlDataCollector.exe db-script --meta SdcMeta --dwh DWH [--account DOMAIN\svc] [--central --hangfire-db HangfireDB --hangfire-schema s] --out <dir>
//   SqlDataCollector.exe db-install --server <sql> --meta SdcMeta --dwh DWH [--account DOMAIN\svc] [--trust-server-certificate]   (current Windows account)
//   SqlDataCollector.exe new-secret-key
if (args.Length > 0 && args[0] is "create-cert" or "db-script" or "db-install" or "new-secret-key")
{
    string? Arg(string name) { var i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    bool Flag(string name) => args.Contains("--" + name);
    try
    {
        switch (args[0])
        {
            case "new-secret-key":
                Console.WriteLine(SecretProtector.NewKey());
                return 0;
            case "create-cert":
            {
                var host = Arg("host") ?? Environment.MachineName.ToLowerInvariant();
                var certs = new CertificateManager(new HttpsOptions { Port = 443, HostName = host, CertificateDirectory = Arg("dir") });
                var c = certs.CreateAndUseSelfSigned(host);
                Console.WriteLine($"Self-signed certificate created in {certs.Directory}: {c.Subject}, {string.Join(", ", c.DnsNames)}, valid until {c.NotAfterUtc:yyyy-MM-dd}, thumbprint {c.Thumbprint}");
                return 0;
            }
            case "db-script":
            {
                var o = new DbSetupOptions(Arg("meta") ?? "", Arg("dwh") ?? "", Arg("account"), Flag("central"), Arg("hangfire-db"), Arg("hangfire-schema") ?? "hangfire_collector");
                var dir = Arg("out") ?? throw new ArgumentException("--out <folder> is required.");
                Directory.CreateDirectory(dir);
                foreach (var (file, sql) in DbSetup.Scripts(o))
                {
                    File.WriteAllText(Path.Combine(dir, file), sql);
                    Console.WriteLine($"Written: {Path.Combine(dir, file)}");
                }
                return 0;
            }
            case "db-install":
            {
                var o = new DbSetupOptions(Arg("meta") ?? "", Arg("dwh") ?? "", Arg("account"));
                var cs = new SqlConnectionStringBuilder
                {
                    DataSource = Arg("server") ?? throw new ArgumentException("--server is required."),
                    IntegratedSecurity = true, TrustServerCertificate = Flag("trust-server-certificate"), ConnectTimeout = 30,
                    ApplicationName = "SqlDataCollector-Setup"
                };
                await DbSetup.InstallAsync(cs.ConnectionString, o, Console.WriteLine, CancellationToken.None);
                return 0;
            }
        }
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SqlException or IOException or UnauthorizedAccessException
                               or System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
    {
        Console.Error.WriteLine($"{args[0]} failed: {ex.Message}");
        return 1;
    }
}

// ---- Helper command: run jobs in-process, without Hangfire (local testing / debugging) ----
//   SqlDataCollector.exe run-job extract <id|all>     SqlDataCollector.exe run-job export <id|all>
//   Uses the normal configuration (appsettings.json + appsettings.{ASPNETCORE_ENVIRONMENT}.json + environment).
if (args.Length > 2 && args[0] == "run-job")
{
    var cli = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        ContentRootPath = AppContext.BaseDirectory,
        EnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Production
    });
    cli.Services.AddSqlDataCollectorCore(cli.Configuration);
    using var host = cli.Build();
    // like the service start: an existing metadata DB is upgraded first (new columns), unless the DBA manages the schema
    var cliOptions = cli.Configuration.GetSection(CollectorOptions.Section).Get<CollectorOptions>() ?? new();
    if (cliOptions.AutoCreateSchema)
        await SchemaInstaller.InstallAsync(host.Services.GetRequiredService<SqlConnectionFactory>(),
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("run-job"), CancellationToken.None);
    var repo = host.Services.GetRequiredService<MetadataRepository>();
    var kind = args[1].ToLowerInvariant();
    var all = args[2].Equals("all", StringComparison.OrdinalIgnoreCase);
    int failures = 0;

    async Task RunOne(string name, Func<Task> run)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { await run(); Console.WriteLine($"OK    {kind} {name} ({sw.Elapsed.TotalSeconds:N1} s)"); }
        catch (Exception ex) { failures++; Console.WriteLine($"FAIL  {kind} {name}: {ex.Message}"); }
    }

    if (kind == "extract")
    {
        var jobs = (await repo.GetExtractJobsAsync()).Where(j => all ? j.IsActive : j.ExtractJobId == int.Parse(args[2])).ToList();
        if (jobs.Count == 0) { Console.Error.WriteLine("No matching extract job."); return 2; }
        foreach (var j in jobs)
            await RunOne(j.Name, () => host.Services.GetRequiredService<ExtractRunner>().RunAsync(j.ExtractJobId, j.Name, "cli", CancellationToken.None));
    }
    else if (kind == "export")
    {
        var jobs = (await repo.GetExportJobsAsync()).Where(j => all ? j.IsActive : j.ExportJobId == int.Parse(args[2])).ToList();
        if (jobs.Count == 0) { Console.Error.WriteLine("No matching export job."); return 2; }
        foreach (var j in jobs)
            await RunOne(j.Name, () => host.Services.GetRequiredService<ExportRunner>().RunAsync(j.ExportJobId, j.Name, "cli", CancellationToken.None));
    }
    else { Console.Error.WriteLine("Usage: run-job extract|export <id|all>"); return 2; }
    return failures == 0 ? 0 : 1;
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "SqlDataCollector");
// Static web assets (Blazor framework files, wwwroot) when running unpublished outside Development, e.g. the LocalTest profile
if (builder.Environment.IsEnvironment("LocalTest")) builder.WebHost.UseStaticWebAssets();

var collector = builder.Configuration.GetSection(CollectorOptions.Section).Get<CollectorOptions>() ?? new();
var dashboard = builder.Configuration.GetSection(DashboardSettings.Section).Get<DashboardSettings>() ?? new();

// HTTPS (Https:Port > 0): Kestrel serves the certificate of the CertificateManager (self-signed until one is imported on Settings)
var httpsOptions = builder.Configuration.GetSection(HttpsOptions.Section).Get<HttpsOptions>() ?? new();
var certificates = new CertificateManager(httpsOptions);
if (collector.IsWeb && certificates.Enabled)
{
    var cert = certificates.EnsureCertificate();
    Console.WriteLine($"HTTPS on port {httpsOptions.Port}: {cert.Subject}, valid until {cert.NotAfterUtc:yyyy-MM-dd}{(cert.SelfSigned ? " (self-signed)" : "")}");
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.ListenAnyIP(httpsOptions.Port, lo =>
        {
            lo.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;   // Windows authentication does not work over HTTP/2
            lo.UseHttps(h => h.ServerCertificateSelector = (_, _) => certificates.Current);
        });
        if (httpsOptions.HttpRedirectPort > 0) k.ListenAnyIP(httpsOptions.HttpRedirectPort);
    });
    if (httpsOptions.HttpRedirectPort > 0) builder.Services.AddHttpsRedirection(o => o.HttpsPort = httpsOptions.Port);
}
builder.Services.AddSingleton(certificates);
if (certificates.Enabled) builder.Services.AddSingleton<SqlDataCollector.Alerts.ICertificateStatus>(certificates);

// The metadata database must be reachable before Hangfire and the job services start. If it is not (e.g. the installer's
// "create the databases later" option), the Web role serves a page that explains what to do; a Worker exits and is restarted.
if (await SetupProbe.ProbeAsync(builder.Configuration, collector) is { } setupProblem)
{
    Console.Error.WriteLine($"Database not ready: {setupProblem}");
    if (!collector.IsWeb) return 3;
    var setupApp = builder.Build();
    SetupProbe.MapSetupRequiredPage(setupApp, setupProblem, builder.Configuration, collector);
    await setupApp.RunAsync();
    return 0;
}

builder.Services.Configure<LocalAdminOptions>(builder.Configuration.GetSection(LocalAdminOptions.Section));

// Job code and services (SqlDataCollector.Core). The same call is used by an organization's existing
// Hangfire Server host in the "shared Hangfire" deployment (see deploy/shared-hangfire/existing-host).
builder.Services.AddSqlDataCollectorCore(builder.Configuration);

// Web and Worker share the same Hangfire storage (SQL): Web writes the job, Worker reads and executes it.
// Embedded mode: ConnectionStrings:Hangfire empty -> Hangfire tables live in the metadata DB.
builder.Services.AddHangfire(cfg => cfg.UseSqlDataCollectorStorage(builder.Configuration));

// Order matters: StartupService (schema + system jobs) starts before the Hangfire server
builder.Services.AddSqlDataCollectorStartup();

if (collector.IsWorker)
{
    builder.Services.AddHangfireServer(o =>
    {
        o.ServerName = $"{Environment.MachineName}:collector";
        o.WorkerCount = Math.Max(1, collector.WorkerCount);
        o.Queues = collector.QueueList;
    });
}

if (collector.IsWeb)
{
    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o =>
        {
            o.LoginPath = "/login";
            o.AccessDeniedPath = "/denied";
            o.Cookie.Name = "sdc.auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
            o.Events.OnValidatePrincipal = AuthEndpoints.ValidatePrincipalAsync;
        })
        .AddNegotiate();

    builder.Services.AddAuthorizationBuilder()
        .AddPolicy(AppRoles.Admin, p => p.RequireRole(AppRoles.Admin))
        .AddPolicy(AppRoles.Editor, p => p.RequireRole(AppRoles.Admin, AppRoles.Editor))
        .AddPolicy(AppRoles.Viewer, p => p.RequireRole(AppRoles.Admin, AppRoles.Editor, AppRoles.Viewer));

    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddRazorComponents().AddInteractiveServerComponents();
    builder.Services.AddSingleton<PrincipalBuilder>();
    builder.Services.AddSingleton<LoginThrottle>();
    builder.Services.AddSingleton<LocalAdminStore>();
    builder.Services.AddScoped<AccessService>();
    // Job failure alerts: dispatched by the Web role, independent of Hangfire Workers (reports missing Workers too)
    builder.Services.AddHostedService<SqlDataCollector.Alerts.AlertBackgroundService>();
}

var app = builder.Build();
SqlDataCollector.Components.Shared.Ui.Tz = TimeZones.Resolve(collector.SystemTimeZone);

if (collector.IsWeb)
{
    if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error", createScopeForErrors: true);
    if (certificates.Enabled && httpsOptions.HttpRedirectPort > 0) app.UseHttpsRedirection();
    app.UseStatusCodePagesWithReExecute("/not-found");
    app.MapStaticAssets();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.MapAuthEndpoints();
    app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

    // Hangfire Dashboard (monitoring): full access for Admin, read-only for Editor/Viewer.
    app.MapHangfireDashboard(dashboard.Path, new DashboardOptions
    {
        DashboardTitle = "SqlDataCollector – Hangfire",
        AppPath = "/",
        Authorization = [new AuthenticatedDashboardFilter()],
        IsReadOnlyFunc = ctx => !ctx.GetHttpContext().User.IsInRole(AppRoles.Admin)
    }).RequireAuthorization(AppRoles.Viewer);
}

app.MapGet("/health", async (SqlConnectionFactory f, CancellationToken ct) =>
{
    try
    {
        await using var c = await f.OpenMetadataAsync(ct);
        return Results.Ok(new { status = "ok", server = Environment.MachineName, roles = collector.Roles });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "error", error = ex.Message }, statusCode: 503);
    }
}).AllowAnonymous();

app.Run();
return 0;

static string ReadSecret()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        sb.Append(k.KeyChar);
    }
    return sb.ToString();
}

/// <summary>Authorization is enforced by the endpoint policy (RequireAuthorization); here we only require an authenticated user.</summary>
internal sealed class AuthenticatedDashboardFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => context.GetHttpContext().User.Identity?.IsAuthenticated == true;
}
