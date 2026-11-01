# ADR-0005: PostgreSQL-backed work queue for the MVP

- **Status:** Accepted
- **Date:** 2026-11-01
- **Deciders:** Arihant Jain
- **Spec references:** §5.2, §9.2, §9.4, §9.5, §23 (spec preferred RabbitMQ)

## Context

The spec asks for a durable queue (Redis Streams or RabbitMQ) plus a transactional outbox to avoid the
database/queue dual-write problem, worker leases, retry scheduling and per-tenant fairness.

With an external broker, we would implement and operate:

1. an outbox table and a dispatcher that publishes to the broker;
2. broker acknowledgements **and** database leases. These are two overlapping delivery mechanisms that must agree;
3. retry delays, through broker delayed-message plugins or a separate scheduler;
4. per-tenant fairness. RabbitMQ has no fair-share scheduling across tenants on a shared queue. It needs a queue per
   tenant or application-level scheduling anyway.

The MVP target is roughly hundreds to low thousands of runs per minute. PostgreSQL handles that comfortably with
`FOR UPDATE SKIP LOCKED`.

## Decision

Use PostgreSQL as the work queue for the MVP, behind an application-layer port (`IRunQueue` or similar), so that a
broker can replace it later without changing orchestration code.

Mechanics:

- **Enqueue** = the run row is inserted with status `Queued` and `next_attempt_at = now()` *in the same transaction* as
  acceptance. There is no dual write and no outbox needed for dispatch.
- **Claim** = a worker selects eligible runs with `FOR UPDATE SKIP LOCKED`, sets `Running`, `lease_owner` and
  `lease_expires_at`, and creates an attempt row in one transaction.
- **Fairness** = the claim query picks across organizations (for example round-robin by the organization with the
  fewest currently-running runs, within per-org and per-workspace concurrency limits). See Spec §9.5.
- **Heartbeat** = workers extend `lease_expires_at` while executing.
- **Recovery** = the reconciler returns runs with expired leases to `RetryWait`/`Queued`, counting the attempt.
- **Retry** = `RetryWait` with `next_attempt_at = now() + backoff(attempt) ± jitter`.
- **Wake-up latency** = `LISTEN/NOTIFY` on enqueue, with polling as a fallback (5 s default).
- The **outbox pattern remains** for *outbound integration events* (webhooks, external notifications) if and when we add them.

Delivery contract: **at-least-once execution attempts, at most one terminal outcome per logical run.** See
[task lifecycle](../architecture/task-lifecycle.md).

## Consequences

**Positive:** removes the outbox dispatcher, the second ack mechanism, a broker to operate and a class of
consistency bugs. Fairness and quotas are SQL queries over the same rows. One fewer container locally and in the cloud.

**Negative:** queue load lands on the primary database; polling and claim queries need careful indexing; less
impressive-sounding than "RabbitMQ" on a CV (the trade-off analysis is the portfolio artifact).

**Risks and mitigations:**

- *Lock contention / bloat on `task_runs`*: a partial index on `(next_attempt_at) WHERE status IN ('Queued','RetryWait')`;
  tune autovacuum for the table; measure with k6 in Phase 3.
- *Lock-in*: the `IRunQueue` port and contract tests let us swap the implementation.

## Alternatives considered

| Option | Why not (now) |
| --- | --- |
| RabbitMQ + outbox (spec preference) | Two delivery mechanisms to reconcile; no native per-tenant fairness; another stateful service to run |
| Redis Streams + outbox | Same dual-write and fairness issues; Redis durability needs AOF tuning; we don't otherwise need Redis |
| Azure Service Bus | Good managed option for later; awkward locally; same fairness gap |
| Hangfire / Quartz on PostgreSQL | Generic job semantics that don't map cleanly to our run state machine and fairness rules |

## Revisit when

- Sustained claim throughput approaches database limits in load tests (for example claim P95 > 50 ms, or database CPU > 60 % from queue queries).
- We need fan-out to multiple independent consumers or cross-region delivery.
- Execution moves to a separately scaled runtime that shouldn't hold database connections.

## Acceptance criteria

- [x] Project owner agrees to deviate from the spec's RabbitMQ preference.
- [ ] Phase 2 spike: claim/lease/heartbeat/recovery prototype with an integration test that kills a worker mid-run.
