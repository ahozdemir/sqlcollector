using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Options;
using SqlDataCollector.Export;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;
using SqlDataCollector.Options;

namespace SqlDataCollector.Storage;

/// <summary>An export destination: uploads files and verifies their size afterwards.</summary>
public interface IObjectStore : IDisposable
{
    /// <summary>Address of an object for run logs and manifests (gs://, s3://, https://…blob…).</summary>
    string Describe(string objectName);
    Task<long> UploadFileAsync(string objectName, string localPath, string contentType, CancellationToken ct);
    Task UploadTextAsync(string objectName, string content, string contentType, CancellationToken ct);
}

/// <summary>Pure helpers (unit tested).</summary>
public static class StorageNames
{
    /// <summary>Account name of an Azure blob endpoint: https://acct.blob.core.windows.net → acct; Azurite http://127.0.0.1:10000/devstoreaccount1 → devstoreaccount1.</summary>
    public static string AzureAccount(string endpoint)
    {
        var uri = new Uri(endpoint);
        var path = uri.AbsolutePath.Trim('/');
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 || uri.IsLoopback && path.Length > 0)
            return path.Split('/')[0];
        return uri.Host.Split('.')[0];
    }

    /// <summary>Network endpoint of a destination (for the reachability check); null when it is only inside an Azure connection string.</summary>
    public static Uri? Endpoint(DestinationDef d, string? gcsEndpoint) => d.Provider switch
    {
        StorageProviders.Gcs => new Uri(string.IsNullOrWhiteSpace(gcsEndpoint) ? "https://storage.googleapis.com" : gcsEndpoint),
        StorageProviders.S3 => new Uri(string.IsNullOrWhiteSpace(d.Endpoint) ? $"https://s3.{d.Region?.Trim()}.amazonaws.com" : d.Endpoint.Trim()),
        _ => string.IsNullOrWhiteSpace(d.Endpoint) ? null : new Uri(d.Endpoint.Trim())
    };

    /// <summary>Container URL without credentials: {endpoint}/{container}.</summary>
    public static Uri AzureContainerUri(string endpoint, string container) => new($"{endpoint.TrimEnd('/')}/{container}");

    public static string S3Address(string bucket, string key) => $"s3://{bucket}/{key}";
    public static string GcsAddress(string bucket, string objectName) => $"gs://{bucket}/{objectName}";

    /// <summary>Why a destination cannot work, or null (checked on save).</summary>
    public static string? Validate(DestinationDef d, bool hasSecret)
    {
        if (string.IsNullOrWhiteSpace(d.Name)) return "Name is required.";
        if (string.IsNullOrWhiteSpace(d.Bucket)) return d.Provider == StorageProviders.AzureBlob ? "Container is required." : "Bucket is required.";
        if (!string.IsNullOrWhiteSpace(d.Endpoint) && !Uri.TryCreate(d.Endpoint, UriKind.Absolute, out _)) return "The endpoint must be an absolute URL (https://…).";
        switch (d.Provider)
        {
            case StorageProviders.Gcs:
                if (d.AuthMethod is not (StorageAuthMethods.Default or StorageAuthMethods.Key)) return "Google Cloud Storage uses the Settings credential or its own JSON key.";
                break;
            case StorageProviders.S3:
                if (d.AuthMethod is not (StorageAuthMethods.Default or StorageAuthMethods.Key)) return "S3 uses an access key or the server's AWS identity.";
                if (d.AuthMethod == StorageAuthMethods.Key && string.IsNullOrWhiteSpace(d.AccessKeyId)) return "Access key ID is required.";
                if (string.IsNullOrWhiteSpace(d.Region) && string.IsNullOrWhiteSpace(d.Endpoint)) return "Enter the AWS region (e.g. eu-central-1) or the endpoint of an S3-compatible service.";
                break;
            case StorageProviders.AzureBlob:
                if (d.AuthMethod != StorageAuthMethods.ConnectionString && string.IsNullOrWhiteSpace(d.Endpoint))
                    return "Enter the storage account URL, e.g. https://myaccount.blob.core.windows.net.";
                break;
            default:
                return $"Unknown provider '{d.Provider}'.";
        }
        if (d.AuthMethod is StorageAuthMethods.Key or StorageAuthMethods.ConnectionString or StorageAuthMethods.Sas && !hasSecret)
            return d.AuthMethod switch
            {
                StorageAuthMethods.ConnectionString => "Enter the connection string.",
                StorageAuthMethods.Sas => "Enter the SAS token.",
                _ => d.Provider == StorageProviders.Gcs ? "Upload the service account key (JSON)." : d.Provider == StorageProviders.S3 ? "Enter the secret access key." : "Enter the account key."
            };
        return null;
    }
}

