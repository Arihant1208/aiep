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
dotnet run --project src/Aiep.Migrator     # applies db/migrations as aiep_migrator; safe to re-run
dotnet test Aiep.slnx                      # Aiep.Database.Tests starts its own Postgres container (needs Docker)
dotnet run --project src/Aiep.Api
```

The API listens on `http://localhost:5080`. Check `http://localhost:5080/health/live` and `/health/ready` (ready also
checks PostgreSQL).

To run the API and worker as containers too:

```bash
docker compose --profile app up -d --build
```

The `migrator` service runs first and exits; the API and worker start only if it succeeded
(`docker compose logs migrator` shows what it applied).

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
| Keycloak | http://localhost:8081 (admin console: `admin` / `keycloak_local_dev`) | Realm `aiep` from `deploy/keycloak/aiep-realm.json`, users `alice` / `alice-local-dev`, `bob` / `bob-local-dev`. Dev mode takes 1–3 minutes to start ([ADR-0016](../adr/0016-keycloak-oidc-provider.md)) |

## Database roles

| Role | Used by | Notes |
| --- | --- | --- |
| `aiep_migrator` | `Aiep.Migrator` | Owns schema `aiep` and the `SECURITY DEFINER` functions. Never used by running services. |
| `aiep_api` | API | `NOBYPASSRLS`, not owner, so RLS always applies |
| `aiep_worker` | Worker | `NOBYPASSRLS`, not owner |

Default local passwords are in `.env.example`. They are synthetic and local-only. Copy it to `.env` to override.

## Authentication locally

The API validates tokens from the Compose Keycloak and API keys peppered with `Authentication:ApiKeys:Pepper`
(synthetic default in `appsettings.Development.json` and `.env.example`). Details: [authentication](../security/authentication.md).
The realm has no password-grant client on purpose, so there is no `curl` recipe for a token. Use API keys for manual
testing once the key endpoints exist (Phase 1 step 6).

`Aiep.Api.Tests` starts its own PostgreSQL and Keycloak containers. The first run pulls the Keycloak image (~500 MB).

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

This **deletes all local data**. Run the migrator again afterwards (the `app` profile does it for you).

`Aiep.Database.Tests` doesn't use the Compose database: it starts a throwaway `postgres:17-alpine` container, runs the
same `01-roles.sh`, and migrates it with the real runner.

## Conventions

- Format before pushing: `dotnet format Aiep.slnx`. CI fails on formatting drift.
- Warnings are errors (`Directory.Build.props`).
- Package versions live only in `Directory.Packages.props`.
- A PR that changes behaviour updates the relevant doc. A PR that makes a significant choice adds an ADR.
