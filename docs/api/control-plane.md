# Control-plane API (as built)

*The `/v1` control plane (ADR-0015): organizations, members, workspaces, grants and API keys. Authorization follows the
[authorization model](../security/authorization-model.md); authentication follows [authentication](../security/authentication.md).
Every route except health requires an authenticated caller. IDs in paths and responses are the prefixed opaque form
(`org_…`, `ws_…`, `sub_…`, `key_…`).*

## Conventions

- **Concealment (ADR-0015).** A resource the caller cannot see is **404**, never 403: a non-member of an organization,
  an API key used outside its own organization, a workspace the caller has no permission in, and a malformed or unknown
  id all return 404. **403** is only for a caller who *can* see the resource but lacks the permission for the action.
- **Problem Details.** Every failure is `application/problem+json` with a stable `code` and a `correlationId`
  ([ProblemCodes](../../src/Aiep.Api/Http/ProblemCodes.cs)): `request.invalid` (400), `auth.forbidden` (403),
  `resource.not_found` (404), `resource.conflict` (409) plus specific codes such as `workspace.slug_taken`,
  `member.already_exists`, `organization.last_owner`.
- **Audit.** Every mutating action writes one `audit_events` row in the same transaction as the change (append-only;
  no payloads). Actions: `organization.created`, `workspace.created`, `workspace.archived`, `grant.set`,
  `grant.removed`, `member.added`, `member.role_changed`, `member.removed`, `api_key.created`, `api_key.revoked`.

## Endpoints

| Method & path | Permission | Notes |
| --- | --- | --- |
| `POST /v1/organizations` | authenticated user | Creates the org and the caller's Owner membership. API keys are refused (403). |
| `GET /v1/organizations/{org}` | `org:read` | |
| `GET /v1/me/permissions?organization={org}` | member | Effective permissions (ADR-0010). An API key reports its own org. |
| `GET /v1/organizations/{org}/members` | `members:read` | Includes display name and email (visible to co-members). |
| `POST /v1/organizations/{org}/members` | `members:manage` | Body `{subjectId, role}`. Subject must already exist (unknown → 404; already a member → 409). |
| `PUT /v1/organizations/{org}/members/{sub}` | `members:manage` | Change role. |
| `DELETE /v1/organizations/{org}/members/{sub}` | `members:manage` | Workspace grants cascade. |
| `POST /v1/organizations/{org}/workspaces` | `workspaces:create` | Slug unique per org (duplicate → 409). |
| `GET /v1/organizations/{org}/workspaces` | member | Lists only workspaces the caller can see. |
| `GET /v1/organizations/{org}/workspaces/{ws}` | workspace access | 404 if the caller has no permission in it. |
| `POST /v1/organizations/{org}/workspaces/{ws}/archive` | `workspaces:manage` | Idempotent. |
| `PUT /v1/organizations/{org}/workspaces/{ws}/grants/{sub}` | `grants:manage` | Body `{role}`. Target must be a member (else 404). |
| `DELETE /v1/organizations/{org}/workspaces/{ws}/grants/{sub}` | `grants:manage` | |
| `POST /v1/organizations/{org}/api-keys` | `keys:create` | Returns the secret **once**. Scope ⊆ creator's permissions (else 403). Optional `workspaceId` scopes the key to one workspace. |
| `GET /v1/organizations/{org}/api-keys` | `keys:read` | Metadata only; the secret is never returned again. |
| `POST /v1/organizations/{org}/api-keys/{key}/revoke` | `keys:revoke` | Soft (sets `revoked_at`); idempotent. |

## Invariants enforced here

- **Owner protection** (authorization-model footnote 2): only an Owner may add, change or remove an Owner (else 403).
- **At least one Owner**: demoting or removing the last active Owner → 409 `organization.last_owner`.
- **No one grants what they lack**: a workspace grant's role, and an API key's scope, must be within the actor's own
  effective permissions (else 403), via `AuthorizationInvariants`.

## Deferred (not yet enforced)

These authorization-model footnotes have no backing data or policy yet; they are follow-ups, not silent gaps:

- `keys:read` / `keys:revoke` scoped to a Developer's **own** keys, and `keys:read` / `audit:read` scoped to one
  workspace for WorkspaceAdmin: the key list and revoke are currently org-level (`keys:read` / `keys:revoke`).
- Developer key creation gated by a per-workspace "developer-created keys" policy (no such setting exists yet).
- `org:manage` / `retention:manage` "within Owner limits", and the run-scoped footnotes, which belong to later phases.
