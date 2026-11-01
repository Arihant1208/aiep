# ADR-0013: Data access with EF Core; schema via versioned SQL migrations

- **Status:** Accepted
- **Date:** 2026-11-01
- **Deciders:** Arihant Jain
- **Spec references:** §6.3, §13.3, §14.4

## Context

Tenant isolation relies on PostgreSQL features that ORMs model poorly: RLS policies, `FORCE ROW LEVEL SECURITY`,
composite foreign keys, partial indexes, per-table grants and `SECURITY DEFINER` functions. The run queue (ADR-0005)
also needs hand-tuned `SKIP LOCKED` SQL. On the other hand, typed queries and change tracking speed up ordinary CRUD.

## Decision

- **Schema is owned by SQL**: forward-only migrations in `db/migrations/V####__name.sql`, applied by a small migrator
  (DbUp, or a minimal in-house runner) as the `aiep_migrator` role. RLS policies and grants live next to the tables they protect.
- **Data access uses EF Core** (Npgsql provider) for typed queries, mapped onto the SQL-owned schema. EF migrations are
  **not** used.
- **Hot paths use raw SQL** through Npgsql (queue claim, lease heartbeat, quota reservation).
- **Tenant context** is applied through one mechanism: a unit-of-work that opens a transaction and runs
  `SELECT set_config('app.organization_id', @org, true)` (plus workspace and subject) before any query. There is no other code path to the database.
- A CI job applies all migrations to an empty database, then runs RLS and isolation integration tests (Testcontainers).

## Consequences

**Positive:** the security-critical schema is explicit and reviewable as SQL; no ORM surprises around RLS.

**Negative:** EF model and SQL schema can drift. Mitigate with a startup/CI check that the EF model matches the database (compare columns), or by keeping EF mappings minimal.

## Alternatives considered

| Option | Why not |
| --- | --- |
| EF Core migrations with `migrationBuilder.Sql(...)` for RLS | Security-critical SQL is buried in C# migration classes; snapshot noise |
| Dapper only | Fine, but more boilerplate for CRUD-heavy control-plane endpoints |
| Flyway/Liquibase | JVM dependency in a .NET repo |
| DbUp | Viable; an in-house runner is small and makes the locking and checksum behaviour explicit |

## Acceptance criteria

- [x] Project owner agrees on SQL-first migrations.
- [x] Runner: in-house `Aiep.Migrator` (about 150 lines on Npgsql). It gives explicit control of transactions, advisory locking and checksums, and avoids an extra dependency.
