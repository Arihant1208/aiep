# ADR-0002: Modular monolith with a separately deployed worker

- **Status:** Accepted
- **Date:** 2026-11-01
- **Deciders:** Arihant Jain
- **Spec references:** §5, §5.1

## Context

The platform has distinct concerns: identity and tenancy, task definitions, run lifecycle, execution, evaluation and
usage/quotas. One engineer builds it on a ~12-week timeline. Splitting into microservices early adds network contracts,
distributed transactions, separate pipelines and more failure modes. None of that is justified by the scaling or
team structure yet.

Two things do need a hard runtime split. The **API** serves untrusted callers and must stay responsive. The **worker**
calls external model providers, runs long operations, and should run under a different, less-privileged identity.

## Decision

One .NET solution, organised as layered projects with module folders inside them:

| Project | Responsibility | May depend on |
| --- | --- | --- |
| `Aiep.Domain` | Entities, value objects, state machine, invariants. No framework dependencies. | — |
| `Aiep.Application` | Use cases, authorization decisions, ports (interfaces) | Domain |
| `Aiep.Infrastructure` | PostgreSQL, object storage, model-provider adapters, queue | Application, Domain |
| `Aiep.ServiceDefaults` | Telemetry and health-check wiring shared by hosts | — |
| `Aiep.Api` | HTTP host: authentication, endpoints, request validation | Application, Infrastructure, ServiceDefaults |
| `Aiep.Worker` | Background host: claim, execute, reconcile, dispatch | Application, Infrastructure, ServiceDefaults |

Deployable units for the MVP: **API**, **Worker** (which also runs the scheduler and reconciliation loops, using
database leases so multiple replicas are safe) and the **telemetry stack**.

Module boundaries (Tenancy, Definitions, Runs, Execution, Evaluation, Usage) are folders and namespaces inside the
layers. A module talks to another only through Application-layer interfaces, never by reaching into another
module's tables.

The API and the worker connect to PostgreSQL with **different database roles** (`aiep_api`, `aiep_worker`).

## Consequences

**Positive:** one build, one deployment pipeline, in-process calls, transactional consistency across modules,
simple local development.

**Negative:** module boundaries are enforced by convention and review, not by the network. Coupling can creep in.

**Mitigation:** add architecture tests (for example NetArchTest) in Phase 1 to enforce the dependency rules above.

## Alternatives considered

| Option | Why not (now) |
| --- | --- |
| Microservice per module | Distributed-systems cost with no scaling or ownership benefit at this stage |
| Single process doing API and execution | Long provider calls compete with API latency; API and worker can't run under separate least-privilege identities |
| Vertical-slice projects per module | Reasonable, but more projects to manage; layer-first with module folders is enough at this size |

## Revisit when

- A module needs independent scaling that worker replicas can't provide (for example, evaluation runs swamping task runs).
- A security boundary requires separate deployment (for example, a hardened sandbox runner, see ADR-0007).
- Scheduler/reconciler coordination inside the worker gets complicated enough to justify its own process.
