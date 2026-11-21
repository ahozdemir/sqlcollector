/* =====================================================================
   SqlDataCollector - metadata schema (ConnectionStrings:Metadata)
   Idempotent: safe to run repeatedly. The service runs it on startup
   (Collector:AutoCreateSchema=true), or a DBA can run it manually.
   Collected data is NOT stored here but in the TARGET server/database
   selected on each job.
   ===================================================================== */
IF SCHEMA_ID(N'etl') IS NULL EXEC(N'CREATE SCHEMA etl AUTHORIZATION dbo');
GO

/* ---------------- SQL Server definitions ----------------
   Connections use the application's AD service account (Integrated Security);
   no passwords are stored. IsSource / IsTarget: usable as source and/or target. */
IF OBJECT_ID(N'etl.SqlServer', N'U') IS NULL
CREATE TABLE etl.SqlServer
(
    SqlServerId            int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SqlServer PRIMARY KEY,
    Name                   nvarchar(100) NOT NULL CONSTRAINT UQ_SqlServer_Name UNIQUE,
    ServerAddress          nvarchar(256) NOT NULL,        -- host, host\instance, host,port, AG listener
    IsSource               bit NOT NULL CONSTRAINT DF_SqlServer_IsSource DEFAULT (1),
    IsTarget               bit NOT NULL CONSTRAINT DF_SqlServer_IsTarget DEFAULT (0),
    EncryptMode            varchar(20) NOT NULL CONSTRAINT DF_SqlServer_Encrypt DEFAULT ('Mandatory'),
    TrustServerCertificate bit NOT NULL CONSTRAINT DF_SqlServer_Trust DEFAULT (0),
    ReadOnlyIntent         bit NOT NULL CONSTRAINT DF_SqlServer_RO DEFAULT (0),
    MultiSubnetFailover    bit NOT NULL CONSTRAINT DF_SqlServer_MSF DEFAULT (0),
    ConnectTimeoutSec      int NOT NULL CONSTRAINT DF_SqlServer_CT DEFAULT (30),
    Description            nvarchar(400) NULL,
    IsActive               bit NOT NULL CONSTRAINT DF_SqlServer_IsActive DEFAULT (1),
    ModifiedBy             nvarchar(256) NULL,
    ModifiedAtUtc          datetime2(3) NOT NULL CONSTRAINT DF_SqlServer_Mod DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_SqlServer_Encrypt CHECK (EncryptMode IN ('Optional','Mandatory','Strict'))
);
GO

/* ---------------- Roles ----------------
   PrincipalName: DOMAIN\user or DOMAIN\group (local admin: 'local:admin')
   Role: Admin  = everything (servers, permissions, all jobs, Hangfire dashboard)
         Editor = defines/runs jobs on permitted servers/databases
         Viewer = sees jobs and run history                                    */
IF OBJECT_ID(N'etl.RoleAssignment', N'U') IS NULL
CREATE TABLE etl.RoleAssignment
(
    RoleAssignmentId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_RoleAssignment PRIMARY KEY,
    PrincipalName    nvarchar(256) NOT NULL,
    PrincipalType    char(1) NOT NULL CONSTRAINT CK_RoleAssignment_Type CHECK (PrincipalType IN ('U','G')),
    Role             varchar(20) NOT NULL CONSTRAINT CK_RoleAssignment_Role CHECK (Role IN ('Admin','Editor','Viewer')),
    ModifiedBy       nvarchar(256) NULL,
    ModifiedAtUtc    datetime2(3) NOT NULL CONSTRAINT DF_RoleAssignment_Mod DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_RoleAssignment UNIQUE (PrincipalName, Role)
);
GO

/* ---------------- Server / database permissions ----------------
   DatabaseName NULL = all databases on that server. Not needed for the Admin role. */
IF OBJECT_ID(N'etl.ServerPermission', N'U') IS NULL
CREATE TABLE etl.ServerPermission
(
    ServerPermissionId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ServerPermission PRIMARY KEY,
    PrincipalName      nvarchar(256) NOT NULL,
    SqlServerId        int NOT NULL CONSTRAINT FK_ServerPermission_Server REFERENCES etl.SqlServer(SqlServerId) ON DELETE CASCADE,
    DatabaseName       sysname NULL,
    CanSource          bit NOT NULL CONSTRAINT DF_ServerPermission_Src DEFAULT (1),
    CanTarget          bit NOT NULL CONSTRAINT DF_ServerPermission_Tgt DEFAULT (0),
    ModifiedBy         nvarchar(256) NULL,
    ModifiedAtUtc      datetime2(3) NOT NULL CONSTRAINT DF_ServerPermission_Mod DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_ServerPermission UNIQUE (PrincipalName, SqlServerId, DatabaseName)
);
GO

