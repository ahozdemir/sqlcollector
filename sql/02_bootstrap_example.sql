/* Initial setup example (alternatively done in the UI as local admin) */
USE [DataCollectorMeta];

-- Admin role for an AD group
INSERT etl.RoleAssignment (PrincipalName, PrincipalType, Role, ModifiedBy) VALUES (N'CORP\DBA-Team', 'G', 'Admin', N'bootstrap');
-- Editor role
INSERT etl.RoleAssignment (PrincipalName, PrincipalType, Role, ModifiedBy) VALUES (N'CORP\ETL-Editors', 'G', 'Editor', N'bootstrap');

-- Servers (connections use the service account, no passwords)
INSERT etl.SqlServer (Name, ServerAddress, IsSource, IsTarget, EncryptMode, ReadOnlyIntent, MultiSubnetFailover, ModifiedBy)
VALUES (N'ERP',  N'ERP-AG-LISTENER', 1, 0, 'Mandatory', 1, 1, N'bootstrap'),
       (N'CRM',  N'CRM-SQL01',       1, 0, 'Mandatory', 0, 0, N'bootstrap'),
       (N'DWH',  N'DWH-SQL01',       0, 1, 'Mandatory', 0, 0, N'bootstrap');

-- ETL-Editors: may use all ERP databases as source and the DWH database on the DWH server as target
INSERT etl.ServerPermission (PrincipalName, SqlServerId, DatabaseName, CanSource, CanTarget, ModifiedBy)
SELECT N'CORP\ETL-Editors', SqlServerId, NULL, 1, 0, N'bootstrap' FROM etl.SqlServer WHERE Name = N'ERP'
UNION ALL
SELECT N'CORP\ETL-Editors', SqlServerId, N'DWH', 0, 1, N'bootstrap' FROM etl.SqlServer WHERE Name = N'DWH';