/// <summary>Creates the store of a destination, and the legacy GCS store for export jobs without a destination.</summary>
public sealed class ObjectStoreFactory(MetadataRepository repo, SecretProtector secrets, GcsUploader gcs, IOptions<GcsOptions> gcsOptions, ILoggerFactory loggers)
{
    public async Task<IObjectStore> ForExportAsync(ExportJobDef job)
    {
        if (job.DestinationId is not int id) return new GcsObjectStore(gcs, job.Bucket, null, gcsOptions.Value, loggers.CreateLogger<GcsObjectStore>());
        var d = await repo.GetDestinationAsync(id, includeSecret: true) ?? throw new InvalidOperationException($"Export destination {id} does not exist.");
        if (!d.IsActive) throw new InvalidOperationException($"Export destination '{d.Name}' is inactive.");
        return Create(d);
    }

    public IObjectStore Create(DestinationDef d)
    {
        string? secret = string.IsNullOrEmpty(d.Secret) ? null : secrets.Unprotect(d.Secret);
        return d.Provider switch
        {
            StorageProviders.Gcs => new GcsObjectStore(gcs, d.Bucket, d.AuthMethod == StorageAuthMethods.Key ? secret : null, gcsOptions.Value, loggers.CreateLogger<GcsObjectStore>()),
            StorageProviders.S3 => new S3ObjectStore(d, secret, loggers.CreateLogger<S3ObjectStore>()),
            StorageProviders.AzureBlob => new AzureBlobObjectStore(d, secret, loggers.CreateLogger<AzureBlobObjectStore>()),
            _ => throw new InvalidOperationException($"Unknown storage provider '{d.Provider}'.")
        };
    }

    /// <summary>"Test": writes and verifies _sdc_connection_test/&lt;machine&gt;.txt (needs only the right to create objects).</summary>
    public async Task<string> TestAsync(int destinationId, CancellationToken ct)
    {
        var d = await repo.GetDestinationAsync(destinationId, includeSecret: true) ?? throw new InvalidOperationException("Destination not found.");
        using var store = Create(d);
        var name = $"_sdc_connection_test/{Environment.MachineName}.txt";
        await store.UploadTextAsync(name, $"SqlHarbor connection test {DateTime.UtcNow:O}", "text/plain", ct);
        return $"Test object written: {store.Describe(name)}";
    }
}

/// <summary>Google Cloud Storage: the shared client (Settings credential) or a client with the destination's own JSON key.</summary>
public sealed class GcsObjectStore : IObjectStore
{
    private readonly GcsUploader _shared;
    private readonly string _bucket;
    private readonly StorageClient? _own;
    private readonly ILogger _logger;

    public GcsObjectStore(GcsUploader shared, string bucket, string? jsonKey, GcsOptions options, ILogger logger)
    {
        _shared = shared; _bucket = bucket; _logger = logger;
        if (jsonKey is null) return;
        var builder = new StorageClientBuilder { GoogleCredential = CredentialFactory.FromJson<ServiceAccountCredential>(jsonKey).ToGoogleCredential() };
        if (!string.IsNullOrWhiteSpace(options.Endpoint)) builder.BaseUri = options.Endpoint.TrimEnd('/') + "/storage/v1/";
        _own = builder.Build();
    }

    public string Describe(string objectName) => StorageNames.GcsAddress(_bucket, objectName);

    public async Task<long> UploadFileAsync(string objectName, string localPath, string contentType, CancellationToken ct)
    {
        if (_own is null) return await _shared.UploadFileAsync(_bucket, objectName, localPath, contentType, ct);
        var expected = new FileInfo(localPath).Length;
        await using var fs = File.OpenRead(localPath);
        var obj = await _own.UploadObjectAsync(_bucket, objectName, contentType, fs, cancellationToken: ct);
        if ((long)(obj.Size ?? 0) != expected) throw new InvalidOperationException($"GCS size mismatch: {objectName} local={expected} gcs={obj.Size}");
        _logger.LogInformation("GCS upload: {Address} ({Bytes:N0} bytes)", Describe(objectName), expected);
        return expected;
    }

    public async Task UploadTextAsync(string objectName, string content, string contentType, CancellationToken ct)
    {
        if (_own is null) { await _shared.UploadTextAsync(_bucket, objectName, content, contentType, ct); return; }
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        await _own.UploadObjectAsync(_bucket, objectName, contentType, ms, cancellationToken: ct);
    }

