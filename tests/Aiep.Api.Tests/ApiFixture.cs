using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Keycloak;

namespace Aiep.Api.Tests;

/// <summary>
/// The API host against a real PostgreSQL (real init script and migrations) and a real Keycloak importing the
/// checked-in realm (deploy/keycloak/aiep-realm.json). Test-only Keycloak objects (a password-grant client, a decoy
/// admin user) are created inside this throwaway container through the admin API, never in the realm file.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string KeycloakImage = "quay.io/keycloak/keycloak:26.8.2";
    public const string ApiKeyEnvironment = "test";
    public const string TestClient = "aiep-tests";
    public const string NoAudienceClient = "aiep-tests-no-audience";
    public const string DecoyAdmin = "eve";
    public const string DecoyAdminPassword = "eve-test";
    private const string TestClientSecret = "aiep-tests-secret";
    private const string KeycloakAdmin = "admin";
    private const string KeycloakAdminPassword = "keycloak_test";

    public static readonly byte[] Pepper = "aiep-integration-test-pepper-0123456789"u8.ToArray();

    private readonly KeycloakContainer _keycloak = new KeycloakBuilder(KeycloakImage)
        .WithUsername(KeycloakAdmin)
        .WithPassword(KeycloakAdminPassword)
        .WithResourceMapping(
            File.ReadAllBytes(Path.Combine(PostgresFixture.RepositoryRoot, "deploy", "keycloak", "aiep-realm.json")),
            "/opt/keycloak/data/import/aiep-realm.json")
        .WithCommand("--import-realm")
        // Dev mode rebuilds Keycloak on every start (Quarkus augmentation): over a minute on a loaded laptop or CI
        // runner, before the realm import even begins. Wait on readiness with an explicit, generous timeout.
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
            request => request.ForPort(9000).ForPath("/health/ready"),
            options => options.WithTimeout(TimeSpan.FromMinutes(8))))
        .Build();

    private readonly HttpClient _keycloakHttp = new();

    public PostgresFixture Database { get; } = new();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string KeycloakBaseAddress => _keycloak.GetBaseAddress().TrimEnd('/');

    public string Issuer => $"{KeycloakBaseAddress}/realms/aiep";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(Database.InitializeAsync(), _keycloak.StartAsync());
        await ConfigureKeycloakForTestsAsync();

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Aiep"] = Database.ConnectionString("aiep_api", pooling: true),
                ["Authentication:Oidc:Issuer"] = Issuer,
                ["Authentication:Oidc:Audience"] = "aiep-api",
                ["Authentication:Oidc:MetadataAddress"] = null,
                ["Authentication:Oidc:RequireHttpsMetadata"] = "false",
                ["Authentication:ApiKeys:Environment"] = ApiKeyEnvironment,
                ["Authentication:ApiKeys:Pepper"] = Convert.ToBase64String(Pepper),
            }));
            builder.ConfigureServices(services => services.AddTransient<IStartupFilter, ClaimsProbeStartupFilter>());
        });
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        _keycloakHttp.Dispose();
        await _keycloak.DisposeAsync();
        await Database.DisposeAsync();
    }

    /// <summary>A real access token from Keycloak (password grant through the test-only client).</summary>
    public async Task<string> GetTokenAsync(string username, string password, string clientId = TestClient)
    {
        using var response = await _keycloakHttp.PostAsync(
            $"{Issuer}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = clientId,
                ["client_secret"] = TestClientSecret,
                ["username"] = username,
                ["password"] = password,
                ["scope"] = "openid profile email",
            }));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        return body!["access_token"]!.GetValue<string>();
    }

    /// <summary>A token from the master realm: valid Keycloak signature, wrong issuer for AIEP.</summary>
    public Task<string> GetMasterRealmTokenAsync() => AdminTokenAsync();

    private async Task<string> AdminTokenAsync()
    {
        using var response = await _keycloakHttp.PostAsync(
            $"{KeycloakBaseAddress}/realms/master/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = KeycloakAdmin,
                ["password"] = KeycloakAdminPassword,
            }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["access_token"]!.GetValue<string>();
    }

    private async Task ConfigureKeycloakForTestsAsync()
    {
        var token = await AdminTokenAsync();
        var admin = $"{KeycloakBaseAddress}/admin/realms/aiep";

        async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            var response = await _keycloakHttp.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Keycloak admin call {method} {url} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }

            return response;
        }

        object PasswordGrantClient(string clientId, bool withAudience) => new
        {
            clientId,
            enabled = true,
            protocol = "openid-connect",
            publicClient = false,
            secret = TestClientSecret,
            standardFlowEnabled = false,
            directAccessGrantsEnabled = true,
            serviceAccountsEnabled = false,
            protocolMappers = withAudience
                ? new object[]
                {
                    new
                    {
                        name = "aiep-api-audience",
                        protocol = "openid-connect",
                        protocolMapper = "oidc-audience-mapper",
                        config = new Dictionary<string, string>
                        {
                            ["included.client.audience"] = "aiep-api",
                            ["access.token.claim"] = "true",
                            ["id.token.claim"] = "false",
                        },
                    },
                }
                : [],
        };

        (await Send(HttpMethod.Post, $"{admin}/clients", PasswordGrantClient(TestClient, withAudience: true))).Dispose();
        (await Send(HttpMethod.Post, $"{admin}/clients", PasswordGrantClient(NoAudienceClient, withAudience: false))).Dispose();

        // Decoy: a user holding a realm role called "admin" and a group. AIEP must ignore both.
        (await Send(HttpMethod.Post, $"{admin}/roles", new { name = "admin" })).Dispose();
        (await Send(HttpMethod.Post, $"{admin}/groups", new { name = "Owners" })).Dispose();
        (await Send(HttpMethod.Post, $"{admin}/users", new
        {
            username = DecoyAdmin,
            email = "eve@decoy.test",
            firstName = "Eve",
            lastName = "Decoy",
            enabled = true,
            emailVerified = true,
            groups = new[] { "/Owners" },
            credentials = new[] { new { type = "password", value = DecoyAdminPassword, temporary = false } },
        })).Dispose();

        using var users = await Send(HttpMethod.Get, $"{admin}/users?username={DecoyAdmin}&exact=true");
        var userId = (await users.Content.ReadFromJsonAsync<JsonArray>())![0]!["id"]!.GetValue<string>();
        using var role = await Send(HttpMethod.Get, $"{admin}/roles/admin");
        var roleRepresentation = await role.Content.ReadFromJsonAsync<JsonObject>();
        (await Send(HttpMethod.Post, $"{admin}/users/{userId}/role-mappings/realm", new[] { roleRepresentation })).Dispose();
    }

    /// <summary>
    /// Test-only endpoint <c>/__test/claims</c>: authenticates with the API's default scheme and reports exactly what
    /// the principal carries. Lets tests prove that nothing from the IdP token reaches AIEP's principal.
    /// </summary>
    private sealed class ClaimsProbeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path != "/__test/claims")
                {
                    await nextMiddleware(context);
                    return;
                }

                var result = await context.AuthenticateAsync();
                if (!result.Succeeded)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                await context.Response.WriteAsJsonAsync(new ClaimsProbe(
                    [.. result.Principal.Claims.Select(c => c.Type)],
                    result.Principal.IsInRole("admin"),
                    result.Principal.IsInRole("Owners")));
            });
            next(app);
        };
    }
}

public sealed record ClaimsProbe(IReadOnlyList<string> ClaimTypes, bool IsInAdminRole, bool IsInOwnersRole);

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}

internal static class JwtParts
{
    public static JsonObject Payload(string jwt)
    {
        var part = jwt.Split('.')[1];
        var padded = part.Replace('-', '+').Replace('_', '/') + new string('=', (4 - (part.Length % 4)) % 4);
        return JsonNode.Parse(Convert.FromBase64String(padded))!.AsObject();
    }

    public static string Base64Url(string json) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Base64Url(JsonObject json) => Base64Url(json.ToJsonString(new JsonSerializerOptions()));
}
