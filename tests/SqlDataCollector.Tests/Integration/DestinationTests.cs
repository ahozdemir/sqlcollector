using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Azure.Storage.Blobs;
using Dapper;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;
using SqlDataCollector.Storage;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Exports to S3-compatible storage (adobe/s3mock) and Azure Blob Storage (Azurite), destination test, inactive destinations, upgrade of legacy GCS jobs.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DestinationTests(CollectorFixture fx) : IAsyncLifetime
{
    private const string Dwh = "DWH";
    // s3mock accepts any credentials; the access key path is still exercised (signing, headers)
    private const string MinioUser = "s3-test", MinioPassword = "s3-test-secret";
    // the public, documented development account of the Azure Storage emulator (not a secret)
    private const string AzuriteKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private IContainer _minio = null!, _azurite = null!;
    private string MinioUrl => $"http://localhost:{_minio.GetMappedPublicPort(9090)}";
    private string AzuriteBlobEndpoint => $"http://127.0.0.1:{_azurite.GetMappedPublicPort(10000)}/devstoreaccount1";
    private string AzuriteConnectionString =>
        $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey={AzuriteKey};BlobEndpoint={AzuriteBlobEndpoint};";

    public async Task InitializeAsync()
    {
        _minio = new ContainerBuilder("adobe/s3mock:3.12.0")
            .WithPortBinding(9090, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/")))
            .Build();
        _azurite = new ContainerBuilder("mcr.microsoft.com/azure-storage/azurite:3.34.0")
            .WithCommand("azurite-blob", "--blobHost", "0.0.0.0", "--skipApiVersionCheck", "--loose")
            .WithPortBinding(10000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("successfully listens"))
            .Build();
        await Task.WhenAll(_minio.StartAsync(), _azurite.StartAsync());

        using var s3 = MinioClient();
        await s3.PutBucketAsync("dwh-exports");
        await new BlobContainerClient(AzuriteConnectionString, "exports").CreateIfNotExistsAsync();

        await fx.ExecAsync(Dwh, """
            IF SCHEMA_ID('dst') IS NULL EXEC('CREATE SCHEMA dst');
            DROP TABLE IF EXISTS dst.Items;
            CREATE TABLE dst.Items(Id int NOT NULL, Name nvarchar(50));
            INSERT dst.Items VALUES (1, N'Ayşe'), (2, N'Bora'), (3, N'Cem');
            """);
    }

    public async Task DisposeAsync() => await Task.WhenAll(_minio.DisposeAsync().AsTask(), _azurite.DisposeAsync().AsTask());

    private AmazonS3Client MinioClient() => new(new BasicAWSCredentials(MinioUser, MinioPassword),
        new AmazonS3Config { ServiceURL = MinioUrl, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });

    private async Task<int> DestinationAsync(DestinationDef d, string? secret) =>
        await fx.Repo.SaveDestinationAsync(d, secret is null ? null : fx.Get<SecretProtector>().Protect(secret), "tests");

    private async Task<ExportJobDef> ExportAsync(int destinationId, string prefix, ExportFileFormat format = ExportFileFormat.Csv)
    {
        var e = new ExportJobDef
        {
            Name = "dst_" + Guid.NewGuid().ToString("N")[..6], ExportMode = ExportMode.Full, FileFormat = format,
            SourceServerId = fx.TargetServerId, SourceDatabase = Dwh, SourceSchema = "dst", SourceTable = "Items",
            DestinationId = destinationId, Bucket = "ignored", ObjectNameTemplate = prefix + "/{Name}_{Part}.csv",
            CronExpression = "0 1 * * *", TimeZoneId = "UTC", UploadWhenEmpty = false, WriteManifest = true, CommandTimeoutSec = 300
        };
        await fx.Repo.SaveExportJobAsync(e, "tests");
        return e;
    }

    private Task<(string Status, string? Objects, string? Error)> LastRunAsync(ExportJobDef e) => Query<(string, string?, string?)>(
        "SELECT TOP 1 Status, ObjectNames, ErrorMessage FROM etl.ExportRun WHERE ExportJobId = @id ORDER BY ExportRunId DESC", new { id = e.ExportJobId });

    private async Task<T> Query<T>(string sql, object args)
    {
        await using var c = fx.Db("DataCollectorMeta");
        return await c.QuerySingleAsync<T>(sql, args);
    }

    [Fact]
    public async Task Export_ToS3Compatible_UploadsFilesAndManifest_AndTestWritesAnObject()
    {
        var id = await DestinationAsync(new DestinationDef
        {
            Name = "minio_" + Guid.NewGuid().ToString("N")[..6], Provider = StorageProviders.S3, Bucket = "dwh-exports",
            Endpoint = MinioUrl, Region = "us-east-1", ForcePathStyle = true, AuthMethod = StorageAuthMethods.Key, AccessKeyId = MinioUser
        }, MinioPassword);

        Assert.StartsWith("Test object written: s3://dwh-exports/_sdc_connection_test/", await fx.Get<ObjectStoreFactory>().TestAsync(id, CancellationToken.None));

        var e = await ExportAsync(id, "s3-test");
        await fx.RunExportAsync(e);
        var (status, objects, _) = await LastRunAsync(e);
        Assert.Equal("Succeeded", status);
        Assert.StartsWith($"s3://dwh-exports/s3-test/{e.Name}_0001.csv", objects);

        using var s3 = MinioClient();
        var list = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "dwh-exports", Prefix = "s3-test/" });
        var file = Assert.Single(list.S3Objects, o => o.Key == $"s3-test/{e.Name}_0001.csv");
        using var body = await s3.GetObjectAsync("dwh-exports", file.Key);
        using var reader = new StreamReader(body.ResponseStream);
        Assert.Equal("Id,Name\n1,Ayşe\n2,Bora\n3,Cem\n", await reader.ReadToEndAsync());
        Assert.Single(list.S3Objects, o => o.Key.StartsWith("s3-test/_manifests/"));
    }

    [Fact]
    public async Task Export_ToAzureBlob_WithConnectionString_AndAccountKey()
    {
        var byConnection = await DestinationAsync(new DestinationDef
        {
            Name = "azurite_cs_" + Guid.NewGuid().ToString("N")[..6], Provider = StorageProviders.AzureBlob, Bucket = "exports",
            AuthMethod = StorageAuthMethods.ConnectionString
        }, AzuriteConnectionString);
        var e = await ExportAsync(byConnection, "az-test", ExportFileFormat.Parquet);
        await fx.RunExportAsync(e);
        var (status, objects, _) = await LastRunAsync(e);
        Assert.Equal("Succeeded", status);
        Assert.StartsWith($"{AzuriteBlobEndpoint}/exports/az-test/{e.Name}_0001.parquet", objects);
        var blob = new BlobContainerClient(AzuriteConnectionString, "exports").GetBlobClient($"az-test/{e.Name}_0001.parquet");
        Assert.True((await blob.GetPropertiesAsync()).Value.ContentLength > 0);

        // account key + account URL (the account name comes from the URL)
        var byKey = await DestinationAsync(new DestinationDef
        {
            Name = "azurite_key_" + Guid.NewGuid().ToString("N")[..6], Provider = StorageProviders.AzureBlob, Bucket = "exports",
            Endpoint = AzuriteBlobEndpoint, AuthMethod = StorageAuthMethods.Key
        }, AzuriteKey);
        Assert.StartsWith($"Test object written: {AzuriteBlobEndpoint}/exports/_sdc_connection_test/", await fx.Get<ObjectStoreFactory>().TestAsync(byKey, CancellationToken.None));
    }

    [Fact]
    public async Task MissingBucket_FailsTheTest_AndAnInactiveDestinationFailsTheRun()
    {
        var wrong = await DestinationAsync(new DestinationDef
        {
            Name = "s3_missing_" + Guid.NewGuid().ToString("N")[..6], Provider = StorageProviders.S3, Bucket = "no-such-bucket",
            Endpoint = MinioUrl, Region = "us-east-1", ForcePathStyle = true, AuthMethod = StorageAuthMethods.Key, AccessKeyId = MinioUser
        }, MinioPassword);
        await Assert.ThrowsAnyAsync<Exception>(() => fx.Get<ObjectStoreFactory>().TestAsync(wrong, CancellationToken.None));

        var d = (await fx.Repo.GetDestinationAsync(wrong))!;
        d.IsActive = false;
        await fx.Repo.SaveDestinationAsync(d, null, "tests");
        var e = await ExportAsync(wrong, "inactive");
        await Assert.ThrowsAnyAsync<Exception>(() => fx.RunExportAsync(e));
        var (status, _, error) = await LastRunAsync(e);
        Assert.Equal("Failed", status);
        Assert.Contains("is inactive", error);
    }

    [Fact]
    public async Task SchemaUpgrade_GivesLegacyGcsJobsADestination_AndTheyKeepExporting()
    {
        var legacy = new ExportJobDef
        {
            Name = "legacy_" + Guid.NewGuid().ToString("N")[..6], ExportMode = ExportMode.Full,
            SourceServerId = fx.TargetServerId, SourceDatabase = Dwh, SourceSchema = "dst", SourceTable = "Items",
            Bucket = CollectorFixture.Bucket, ObjectNameTemplate = "legacy/{Name}_{Part}.csv",
            CronExpression = "0 1 * * *", TimeZoneId = "UTC", UploadWhenEmpty = false, WriteManifest = false, CommandTimeoutSec = 300
        };
        await fx.Repo.SaveExportJobAsync(legacy, "tests");
        Assert.Null((await fx.Repo.GetExportJobAsync(legacy.ExportJobId))!.DestinationId);

        await SchemaInstaller.InstallAsync(fx.Get<SqlConnectionFactory>(), fx.Get<Microsoft.Extensions.Logging.ILogger<DestinationTests>>(), CancellationToken.None);

        var upgraded = (await fx.Repo.GetExportJobAsync(legacy.ExportJobId))!;
        Assert.NotNull(upgraded.DestinationId);
        Assert.Equal(StorageProviders.Gcs, upgraded.DestinationProvider);
        Assert.Equal($"gcs-{CollectorFixture.Bucket}", upgraded.DestinationName);

        await fx.RunExportAsync(upgraded);
        var (status, objects, _) = await LastRunAsync(upgraded);
        Assert.Equal("Succeeded", status);
        Assert.StartsWith($"gs://{CollectorFixture.Bucket}/legacy/", objects);
    }
}
