/* =====================================================================
   Demo source #2: CRM (Docker SQL Server "src-crm")
   Idempotent: drops and recreates the CRM database.
   ===================================================================== */
SET NOCOUNT ON;
IF DB_ID('CRM') IS NOT NULL BEGIN ALTER DATABASE CRM SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE CRM; END
GO
CREATE DATABASE CRM;
GO
USE CRM;
GO
CREATE SCHEMA sales;
GO
CREATE TABLE sales.Contacts (
    ContactId    int NOT NULL PRIMARY KEY,
    FullName     nvarchar(150) NOT NULL,
    Email        varchar(200) NULL,
    Phone        varchar(30) NULL,
    Segment      nvarchar(30) NULL,
    Score        real NULL,
    Profile      xml NULL,
    UpdatedAt    datetime2(3) NOT NULL);

CREATE TABLE sales.Activities (
    ActivityId   bigint IDENTITY(1,1) PRIMARY KEY,
    ContactId    int NOT NULL,
    Kind         varchar(20) NOT NULL,
    Subject      nvarchar(300) NULL,
    HappenedAt   datetimeoffset(3) NOT NULL,
    DurationSec  int NULL,
    Attachment   varbinary(max) NULL,
    Rv           rowversion);
GO
;WITH n AS (SELECT TOP (20000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT sales.Contacts
SELECT i,
       CASE WHEN i % 40 = 0 THEN CONCAT(N'Ayşe "Ece" Yılmaz, ', i) ELSE CONCAT(N'Kişi ', i) END,
       CASE WHEN i % 11 = 0 THEN NULL ELSE CONCAT('user', i, '@example.com') END,
       CONCAT('+90 5', RIGHT(CONCAT('000000000', i * 31), 9)),
       CASE i % 4 WHEN 0 THEN N'Kurumsal' WHEN 1 THEN N'Bireysel' WHEN 2 THEN N'KOBİ' END,
       CASE WHEN i % 6 = 0 THEN NULL ELSE (i % 1000) / 10.0 END,
       CASE WHEN i % 100 = 0 THEN CAST(CONCAT('<p><tags>a,b</tags><n>', i, '</n></p>') AS xml) END,
       DATEADD(SECOND, i * 61, '2025-06-01')
FROM n;

;WITH n AS (SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c)
INSERT sales.Activities (ContactId, Kind, Subject, HappenedAt, DurationSec, Attachment)
SELECT 1 + i % 20000,
       CASE i % 3 WHEN 0 THEN 'CALL' WHEN 1 THEN 'MAIL' ELSE 'MEETING' END,
       CASE WHEN i % 19 = 0 THEN CONCAT(N'Görüşme', NCHAR(13), NCHAR(10), N'notu; "önemli", ', i) ELSE CONCAT(N'Aktivite ', i) END,
       TODATETIMEOFFSET(DATEADD(SECOND, i * 29, '2025-06-01'), CASE WHEN i % 2 = 0 THEN '+03:00' ELSE '+01:00' END),
       CASE WHEN i % 3 = 0 THEN i % 3600 END,
       CASE WHEN i % 500 = 0 THEN CAST(REPLICATE(CAST('x' AS varchar(max)), 5000) AS varbinary(max)) END
FROM n;
GO
CREATE VIEW sales.vw_ActiveContacts AS
SELECT c.ContactId, c.FullName, c.Email, c.Segment, MAX(a.HappenedAt) AS LastActivity, COUNT(*) AS Activities
FROM sales.Contacts c JOIN sales.Activities a ON a.ContactId = c.ContactId
GROUP BY c.ContactId, c.FullName, c.Email, c.Segment;
GO
/* EXEC sales.usp_SimulateActivity @NewActivities = 200 */
CREATE PROCEDURE sales.usp_SimulateActivity @NewActivities int = 200 AS
BEGIN
    SET NOCOUNT ON;
    ;WITH n AS (SELECT TOP (@NewActivities) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
    INSERT sales.Activities (ContactId, Kind, Subject, HappenedAt, DurationSec)
    SELECT 1 + ABS(CHECKSUM(NEWID())) % 20000, 'CALL', CONCAT(N'yeni aktivite ', i), SYSDATETIMEOFFSET(), i FROM n;
    UPDATE TOP (@NewActivities / 4) sales.Contacts SET Score = ISNULL(Score, 0) + 1, UpdatedAt = SYSDATETIME();
END
GO
SELECT 'CRM' AS db, (SELECT COUNT(*) FROM sales.Contacts) contacts, (SELECT COUNT(*) FROM sales.Activities) activities;
GO
/* completion marker checked by the src-seed service */
USE CRM;
CREATE TABLE dbo.__SeedInfo (SeededAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
INSERT dbo.__SeedInfo DEFAULT VALUES;
GO
