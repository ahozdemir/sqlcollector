using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;

namespace SqlDataCollector.Tests.Unit;

/// <summary>Source/target connection pools grow with the worker count, so parallel readers/writers never run out of connections.</summary>
public sealed class ConnectionPoolTests
{
    private static SqlConnectionFactory Factory(string? workerCount) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Metadata"] = "Server=meta;Database=m;Integrated Security=true",
            ["Collector:WorkerCount"] = workerCount
        }).Build());

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 100)]
    [InlineData(4, 100)]     // default: 4 × 18 = 72 → the .NET default stays
    [InlineData(8, 144)]
    [InlineData(12, 216)]
    [InlineData(16, 288)]
    public void PoolSize_CoversEveryWorkerWithTheMostParallelConnections(int workers, int expected) =>
        Assert.Equal(expected, SqlConnectionFactory.PoolSizeFor(workers));

    [Fact]
    public void PoolSize_LeavesRoomForTheLargestJobOnEveryWorker()
    {
        Assert.True(SqlConnectionFactory.ConnectionsPerJob > ExtractJobRules.MaxReadParallelism);
        Assert.True(SqlConnectionFactory.ConnectionsPerJob > ExtractJobRules.MaxWriteParallelism);
        for (var w = 1; w <= 64; w++)
            Assert.True(SqlConnectionFactory.PoolSizeFor(w) >= w * (ExtractJobRules.MaxReadParallelism + 1));
    }

    [Theory]
    [InlineData("16", 288)]
    [InlineData(null, 100)]   // not configured: CollectorOptions default (4)
    public void ServerConnectionStrings_CarryThePoolSize(string? workerCount, int expected)
    {
        var cs = Factory(workerCount).BuildConnectionString(new SqlServerDef { Name = "DWH", ServerAddress = "dwh01" }, "SdcDwh", forWrite: true);
        Assert.Equal(expected, new SqlConnectionStringBuilder(cs).MaxPoolSize);
    }
}
