# Authentication (as built)

*Decisions: [ADR-0008](../adr/0008-identity-oidc-and-scoped-api-keys.md) (OIDC for people, API keys for
applications), [ADR-0016](../adr/0016-keycloak-oidc-provider.md) (Keycloak),
[ADR-0017](../adr/0017-pre-tenant-operations-security-definer.md) (pre-tenant lookups). This page describes the code;
tests named here prove each property.*

## One header, two credential types

Both credential types arrive as `Authorization: Bearer <value>`. The default authentication scheme (`Aiep`) is a policy
scheme that forwards by shape: a value starting with `aiep_` goes to the API key handler, anything else to JWT bearer
validation. Code: [`src/Aiep.Api/Authentication`](../../src/Aiep.Api/Authentication).

Every endpoint requires an authenticated caller (fallback authorization policy). Only `/health/live` and `/health/ready`
are anonymous. That includes unmatched routes: an anonymous caller gets 401 for `/v1/anything`, so routes can't be
probed; an authenticated caller gets 404. All errors are RFC 9457 Problem Details with `code` and `correlationId`.

## People: OIDC tokens

| Check | Setting |
| --- | --- |
| Signature | Keys from the discovery document's JWKS; only RS256 and ES256 accepted; unsigned tokens rejected |
| Issuer | `Authentication:Oidc:Issuer` exactly (local: `http://localhost:8081/realms/aiep`) |
| Audience | `Authentication:Oidc:Audience` (`aiep-api`) |
| Lifetime | `exp` required, `nbf` honoured, 30 s clock skew |
| HTTPS metadata | Required, except `Development` (startup fails otherwise) |

After validation, `(iss, sub)` is mapped to an AIEP subject through `aiep.resolve_subject` (created on first sign-in;
cached in memory for 5 minutes, keyed on the display data too, so name or email changes are written through). The
principal is then **replaced** by one carrying only two claims: `aiep:subject_id` and `aiep:auth_method`. Roles,
groups and every other token claim are discarded, and role checks (`IsInRole`) always return false.

In Compose, tokens are issued for `http://localhost:8081` (the browser's view) while the API container fetches discovery
and keys from `http://keycloak:8080` (`Authentication:Oidc:MetadataAddress`). Keycloak's `KC_HOSTNAME` plus
`KC_HOSTNAME_BACKCHANNEL_DYNAMIC` make both work.

### Flow

```mermaid
sequenceDiagram
    autonumber
    participant C as Client
    participant API as Aiep.Api
    participant KC as Keycloak (JWKS)
    participant DB as PostgreSQL
    C->>API: Authorization: Bearer &lt;JWT&gt;
    Note over API: Policy scheme: value is not aiep_* → JWT bearer
    API->>KC: Fetch discovery + JWKS (cached)
    KC-->>API: Signing keys
    Note over API: Validate signature (RS256/ES256),<br/>issuer, audience, exp/nbf (30s skew)
    alt token invalid
        API-->>C: 401 auth.unauthenticated
    else token valid
        API->>DB: aiep.resolve_subject(iss, sub, name, email)<br/>SECURITY DEFINER, no tenant context
        DB-->>API: subject_id (created on first sign-in)
        Note over API: Replace principal → {aiep:subject_id, aiep:auth_method};<br/>all token claims, roles and groups discarded
        API-->>C: Authorized request proceeds
    end
```

## Applications: API keys

Format `aiep_<environment>_<keyId>_<secret>`, all lowercase Crockford base32:

| Part | Size | Purpose |
| --- | --- | --- |
| `environment` | 2–10 letters | Deployment tag (`dev`, `test`, `prod`, …). A key for another environment is rejected before any lookup |
| `keyId` | 16 chars (80 random bits) | Public lookup handle, stored in clear, globally unique |
| `secret` | 52 chars (256 random bits) | Stored only as HMAC-SHA-256 with a server-side pepper (`Authentication:ApiKeys:Pepper`, ≥ 32 bytes) |

Verification (`ApiKeyAuthenticator`): parse → environment → pre-tenant lookup by `keyId` (`aiep.find_api_key`) → hash
the secret (also when the key doesn't exist, so timing doesn't reveal valid key ids) → constant-time compare → not
revoked → not expired → **the owner still has an active membership** in the key's organization. On success,
`last_used_at` is written at most once a minute. The principal carries the owner's subject id, the key id, its
organization, optional workspace and the key's permissions (a ceiling; authorization intersects it with the owner's
current permissions).