/* ---------------- Extract job definitions ----------------
   SourceType : 1 = Table (table + optional column list + WHERE)
                2 = Query (free SQL: CTEs, temp tables, EXEC sp ... supported)
   LoadMode   : 1 = Append      (each run appends rows; snapshot history)
                2 = Replace     (after a successful load, older loads of this job are deleted)
                3 = Incremental (WatermarkColumn > last watermark)
                4 = Merge       (upsert by KeyColumns: changed rows updated, new rows inserted, optionally rows
                                 missing from a full read deleted; optional WatermarkColumn reads only changed rows)
   Incremental + Query -> use @WatermarkFrom in the query:
        WHERE (@WatermarkFrom IS NULL OR t.ModifiedAt > @WatermarkFrom)
   SchemaPolicy  : 1 = Strict (create if missing; if it exists it must match exactly, otherwise fail)
                   2 = Evolve (add new columns, widen text/binary columns)
   PartitionDaily: daily partitioning on _LoadedAt (UTC) when the table is created
   RetentionDays : retention on the target by _LoadedAt (NULL = keep forever)  */
IF OBJECT_ID(N'etl.ExtractJob', N'U') IS NULL
CREATE TABLE etl.ExtractJob
(
    ExtractJobId      int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ExtractJob PRIMARY KEY,
    Name              nvarchar(200) NOT NULL CONSTRAINT UQ_ExtractJob_Name UNIQUE,
    SourceServerId    int NOT NULL CONSTRAINT FK_ExtractJob_SourceServer REFERENCES etl.SqlServer(SqlServerId),
    SourceDatabase    sysname NOT NULL,
    SourceType        tinyint NOT NULL CONSTRAINT CK_ExtractJob_SourceType CHECK (SourceType IN (1,2)),
    SourceSchema      sysname NULL,
    SourceTable       sysname NULL,
    ColumnList        nvarchar(max) NULL,
    FilterClause      nvarchar(max) NULL,
    SourceQuery       nvarchar(max) NULL,
    UseNoLock         bit NOT NULL CONSTRAINT DF_ExtractJob_UseNoLock DEFAULT (0),
    LoadMode          tinyint NOT NULL CONSTRAINT CK_ExtractJob_LoadMode CHECK (LoadMode IN (1,2,3,4)),
    WatermarkColumn   sysname NULL,
    SchemaPolicy      tinyint NOT NULL CONSTRAINT DF_ExtractJob_SchemaPolicy DEFAULT (1)
                      CONSTRAINT CK_ExtractJob_SchemaPolicy CHECK (SchemaPolicy IN (1,2)),  -- 1=Strict, 2=Evolve
    PartitionDaily    bit NOT NULL CONSTRAINT DF_ExtractJob_PartitionDaily DEFAULT (0),     -- applied when the target table is created
    TargetServerId    int NOT NULL CONSTRAINT FK_ExtractJob_TargetServer REFERENCES etl.SqlServer(SqlServerId),
    TargetDatabase    sysname NOT NULL,
    TargetSchema      sysname NOT NULL CONSTRAINT DF_ExtractJob_TargetSchema DEFAULT (N'stage'),
    TargetTable       sysname NOT NULL,
    CronExpression    varchar(100) NOT NULL,
    TimeZoneId        varchar(100) NOT NULL CONSTRAINT DF_ExtractJob_Tz DEFAULT ('Europe/Istanbul'),
    RetentionDays     int NULL CONSTRAINT CK_ExtractJob_Retention CHECK (RetentionDays IS NULL OR RetentionDays > 0),
    CommandTimeoutSec int NOT NULL CONSTRAINT DF_ExtractJob_Timeout DEFAULT (3600),
    BatchSize         int NOT NULL CONSTRAINT DF_ExtractJob_BatchSize DEFAULT (10000),
    IsActive          bit NOT NULL CONSTRAINT DF_ExtractJob_IsActive DEFAULT (1),
    CreatedBy         nvarchar(256) NULL,
    ModifiedBy        nvarchar(256) NULL,
    ModifiedAtUtc     datetime2(3) NOT NULL CONSTRAINT DF_ExtractJob_Modified DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_ExtractJob_TableDef CHECK (SourceType <> 1 OR SourceTable IS NOT NULL),
    CONSTRAINT CK_ExtractJob_QueryDef CHECK (SourceType <> 2 OR SourceQuery IS NOT NULL),
    CONSTRAINT CK_ExtractJob_Watermark CHECK (LoadMode <> 3 OR WatermarkColumn IS NOT NULL)
);
GO

