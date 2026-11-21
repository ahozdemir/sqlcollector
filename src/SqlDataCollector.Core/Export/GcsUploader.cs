using System.Net;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Options;
using SqlDataCollector.Options;

namespace SqlDataCollector.Export;

/// <summary>Resumable upload to Google Cloud Storage with retries and size verification.</summary>
public sealed class GcsUploader(IOptions<GcsOptions> options, ILogger<GcsUploader> logger) : IDisposable
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private StorageClient? _client;

    private async Task<StorageClient> GetClientAsync(CancellationToken ct)
    {
        if (_client is not null) return _client;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_client is not null) return _client;
            var o = options.Value;
            var builder = new StorageClientBuilder();
            if (!string.IsNullOrWhiteSpace(o.Endpoint))
                builder.BaseUri = o.Endpoint.TrimEnd('/') + "/storage/v1/";

            if (o.Anonymous)
                builder.UnauthenticatedAccess = true;
            else if (!string.IsNullOrWhiteSpace(o.CredentialsPath))
                builder.GoogleCredential = (await CredentialFactory.FromFileAsync<ServiceAccountCredential>(o.CredentialsPath, ct)).ToGoogleCredential();
            else
                builder.GoogleCredential = await GoogleCredential.GetApplicationDefaultAsync(ct);

            _client = await builder.BuildAsync(ct);
            return _client;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<long> UploadFileAsync(string bucket, string objectName, string localPath, string contentType, CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        var expected = new FileInfo(localPath).Length;
        var uploadOptions = new UploadObjectOptions { ChunkSize = Math.Max(1, options.Value.UploadChunkSizeMb) * 1024 * 1024 };
        int maxRetries = Math.Max(1, options.Value.MaxRetries);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var fs = File.OpenRead(localPath);
                var obj = await client.UploadObjectAsync(bucket, objectName, contentType, fs, uploadOptions, ct);
                if ((long)(obj.Size ?? 0) != expected)
                    throw new InvalidOperationException($"GCS size mismatch: {objectName} local={expected} gcs={obj.Size}");
                logger.LogInformation("GCS upload: gs://{Bucket}/{Object} ({Bytes:N0} bytes)", bucket, objectName, expected);
                return expected;
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5);
                logger.LogWarning(ex, "GCS upload attempt {Attempt}/{Max} failed ({Object}); retrying in {Delay}.",
                    attempt, maxRetries, objectName, delay);
                await Task.Delay(delay, ct);
            }
        }
    }

    public async Task UploadTextAsync(string bucket, string objectName, string content, string contentType, CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        await client.UploadObjectAsync(bucket, objectName, contentType, ms, cancellationToken: ct);
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        GoogleApiException g => g.HttpStatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                                || (int)g.HttpStatusCode >= 500,
        HttpRequestException or IOException or TaskCanceledException or InvalidOperationException => true,
        _ => false
    };

    public void Dispose()
    {
        _client?.Dispose();
        _initLock.Dispose();
    }
}
