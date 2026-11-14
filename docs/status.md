# Current state

*What AIEP **is** right now — not the plan, the plan is in the [roadmap](roadmap/README.md). Updated at the end of each
vertical slice. This page links to canonical docs rather than repeating them.*

**As of:** 2026-11-14 · **Phase:** 2 **complete** (task lifecycle and durable execution); Phase 3 next · **Latest
commit:** `Prove durable execution across a worker crash (Phase 2, slice 6)`.

## In one paragraph

AIEP is a multi-tenant .NET 10 service with a PostgreSQL 17 system of record. Today it can **authenticate** a caller —
a person via an OIDC token from Keycloak, or an application via a scoped API key — and run the full **control plane**:
create and read organizations, manage members and their roles, create and archive workspaces, grant workspace roles,
and issue, list and revoke API keys. Every request is authorized against the role model (org role ∪ workspace grant ∩
key scope), a resource the caller can't see is concealed as 404, and every administrative action is audited. Tenant
isolation is enforced in the database with row-level security underneath it all. It does **not yet** run any AI task:
**Phase 2 is complete:** a caller can author a task definition and immutable versions, submit a run (idempotently), and
the worker claims it across tenants, executes the handler against the deterministic fake provider, and persists exactly
one terminal outcome — proven to survive a simulated worker crash (the reconciler recovers the abandoned run and a second
worker finishes it). It still calls only the **fake** provider; the real Anthropic adapter is a later addition.

## What runs today

| Component | State | Notes |
| --- | --- | --- |
| `Aiep.Api` | Runs | Health, authentication, `GET /v1/me`. Every `/v1` route requires an authenticated caller. |
| `Aiep.Worker` | Runs | Claims runs across tenants, executes the handler (fake provider), settles the outcome; reconciles expired leases. No submit API yet (Phase 2 slice 5). |
| `Aiep.Migrator` | Runs | Applies `db/migrations/V*.sql` as `aiep_migrator`; advisory-locked, checksum-guarded. |
| PostgreSQL 17 | Runs | System of record. RLS enabled **and** forced on every tenant table. Schema: V0001 tenancy baseline. |
| Keycloak 26.8.2 | Runs (dev/CI) | OIDC provider; realm-as-code in [`deploy/keycloak`](../deploy/keycloak). IdP roles ignored. |
| Telemetry stack | Runs | OTel Collector → Jaeger / Prometheus / Grafana. No payloads in telemetry. |
| Azurite (blob) | Runs | Not used by code yet (Phase 2+). |

Full local stack: [`docker compose --profile app up -d --build`](development/local-setup.md).

## What is built, by layer

- **Domain** — `TaskRunStatus` + `TaskRunTransitions` state machine (single source of truth; [task lifecycle](architecture/task-lifecycle.md)); `ErrorCategory` retry classification and `RetryBackoff` / `RetryPolicy` (full-jitter exponential backoff).
- **Application** — `TenantContext`; identity primitives (API-key format/hashing, `ExternalIdentity`); resource-ID encoding ([`ResourceId`](../src/Aiep.Application/Ids/ResourceId.cs)); the [authorization model](security/authorization-model.md) (`Permission`, `RolePermissions`, `AuthorizationSubject`, `IAuthorizationService`, `AuthorizationInvariants`); the control-plane port `IControlPlaneStore`; the execution ports `IModelProvider` (with the deterministic `FakeModelProvider`, ADR-0009), `ITaskHandler` / `IHandlerRegistry` allowlist and the `completion` handler (ADR-0007); the queue ports `IRunQueue` and `IIdempotencyStore` (ADR-0005, ADR-0015); `RunExecutor`, the worker's execution orchestration.
- **Infrastructure** — `TenantDatabase` unit of work (the **only** path to tenant data: transaction-local `set_config`, EF `AiepDbContext` bound to that connection); API-key store, subject directory and `ControlPlaneStore` over the unit of work and `SECURITY DEFINER` lookups ([ADR-0017](adr/0017-pre-tenant-operations-security-definer.md)); `PostgresRunQueue` and the idempotency store over the V0003 `claim_next_run` / `reconcile_expired_leases` definer functions.
- **Api** — two-scheme authentication ([authentication, as built](security/authentication.md)); the `/v1` [control-plane endpoints](api/control-plane.md) (organizations, members, workspaces, grants, API keys, `/me`, `/me/permissions`) with `EndpointAuth` (resolve + authorize) and `CodedProblem`; Problem Details with stable `code`s; correlation-id and credentials-in-URL middleware. The [task-execution API](api/task-execution.md): task-definition and version authoring and runs (`POST …/runs` with `Idempotency-Key`, get, list, cancel), workspace-scoped with result-read gating.
- **Worker** — `RunWorker` loop (claim → execute → settle, with LISTEN/NOTIFY wake) and `ReconcilerService` (expired-lease recovery), hosting `RunExecutor`.
- **Data** — V0001: `subjects`, `organizations`, `memberships`, `workspaces`, `workspace_memberships`, `api_keys`, `audit_events`. V0002: `task_definitions`, `task_definition_versions`, `task_runs`, `task_run_attempts`, `idempotency_keys`. V0003: the `claim_next_run` and `reconcile_expired_leases` queue functions ([data model](data/data-model.md)).

