using System.Security.Cryptography;
using Aiep.Application.Identity;

namespace Aiep.Application.Tests;

public sealed class ApiKeyFormatTests
{
    [Fact]
    public void Generated_keys_parse_back_into_their_parts()
    {
        var key = ApiKeyFormat.Generate("prod");

        Assert.Matches("^aiep_prod_[0-9a-hjkmnp-tv-z]{16}_[0-9a-hjkmnp-tv-z]{52}$", key.Plaintext);
        Assert.True(ApiKeyFormat.TryParse(key.Plaintext, out var parts));
        Assert.Equal(new ApiKeyParts("prod", key.KeyId, key.Secret), parts);
    }

    [Fact]
    public void Generated_keys_are_unique()
    {
        var keys = Enumerable.Range(0, 1000).Select(_ => ApiKeyFormat.Generate("dev")).ToList();

        Assert.Equal(keys.Count, keys.Select(k => k.KeyId).Distinct().Count());
        Assert.Equal(keys.Count, keys.Select(k => k.Secret).Distinct().Count());
    }

    [Theory]
    [InlineData("PROD")]
    [InlineData("p")]
    [InlineData("staging_eu")]
    [InlineData("")]
    public void Generate_rejects_invalid_environments(string environment) =>
        Assert.Throws<ArgumentException>(() => ApiKeyFormat.Generate(environment));

    public static TheoryData<string?> Malformed()
    {
        var valid = ApiKeyFormat.Generate("dev").Plaintext;
        return new TheoryData<string?>
        {
            null,
            string.Empty,
            valid.ToUpperInvariant(),
            valid[..^1],
            valid + "0",
            valid.Replace("aiep_", "aeip_", StringComparison.Ordinal),
            " " + valid,
            valid + "\n",
            valid[..^1] + "i", // excluded letter
            valid + new string('0', 200),
        };
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Rejects_malformed_keys(string? value) => Assert.False(ApiKeyFormat.TryParse(value, out _));

    [Theory]
    [InlineData("/v1/me?api_key=aiep_dev_0123456789abcdef_x", true)]
    [InlineData("AIEP_DEV_0123456789ABCDEF_", true)]
    [InlineData("aiep_dev_short_", false)]
    [InlineData("nothing to see", false)]
    public void Spots_key_like_values_inside_other_text(string value, bool expected) =>
        Assert.Equal(expected, ApiKeyFormat.ContainsKeyLikeValue(value));
}

public sealed class ApiKeyHasherTests
{
    [Fact]
    public void Requires_a_pepper_of_at_least_32_bytes()
    {
        Assert.Throws<ArgumentException>(() => new ApiKeyHasher(new byte[31]));
        _ = new ApiKeyHasher(new byte[32]);
    }

    [Fact]
    public void Hash_depends_on_both_secret_and_pepper()
    {
        var pepperA = new ApiKeyHasher(RandomNumberGenerator.GetBytes(32));
        var pepperB = new ApiKeyHasher(RandomNumberGenerator.GetBytes(32));

        Assert.Equal(32, pepperA.Hash("secret").Length);
        Assert.Equal(pepperA.Hash("secret"), pepperA.Hash("secret"));
        Assert.NotEqual(pepperA.Hash("secret"), pepperA.Hash("secreu"));
        Assert.NotEqual(pepperA.Hash("secret"), pepperB.Hash("secret"));
    }
}

public sealed class ApiKeyAuthenticatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly ApiKeyHasher _hasher = new(RandomNumberGenerator.GetBytes(32));
    private readonly FakeStore _store = new();
    private readonly GeneratedApiKey _key = ApiKeyFormat.Generate("test");

    private ApiKeyAuthenticator Authenticator() => new(_store, _hasher, "test", new FixedTime(Now));

    private StoredApiKey Stored(Action<StoredApiKeyBuilder>? change = null)
    {
        var builder = new StoredApiKeyBuilder { SecretHash = _hasher.Hash(_key.Secret) };
        change?.Invoke(builder);
        return builder.Build();
    }

    [Fact]
    public async Task A_valid_key_authenticates_with_its_stored_scope()
    {
        var stored = Stored();
        _store.Keys[_key.KeyId] = stored;

        var result = await Authenticator().AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        Assert.Null(result.Failure);
        Assert.Equal(stored.Id, result.Key!.ApiKeyId);
        Assert.Equal(stored.OwnerSubjectId, result.Key.OwnerSubjectId);
        Assert.Equal(["runs:submit"], result.Key.Permissions);
        Assert.Equal([stored.Id], _store.RecordedUses);
    }

