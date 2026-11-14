# Session handoff: AIEP, end of Phase 0 → start of Phase 1

> **For the next Claude session:** read this file, then `CLAUDE.md`, then `docs/README.md`. Everything below is
> current as of **2026-11-09**. The project owner is **Arihant Jain**. The previous session ran in the Claude desktop app on
> Windows. This one continues in **WSL2 (Ubuntu 26.04)** at `~/projects/aiep`.
> When Phase 1 is done, update or delete this file. It's a handoff note, not permanent documentation.

---

## 1. What this project is

**AIEP (AI Inference & Evaluation Platform)** is a portfolio-grade, multi-tenant platform that accepts AI tasks from many
customers, executes them reliably with isolation, measures latency, cost and quality, and gives each customer a secure
workspace. The engineering focus is **tenant isolation, authorization, durable execution, observability and
evaluation**, not "calling an LLM".

Source of truth for requirements: the owner's long spec ("AI Inference & Evaluation Platform: Complete Product and
Technical Specification", 26 sections, FR-001…FR-036). Docs cite it as `Spec §N`.

- [ ] **Owner action:** save that spec as `docs/spec/aiep-spec.md` so future sessions can read it. It was pasted into
  chat and isn't in the repo yet. If it's still missing, ask the owner for it rather than guessing at section contents.

---

## 2. How to start the new session

```bash
cd ~/projects/aiep
git config user.name "Arihant Jain"
git config user.email "arihant.jain@nightingalehq.ai"
claude
```

First prompt suggestion: *"Read HANDOFF.md and continue with Phase 1, step 1."*

---

## 3. History: what happened and why

| Date | Event |
| --- | --- |
| 2026-11-01 | Owner pasted the spec. Claude reviewed it and proposed changes: PostgreSQL as the queue instead of RabbitMQ; a clear org-role vs workspace-role model; reserve-then-settle quotas; a concrete RLS-with-pooling rule; webhook SSRF risk; defer Python; trim scope. |
| 2026-11-01 | Owner asked for (1) a repo scaffold, (2) a `docs/` folder with **clear, manageable records of every decision and why**, (3) Phase 0 docs, then implementation. |
| 2026-11-01 | Scaffold and docs written (no SDK on Windows, so not compiled). Found that **.NET 8 support ends 2026-11-10**, so switched to **.NET 10 LTS** (ADR-0003). |
| 2026-11-01 | Owner: **"accept all, use Keycloak, start Phase 1"**. ADRs 0005, 0009, 0010, 0013 and 0014 accepted. Migration runner chosen: **in-house** (ADR-0013). |
| 2026-11-01 → 09 | Moved tooling to WSL. Installed `dotnet-sdk-10.0` (10.0.114), `docker.io` (29.1.3), `docker-compose-v2` (2.40.3) via apt. Hit a broken GitHub CLI apt key (`/usr/share/keyrings/githubcli-archive-keyring.gpg` missing) and fixed it by re-downloading the key. |
| 2026-11-09 | Repo cloned into `~/projects/aiep`. **First build:** NuGet audit failed restore on OpenTelemetry 1.12.0 advisories, so bumped all packages to current stable. Then **build, format and 28/28 tests passed**, and the **full compose stack was verified** (details in §5). Fixed `OTEL_SERVICE_NAME` being ignored. The desktop app couldn't open WSL folders, so the work continues in the CLI inside WSL. |

### Owner preferences (keep following these)

- Wants **every significant decision recorded as an ADR**, with context, alternatives and the reason. Decision history
  matters as much as the code.
- Accepted ADRs are **immutable**: change a decision with a new superseding ADR (that's why Keycloak gets ADR-0016 and ADR-0008 isn't edited).
- Prefers being told plainly about deviations from the spec, with the reason.
- Is building this as a portfolio and interview piece: **tests and measurements that prove claims** matter more than feature count.

---

## 4. Decisions in force (all Accepted, see `docs/adr/README.md`)

