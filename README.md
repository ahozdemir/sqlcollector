# SqlDataCollector

A .NET 10 application that collects data on a schedule from several SQL Server sources into tables on one target
SQL Server, manages retention on the target, and exports the collected data every night as CSV files to
Google Cloud Storage. Scheduling and execution use **Hangfire**. The UI is **Blazor Server** (English) with an
in-app **Help** section; users sign in with **Active Directory** (Windows/Negotiate), plus one local admin account.

---

## 1. Architecture

```
 [APP server]                    [HANGFIRE server]                  [TARGET SQL Server host]
  SqlDataCollector                SqlDataCollector                    SQL Server
  Roles = Web                     Roles = Worker                      ├─ DataCollectorMeta  (etl.*: jobs, roles, permissions, run log)
  (IIS or Windows Service)        Queues = sdc_extract,sdc_system     └─ DWH                (collected data: stage.Orders …)
        │                               │                             SqlDataCollector (optional)
        │                               │                             Roles = Worker, Queues = sdc_export
        │                               │                             → reads DWH locally, writes CSV to local disk, uploads to GCS
        └──── all ──── Central Hangfire DB (schema hangfire_collector) ────┘
                                        └──> source SQL Servers (ERP, CRM …) – read only
```

**No code is sent to Hangfire.** When a user clicks *Save*:

1. The job definition is written to `DataCollectorMeta.etl.ExtractJob` (data).
2. A recurring entry `extract-5 → "0 2 * * *" → ExtractRunner.RunAsync(5, …)` is written to the Hangfire DB (a method invocation).

At 02:00 a Worker picks up that entry and runs its own copy of `ExtractRunner`, which reads job 5 from the metadata
database, reads the source and writes the target. Adding jobs needs no deployment; only new application versions do.

| Role | Does | Connections |
|---|---|---|
| **Web** | UI, job definitions, schedule registration, "Run now" (enqueue), monitoring | Metadata (r/w), Hangfire (r/w), sources/target read-only (lists, preview, check target) |
| **Worker** | Hangfire Server: executes extract, export, retention and sync jobs | Metadata, Hangfire, sources (read), target (write + DDL), GCS |

