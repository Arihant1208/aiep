# ADR-0016: Use Keycloak as the OIDC provider

- **Status:** Accepted
- **Date:** 2026-11-09 (choice made by the owner on 2026-11-01; written up at the start of Phase 1)
- **Deciders:** Arihant Jain
- **Spec references:** §8.1, FR-001
- **Supersedes / Superseded by:** Partly supersedes [ADR-0008](0008-identity-oidc-and-scoped-api-keys.md): it settles
  ADR-0008's open IdP choice and replaces its "dev-only token issuer" rule. The rest of ADR-0008 stands.

## Context

[ADR-0008](0008-identity-oidc-and-scoped-api-keys.md) decided that people authenticate with OpenID Connect through an
external identity provider, and that authorization data stays in AIEP. It left the provider open (Microsoft Entra
External ID, Auth0 or Keycloak) and planned a dev-only token issuer for local development.

Phase 1 needs a real issuer now. Its headline deliverable is a security test suite proving that callers only reach
resources they are authorized for. Those tests are more convincing when the tokens come from a real OIDC server
(discovery, JWKS, key rotation, standard claims) than from a fake issuer the API was written to trust.

Forces:

- **Runs locally and in CI.** Anyone cloning the repo must get working sign-in with `docker compose up`, no cloud
  account and no secrets.
- **Standard OIDC.** The API must not depend on provider-specific behaviour, so a later move to a managed IdP is a
  configuration change.
- **Cost and openness.** This is a portfolio project. No per-user pricing, no vendor account needed to run it.
- **Smaller trusted surface.** A dev-only issuer is code that must never reach production. Not having one removes that risk.

## Decision

Use **Keycloak** as the OIDC provider for local development, CI and integration tests.

**Running it**

- Image `quay.io/keycloak/keycloak`, pinned to an exact version (**26.8.2** at the time of writing). Never `latest`.
  Bump it deliberately, together with the realm file, because the export format can change between versions.
- Local Compose only: `start-dev --import-realm`, host port **8081** (8080 is the API container). Dev mode (HTTP, in-memory
  H2 store) is acceptable because the realm is re-imported on every start and holds only synthetic data.
- Integration tests start Keycloak with Testcontainers or reuse the Compose instance, and get real tokens from it.

**Realm as code**

- One realm, `aiep`, exported to `deploy/keycloak/aiep-realm.json` and checked in. Changes to the realm are made by
  editing that file and are reviewed like code.
- Clients:
  - `aiep-api`: the resource server. Tokens for the API must carry `aud: aiep-api` (added by an audience mapper on the
    clients below). It has no login flow of its own.
  - `aiep-web` and `aiep-cli`: public clients, authorization code flow with **PKCE (S256)** only. No implicit flow, no
    resource-owner password grant, no client secrets.
- **Synthetic test users only** (for example `alice@org-a.test`, `bob@org-b.test`), with throwaway passwords in the
  realm file. No real people's data.

**Token validation in the API** (standard ASP.NET Core JWT bearer handler)

- Validate issuer, audience (`aiep-api`), signature through the discovered JWKS, `exp` and `nbf` with small clock skew;
  require `sub`. Only asymmetric algorithms (RS256/ES256) are accepted.
- Map `(iss, sub)` to the AIEP `subjects` row, creating it on first sign-in (ADR-0008). Email and username are display
  data, never identifiers.
- **Keycloak realm roles, client roles and groups are ignored.** Authorization comes only from AIEP's own memberships and
  grants ([ADR-0010](0010-two-level-role-model.md)). The realm defines no roles that AIEP reads.
- Authority, audience and the "require HTTPS metadata" flag come from configuration. HTTPS metadata may be off only in
  the `Development` environment.

**No dev-only token issuer.** This replaces the ADR-0008 bullet that planned one. Local development uses Keycloak like
every other environment.

**Production hosting is not decided here.** Whether production runs Keycloak (and where) or moves to a managed IdP such
as Entra External ID is a Phase 5 decision with its own ADR. Because the API only uses standard OIDC, that move should
need configuration changes and a subject migration plan for `(iss, sub)`, not code changes.

## Consequences

**Positive**

- Realistic OIDC everywhere: discovery, JWKS, signature checks and expiry are exercised in tests, not mocked.
- No dev-only authentication code that could leak into production.
- Open source, no account or cost; the realm file makes the identity setup reproducible and reviewable.
- Swapping providers later is a configuration change, because nothing reads Keycloak-specific claims.

**Negative / costs**

- One more container locally (roughly 500 MB to 1 GB of memory, 10 to 20 s start-up). CI integration tests pay that
  start-up time too.
- Keycloak's realm export format changes between major versions. The pinned image version and the realm file must be
  upgraded together.
- Dev mode is not production configuration. It must not be mistaken for a hosting recipe.

**Risks and mitigations**

- *Someone reads roles from the token later "because they're there".* The realm defines none; the authorization model
  doc states that IdP roles are ignored; Phase 1 security tests use a token that carries a Keycloak admin role and
  assert it grants nothing in AIEP.
- *Signing key rotation breaks validation.* The JWT handler refreshes JWKS from the discovery document; a test covers a
  token signed by an unknown key being rejected.
- *Synthetic passwords copied into a real deployment.* They live only in the dev realm file, are obviously fake, and
  production hosting is a separate decision.

## Alternatives considered

| Option | Why not (now) |
| --- | --- |
| Dev-only token issuer (ADR-0008's plan) plus a managed IdP later | Tests would validate tokens from code we wrote to be trusted; it is code that must never ship. Keycloak removes both problems. |
| Microsoft Entra External ID | Good fit for the Azure target, but needs a tenant and network access to run tests; can't run in Compose or offline CI. Still a candidate for production. |
| Auth0 | Same as Entra: hosted only, needs an account, free-tier limits. |
| Duende IdentityServer / OpenIddict inside the solution | Turns us into an identity provider author, which ADR-0008 avoided on purpose. Duende also needs a commercial licence beyond small use. |
| Mock OIDC server (for example `mock-oauth2-server`) | Lighter, but a test fixture rather than an IdP; can't serve as the sign-in system for a real UI. |

## Revisit when

- Production hosting is decided (Phase 5): run Keycloak in production, or move to a managed IdP.
- Enterprise customers need SAML or SCIM federation (Keycloak can broker it; a managed IdP may do it better).
- Keycloak start-up time makes CI integration tests materially slow.
