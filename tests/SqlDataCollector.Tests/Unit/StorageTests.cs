using SqlDataCollector.Models;
using SqlDataCollector.Storage;

namespace SqlDataCollector.Tests.Unit;

public class StorageTests
{
    [Theory]
    [InlineData("https://myaccount.blob.core.windows.net", "myaccount")]
    [InlineData("https://myaccount.blob.core.windows.net/", "myaccount")]
    [InlineData("http://127.0.0.1:10000/devstoreaccount1", "devstoreaccount1")]
    [InlineData("http://localhost:10000/devstoreaccount1", "devstoreaccount1")]
    public void AzureAccount_FromEndpoint(string endpoint, string expected) => Assert.Equal(expected, StorageNames.AzureAccount(endpoint));

    [Fact]
    public void Addresses_AndEndpoints()
    {
        Assert.Equal("s3://b/exports/a.csv", StorageNames.S3Address("b", "exports/a.csv"));
        Assert.Equal("gs://b/exports/a.csv", StorageNames.GcsAddress("b", "exports/a.csv"));
        Assert.Equal("https://acct.blob.core.windows.net/exports", StorageNames.AzureContainerUri("https://acct.blob.core.windows.net/", "exports").ToString());

        Assert.Equal("storage.googleapis.com", StorageNames.Endpoint(new DestinationDef { Provider = StorageProviders.Gcs }, null)!.Host);
        Assert.Equal("s3.eu-central-1.amazonaws.com", StorageNames.Endpoint(new DestinationDef { Provider = StorageProviders.S3, Region = "eu-central-1" }, null)!.Host);
        Assert.Equal(9000, StorageNames.Endpoint(new DestinationDef { Provider = StorageProviders.S3, Endpoint = "http://minio:9000" }, null)!.Port);
        Assert.Null(StorageNames.Endpoint(new DestinationDef { Provider = StorageProviders.AzureBlob, AuthMethod = StorageAuthMethods.ConnectionString }, null));
    }

    [Fact]
    public void Validate_RequiresWhatEachProviderNeeds()
    {
        DestinationDef D(string provider, string auth, Action<DestinationDef>? f = null)
        {
            var d = new DestinationDef { Name = "x", Provider = provider, Bucket = "b", AuthMethod = auth };
            f?.Invoke(d);
            return d;
        }
        Assert.Null(StorageNames.Validate(D(StorageProviders.Gcs, StorageAuthMethods.Default), hasSecret: false));
        Assert.NotNull(StorageNames.Validate(D(StorageProviders.Gcs, StorageAuthMethods.Key), hasSecret: false));
        Assert.NotNull(StorageNames.Validate(D(StorageProviders.S3, StorageAuthMethods.Key, d => d.Region = "eu-central-1"), hasSecret: true));   // no access key id
        Assert.NotNull(StorageNames.Validate(D(StorageProviders.S3, StorageAuthMethods.Default), hasSecret: false));                          // no region / endpoint
        Assert.Null(StorageNames.Validate(D(StorageProviders.S3, StorageAuthMethods.Key, d => { d.Endpoint = "http://minio:9000"; d.AccessKeyId = "k"; }), hasSecret: true));
        Assert.NotNull(StorageNames.Validate(D(StorageProviders.AzureBlob, StorageAuthMethods.Default), hasSecret: false));                   // no account URL
        Assert.Null(StorageNames.Validate(D(StorageProviders.AzureBlob, StorageAuthMethods.ConnectionString), hasSecret: true));
        Assert.NotNull(StorageNames.Validate(D(StorageProviders.AzureBlob, StorageAuthMethods.Sas, d => d.Endpoint = "https://a.blob.core.windows.net"), hasSecret: false));
        Assert.NotNull(StorageNames.Validate(D(StorageProviders.S3, StorageAuthMethods.Key, d => { d.Endpoint = "not a url"; d.AccessKeyId = "k"; }), hasSecret: true));
    }
}
