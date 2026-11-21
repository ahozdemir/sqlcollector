# CLAUDE.md – SqlDataCollector

Guidance for Claude Code in this repository. Read it fully before changing code.

## Working agreement

- The owner (Ahmet) writes in **Turkish**: answer in Turkish. Everything in the product – UI, log and exception messages,
  code comments, README – is **English**. Exception: the **Help page is bilingual** – English technical reference
  (`Components/Pages/Help.razor`) and a plain-language Turkish guide (`Components/Pages/HelpTr.razor`, UI names kept in
  English) with the same topic ids; a switch on the page chooses the language. The **Guide** (`/guide`, `GuideEn.razor` /
  `GuideTr.razor`) explains tasks scenario by scenario (why, steps, tips, pitfalls) with screenshots in `wwwroot/guide`,
  regenerated from the running local app with `scripts/guide-screenshots.ps1` (crops never show user or machine names).
- He reviews output critically and expects changes applied directly, with step-by-step numbers when explaining
  results (row counts, timings, before/after). Do not claim something works without running it.
- **Delegate test execution to the `test-runner` subagent** (Sonnet 5.5, effort high – see `.claude/agents/test-runner.md`)
  to save tokens: builds, `dotnet test`, the local smoke run, reading Docker/app logs. Do the design and code changes
  yourself; give the subagent a precise instruction and act on its summary.
- Never commit, push or delete files unless asked. Never put real credentials in the repo.

## What the product does

Collects data on a schedule from several SQL Server **sources** into tables on one **target** SQL Server, keeps it for
a retention period, and every night exports what was collected as **plain CSV files to Google Cloud Storage**
(local archive on the export worker is `.csv.gz`). Hangfire schedules and executes; Blazor Server UI (English) with
AD (Negotiate) sign-in plus one local admin; roles Admin/Editor/Viewer with server/database permissions.

## Repository layout

```
src/SqlDataCollector.Core/   job/domain library, NO ASP.NET dependency (also loaded by an organization's existing Hangfire host)
  Extract/                   ExtractRunner, TargetTableManager (Strict/Evolve, daily partitions), Watermark, AugmentedDataReader
  Export/                    ExportRunner (single table / custom query / AllTables), CsvPartWriter, GcsUploader
  Scheduling/SystemJobs.cs   RetentionRunner, JobSynchronizer, StartupService
  Infrastructure/            SqlConnectionFactory, MetadataRepository (Dapper), AppLock, CollectorQueues, SchemaInstaller
  Services/                  CatalogService (db/table/column lists, preview, target compare), JobRegistrar (Hangfire registration)
  Sql/001_schema.sql         etl.* metadata schema – embedded, idempotent, contains upgrade blocks
  ServiceCollectionExtensions.cs   AddSqlDataCollectorCore / AddSqlDataCollectorStartup / UseSqlDataCollectorStorage
src/SqlDataCollector/        host: Blazor UI (Roles=Web) and/or Hangfire Server (Roles=Worker), auth, CLI commands (Program.cs)
samples/ExistingHangfireHost/  minimal "corporate Hangfire host" that runs our jobs via SqlDataCollector.Core (deployment 2b)
tests/SqlDataCollector.Tests/  xUnit: Unit/ (no Docker) + Integration/ (Testcontainers: SQL Server 2022 + fake GCS)
docker/                      compose.yml (local test env + "full"/"shared" profiles), Dockerfile, seed SQL
deploy/                      appsettings samples per deployment mode + existing-host integration guide
scripts/dev.ps1              all local environment / test commands
sql/                         DBA permission template, bootstrap example
```

## Deployment modes (both must keep working)

1. **Embedded** – one app server, `Roles=Web,Worker`, `ConnectionStrings:Hangfire` empty ⇒ Hangfire tables in the
   metadata DB (`hangfire_collector` schema, `HangfirePrepareSchema=true`).
2. **Shared Hangfire** – UI on the app server (`Roles=Web`), jobs in the organization's central Hangfire DB
   (own schema, installed by the DBA, `HangfirePrepareSchema=false`), executed by
   2a) our Worker service (`Queues=sdc_extract,sdc_system`) or 2b) the existing host with `AddSqlDataCollectorCore`.
   Optional export Worker on the target SQL host (`Queues=sdc_export`).

No code is ever "sent" to Hangfire: the Web writes `etl.*` rows plus a recurring entry
`extract-{id} → ExtractRunner.RunAsync(id, name, triggeredBy)`; whichever process has SqlDataCollector.Core and listens to
the queue executes it. **Job method signatures are a contract** stored in Hangfire – changing them requires keeping old
overloads or clearing recurring jobs.

## Local test environment (developer machine, Windows)

