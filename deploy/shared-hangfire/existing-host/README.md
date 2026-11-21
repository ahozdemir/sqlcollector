# Running SqlDataCollector jobs in an existing (shared) Hangfire Server host

Use this when the organization already operates a Hangfire Server process that must execute the jobs
(deployment mode **2b**). A runnable example is `samples/ExistingHangfireHost`; Docker profile `shared`
runs it as `corporate-host`.

## 1. Reference the job library

Add `SqlDataCollector.Core` (project reference, or the DLLs from `dotnet publish src/SqlDataCollector.Core`)
to the host. The host must run on .NET 10 and use Hangfire 1.8.x.

## 2. Register services and queues

```csharp
builder.Services.AddHangfire(cfg => cfg
    /* ... the host's existing storage configuration ... */
    .UseSqlServerStorage(() => new SqlConnection(hangfireCs), new SqlServerStorageOptions
    {
        SchemaName = "hangfire_collector",      // the schema the SqlDataCollector Web writes to
        PrepareSchemaIfNecessary = false
    }));

builder.Services.AddSqlDataCollectorCore(builder.Configuration);   // job runners + services
builder.Services.AddSqlDataCollectorStartup();                     // optional: schema check, system jobs, first sync

builder.Services.AddHangfireServer(o =>
{
    o.Queues = ["default", /* host's own queues */ CollectorQueues.Extract, CollectorQueues.System];
    // add CollectorQueues.Export too, unless exports run on a Worker on the target SQL Server host
});
```

If the host already serves another Hangfire storage/schema, add a **second** `AddHangfireServer`
bound to the `hangfire_collector` storage (`JobStorage` instance), because one Hangfire server reads one storage.

## 3. Configuration the host needs

```json
{
  "ConnectionStrings": { "Metadata": "Server=DWH-SQL01;Database=DataCollectorMeta;Integrated Security=true;Encrypt=true" },
  "Collector": { "Roles": "Worker", "HangfireSchema": "hangfire_collector", "WorkDirectory": "D:\\SqlDataCollector\\work" },
  "Gcs": { "CredentialsPath": "D:\\SqlDataCollector\\secrets\\gcs-sa.json" }
}
```

## 4. Permissions for the host's service account

Same as a SqlDataCollector Worker: read on sources, write + DDL on the target database, read/write on
`DataCollectorMeta`, and SELECT/INSERT/UPDATE/DELETE on the `hangfire_collector` schema
(`SqlDataCollector.exe hangfire-install-script hangfire_collector "<host account>"`).

## 5. Upgrades

Deploy a new `SqlDataCollector.Core` to the host whenever the SqlDataCollector Web is upgraded
(job method signatures are part of the Hangfire contract).