| ADR | Decision (one line) |
| --- | --- |
| 0001 | Record decisions as ADRs; Proposed → Accepted → Superseded; Accepted ADRs are frozen |
| 0002 | Modular monolith (Domain / Application / Infrastructure / ServiceDefaults / Api / Worker); worker deployed separately, with its own database role |
| 0003 | **.NET 10 LTS** (spec said .NET 8, which goes out of support on 2026-11-10) |
| 0004 | PostgreSQL 17 is the system of record; queue and caches are never authoritative; blobs in object storage |
| 0005 | **PostgreSQL-backed queue** (`FOR UPDATE SKIP LOCKED`, leases, heartbeat, reconciler, LISTEN/NOTIFY) behind an `IRunQueue` port; no RabbitMQ; outbox only for future outbound events |
| 0006 | Shared database plus **three layers**: app authorization, composite FKs, RLS **enabled and forced**. Tenant context only through `SET LOCAL`/`set_config(...,true)` inside a transaction. Services connect as non-owner, `NOBYPASSRLS` roles |
| 0007 | Only trusted, allowlisted, platform-authored handlers; no customer code; model output is untrusted data |
| 0008 | OIDC for humans (roles kept in AIEP, not the IdP); API keys `aiep_<env>_<keyId>_<secret>`, peppered SHA-256, scope ⊆ owner's current permissions, Bearer header only |
| 0009 | One real provider (**Anthropic**, `claude-sonnet-5-5` default, `claude-haiku-4-5-20251001` for comparison) plus a **deterministic fake provider** for CI and load tests. Versioned pricing table |
| 0010 | **Org roles** Owner / Admin / Auditor / Member, plus **workspace roles** WorkspaceAdmin / Developer / Evaluator / Viewer. Effective = org-implied ∪ workspace grant (∩ key scope). Member alone = no workspace access. Matrix in `docs/security/authorization-model.md` |
| 0011 | Docker Compose locally (`app` profile for API and worker); Azure Container Apps + Postgres Flexible Server + Key Vault + managed identity later |
| 0012 | OpenTelemetry → OTLP → Collector → Jaeger / Prometheus / Grafana; no payloads in telemetry; low-cardinality labels only; worker spans *link* to the stored API `traceparent` |
| 0013 | Schema in **SQL migrations** (`db/migrations/V####__name.sql`) applied by an **in-house `Aiep.Migrator`** as `aiep_migrator`; EF Core for typed queries (no EF migrations); raw Npgsql for hot paths; one tenant-context unit of work |
| 0014 | Evaluation scorers in .NET, versioned; Python only for a concrete need |
| 0015 | `/v1`; prefixed opaque IDs (UUIDv7 inside); **404 to hide unauthorized resources**, 403 only when the resource is visible; RFC 9457 Problem Details with stable `code`; `Idempotency-Key` on submissions; cursor pagination |
| 0016 | **Keycloak** as the OIDC provider for dev/CI/tests; realm-as-code; IdP roles ignored; production hosting deferred to Phase 5 |

Still open (unticked, not blocking Phase 1): ADR-0005 Phase 2 queue spike; ADR-0009 provider-side budget limits before real calls.

---

## 5. Current state (verified 2026-11-09 in WSL)

```
src/Aiep.Domain           TaskRunStatus + TaskRunTransitions (state machine; single source of truth)
src/Aiep.Application      empty (use cases / authz / ports go here)
src/Aiep.Infrastructure   NpgsqlDataSource registration (lazy), PostgresHealthCheck
src/Aiep.ServiceDefaults  OpenTelemetry + health checks (/health/live, /health/ready)
src/Aiep.Api              minimal host: CorrelationIdMiddleware, ProblemDetails, health
src/Aiep.Worker           RunProcessor placeholder loop (5 s tick)
tests/Aiep.Domain.Tests   21 tests (state machine)
tests/Aiep.Api.Tests      7 tests (liveness, correlation id, problem+json 404)
db/init/01-roles.sh       creates aiep_migrator (schema owner), aiep_api, aiep_worker; schema `aiep`
docker-compose.yml        postgres:17, azurite, otel-collector, jaeger, prometheus, grafana; api+worker under profile `app`
.github/workflows/ci.yml  restore → format check → build → test; compose validate + image build
docs/                     ADRs 0001–0015, architecture, task lifecycle, threat model, authz model, data model, setup, roadmap
```

