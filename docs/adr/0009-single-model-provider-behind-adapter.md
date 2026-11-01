# ADR-0009: One model provider behind an adapter, plus a deterministic fake

- **Status:** Proposed
- **Date:** 2026-11-01
- **Deciders:** Arihant Jain (pending sign-off)
- **Spec references:** §10.1, §10.2, §24

## Context

The orchestration layer must not depend on one vendor's peculiarities (Spec §10.1). Supporting several providers in
the first release multiplies error mapping, usage accounting and test surface. Load tests and CI also need model
behaviour that is free, fast and reproducible. Calling a real provider from k6 would measure the provider, cost money
and give noisy results.

## Decision

- Define an `IModelProvider` port in Application with normalized request/response, usage (`input_tokens`,
  `output_tokens`, when available), error categories (`RateLimited`, `Timeout`, `Transient`, `InvalidRequest`,
  `ContentRefused`, `AuthFailed`), retry hints and cancellation.
- Implement **one real provider** for the MVP.
  - **Recommended:** Anthropic's Messages API (current models: `claude-sonnet-5-5` as the default and
    `claude-haiku-4-5-20251001` as the cheaper comparison point, which gives evaluation suites a real quality/cost trade-off
    to show). Record exact model IDs on every run (Spec §10.2).
- Implement a **deterministic fake provider** (`FakeModelProvider`): configurable latency distribution, error rate,
  rate-limit injection and canned/schema-valid outputs seeded per input. It's used for CI, load tests and fault injection.
  Benchmarks must state which provider they used.
- Provider credentials live in the secret manager, referenced by `ProviderConnections`. The worker resolves them at
  execution time and never logs them.
- Pricing used for cost estimates is a versioned table (`provider`, `model`, `effective_from`, prices, source URL).
  Estimates record the pricing version they used (Spec §18.2).

## Consequences

**Positive:** one integration done well; benchmarks are reproducible; a second provider later becomes an adapter
exercise, which validates the abstraction.

**Negative:** quality comparisons in the first release are within a single vendor's model family.

## Alternatives considered

| Option | Why not (now) |
| --- | --- |
| Multiple providers in v1 | Doubles adapter, error-mapping and pricing work before the core path is proven |
| Use an aggregation gateway (for example LiteLLM) | Adds a dependency and hides the error and usage normalization work this project should demonstrate |
| Real provider only, no fake | Expensive, slow, non-deterministic load tests and CI |

## Revisit when

The first vertical slice is done and the evaluation phase would benefit from a cross-vendor comparison.

## Acceptance criteria

- [ ] Project owner confirms the provider (or picks another, such as Azure OpenAI, to stay within Azure).
- [ ] Account and budget limits set on the provider side before any real calls.
