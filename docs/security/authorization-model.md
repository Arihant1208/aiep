# Authorization model

*Derived from Spec §8. Role structure is Accepted in [ADR-0010](../adr/0010-two-level-role-model.md). This matrix is the
**source** for the role-to-permission table in code (`src/Aiep.Application/Authorization`): `RolePermissionsMatrixTests`
parses this page and fails if the two drift, so the doc and the enforced rules can never silently disagree.*

## Algorithm (every protected request)

1. **Authenticate**: OIDC token or API key → `subject_id` (+ key scope if API key). Reject → `401`.
   Roles and groups in the token are ignored; authorization data lives only in AIEP
   ([ADR-0008](../adr/0008-identity-oidc-and-scoped-api-keys.md), [ADR-0016](../adr/0016-keycloak-oidc-provider.md)).
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

## How the table is enforced

The code table answers algorithm **step 5** only: *does this role hold this permission at all?* A footnoted cell
(`limited¹`, `✓²`, `own`, `ws-scoped`, `✓³⁻⁵`) still grants the permission; its condition is a **step-6 policy** applied
at the endpoint, not an absence of the permission:

| Footnote | Permission is granted, but the endpoint additionally checks… |
| --- | --- |
| `limited¹` (Admin `org:manage`, `retention:manage`) | the change stays within the Owner's configured limits |
| `✓²` (Admin `members:manage`) | the target is not an Owner (can't grant/revoke/remove Owner) |
| `own` (Developer `keys:read`, `keys:revoke`) | the key's owner is the caller |
| `✓⁵` (Developer `keys:create`) | workspace policy allows developer-created keys; scope ⊆ own permissions |
| `ws-scoped` (WA `audit:read`) | events are limited to the caller's workspace |
| `✓³` (Evaluator `runs:submit`, `runs:read_result`) | evaluation runs and their results only |
| `✓⁴` (Developer/Evaluator `runs:cancel`) | runs the caller submitted |

## Invariants (each one has a test)

- Organization membership alone (role `Member`) grants **no** workspace access (FR-005) —
  `AuthorizationTests.A_plain_member_has_no_access_even_in_a_workspace`, `…A_workspace_grant_applies_only_to_its_workspace`.
- No one can grant a permission they don't hold — `…No_one_can_delegate_a_permission_they_lack`,
  `…Key_scope_must_stay_within_the_owners_permissions`.
- An organization must always have at least one active Owner — `…The_last_owner_cannot_be_removed`.
- API key effective permissions = key scope ∩ owner's *current* effective permissions —
  `…An_api_key_cannot_exceed_its_scope`, `…A_key_loses_power_when_its_owner_loses_access`.
- Platform operators have no tenant permissions without an active, audited break-glass grant *(Phase 5; no operator identity exists yet)*.
- No role can read secret values after creation *(no endpoint returns a secret; enforced when key/secret endpoints land in step 6)*.

## Open questions

- Should Auditor see run **results** if the Owner explicitly enables it per workspace? (Spec says "metadata only by default".)
- Should Evaluators be allowed to submit ordinary runs? (Matrix says evaluation runs only.)
