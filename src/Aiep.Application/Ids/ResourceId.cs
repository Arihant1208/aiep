namespace Aiep.Application.Ids;

/// <summary>
/// Public resource identifiers (ADR-0015): <c>{prefix}_{26-char Crockford base32 of the UUIDv7}</c>, for example
/// <c>org_068ZK3Q0J1R4XW5N9T7B2C6D8E</c>. The prefix catches mixed-up IDs; the UUID is never exposed raw.
/// </summary>
public static class ResourceId
{
    public const string Organization = "org";
    public const string Workspace = "ws";
    public const string Subject = "sub";
    public const string ApiKey = "key";
    public const string Membership = "mem";

    public static string Format(string prefix, Guid id) => $"{prefix}_{CrockfordBase32.Encode128(id)}";

    /// <summary>Parses an ID with exactly the expected prefix. Wrong prefix, bad characters or length → false.</summary>
    public static bool TryParse(string prefix, string? value, out Guid id)
    {
        id = Guid.Empty;
        if (value is null || value.Length != prefix.Length + 27
            || !value.StartsWith(prefix, StringComparison.Ordinal) || value[prefix.Length] != '_')
        {
            return false;
        }

        return CrockfordBase32.TryDecode128(value.AsSpan(prefix.Length + 1), out id) && id != Guid.Empty;
    }
}
