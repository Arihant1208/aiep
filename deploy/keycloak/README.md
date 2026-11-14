# Keycloak realm (local development and tests only)

`aiep-realm.json` is imported by the `keycloak` Compose service on every start (`start-dev --import-realm`) and by
the API integration tests. Decision and rules: [ADR-0016](../../docs/adr/0016-keycloak-oidc-provider.md).

| Client | Type | Flows | Notes |
| --- | --- | --- | --- |
| `aiep-api` | Resource server | None | The audience every API token must carry |
| `aiep-web` | Public | Authorization code + PKCE (S256) | Redirect `http://localhost:5173/*` |
| `aiep-cli` | Public | Authorization code + PKCE (S256) | Loopback redirect `http://127.0.0.1:8400/*` |

Users `alice` / `alice-local-dev` and `bob` / `bob-local-dev` are synthetic. The realm defines **no roles that AIEP
reads**: authorization data lives in AIEP's database.

There is no password-grant client here. The API integration tests create one (`aiep-tests`) and a decoy user with a
realm `admin` role inside their own throwaway Keycloak container, through the admin API, so this file stays exactly
what ADR-0016 describes.

To change the realm, edit this file (or export from a running instance and review the diff). The export format can
change between Keycloak versions: bump the image tag in `docker-compose.yml` and the tests together with this file.
