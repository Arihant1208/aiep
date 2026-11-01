# Local development setup

## Prerequisites

| Tool | Version | Check |
| --- | --- | --- |
| .NET SDK | 10.0.100+ | `dotnet --list-sdks` |
| Docker Desktop (or Docker Engine + Compose v2) | recent | `docker compose version` |
| Git | any recent | `git --version` |

## First run

```bash
docker compose up -d
dotnet test Aiep.slnx
dotnet run --project src/Aiep.Api
```

The API listens on `http://localhost:5080`. Check `http://localhost:5080/health/live` and `/health/ready` (ready also
checks PostgreSQL).

To run the API and worker as containers too:

```bash
docker compose --profile app up -d --build
```

The containerised API listens on `http://localhost:8080`.

## Local services

| Service | URL / port | Notes |
| --- | --- | --- |
| PostgreSQL | `localhost:5432`, db `aiep` | Roles created by `db/init/01-roles.sh` on first volume creation |
| Azurite (Blob) | `localhost:10000` | Default Azurite dev account |
| OTel Collector | `4317` gRPC, `4318` HTTP | Apps export here when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (launch profiles set it) |
| Jaeger | http://localhost:16686 | Traces |
| Prometheus | http://localhost:9090 | Metrics |
| Grafana | http://localhost:3000 | Anonymous admin, **local only** |

## Database roles

| Role | Used by | Notes |
| --- | --- | --- |
| `aiep_migrator` | Migration runner | Owns schema `aiep`. Never used by running services. |
| `aiep_api` | API | `NOBYPASSRLS`, not owner, so RLS always applies |
| `aiep_worker` | Worker | `NOBYPASSRLS`, not owner |

Default local passwords are in `.env.example`. They are synthetic and local-only. Copy it to `.env` to override.

## Secrets

- **Never** put real provider keys in `.env`, `appsettings*.json` or the repo.
- Use user secrets for anything real:

```bash
dotnet user-secrets --project src/Aiep.Worker set "Providers:Anthropic:ApiKey" "<your-key>"
```

## Resetting

The init script runs only when the Postgres volume is first created. To re-run it:

```bash
docker compose down -v
```

This **deletes all local data**.

## Conventions

- Format before pushing: `dotnet format Aiep.slnx`. CI fails on formatting drift.
- Warnings are errors (`Directory.Build.props`).
- Package versions live only in `Directory.Packages.props`.
- A PR that changes behaviour updates the relevant doc. A PR that makes a significant choice adds an ADR.
