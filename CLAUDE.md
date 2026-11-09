# AIEP: guidance for Claude Code

Multi-tenant AI task execution and evaluation platform (.NET 10, PostgreSQL 17, OpenTelemetry). The value of this
project is **provable** tenant isolation, authorization, durable execution and measurement. Read `docs/README.md` for
the map and `docs/adr/README.md` for decisions. If `HANDOFF.md` exists, read it first.

## Commands

```bash
docker compose up -d                              # infra only
docker compose --profile app up -d --build        # infra + api + worker containers
dotnet build Aiep.slnx
dotnet test Aiep.slnx
dotnet format Aiep.slnx                           # CI runs --verify-no-changes
dotnet run --project src/Aiep.Api                 # http://localhost:5080
```

## Non-negotiable rules

- **Tenant data access goes through the tenant-context unit of work only** (`set_config(..., true)` inside a
  transaction). Never session-level `SET`. Never connect services as `aiep_migrator`. Never add `BYPASSRLS`.
- Tenant scope comes from authentication and resource rows, **never** from request bodies or queue payloads.
- Unauthorized resource → **404**, not 403 (ADR-0015). 403 only when the caller can see the resource.
- Every tenant-owned table: `organization_id`, composite FKs, RLS `ENABLE` **and** `FORCE`, and a negative test.
- Run status changes only through `TaskRunTransitions`. Keep `docs/architecture/task-lifecycle.md` in sync.
- No prompts, documents, model outputs, secrets or auth headers in logs, traces or metrics. Metric labels stay low-cardinality (no tenant or run IDs).
- Model output is untrusted data.

## Workflow conventions

- **Significant decision → new ADR** (`docs/adr/template.md`), added to the index. Accepted ADRs are immutable; supersede, don't edit.
- Behaviour change → update the relevant doc in the same commit. No aspirational claims in docs.
- Security properties need tests that prove them; benchmarks need raw results.
- Warnings are errors, including NuGet vulnerability audit. Fix by upgrading, never by suppressing.
- Package versions only in `Directory.Packages.props`. Schema changes only as new `db/migrations/V####__*.sql` (never edit an applied one).
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Ask before pushing, creating GitHub repos or anything outward-facing.
