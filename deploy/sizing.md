# Sizing guide

How much data and how many source servers one installation handles, what hardware to plan, and how to confirm the
numbers with a short pilot before go-live.

## 1. The short answer

- **Source database size is not the limit.** The collector never reads a whole database: it reads the tables you
  configure, and with *Incremental*, *Merge + watermark* or *Change Tracking* only the rows that changed since the last
  run. A 20 TB ERP database that changes 1 % a day is a ~200 GB/day job, not a 20 TB one. Rows are streamed from the
  source reader straight into `SqlBulkCopy`; nothing is held in memory, so table size does not change the memory of a
  worker (measured: ~200 MB working set for the whole process).
- **The number of source servers has no fixed limit.** A source is a row in *Servers* (connection string + permissions);
  there is no agent on the source and no per-server cost besides one read connection per running job.
- **What limits an installation** is how many rows it can move inside the time window you allow (worker slots × rows per
  second per slot), how fast the **target SQL Server** can write them, and how much **target storage** the retention
  period needs. These three numbers are what this guide sizes.

## 2. Measured throughput

Measured on the local test environment: developer workstation with 16 logical CPUs, 32 GB RAM and SSD; source SQL Server
2022 in Docker (WSL 2), target SQL Server installed locally, S3/Azure emulators in Docker. Numbers come from the run
history (`etl.LoadRun`, `etl.ExportRun`) and are best-of-several runs. Treat them as a **floor**: production servers with
dedicated storage and 10 GbE are usually faster, but only a pilot (section 6) gives a number you can commit to.

### Extract (one job = one worker slot)

| Case | Rows | Time | Rows/s |
|---|---:|---:|---:|
| Rowstore target, 1 writer – `Orders` (~160 bytes/row) | 200,000 | 6.9 s | **29,000** (~4.5 MB/s, ~16 GB/h) |
| Rowstore target, 1 writer – `OrderLines` (narrow, ~40 bytes/row) | 450,000 | 11.9 s | **38,000** |
| Rowstore target, 4 parallel writers | 450,000 | 10.6 s | 42,600 (+12 %) |
| Columnstore target, 1 writer | 450,000 | 5.1 s | **88,600** |
| Columnstore target, 4 parallel writers | 450,000 | 2.8 s | **162,000** |
| Merge with Change Tracking, first (full) read | 200,950 | 11.2 s | 18,000 |
| Change Tracking delta: 100 inserts + 50 updates | 150 | 1.5 s | – |
| Run with nothing new (incremental / CT) | 0 | 0.26 – 0.75 s | – |

Parallel readers gave no gain locally (reading took 0.35 s of 5.1 s – the target was the bottleneck); they help when the
source read is the slow part (remote source, large table scan).

### Export (one export job = one worker slot)

| Case | Rows | Output | Time | Rows/s |
|---|---:|---:|---:|---:|
| All tables → CSV → S3-compatible | 14.9 M | 457 MB, 9 files | 39.2 s | **380,000** (~11.7 MB/s, ~42 GB/h) |
| All tables → Parquet → Azure Blob | 14.2 M | 68 MB, 6 files | 21.0 s | **676,000** |

Parquet files were 5.6× smaller than the same rows as CSV (2.79 M rows: 77 MB CSV vs 13.6 MB Parquet).

### Worker CPU per running job

CPU time of the worker process (`TotalProcessorTime`) during one run, divided by its duration; same narrow 450,000-row
table, wider rows cost proportionally more. The target SQL Server's own CPU is not included.

| Job | Duration | Worker CPU | Cores busy while it runs | CPU per row |
|---|---:|---:|---:|---:|
| Extract, rowstore, 1 writer | 12.3 s | 4.6 s | **0.37** | ~10 µs |
| Extract, columnstore, 4 writers | 1.7 s | 4.3 s | **2.5** | ~10 µs |
| Export 1.35 M rows → CSV (S3) | 7.5 s | 3.4 s | **0.45** | ~2.5 µs |
| Export 1.35 M rows → Parquet | 6.7 s | 2.3 s | **0.34** | ~1.7 µs |
| Hash masking (per value, one core) | – | – | – | ~0.55 µs |

The CPU per row is the same however the job is set up – a faster job (parallel writers, columnstore) just uses more cores
at once. Rule of thumb: **worker cores ≈ rows per second being loaded × 10 µs**, plus ~2 cores for the OS, the UI and Hangfire.

