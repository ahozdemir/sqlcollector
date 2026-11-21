/* =====================================================================
   SqlDataCollector – service account permission template (gMSA recommended)
   CORP\sdc-web$    : APP server (Roles=Web)
   CORP\sdc-worker$ : Worker(s) (Roles=Worker) – central and, optionally, on the target SQL Server host
   Replace with your own domain/account names.
   ===================================================================== */

/* ---------- 1) TARGET SQL Server: metadata DB (DataCollectorMeta) ---------- */
-- The app creates the schema on first start (Collector:AutoCreateSchema=true) -> needs db_ddladmin at that moment,
-- or the DBA runs src/SqlDataCollector/Sql/001_schema.sql and AutoCreateSchema=false is set.
USE [master];
IF SUSER_ID(N'CORP\sdc-web$')    IS NULL CREATE LOGIN [CORP\sdc-web$]    FROM WINDOWS;
IF SUSER_ID(N'CORP\sdc-worker$') IS NULL CREATE LOGIN [CORP\sdc-worker$] FROM WINDOWS;
GO
USE [DataCollectorMeta];
IF DATABASE_PRINCIPAL_ID(N'CORP\sdc-web$')    IS NULL CREATE USER [CORP\sdc-web$]    FOR LOGIN [CORP\sdc-web$];
IF DATABASE_PRINCIPAL_ID(N'CORP\sdc-worker$') IS NULL CREATE USER [CORP\sdc-worker$] FOR LOGIN [CORP\sdc-worker$];
ALTER ROLE db_datareader ADD MEMBER [CORP\sdc-web$];
ALTER ROLE db_datawriter ADD MEMBER [CORP\sdc-web$];
ALTER ROLE db_datareader ADD MEMBER [CORP\sdc-worker$];
ALTER ROLE db_datawriter ADD MEMBER [CORP\sdc-worker$];
-- If the app installs the schema (first start), temporarily:
-- ALTER ROLE db_ddladmin ADD MEMBER [CORP\sdc-worker$];
GO

/* ---------- 2) TARGET SQL Server: data DB (DWH) ---------- */
USE [DWH];
IF DATABASE_PRINCIPAL_ID(N'CORP\sdc-worker$') IS NULL CREATE USER [CORP\sdc-worker$] FOR LOGIN [CORP\sdc-worker$];
ALTER ROLE db_datareader ADD MEMBER [CORP\sdc-worker$];
ALTER ROLE db_datawriter ADD MEMBER [CORP\sdc-worker$];
ALTER ROLE db_ddladmin   ADD MEMBER [CORP\sdc-worker$];   -- CREATE/ALTER TABLE, TRUNCATE ... WITH (PARTITIONS)
GRANT ALTER ANY DATASPACE TO [CORP\sdc-worker$];         -- daily partitioning (CREATE/SPLIT/MERGE PARTITION)
-- APP server: read only, for schema lists / check target / preview in the UI
IF DATABASE_PRINCIPAL_ID(N'CORP\sdc-web$') IS NULL CREATE USER [CORP\sdc-web$] FOR LOGIN [CORP\sdc-web$];
ALTER ROLE db_datareader ADD MEMBER [CORP\sdc-web$];
GRANT VIEW DEFINITION TO [CORP\sdc-web$];
-- Export reader: every export read and export preview switches to this read-only user (EXECUTE AS USER ... WITH COOKIE),
-- so export SQL written by Editors cannot use the Worker's write/DDL rights. Name = Collector:ExportReadAsUser.
-- Required in EVERY database that exports read from; run it when the database is created (initial setup).
IF DATABASE_PRINCIPAL_ID(N'sdc_export_reader') IS NULL CREATE USER [sdc_export_reader] WITHOUT LOGIN;
ALTER ROLE db_datareader ADD MEMBER [sdc_export_reader];
GRANT IMPERSONATE ON USER::[sdc_export_reader] TO [CORP\sdc-worker$];   -- exports
GRANT IMPERSONATE ON USER::[sdc_export_reader] TO [CORP\sdc-web$];      -- export preview in the UI
GO

/* ---------- 3) On every SOURCE SQL Server, for every database to read ---------- */
-- USE [ERP];
-- CREATE USER [CORP\sdc-worker$] FOR LOGIN [CORP\sdc-worker$];  ALTER ROLE db_datareader ADD MEMBER [CORP\sdc-worker$];
-- CREATE USER [CORP\sdc-web$]    FOR LOGIN [CORP\sdc-web$];     ALTER ROLE db_datareader ADD MEMBER [CORP\sdc-web$];
-- For jobs that use stored procedures (EXEC ...): GRANT EXECUTE ON SCHEMA::dbo TO [CORP\sdc-worker$], [CORP\sdc-web$];
-- IMPORTANT: do NOT grant write permissions on sources. Users with the Editor role can write free SQL,
-- so this account's permissions are the security boundary on the source side.

/* ---------- 4) Central HANGFIRE DB ---------- */
-- The app generates the script (the DBA runs it once):
--   SqlDataCollector.exe hangfire-install-script hangfire_collector "CORP\sdc-web$" "CORP\sdc-worker$" > hangfire_install.sql
-- Result: hangfire_collector schema + SELECT/INSERT/UPDATE/DELETE on that schema only, for both accounts.
-- appsettings: Collector:HangfirePrepareSchema = false
