# Authorization model

*Derived from Spec §8. Role structure is **Proposed** in [ADR-0010](../adr/0010-two-level-role-model.md). This matrix
becomes the source for the role-to-permission table in code and its unit tests. Freeze it before Phase 1.*

## Algorithm (every protected request)

1. **Authenticate**: OIDC token or API key → `subject_id` (+ key scope if API key). Reject → `401`.
2. **Resolve scope from resources, not input**: load the target resource (or its parent workspace) with the
   caller's organization context. Not found or not visible → `404`.
3. **Membership**: the caller is an active member of the resource's organization, or the key belongs to it.
4. **Effective permissions** = org-role implicit ∪ workspace-role permissions (∩ key scope, for API keys).
5. **Check** that the required permission for the action is in the effective set. Missing but resource visible → `403`.
6. **Policies**: workspace status (archived → no submissions), environment restrictions on keys, quotas, capabilities.
7. **Execute** inside the tenant-context transaction (RLS as backstop).
8. **Audit** sensitive and administrative actions.

Workers follow the same steps using their service identity and the **claimed run row** as the trusted scope.

## Permissions

| Permission | Description |
| --- | --- |
| `org:read` / `org:manage` / `org:transfer` | Org metadata / settings and policy / ownership transfer |
| `members:read` / `members:manage` | List / invite, change role, remove |
| `workspaces:create` / `workspaces:manage` | Create / settings, archive |
| `grants:manage` | Manage workspace role grants |
| `definitions:read` / `definitions:write` | View / create and publish versions |
| `runs:submit` / `runs:read` / `runs:read_result` / `runs:cancel` / `runs:replay` | Run lifecycle |
| `evals:read` / `evals:write` | Evaluation suites, runs, reports |
| `keys:read` / `keys:create` / `keys:revoke` | API keys (scoped ⊆ own permissions) |
| `retention:read` / `retention:manage` | Retention policy |
| `audit:read` | Audit events |
| `usage:read` | Usage and quota consumption |

## Role → permission matrix

Org roles: **O** Owner, **A** Admin, **Au** Auditor, **M** Member (no implicit workspace access).
Workspace roles: **WA** WorkspaceAdmin, **D** Developer, **E** Evaluator, **V** Viewer.

| Permission | O | A | Au | WA | D | E | V |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `org:read` | ✓ | ✓ | ✓ | | | | |
| `org:manage` | ✓ | limited¹ | | | | | |
| `org:transfer` | ✓ | | | | | | |
| `members:read` | ✓ | ✓ | ✓ | | | | |
| `members:manage` | ✓ | ✓² | | | | | |
| `workspaces:create` | ✓ | ✓ | | | | | |
| `workspaces:manage` | ✓ | ✓ | | ✓ | | | |
| `grants:manage` | ✓ | ✓ | | ✓ | | | |
| `definitions:read` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `definitions:write` | ✓ | ✓ | | ✓ | ✓ | | |
| `runs:submit` | ✓ | ✓ | | ✓ | ✓ | ✓³ | |
| `runs:read` (metadata) | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `runs:read_result` | ✓ | ✓ | | ✓ | ✓ | ✓³ | ✓ |
| `runs:cancel` | ✓ | ✓ | | ✓ | ✓⁴ | ✓⁴ | |
| `runs:replay` | ✓ | ✓ | | ✓ | ✓ | | |
| `evals:read` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `evals:write` | ✓ | ✓ | | ✓ | | ✓ | |
| `keys:read` | ✓ | ✓ | ✓ | ✓ | own | | |
| `keys:create` | ✓ | ✓ | | ✓ | ✓⁵ | | |
| `keys:revoke` | ✓ | ✓ | | ✓ | own | | |
| `retention:read` | ✓ | ✓ | ✓ | | | | |
| `retention:manage` | ✓ | limited¹ | | | | | |
| `audit:read` | ✓ | ✓ | ✓ | ws-scoped | | | |
| `usage:read` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | |
| Read secret values | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ |

1. Within limits the Owner sets; cannot change security policy or loosen retention below the Owner's minimum.
2. Cannot grant or revoke Owner, or remove an Owner.
3. Evaluation runs and the results they produce only, unless also Developer.
4. Runs they submitted.
5. Only if workspace policy allows developer-created keys; scope ⊆ own permissions.

Org Owner/Admin hold **WA** implicitly on every workspace in the organization.

## Invariants (each one gets a test)

- Organization membership alone (role `Member`) grants **no** workspace access (FR-005).
- No one can grant a permission they don't hold.
- An organization must always have at least one active Owner.
- API key effective permissions = key scope ∩ owner's *current* effective permissions.
- Platform operators have no tenant permissions without an active, audited break-glass grant.
- No role can read secret values after creation.

## Open questions

- Should Auditor see run **results** if the Owner explicitly enables it per workspace? (Spec says "metadata only by default".)
- Should Evaluators be allowed to submit ordinary runs? (Matrix says evaluation runs only.)