- Locally installed SQL Server = metadata DB `SdcMeta` (+ embedded Hangfire) and target DB `SdcDwh`, Windows auth.
- Docker: `src-erp` (localhost,14331, DB `ERP`: 5k customers, 200k orders, 450k lines, procs `usp_DailySales`,
  `usp_SimulateActivity`), `src-crm` (localhost,14332, DB `CRM`: 20k contacts, 100k activities, view, proc),
  `fakegcs` GCS emulator (http://localhost:4443, bucket `test-bucket`). SA password `Dev_Passw0rd!2026` (dev only).
- App: http://localhost:5080, local admin `admin` / `Admin!Dev2026`; the Windows user running setup gets Admin.

Claude Code on Windows runs commands in Git Bash – call the script like this:

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 setup        # first time (idempotent)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 smoke        # end-to-end: all extracts, simulate, incremental, export, GCS check
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 run          # start the app (run in background)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 run-job "extract all"   # or "extract 3", "export all"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 simulate     # new/changed source rows
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 gcs-ls exports/
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 reset-local  # drop + recreate SdcMeta/SdcDwh
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 full-up      # everything in Docker, embedded mode (http://localhost:8080)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 shared-up    # everything in Docker, shared mode (web + corporate-host + worker-export)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 down         # stop containers (-Volumes to delete data)
```

If the local SQL Server is a named instance or auto-detection fails: `-SqlInstance "localhost\SQLEXPRESS"`
(or env `SDC_SQL_INSTANCE`). `appsettings.LocalTest.json` is generated by setup and git-ignored.

## Build and test

```bash
dotnet build SqlCollector.sln
dotnet test SqlCollector.sln --filter "Category!=Integration"   # unit, seconds, no Docker
dotnet test SqlCollector.sln --filter "Category=Integration"    # Testcontainers: needs Docker Desktop (Linux containers)
dotnet test SqlCollector.sln                                    # all (currently 35 tests)
```

Integration tests start their own SQL Server + fake GCS containers (independent of the dev environment) and cover:
append/replace/incremental (datetime and rowversion watermarks), decimal(38) exactness, stored procedure with temp table,
Strict failure + rollback, Evolve, partition-truncate retention, AllTables export (plain CSV upload, gzip archive,
RFC 4180 content, per-table incremental). **Every behaviour change needs a test** (unit if possible, integration when SQL
or GCS is involved). Definition of done: build without warnings, all tests green, smoke passes when the change touches
runtime behaviour, Help page (both languages) and README updated when user-visible behaviour changes.

## Invariants and hard-won lessons (do not regress)

1. **Dapper does not call setters for NULL** – never give a C# default to a property whose DB column is nullable
   (it silently replaced NULL schema filters with "stage").
2. **Watermark parameters use the source column's real SQL type** (`datetime` stays `datetime`, `varchar` stays
   `varchar`) – otherwise `.003` datetime rows reload every run or index seeks are lost.
3. **decimal(38,x)** goes through `SqlDecimal` (`AugmentedDataReader`, `CsvValueFormatter`) – .NET decimal overflows.
4. **Dead-load recovery**: acquire the job's `sp_getapplock`, then re-read the load status, then act; status updates are
   conditional (`WHERE Status IN ('Running','CleanupPending')`). A race once deleted a successful load.
5. **Cleanup/retention only touch tables that have `_LoadId`** (`SqlHelpers.HasLoadIdColumnAsync`); a pre-existing table
   that failed the Strict check must end as `Failed`, not `CleanupPending`.
6. **Incremental/AllTables exports** use `GetSafeMaxLoadIdAsync` (never pass a Running/CleanupPending load) and read from
   the primary (`forWrite: true`) – a lagging AG secondary would skip rows.
7. **CSV** is RFC 4180: quote delimiter/quote/CR/LF/leading-trailing spaces; empty string is `""`, NULL is the NullValue.
   Do not switch to bcp: measured equal speed and it corrupts such data (no quoting, split rows, no header).
8. **Upload plain CSV by default; the local archive is always gzip**, compressed only after a successful upload.
9. **Queues are `sdc_extract`, `sdc_export`, `sdc_system`** (`CollectorQueues`) – never `default`; shared Hangfire storages
   must not hand our jobs to other applications' servers.
10. The Hangfire install script needs `SET QUOTED_IDENTIFIER ON` (sqlcmd defaults to OFF).
11. `Sql/001_schema.sql` must stay idempotent and upgrade existing installations (add `IF NOT EXISTS` / constraint
    upgrade blocks; never drop user data).
12. The **Web role needs no write access to target databases**; cleanup/recovery runs only in Workers.
13. **Permissions are enforced server-side on save** (`AccessService.CanUseAsync`), not only by filtering dropdowns.
14. Base `appsettings.json` = embedded defaults; mode-specific settings live in `deploy/`. Containers set `URLS`
    because `appsettings.json` defines `Urls`.
15. Linux containers cannot use Windows auth: tests and Docker profiles use `SqlCredentialOverrides:<ServerName>`
    (dev/test only); production uses Integrated Security (gMSA).

## Code conventions

- .NET 10, C# latest, nullable enabled, file-scoped namespaces, async all the way with `CancellationToken`.
- Structured logging with message templates (`"Extract {Name}: {Rows:N0} rows"`), English.
- SQL identifiers always via `SqlIdent.Quote`; values always as parameters. Free SQL (filters, queries) is an Editor
  feature by design – the security boundary is the service account's permissions.
- Keep `SqlDataCollector.Core` free of ASP.NET references. UI-only code stays in `src/SqlDataCollector`.
- No FluentAssertions (license); use xUnit `Assert`.
- New user-visible behaviour: update `Components/Pages/Help.razor` **and** `HelpTr.razor` (same `HelpLink` topic ids, both
  tables of contents) and README. `HelpTopicsTests` fails when a topic exists in only one language.
  When a scenario of the Guide changes, update both `GuideEn.razor` and `GuideTr.razor` and refresh the screenshots
  (`scripts/guide-screenshots.ps1`; adjust its crop regions when a page layout moves).