    public void Dispose() => _own?.Dispose();
}

/// <summary>Amazon S3 and S3-compatible services (MinIO, Wasabi, Ceph, …): multipart upload for large files, size check afterwards.</summary>
public sealed class S3ObjectStore : IObjectStore
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly ILogger _logger;

    public S3ObjectStore(DestinationDef d, string? secret, ILogger logger)
    {
        _bucket = d.Bucket; _logger = logger;
        var config = new AmazonS3Config
        {
            ForcePathStyle = d.ForcePathStyle,
            // S3-compatible services do not all support the newer default checksums
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            MaxErrorRetry = 4
        };
        if (!string.IsNullOrWhiteSpace(d.Endpoint))
        {
            config.ServiceURL = d.Endpoint.Trim();
            if (!string.IsNullOrWhiteSpace(d.Region)) config.AuthenticationRegion = d.Region.Trim();
        }
        else config.RegionEndpoint = RegionEndpoint.GetBySystemName(d.Region!.Trim());

        _client = d.AuthMethod == StorageAuthMethods.Key
            ? new AmazonS3Client(new BasicAWSCredentials(d.AccessKeyId, secret), config)
            : new AmazonS3Client(config);   // AWS default chain: environment, profile, EC2/ECS role
    }

    public string Describe(string objectName) => StorageNames.S3Address(_bucket, objectName);

    public async Task<long> UploadFileAsync(string objectName, string localPath, string contentType, CancellationToken ct)
    {
        var expected = new FileInfo(localPath).Length;
        using var transfer = new TransferUtility(_client);
        await transfer.UploadAsync(new TransferUtilityUploadRequest
        {
            BucketName = _bucket, Key = objectName, FilePath = localPath, ContentType = contentType
        }, ct);
        var meta = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = objectName }, ct);
        if (meta.ContentLength != expected) throw new InvalidOperationException($"S3 size mismatch: {objectName} local={expected} s3={meta.ContentLength}");
        _logger.LogInformation("S3 upload: {Address} ({Bytes:N0} bytes)", Describe(objectName), expected);
        return expected;
    }

    public async Task UploadTextAsync(string objectName, string content, string contentType, CancellationToken ct) =>
        await _client.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = objectName, ContentBody = content, ContentType = contentType }, ct);

    public void Dispose() => _client.Dispose();
}

/// <summary>Azure Blob Storage (also ADLS Gen2 accounts): connection string, SAS token, account key or Managed Identity.</summary>
public sealed class AzureBlobObjectStore : IObjectStore
{
    private readonly BlobContainerClient _container;
    private readonly string _display;
    private readonly ILogger _logger;

    public AzureBlobObjectStore(DestinationDef d, string? secret, ILogger logger)
    {
        _logger = logger;
        _container = d.AuthMethod switch
        {
            StorageAuthMethods.ConnectionString => new BlobContainerClient(secret, d.Bucket),
            StorageAuthMethods.Sas => new BlobContainerClient(new Uri($"{StorageNames.AzureContainerUri(d.Endpoint!, d.Bucket)}?{secret!.TrimStart('?')}")),
            StorageAuthMethods.Key => new BlobContainerClient(StorageNames.AzureContainerUri(d.Endpoint!, d.Bucket),
                new StorageSharedKeyCredential(StorageNames.AzureAccount(d.Endpoint!), secret)),
            _ => new BlobContainerClient(StorageNames.AzureContainerUri(d.Endpoint!, d.Bucket), new DefaultAzureCredential())   // Managed Identity, az login, …
        };
        // never show a SAS token in run logs
        _display = _container.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public string Describe(string objectName) => $"{_display}/{objectName}";

    public async Task<long> UploadFileAsync(string objectName, string localPath, string contentType, CancellationToken ct)
    {
        var expected = new FileInfo(localPath).Length;
        var blob = _container.GetBlobClient(objectName);
        await blob.UploadAsync(localPath, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);
        var props = await blob.GetPropertiesAsync(cancellationToken: ct);
        if (props.Value.ContentLength != expected) throw new InvalidOperationException($"Azure size mismatch: {objectName} local={expected} azure={props.Value.ContentLength}");
        _logger.LogInformation("Azure upload: {Address} ({Bytes:N0} bytes)", Describe(objectName), expected);
        return expected;
    }

    public async Task UploadTextAsync(string objectName, string content, string contentType, CancellationToken ct) =>
        await _container.GetBlobClient(objectName).UploadAsync(BinaryData.FromString(content),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);

    public void Dispose() { }
}
