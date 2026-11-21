using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using SqlDataCollector;
using SqlDataCollector.Infrastructure;

// ---------------------------------------------------------------------------------------------
// Example: the organization's existing, shared Hangfire Server host.
// Lines marked [SDC] are the only additions needed to run SqlDataCollector jobs in it.
// ---------------------------------------------------------------------------------------------
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "CorporateHangfireHost");

// The host's own Hangfire storage (central Hangfire DB). SqlDataCollector jobs live in their own schema.
var hangfireCs = builder.Configuration.GetConnectionString("Hangfire")
                 ?? throw new InvalidOperationException("ConnectionStrings:Hangfire is not configured.");
var schema = builder.Configuration["Collector:HangfireSchema"] ?? "hangfire_collector";

builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(() => new SqlConnection(hangfireCs), new SqlServerStorageOptions
    {
        SchemaName = schema,
        PrepareSchemaIfNecessary = false,       // installed by the DBA (SqlDataCollector hangfire-install-script)
        QueuePollInterval = TimeSpan.Zero,
        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
        UseRecommendedIsolationLevel = true,
        DisableGlobalLocks = true
    }));

builder.Services.AddSqlDataCollectorCore(builder.Configuration);   // [SDC] job runners + services
builder.Services.AddSqlDataCollectorStartup();                     // [SDC] optional: schema check, system jobs, first sync

// If the host serves several storages/schemas it needs one server per storage; here: one.
builder.Services.AddHangfireServer(o =>
{
    o.ServerName = $"{Environment.MachineName}:corporate-host";
    o.WorkerCount = 4;
    var own = (builder.Configuration["Host:OwnQueues"] ?? "default").Split(',', StringSplitOptions.RemoveEmptyEntries);
    var sdc = (builder.Configuration["Collector:Queues"] ?? string.Join(',', CollectorQueues.All))
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    o.Queues = own.Concat(sdc).Distinct().ToArray();             // [SDC] listen to the sdc_* queues
});

await builder.Build().RunAsync();