## What a caller can do today

Health (`/health/live`, `/health/ready`) is anonymous; everything under `/v1` requires authentication (401 otherwise,
404 for unknown routes so they can't be probed). The control-plane surface — organizations, members, workspaces, grants,
API keys, `GET /v1/me` and `GET /v1/me/permissions` — is in the [control-plane API reference](api/control-plane.md),
with the permission, status codes and audit action for each endpoint.

## Tests (all green)

| Suite | Count | Proves |
| --- | --- | --- |
| `Aiep.Domain.Tests` | 42 | Run state machine; retry classification and full-jitter backoff |
| `Aiep.Application.Tests` | 72 | Key format/hashing, resource IDs, authorization model (incl. matrix-vs-code); fake provider, completion handler and handler-registry allowlist |
| `Aiep.Architecture.Tests` | 4 | Module-boundary rules (ADR-0002) via assembly references |
| `Aiep.Database.Tests` | 118 | RLS isolation, no-context/wrong-context/pool-leak, composite FKs, migrator; V0002 task-execution privileges, the version immutability trigger and cross-tenant run/attempt FKs; the work queue (claim/heartbeat/settle/reconcile, concurrency, idempotency); the worker execution paths (success/fail/retry/dead-letter/cancel); durable execution across a worker crash |
| `Aiep.Api.Tests` | 90 | OIDC + API-key auth, control-plane endpoints, the cross-tenant security suite, and the task-definition / run endpoints (authoring, idempotency, cancel), against real Postgres + Keycloak |
| **Total** | **326** | |

Run: `dotnet test Aiep.slnx`. Database and API suites need Docker (Testcontainers).

## Decisions in force

All architectural decisions are recorded as ADRs — see the [ADR index](adr/README.md) (0001–0017, all Accepted except
0017 Proposed). The [HANDOFF.md](../HANDOFF.md) one-line table is a convenience copy; the ADRs are authoritative.

## What's next

**Phase 1 and Phase 2 are complete** ([Phase 2 checklist](roadmap/phase-2.md)): the exit criterion — submit → queue →
execute → persist, surviving a simulated worker crash — is met and proven by tests, and ADR-0005's acceptance criterion
is ticked. Next is **Phase 3: execution isolation and observability** ([roadmap](roadmap/README.md)) — every run
traceable, measurable and cleaned up safely (worker spans linked to the stored `traceparent`, run artifacts / blob
storage, retention) — and the real Anthropic provider adapter behind `IModelProvider` once budget guards exist.

## Keeping this page honest

Update it at the end of every slice, in the same commit as the code. State only what is built and verified; link the test
that proves a claim. Decisions still go in ADRs, how-it-works in the area doc — this page is just the snapshot that ties
them together.
