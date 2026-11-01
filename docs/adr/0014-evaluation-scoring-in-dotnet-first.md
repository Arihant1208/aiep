# ADR-0014: Evaluation scoring in .NET first; Python only when needed

- **Status:** Accepted
- **Date:** 2026-11-01
- **Deciders:** Arihant Jain
- **Spec references:** §5.1 (evaluation runner in Python), §10.3, §10.4

## Context

The spec suggests a Python evaluation runner. The MVP scorers are exact match, field-level precision/recall,
accuracy/F1, schema-valid rate, latency and cost aggregation. All of them are simple arithmetic over stored results.
A second runtime adds a container image, dependency management, a second test toolchain and another service identity
that needs tenant-scoped data access.

## Decision

- Implement MVP scorers in .NET (`Aiep.Application/Evaluation`), executed by the worker as evaluation-run handlers.
- Each scorer has a name and **version**. Every result records the scorer version (Spec §10.2).
- Reports export raw per-example results as CSV/JSON, so anyone can re-analyse them in Python or notebooks
  **outside** the platform.
- Add a Python runner only for a concrete need: LLM-as-judge with established libraries, embeddings-based similarity or
  statistical tests we don't want to re-implement.

## Consequences

**Positive:** one runtime and one security model; scorers run under the same tenant context rules as everything else.

**Negative:** less convenient for ML-heavy scoring later.

## Alternatives considered

| Option | Why not (now) |
| --- | --- |
| Python service from day one | Second runtime and identity for arithmetic we can do in C# |
| Notebooks only | Not reproducible from the platform; violates FR-033/FR-034 |

## Acceptance criteria

- [x] Project owner agrees to defer Python.
