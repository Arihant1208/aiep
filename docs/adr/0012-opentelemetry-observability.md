# ADR-0012: OpenTelemetry for traces, metrics, and logs

- **Status:** Accepted
- **Date:** 2026-11-01
- **Deciders:** Arihant Jain
- **Spec references:** §16, §15.3, FR-021, FR-031, FR-032

## Context

Proving that the platform works, and tracing one run across the API, database, queue, worker and provider, needs
vendor-neutral instrumentation that runs the same locally and in Azure.

## Decision

- Instrument with the **OpenTelemetry .NET SDK**, wired once in `Aiep.ServiceDefaults`.
- Export over **OTLP** to a Collector. Locally: Collector → Jaeger (traces), Prometheus (metrics), debug (logs), viewed in
  Grafana. In Azure: Collector or Azure Monitor OTLP ingestion.
- Platform activity sources and meters are named `Aiep.<Module>` (for example `Aiep.Runs`, `Aiep.Execution`).
- **Trace continuity across async boundaries:** the API stores W3C `traceparent` on the run row; the worker starts its
  execution span with a **span link** to it (a link rather than a parent-child relationship, because execution may happen
  minutes later and across retries).
- Every response carries `X-Correlation-Id` (validated if supplied by the client, otherwise the trace ID).
- **Telemetry hygiene:**
  - No prompts, documents, model outputs, API keys or auth headers in spans, logs or metrics by default.
  - **Metric labels are low-cardinality only**: task type, status, provider, model, error category. Never tenant ID, run ID or user.
  - Per-tenant analysis goes through access-controlled queries on the database or trace search, not metric labels.
- Health endpoints are excluded from tracing.

## Consequences

**Positive:** vendor-neutral; one trace view per run; dashboards from day one.

**Negative:** span links show up less directly in some UIs than parent-child relationships; we must review new
telemetry for payload leaks.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Application Insights SDK directly | Vendor lock-in; awkward locally |
| Serilog sinks only | Logs without traces or metrics; doesn't meet FR-031 |

## Revisit when

Telemetry volume or cost requires tail-based sampling (add it at the Collector).
