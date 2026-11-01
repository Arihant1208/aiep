#!/bin/sh
# Runs once, on first creation of the local Postgres volume.
# Creates separate least-privilege roles (ADR-0006). None of them can bypass row-level security.
#   aiep_migrator  owns the schema and applies migrations; never used by running services
#   aiep_api       used by the API service
#   aiep_worker    used by the worker service
set -eu

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v migrator_password="$AIEP_MIGRATOR_PASSWORD" \
  -v api_password="$AIEP_API_PASSWORD" \
  -v worker_password="$AIEP_WORKER_PASSWORD" <<'SQL'
CREATE ROLE aiep_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD :'migrator_password';
CREATE ROLE aiep_api      LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD :'api_password';
CREATE ROLE aiep_worker   LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD :'worker_password';

REVOKE ALL ON SCHEMA public FROM PUBLIC;
REVOKE CREATE ON DATABASE aiep FROM PUBLIC;
GRANT CONNECT ON DATABASE aiep TO aiep_migrator, aiep_api, aiep_worker;

CREATE SCHEMA aiep AUTHORIZATION aiep_migrator;
GRANT USAGE ON SCHEMA aiep TO aiep_api, aiep_worker;

-- Baseline DML grants for tables the migrator creates. Migrations narrow these per table
-- (for example, audit_events is INSERT/SELECT only).
ALTER DEFAULT PRIVILEGES FOR ROLE aiep_migrator IN SCHEMA aiep
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO aiep_api, aiep_worker;
ALTER DEFAULT PRIVILEGES FOR ROLE aiep_migrator IN SCHEMA aiep
  GRANT USAGE, SELECT ON SEQUENCES TO aiep_api, aiep_worker;

ALTER ROLE aiep_migrator SET search_path = aiep;
ALTER ROLE aiep_api      SET search_path = aiep;
ALTER ROLE aiep_worker   SET search_path = aiep;
SQL
