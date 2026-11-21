# Phase 4: Evaluation and cost governance

**Goal:** compare two configurations on one dataset and explain the quality, latency and cost trade-offs, with budgets
enforced.

**Exit criterion (Spec §22):** compare two configurations on one dataset; explain the quality/latency/cost trade-offs.

Scorers are .NET and versioned ([ADR-0014](../adr/0014-evaluation-scoring-in-dotnet-first.md)); pricing is a versioned
table and every cost records the pricing it used ([ADR-0009](../adr/0009-single-model-provider-behind-adapter.md)). Phase 4
runs on the deterministic fake provider with **two model profiles** (`fake-sonnet` vs `fake-haiku`), giving a real
quality/latency/cost gradient with no spend; the real Anthropic adapter stays a drop-in behind `IModelProvider`.

## Slices

### 1. Pricing and cost

- [x] `model_pricing` (V0006, global/versioned, read-only to services): provider, model, effective_from, input/output
  price per Mtok, source URL; seeded for `fake-sonnet` and `fake-haiku`
- [x] Two deterministic `FakeModelProvider` profiles: `fake-sonnet` (higher latency, 2× output tokens) and `fake-haiku`
  (lower), so configurations differ measurably in latency and cost
- [x] Per-run cost: `IPricing` resolves the pricing row in effect; on success the worker stores `cost_usd` and the
  `pricing_id` on the attempt and run. `PricingAndCostTests`: the costlier profile costs more, a model with no pricing
  row has no cost, and the cost equals tokens × pricing. Full suite 336 green

### 2. Evaluation

- [x] Datasets (V0007): evaluation suites → immutable versions holding examples (`[{input, expected}]`), workspace-scoped,
  with the same publish/immutability pattern as task definitions
- [x] `IEvaluationStore` / `EvaluationStore`: creating an evaluation run fans out one task_run per example against the
  task-definition version (the worker executes them like any run); `ScoreAsync` runs the versioned scorers over the
  completed task_runs, writes per-example results and the run summary, and is idempotent / waits until all task_runs finish
- [x] Versioned `IScorer` (`ExactMatchScorer`, deep JSON equality); summary aggregates exact-match rate, succeeded count,
  avg/p95 latency and total/avg cost. `EvaluationTests` prove fan-out → execute → score → summary (50% match on a crafted
  dataset), that scoring waits for completion, and that an unpublished dataset version is rejected. Full suite 339 green

### 3. Comparison and export (headline)

- [ ] Compare two eval runs (two configurations) on one dataset: quality, latency (p50/p95) and cost per configuration
- [ ] Raw per-example results exported as CSV/JSON (ADR-0014); API endpoints. Tests prove the exit criterion

### 4. Cost governance

- [ ] Reserve-then-settle quota ledger; per-organization/workspace budgets; reserve at submit (429 on exceed), settle the
  actual cost at completion, release on failure — completes threat T12
- [ ] Tests: a tenant over budget is refused; settle and release adjust the ledger

## Deviations and decisions

| Topic | Decision | Where |
| --- | --- | --- |
| Provider | Two deterministic fake profiles; real Anthropic adapter deferred (drop-in behind `IModelProvider`) | ADR-0009 |
| Scorers | .NET, versioned; raw results exported for external re-analysis | ADR-0014 |
| Pricing | Versioned `model_pricing` table; every cost records the pricing id it used | ADR-0009 |