IF OBJECT_ID(N'etl.ExtractState', N'U') IS NULL
CREATE TABLE etl.ExtractState
(
    ExtractJobId   int NOT NULL CONSTRAINT PK_ExtractState PRIMARY KEY
                   CONSTRAINT FK_ExtractState_Job REFERENCES etl.ExtractJob(ExtractJobId) ON DELETE CASCADE,
    WatermarkType  varchar(50)   NULL,
    WatermarkValue nvarchar(100) NULL,
    UpdatedAtUtc   datetime2(3)  NOT NULL CONSTRAINT DF_ExtractState_Upd DEFAULT (SYSUTCDATETIME())
);
GO

/* LoadRunId = _LoadId in target tables (monotonically increasing) */
IF OBJECT_ID(N'etl.LoadRun', N'U') IS NULL
CREATE TABLE etl.LoadRun
(
    LoadRunId      bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_LoadRun PRIMARY KEY,
    ExtractJobId   int NOT NULL,
    TargetServerId int NOT NULL,
    TargetDatabase sysname NOT NULL,
    TargetSchema   sysname NOT NULL,
    TargetTable    sysname NOT NULL,
    Status         varchar(20) NOT NULL,   -- Running | Succeeded | Failed | CleanupPending | Abandoned
    TriggeredBy    nvarchar(256) NULL,
    StartedAtUtc   datetime2(3) NOT NULL CONSTRAINT DF_LoadRun_Start DEFAULT (SYSUTCDATETIME()),
    FinishedAtUtc  datetime2(3) NULL,
    RowsLoaded     bigint NULL,
    WatermarkFrom  nvarchar(100) NULL,
    WatermarkTo    nvarchar(100) NULL,
    HostName       nvarchar(128) NULL,
    ErrorMessage   nvarchar(max) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LoadRun_Target_Status' AND object_id = OBJECT_ID(N'etl.LoadRun'))
    CREATE INDEX IX_LoadRun_Target_Status ON etl.LoadRun (TargetServerId, TargetDatabase, TargetSchema, TargetTable, Status) INCLUDE (LoadRunId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LoadRun_Job' AND object_id = OBJECT_ID(N'etl.LoadRun'))
    CREATE INDEX IX_LoadRun_Job ON etl.LoadRun (ExtractJobId, LoadRunId DESC) INCLUDE (Status, StartedAtUtc, FinishedAtUtc, RowsLoaded);
GO

/* ---------------- CSV -> GCS export job definitions ----------------
   Source: usually the target table(s) of extract jobs (SourceServerId/SourceDatabase).
   ExportMode : 1 = Full        (whole table)
                2 = Incremental (loads arrived since the last successful export, by _LoadId)
                3 = Query       (free SQL; parameters @RunDate (date, job TZ) and @RunAtUtc)
                4 = AllTables   (every extract target table in SourceDatabase [optionally SourceSchema],
                                 one CSV set per table, incremental per table -> "what was collected today")
   ObjectNameTemplate tokens: {Name} {Database} {Schema} {Table} {Date} {DateCompact} {Time} {Part}
   QuoteMode : 0 = when needed, 1 = all text fields, 2 = all fields                */
IF OBJECT_ID(N'etl.ExportJob', N'U') IS NULL
CREATE TABLE etl.ExportJob
(
    ExportJobId             int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ExportJob PRIMARY KEY,
    Name                    nvarchar(200) NOT NULL CONSTRAINT UQ_ExportJob_Name UNIQUE,
    ExportMode              tinyint NOT NULL CONSTRAINT CK_ExportJob_Mode CHECK (ExportMode IN (1,2,3,4)),
    SourceServerId          int NOT NULL CONSTRAINT FK_ExportJob_Server REFERENCES etl.SqlServer(SqlServerId),
    SourceDatabase          sysname NOT NULL,
    SourceSchema            sysname NULL CONSTRAINT DF_ExportJob_SrcSchema DEFAULT (N'stage'),
    SourceTable             sysname NULL,
    SourceQuery             nvarchar(max) NULL,
    ColumnList              nvarchar(max) NULL,
    IncludeTechnicalColumns bit NOT NULL CONSTRAINT DF_ExportJob_Tech DEFAULT (0),
    CronExpression          varchar(100) NOT NULL,
    TimeZoneId              varchar(100) NOT NULL CONSTRAINT DF_ExportJob_Tz DEFAULT ('Europe/Istanbul'),
    Bucket                  nvarchar(222) NOT NULL,
    ObjectNameTemplate      nvarchar(400) NOT NULL CONSTRAINT DF_ExportJob_Tpl
                            DEFAULT (N'exports/{Name}/dt={Date}/{Name}_{DateCompact}_{Time}_{Part}.csv'),
    Delimiter               nvarchar(5) NOT NULL CONSTRAINT DF_ExportJob_Delim DEFAULT (N','),
    QuoteMode               tinyint NOT NULL CONSTRAINT DF_ExportJob_Quote DEFAULT (0),
    NullValue               nvarchar(20) NOT NULL CONSTRAINT DF_ExportJob_Null DEFAULT (N''),
    LineEnding              varchar(4) NOT NULL CONSTRAINT DF_ExportJob_Eol DEFAULT ('LF'),
    Utf8Bom                 bit NOT NULL CONSTRAINT DF_ExportJob_Bom DEFAULT (0),
    Gzip                    bit NOT NULL CONSTRAINT DF_ExportJob_Gzip DEFAULT (0),   -- uploaded files; local archive is always gzip
    MaxRowsPerFile          int NULL,
    UploadWhenEmpty         bit NOT NULL CONSTRAINT DF_ExportJob_Empty DEFAULT (1),
    WriteManifest           bit NOT NULL CONSTRAINT DF_ExportJob_Manifest DEFAULT (1),
    CommandTimeoutSec       int NOT NULL CONSTRAINT DF_ExportJob_Timeout DEFAULT (3600),
    IsActive                bit NOT NULL CONSTRAINT DF_ExportJob_IsActive DEFAULT (1),
    CreatedBy               nvarchar(256) NULL,
    ModifiedBy              nvarchar(256) NULL,
    ModifiedAtUtc           datetime2(3) NOT NULL CONSTRAINT DF_ExportJob_Modified DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_ExportJob_TableDef CHECK (ExportMode IN (3,4) OR SourceTable IS NOT NULL),
    CONSTRAINT CK_ExportJob_QueryDef CHECK (ExportMode <> 3 OR SourceQuery IS NOT NULL),
    CONSTRAINT CK_ExportJob_Eol CHECK (LineEnding IN ('LF','CRLF')),
    CONSTRAINT CK_ExportJob_QuoteMode CHECK (QuoteMode IN (0,1,2))
);
GO

IF OBJECT_ID(N'etl.ExportState', N'U') IS NULL
CREATE TABLE etl.ExportState
(
    ExportJobId        int NOT NULL CONSTRAINT PK_ExportState PRIMARY KEY
                       CONSTRAINT FK_ExportState_Job REFERENCES etl.ExportJob(ExportJobId) ON DELETE CASCADE,
    LastExportedLoadId bigint NOT NULL CONSTRAINT DF_ExportState_Last DEFAULT (0),
    UpdatedAtUtc       datetime2(3) NOT NULL CONSTRAINT DF_ExportState_Upd DEFAULT (SYSUTCDATETIME())
);
GO

/* Upgrade: AllTables mode (4) for databases created by older versions */
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ExportJob_Mode' AND definition NOT LIKE N'%4%')
BEGIN
    ALTER TABLE etl.ExportJob DROP CONSTRAINT CK_ExportJob_Mode;
    ALTER TABLE etl.ExportJob ADD CONSTRAINT CK_ExportJob_Mode CHECK (ExportMode IN (1,2,3,4));
END
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ExportJob_TableDef' AND definition NOT LIKE N'%4%')
BEGIN
    ALTER TABLE etl.ExportJob DROP CONSTRAINT CK_ExportJob_TableDef;
    ALTER TABLE etl.ExportJob ADD CONSTRAINT CK_ExportJob_TableDef CHECK (ExportMode IN (3,4) OR SourceTable IS NOT NULL);
END
GO

/* Per-table export watermark for AllTables mode */
IF OBJECT_ID(N'etl.ExportTableState', N'U') IS NULL
CREATE TABLE etl.ExportTableState
(
    ExportJobId        int NOT NULL CONSTRAINT FK_ExportTableState_Job REFERENCES etl.ExportJob(ExportJobId) ON DELETE CASCADE,
    SchemaName         sysname NOT NULL,
    TableName          sysname NOT NULL,
    LastExportedLoadId bigint NOT NULL CONSTRAINT DF_ExportTableState_Last DEFAULT (0),
    UpdatedAtUtc       datetime2(3) NOT NULL CONSTRAINT DF_ExportTableState_Upd DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_ExportTableState PRIMARY KEY (ExportJobId, SchemaName, TableName)
);
GO

IF OBJECT_ID(N'etl.ExportRun', N'U') IS NULL
CREATE TABLE etl.ExportRun
(
    ExportRunId    bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ExportRun PRIMARY KEY,
    ExportJobId    int NOT NULL,
    Status         varchar(20) NOT NULL,   -- Running | Succeeded | PartiallyFailed | Failed | Skipped | Abandoned
    TriggeredBy    nvarchar(256) NULL,
    StartedAtUtc   datetime2(3) NOT NULL CONSTRAINT DF_ExportRun_Start DEFAULT (SYSUTCDATETIME()),
    FinishedAtUtc  datetime2(3) NULL,
    RowsExported   bigint NULL,
    FileCount      int NULL,
    BytesUploaded  bigint NULL,
    LoadIdFrom     bigint NULL,
    LoadIdTo       bigint NULL,
    ObjectNames    nvarchar(max) NULL,
    HostName       nvarchar(128) NULL,
    ErrorMessage   nvarchar(max) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExportRun_Job' AND object_id = OBJECT_ID(N'etl.ExportRun'))
    CREATE INDEX IX_ExportRun_Job ON etl.ExportRun (ExportJobId, ExportRunId DESC) INCLUDE (Status, StartedAtUtc, FinishedAtUtc, RowsExported);
GO

/* ---------------- Audit ---------------- */
IF OBJECT_ID(N'etl.AuditLog', N'U') IS NULL
CREATE TABLE etl.AuditLog
(
    AuditLogId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
    AtUtc      datetime2(3) NOT NULL CONSTRAINT DF_AuditLog_At DEFAULT (SYSUTCDATETIME()),
    UserName   nvarchar(256) NOT NULL,
    Action     varchar(50) NOT NULL,
    Entity     varchar(50) NOT NULL,
    EntityId   nvarchar(50) NULL,
    Details    nvarchar(max) NULL
);
GO

/* ---------------- System settings ----------------
   Policies edited by Admins on the Settings page (e.g. Retention.MaxDays). No row = the value from the
   process configuration (appsettings) is used; a row with Value NULL = explicitly "none". */
IF OBJECT_ID(N'etl.SystemSetting', N'U') IS NULL
CREATE TABLE etl.SystemSetting
(
    Name          varchar(100) NOT NULL CONSTRAINT PK_SystemSetting PRIMARY KEY,
    Value         nvarchar(max) NULL,
    ModifiedBy    nvarchar(256) NOT NULL,
    ModifiedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_SystemSetting_Mod DEFAULT (SYSUTCDATETIME())
);
GO
-- upgrade: Value was nvarchar(400) – too small for encrypted secrets (GCS key)
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'etl.SystemSetting') AND name = N'Value' AND max_length <> -1)
    ALTER TABLE etl.SystemSetting ALTER COLUMN Value nvarchar(max) NULL;
GO

/* ---------------- Alerts ----------------
   AlertChannel: e-mail / Microsoft Teams / generic webhook destinations with their rules (Admin, Alerts page).
   Statuses: comma list of Failed, PartiallyFailed, Abandoned, CleanupPending, Recovered, Missed, NoServer.
   AlertLog: one row per (event, destination); prevents duplicates, holds retries and suppressed (throttled) events.
   ChannelKey: 'C<id>' = channel, 'J' = the job's own NotifyEmails. */
IF COL_LENGTH(N'etl.ExtractJob', N'NotifyEmails') IS NULL ALTER TABLE etl.ExtractJob ADD NotifyEmails nvarchar(1000) NULL;
IF COL_LENGTH(N'etl.ExportJob', N'NotifyEmails') IS NULL ALTER TABLE etl.ExportJob ADD NotifyEmails nvarchar(1000) NULL;
-- Replace: 1 = rebuild the table from the source schema and swap it in (instead of deleting previous loads)
IF COL_LENGTH(N'etl.ExtractJob', N'ReplaceRebuild') IS NULL ALTER TABLE etl.ExtractJob ADD ReplaceRebuild bit NOT NULL CONSTRAINT DF_ExtractJob_ReplaceRebuild DEFAULT (0);
GO

/* ---------------- Job dependencies ----------------
   TriggerMode: 1 = on its cron schedule, 2 = after its upstream jobs (etl.JobDependency) have all succeeded.
   JobTrigger.LastTriggeredAtUtc: when a dependency-triggered job was last enqueued (each upstream needs a newer success). */
IF COL_LENGTH(N'etl.ExtractJob', N'TriggerMode') IS NULL ALTER TABLE etl.ExtractJob ADD TriggerMode tinyint NOT NULL CONSTRAINT DF_ExtractJob_TriggerMode DEFAULT (1);
IF COL_LENGTH(N'etl.ExportJob', N'TriggerMode') IS NULL ALTER TABLE etl.ExportJob ADD TriggerMode tinyint NOT NULL CONSTRAINT DF_ExportJob_TriggerMode DEFAULT (1);
GO
IF OBJECT_ID(N'etl.JobDependency', N'U') IS NULL
CREATE TABLE etl.JobDependency
(
    JobDependencyId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_JobDependency PRIMARY KEY,
    JobKind         varchar(10) NOT NULL CONSTRAINT CK_JobDependency_Kind CHECK (JobKind IN ('Extract','Export')),
    JobId           int NOT NULL,
    UpstreamKind    varchar(10) NOT NULL CONSTRAINT CK_JobDependency_UpKind CHECK (UpstreamKind IN ('Extract','Export')),
    UpstreamId      int NOT NULL,
    CreatedBy       nvarchar(256) NOT NULL,
    CreatedAtUtc    datetime2(3) NOT NULL CONSTRAINT DF_JobDependency_Created DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_JobDependency UNIQUE (JobKind, JobId, UpstreamKind, UpstreamId)
);
GO
IF OBJECT_ID(N'etl.JobTrigger', N'U') IS NULL
CREATE TABLE etl.JobTrigger
(
    JobKind            varchar(10) NOT NULL,
    JobId              int NOT NULL,
    LastTriggeredAtUtc datetime2(3) NOT NULL,
    CONSTRAINT PK_JobTrigger PRIMARY KEY (JobKind, JobId)
);
GO
IF OBJECT_ID(N'etl.AlertChannel', N'U') IS NULL
CREATE TABLE etl.AlertChannel
(
    AlertChannelId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AlertChannel PRIMARY KEY,
    Name           nvarchar(100) NOT NULL CONSTRAINT UQ_AlertChannel_Name UNIQUE,
    ChannelType    varchar(20) NOT NULL CONSTRAINT CK_AlertChannel_Type CHECK (ChannelType IN ('Email','Teams','Webhook')),
    Target         nvarchar(2000) NOT NULL,   -- e-mail addresses (; separated) or the webhook URL (a secret: shown masked)
    Statuses       varchar(200) NOT NULL CONSTRAINT DF_AlertChannel_Statuses DEFAULT ('Failed,PartiallyFailed,Abandoned,CleanupPending,Missed,NoServer'),
    JobKinds       varchar(30) NOT NULL CONSTRAINT DF_AlertChannel_Kinds DEFAULT ('Extract,Export'),
    JobNameFilter  nvarchar(200) NULL,        -- wildcard, e.g. erp_*; NULL = all jobs
    IsActive       bit NOT NULL CONSTRAINT DF_AlertChannel_Active DEFAULT (1),
    ModifiedBy     nvarchar(256) NOT NULL,
    ModifiedAtUtc  datetime2(3) NOT NULL CONSTRAINT DF_AlertChannel_Mod DEFAULT (SYSUTCDATETIME())
);
GO
IF OBJECT_ID(N'etl.AlertLog', N'U') IS NULL
CREATE TABLE etl.AlertLog
(
    AlertLogId    bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AlertLog PRIMARY KEY,
    EventType     varchar(30) NOT NULL,
    JobKind       varchar(10) NOT NULL,       -- Extract | Export | System
    JobId         int NOT NULL,
    JobName       nvarchar(200) NOT NULL,
    RunId         bigint NOT NULL,            -- run id; Missed: occurrence (unix minutes); NoServer: time bucket
    ChannelKey    varchar(20) NOT NULL,
    Destination   nvarchar(200) NOT NULL,     -- channel name or "job recipients"
    Status        varchar(20) NOT NULL,       -- Sent | Failed | Suppressed
    Attempts      int NOT NULL CONSTRAINT DF_AlertLog_Attempts DEFAULT (0),
    LastError     nvarchar(max) NULL,
    CreatedAtUtc  datetime2(3) NOT NULL CONSTRAINT DF_AlertLog_Created DEFAULT (SYSUTCDATETIME()),
    SentAtUtc     datetime2(3) NULL,
    CONSTRAINT UQ_AlertLog_Event UNIQUE (EventType, JobKind, JobId, RunId, ChannelKey)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AlertLog_Throttle' AND object_id = OBJECT_ID(N'etl.AlertLog'))
    CREATE INDEX IX_AlertLog_Throttle ON etl.AlertLog (JobKind, JobId, EventType, ChannelKey, SentAtUtc);
GO

/* ---------------- Permission checks ----------------
   Latest result of the service-account permission check per process (ProcessRole + Queue + HostName).
   Written by the Web ("Check permissions" on the Servers page) and by Workers (queue job / startup self-check).
   Status: Ok | Missing | Excess | Warning | Error */
IF OBJECT_ID(N'etl.PermissionCheck', N'U') IS NULL
CREATE TABLE etl.PermissionCheck
(
    PermissionCheckId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PermissionCheck PRIMARY KEY,
    CheckedAtUtc      datetime2(3) NOT NULL CONSTRAINT DF_PermissionCheck_At DEFAULT (SYSUTCDATETIME()),
    ProcessRole       varchar(20) NOT NULL,     -- Web | Worker
    Queue             varchar(30) NULL,         -- Worker: queue the check ran for
    HostName          nvarchar(128) NOT NULL,
    ProcessAccount    nvarchar(256) NOT NULL,   -- Windows account of the process
    Category          varchar(20) NOT NULL,     -- Metadata | Hangfire | Target | Source
    ServerName        nvarchar(128) NOT NULL,
    DatabaseName      sysname NULL,
    LoginName         nvarchar(256) NULL,       -- SQL login as seen by the server (SUSER_SNAME)
    Item              nvarchar(200) NOT NULL,
    Status            varchar(10) NOT NULL,
    Detail            nvarchar(max) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PermissionCheck_Process' AND object_id = OBJECT_ID(N'etl.PermissionCheck'))
    CREATE INDEX IX_PermissionCheck_Process ON etl.PermissionCheck (ProcessRole, HostName, Queue);
GO

/* ---------------- Monitoring view ---------------- */
CREATE OR ALTER VIEW etl.vJobStatus
AS
SELECT 'Extract' AS JobKind, j.ExtractJobId AS JobId, j.Name, j.IsActive, j.CronExpression,
       r.Status AS LastStatus, r.StartedAtUtc, r.FinishedAtUtc,
       DATEDIFF(SECOND, r.StartedAtUtc, r.FinishedAtUtc) AS DurationSec,
       r.RowsLoaded AS [Rows], r.ErrorMessage
FROM etl.ExtractJob j
OUTER APPLY (SELECT TOP (1) * FROM etl.LoadRun lr WHERE lr.ExtractJobId = j.ExtractJobId ORDER BY lr.LoadRunId DESC) r
UNION ALL
SELECT 'Export', e.ExportJobId, e.Name, e.IsActive, e.CronExpression,
       r.Status, r.StartedAtUtc, r.FinishedAtUtc,
       DATEDIFF(SECOND, r.StartedAtUtc, r.FinishedAtUtc),
       r.RowsExported, r.ErrorMessage
FROM etl.ExportJob e
OUTER APPLY (SELECT TOP (1) * FROM etl.ExportRun er WHERE er.ExportJobId = e.ExportJobId ORDER BY er.ExportRunId DESC) r;
GO

/* ---------------- Merge (upsert) load mode ----------------
   LoadMode 4 = Merge: KeyColumns = source column names identifying a row (comma separated),
   MergeDeleteMissing = delete target rows whose key is missing from a full read (not with a watermark).
   LoadRun.RowsInserted/Updated/Deleted: what a merge changed (RowsLoaded = rows read). */
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ExtractJob_LoadMode' AND definition NOT LIKE N'%4%')
BEGIN
    ALTER TABLE etl.ExtractJob DROP CONSTRAINT CK_ExtractJob_LoadMode;
    ALTER TABLE etl.ExtractJob ADD CONSTRAINT CK_ExtractJob_LoadMode CHECK (LoadMode IN (1,2,3,4));
END
IF COL_LENGTH(N'etl.ExtractJob', N'KeyColumns') IS NULL ALTER TABLE etl.ExtractJob ADD KeyColumns nvarchar(1000) NULL;
IF COL_LENGTH(N'etl.ExtractJob', N'MergeDeleteMissing') IS NULL ALTER TABLE etl.ExtractJob ADD MergeDeleteMissing bit NOT NULL CONSTRAINT DF_ExtractJob_MergeDeleteMissing DEFAULT (0);
IF COL_LENGTH(N'etl.LoadRun', N'RowsInserted') IS NULL ALTER TABLE etl.LoadRun ADD RowsInserted bigint NULL, RowsUpdated bigint NULL, RowsDeleted bigint NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ExtractJob_MergeKey')
    ALTER TABLE etl.ExtractJob ADD CONSTRAINT CK_ExtractJob_MergeKey CHECK (LoadMode <> 4 OR KeyColumns IS NOT NULL);
GO

/* ---------------- Data protection: masking rules ----------------
   ColumnPattern: column name patterns (; separated, * ? wildcards); ScopePattern: "Database.Schema.Table" pattern.
   Method: 1 = Hash (HMAC-SHA256 with the Secrets:Key derived key), 2 = Partial (KeepLast visible), 3 = Email, 4 = Redact, 5 = Null.
   AppliesTo: 1 = exported files only, 2 = already when collecting (the DWH holds masked values) and therefore in exports.
   The export guard mode is etl.SystemSetting 'Privacy.ExportGuard' (Off / Warn / Block). */
IF OBJECT_ID(N'etl.MaskingRule', N'U') IS NULL
CREATE TABLE etl.MaskingRule (
    MaskingRuleId  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MaskingRule PRIMARY KEY,
    Name           nvarchar(100) NOT NULL CONSTRAINT UQ_MaskingRule_Name UNIQUE,
    ColumnPattern  nvarchar(400) NOT NULL,
    ScopePattern   nvarchar(400) NOT NULL CONSTRAINT DF_MaskingRule_Scope DEFAULT (N'*'),
    Method         tinyint NOT NULL CONSTRAINT CK_MaskingRule_Method CHECK (Method BETWEEN 1 AND 5),
    KeepLast       int NOT NULL CONSTRAINT DF_MaskingRule_KeepLast DEFAULT (4),
    AppliesTo      tinyint NOT NULL CONSTRAINT CK_MaskingRule_AppliesTo CHECK (AppliesTo IN (1,2)),
    IsActive       bit NOT NULL CONSTRAINT DF_MaskingRule_Active DEFAULT (1),
    ModifiedBy     nvarchar(256) NOT NULL,
    ModifiedAtUtc  datetime2(3) NOT NULL CONSTRAINT DF_MaskingRule_Modified DEFAULT (SYSUTCDATETIME())
);
GO

/* ---------------- Export file format ----------------
   FileFormat: 1 = CSV (Delimiter, QuoteMode, ... apply), 2 = Parquet (typed columns, compressed inside the file;
   Gzip/Delimiter/QuoteMode/NullValue/LineEnding/Utf8Bom do not apply). ParquetCompression: Snappy, Zstd, Gzip, None. */
IF COL_LENGTH(N'etl.ExportJob', N'FileFormat') IS NULL
    ALTER TABLE etl.ExportJob ADD FileFormat tinyint NOT NULL CONSTRAINT DF_ExportJob_FileFormat DEFAULT (1)
        CONSTRAINT CK_ExportJob_FileFormat CHECK (FileFormat IN (1,2));
IF COL_LENGTH(N'etl.ExportJob', N'ParquetCompression') IS NULL
    ALTER TABLE etl.ExportJob ADD ParquetCompression varchar(10) NOT NULL CONSTRAINT DF_ExportJob_ParquetCompression DEFAULT ('Snappy')
        CONSTRAINT CK_ExportJob_ParquetCompression CHECK (ParquetCompression IN ('Snappy','Zstd','Gzip','None'));
GO
