# ADR-0001: Record architecture decisions as ADRs

- **Status:** Accepted
- **Date:** 2026-11-01
- **Deciders:** Arihant Jain
- **Spec references:** §23

## Context

AIEP is meant to show sound engineering judgment, not just working code. Many of its most important properties,
such as tenant isolation, delivery guarantees and the execution trust model, come from decisions that are invisible in
the code itself. Without a written record, the reasoning is lost, decisions get re-argued, and reviewers or
interviewers cannot tell a deliberate trade-off from an accident.

## Decision

- Record every significant decision as an ADR in `docs/adr/`, numbered sequentially, using [template.md](template.md).
- ADRs move through **Proposed → Accepted**, and later possibly **Superseded**. Once Accepted, only the status line may change.
- A change of mind is a new ADR that supersedes the old one.
- Proposed ADRs must state their acceptance criteria and which phase they block.
- Each ADR lists the alternatives considered and the concrete triggers for revisiting it.

## Consequences

**Positive:** the reasoning survives; onboarding and reviews are faster; the docs double as portfolio material.

**Negative:** writing overhead, and the risk that ADRs drift away from the code.

**Mitigation:** keep ADRs short; PR reviewers check that changed behaviour is still consistent with the referenced ADRs.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Wiki or Notion pages | Live outside the repo, aren't versioned with the code, and rot silently |
| Design notes in PR descriptions | Hard to find later; no single index |
| Nothing | Unacceptable for a project whose value lies in its design decisions |

## Revisit when

The ADR count makes the index hard to scan (> ~60), at which point group the index by area.
