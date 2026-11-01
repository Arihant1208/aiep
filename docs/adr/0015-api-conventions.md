# ADR-0015: API conventions: versioning, errors, IDs, concealment

- **Status:** Accepted
- **Date:** 2026-11-01
- **Deciders:** Arihant Jain
- **Spec references:** §12, §12.1–12.3

## Context

Consistent API conventions decided up front avoid breaking clients later and close common leak paths: enumerable IDs,
verbose errors, and "403 vs 404" responses that reveal whether a resource exists.

## Decision

- **Versioning:** URL path prefix `/v1`. Additive changes within a version; breaking changes mean `/v2`.
- **Resource IDs:** opaque, type-prefixed strings, for example `run_01JC…`, `ws_…`, `td_…`, `org_…`. Internally UUIDv7
  (time-ordered, index-friendly) rendered as `prefix_` plus a base32 encoding. Never sequential integers.
- **Tenant scope comes from authentication and resource ownership**, never from body fields. Path IDs are
  looked up and then authorized.
- **Concealment:** if the caller cannot access a resource, return **404**, never 403, so existence isn't revealed. Return
  **403** only when the caller can see the resource but lacks permission for the action (for example a Viewer trying to cancel).
- **Errors:** RFC 9457 Problem Details (`application/problem+json`) with stable machine-readable `code` values
  (`run.not_found`, `idempotency.conflict`, `quota.exceeded`, …) and `correlationId`. No stack traces, internal hosts,
  provider messages or other tenants' metadata.
- **Idempotency:** `Idempotency-Key` header required on run submission (Spec §9.2), scoped to
  `(organization, workspace, caller)` and stored with a hash of the request.
- **Asynchronous work:** `202 Accepted` with `runId`, `status` and a status URL. Completion by polling first, webhooks later.
- **Pagination:** cursor-based (`?cursor=…&limit=…`), maximum page size enforced.
- **Rate limits:** `429` with `Retry-After`.
- **Contract:** OpenAPI document generated from the code; contract tests in CI catch breaking changes.

## Consequences

**Positive:** predictable for clients; closes ID enumeration and existence leaks.

**Negative:** 404-for-unauthorized makes debugging harder for legitimate callers. Mitigate with correlation IDs and an
authenticated "why" view in operator tooling.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Header/media-type versioning | Less visible; harder to route and cache |
| Raw UUIDs | Workable, but prefixes make logs and support far easier and catch ID mix-ups |
| GraphQL | Authorization per field is harder; REST fits the resource model |
