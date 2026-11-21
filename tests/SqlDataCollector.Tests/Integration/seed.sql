/* Test databases: metadata, target (DWH) and a source database with tricky data. */
CREATE DATABASE DataCollectorMeta;
GO
CREATE DATABASE DWH;
GO
CREATE DATABASE SrcDb COLLATE Turkish_100_CI_AS;
GO
USE SrcDb;
GO
CREATE TABLE dbo.Orders(
  OrderId int IDENTITY PRIMARY KEY,
  CustomerName nvarchar(100) NULL,
  Note nvarchar(max) NULL,
  Amount decimal(38,10) NULL,
  Price money NULL,
  Ratio float NULL,
  IsPaid bit NULL,
  CreatedAt datetime NOT NULL,
  ModifiedAt datetime2(7) NOT NULL,
  ShippedAt datetimeoffset(7) NULL,
  OrderDate date NULL,
  OrderTime time(7) NULL,
  Payload varbinary(50) NULL,
  RowGuid uniqueidentifier NOT NULL DEFAULT NEWID(),
  Xmldata xml NULL,
  Rv rowversion);
INSERT dbo.Orders(CustomerName, Note, Amount, Price, Ratio, IsPaid, CreatedAt, ModifiedAt, ShippedAt, OrderDate, OrderTime, Payload, Xmldata) VALUES
 (N'Şükrü Öztürk', N'a,b "quoted"', 1234567890123456789012345678.1234567890, 12.3456, 0.1, 1, '2026-11-20 10:00:00.003', '2026-11-20 10:00:00.1234567', '2026-11-20 10:00:00 +03:00', '2026-11-20', '10:15:30.5', 0x0102FF, '<a>1</a>'),
 (N'Çağla İğdır', N'satır1' + NCHAR(13) + NCHAR(10) + N'satır2', -0.5, 0, 1e-10, 0, '2026-11-20 11:00:00', '2026-11-20 11:00:00', NULL, NULL, '00:00:00', NULL, NULL),
 (N'', NULL, NULL, NULL, NULL, NULL, '2026-11-20 12:00:00', '2026-11-20 12:00:00', NULL, NULL, NULL, NULL, NULL),
 (NULL, N'  spaced  ', 1, 1, 1, 1, '2026-11-20 13:00:00.997', '2026-11-20 13:00:00', NULL, '2026-01-01', NULL, 0x, NULL);
GO
CREATE TABLE dbo.Customers(CustomerId int PRIMARY KEY, Name nvarchar(50), City nvarchar(30));
INSERT dbo.Customers VALUES (1, N'Ahmet', N'İstanbul'), (2, N'Ayşe', N'Ankara'), (3, N'Mehmet', N'İzmir');
GO
CREATE PROCEDURE dbo.usp_CitySummary AS
BEGIN
  SET NOCOUNT ON;
  CREATE TABLE #t(City nvarchar(30), Cnt int);
  INSERT #t SELECT City, COUNT(*) FROM dbo.Customers GROUP BY City;
  SELECT City, Cnt, SYSDATETIME() AS SnapshotAt FROM #t;
END
GO
