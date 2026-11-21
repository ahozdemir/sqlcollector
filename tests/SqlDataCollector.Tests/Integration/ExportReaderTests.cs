using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;
using SqlDataCollector.Services;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Export reads run as the read-only Collector:ExportReadAsUser (EXECUTE AS USER ... WITH COOKIE).</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ExportReaderTests(CollectorFixture fx)
{
    private const string Dwh = "DWH";

    private async Task<ExportJobDef> SaveQueryExportAsync(string sql)
    {
        var export = new ExportJobDef
        {
            Name = "q_" + Guid.NewGuid().ToString("N")[..8], ExportMode = ExportMode.Query,
            SourceServerId = fx.TargetServerId, SourceDatabase = Dwh, SourceQuery = sql,
            Bucket = CollectorFixture.Bucket, CronExpression = "0 1 * * *", TimeZoneId = "UTC",
            UploadWhenEmpty = false, WriteManifest = false, CommandTimeoutSec = 120
        };
        await fx.Repo.SaveExportJobAsync(export, "tests");
        return export;
    }

    private Task<string> LastStatusAsync(ExportJobDef export) => fx.ScalarAsync<string>("DataCollectorMeta",
        "SELECT TOP 1 Status FROM etl.ExportRun WHERE ExportJobId = @id ORDER BY ExportRunId DESC", new { id = export.ExportJobId });

    [Fact]
    public async Task QueryExport_RunsAsTheReaderUser()
    {
        var export = await SaveQueryExportAsync("SELECT USER_NAME() AS DbUser, SUSER_SNAME() AS LoginName;");
        await fx.RunExportAsync(export);

        var csv = Assert.Single(await fx.ListObjectsAsync($"exports/{export.Name}/"));
        var rows = Csv.Parse(await fx.DownloadTextAsync(csv.Name));
        Assert.Equal("sdc_export_reader", rows[1][Array.IndexOf(rows[0], "DbUser")]);
    }

    [Fact]
    public async Task QueryExport_CannotWrite_OrRevert_OnTheTarget()
    {
        var table = "ExportGuard_" + Guid.NewGuid().ToString("N")[..6];
        await fx.ExecAsync(Dwh, $"CREATE TABLE dbo.{table} (Id int); INSERT dbo.{table} VALUES (1), (2);");

        var delete = await SaveQueryExportAsync($"DELETE FROM dbo.{table}; SELECT 1 AS X;");
        var ex = await Assert.ThrowsAnyAsync<SqlException>(() => fx.RunExportAsync(delete));
        Assert.Contains("DELETE permission was denied", ex.Message);
        Assert.Equal("Failed", await LastStatusAsync(delete));

        var drop = await SaveQueryExportAsync($"DROP TABLE dbo.{table}; SELECT 1 AS X;");
        await Assert.ThrowsAnyAsync<SqlException>(() => fx.RunExportAsync(drop));

        // without the cookie the impersonation cannot be reverted to regain the Worker's rights
        var revert = await SaveQueryExportAsync($"REVERT; DELETE FROM dbo.{table}; SELECT 1 AS X;");
        await Assert.ThrowsAnyAsync<SqlException>(() => fx.RunExportAsync(revert));

        Assert.Equal(2, await fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(*) FROM dbo.{table}"));
    }

    [Fact]
    public async Task MissingReaderUser_FailsWithAClearMessage()
    {
        await using var master = fx.Db("master");
        var sa = new SqlConnectionStringBuilder(master.ConnectionString);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Metadata"] = master.ConnectionString,
            [$"SqlCredentialOverrides:{CollectorFixture.TargetServerName}"] = $"User ID={sa.UserID};Password={sa.Password}",
            ["Collector:ExportReadAsUser"] = "no_such_reader",
        }).Build();
        var factory = new SqlConnectionFactory(config);
        var server = (await fx.Repo.GetServerAsync(fx.TargetServerId))!;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.OpenExportReadAsync(server, Dwh, false, CancellationToken.None));
        Assert.Contains("no_such_reader", ex.Message);
        Assert.Contains("CREATE USER [no_such_reader] WITHOUT LOGIN", ex.Message);
    }

    [Fact]
    public async Task EmptySetting_ReadsWithTheServiceAccount()
    {
        await using var master = fx.Db("master");
        var sa = new SqlConnectionStringBuilder(master.ConnectionString);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Metadata"] = master.ConnectionString,
            [$"SqlCredentialOverrides:{CollectorFixture.TargetServerName}"] = $"User ID={sa.UserID};Password={sa.Password}",
            ["Collector:ExportReadAsUser"] = "",
        }).Build();
        var factory = new SqlConnectionFactory(config);
        await using var session = await factory.OpenExportReadAsync((await fx.Repo.GetServerAsync(fx.TargetServerId))!, Dwh, false, CancellationToken.None);
        Assert.Null(session.ReadAsUser);
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = "SELECT USER_NAME();";
        Assert.Equal("dbo", await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task PermissionCheck_VerifiesTheReaderUser()
    {
        await SaveQueryExportAsync("SELECT 1 AS X;");
        var checker = fx.Get<PermissionChecker>();

        var rows = await checker.CheckAsync(checker.WorkerContext(CollectorQueues.Export), CancellationToken.None);

        var target = rows.Where(r => r.Category == CheckCategory.Target && r.DatabaseName == Dwh).ToList();
        Assert.Equal(CheckStatus.Ok, Assert.Single(target, r => r.Item == "Export reads run as a read-only database user").Status);
        Assert.Equal(CheckStatus.Ok, Assert.Single(target, r => r.Item == "Export reader 'sdc_export_reader' can read").Status);
        var noWrite = Assert.Single(target, r => r.Item == "Export reader 'sdc_export_reader' has no write access");
        Assert.Equal(CheckStatus.Ok, noWrite.Status);
        Assert.Equal("sdc_export_reader", noWrite.LoginName);
    }
}