**Queues** route work to machines: extract jobs → `sdc_extract`, exports → `sdc_export`, system jobs → `sdc_system`
(prefixed so a shared Hangfire storage never hands our jobs to another application's servers).
`Collector:Queues` decides what a Worker takes. Recommended for large exports: a Worker **on the target SQL Server host**
with `Queues = sdc_export` (data is read locally and the CSV is written to that machine's disk, then uploaded), and the
central Worker with `Queues = sdc_extract,sdc_system`. Several Workers may share a queue; the same job never runs twice at once
(SQL `sp_getapplock`). Small installations can run one process with `Roles = "Web,Worker"`.

### Deployment modes

| Mode | Summary | Samples |
|---|---|---|
| **1. Embedded** | One app server, `Roles=Web,Worker`, `ConnectionStrings:Hangfire` empty → Hangfire tables in the metadata DB (`hangfire_collector` schema, created by the app) | `deploy/embedded` |
| **2a. Shared Hangfire + our Worker** | UI on the app server (`Roles=Web`); jobs in the organization's central Hangfire DB; our Worker service on the Hangfire server executes them | `deploy/shared-hangfire/web`, `…/worker` |
| **2b. Shared Hangfire + existing host** | Same, but the organization's existing Hangfire Server host references `SqlDataCollector.Core` and runs the jobs | `deploy/shared-hangfire/existing-host`, `samples/ExistingHangfireHost` |

Both shared variants may add an export Worker on the target SQL host (`deploy/shared-hangfire/worker-export-dbhost`).

---

## 2. Features

**Extract**
- Pick source tables/views (multi-select creates one job per table), columns, WHERE filter, NOLOCK
- Free SQL: CTEs, temp tables, `EXEC dbo.sp …`
- Load modes: **Append** (snapshot history) · **Replace** (keep only the latest successful load) ·
  **Incremental** (watermark: datetime/datetime2/date/int/bigint/decimal/rowversion; the parameter uses the column's real
  SQL type, so no datetime `.003` duplicates and no lost index seeks)
- **Replace with rebuild** (optional): load a fresh `<table>__sdc_new` from the current source schema (minimally logged),
  then swap it in atomically – no slow DELETE of the previous load, source column changes absorbed, the old table stays
  if the load fails. Only for collector tables written by this job alone; grant permissions on the schema.
- New jobs get the target schema from the source database name (`ERP` → `erp`) – one schema per source avoids collisions
- **Schema policy**: *Strict* (create if missing; an existing table must match exactly, otherwise fail with a diff) ·
  *Evolve* (add columns, widen text/binary columns)
- **Check target** compares the source result schema with the target table column by column before saving
- Optional **daily partitioning** on `_LoadedAt` (UTC): retention drops whole days with `TRUNCATE … WITH (PARTITIONS)`;
  future partitions are created automatically
- Technical columns on every target table: `_LoadId`, `_LoadedAt`, `_ExtractJobId`, `_SourceServer`
- Failed loads are rolled back; loads interrupted by a crash are cleaned up by the next sync
- decimal(38,x) values are transferred exactly (`SqlDecimal`)

**GCS export**
- **All collected tables** (nightly): every extract target table in a database (optionally one schema), **one CSV set per
  table**, only the loads since that table's previous export – i.e. "what was collected today". New tables are picked up
  automatically. Per-table state: one failing table does not block the others; a retry only redoes what is missing
  (*PartiallyFailed*). One run manifest in `…/_manifests/`.
- **Single table** incremental or full, **Custom query** (`@RunDate`, `@RunAtUtc`)
- A load that is still running is never exported half-done
- RFC 4180 CSV: delimiter, quoting, LF/CRLF, NULL value, UTF-8 BOM, split by max rows per file
- **Uploaded to GCS as plain CSV** (optional per job: upload gzip); the **local archive is always `.csv.gz`**
- Object names with tokens `{Name} {Database} {Schema} {Table} {Date} {DateCompact} {Time} {Part}`; default
  `exports/{Database}/{Schema}.{Table}/dt={Date}/{Table}_{DateCompact}_{Time}_{Part}.csv`
- Local day folders under `WorkDirectory`; with `KeepLocalFiles` the files are gzip-compressed after a successful upload and kept for `LocalFileRetentionDays`
- Resumable upload, retries, size verification

**Administration**
- Roles **Admin / Editor / Viewer** for AD users or groups; server/database permissions (who may use which server/database as source or target)
- Changes reach open sessions within 10 minutes
- **Settings** (Admin): organization-wide retention policy – default and maximum retention, whether "keep forever" is
  allowed, whether Editors may change retention – plus the retention schedule and run history / audit / permission check
  retention. Stored in `etl.SystemSetting`, no restart needed, audited; enforced on job save and by the retention job.
  Lists orphaned collector tables (left behind by deleted jobs). GCS objects: use a bucket lifecycle rule.
- **Alerts**: failed / partially failed / abandoned / missed runs, recoveries and queues without a Hangfire server are sent
  to e-mail (SMTP via MailKit), Microsoft Teams (Workflows webhook, Adaptive Card) or a generic JSON webhook, plus the
  job's own *Notify* addresses. Per-channel events, job kinds and name filter; repeat interval; 3 delivery attempts;
  delivery history. Dispatched every minute by the Web process (so a missing Worker is reported too).
- Audit log, run history (with read/CSV vs upload timings), status page with active Workers and their queues
- Hangfire Dashboard: full access for Admin, read-only for others
- In-app **Help** (menu *Help*, plus `?` links next to each form section)

---

## 3. Why not bcp for the CSV export?

Measured on the same 1,000,000-row table (8 columns, Turkish text, some values with commas, quotes and line breaks),
SQL Server and exporter on the same machine (2 vCPU):

| | Time | Correct CSV? |
|---|---|---|
| bcp `-c -t,` (CSV only) | 1.9 s | **No** – 52,941 extra lines (embedded line breaks split rows), unquoted commas shift columns, no header row, empty string vs NULL lost |
| bcp + separate `gzip -6` | 1.9 + 3.7 = 5.6 s | No (same) |
| SqlDataCollector, plain CSV | 1.8 s (warm) | Yes – 1,000,000 rows, 0 rows with wrong field count |
| SqlDataCollector, CSV + gzip (streamed) | 3.3 s | Yes |
| bcp `-n` (native binary) | 0.8 s | not CSV |

Upload time depends on bytes: gzip reduced 113 MB to 38 MB. The locality benefit of exporting on the database host is
achieved with an export-only Worker on that host (see Architecture) – no `xp_cmdshell` needed.

---

## 4. Installation

### 4.0 Installer (recommended)
```powershell
.\scripts\dev.ps1 installer        # -> artifacts\installer\dist\SqlDataCollector-Setup.exe
```
`SqlDataCollector-Setup.exe` is one self-contained file (application + .NET runtime + wizard): nothing has to be installed on
the server except an existing SQL Server. Requirements: Windows Server joined to the AD domain, a domain service account
(or gMSA). The wizard asks, in this order:

1. **Mode** – *Embedded* (one server, Hangfire in the metadata DB) or *Central* (organization's Hangfire DB; Web and/or Worker on this server).
2. **Service account** – verified against the domain; gets "Log on as a service"; the service runs as this account.
3. **Databases** – *create now* with the installing user's Windows account (databases, etl schema, permissions, `sdc_export_reader`),
   or *later*: scripts in `<program folder>\sql` for the DBA (`01_databases_and_permissions.sql`, `02_etl_schema.sql`,
   central: `03_hangfire_central.sql`). Until the metadata DB is ready the app serves a *setup required* page and restarts itself once it is.
4. **Web and HTTPS** – host name, port (443), self-signed certificate, firewall rule, program and data folder
   (`%ProgramData%\SqlDataCollector`: `certs\`, `exports\` = temporary CSV files / archive).

The service is registered (automatic delayed start, restart on failure) and started; *Open SqlDataCollector* opens
`https://localhost/login`. **First sign-in** (only possible on the server itself): *Local administrator* asks for a new
password – there is no default. Then: grant roles to AD groups (*Permissions*), upload the GCS service account key and
import the corporate HTTPS certificate (*Settings*). Kerberos: `setspn -S HTTP/<host> <service account>`.

Silent installation: `SqlDataCollector-Setup.exe /silent /config install.json /password-env SDC_PWD [/log file] [/dryrun]`
(the JSON has the wizard's fields, e.g. `{"Mode":"Embedded","ServiceAccount":"CORP\\svc-sdc","SqlServer":"sql01","MetadataDb":"SdcMeta","DwhDb":"DWH","DatabaseAction":"Now","HostName":"sdc.corp.local"}`).
Running the setup again upgrades the binaries and keeps the configuration. Uninstall: *Apps & features* or `/uninstall [/purge]`.

**GitHub release:** pushing a version tag builds the installer on a GitHub-hosted Windows runner
(`.github/workflows/release.yml`: build with warnings as errors, unit tests, `dev.ps1 installer`, SHA-256) and attaches
`SqlDataCollector-Setup.exe` and its `.sha256` to a release of this repository:
```powershell
git tag v1.0.0
git push origin v1.0.0
```
The version (`1.0.0`) is stamped into the application and the setup. *Actions → Release installer → Run workflow* builds
without releasing (the exe is kept as a workflow artifact for 14 days). The exe is not code-signed (SmartScreen may warn).

Manual installation (below) remains possible.

### 4.1 Build
```powershell
dotnet publish src/SqlDataCollector -c Release -r win-x64 --self-contained false -o D:\SqlDataCollector\app
```

### 4.2 Databases
1. On the target SQL Server create `DataCollectorMeta` and `DWH`.
2. *Embedded mode:* nothing to do – the app creates `hangfire_collector` in the metadata DB (`HangfirePrepareSchema=true`).
   *Shared mode:* generate the install script for the central Hangfire DB and hand it to the DBA:
   ```powershell
   SqlDataCollector.exe hangfire-install-script hangfire_collector "CORP\sdc-web$" "CORP\sdc-worker$" > hangfire_install.sql
   ```
   It creates the `hangfire_collector` schema and grants the two accounts **only** SELECT/INSERT/UPDATE/DELETE on it
   (keep `Collector:HangfirePrepareSchema = false`).
3. Service account permissions: `sql/01_permissions_template.sql` – including the read-only `sdc_export_reader` user in `DWH`
   (and every other database exports read from), which export reads switch to.
4. The `etl.*` schema is installed on first start (`AutoCreateSchema=true`, needs `db_ddladmin` at that moment), or the DBA
   runs `SqlDataCollector.exe metadata-install "<connection string>"` (or `src/SqlDataCollector.Core/Sql/001_schema.sql`) and you set `AutoCreateSchema=false`.

### 4.3 Local admin password
```powershell
SqlDataCollector.exe hash-password      # paste the output into LocalAdmin:PasswordHash
```
Sign in as local admin first, then grant the Admin role to an AD group on **Permissions** (or use `sql/02_bootstrap_example.sql`).

On **Servers** define the target server(s) and the source servers (the metadata and Hangfire databases are shown there
read-only, as configured in appsettings). Then click **Check permissions**: the Web account is checked directly and a
check job is queued on every queue a live Hangfire server listens to, so each Worker verifies its own account. Missing
permissions (vs. `sql/01_permissions_template.sql`) and permissions that must not exist (write/DDL on sources, write on
targets for a Web-only process, rights outside our schema in a shared Hangfire database) are listed per process.
Workers also run this check on startup and log problems as warnings.

### 4.4 Workers (Windows Service)
```powershell
# central Hangfire server – appsettings: "Roles": "Worker", "Queues": "sdc_extract,sdc_system"
sc.exe create SqlDataCollectorWorker binPath= "D:\SqlDataCollector\app\SqlDataCollector.exe" start= delayed-auto obj= "CORP\sdc-worker$"
sc.exe failure SqlDataCollectorWorker reset= 86400 actions= restart/60000/restart/60000/restart/300000
sc.exe start SqlDataCollectorWorker

# optional export Worker on the target SQL Server host – "Roles": "Worker", "Queues": "sdc_export",
#   "WorkDirectory": "E:\\exports", "KeepLocalFiles": true, "LocalFileRetentionDays": 7
#   needs outbound HTTPS (443) to storage.googleapis.com
```
With a gMSA (`…$`) no password is entered.

### 4.5 Web (app server)
- **IIS:** install the ASP.NET Core Hosting Bundle 10, app pool identity `CORP\sdc-web$`, enable **Windows Authentication + Anonymous**
  (anonymous is required for the sign-in page and static files).
- **Windows Service (Kestrel):** as above with `Roles=Web`. Register `setspn -S HTTP/sdc.corp.local CORP\sdc-web$` for Kerberos (NTLM works without it).
- Use HTTPS in production (IIS binding or `Kestrel:Endpoints`).

### 4.6 appsettings.json (base file = embedded defaults; per-mode overrides in `deploy/`)
| Setting | Description |
|---|---|
| `ConnectionStrings:Metadata` | etl.* database (on the target server, separate DB) |
| `ConnectionStrings:Hangfire` | Central Hangfire DB (falls back to Metadata if empty) |
| `Collector:Roles` | `Web`, `Worker` or `Web,Worker` |
| `Collector:Queues` | Queues a Worker serves, e.g. `sdc_export` or `sdc_extract,sdc_system` (default: all three) |
| `Collector:HangfireSchema` / `HangfirePrepareSchema` | Default `hangfire_collector`; `true` for embedded, `false` for a DBA-managed shared schema |
| `Collector:WorkerCount` | Concurrent jobs per Worker |
| `Collector:TargetTableCompression` | New target tables: `NONE` / `ROW` / `PAGE` |
| `Collector:WorkDirectory` / `KeepLocalFiles` / `LocalFileRetentionDays` | Local day folders; archive kept as `.csv.gz` for N days |
| `Collector:ExportReadAsUser` | Read-only database user export reads switch to (default `sdc_export_reader`; empty = off, not recommended) |
| `Alerts:Smtp:Host` / `Port` / `Security` / `From` / `UserName` / `Password` | SMTP for e-mail alerts (internal relay recommended; `Security`: Auto, None, StartTls, SslOnConnect) |
| `Alerts:PublicUrl` | Base URL of the UI for links in alerts |
| `Https:Port` / `HttpRedirectPort` / `HostName` / `CertificateDirectory` | HTTPS served by Kestrel (0 = off, Urls used); self-signed certificate until one is imported on Settings |
| `Secrets:Key` | AES-256 key (base64, 32 bytes) for secrets stored in the metadata DB (GCS key); the same on all servers of an installation |
| `Collector:RetentionCron` / `RunLogRetentionDays` | Initial values only: once saved on **Settings**, the values in `etl.SystemSetting` win |
| `Gcs:CredentialsPath` | Optional service account JSON file; empty = the key uploaded on Settings, else Application Default Credentials (needs `roles/storage.objectCreator`) |
| `Gcs:Endpoint` | Optional (Private Service Connect / emulator) |
| `SqlCredentialOverrides:<ServerName>` | **Only** for non-domain servers or tests (`User ID=..;Password=..`); normally empty |

Secrets can come from environment variables: `ConnectionStrings__Metadata`, `LocalAdmin__PasswordHash`, …

---

## 5. Daily flow (example)

| Time | Job | Worker |
|---|---|---|
| 00:00–00:30 | Extract jobs (ERP, CRM → DWH.stage.*) | central (`sdc_extract`) |
| 01:00 | GCS export *All collected tables* (DWH) → one CSV set per table with that day's loads | DB host (`sdc_export`) |
| 01:30 | Retention (old days removed, partitions truncated) | central (`sdc_system`) |

---

## 6. Security notes
- No SQL passwords stored; all connections use the service account (Integrated Security).
- Editors can write SQL (filters, column expressions, queries). The real boundary on sources is the Worker account's
  permissions: grant **read only** on sources. Export queries run on the target with the Worker account; give the Editor
  role to trusted people. **Servers → Check permissions** reports write/DDL permissions on sources as *Excess*.
- Export reads and export previews run on the target as the read-only database user `Collector:ExportReadAsUser`
  (default `sdc_export_reader`, `CREATE USER … WITHOUT LOGIN` + `db_datareader`) via `EXECUTE AS USER … WITH COOKIE`,
  so export SQL cannot write, drop or `REVERT` to the Worker's rights. Create the user (and `GRANT IMPERSONATE` to both
  service accounts) when the target database is set up – see `sql/01_permissions_template.sql`. Empty setting = disabled.
- Local admin: PBKDF2-SHA256 hash, lockout after 5 failures for 15 minutes, antiforgery-protected form.

---

## 7. CLI commands

```powershell
SqlDataCollector.exe hash-password                                         # PBKDF2 hash for LocalAdmin:PasswordHash
SqlDataCollector.exe hangfire-install-script [schema] [accounts…] > x.sql  # DBA script for a shared Hangfire DB
SqlDataCollector.exe hangfire-install "<connection string>" [schema]       # install Hangfire tables directly
SqlDataCollector.exe metadata-install "<connection string>"                # install/upgrade the etl.* schema
SqlDataCollector.exe run-job extract|export <id|all>                       # run job(s) in-process now (exit code 0 = OK)
SqlDataCollector.exe create-cert --host sdc.corp.local [--dir <folder>]      # self-signed HTTPS certificate (installer)
SqlDataCollector.exe db-script --meta M --dwh D [--account DOMAIN\svc] [--central --hangfire-db H] --out <folder>   # DBA scripts
SqlDataCollector.exe db-install --server S --meta M --dwh D [--account DOMAIN\svc] [--trust-server-certificate]   # run them (current Windows account)
SqlDataCollector.exe new-secret-key                                         # value for Secrets:Key
```

---

## 8. Development and tests

Prerequisites: .NET 10 SDK, Docker Desktop (Linux containers), a local SQL Server (Developer/Express) reachable with
Windows authentication, PowerShell.

```powershell
.\scripts\dev.ps1 setup      # Docker sources ERP (:14331) + CRM (:14332) with demo data, GCS emulator (:4443),
                             # SdcMeta + SdcDwh on the local SQL Server, 8 extract jobs + 1 export job, appsettings.LocalTest.json
.\scripts\dev.ps1 run        # app on http://localhost:5080 (admin / Admin!Dev2026, or your Windows account = Admin)
.\scripts\dev.ps1 smoke      # all extracts → simulate source changes → incremental → all-tables export → check emulator
.\scripts\dev.ps1 test       # unit + Testcontainers integration tests
.\scripts\dev.ps1 full-up    # embedded mode completely in Docker (http://localhost:8080)
.\scripts\dev.ps1 shared-up  # shared Hangfire mode in Docker: web + corporate-host + export worker
.\scripts\dev.ps1 down       # stop containers
```

Named SQL instance: `-SqlInstance "localhost\SQLEXPRESS"`. Claude Code users: see `CLAUDE.md` and `.claude/`.

---

## 9. Project layout
```
src/SqlDataCollector.Core/   job library without ASP.NET (Extract/, Export/, Scheduling/, Infrastructure/, Services/, Sql/001_schema.sql)
src/SqlDataCollector/        host: Program.cs (roles, queues, auth, Hangfire, CLI), Security/, Components/ (Blazor UI, Help)
samples/ExistingHangfireHost/  organization Hangfire host running our jobs (deployment 2b)
tests/SqlDataCollector.Tests/  unit + Testcontainers integration tests
docker/                      compose.yml, Dockerfile, seed SQL for demo sources
deploy/                      appsettings per deployment mode
scripts/dev.ps1              local environment and test commands
sql/                         permission template, bootstrap example
```