### Planning figures per worker slot

Use these until a pilot gives your own numbers:

| Target table | Rows/s per slot | Rows per hour per slot |
|---|---:|---:|
| Rowstore (PAGE compressed, default) | 30,000 | ~100 million |
| Columnstore, 4 parallel writers | 150,000 | ~500 million |
| Fixed overhead per run | 0.5 s | ~7,000 small runs per slot-hour |

## 3. Worksheet

```
Rows per day       R  = Σ over jobs (rows per run × runs per day)
Slot-hours per day H  = Σ over jobs (rows per run ÷ rows/s per slot) ÷ 3600  +  runs per day × 0.5 s ÷ 3600
Worker slots       S  = H ÷ window hours × 1.5 (headroom)            S = WorkerCount × worker processes
Target storage     T  = rows kept × bytes per row on the target
                        rows kept = R × RetentionDays (Append/Incremental), = table rows (Replace/Merge)
Export time        E  = rows exported ÷ export rows/s per slot
```

- **Initial load** counts separately: the first run of every job (and every run of a *Replace* job) reads the whole
  table. Schedule it once, table by table, over a weekend, with columnstore and parallel writers for the large ones.
- **Aggregate target speed caps S.** Slots scale as long as the target SQL Server keeps up; several jobs writing the
  same server share its log and data I/O. Measure the aggregate in the pilot (section 6, step 3).
- **Bytes per row** depend on the data: measure it after the first load (`sp_spaceused` ÷ row count). Columnstore is
  typically several times smaller than PAGE-compressed rowstore; the synthetic test data compresses far better than
  real data, so it is not quoted here.

### Worked examples (planning figures, rowstore unless stated)

| Scenario | Rows | Slot-hours | Result |
|---|---:|---:|---|
| 20 TB ERP source, incremental, 50 M changed rows/day | 50 M/day | 0.5 | 4 slots finish in minutes; size is irrelevant |
| 30 sources, 300 tables, every 15 min, 500 M rows/day | 500 M/day + 28,800 runs | 5 + 4 | 8 slots, spread over the day |
| Initial load, 5 billion rows | 5 B once | 50 (rowstore) / 10 (columnstore, 4 writers) | 4 jobs at a time: ~13 h / ~3 h if the target keeps up |
| Nightly *Replace* of a 1 billion row table | 1 B/day | 10 | Too slow for a night: use Incremental, or Merge + Change Tracking |

### Example: about 1 TB of new data per night

Planning figures, 6-hour window. "1 TB" = the rows' size as CSV.

| Step | Per slot | 1 TB needs | Plan |
|---|---|---|---|
| Extract, rowstore, 1 writer | ~16 GB/h | ~64 slot-hours | 11 jobs at a time (16 slots with headroom); the target writes ~50 MB/s of data **plus** the same again in log (fully logged) |
| Extract, columnstore, 4 writers | ~4× rowstore, ~65 GB/h | ~16 job-hours | 3–4 large jobs at a time, 12–16 writer connections, one target core per writer |
| Export, CSV | ~42 GB/h | ~24 slot-hours | 4 export jobs (tables split into groups) at a time; one *All tables* job exports its tables one after another and would need ~24 h alone; upload 1 TB in 6 h ≈ 370 Mbit/s to the cloud |
| Export, Parquet | ~1.8× the rows/s of CSV, files ~5.6× smaller | ~13 slot-hours | 2–3 export jobs at a time; ~180 GB upload ≈ 70 Mbit/s |

- **Do not extract it all at night.** Incremental and Change Tracking jobs can run every hour during the day, so only the
  export is left for the night window – the extract slot-hours then spread over 24 hours.
- **Check what the 1 TB is.** If it is whole tables reloaded with *Replace*, switch them to Incremental or Merge + Change
  Tracking; the new rows are usually a fraction of it.
- **Storage grows by the compressed day × retention**: with typical compression (PAGE rowstore 2–3×, columnstore 5–10×;
  measure it in the pilot) 30 days of 1 TB/day is ~10–15 TB rowstore or ~3–6 TB columnstore on the target.
