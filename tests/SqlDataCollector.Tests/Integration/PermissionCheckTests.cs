using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Services;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Permission check against real SQL Server permissions: a low-privileged login and the sysadmin test login.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PermissionCheckTests(CollectorFixture fx)
{
    private const string Login = "sdc_check";
    private const string Password = "Chk_Passw0rd!2026";

    /// <summary>
    /// sdc_check: DML on etl (metadata), db_datareader on DWH (cannot load), db_datareader + db_datawriter on SrcDb (excess).
    /// Returns a checker that connects as sdc_check everywhere, configured as a Worker.
    /// </summary>
    private async Task<(ServiceProvider Services, PermissionChecker Checker)> LowPrivilegeCheckerAsync(string roles)
    {
        await fx.ExecAsync("master", $"IF SUSER_ID('{Login}') IS NULL CREATE LOGIN [{Login}] WITH PASSWORD = '{Password}', CHECK_POLICY = OFF;");
        await fx.ExecAsync("DataCollectorMeta", $"""
            IF DATABASE_PRINCIPAL_ID('{Login}') IS NULL CREATE USER [{Login}] FOR LOGIN [{Login}];
            GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::etl TO [{Login}];
            """);
        await fx.ExecAsync("DWH", $"""
            IF DATABASE_PRINCIPAL_ID('{Login}') IS NULL CREATE USER [{Login}] FOR LOGIN [{Login}];
            ALTER ROLE db_datareader ADD MEMBER [{Login}];
            """);
        await fx.ExecAsync("SrcDb", $"""
            IF DATABASE_PRINCIPAL_ID('{Login}') IS NULL CREATE USER [{Login}] FOR LOGIN [{Login}];
            ALTER ROLE db_datareader ADD MEMBER [{Login}];
            ALTER ROLE db_datawriter ADD MEMBER [{Login}];
            """);

        await using var master = fx.Db("master");
        var meta = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = "DataCollectorMeta", UserID = Login, Password = Password };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Metadata"] = meta.ConnectionString,
            [$"SqlCredentialOverrides:{CollectorFixture.SourceServerName}"] = $"User ID={Login};Password={Password}",
            [$"SqlCredentialOverrides:{CollectorFixture.TargetServerName}"] = $"User ID={Login};Password={Password}",
            ["Collector:Roles"] = roles,
            ["Collector:AutoCreateSchema"] = "false",
            ["Collector:HangfirePrepareSchema"] = "false",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSqlDataCollectorCore(config);
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<PermissionChecker>());
    }

    private static PermissionCheckRow Find(IReadOnlyList<PermissionCheckRow> rows, string category, string? database, string itemStart) =>
        Assert.Single(rows, r => r.Category == category && r.DatabaseName == database && r.Item.StartsWith(itemStart, StringComparison.Ordinal));

    [Fact]
    public async Task LowPrivilegeWorker_ReportsMissingTargetRights_AndExcessSourceRights()
    {
        await fx.SaveAsync(fx.NewExtractJob("T_PermCheck"));
        var (sp, checker) = await LowPrivilegeCheckerAsync("Worker");
        await using var _ = sp;

        var rows = await checker.CheckAsync(checker.WorkerContext(CollectorQueues.Extract), CancellationToken.None);

        var meta = Find(rows, CheckCategory.Metadata, "DataCollectorMeta", "Read/write etl tables");
        Assert.Equal(CheckStatus.Ok, meta.Status);
        Assert.Equal(Login, meta.LoginName);
        Assert.DoesNotContain(rows, r => r.Category == CheckCategory.Metadata && r.Item.StartsWith("Install"));   // AutoCreateSchema=false

        var load = Find(rows, CheckCategory.Target, "DWH", "Load and clean up data");
        Assert.Equal(CheckStatus.Missing, load.Status);
        Assert.Contains("INSERT", load.Detail);
        Assert.DoesNotContain("SELECT", load.Detail);                                                        // db_datareader covers SELECT
        Assert.Equal(CheckStatus.Missing, Find(rows, CheckCategory.Target, "DWH", "Create and alter target tables").Status);

        // other tests may add stored procedure jobs (EXECUTE is not granted to sdc_check): only the table itself must be fine
        Assert.DoesNotContain("[dbo].[Orders]", Find(rows, CheckCategory.Source, "SrcDb", "Read source tables").Detail ?? "");
        var excess = Find(rows, CheckCategory.Source, "SrcDb", "No write or DDL permissions");
        Assert.Equal(CheckStatus.Excess, excess.Status);
        Assert.Contains("INSERT", excess.Detail);
        Assert.DoesNotContain("sysadmin", excess.Detail);
    }

    [Fact]
    public async Task LowPrivilegeWebOnly_HasNoWriteOnTarget_ButMissesViewDefinition()
    {
        await fx.SaveAsync(fx.NewExtractJob("T_PermCheckWeb"));
        var (sp, checker) = await LowPrivilegeCheckerAsync("Web");
        await using var _ = sp;

        var rows = await checker.CheckAsync(checker.WebContext(), CancellationToken.None);

        Assert.Equal(CheckStatus.Ok, Find(rows, CheckCategory.Target, "DWH", "No write access (Web role)").Status);
        var read = Find(rows, CheckCategory.Target, "DWH", "Read for the UI");
        Assert.Equal(CheckStatus.Missing, read.Status);
        Assert.Contains("VIEW DEFINITION", read.Detail);
        Assert.DoesNotContain(rows, r => r.Item == "Load and clean up data");
    }

    [Fact]
    public async Task SysadminWorker_ReportsExcessOnSources_AndSaveKeepsOnlyTheLatestResult()
    {
        await fx.SaveAsync(fx.NewExtractJob("T_PermCheckSa"));
        var checker = fx.Get<PermissionChecker>();
        var ctx = checker.WorkerContext(CollectorQueues.Extract);

        await checker.CheckAndSaveAsync(ctx, CancellationToken.None);
        var rows = await checker.CheckAndSaveAsync(ctx, CancellationToken.None);

        var excess = Find(rows, CheckCategory.Source, "SrcDb", "No write or DDL permissions");
        Assert.Equal(CheckStatus.Excess, excess.Status);
        Assert.Contains("sysadmin", excess.Detail);
        Assert.Equal(CheckStatus.Ok, Find(rows, CheckCategory.Target, "DWH", "Load and clean up data").Status);
        Assert.Equal(CheckStatus.Ok, Find(rows, CheckCategory.Metadata, "DataCollectorMeta", "Install/upgrade the etl schema").Status);
        // no Hangfire in the test fixture: the schema check reports it
        Assert.Equal(CheckStatus.Missing, Assert.Single(rows, r => r.Item == "Hangfire schema installed").Status);

        var saved = (await fx.Repo.GetPermissionChecksAsync())
            .Where(r => r.ProcessRole == CheckContext.Worker && r.Queue == CollectorQueues.Extract && r.HostName == Environment.MachineName).ToList();
        Assert.Equal(rows.Count, saved.Count);
    }
}
