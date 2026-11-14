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

- [x] `TenantDatabase.ExecuteAsync(TenantContext, work)`: the only access path; transaction-local `set_config`;
  saves and commits, or rolls back on any exception ([data model](../data/data-model.md#how-services-reach-tenant-data))
- [x] EF Core mappings for V0001 (Npgsql provider 10.0.3, snake_case columns, no EF migrations); `NpgsqlDataSource`
  and `AiepDbContext` not resolvable from DI and not publicly constructible
- [x] 21 tests in `TenantDatabaseTests`: no context → nothing through EF; own tenant only (even by id); EF insert for
  another tenant rejected and not committed; commit and rollback semantics; pool of one connection alternating
  A/B/none 30 times on the same backend; failed unit of work leaves no context; settings transaction-local on one
  connection; pooled connection clean afterwards; `aiep_api` can't `SET ROLE`, `SET SESSION AUTHORIZATION`, gain
  `BYPASSRLS`, disable/unforce RLS or drop policies; `row_security = off` errors instead of bypassing; EF model
  columns match `information_schema` exactly
- [x] Mutation check: session-level `set_config(..., false)` in `TenantDatabase` fails
  `Tenant_settings_are_transaction_local_on_the_same_connection` (run by hand, 2026-11-10). Without that test the
  regression was invisible, because every unit of work overwrites both settings and Npgsql's pool reset clears them.

### 4. Authentication

- [x] Keycloak 26.8.2 in Compose with `deploy/keycloak/aiep-realm.json` (clients `aiep-api`, `aiep-web`, `aiep-cli`
  with PKCE; synthetic users); API container validates `iss=localhost:8081` while fetching keys from `keycloak:8080`
- [x] JWT bearer validation (issuer, audience, RS256/ES256 signature, lifetime); `(iss, sub)` → subject via
  `resolve_subject` with a 5-minute cache; principal rebuilt with AIEP claims only
- [x] API keys: Crockford base32 format, HMAC-SHA-256 with pepper, constant-time compare, environment check before
  lookup, expiry, revocation, owner's active membership, `last_used_at` at most once a minute; keys in URLs refused
- [x] Fallback policy: everything except health requires authentication; Problem Details carry `code` and `correlationId`
- [x] `GET /v1/me` with ADR-0015 prefixed IDs (`ResourceId`)
- [x] Tests promised by ADR-0016: Keycloak roles grant nothing; unknown signing key rejected. Plus unsigned, tampered,
  wrong-audience (mutation-checked) and other-realm tokens; 8 API key rejection cases; keys in URLs
  ([authentication](../security/authentication.md#tests)). New test projects: `Aiep.Application.Tests` (47 unit
  tests), `Aiep.Testing` (shared fixtures); `Aiep.Api.Tests` now runs against real PostgreSQL and Keycloak
- [x] Bug found by the unit tests: .NET regex `$` matches before a trailing newline, so `"<key>\n"` parsed as valid.
  Key patterns now end in `\z`

### 5. Authorization

- [x] `Permission` constants (24, frozen) and `OrgRole`/`WorkspaceRole` with strict parsing of the stored strings
  (`src/Aiep.Application/Authorization`)
- [x] `RolePermissions` table: the single code copy of the matrix in the [authorization model](../security/authorization-model.md).
  Footnoted cells (`limited`, `own`, `ws-scoped`) grant the permission; their condition is a step-6 policy, not an absence
- [x] `AuthorizationSubject.EffectiveInWorkspace/AtOrganization`: org-role permissions ∪ workspace-grant permissions
  (Owner/Admin implicitly WorkspaceAdmin everywhere) ∩ API-key scope
- [x] `IAuthorizationService` (algorithm steps 3–5): `NotAMember` → 404, `MissingPermission` → 403; an unknown required
  permission throws (it is always a code constant)
- [x] `AuthorizationInvariants`: `CanDelegate`, `ScopeExceeding`, `WouldRemoveLastOwner`
- [x] 17 tests (`Aiep.Application.Tests/Authorization`), including a test that parses the model doc's matrix and fails if
  the code table drifts — it caught a missing `keys:read` for Auditor on first run

### 6. Endpoints

- [x] Organizations (create, get) and `GET /v1/me/permissions` (6a); workspaces (create, list, get, archive) and
  workspace grants (set, remove) (6b); members (list, add, change role, remove) (6c); API keys (create returning the
  secret once, list, revoke) (6d). See the [control-plane API](../api/control-plane.md)
- [x] `IControlPlaneStore` (Application) / `ControlPlaneStore` (Infrastructure): every mutation writes its audit event
  in the same transaction; `EndpointAuth` runs algorithm steps 3–5 per request (not-a-member → 404, missing permission
  → 403); `CodedProblem` carries `code` + `correlationId` on every fault
- [x] Invariants enforced at the edge: Owner-protection, at-least-one-Owner (409 `organization.last_owner`), and
  scope/role delegation within the actor's own permissions
- [x] 41 API tests across organizations, members, workspaces/grants and API keys

### 7. Security test suite

- [x] `CrossTenantSecurityTests`: two orgs A and B; as A, by token and by API key, every B endpoint with B's real ids
  (read, mutate, enumerate, guess) → always 404, and B verified unchanged afterwards
- [x] Escalation (a Member cannot promote themselves; a key cannot exceed its scope), revoked / expired /
  wrong-environment keys, and a removed member's next request → 404
- [x] Threat model T1, T5, T6, T17 "verified by" updated with the test names

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
| Public ID encoding | `prefix_` + 26 uppercase Crockford base32 chars (big-endian UUID) | [Authentication](../security/authentication.md#resource-ids-in-responses) |
| Unknown routes | Anonymous → 401 (no route probing); authenticated → 404 | [Authentication](../security/authentication.md) |
| Test-only Keycloak objects | Password-grant client and decoy admin created by the test fixture, not in the realm file | [Keycloak README](../../deploy/keycloak/README.md) |
