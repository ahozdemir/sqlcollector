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
    builder.Services.AddScoped<AccessService>();
    // Job failure alerts: dispatched by the Web role, independent of Hangfire Workers (reports missing Workers too)
    builder.Services.AddHostedService<SqlDataCollector.Alerts.AlertBackgroundService>();
}

var app = builder.Build();
SqlDataCollector.Components.Shared.Ui.Tz = TimeZones.Resolve(collector.SystemTimeZone);

if (collector.IsWeb)
{
    if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error", createScopeForErrors: true);
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