    [Fact]
    public async Task Malformed_input_is_rejected_without_a_lookup()
    {
        var result = await Authenticator().AuthenticateAsync("aiep_test_nope", CancellationToken.None);

        Assert.Equal(ApiKeyFailure.Malformed, result.Failure);
        Assert.Equal(0, _store.Lookups);
    }

    [Fact]
    public async Task A_key_for_another_environment_is_rejected_without_a_lookup()
    {
        var prodKey = ApiKeyFormat.Generate("prod");

        var result = await Authenticator().AuthenticateAsync(prodKey.Plaintext, CancellationToken.None);

        Assert.Equal(ApiKeyFailure.WrongEnvironment, result.Failure);
        Assert.Equal(0, _store.Lookups);
    }

    [Fact]
    public async Task A_stored_key_from_another_environment_is_rejected_even_with_a_matching_secret()
    {
        _store.Keys[_key.KeyId] = Stored(b => b.Environment = "prod");

        var result = await Authenticator().AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        Assert.Equal(ApiKeyFailure.WrongEnvironment, result.Failure);
    }

    [Fact]
    public async Task Unknown_key_ids_are_rejected()
    {
        var result = await Authenticator().AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        Assert.Equal(ApiKeyFailure.UnknownKey, result.Failure);
    }

    [Fact]
    public async Task A_wrong_secret_is_rejected_and_use_is_not_recorded()
    {
        _store.Keys[_key.KeyId] = Stored(b => b.SecretHash = _hasher.Hash("something-else"));

        var result = await Authenticator().AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        Assert.Equal(ApiKeyFailure.InvalidSecret, result.Failure);
        Assert.Empty(_store.RecordedUses);
    }

    [Fact]
    public async Task Revoked_keys_are_rejected()
    {
        _store.Keys[_key.KeyId] = Stored(b => b.RevokedAt = Now.AddDays(-1));

        Assert.Equal(ApiKeyFailure.Revoked, (await Authenticator().AuthenticateAsync(_key.Plaintext, CancellationToken.None)).Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Keys_at_or_past_expiry_are_rejected(int secondsFromNow)
    {
        _store.Keys[_key.KeyId] = Stored(b => b.ExpiresAt = Now.AddSeconds(secondsFromNow));

        Assert.Equal(ApiKeyFailure.Expired, (await Authenticator().AuthenticateAsync(_key.Plaintext, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task Keys_before_expiry_are_accepted()
    {
        _store.Keys[_key.KeyId] = Stored(b => b.ExpiresAt = Now.AddSeconds(1));

        Assert.Null((await Authenticator().AuthenticateAsync(_key.Plaintext, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task Keys_whose_owner_left_the_organization_are_rejected()
    {
        _store.Keys[_key.KeyId] = Stored();
        _store.OwnerIsMember = false;

        Assert.Equal(ApiKeyFailure.OwnerNoLongerMember, (await Authenticator().AuthenticateAsync(_key.Plaintext, CancellationToken.None)).Failure);
    }

    private sealed class FakeStore : IApiKeyStore
    {
        public Dictionary<string, StoredApiKey> Keys { get; } = [];
        public List<Guid> RecordedUses { get; } = [];
        public bool OwnerIsMember { get; set; } = true;
        public int Lookups { get; private set; }

        public Task<StoredApiKey?> FindByKeyIdAsync(string keyId, CancellationToken ct)
        {
            Lookups++;
            return Task.FromResult(Keys.GetValueOrDefault(keyId));
        }

        public Task<bool> ConfirmOwnerAndRecordUseAsync(StoredApiKey key, DateTimeOffset now, CancellationToken ct)
        {
            if (OwnerIsMember)
            {
                RecordedUses.Add(key.Id);
            }

            return Task.FromResult(OwnerIsMember);
        }
    }

    private sealed class StoredApiKeyBuilder
    {
        public required byte[] SecretHash { get; set; }
        public string Environment { get; set; } = "test";
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }

        public StoredApiKey Build() => new(
            Guid.CreateVersion7(), Guid.CreateVersion7(), null, Guid.CreateVersion7(), SecretHash, Environment,
            ["runs:submit"], ExpiresAt, RevokedAt);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