There is no positive cache for keys yet, so revocation and member removal take effect on the next request. ADR-0008
allows a cache of up to 30 s if lookups become a hot spot.

**Failures all look the same:** 401 with `code: auth.unauthenticated`. The reason (`Malformed`, `WrongEnvironment`,
`UnknownKey`, `InvalidSecret`, `Revoked`, `Expired`, `OwnerNoLongerMember`) goes to logs and the trace tag
`aiep.auth.failure`. The key itself is never logged.

### Flow

```mermaid
sequenceDiagram
    autonumber
    participant C as Client
    participant API as Aiep.Api
    participant DB as PostgreSQL
    C->>API: Authorization: Bearer aiep_&lt;env&gt;_&lt;keyId&gt;_&lt;secret&gt;
    Note over API: Policy scheme: value is aiep_* → API key handler
    Note over API: Parse format; reject a key for another environment<br/>before any lookup
    API->>DB: aiep.find_api_key(keyId)<br/>pre-tenant, SECURITY DEFINER
    DB-->>API: stored key row, or none
    Note over API: HMAC-SHA-256(secret, pepper) — hashed even when<br/>unknown so timing hides valid key ids; constant-time compare
    Note over API: not revoked? not expired?
    API->>DB: owner still has an active membership in the key's org?
    DB-->>API: yes / no (+ throttled last_used_at write)
    alt any check fails
        API-->>C: 401 auth.unauthenticated<br/>(reason only to logs + trace tag)
    else all checks pass
        API-->>C: Authorized; principal = owner subject + key scope ceiling
    end
```

**Keys in URLs are refused** with 400 `auth.credentials_in_url`, even if the header is also valid, because URLs end up
in proxy logs and browser history. Parameters named `access_token`, `id_token`, `api_key`, `apikey`, `key` or `token`,
and any value that looks like an AIEP key, trigger it. The response tells the caller to revoke the key.

## Resource IDs in responses

`GET /v1/me` already returns public IDs in the ADR-0015 format: `{prefix}_{26 uppercase Crockford base32 chars}` encoding
the UUIDv7 big-endian, so string order equals UUID byte order (creation time, to the millisecond). Prefixes: `org`,
`ws`, `sub`, `key`, `mem`. Code: [`ResourceId`](../../src/Aiep.Application/Ids/ResourceId.cs).

## Tests

| Property | Tests |
| --- | --- |
| Real Keycloak tokens accepted and mapped to one stable subject keyed by `(iss, sub)` | `OidcAuthenticationTests.A_valid_token_is_mapped_to_one_stable_subject`, `…Different_people_get_different_subjects` |
| IdP roles and groups reach nothing | `…Keycloak_roles_and_groups_in_the_token_reach_nothing_in_aiep` (decoy user with realm role `admin` and group `Owners`) |
| Forged, unsigned, tampered, wrong-audience and other-realm tokens rejected | `…A_token_signed_by_an_unknown_key_is_rejected`, `…An_unsigned_token_is_rejected`, `…A_tampered_token_is_rejected`, `…A_token_without_the_api_audience_is_rejected` (mutation-checked), `…A_token_from_another_realm_is_rejected` |
| Every key rejection is the same 401 | `ApiKeyAuthenticationTests.Rejected_keys_all_get_the_same_401` (8 cases) and `ApiKeyAuthenticatorTests` (unit) |
| Removed member's key stops working on the next request | `…A_removed_owner_loses_access_on_the_next_request` |
| Keys in URLs refused | `…Credentials_in_the_url_are_refused_even_with_a_valid_header` |
| Key format, hashing, ID encoding | `ApiKeyFormatTests`, `ApiKeyHasherTests`, `ResourceIdTests` |

The tests run the API host against a real PostgreSQL and a real Keycloak 26.8.2 importing
[`deploy/keycloak/aiep-realm.json`](../../deploy/keycloak/aiep-realm.json). The password-grant client and the decoy
user exist only inside the test's throwaway Keycloak (created through its admin API), never in the realm file.

## Open questions

- Pepper rotation: needs a `pepper_version` column and dual verification during rollover. Decide before production.
- Rate limiting of failed authentication (per IP and per key id). Phase 1 step 7 or Phase 3.
- Getting a token locally without a browser: the realm deliberately has no password-grant client. Until the UI or CLI
  exists, manual testing uses API keys (created through the step 6 endpoints).
