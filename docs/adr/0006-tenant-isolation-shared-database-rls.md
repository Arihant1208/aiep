# ADR-0006: Tenant isolation through a shared database, RLS, and application authorization

- **Status:** Accepted
- **Date:** 2026-11-01
- **Deciders:** Arihant Jain
- **Spec references:** §6, §6.1–6.5, §13.3, FR-001–FR-008, FR-017–FR-022

## Context

Multiple organizations share infrastructure. A cross-tenant leak is the worst possible failure for this product.
Per-tenant databases give strong separation but multiply operational cost, migrations and connection pools, which
isn't justified for an MVP. A shared database relies on every query being correctly scoped. One missing `WHERE` clause
is a breach.

## Decision

Shared PostgreSQL with **three independent layers**, each of which must fail closed:

1. **Application authorization** (primary). Every request resolves the caller's organization/workspace from
   authenticated identity and resource ownership, never from the request body, and checks the action against
   role and scope ([authorization model](../security/authorization-model.md)).
2. **Database constraints**. Every tenant-owned table carries `organization_id`. Child tables use **composite
   foreign keys** `(organization_id, parent_id)` so a row cannot reference a parent in another tenant.
3. **Row-level security** (defense in depth). Every tenant-owned table has RLS `ENABLE`d **and** `FORCE`d, with
   policies comparing `organization_id` to `current_setting('app.organization_id', true)::uuid`.

Rules for the RLS context:

- The tenant context is set **only** with `SET LOCAL` (or `set_config(..., true)`) **inside an explicit transaction**,
  after authentication has succeeded. Session-level `SET` is forbidden because pooled connections would leak it.
- A missing context must match **no rows**: `current_setting(..., true)` returns NULL, `NULL = x` is not true, so the row is excluded.
  Tests assert this explicitly.
- Running services connect as `aiep_api` / `aiep_worker`: not table owners, `NOBYPASSRLS`. Only `aiep_migrator` owns
  the schema, and services never use it.
- Workers take tenant context from the **trusted run row** they claimed, never from message payloads.
- Cross-tenant platform operations (reconciler scans, operator tooling) use narrowly scoped `SECURITY DEFINER`
  functions or a dedicated role with explicit, audited policies. They never use a blanket bypass.

Isolation also covers every other shared surface (Spec §6.4): cache keys, idempotency keys, rate-limit buckets, object
storage paths, signed URLs, logs, exports and support tools all include or check tenant scope.

## Consequences

**Positive:** economical; a bug in one layer is caught by another; isolation is testable.

**Negative:** every data-access path must open a transaction and set context, which carries a small per-request cost;
RLS policies make query plans harder to reason about; developers must understand the pooling rule.

**Verification (required, not optional):**

- Negative tests for every tenant-owned endpoint: tenant A attempts read, update, cancel, download, replay and enumerate
  on tenant B's resources (Spec §6.5).
- RLS tests that run directly as `aiep_api` with no context, the wrong context and the right context.
- A connection-pool leak test: pool size 1, alternating tenants, assert that no context carries over.

## Alternatives considered

| Option | Why not (now) |
| --- | --- |
| Database per tenant | Strongest separation; operationally heavy for an MVP; kept as a possible future enterprise tier |
| Schema per tenant | Migration fan-out and catalog bloat; weaker than a database per tenant |
| Application filtering only | One forgotten filter is a breach; no defense in depth |
| RLS only | The database cannot express role/action/workspace authorization well, and errors surface as empty results rather than 403/404 |

## Revisit when

A customer requires physical separation or data residency, or tenant count or size makes noisy-neighbour effects
visible in database metrics.
