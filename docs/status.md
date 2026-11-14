# Current state

*What AIEP **is** right now — not the plan, the plan is in the [roadmap](roadmap/README.md). Updated at the end of each
vertical slice. This page links to canonical docs rather than repeating them.*

**As of:** 2026-11-14 · **Phase:** 1 (control plane and tenant security), steps 1–7 done · **Latest commit:**
`Add the cross-tenant security test suite (Phase 1, step 7)`.

## In one paragraph

AIEP is a multi-tenant .NET 10 service with a PostgreSQL 17 system of record. Today it can **authenticate** a caller —
a person via an OIDC token from Keycloak, or an application via a scoped API key — and run the full **control plane**:
create and read organizations, manage members and their roles, create and archive workspaces, grant workspace roles,
and issue, list and revoke API keys. Every request is authorized against the role model (org role ∪ workspace grant ∩
key scope), a resource the caller can't see is concealed as 404, and every administrative action is audited. Tenant
isolation is enforced in the database with row-level security underneath it all. It does **not yet** run any AI task —
that is Phase 2. The remaining Phase 1 work is the cross-tenant security test suite and the CI hardening (steps 7–9).

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
- **Application** — `TenantContext`; identity primitives (API-key format/hashing, `ExternalIdentity`); resource-ID encoding ([`ResourceId`](../src/Aiep.Application/Ids/ResourceId.cs)); the [authorization model](security/authorization-model.md) (`Permission`, `RolePermissions`, `AuthorizationSubject`, `IAuthorizationService`, `AuthorizationInvariants`); the control-plane port `IControlPlaneStore`.
- **Infrastructure** — `TenantDatabase` unit of work (the **only** path to tenant data: transaction-local `set_config`, EF `AiepDbContext` bound to that connection); API-key store, subject directory and `ControlPlaneStore` over the unit of work and `SECURITY DEFINER` lookups ([ADR-0017](adr/0017-pre-tenant-operations-security-definer.md)).
- **Api** — two-scheme authentication ([authentication, as built](security/authentication.md)); the `/v1` [control-plane endpoints](api/control-plane.md) (organizations, members, workspaces, grants, API keys, `/me`, `/me/permissions`) with `EndpointAuth` (resolve + authorize) and `CodedProblem`; Problem Details with stable `code`s; correlation-id and credentials-in-URL middleware.
- **Data** — `subjects`, `organizations`, `memberships`, `workspaces`, `workspace_memberships`, `api_keys`, `audit_events` ([data model](data/data-model.md)).

## What a caller can do today

Health (`/health/live`, `/health/ready`) is anonymous; everything under `/v1` requires authentication (401 otherwise,
404 for unknown routes so they can't be probed). The control-plane surface — organizations, members, workspaces, grants,
API keys, `GET /v1/me` and `GET /v1/me/permissions` — is in the [control-plane API reference](api/control-plane.md),
with the permission, status codes and audit action for each endpoint.

## Tests (all green)

| Suite | Count | Proves |
| --- | --- | --- |
| `Aiep.Domain.Tests` | 21 | Run state machine |
| `Aiep.Application.Tests` | 64 | Key format/hashing, resource IDs, authorization model (incl. matrix-vs-code) |
| `Aiep.Database.Tests` | 82 | RLS isolation, no-context/wrong-context/pool-leak, composite FKs, migrator |
| `Aiep.Api.Tests` | 80 | OIDC + API-key auth, control-plane endpoints, and the cross-tenant security suite, against real Postgres + Keycloak |
| **Total** | **247** | |

Run: `dotnet test Aiep.slnx`. Database and API suites need Docker (Testcontainers).

## Decisions in force

All architectural decisions are recorded as ADRs — see the [ADR index](adr/README.md) (0001–0017, all Accepted except
0017 Proposed). The [HANDOFF.md](../HANDOFF.md) one-line table is a convenience copy; the ADRs are authoritative.

## What's next

Phase 1 is nearly done ([checklist](roadmap/phase-1.md)): the **exit criterion — users access only what they're
authorized for, proven by tests — is met** (`CrossTenantSecurityTests`). Remaining: step 8 architecture tests
(dependency rules) and step 9 CI vulnerability scanning. Then Phase 2: task lifecycle and durable execution.

## Keeping this page honest

Update it at the end of every slice, in the same commit as the code. State only what is built and verified; link the test
that proves a claim. Decisions still go in ADRs, how-it-works in the area doc — this page is just the snapshot that ties
them together.
