using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SqlDataCollector.Export;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Options;
using SqlDataCollector.Security;
using SqlDataCollector.Tests.Unit;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Installer database setup, the startup probe, the first local admin password and the uploaded GCS key.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SetupIntegrationTests(CollectorFixture fx)
{
    private string MasterCs
    {
        get { using var c = fx.Db("master"); return c.ConnectionString; }
    }

    private static IConfiguration Config(string? metadata) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Metadata"] = metadata }).Build();

    [Fact]
    public async Task DbInstall_CreatesDatabases_EtlSchema_AndReader_Idempotently()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var o = new DbSetupOptions($"SetupMeta_{suffix}", $"SetupDwh_{suffix}", null);
        await DbSetup.InstallAsync(MasterCs, o, null, CancellationToken.None);
        await DbSetup.InstallAsync(MasterCs, o, null, CancellationToken.None);   // second run changes nothing

        Assert.Equal(1, await fx.ScalarAsync<int>(o.MetadataDb, "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID('etl.ExtractJob')"));
        Assert.Equal(1, await fx.ScalarAsync<int>(o.MetadataDb, "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID('etl.AlertChannel')"));
        Assert.Equal(1, await fx.ScalarAsync<int>(o.DwhDb, "SELECT COUNT(*) FROM sys.database_principals WHERE name = 'sdc_export_reader' AND authentication_type_desc = 'NONE'"));
        Assert.Equal(1, await fx.ScalarAsync<int>(o.DwhDb, "SELECT IS_ROLEMEMBER('db_datareader', 'sdc_export_reader')"));
    }

    [Fact]
    public async Task Probe_ReportsMissingDatabaseOrSchema_AndAcceptsAReadyOne()
    {
        var meta = new SqlConnectionStringBuilder(MasterCs) { InitialCatalog = "DataCollectorMeta" }.ConnectionString;
        Assert.Null(await SetupProbe.ProbeAsync(Config(meta), new CollectorOptions { AutoCreateSchema = false }));

        var missing = new SqlConnectionStringBuilder(MasterCs) { InitialCatalog = "NoSuchDb_" + Guid.NewGuid().ToString("N")[..6] }.ConnectionString;
        Assert.Contains("cannot be used", await SetupProbe.ProbeAsync(Config(missing), new CollectorOptions()));

        var empty = "Empty_" + Guid.NewGuid().ToString("N")[..6];
        await fx.ExecAsync("master", $"CREATE DATABASE [{empty}]");
        var emptyCs = new SqlConnectionStringBuilder(MasterCs) { InitialCatalog = empty }.ConnectionString;
        Assert.Contains("etl schema is missing", await SetupProbe.ProbeAsync(Config(emptyCs), new CollectorOptions { AutoCreateSchema = false }));
        Assert.Null(await SetupProbe.ProbeAsync(Config(emptyCs), new CollectorOptions { AutoCreateSchema = true }));   // installed at startup

        Assert.Contains("not configured", await SetupProbe.ProbeAsync(Config(null), new CollectorOptions()));
    }

    [Fact]
    public async Task LocalAdmin_FirstPassword_IsStoredHashed_AndNotAudited()
    {
        var store = new LocalAdminStore(fx.Repo, Microsoft.Extensions.Options.Options.Create(new LocalAdminOptions { Enabled = true, PasswordHash = "" }));
        try
        {
            Assert.True(await store.NeedsInitialPasswordAsync());
            await store.SetPasswordAsync("First!Password-2026", @"local\admin");
            Assert.False(await store.NeedsInitialPasswordAsync());
            Assert.True(PasswordHasher.Verify("First!Password-2026", await store.GetHashAsync()));
            Assert.False(PasswordHasher.Verify("wrong-password-123", await store.GetHashAsync()));
            Assert.Equal(0, await fx.ScalarAsync<int>("DataCollectorMeta", "SELECT COUNT(*) FROM etl.AuditLog WHERE Details LIKE '%pbkdf2%'"));
            Assert.True(await fx.ScalarAsync<int>("DataCollectorMeta", "SELECT COUNT(*) FROM etl.AuditLog WHERE Entity = 'LocalAdminPassword'") >= 1);
        }
        finally { await fx.ExecAsync("DataCollectorMeta", $"DELETE etl.SystemSetting WHERE Name = '{LocalAdminStore.HashKey}'"); }
    }

    [Fact]
    public async Task GcsKey_IsStoredEncrypted_UsedForUploads_AndRemovable()
    {
        var secrets = new SecretProtector(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [SecretProtector.ConfigKey] = SecretProtector.NewKey() }).Build());
        var store = new GcsCredentialStore(fx.Repo, secrets, Microsoft.Extensions.Options.Options.Create(new GcsOptions()));
        try
        {
            Assert.Equal(GcsCredentialSource.ApplicationDefault, (await store.DescribeAsync()).Source);
            var versionBefore = await store.GetVersionAsync();

            var info = await store.SaveKeyAsync(GcsKeyTests.FakeKeyJson(), "tests");
            var (source, _, stored) = await store.DescribeAsync();
            Assert.Equal(GcsCredentialSource.StoredKey, source);
            Assert.Equal(info.ClientEmail, stored!.ClientEmail);
            Assert.NotEqual(versionBefore, await store.GetVersionAsync());
            var raw = await fx.ScalarAsync<string>("DataCollectorMeta", "SELECT Value FROM etl.SystemSetting WHERE Name = @n", new { n = GcsCredentialStore.KeySetting });
            Assert.StartsWith("v1:", raw);
            Assert.DoesNotContain("PRIVATE KEY", raw);
            Assert.NotNull(await store.ResolveAsync(CancellationToken.None));   // the Google library accepts the decrypted key

            await store.RemoveKeyAsync("tests");
            Assert.Equal(GcsCredentialSource.ApplicationDefault, (await store.DescribeAsync()).Source);
        }
        finally { await fx.ExecAsync("DataCollectorMeta", "DELETE etl.SystemSetting WHERE Name LIKE 'Gcs.%'"); }
    }
}
