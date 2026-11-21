---
description: Verify both deployment modes in Docker (embedded = profile full, shared Hangfire = profile shared)
---
Use the `test-runner` subagent to verify both deployment modes, one after the other. SQL checks run with
`docker exec sqlcollector-target-sql-1 /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Dev_Passw0rd!2026' -C -Q "..."`.

1. Embedded: `scripts/dev.ps1 full-up`; wait until `curl -s http://localhost:8080/health` returns `"status":"ok"`;
   check `docker compose -f docker/compose.yml --profile full logs app` for "SqlDataCollector started." and no `fail:` lines;
   check that `DataCollectorMeta` has tables in schema `hangfire_collector`; after ~2 minutes (SyncCron */2) check
   `DataCollectorMeta.etl.LoadRun` for runs if any job was due. Then `scripts/dev.ps1 down`.
2. Shared: `scripts/dev.ps1 shared-up`; check that `web`, `corporate-host` and `worker-export` are running
   (`docker compose -f docker/compose.yml --profile shared ps`), that the corporate-host log lists the `sdc_extract` and
   `sdc_system` queues, worker-export lists `sdc_export`, and `HangfireDB` has tables in schema `hangfire_collector`.
   Then `scripts/dev.ps1 down`.

Report per mode: PASS/FAIL with the evidence lines. Answer me in Turkish.
