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

### 2. Domain and ports

- [ ] Error categories and the retry/backoff policy (retryable vs permanent; `min(cap, base·2^(n-1))` with full jitter);
  lease model. Unit-tested against [task-lifecycle.md](../architecture/task-lifecycle.md)
- [ ] `IRunQueue` port (enqueue, claim, heartbeat, complete, requeue) and `ITaskHandler` + an allowlisted registry
  (ADR-0007); `IModelProvider` with the deterministic fake (ADR-0009)

### 3. Queue (Infrastructure)

- [ ] PostgreSQL queue over the tenant unit of work: enqueue in the acceptance transaction; claim with
  `FOR UPDATE SKIP LOCKED` (promote due `RetryWait` → `Queued`), set lease + create the attempt in one transaction;
  heartbeat; terminal/ requeue as conditional updates (`WHERE id = @id AND status = @expected`)
- [ ] `LISTEN/NOTIFY` on enqueue with polling fallback; idempotency store
- [ ] Queue contract tests, including concurrent claims never double-claiming a run

### 4. Worker

- [ ] Real loop: claim → heartbeat → execute handler (fake provider) → persist, with at-most-one terminal outcome;
  retry/backoff and dead-lettering; cooperative cancellation
- [ ] Reconciler: `Running` runs with expired leases → `RetryWait`/`DeadLettered`, attempt recorded as `LeaseExpired`

### 5. API

- [ ] Task-definition endpoints (create, publish a version, list, get); `POST …/runs` with `Idempotency-Key`;
  get / list / cancel a run. 404 concealment, authorization and audit as in Phase 1
- [ ] Endpoint + idempotency tests (same key + payload → same run; same key + different payload → 409)

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
