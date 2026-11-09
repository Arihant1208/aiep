# Architecture Decision Records

An ADR captures one significant decision: the forces at play, what we chose, what it costs us, and what we rejected.
We use a lightweight variant of Michael Nygard's format ([ADR-0001](0001-record-architecture-decisions.md)).

## Status legend

| Status | Meaning |
| --- | --- |
| **Proposed** | Drafted and recommended, awaiting the project owner's sign-off. Do not build on it irreversibly yet. |
| **Accepted** | Agreed. Implementation should follow it. Content is frozen. |
| **Superseded by ADR-XXXX** | Replaced by a later decision. Kept for history. |
| **Deprecated** | No longer relevant, not replaced. |
| **Rejected** | Considered and declined. Kept so we don't re-litigate it. |

## Index

| # | Decision | Status | Blocks |
| --- | --- | --- | --- |
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions as ADRs | Accepted | — |
| [0002](0002-modular-monolith-with-separate-worker.md) | Modular monolith with a separately deployed worker | Accepted | — |
| [0003](0003-dotnet-10-lts.md) | Target .NET 10 LTS instead of .NET 8 | Accepted | — |
| [0004](0004-postgresql-system-of-record.md) | PostgreSQL is the system of record for all state | Accepted | — |
| [0005](0005-postgresql-backed-work-queue.md) | PostgreSQL-backed work queue for the MVP | Accepted | — |
| [0006](0006-tenant-isolation-shared-database-rls.md) | Tenant isolation: shared database, RLS, and application authorization | Accepted | — |
| [0007](0007-trusted-allowlisted-handlers-only.md) | Execution trust model: trusted, allowlisted handlers only | Accepted | — |
| [0008](0008-identity-oidc-and-scoped-api-keys.md) | Identity: OIDC for people, scoped API keys for applications | Accepted (IdP choice and local issuer partly superseded by 0016) | — |
| [0009](0009-single-model-provider-behind-adapter.md) | One model provider behind an adapter, plus a deterministic fake | Accepted | — |
| [0010](0010-two-level-role-model.md) | Two-level role model: organization roles and workspace grants | Accepted | — |
| [0011](0011-docker-compose-local-managed-containers-cloud.md) | Docker Compose locally, managed containers on Azure | Accepted | — |
| [0012](0012-opentelemetry-observability.md) | OpenTelemetry for traces, metrics, and logs | Accepted | — |
| [0013](0013-data-access-and-migrations.md) | Data access with EF Core; schema via versioned SQL migrations | Accepted | — |
| [0014](0014-evaluation-scoring-in-dotnet-first.md) | Evaluation scoring in .NET first; Python only when needed | Accepted | — |
| [0015](0015-api-conventions.md) | API conventions: versioning, errors, IDs, concealment | Accepted | — |
| [0016](0016-keycloak-oidc-provider.md) | Use Keycloak as the OIDC provider | Accepted | — |
| [0017](0017-pre-tenant-operations-security-definer.md) | Pre-tenant operations through narrow SECURITY DEFINER functions | **Proposed** | — |

## Writing a new ADR

1. Copy [template.md](template.md) to `NNNN-kebab-case-title.md` using the next free number.
2. Set status **Proposed**, fill in every section, and keep it to what a reviewer needs to agree or disagree.
3. Add it to the index above in the same PR.
4. When it's agreed, change status to **Accepted** with the date. From then on, only the status line may change.

Write an ADR when a choice is **hard to reverse**, **affects more than one module**, **constrains security or data**, or
**someone will ask "why did we do it this way?"** in six months. Don't write one for routine, easily reversible
choices.