- **Suggested setup**: size **L** – 2 extract Workers (`WorkerCount=8`), an export Worker on the target host
  (`WorkerCount=4`, Parquet, work disk for the largest table's daily export), columnstore + daily partitions for the large
  tables, a target SQL Server with 16+ cores, ≥ 300 MB/s sustained write and the log on its own volume, and ≥ 1 Gbit/s to
  the object storage. At this volume the pilot (section 6) is required, not optional.

## 4. Recommended sizes

Estimates from the planning figures above with 1.5× headroom and a 6-hour night window. They are **not** load-tested at
these scales (the local environment runs 19 jobs); confirm the row with a pilot before committing.

| Size | Topology | Slots | Source servers | Jobs | Data moved per day |
|---|---|---:|---:|---:|---|
| **S** | 1 app server, embedded (`Roles=Web,Worker`), 4 vCPU / 16 GB | 4 | up to ~25 | up to ~250 | up to ~100 GB / ~0.7 B rows |
| **M** | App server (`Roles=Web`, 2 vCPU / 8 GB) + 2 Workers (4 vCPU / 8 GB, `WorkerCount=8`) | 16 | up to ~100 | up to ~1,000 | up to ~500 GB / ~3 B rows |
| **L** | App server + 4 Workers (`WorkerCount=8`) + export Worker on the target host | 32 | 200+ | 1,000+ | up to ~1.5 TB / ~10 B rows |

- **Worker CPU** (section 2): a rowstore job keeps ~0.4 core busy, a columnstore job with 4 writers ~2.5 cores. A 12-core
  server therefore carries `WorkerCount=12` with up to 3 large columnstore loads at the same time; CPU is rarely the limit.
- **Workers** are light: the process used ~200 MB; each running job adds bounded row buffers and one source and one
  target connection (plus one per extra reader/writer). More slots cost connections on the target, not worker RAM.
  The connection pools follow `WorkerCount` (18 connections per worker and database, at least .NET's default 100), so
  raising it never runs a job out of pooled connections; a changed `WorkerCount` needs a service restart.
  Place Workers close to the target; a slow WAN link to a source lowers that job's rows/s, not the others'.
- **Export Worker**: the work folder is chosen in the setup wizard (*Export work folder*) or set as
  `Collector:WorkDirectory`; CPU for CSV formatting and Parquet encoding (memory per Parquet export ≈ `ParquetRowGroupSize`
  × row width), and a work disk (`WorkDirectory`) for the largest table's daily export uncompressed, plus
  1–3 days of the local export archive (on by default, 1 day; *Settings → Local export archive*): CSV as `.csv.gz`, Parquet as is.
- **Above 1,000 jobs**: Hangfire handles thousands of recurring jobs, but the job lists in the UI load every job at
  once and have not been measured at that size. Run a synthetic test with the expected job count first.

## 5. Target SQL Server and metadata database

- **Target storage** = worksheet `T` plus free space for one extra copy of the largest *Replace with rebuild* table (it
  loads a new table and swaps it in). Daily partitions make retention a partition truncate, so retention adds no log load.
- **Log**: parallel writers load without a table lock, so rowstore loads are fully logged. Size the log (and log backups)
  for the largest run's data, or use columnstore targets for the big tables.
- **CPU**: columnstore compresses on the target – one core per parallel writer during a load.
- **Connections**: peak target connections ≈ Σ writers of the jobs that run at the same time + export readers.
- **Metadata DB** stays small: about 0.5 KB per run in `etl.LoadRun`, kept `RunLogRetentionDays` (90). 1,000 jobs every
  15 minutes = 96,000 runs/day ≈ 4–5 GB at 90 days. Hangfire's own job records expire after a day.
- **Sources**: incremental reads seek on the watermark column (index it); Change Tracking adds a small write cost on
  the source table. Parallel readers multiply the read load on the source – schedule big reads off-hours.

## 6. Pilot: measure before you commit

1. Pick the widest large table of each kind (rowstore and columnstore target, local and remote source) and create
   one job each with the planned settings.
2. Run each once with **Run now**. Read rows and duration in **Run history**: rows ÷ seconds = your rows/s
   per slot. Replace the planning figures in section 2 with it.
3. Start 4 (then 8) of these jobs at the same time and read the durations again: the total rows ÷ the longest duration
   is the aggregate target speed. When it stops growing, more slots will not help – the target is the limit.
4. Run `EXEC sp_spaceused 'schema.table'` on each target table and divide by its row count: bytes per row for `T`.
5. Run the export job once: rows ÷ seconds = export rows/s; file size ÷ rows = output bytes per row.
6. Fill in the worksheet (section 3) and choose the size (section 4).
