using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aiep.Application.Ids;

namespace Aiep.Application.Identity;

/// <summary>The three parts of a presented key: <c>aiep_{environment}_{keyId}_{secret}</c> (ADR-0008).</summary>
public sealed record ApiKeyParts(string Environment, string KeyId, string Secret);

/// <summary>A freshly generated key. <see cref="Plaintext"/> is shown to the caller once and never stored.</summary>
public sealed record GeneratedApiKey(string Plaintext, string KeyId, string Secret);

public static partial class ApiKeyFormat
{
    public const string Prefix = "aiep_";

    // keyId: 10 random bytes (80 bits) → 16 chars. secret: 32 random bytes (256 bits) → 52 chars.
    private const int KeyIdBytes = 10;
    private const int SecretBytes = 32;
    private const int MaxLength = 100;

    // \z, not $: in .NET, $ also matches before a trailing newline.
    [GeneratedRegex("^aiep_(?<env>[a-z]{2,10})_(?<id>[0-9a-hjkmnp-tv-z]{16})_(?<secret>[0-9a-hjkmnp-tv-z]{52})\\z")]
    private static partial Regex KeyPattern();

    // Anything that looks like the start of a key, anywhere in a string (for spotting keys in URLs).
    [GeneratedRegex("aiep_[a-z]{2,10}_[0-9a-hjkmnp-tv-z]{16}_", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedKeyPattern();

    [GeneratedRegex("^[a-z]{2,10}\\z")]
    private static partial Regex EnvironmentPattern();

    public static bool IsValidEnvironment(string? environment) =>
        environment is not null && EnvironmentPattern().IsMatch(environment);

    public static GeneratedApiKey Generate(string environment)
    {
        if (!IsValidEnvironment(environment))
        {
            throw new ArgumentException("Environment must be 2-10 lowercase letters.", nameof(environment));
        }

        var keyId = CrockfordBase32.EncodeLower(RandomNumberGenerator.GetBytes(KeyIdBytes));
        var secret = CrockfordBase32.EncodeLower(RandomNumberGenerator.GetBytes(SecretBytes));
        return new GeneratedApiKey($"{Prefix}{environment}_{keyId}_{secret}", keyId, secret);
    }

    public static bool TryParse(string? value, out ApiKeyParts parts)
    {
        parts = null!;
        if (value is null || value.Length > MaxLength)
        {
            return false;
        }

        var match = KeyPattern().Match(value);
        if (!match.Success)
        {
            return false;
        }

        parts = new ApiKeyParts(match.Groups["env"].Value, match.Groups["id"].Value, match.Groups["secret"].Value);
        return true;
    }

    public static bool ContainsKeyLikeValue(string? value) =>
        value is not null && EmbeddedKeyPattern().IsMatch(value);
}

/// <summary>
/// HMAC-SHA-256 of the secret, keyed with a server-side pepper (ADR-0008). Keys are 256-bit random values, so a slow
/// password hash adds nothing; the pepper means a database dump alone can't be used to test guesses.
/// </summary>
public sealed class ApiKeyHasher
{
    public const int MinimumPepperBytes = 32;
    private readonly byte[] _pepper;

    public ApiKeyHasher(byte[] pepper)
    {
        ArgumentNullException.ThrowIfNull(pepper);
        if (pepper.Length < MinimumPepperBytes)
        {
            throw new ArgumentException($"The API key pepper must be at least {MinimumPepperBytes} bytes.", nameof(pepper));
        }

        _pepper = (byte[])pepper.Clone();
    }

    public byte[] Hash(string secret) => HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(secret));

    public static bool Matches(ReadOnlySpan<byte> computed, ReadOnlySpan<byte> stored) =>
        CryptographicOperations.FixedTimeEquals(computed, stored);
}

/// <summary>What the database holds about a key, as returned by the pre-tenant lookup.</summary>
public sealed record StoredApiKey(
    Guid Id,
    Guid OrganizationId,
    Guid? WorkspaceId,
    Guid OwnerSubjectId,
    byte[] SecretHash,
    string Environment,
    IReadOnlyList<string> Permissions,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt);

public interface IApiKeyStore
{
    /// <summary>Pre-tenant lookup by the public key id (ADR-0017).</summary>
    Task<StoredApiKey?> FindByKeyIdAsync(string keyId, CancellationToken ct);

    /// <summary>
    /// In the key's organization: true if the owner still has an active membership. If so, records the use
    /// (<c>last_used_at</c>, written at most once a minute).
    /// </summary>
    Task<bool> ConfirmOwnerAndRecordUseAsync(StoredApiKey key, DateTimeOffset now, CancellationToken ct);
}

public enum ApiKeyFailure
{
    Malformed,
    WrongEnvironment,
    UnknownKey,
    InvalidSecret,
    Revoked,
    Expired,
    OwnerNoLongerMember,
}

public sealed record AuthenticatedApiKey(
    Guid ApiKeyId, Guid OrganizationId, Guid? WorkspaceId, Guid OwnerSubjectId, IReadOnlyList<string> Permissions);

public sealed record ApiKeyAuthenticationResult(AuthenticatedApiKey? Key, ApiKeyFailure? Failure)
{
    public static ApiKeyAuthenticationResult Fail(ApiKeyFailure failure) => new(null, failure);
}

/// <summary>
/// Verifies a presented API key. Every failure looks the same to the caller (401); the reason is only for logs.
/// Permissions returned here are the key's ceiling; authorization still intersects them with the owner's current
/// permissions on every request (ADR-0008, ADR-0010).
/// </summary>
public sealed class ApiKeyAuthenticator(IApiKeyStore store, ApiKeyHasher hasher, string environment, TimeProvider time)
{
    public async Task<ApiKeyAuthenticationResult> AuthenticateAsync(string presented, CancellationToken ct)
    {
        if (!ApiKeyFormat.TryParse(presented, out var parts))
        {
            return ApiKeyAuthenticationResult.Fail(ApiKeyFailure.Malformed);
        }

        // Before any lookup: a key minted for another environment is never valid here.
        if (parts.Environment != environment)
        {
            return ApiKeyAuthenticationResult.Fail(ApiKeyFailure.WrongEnvironment);
        }

        var stored = await store.FindByKeyIdAsync(parts.KeyId, ct);

        // Hash even when the key is unknown, so response time doesn't reveal whether a key id exists.
        var computed = hasher.Hash(parts.Secret);
        if (stored is null)
        {
            return ApiKeyAuthenticationResult.Fail(ApiKeyFailure.UnknownKey);
        }

        if (!ApiKeyHasher.Matches(computed, stored.SecretHash))
        {
            return ApiKeyAuthenticationResult.Fail(ApiKeyFailure.InvalidSecret);
        }

        if (stored.Environment != environment)
        {
            return ApiKeyAuthenticationResult.Fail(ApiKeyFailure.WrongEnvironment);
        }

        var now = time.GetUtcNow();
        if (stored.RevokedAt is not null)
        {
            return ApiKeyAuthenticationResult.Fail(ApiKeyFailure.Revoked);
        }

        if (stored.ExpiresAt is { } expires && expires <= now)
        {
            return ApiKeyAuthenticationResult.Fail(ApiKeyFailure.Expired);
        }

        if (!await store.ConfirmOwnerAndRecordUseAsync(stored, now, ct))
        {
            return ApiKeyAuthenticationResult.Fail(ApiKeyFailure.OwnerNoLongerMember);
        }

        return new ApiKeyAuthenticationResult(
            new AuthenticatedApiKey(stored.Id, stored.OrganizationId, stored.WorkspaceId, stored.OwnerSubjectId, stored.Permissions),
            null);
    }
}
