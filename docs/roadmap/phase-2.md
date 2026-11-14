# Phase 2: Task lifecycle and durable execution

**Goal:** submit a task, queue it, execute it on a worker, and persist exactly one terminal outcome — and prove that a
worker crash mid-run does not lose the work or produce two outcomes.

**Exit criterion (Spec §22):** submit → queue → execute → persist works, including after a simulated worker crash.

The queue is PostgreSQL ([ADR-0005](../adr/0005-postgresql-backed-work-queue.md)): `FOR UPDATE SKIP LOCKED`, leases,
heartbeats, a reconciler, and `LISTEN/NOTIFY` for wake-up. Handlers are trusted and allowlisted
([ADR-0007](../adr/0007-trusted-allowlisted-handlers-only.md)); Phase 2 ships the **deterministic fake provider**
([ADR-0009](../adr/0009-single-model-provider-behind-adapter.md)) so execution is free and reproducible in CI. The run
state machine and delivery contract are in [task-lifecycle.md](../architecture/task-lifecycle.md).

## Slices

Each slice is code + tests + doc updates, tested and pushed on its own.

### 1. Schema (V0002)

- [x] `V0002__task_execution.sql`: `task_definitions`, `task_definition_versions` (immutable once published, via a
  trigger), `task_runs` (queue state: status, `next_attempt_at`, lease columns; partial indexes for claim and
  reconcile), `task_run_attempts`, `idempotency_keys`. Every tenant-owned table has `organization_id`, composite FKs,
  RLS `ENABLE` + `FORCE` and the `tenant_isolation` policy ([data model](../data/data-model.md))
- [x] Narrowed per-role privileges: API authors definitions/versions, submits runs, reads attempts, records
  idempotency keys; worker reads definitions/versions, claims and updates runs, writes attempts. Both keep SELECT
  everywhere; nothing is service-deletable
- [x] EF mappings for the five tables (jsonb as string); `Seed.TenantAsync` seeds one row in each, so the
  catalog-driven isolation and column-drift tests cover them
- [x] `TaskExecutionSchemaTests`: the immutability trigger (draft editable → publish allowed → published frozen), the
  narrowed privileges, the worker claim path, cross-tenant composite-FK rejections, idempotency-scope uniqueness.
  Full suite 264 green (Database 95)

### 2. Domain retry model and execution ports

- [x] Error categories (retryable vs permanent) and full-jitter exponential backoff with `RetryPolicy`
  (`min(cap, base·2^(n-1))`, honouring provider `Retry-After`) in the Domain, unit-tested against
  [task-lifecycle.md](../architecture/task-lifecycle.md)
- [x] Execution ports in the Application: `IModelProvider` + the deterministic fake provider (ADR-0009);
  `ITaskHandler`, `IHandlerRegistry` allowlist (ADR-0007) and the `completion` handler; registered in DI. Unit tests
  cover backoff/classification, fake determinism, the handler (templating, unknown provider, provider-error
  propagation) and the registry allowlist. Full suite 293 green

### 3. Queue (port + Infrastructure)

- [x] `IRunQueue` port (enqueue, claim, heartbeat, settle, reconcile, wait-for-signal) and `IIdempotencyStore`, over the
  tenant unit of work
- [x] PostgreSQL queue (`PostgresRunQueue`): cross-tenant claim and reconcile run through the V0003 `SECURITY DEFINER`
  functions (`claim_next_run`, `reconcile_expired_leases`), because a worker has no tenant context when it claims; claim
  uses `FOR UPDATE OF r SKIP LOCKED`, promotes a due `RetryWait` through `Queued` to `Running` and creates the attempt
  atomically; heartbeat and settle are conditional updates (`WHERE id = @id AND status = 'Running' AND lease_owner = @worker`),
  so a late or duplicate attempt cannot overwrite the result
- [x] `LISTEN/NOTIFY` on enqueue (`WaitForEnqueueSignalAsync`, polling fallback in the worker loop); idempotency store
  (SHA-256 key/payload hashes, `ON CONFLICT DO NOTHING` for the submission race)
- [x] Queue contract tests in an isolated migrated database: enqueue/claim/heartbeat/settle (incl. at-most-one terminal),
  retry re-claim, reconcile recover + dead-letter, concurrent claims never double-claim, API role cannot claim/reconcile,
  NOTIFY wakes a waiter, idempotency mapping. Full suite 307 green

### 4. Worker

- [x] `RunExecutor` (Application, pure orchestration): resolve the allowlisted handler, run it under a hard timeout while
  heartbeating the lease, then settle via the Domain's retry classification and backoff. Success, permanent failure,
  retry, dead-letter, missing handler and cooperative cancellation (heartbeat observes `cancel_requested_at`) all handled
- [x] Worker host: `RunWorker` loop (claim → execute → settle, LISTEN/NOTIFY wake with poll fallback) and
  `ReconcilerService` (periodic `reconcile_expired_leases`). The Phase 0 placeholder `RunProcessor` is gone
- [x] `RunExecutorTests` drive all six paths end to end against the real queue and fake provider. Full suite 313 green

### 5. API

- [x] Task-definition endpoints (create, create a draft version, publish, list, get) with the handler allowlist enforced
  at authoring time; workspace-scoped permissions (`definitions:read`/`write`), 404 concealment, per-mutation audit
- [x] Run endpoints: `POST …/runs` with the `Idempotency-Key` header (atomic run + idempotency insert, same-key/same-payload
  → same run, different payload → 409 `idempotency.conflict`); get / list / cancel. The result (`output`) is returned only
  to callers with `runs:read_result`; cancellation is immediate for pending runs, flagged for running ones
- [x] Endpoint tests for the catalog (authoring, publish, allowlist, viewer-read-not-write, concealment) and runs
  (acceptance, idempotency same/different, unpublished guard, cancel, viewer-cannot-submit). Full suite 323 green

### 6. Durability proof

- [ ] The headline test: kill a worker mid-run → the reconciler recovers it → exactly one terminal outcome. Plus
  retry/backoff, duplicate submission, and cancellation
- [ ] Tick ADR-0005's acceptance criterion; update the threat model, [status](../status.md) and this checklist

## Deviations and decisions

| Topic | Decision | Where |
| --- | --- | --- |
| Model provider | Deterministic fake only in Phase 2; real Anthropic deferred until budget/quota guards exist | ADR-0009 |
| Payload storage | `input`/`output` inline as `jsonb`; `run_artifacts` / blob storage is a later phase | [Data model](../data/data-model.md) |
| Version authoring | Draft → publish, published versions immutable by trigger | [Data model](../data/data-model.md) |
