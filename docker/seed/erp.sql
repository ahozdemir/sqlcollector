/* =====================================================================
   Demo source #1: ERP (Docker SQL Server "src-erp")
   Realistic volume + tricky values: Turkish text, commas/quotes/newlines,
   decimal(38,10), datetime .003/.997, rowversion, NULL vs empty string.
   Idempotent: drops and recreates the ERP database.
   ===================================================================== */
SET NOCOUNT ON;
IF DB_ID('ERP') IS NOT NULL BEGIN ALTER DATABASE ERP SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ERP; END
GO
CREATE DATABASE ERP COLLATE Turkish_100_CI_AS;
GO
USE ERP;
GO
CREATE TABLE dbo.Customers (
    CustomerId   int NOT NULL PRIMARY KEY,
    Name         nvarchar(100) NOT NULL,
    City         nvarchar(50) NULL,
    TaxNo        varchar(11) NULL,
    CreditLimit  decimal(18,2) NULL,
    IsActive     bit NOT NULL,
    CreatedAt    datetime NOT NULL,
    ModifiedAt   datetime2(3) NOT NULL);

CREATE TABLE dbo.Products (
    ProductId    int NOT NULL PRIMARY KEY,
    Sku          varchar(20) NOT NULL,
    Name         nvarchar(200) NOT NULL,
    UnitPrice    money NOT NULL,
    Weight       float NULL,
    Picture      varbinary(64) NULL);

CREATE TABLE dbo.Orders (
    OrderId      bigint NOT NULL PRIMARY KEY,
    CustomerId   int NOT NULL,
    OrderDate    date NOT NULL,
    Amount       decimal(38,10) NOT NULL,
    Currency     char(3) NOT NULL,
    Note         nvarchar(400) NULL,
    Status       varchar(10) NOT NULL,
    CreatedAt    datetime NOT NULL,
    ModifiedAt   datetime2(7) NOT NULL,
    ShippedAt    datetimeoffset(7) NULL,
    RowGuid      uniqueidentifier NOT NULL DEFAULT NEWID(),
    Rv           rowversion);
CREATE INDEX IX_Orders_ModifiedAt ON dbo.Orders (ModifiedAt);

CREATE TABLE dbo.OrderLines (
    OrderId      bigint NOT NULL,
    LineNumber   int NOT NULL,
    ProductId    int NOT NULL,
    Qty          decimal(18,3) NOT NULL,
    LineAmount   decimal(19,4) NOT NULL,
    CONSTRAINT PK_OrderLines PRIMARY KEY (OrderId, LineNumber));
GO

DECLARE @cities TABLE (i int IDENTITY(0,1), c nvarchar(50));
INSERT @cities (c) VALUES (N'İstanbul'),(N'Ankara'),(N'İzmir'),(N'Bursa'),(N'Şanlıurfa'),(N'Çanakkale'),(N'Muğla'),(N'Iğdır'),(N'Podgorica'),(NULL);

