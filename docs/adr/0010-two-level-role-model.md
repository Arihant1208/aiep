# ADR-0010: Two-level role model: organization roles and workspace grants

- **Status:** Proposed
- **Date:** 2026-11-01
- **Deciders:** Arihant Jain (pending sign-off)
- **Spec references:** §8.2, §8.3, §8.5, §13.1, FR-005

## Context

The spec defines roles (Owner, Admin, Developer, Evaluator, Auditor) and also a `WorkspaceMemberships` table, but it
doesn't say how organization roles and workspace permissions combine. FR-005 requires that organization membership
does **not** automatically grant every workspace's permissions. A real customer wants, for example, "Developer in
`inventory-dev`, read-only in `inventory-prod`".

Every authorization check and every negative test depends on this, so it has to be settled before Phase 1.

## Decision

**Organization role** (exactly one per member):

| Org role | Meaning |
| --- | --- |
| `Owner` | Full control of the organization, including ownership transfer and security policy. Implicit `WorkspaceAdmin` on all workspaces. |
| `Admin` | Manage members, workspaces, quotas within owner limits. Implicit `WorkspaceAdmin` on all workspaces. |
| `Auditor` | Organization-wide read of audit events, configuration metadata and reports. **No** task inputs or outputs by default. No workspace grants needed. |
| `Member` | Belongs to the organization. **No workspace access** until granted. |

**Workspace role** (zero or one per member per workspace, through `workspace_memberships`):

| Workspace role | Meaning |
| --- | --- |
| `WorkspaceAdmin` | Manage workspace settings, grants, scoped API keys |
| `Developer` | Manage task definitions, submit/cancel runs, read results |
| `Evaluator` | Manage evaluation suites/runs, read evaluation reports and results they produce |
| `Viewer` | Read run metadata and results |

**Effective permission** = implicit permissions from the org role ∪ permissions of the explicit workspace role.

- Permissions are fine-grained strings (`runs:submit`, `runs:read_result`, `keys:create`, …), and roles map to permission
  sets **in code**, in one table, unit-tested against the matrix in the [authorization model](../security/authorization-model.md).
- **Service accounts and API keys** carry an explicit permission list (a subset of their creator's effective
  permissions) bound to one organization and optionally one workspace.
- **Platform operator** is not an organization role. It's a separate platform identity whose access to tenant data
  needs a time-bound, audited break-glass grant.
- Removing an org membership removes all workspace grants (cascade). Revocation takes effect on the next request (FR-007).

## Consequences

**Positive:** satisfies FR-005; matches how customers separate environments; a small, testable role set; permissions
as strings make API-key scoping natural.

**Negative:** two places to look when debugging "why can't I access X?". Mitigate with an "effective permissions"
endpoint and a clear 403 reason code.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Org-level roles only | Violates FR-005; cannot express per-environment access |
| Workspace-level roles only | Every admin must be granted on every workspace; no org-wide auditor |
| General ABAC/policy engine (OPA, Cedar) now | Premature (Spec §8.5); revisit when policies outgrow a role table |

## Revisit when

Customers need custom roles, conditional policies (time, IP, environment) beyond the handful in Spec §8.5, or
resource-level sharing below workspace granularity.

## Acceptance criteria

- [ ] Project owner agrees on org role and workspace role names and the implicit-admin rule.
- [ ] Permission matrix in the authorization model is reviewed and frozen for Phase 1.
