# ADR-0008: Identity: OIDC for people, scoped API keys for applications

- **Status:** Accepted
- **Date:** 2026-11-01
- **Deciders:** Arihant Jain
- **Spec references:** §8.1, FR-001, FR-007, FR-023, FR-024

## Context

Two kinds of caller: humans using the UI, and applications or services calling the API. Building our own password
authentication is a liability with no product value. Machine callers need credentials that are scoped, rotatable
and revocable.

## Decision

**Humans:** OpenID Connect through an external identity provider.

- Validate issuer, audience, signature, expiry and `nbf`; require `sub`.
- The internal `subject_id` is our own immutable ID, mapped from `(issuer, sub)`. Emails are not identifiers.
- **Authorization data lives in AIEP, not the IdP.** Identity-provider groups are not trusted for roles.
- Local development uses a dev-only token issuer, enabled only in `Development` and never compiled into production config.
- The IdP is chosen in Phase 1 (candidates: Microsoft Entra External ID, Auth0, Keycloak for local/self-hosted).

**Applications:** API keys.

- Format `aiep_<env>_<keyId>_<secret>`. `keyId` is a lookup handle; `secret` is ≥ 256 bits of randomness.
- Store `keyId` plus a **SHA-256 hash of the secret** (keyed with a server-side pepper from the secret manager). Keys are
  high-entropy random values, so a slow password hash isn't needed. Compare in constant time.
- The plaintext is shown **once**, at creation.
- Each key has an owner, organization, optional workspace, allowed actions, expiry, `last_used_at`, `revoked_at`.
- A key's scope can never exceed its creator's permissions at creation time, **and** it is re-checked against the owner's
  current permissions on each use. If the owner loses access, the key loses access (FR-007).
- Accepted only in the `Authorization: Bearer` header, never in URLs.
- Rotation: create a new key, overlap, revoke the old one. `last_used_at` shows when it's safe to revoke.
- The environment prefix stops a production key from being accepted by staging, and the reverse.

**Platform-to-cloud:** managed identities in Azure; no long-lived cloud credentials in configuration.

## Consequences

**Positive:** no password storage; keys are easy to identify in leak scanners thanks to the prefix; revocation is immediate.

**Negative:** per-request key lookup. Mitigate with a short-lived (≤ 30 s), tenant-scoped positive cache that
revocation invalidates.

## Alternatives considered

| Option | Why not (now) |
| --- | --- |
| Build username/password authentication | Security liability; no product value |
| OAuth client-credentials for machine callers | Better long-term for enterprise; heavier onboarding for an MVP |
| Roles from IdP groups | Couples authorization to an external system we don't control; hard to test |

## Revisit when

Enterprise customers ask for SAML/SCIM, or machine callers need federated workload identity.
