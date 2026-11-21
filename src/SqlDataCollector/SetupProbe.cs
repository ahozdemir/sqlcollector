using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting.WindowsServices;
using SqlDataCollector.Options;

namespace SqlDataCollector;

/// <summary>
/// Startup check of the metadata database. When it is not usable yet (installer option "create the databases later",
/// DBA scripts not run, wrong connection string) the Web role serves only an explanation page instead of failing to start,
/// and restarts itself (as a Windows service) as soon as the database is ready.
/// </summary>
public static class SetupProbe
{
    /// <summary>null = ready; otherwise a description of the problem.</summary>
    public static async Task<string?> ProbeAsync(IConfiguration configuration, CollectorOptions options, CancellationToken ct = default)
    {
        var cs = configuration.GetConnectionString("Metadata");
        if (string.IsNullOrWhiteSpace(cs)) return "ConnectionStrings:Metadata is not configured.";
        try
        {
            var b = new SqlConnectionStringBuilder(cs) { ConnectTimeout = 15, Pooling = false };
            await using var c = new SqlConnection(b.ConnectionString);
            await c.OpenAsync(ct);
            if (options.AutoCreateSchema) return null;   // the schema is installed at startup
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT CASE WHEN OBJECT_ID(N'etl.ExtractJob', N'U') IS NULL THEN 0 ELSE 1 END;";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1
                ? null
                : $"The etl schema is missing in database '{b.InitialCatalog}' (Collector:AutoCreateSchema is false): run the setup scripts.";
        }
        catch (SqlException ex)
        {
            var b = new SqlConnectionStringBuilder(cs);
            return $"The metadata database '{b.InitialCatalog}' on '{b.DataSource}' cannot be used: {ex.Message}";
        }
        catch (ArgumentException ex)
        {
            return $"ConnectionStrings:Metadata is invalid: {ex.Message}";
        }
    }

    public static void MapSetupRequiredPage(WebApplication app, string problem, IConfiguration configuration, CollectorOptions options)
    {
        var scripts = configuration["Setup:ScriptsDirectory"];
        app.MapGet("/health", () => Results.Json(new { status = "setup-required", error = problem }, statusCode: 503));
        app.MapFallback(() => Results.Content(Page(problem, scripts), "text/html; charset=utf-8", statusCode: 503));

        // as a Windows service: exit with an error code once the database is ready, so the service recovery restarts it
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Setup");
        logger.LogError("The metadata database is not ready: {Problem}. Serving the setup page.", problem);
        _ = Task.Run(async () =>
        {
            while (!lifetime.ApplicationStopping.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), lifetime.ApplicationStopping); } catch (OperationCanceledException) { return; }
                if (await ProbeAsync(configuration, options, lifetime.ApplicationStopping) is not null) continue;
                logger.LogWarning("The metadata database is ready now; restarting.");
                if (WindowsServiceHelpers.IsWindowsService()) Environment.Exit(3);
                return;
            }
        });
    }

    private static string Page(string problem, string? scripts)
    {
        static string e(string? s) => WebUtility.HtmlEncode(s ?? "");
        var scriptHint = string.IsNullOrWhiteSpace(scripts)
            ? "<li>Create the databases and permissions, e.g. with <code>SqlDataCollector.exe db-install</code> (see README) or the scripts of the installation.</li>"
            : $"<li>Run the SQL scripts in <code>{e(scripts)}</code> (in numeric order, with a sysadmin/DBA account), or let the installer create the databases.</li>";
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><meta http-equiv="refresh" content="30">
            <title>Setup required – SqlDataCollector</title>
            <style>body{font-family:Segoe UI,Arial,sans-serif;max-width:760px;margin:48px auto;padding:0 16px;color:#222}
            code{background:#f3f3f3;padding:1px 4px;border-radius:3px}.box{border:1px solid #e0b4b4;background:#fff6f6;padding:12px 16px;border-radius:6px}</style>
            </head><body>
            <h1>SqlDataCollector – setup required</h1>
            <div class="box"><b>The metadata database is not ready.</b><br>{{e(problem)}}</div>
            <h3>What to do</h3>
            <ol>
            {{scriptHint}}
            <li>Check <code>ConnectionStrings:Metadata</code> in <code>appsettings.Production.json</code> and that the service account can sign in to SQL Server.</li>
            <li>The service checks the database every 30 seconds and restarts itself when it is ready; this page reloads automatically.
                Otherwise restart it: <code>Restart-Service SqlDataCollector</code>.</li>
            </ol>
            </body></html>
            """;
    }
}
