# ADR-0011: Docker Compose locally, managed containers on Azure

- **Status:** Accepted
- **Date:** 2026-11-01
- **Deciders:** Arihant Jain
- **Spec references:** §14.1, §14.2, §14.3, §23

## Context

Developers need a one-command, reproducible local environment. Production needs managed services, private networking,
secrets and identities without the operational load of running Kubernetes alone.

## Decision

**Local:** `docker-compose.yml` at the repo root.

- `docker compose up -d` starts infrastructure (PostgreSQL, Azurite, OpenTelemetry Collector, Jaeger, Prometheus,
  Grafana). The API and worker run from the IDE or with `dotnet run`.
- `docker compose --profile app up -d --build` also runs the API and worker as containers.
- Local credentials are non-secret, synthetic defaults that `.env` can override. Real provider keys go in
  `dotnet user-secrets`, never in `.env` or the repo.

**Cloud (Phase 5):** Azure Container Apps for API and worker; Azure Database for PostgreSQL Flexible Server; Blob
Storage; Key Vault; managed identities; VNet integration with private endpoints for data services; Azure Monitor with
an OTLP-compatible pipeline. Infrastructure as Code with **Bicep** (to be confirmed in a Phase 5 ADR against Terraform).

Separate resource groups (preferably separate subscriptions) and identities per environment: local, CI, staging, production.

## Consequences

**Positive:** fast onboarding; minimal operations; managed identities remove stored cloud credentials.

**Negative:** Container Apps limits low-level control over node isolation and sandbox runtimes.

## Alternatives considered

| Option | Why not (now) |
| --- | --- |
| AKS from day one | Operational burden not justified until we need custom scheduling, sandbox runtimes or dedicated node pools (Spec §14.2) |
| App Service | Fine for the API, weaker for background workers and scale-to-zero |
| .NET Aspire AppHost for local orchestration | Attractive; Compose is more universal for reviewers. May add an AppHost later without replacing Compose |

## Revisit when

We adopt hardened sandboxes (gVisor/Kata) or need per-tenant node pools. Both point to AKS.
