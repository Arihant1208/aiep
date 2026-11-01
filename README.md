# AIEP: AI Inference & Evaluation Platform

A multi-tenant platform that accepts AI tasks from many customers, executes them reliably with isolation, measures
their performance, quality and cost, and gives each customer a secure workspace for their tasks, results and evaluations.

The engineering focus is not calling an LLM. It is running thousands of tasks for different tenants **safely**:
keeping their data separate, enforcing permissions, recovering from failures, controlling cost, and proving all of
it with tests and measurements.

> **Status:** Phase 0 (foundations). See the [roadmap](docs/roadmap/README.md).

## Quick start

Prerequisites: .NET 10 SDK, Docker.

```bash
docker compose up -d
dotnet test Aiep.slnx
dotnet run --project src/Aiep.Api
```

Then open http://localhost:5080/health/ready. Full instructions: [local setup](docs/development/local-setup.md).

## Repository layout

```
src/
  Aiep.Domain            entities, run state machine, invariants (no dependencies)
  Aiep.Application       use cases, authorization, ports
  Aiep.Infrastructure    PostgreSQL, storage, model providers
  Aiep.ServiceDefaults   telemetry + health checks shared by hosts
  Aiep.Api               HTTP host
  Aiep.Worker            background execution host
tests/                   unit, integration and security tests
db/                      database init scripts and migrations
deploy/                  local observability configuration
docs/                    decisions (ADRs), architecture, security, data, roadmap
```

## Documentation

Start at [docs/README.md](docs/README.md). Key decisions are in the [ADR index](docs/adr/README.md).

| Guarantee | Where it's designed | How it's proven |
| --- | --- | --- |
| Tenant isolation | [ADR-0006](docs/adr/0006-tenant-isolation-shared-database-rls.md) | Cross-tenant negative test suite (Phase 1) |
| Durable, recoverable execution | [Task lifecycle](docs/architecture/task-lifecycle.md) | Crash and duplicate-delivery fault injection (Phase 2) |
| Bounded execution | [ADR-0007](docs/adr/0007-trusted-allowlisted-handlers-only.md) | Resource-limit and cleanup tests (Phase 3) |
| Measurable quality and cost | [ADR-0014](docs/adr/0014-evaluation-scoring-in-dotnet-first.md) | Reproducible benchmark reports with raw data (Phase 4) |
