# Glossary

| Term | Meaning |
| --- | --- |
| **Organization / tenant** | The root customer boundary for data, membership, quotas and retention. |
| **Workspace** | A grouping inside an organization (application, team or environment). A *logical* security boundary enforced by authorization, not a process boundary. |
| **Subject** | Any authenticated identity (human user or service account), with an immutable internal ID. |
| **Task definition** | A named, approved type of work in a workspace. |
| **Task definition version** | The immutable execution contract: schemas, handler, model, prompt, limits, capabilities. |
| **Handler** | Platform-authored code that executes a task type. Allowlisted by `(type, version)`. |
| **Task run** | One logical execution of a definition version. Has a state and one or more attempts. |
| **Attempt** | One try at executing a run. Retries add attempts; history is kept. |
| **Replay** | A user-initiated new run linked to a previous one. Not a retry. |
| **Lease** | A time-bound claim a worker holds on a running run, renewed by heartbeat. |
| **Dead-lettered** | Terminal state for runs that exhausted retryable attempts. |
| **Idempotency key** | Client-supplied key making submission safe to retry; scoped to org + workspace + caller. |
| **Tenant context** | The transaction-local `app.organization_id` (etc.) setting that RLS policies read. |
| **RLS** | PostgreSQL row-level security: per-row access policies enforced by the database. |
| **Effective permissions** | Org-role implicit permissions ∪ workspace-role permissions (∩ key scope for API keys). |
| **Operational success** | The run reached `Succeeded` (handler finished, output schema-valid). |
| **Semantic quality** | Whether the output is *correct*, measured by evaluation scorers, separately from operational success. |
| **Evaluation suite** | A versioned dataset plus scorers plus configurations to compare. |
| **Cost per successful task** | Total attributable cost ÷ successful logical runs, with the cost components stated. |
| **Break-glass** | Time-bound, audited elevated access for operators. |