;WITH n AS (SELECT TOP (5000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.Customers
SELECT i,
       CASE i % 50 WHEN 0 THEN CONCAT(N'Öztürk, Şükrü "VIP" ', i) WHEN 1 THEN N'' ELSE CONCAT(N'Müşteri ', i) END,
       (SELECT c FROM @cities WHERE i = n.i % 10),
       CASE WHEN i % 13 = 0 THEN NULL ELSE RIGHT(CONCAT('0000000000', i * 7919), 10) END,
       (i % 1000) * 125.50, CASE WHEN i % 17 = 0 THEN 0 ELSE 1 END,
       DATEADD(SECOND, i * 37, '2025-01-01'), DATEADD(SECOND, i * 37, '2025-01-01')
FROM n;

;WITH n AS (SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
INSERT dbo.Products
SELECT i, CONCAT('SKU-', RIGHT(CONCAT('00000', i), 5)),
       CASE WHEN i % 25 = 0 THEN CONCAT(N'Ürün, çok satırlı', NCHAR(13), NCHAR(10), N'açıklama ', i) ELSE CONCAT(N'Ürün ', i) END,
       (i % 300) + 0.99, CASE WHEN i % 9 = 0 THEN NULL ELSE i / 3.0 END,
       CASE WHEN i % 4 = 0 THEN CAST(CONCAT('img', i) AS varbinary(64)) END
FROM n;

;WITH n AS (SELECT TOP (200000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c)
INSERT dbo.Orders (OrderId, CustomerId, OrderDate, Amount, Currency, Note, Status, CreatedAt, ModifiedAt, ShippedAt)
SELECT i, 1 + i % 5000, DATEADD(DAY, i % 365, '2025-11-01'),
       CASE WHEN i = 1 THEN 1234567890123456789012345678.1234567890 ELSE (i % 100000) / 7.0 END,
       CASE i % 3 WHEN 0 THEN 'TRY' WHEN 1 THEN 'EUR' ELSE 'USD' END,
       CASE WHEN i % 10 = 0 THEN N'not, virgüllü "tırnaklı"'
            WHEN i % 17 = 0 THEN CONCAT(N'satır1', NCHAR(10), N'satır2')
            WHEN i % 7 = 0 THEN NULL
            WHEN i % 23 = 0 THEN N''
            ELSE CONCAT(N'sipariş ', i) END,
       CASE i % 5 WHEN 0 THEN 'CANCELLED' WHEN 1 THEN 'NEW' ELSE 'SHIPPED' END,
       DATEADD(MILLISECOND, (i % 1000) * 3, DATEADD(SECOND, i * 13, '2025-11-01')),   -- datetime .003/.997 values
       DATEADD(SECOND, i * 13, '2025-11-01'),
       CASE WHEN i % 5 IN (2,3,4) THEN TODATETIMEOFFSET(DATEADD(SECOND, i * 13 + 86400, '2025-11-01'), '+03:00') END
FROM n;

INSERT dbo.OrderLines
SELECT o.OrderId, l.n, 1 + (o.OrderId * l.n) % 500, l.n * 1.5, l.n * 1.5 * 10.99
FROM dbo.Orders o CROSS JOIN (VALUES (1),(2),(3)) l(n)
WHERE o.OrderId % 4 <> 0;
GO

/* Free-SQL source example: procedure with a temp table */
CREATE PROCEDURE dbo.usp_DailySales @Days int = 30 AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #d (OrderDate date, Currency char(3), Orders int, Amount decimal(38,10));
    INSERT #d SELECT OrderDate, Currency, COUNT(*), SUM(Amount) FROM dbo.Orders
    WHERE OrderDate >= DATEADD(DAY, -@Days, (SELECT MAX(OrderDate) FROM dbo.Orders)) GROUP BY OrderDate, Currency;
    SELECT OrderDate, Currency, Orders, Amount, SYSDATETIME() AS SnapshotAt FROM #d;
END
GO

/* Simulates new and changed orders, to test incremental loads:  EXEC dbo.usp_SimulateActivity @NewOrders = 100, @Updates = 50 */
CREATE PROCEDURE dbo.usp_SimulateActivity @NewOrders int = 100, @Updates int = 50 AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @max bigint = (SELECT MAX(OrderId) FROM dbo.Orders);
    ;WITH n AS (SELECT TOP (@NewOrders) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
    INSERT dbo.Orders (OrderId, CustomerId, OrderDate, Amount, Currency, Note, Status, CreatedAt, ModifiedAt)
    SELECT @max + i, 1 + (@max + i) % 5000, CAST(SYSDATETIME() AS date), i * 10.5, 'TRY', CONCAT(N'yeni sipariş ', @max + i), 'NEW',
           GETDATE(), SYSDATETIME() FROM n;
    UPDATE TOP (@Updates) dbo.Orders SET Status = 'SHIPPED', ModifiedAt = SYSDATETIME(), ShippedAt = SYSDATETIMEOFFSET()
    WHERE Status = 'NEW';
    SELECT @NewOrders AS Inserted, @@ROWCOUNT AS Updated;
END
GO
SELECT 'ERP' AS db, (SELECT COUNT(*) FROM dbo.Customers) customers, (SELECT COUNT(*) FROM dbo.Orders) orders, (SELECT COUNT(*) FROM dbo.OrderLines) lines;
GO
/* completion marker checked by the src-seed service */
USE ERP;
CREATE TABLE dbo.__SeedInfo (SeededAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
INSERT dbo.__SeedInfo DEFAULT VALUES;
GO
