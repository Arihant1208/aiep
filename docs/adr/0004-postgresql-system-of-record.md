# ADR-0004: PostgreSQL is the system of record for all state

- **Status:** Accepted
- **Date:** 2026-11-01
- **Deciders:** Arihant Jain
- **Spec references:** §5, §5.2, §13

## Context

The platform needs transactional state changes for runs, constraints that enforce tenant ownership, row-level security,
an append-only audit trail and indexed querying for the UI. Several components could plausibly hold state: the
database, the queue, object storage and caches. If more than one of them is authoritative, they eventually disagree.

## Decision

- **PostgreSQL 17** holds all authoritative metadata and state: tenancy, membership, definitions, runs, attempts, API
  key verifiers, audit events, usage and quotas.
- **Object storage** holds large payloads (inputs, outputs, datasets, artifacts). PostgreSQL stores references to them.
  A blob without a database row is garbage. A database row without its blob is a detectable integrity error.
- **The queue is never authoritative.** If queue state and database state disagree, the database wins and
  reconciliation repairs the queue.
- **Caches are disposable** and always keyed by tenant scope (see ADR-0006).
- Managed PostgreSQL in the cloud (Azure Database for PostgreSQL Flexible Server).

## Consequences

**Positive:** one source of truth; ACID transactions across a run and its outbox/audit records; mature RLS,
constraints and JSONB; a well-known operational model.

**Negative:** PostgreSQL becomes the main scaling and availability dependency. Write-heavy telemetry must not go
there, because metrics and traces belong in the observability stack.

## Alternatives considered

| Option | Why not |
| --- | --- |
| SQL Server / Azure SQL | Also viable; PostgreSQL's RLS ergonomics, cost and portability are better here |
| Document store (Cosmos DB, MongoDB) | Weaker relational constraints for the ownership invariants that tenant isolation depends on |
| Queue as status source | Queues are delivery mechanisms; inspecting and correcting state there is unreliable |

## Revisit when

Sustained write load or table size (for example `task_run_attempts`, `audit_events`) needs partitioning or archival.
Plan time-based partitioning before reaching ~100M rows in any single table.
