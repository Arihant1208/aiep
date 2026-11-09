# Roadmap

*Derived from Spec §22. These are planning estimates, not commitments. We build in vertical slices: one end-to-end
capability at a time, then harden it.*

| Phase | Weeks | Theme | Exit criterion | Status |
| --- | --- | --- | --- | --- |
| [0](phase-0.md) | 1 | Architecture and foundations | Reproducible local environment; agreed architecture | **In progress** |
| [1](phase-1.md) | 2–3 | Control plane and tenant security | Users access only resources authorized for their identity and scope | **In progress** |
| 2 | 4–5 | Task lifecycle and durable execution | Submit → queue → execute → persist works, including after a simulated worker crash | Not started |
| 3 | 6–7 | Execution isolation and observability | Every run can be traced, measured and cleaned up safely | Not started |
| 4 | 8–9 | Evaluation and cost governance | Compare two configurations on one dataset; explain quality/latency/cost trade-offs | Not started |
| 5 | 10–12 | Cloud deployment and portfolio release | Repeatable deployment; defensible technical demo with raw benchmark evidence | Not started |

## Priority rule

If time is short, protect this order: **tenant-isolation tests → durable end-to-end execution → observability →
evaluation → UI → cloud polish.** Kubernetes, multiple providers and the other Spec §24 items stay deferred.

## Phase 1 outline

Progress is tracked in the [Phase 1 checklist](phase-1.md).

1. Migration runner and baseline schema: subjects, organizations, memberships, workspaces, workspace_memberships, api_keys, audit_events.
2. Tenant-context unit of work (`SET LOCAL`) and RLS policies.
3. Authentication: OIDC validation against Keycloak ([ADR-0016](../adr/0016-keycloak-oidc-provider.md)); API key verification.
4. Authorization service plus the role→permission table from the [authorization model](../security/authorization-model.md).
5. Endpoints: organizations, members, workspaces, grants, API keys.
6. Test suites: RLS (no context / wrong context / pool leak), cross-tenant negative tests, escalation tests.
7. Architecture tests enforcing project dependency rules (ADR-0002).
