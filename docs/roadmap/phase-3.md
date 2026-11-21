# Phase 3: Execution isolation and observability

**Goal:** every run can be traced, measured and cleaned up safely.

**Exit criterion (Spec §22):** every run can be traced, measured and cleaned up safely.

Telemetry follows [ADR-0012](../adr/0012-opentelemetry-observability.md): OpenTelemetry, `Aiep.*` sources/meters, the
worker's execution span **linked** to the run's stored `traceparent`, low-cardinality metric labels, and no payloads in
telemetry. Phase 3 keeps the deterministic fake provider; the real Anthropic adapter and blob artifact storage are later.

## Slices

### 1. Execution telemetry

- [x] `Aiep.Execution` activity source: the worker's `run.execute` span starts with a **span link** to the run's stored
  `traceparent` (V0004 returns it from the claim), tagged only with handler type, model provider/name, attempt number and
  terminal status — never inputs, outputs, tenant ids or run ids
- [x] `Aiep.Runs` meter: `aiep.runs.settled` (by status), `aiep.runs.execution.duration`, `aiep.runs.provider.latency`,
  all with low-cardinality labels (handler type, model provider/name, status)
- [x] Hygiene tests via `ActivityListener` / `MeterListener`: the span is linked and payload-free; metric labels are a
  closed allowlist with no tenant/run ids ([observability](../architecture/observability.md))

### 2. Retention and cleanup

- [x] `purge_expired` (V0005), a cross-tenant `SECURITY DEFINER` function (worker-only) that deletes expired idempotency
  keys and terminal runs past the retention window with their attempts, bounded per sweep; `idempotency_keys` gains a
  `definer_purge` policy so the owner can reach rows across tenants
- [x] `IRunQueue.PurgeExpiredAsync` and the worker `MaintenanceService` (configurable `RetentionWindow` / `PurgeInterval`
  / `PurgeBatch`)
- [x] `RetentionTests`: expired keys and old terminal runs (with attempts) are removed; live, recent and active runs are
  untouched; the API role cannot purge. Full suite 331 green

### 3. Isolation and wrap

- [ ] Input/output payload size limits enforced at submission and on handler output (threat T12)
- [ ] Observability doc; threat model T11/T12 verified-by; roadmap and status updated

## Deviations and decisions

| Topic | Decision | Where |
| --- | --- | --- |
| Model provider | Still the deterministic fake; real Anthropic deferred until budget guards exist | ADR-0009 |
| Payload storage | Inline `jsonb`; blob `run_artifacts` deferred | [Data model](../data/data-model.md) |
| Trace continuity | Span **link** to the stored `traceparent`, not parent-child (execution is later, across retries) | ADR-0012 |
