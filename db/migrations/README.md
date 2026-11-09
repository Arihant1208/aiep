# Database migrations

Versioned, forward-only SQL migrations applied by [`Aiep.Migrator`](../../src/Aiep.Migrator) as the `aiep_migrator`
role ([ADR-0013](../../docs/adr/0013-data-access-and-migrations.md)).

## Running

```bash
dotnet run --project src/Aiep.Migrator                # local Postgres from docker compose
docker compose --profile app up -d --build            # the migrator service runs first; api and worker wait for it
```

## Rules the runner enforces

- File names are `V0001__lower_snake_name.sql`. Any other `*.sql` file, version 0 or a duplicate version is an error.
- Applied in version order; each script and its `aiep.schema_migrations` row commit in **one transaction**.
- An applied script that was **edited, renamed or deleted** stops the run before anything is applied. Checksums are
  SHA-256 of the script with line endings normalized to LF.
- A new script **older than the newest applied version** stops the run (renumber it).
- A `pg_advisory_lock` serializes concurrent runners; the second one waits and then finds nothing to do.
- `aiep.schema_migrations` is created by the runner and readable only by `aiep_migrator`.

## Rules for authors

- **Never edit an applied migration.** Add a new one.
- No `BEGIN`/`COMMIT` in scripts (the runner owns the transaction). Statements that can't run in a transaction
  (`CREATE INDEX CONCURRENTLY`) aren't supported yet; add support when one is needed.
- Every tenant-owned table has `organization_id`, RLS enabled **and** forced, the `tenant_isolation` policy, composite
  foreign keys that keep children in the parent's tenant, and a row in `Seed.TenantAsync` (tests fail otherwise).
  See the [data model](../../docs/data/data-model.md).
- New tables get full DML for both service roles by default (`db/init/01-roles.sh`). Narrow that in the same migration.
- `SECURITY DEFINER` functions follow [ADR-0017](../../docs/adr/0017-pre-tenant-operations-security-definer.md):
  `SET search_path = aiep, pg_temp`, `EXECUTE` revoked from `PUBLIC`, no dynamic SQL.
- Expand/contract for breaking changes: add new columns or tables first, migrate data, remove the old ones in a later release.
- Running services never connect as the schema owner.
