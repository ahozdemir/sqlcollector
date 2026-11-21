# Deployment modes

| Mode | Where the UI runs | Where jobs are stored | Who executes jobs | Config samples |
|---|---|---|---|---|
| **1. Embedded** | App server (`Roles=Web,Worker`) | Metadata DB (`hangfire_collector` schema, created by the app) | The same process | `embedded/` |
| **2a. Shared Hangfire + our Worker** | App server (`Roles=Web`) | Central Hangfire DB (own schema, installed by the DBA) | SqlDataCollector Worker service(s) on the Hangfire server (`Queues=sdc_extract,sdc_system`) | `shared-hangfire/web`, `shared-hangfire/worker` |
| **2b. Shared Hangfire + existing host** | App server (`Roles=Web`) | Central Hangfire DB | The organization's existing Hangfire Server host referencing `SqlDataCollector.Core` | `shared-hangfire/web`, `shared-hangfire/existing-host` |

In both shared variants an optional export Worker can run on the target SQL Server host
(`shared-hangfire/worker-export-dbhost`, `Queues=sdc_export`) so CSV files are produced next to the data.

Put the matching `appsettings.Production.json` next to `SqlDataCollector.exe`
(the default environment is Production). Install as a Windows Service, see the main README.

Queue names are always `sdc_extract`, `sdc_export`, `sdc_system` so a shared Hangfire storage never
hands our jobs to another application's servers. Test both modes locally:
`.\scripts\dev.ps1 full-up` (embedded) and `.\scripts\dev.ps1 shared-up` (2a + 2b).
