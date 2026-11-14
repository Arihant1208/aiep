# Task-execution API (Phase 2)

*Task definitions, their immutable versions, and runs. Workspace-scoped, under
`/v1/organizations/{organizationId}/workspaces/{workspaceId}`. Conventions (prefixed opaque IDs, 404 concealment, RFC 9457
Problem Details with a stable `code`) follow [ADR-0015](../adr/0015-api-conventions.md); authorization follows the
[authorization model](../security/authorization-model.md). A workspace the caller cannot see is 404.*

## Task definitions and versions

A definition is a named task in a workspace; a version is an immutable, publishable configuration of it
([ADR-0007](../adr/0007-trusted-allowlisted-handlers-only.md): the handler must be allowlisted).

| Method & path | Permission | Notes |
| --- | --- | --- |
| `POST /task-definitions` | `definitions:write` | `{slug, name, description?}`. 409 `definition.slug_taken` |
| `GET /task-definitions` | `definitions:read` | List in the workspace |
| `GET /task-definitions/{definitionId}` | `definitions:read` | |
| `POST /task-definitions/{definitionId}/versions` | `definitions:write` | Creates the next **draft** version. Rejects a non-allowlisted handler (400) |
| `POST /task-definitions/{definitionId}/versions/{version}/publish` | `definitions:write` | Idempotent; sets `current_version`. A published version is immutable |
| `GET /task-definitions/{definitionId}/versions` | `definitions:read` | |
| `GET /task-definitions/{definitionId}/versions/{version}` | `definitions:read` | |

A version body: `inputSchema`, `outputSchema` (JSON Schema), `handlerType`, `handlerVersion`, `modelProvider`,
`modelName`, `modelConfig?`, `promptTemplate?`, and the limits `maxAttempts` (1–10), `timeoutSeconds` (1–3600),
`leaseSeconds` (5–3600).

## Runs

| Method & path | Permission | Notes |
| --- | --- | --- |
| `POST /runs` | `runs:submit` | `{taskDefinitionId, input, version?}`. `version` defaults to the definition's current published version. `202 Accepted`; the run is durably `Queued` ([ADR-0005](../adr/0005-postgresql-backed-work-queue.md)) |
| `GET /runs` | `runs:read` | Most-recent first; no `output` |
| `GET /runs/{runId}` | `runs:read` | `output` included only with `runs:read_result` |
| `POST /runs/{runId}/cancel` | `runs:cancel` | `202 Accepted`. Pending runs cancel immediately; a running run is flagged and the worker stops at its next heartbeat ([task lifecycle](../architecture/task-lifecycle.md)) |

**Idempotency** ([ADR-0015](../adr/0015-api-conventions.md)): submit with an `Idempotency-Key` header. The same key and
payload from the same caller in the same workspace returns the same run (`200 OK`); the same key with a different payload
is `409 idempotency.conflict`. The run row and the idempotency mapping are inserted in one transaction, so concurrent
submissions of the same key still resolve to one run. Keys are retained 24 h.

**Submission guards:** submitting against a definition with no published version is `409 definition.no_published_version`;
against an unpublished version, `409 definition.version_not_published`.

Every authoring and run mutation writes an audit event in the same transaction, with no payloads
([ADR-0012](../adr/0012-opentelemetry-observability.md)).