Verification results:

- `dotnet build Aiep.slnx`: succeeded, 0 warnings (warnings are errors).
- `dotnet format --verify-no-changes`: clean.
- `dotnet test`: **28/28 passed**.
- `docker compose --profile app up -d --build`: all 8 services running; roles have `rolbypassrls=f`, `rolsuper=f`;
  `/health/live` 200, `/health/ready` 200 (connects as `aiep_api`); `X-Correlation-Id` present; unknown route →
  404 `application/problem+json`; traces in Jaeger under `aiep-api`; `http_server_*` and `dotnet_gc_*` metrics in Prometheus.
- Stack was stopped with `docker compose --profile app down` (volumes kept).

Not done yet:

- **GitHub:** public repo [Arihant1208/aiep](https://github.com/Arihant1208/aiep), `origin/main`. First CI run green on
  2026-11-10 (build, format, all tests including Testcontainers on `ubuntu-latest`, compose validation, image builds).
- No vulnerability scanning step in CI yet (Phase 1 item).

---

## 6. Phase 1: Control plane and tenant security (next)

**Exit criterion:** users can only access resources authorized for their identity and scope, and tests prove it.

Do these in order, as vertical slices. Each step: code + tests + doc/ADR updates, then commit.

1. ✅ **Done 2026-11-09:** [ADR-0016](docs/adr/0016-keycloak-oidc-provider.md) written and Accepted (Keycloak 26.8.2 pinned;
   no dev-only token issuer; ADR-0008 status line notes the partial supersession). Original brief kept below for reference.
   **ADR-0016: Keycloak as the OIDC provider.**
   - Context: ADR-0008 left the IdP open; the owner chose Keycloak. Reasons to record: open source, runs locally in
     Compose (realistic OIDC in dev and CI instead of a fake issuer), standard OIDC so the platform can move to Entra or Auth0 later.
   - Decision points to record: Keycloak in Compose (`quay.io/keycloak/keycloak`, pin a current version, `start-dev`
     locally only); a realm export checked in at `deploy/keycloak/aiep-realm.json` with clients `aiep-api` (bearer-only
     audience) and `aiep-web`/`aiep-cli` (public, PKCE), plus synthetic test users; tokens validated with JWT bearer
     (issuer, audience, signature through JWKS, lifetime); **roles stay in AIEP** (Keycloak roles and groups ignored);
     production Keycloak hosting is a Phase 5 decision.
   - Consequences: another container locally; Keycloak's realm export format changes between versions (pin it).
   - Update `docs/adr/README.md` index and `docs/roadmap/phase-0.md` reference.

2. ✅ **Done 2026-11-09:** `src/Aiep.Migrator`, `db/migrations/V0001__tenancy_baseline.sql`, `migrator` Compose service,
   `tests/Aiep.Database.Tests` (61 tests, Testcontainers). Bootstrap design recorded as
   [ADR-0017](docs/adr/0017-pre-tenant-operations-security-definer.md), **Proposed** pending owner sign-off.
   Progress now lives in [docs/roadmap/phase-1.md](docs/roadmap/phase-1.md). Original brief kept below.
   Gotcha for step 3: `FORCE` RLS binds the table owner too, so `aiep_migrator` sees no tenant rows without context.
   `tests/Aiep.Database.Tests/TenantDb.cs` is a test-only stand-in for the unit of work; replace its use with the real
   one in step 3 where sensible.
   **`Aiep.Migrator` + baseline schema.**
   - New console project `src/Aiep.Migrator` (add to `Aiep.slnx`). Applies `db/migrations/V*.sql` in order as
     `aiep_migrator`; table `aiep.schema_migrations(version, name, checksum_sha256, applied_at)`;
     `pg_advisory_lock` so concurrent runs are safe; each script in its own transaction; **fails if an applied
     script's checksum changed**; idempotent re-runs. Add a `migrator` compose service (profile `app`) that the API and worker depend on.
   - `V0001__tenancy_baseline.sql`: `subjects`, `organizations`, `memberships`, `workspaces`,
     `workspace_memberships`, `api_keys`, `audit_events`, following `docs/data/data-model.md` (UUIDv7 ids,
     `organization_id` on everything tenant-owned, `UNIQUE (organization_id, id)`, composite FKs, RLS
     `ENABLE` + `FORCE`, policy using `nullif(current_setting('app.organization_id', true), '')::uuid`).
     `audit_events`: revoke UPDATE/DELETE from service roles. `subjects` isn't tenant-owned. Decide and document
     its access rule (for example, readable only through a `SECURITY DEFINER` lookup function).
   - Org-creation bootstrap is a chicken-and-egg problem (no tenant context exists yet). Handle it with a narrow
     `SECURITY DEFINER` function `aiep.create_organization(...)` that creates the org and the Owner membership atomically. Document why.

3. ✅ **Done 2026-11-10:** `TenantContext` (Application), `TenantDatabase` / `TenantSession` / `AiepDbContext`
   (Infrastructure), 21 tests in `TenantDatabaseTests`. Use `TenantDatabase.ExecuteAsync` for every tenant access from
   now on; raw SQL goes through `session.Connection` inside Infrastructure only. Original brief kept below.
   **Tenant-context unit of work** (`Aiep.Infrastructure`).
   - One way in: `ITenantScope` / `IUnitOfWork` opens a connection and transaction and runs
     `select set_config('app.organization_id', $1, true), set_config('app.subject_id', $2, true)`. EF Core
     `DbContext` uses that connection and transaction. No other database access path for tenant data.
   - Add EF Core packages (Npgsql EF provider, current stable). Mappings only, no EF migrations.
   - **Tests (Testcontainers.PostgreSql; reuse `db/init/01-roles.sh` + migrator):** no context → 0 rows; wrong
     org → 0 rows and inserts rejected by `WITH CHECK`; right org → rows; **pool size 1, alternating tenants →
     no carry-over**; `aiep_api` can't `SET ROLE`/bypass; composite FK rejects a cross-tenant child.

4. ✅ **Done 2026-11-10:** Keycloak in Compose, JWT + API key authentication, `GET /v1/me`, 32 API tests against real
   Postgres + Keycloak, 47 Application unit tests. See [docs/security/authentication.md](docs/security/authentication.md).
   Follow-ups: Npgsql logs `libgssapi_krb5.so.2` not found at connect in the container (harmless; consider
   `GSS Encryption Mode=Disable`); pepper rotation; auth failure rate limiting. Original brief kept below.
   **Authentication.**
   - Keycloak service and realm import in `docker-compose.yml`; JWT bearer validation in the API; map
     `(iss, sub)` → `subjects` row (create on first login).
   - API keys per ADR-0008: generation, peppered SHA-256 verification (pepper from configuration / user-secrets
     locally, Key Vault later), constant-time compare, expiry and revocation, `last_used_at` (write it throttled),
     custom authentication handler. Reject keys in query strings.
   - Integration tests get real tokens from Keycloak (Testcontainers.Keycloak or the compose instance).
   - ADR-0016 commits to two tests: a token carrying a Keycloak admin/realm role grants nothing in AIEP, and a token
     signed by an unknown key is rejected. Only RS256/ES256 accepted; HTTPS metadata may be off only in `Development`.

5. ✅ **Done 2026-11-14:** `src/Aiep.Application/Authorization` (`Permission`, `OrgRole`/`WorkspaceRole`,
   `RolePermissions`, `AuthorizationSubject`, `IAuthorizationService`, `AuthorizationInvariants`), 17 tests in
   `Aiep.Application.Tests/Authorization`. The role table is the code copy of the matrix in
   `docs/security/authorization-model.md`; `RolePermissionsMatrixTests` parses the doc and fails on drift (it caught a
   missing Auditor `keys:read` on first run). Footnoted cells grant the permission; the condition is a step-6 policy at
   the endpoint. `IAuthorizationService` returns `NotAMember` (→404) / `MissingPermission` (→403). Not yet wired to
   endpoints — that's step 6. Original brief kept below.
   **Authorization** (`Aiep.Application`).
   - `Permission` constants, a role→permission table built from the matrix in `docs/security/authorization-model.md`
     (that doc is the spec, and a unit test asserts the table matches it), an `IAuthorizationService` implementing the
     8-step algorithm in that doc, effective permissions = org-implied ∪ workspace grant ∩ key scope.
   - Invariant tests: Member has no workspace access; nobody grants a permission they lack; at least one Owner always
     remains; key permissions shrink when the owner loses access.

6. **Endpoints** (`/v1`, ADR-0015): organizations (create, get), members (list, invite/add, change role, remove),
   workspaces (create, list, get, archive), workspace grants, API keys (create, which returns the secret once; list; revoke),
   `GET /v1/me/permissions` (the debugging aid from ADR-0010). Prefixed opaque IDs; 404 concealment;
   Problem Details `code`s; audit events for every administrative action.

7. **Security test suite** (Spec §6.5, the headline Phase 1 deliverable): two orgs, A and B; as A, try to read, update,
   delete, enumerate and guess IDs for every B resource through every endpoint, by token and by API key → always 404 and
   never modified. Plus role escalation, revoked and expired keys, a removed member's next request, a staging-prefixed key
   rejected in a production config.

8. **Architecture tests** (for example NetArchTest): Domain depends on nothing; Application doesn't reference
   Infrastructure; hosts don't touch Npgsql directly.

9. **CI:** add a vulnerability scan (`dotnet list package --vulnerable --include-transitive`, fail on any) and
   Trivy for the images; integration tests need Docker on the runner (`ubuntu-latest` has it).

10. **Docs to update as you go:** threat model (mark T1–T6 "verified by" with test names), data model (columns
    actually built), authorization model (any decisions on its open questions), `docs/roadmap/README.md` status,
    and a new `docs/roadmap/phase-1.md` checklist in the style of `phase-0.md`.

---

## 7. Environment notes and gotchas

- **WSL:** Ubuntu 26.04, systemd on, user `arihant` in the `docker` group. .NET SDK lives at `/usr/lib/dotnet` (apt).
- **NuGet audit + warnings-as-errors** fails restore on any known-vulnerable package, transitive ones included. That's intended. Bump the package; don't suppress it.
- **Package versions** live only in `Directory.Packages.props`. Look up current versions with `dotnet package search <id> --exact-match`.
- **`db/init/01-roles.sh` runs only when the Postgres volume is first created.** After changing it: `docker compose down -v` (wipes local data).
- **Metrics** export every 60 s, so Prometheus looks empty for the first minute after start-up.
- **Health endpoints are excluded from tracing.** Hit a `/v1/...` route to produce a trace.
- **Ports:** API 5080 (`dotnet run`) / 8080 (container), Postgres 5432, Azurite 10000, OTLP 4317/4318, Jaeger 16686,
  Prometheus 9090, Grafana 3000. Keycloak will need one (8081 suggested, since 8080 is taken).
- **Local credentials** are synthetic defaults in `.env.example` and `appsettings.Development.json`. Real provider keys go in
  `dotnet user-secrets` only.
- Line endings: LF everywhere (`.gitattributes`, `.editorconfig`). `dotnet format` enforces them.
- Commit trailer used so far: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
