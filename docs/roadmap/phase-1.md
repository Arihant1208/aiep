# Phase 1: Control plane and tenant security

**Goal:** organizations, members, workspaces, grants and API keys, with tenant isolation and authorization that tests prove.

**Exit criterion (Spec §22):** users can only access resources authorized for their identity and scope, and tests prove it.

## Checklist

### 1. Identity provider decision

- [x] [ADR-0016](../adr/0016-keycloak-oidc-provider.md): Keycloak as the OIDC provider for dev, CI and tests (2026-11-09)

### 2. Migration runner and baseline schema

- [x] `Aiep.Migrator`: ordered SQL migrations as `aiep_migrator`, one transaction per script, SHA-256 checksums,
  advisory lock, out-of-order and edited-script detection ([db/migrations/README.md](../../db/migrations/README.md))
- [x] `migrator` Compose service (profile `app`); API and worker wait for it to succeed
- [x] `V0001__tenancy_baseline.sql`: subjects, organizations, memberships, workspaces, workspace_memberships, api_keys,
  audit_events ([data model](../data/data-model.md))
- [x] Pre-tenant bootstrap functions `resolve_subject`, `find_api_key`, `create_organization`
  ([ADR-0017](../adr/0017-pre-tenant-operations-security-definer.md), **Proposed**: awaiting sign-off)
- [x] `tests/Aiep.Database.Tests` (Testcontainers, real `01-roles.sh` and runner), 61 tests:
  - Runner: order, idempotent rerun, edited/renamed/missing/out-of-order scripts, rollback of a failing script,
    six concurrent runners, service roles can't touch history
  - Catalog: every tenant table has `organization_id`, RLS enabled **and** forced and the `tenant_isolation` policy;
    service roles can't bypass RLS and own nothing
  - Isolation: no context → 0 rows in every tenant table (API and worker roles); with context → only own rows;
    cross-tenant update/delete affect 0 rows; cross-tenant insert/move rejected by `WITH CHECK`; context ends with its transaction
  - Composite FKs: grant or API key on another tenant's workspace rejected; grant requires membership and cascades with it
  - Privileges: audit events append-only (grant + trigger); narrowed DML for both roles; subjects read-only and
    visible only to self and co-members; `create_organization` can't hijack an existing org; worker can't call bootstrap functions
- [x] Mutation check: removing `FORCE` from one table, or adding an unseeded tenant table, fails the catalog tests (run by hand, 2026-11-09)

### 3. Tenant-context unit of work

- [ ] `ITenantScope` / unit of work, EF Core mappings (no EF migrations)
- [ ] Pool-size-1 alternating-tenant test through the unit of work; `aiep_api` can't `SET ROLE`

### 4. Authentication

- [ ] Keycloak in Compose with `deploy/keycloak/aiep-realm.json`; JWT bearer validation; `(iss, sub)` → subject
- [ ] API keys: generation, peppered hash, constant-time compare, expiry, revocation, throttled `last_used_at`
- [ ] Tests promised by ADR-0016: Keycloak roles grant nothing; unknown signing key rejected

### 5. Authorization

- [ ] Permission constants and role table checked against the [authorization model](../security/authorization-model.md)
- [ ] Invariant tests

### 6. Endpoints

- [ ] Organizations, members, workspaces, grants, API keys, `GET /v1/me/permissions`; audit events

### 7. Security test suite

- [ ] Cross-tenant negative suite over every endpoint, by token and by API key (Spec §6.5)

### 8. Architecture tests

- [ ] Project dependency rules (ADR-0002)

### 9. CI

- [ ] Vulnerability scanning (packages and images)

## Deviations and decisions

| Topic | Decision | Where |
| --- | --- | --- |
| Local token issuer | None; Keycloak everywhere instead of ADR-0008's dev-only issuer | ADR-0016 |
| Pre-tenant access | Three `SECURITY DEFINER` functions; definer policies scoped `TO aiep_migrator` | ADR-0017 (Proposed) |
| ID generation | Application-generated UUIDv7 (PostgreSQL 17 has no `uuidv7()`) | [Data model](../data/data-model.md) |
