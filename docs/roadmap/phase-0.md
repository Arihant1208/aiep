# Phase 0: Architecture and foundations

**Goal:** a reproducible local environment and an agreed architecture, so Phase 1 can start without re-arguing fundamentals.

**Exit criterion (Spec §22):** a reproducible local environment and an agreed architecture.

## Checklist

### Repository and build

- [x] Modular .NET 10 solution (`Aiep.slnx`): Domain, Application, Infrastructure, ServiceDefaults, Api, Worker ([ADR-0002](../adr/0002-modular-monolith-with-separate-worker.md), [ADR-0003](../adr/0003-dotnet-10-lts.md))
- [x] Central package management, warnings as errors, `.editorconfig`, pinned SDK
- [x] Test projects: domain unit tests (run state machine), API host smoke tests
- [ ] **First successful local build and test run** (`dotnet test Aiep.slnx`), on a machine with the .NET 10 SDK

### Local environment

- [x] Docker Compose: PostgreSQL 17, Azurite, OTel Collector, Jaeger, Prometheus, Grafana ([ADR-0011](../adr/0011-docker-compose-local-managed-containers-cloud.md))
- [x] Least-privilege database roles created at init (`aiep_migrator`, `aiep_api`, `aiep_worker`; none can bypass RLS)
- [x] API and worker Dockerfiles (non-root), `app` compose profile
- [x] Health endpoints (`/health/live`, `/health/ready` with PostgreSQL check), correlation IDs, Problem Details
- [x] OpenTelemetry wiring with OTLP export when configured ([ADR-0012](../adr/0012-opentelemetry-observability.md))
- [ ] **Verify** `docker compose up -d` starts cleanly and traces from the API appear in Jaeger

### CI

- [x] GitHub Actions: restore, format check, build, test, compose validation, image build
- [x] Dependabot for NuGet, Actions and Docker
- [ ] Push to GitHub and get a green first run
- [ ] Add dependency and image vulnerability scanning (for example `dotnet list package --vulnerable`, Trivy). Phase 1.

### Decisions and documents

- [x] ADR process and template ([ADR-0001](../adr/0001-record-architecture-decisions.md))
- [x] ADRs 0002–0015 drafted ([index](../adr/README.md))
- [x] [Vision and scope](../product/vision-and-scope.md)
- [x] [Architecture overview](../architecture/overview.md), [task lifecycle and delivery contract](../architecture/task-lifecycle.md)
- [x] [Threat model v0.1](../security/threat-model.md), [authorization model](../security/authorization-model.md)
- [x] [Data model v0.1](../data/data-model.md)
- [ ] **Sign off Proposed ADRs:** 0005 queue, 0009 provider, 0010 roles, 0013 migrations, 0014 evaluation runtime
- [ ] Choose the identity provider for Phase 1 (see [ADR-0008](../adr/0008-identity-oidc-and-scoped-api-keys.md))

## Deviations from the spec (all recorded in ADRs)

| Spec said | We chose | Why | ADR |
| --- | --- | --- | --- |
| .NET 8 | .NET 10 LTS | .NET 8 support ends 10 Nov 2026 | 0003 |
| RabbitMQ or Redis Streams + outbox | PostgreSQL queue (proposed) | No dual write, native fairness, fewer moving parts | 0005 |
| Python evaluation runner | .NET scorers first (proposed) | One runtime and security model for MVP scorers | 0014 |

## Known limitations of the scaffold

- Package and image versions were set by hand and have not been restored or built yet. The first build may need version bumps.
- The worker's `RunProcessor` is a placeholder loop. Execution arrives in Phase 2.
- No migrations yet. The schema starts in Phase 1 once ADR-0013 is accepted.
