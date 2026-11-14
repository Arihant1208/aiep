# AIEP documentation

This folder is the project's memory. Code shows *what* the system does. These docs record *why* it does it that way,
what we decided against, and what we still have to decide.

## Map

| Area | What lives there | Start with |
| --- | --- | --- |
| [Current state](status.md) | What is built and verified **right now**, updated each slice | [Status](status.md) |
| [Decisions (ADRs)](adr/README.md) | One page per significant decision: context, choice, trade-offs, alternatives | [ADR index](adr/README.md) |
| [Product](product/vision-and-scope.md) | Product promise, personas, MVP scope and non-goals | [Vision and scope](product/vision-and-scope.md) |
| [Architecture](architecture/overview.md) | Components, deployable units, module boundaries, flows | [Overview](architecture/overview.md), [Task lifecycle](architecture/task-lifecycle.md) |
| [Security](security/threat-model.md) | Threat model, authentication, authorization model | [Threat model](security/threat-model.md), [Authentication](security/authentication.md), [Authorization](security/authorization-model.md) |
| [Data](data/data-model.md) | Entities, ownership invariants, tenancy enforcement | [Data model](data/data-model.md) |
| [Development](development/local-setup.md) | Running the stack locally, conventions | [Local setup](development/local-setup.md) |
| [Roadmap](roadmap/README.md) | Phases, exit criteria, progress | [Phase 0](roadmap/phase-0.md) |
| [Glossary](glossary.md) | Shared vocabulary | — |

## How we keep these docs useful

1. **Decisions go in ADRs, descriptions go in docs.** If you are choosing between options, write an ADR. If you are
   describing how something works, update the relevant doc and link the ADR that justified it.
2. **ADRs are immutable once Accepted.** To change a decision, write a new ADR that supersedes the old one and update the
   old one's status line only. The history of *why we changed our mind* is the valuable part.
3. **Docs change in the same pull request as the code.** A PR that changes behaviour described here updates the doc too.
   Reviewers should ask, "Which doc does this invalidate?"
4. **Open questions are written down, not remembered.** Each doc ends with an *Open questions* section; each proposed
   ADR lists what would make us accept it.
5. **No aspirational claims.** Docs describe what is built or explicitly mark what is planned. Benchmarks link to raw
   results; security properties link to the tests that prove them.
6. **Link, don't duplicate.** One canonical place per fact; everywhere else links to it.

## Spec traceability

The original product/technical specification is the source for most of these docs. Where a doc derives from it, the
relevant spec section is cited as `Spec §N`. Functional requirements keep their spec IDs (`FR-001` … `FR-036`).
