-- ============================================================
-- The account dblens connects with by default. Read-only by construction: dblens refuses to
-- execute caller SQL (explain_query with analyze, query_with_receipt) on a login that can write,
-- so this is the account real deployments should mirror.
--   db_datareader         SELECT on every table
--   VIEW DATABASE STATE   index usage, fragmentation, Query Store
--   VIEW DEFINITION       view and routine bodies, extended properties
--   SHOWPLAN              execution plans
--   VIEW SERVER STATE     server start time, for the unused-index uptime check
-- ============================================================
USE master;
GO
CREATE LOGIN dblens_reader WITH PASSWORD = 'ReaderPassword123!', CHECK_POLICY = OFF;
GO
GRANT VIEW SERVER STATE TO dblens_reader;
GO
USE dblens_demo;
GO
CREATE USER dblens_reader FOR LOGIN dblens_reader;
ALTER ROLE db_datareader ADD MEMBER dblens_reader;
GRANT VIEW DATABASE STATE, VIEW DEFINITION, SHOWPLAN TO dblens_reader;
GO
