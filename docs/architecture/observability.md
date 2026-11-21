# Observability (as built)

*How a run is traced and measured. Decision and rules: [ADR-0012](../adr/0012-opentelemetry-observability.md). Wiring is
in [`Aiep.ServiceDefaults`](../../src/Aiep.ServiceDefaults/ServiceDefaultsExtensions.cs); the hosts collect every
`Aiep.*` source and meter and export over OTLP to the Collector (→ Jaeger / Prometheus / Grafana) when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set.*

## Tracing a run across the async boundary

A run is accepted on the API and executed on the worker, possibly minutes later and across retries. A parent-child span
would misrepresent that, so (ADR-0012):

1. On submit, the API captures the current W3C `traceparent` and stores it on the `task_runs` row.
2. The claim (`claim_next_run`, V0004) returns that `traceparent` to the worker.
3. `RunExecutor` starts the `run.execute` span (source `Aiep.Execution`) with a **span link** to it — one submission
   trace, linked from each execution attempt's span.

The span's attributes are low-cardinality and payload-free: `aiep.handler.type`, `aiep.model.provider`,
`aiep.model.name`, `aiep.attempt.number`, and the terminal `aiep.run.status`. Prompts, inputs and outputs never appear.

## Metrics

Meter `Aiep.Runs`:

| Instrument | Kind | Labels |
| --- | --- | --- |
| `aiep.runs.settled` | counter | handler type, model provider, model name, status |
| `aiep.runs.execution.duration` (ms) | histogram | handler type, model provider, model name, status |
| `aiep.runs.provider.latency` (ms) | histogram | model provider, model name |

Labels are a **closed allowlist** — never tenant id, run id or user (ADR-0012). Per-tenant analysis goes through
access-controlled database or trace-search queries, not metric labels.

## Hygiene, proven by tests

`ExecutionTelemetryTests` runs a real execution with an `ActivityListener` and `MeterListener` and asserts the span is
linked to the submission trace, every span tag and metric label is on the allowlist, and the input payload never appears
in any attribute. This is the automated half of threat-model **T11** (payload leakage in telemetry).

## Correlation and health

Every response carries `X-Correlation-Id` (the client's, if valid, else the trace id). Health endpoints
(`/health/live`, `/health/ready`) are excluded from tracing and are anonymous.
