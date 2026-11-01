# Vision and scope

*Derived from Spec §1–§4.*

## Product promise

A customer can submit an approved AI task, track it, retrieve the result, understand what it cost, and evaluate whether
a different model or prompt does better, **without being able to access any other customer's resources**. Each of
those guarantees is measurable and covered by tests.

## What AIEP is

1. **Multi-tenant control plane:** organizations, workspaces, members, roles, API keys, quotas, task definitions,
   provider configuration.
2. **Asynchronous execution platform:** durable queueing, scheduling, workers, retries, idempotency, leases, limits.
3. **AI inference layer:** provider adapter, versioned prompts and configuration, structured output validation.
4. **Evaluation and observability:** latency, throughput, errors, cost per successful task, quality, traces, benchmarks.

## Personas

| Persona | Primary need |
| --- | --- |
| Organization owner | Create the organization, set policy, quotas and retention |
| Organization admin | Configure workspaces and membership |
| Developer | Define tasks, submit runs, integrate through the API |
| Evaluator / ML engineer | Run datasets against configurations and compare |
| Auditor | Review configuration, access events and reports |
| Service account | Make application-to-platform calls without a human session |
| Platform operator | Keep the service healthy; tenant data access is restricted and audited |

## Resource hierarchy

```
Organization (tenant: security, membership, quotas, retention)
└── Workspace (application / team / environment, e.g. inventory-ai-prod)
    └── Task Definition → immutable Versions (schema, handler, model, prompt, limits, capabilities)
        └── Task Run (one logical execution) → Attempts
```

## MVP scope

**In scope**

- REST API (and a minimal read-only UI) to submit and track asynchronous tasks.
- Approved task types: structured extraction, classification, summarization, evaluation.
- Durable queueing, retries with backoff, timeouts, cancellation semantics, dead-lettering.
- Organizations and workspaces with independent permissions and quotas.
- Tenant-scoped storage of inputs, outputs, datasets and artifacts.
- One external model provider behind an adapter ([ADR-0009](../adr/0009-single-model-provider-behind-adapter.md)).
- Versioned evaluation suites and configuration comparisons.
- Operational, cost and quality metrics.
- Local Docker Compose environment, with a path to Azure.

**Explicit non-goals for the MVP** (each one would need its own ADR to add)

- Arbitrary customer code, binaries or container images ([ADR-0007](../adr/0007-trusted-allowlisted-handlers-only.md)).
- Privileged containers, host mounts, Docker socket access, unrestricted egress.
- Model output that grants permissions or triggers privileged actions.
- Claims that model output is correct or unbiased.
- "Exactly-once execution" claims (we define a precise, weaker contract in [task lifecycle](../architecture/task-lifecycle.md)).
- Compliance certification claims.
- Kubernetes, multi-region, multiple providers, self-hosted GPUs, workflow builders, SAML/SCIM (Spec §24).

## What "done" means for the MVP

See Spec §21.5. In short: a developer can authenticate, submit a task to an authorized workspace, and retrieve its
result. The platform proves through traces and tests that the task was authorized, measured and isolated.

## Open questions

- Who is the first real or demo customer, and which task type do we demo first? (Recommendation: structured invoice
  extraction. It has a clear schema, measurable field-level accuracy and easy synthetic data.)
