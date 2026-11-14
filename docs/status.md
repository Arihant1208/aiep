# Current state

*What AIEP **is** right now — not the plan, the plan is in the [roadmap](roadmap/README.md). Updated at the end of each
vertical slice. This page links to canonical docs rather than repeating them.*

**As of:** 2026-11-14 · **Phase:** 1 (control plane and tenant security), steps 1–5 done · **Latest commit:**
`Add the authorization model (Phase 1, step 5)`.

## In one paragraph

AIEP is a multi-tenant .NET 10 service with a PostgreSQL 17 system of record. Today it can **authenticate** a caller —
a person via an OIDC token from Keycloak, or an application via a scoped API key — map them to a tenant-scoped subject,
and enforce tenant isolation at the database with row-level security. The **authorization model** is built and tested:
the role→permission table, effective-permission computation (org role ∪ workspace grant ∩ key scope), and the delegation
and owner-protection invariants. It is **not yet wired to endpoints** — there are no control-plane resources
(organizations, workspaces, members, keys) to authorize against, and no AI task runs. Those are the next steps (6 onward).

## What runs today

| Component | State | Notes |
| --- | --- | --- |
| `Aiep.Api` | Runs | Health, authentication, `GET /v1/me`. Every `/v1` route requires an authenticated caller. |
| `Aiep.Worker` | Placeholder | 5-second tick loop; no execution yet (Phase 2). |
| `Aiep.Migrator` | Runs | Applies `db/migrations/V*.sql` as `aiep_migrator`; advisory-locked, checksum-guarded. |
| PostgreSQL 17 | Runs | System of record. RLS enabled **and** forced on every tenant table. Schema: V0001 tenancy baseline. |
| Keycloak 26.8.2 | Runs (dev/CI) | OIDC provider; realm-as-code in [`deploy/keycloak`](../deploy/keycloak). IdP roles ignored. |
| Telemetry stack | Runs | OTel Collector → Jaeger / Prometheus / Grafana. No payloads in telemetry. |
| Azurite (blob) | Runs | Not used by code yet (Phase 2+). |

Full local stack: [`docker compose --profile app up -d --build`](development/local-setup.md).

## What is built, by layer

- **Domain** — `TaskRunStatus` + `TaskRunTransitions` state machine (single source of truth; [task lifecycle](architecture/task-lifecycle.md)).
- **Application** — `TenantContext`; identity primitives (API-key format/hashing, `ExternalIdentity`); resource-ID encoding ([`ResourceId`](../src/Aiep.Application/Ids/ResourceId.cs)); the [authorization model](security/authorization-model.md) (`Permission`, `RolePermissions`, `AuthorizationSubject`, `IAuthorizationService`, `AuthorizationInvariants`).
- **Infrastructure** — `TenantDatabase` unit of work (the **only** path to tenant data: transaction-local `set_config`, EF `AiepDbContext` bound to that connection); API-key store and subject directory over `SECURITY DEFINER` lookups ([ADR-0017](adr/0017-pre-tenant-operations-security-definer.md)).
- **Api** — two-scheme authentication ([authentication, as built](security/authentication.md)), Problem Details with stable `code`s, correlation-id and credentials-in-URL middleware, `GET /v1/me`.
- **Data** — `subjects`, `organizations`, `memberships`, `workspaces`, `workspace_memberships`, `api_keys`, `audit_events` ([data model](data/data-model.md)).

## What a caller can do today

| Endpoint | Auth | Purpose |
| --- | --- | --- |
| `GET /health/live`, `GET /health/ready` | anonymous | Liveness / readiness |
| `GET /v1/me` | OIDC token or API key | Report the resolved caller (debugging aid) |
| any other `/v1/...` | required | 401 if unauthenticated, 404 if authenticated (routes can't be probed) |

Control-plane resource endpoints (organizations, workspaces, members, API-key management) are **step 6**, not built yet.

## Tests (all green)

| Suite | Count | Proves |
| --- | --- | --- |
| `Aiep.Domain.Tests` | 21 | Run state machine |
| `Aiep.Application.Tests` | 64 | Key format/hashing, resource IDs, authorization model (incl. matrix-vs-code) |
| `Aiep.Database.Tests` | 82 | RLS isolation, no-context/wrong-context/pool-leak, composite FKs, migrator |
| `Aiep.Api.Tests` | 32 | OIDC + API-key paths against real Postgres + Keycloak |
| **Total** | **199** | |

Run: `dotnet test Aiep.slnx`. Database and API suites need Docker (Testcontainers).

## Decisions in force

All architectural decisions are recorded as ADRs — see the [ADR index](adr/README.md) (0001–0017, all Accepted except
0017 Proposed). The [HANDOFF.md](../HANDOFF.md) one-line table is a convenience copy; the ADRs are authoritative.

## What's next

Phase 1 continues ([checklist](roadmap/phase-1.md)): **step 6 endpoints** (organizations, workspaces, members, grants,
API keys, `GET /v1/me/permissions`) — where the authorization model gets wired to real resources with audit events —
then step 7 the cross-tenant security test suite, step 8 architecture tests, step 9 CI vulnerability scan.

## Keeping this page honest

Update it at the end of every slice, in the same commit as the code. State only what is built and verified; link the test
that proves a claim. Decisions still go in ADRs, how-it-works in the area doc — this page is just the snapshot that ties
them together.
