# ADR-0007: Execution trust model: trusted, allowlisted handlers only

- **Status:** Accepted
- **Date:** 2026-11-01
- **Deciders:** Arihant Jain
- **Spec references:** §3, §7, §7.1–7.4, FR-025–FR-029

## Context

Running *trusted* code on customer data and running *customer-supplied* code are different products with very
different threat models. Containers share the host kernel and are not a sufficient boundary for hostile code. Hardened
sandboxes (gVisor, Kata, Firecracker microVMs) need their own design, operations and security assessment.

## Decision

For the MVP, only **platform-authored handlers** compiled into the worker can execute. Customers configure *which*
handler runs (through a task definition: schema, prompt, model, limits) but cannot supply executable code.

- Handlers are registered in an **allowlist** keyed by `(handler_type, handler_version)`. A task-definition version
  pins one entry.
- Each handler declares its **capabilities** (for example `model:invoke`, `artifact:read`, `artifact:write`) and resource
  budget. The executor provides only what's declared.
- Each run executes with a **fresh execution context**: no shared mutable state between runs, scratch files in a
  per-run temp directory that is deleted afterwards.
- Model output is **untrusted data**. It is validated against the output schema and never interpreted as instructions to the platform.
- Worker containers are hardened (non-root, read-only root filesystem, dropped capabilities, no Docker socket, no host
  mounts, egress restricted to the model provider and platform dependencies, no access to the metadata endpoint).
- Execution model: **shared worker process** for allowlisted handlers. Per-run containers are not needed while all code is trusted.

Arbitrary customer code (FR-027) stays **disabled** until a separate ADR and threat model design a hardened sandbox.

## Consequences

**Positive:** a small, well-understood attack surface; fast execution without container start-up per run; the
security story can be defended in an interview.

**Negative:** customers can only use task types we build; adding a task type requires a platform release.

## Alternatives considered

| Option | Why not (now) |
| --- | --- |
| Container per run | Isolation benefit is small when all code is trusted; start-up latency and orchestration cost |
| Customer-supplied containers or scripts | Needs a hardened sandbox and a separate threat model |
| Dedicated worker pool per tenant | Cost; reserve for enterprise or "noisy tenant" cases |

## Revisit when

There is a concrete product need for custom code (for example user-defined scorers), or a handler needs dependencies
(native libraries, headless browsers) whose risk calls for process isolation.
