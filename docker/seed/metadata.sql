/* =====================================================================
   Test metadata: servers, sample extract jobs and the nightly all-tables export.
   Tokens are replaced by the caller (scripts/setup-local.ps1 or the docker meta-seed service):
     __ERP__  __CRM__  __DWH__   server addresses   (e.g. localhost,14331)
     __DWHDB__                    target database    (e.g. SdcDwh)
     __DWH_TRUST__                1 = TrustServerCertificate
     __ADMIN__                    principal granted Admin (e.g. CONTOSO\ahmet) or empty
   Idempotent (MERGE by name).
   ===================================================================== */
SET NOCOUNT ON;

MERGE etl.SqlServer AS t
USING (VALUES
    (N'ERP', N'__ERP__', 1, 0, 1, N'Docker demo source (src-erp)'),
    (N'CRM', N'__CRM__', 1, 0, 1, N'Docker demo source (src-crm)'),
    (N'DWH', N'__DWH__', 0, 1, __DWH_TRUST__, N'Target / metadata SQL Server')
) AS s (Name, ServerAddress, IsSource, IsTarget, Trust, Description)
ON t.Name = s.Name
WHEN MATCHED THEN UPDATE SET ServerAddress = s.ServerAddress, IsSource = s.IsSource, IsTarget = s.IsTarget,
    TrustServerCertificate = s.Trust, Description = s.Description, ModifiedBy = N'seed', ModifiedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (Name, ServerAddress, IsSource, IsTarget, TrustServerCertificate, Description, ModifiedBy)
    VALUES (s.Name, s.ServerAddress, s.IsSource, s.IsTarget, s.Trust, s.Description, N'seed');

DECLARE @erp int = (SELECT SqlServerId FROM etl.SqlServer WHERE Name = N'ERP');
DECLARE @crm int = (SELECT SqlServerId FROM etl.SqlServer WHERE Name = N'CRM');
DECLARE @dwh int = (SELECT SqlServerId FROM etl.SqlServer WHERE Name = N'DWH');

MERGE etl.ExtractJob AS t
USING (VALUES
 -- name,                      srv,  db,    type, schema,  table,        query,                                   mode, watermark,     target schema, target table,  cron,           retention, partition, policy
 (N'erp_orders_incremental',  @erp, N'ERP', 1, N'dbo',   N'Orders',     NULL,                                     3, N'ModifiedAt', N'erp', N'Orders',       '*/15 * * * *', 30, 1, 1),
 (N'erp_orderlines_append',   @erp, N'ERP', 1, N'dbo',   N'OrderLines', NULL,                                     1, NULL,          N'erp', N'OrderLines',   '0 0 * * *',    7,  1, 1),
 (N'erp_customers_replace',   @erp, N'ERP', 1, N'dbo',   N'Customers',  NULL,                                     2, NULL,          N'erp', N'Customers',    '30 0 * * *',   NULL, 0, 1),
 (N'erp_products_replace',    @erp, N'ERP', 1, N'dbo',   N'Products',   NULL,                                     2, NULL,          N'erp', N'Products',     '30 0 * * *',   NULL, 0, 1),
 (N'erp_daily_sales_proc',    @erp, N'ERP', 2, NULL,     NULL,          N'EXEC dbo.usp_DailySales @Days = 30',    1, NULL,          N'erp', N'DailySales',   '0 1 * * *',    90, 0, 1),
 (N'crm_contacts_replace',    @crm, N'CRM', 1, N'sales', N'Contacts',   NULL,                                     2, NULL,          N'crm', N'Contacts',     '0 0 * * *',    NULL, 0, 1),
 (N'crm_activities_rv',       @crm, N'CRM', 1, N'sales', N'Activities', NULL,                                     3, N'Rv',         N'crm', N'Activities',   '*/30 * * * *', 30, 1, 1),
 (N'crm_active_contacts_view',@crm, N'CRM', 1, N'sales', N'vw_ActiveContacts', NULL,                              1, NULL,          N'crm', N'ActiveContactsSnapshot', '0 2 * * *', 14, 1, 1)
) AS s (Name, Srv, Db, SType, SSchema, STable, SQuery, LMode, Wm, TSchema, TTable, Cron, Ret, Part, Policy)
ON t.Name = s.Name
WHEN NOT MATCHED THEN INSERT (Name, SourceServerId, SourceDatabase, SourceType, SourceSchema, SourceTable, SourceQuery, LoadMode, WatermarkColumn,
        SchemaPolicy, PartitionDaily, TargetServerId, TargetDatabase, TargetSchema, TargetTable, CronExpression, TimeZoneId, RetentionDays, CreatedBy, ModifiedBy)
    VALUES (s.Name, s.Srv, s.Db, s.SType, s.SSchema, s.STable, s.SQuery, s.LMode, s.Wm,
        s.Policy, s.Part, @dwh, N'__DWHDB__', s.TSchema, s.TTable, s.Cron, 'Europe/Istanbul', s.Ret, N'seed', N'seed');

IF NOT EXISTS (SELECT 1 FROM etl.ExportJob WHERE Name = N'dwh_nightly_all_tables')
    INSERT etl.ExportJob (Name, ExportMode, SourceServerId, SourceDatabase, SourceSchema, CronExpression, TimeZoneId, Bucket,
                          ObjectNameTemplate, Gzip, MaxRowsPerFile, UploadWhenEmpty, WriteManifest, CreatedBy, ModifiedBy)
    VALUES (N'dwh_nightly_all_tables', 4, @dwh, N'__DWHDB__', NULL, '0 3 * * *', 'Europe/Istanbul', N'test-bucket',
            N'exports/{Database}/{Schema}.{Table}/dt={Date}/{Table}_{DateCompact}_{Time}_{Part}.csv', 0, 100000, 0, 1, N'seed', N'seed');

IF N'__ADMIN__' <> N'' AND NOT EXISTS (SELECT 1 FROM etl.RoleAssignment WHERE PrincipalName = N'__ADMIN__' AND Role = 'Admin')
    INSERT etl.RoleAssignment (PrincipalName, PrincipalType, Role, ModifiedBy) VALUES (N'__ADMIN__', 'U', 'Admin', N'seed');

SELECT (SELECT COUNT(*) FROM etl.SqlServer) AS servers, (SELECT COUNT(*) FROM etl.ExtractJob) AS extract_jobs, (SELECT COUNT(*) FROM etl.ExportJob) AS export_jobs;
