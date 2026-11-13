-- ============================================================
-- The account dblens connects with by default. Read-only by construction: dblens refuses to
-- execute caller SQL (explain_query with analyze, query_with_receipt) on a login that can write,
-- so this is the account real deployments should mirror.
--   pg_read_all_data  SELECT on every table, USAGE on every schema
--   pg_monitor        statistics views, pg_stat_statements text, pgstattuple
-- ============================================================
CREATE ROLE dblens_reader LOGIN PASSWORD 'readerpassword';
GRANT pg_read_all_data, pg_monitor TO dblens_reader;

-- Row estimates come from reltuples; collect them once so they are deterministic from the start.
ANALYZE;
