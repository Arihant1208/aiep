namespace Aiep.Application.Identity;

/// <summary>
/// A person as asserted by a validated OIDC token. <c>(Issuer, Subject)</c> is the identity key (ADR-0008);
/// display name and email are informational only and are never used to identify or authorize anyone.
/// </summary>
public sealed record ExternalIdentity
{
    private ExternalIdentity(string issuer, string subject, string? displayName, string? email)
    {
        Issuer = issuer;
        Subject = subject;
        DisplayName = displayName;
        Email = email;
    }

    public string Issuer { get; }
    public string Subject { get; }
    public string? DisplayName { get; }
    public string? Email { get; }

    /// <summary>
    /// Returns null when the identity key is unusable (missing or over the stored length). Display data that doesn't
    /// fit is truncated (name) or dropped (email) rather than failing sign-in.
    /// </summary>
    public static ExternalIdentity? TryCreate(string? issuer, string? subject, string? displayName, string? email)
    {
        if (string.IsNullOrWhiteSpace(issuer) || issuer.Length > 512
            || string.IsNullOrWhiteSpace(subject) || subject.Length > 255)
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        if (name is { Length: > 200 })
        {
            name = name[..200];
        }

        var mail = string.IsNullOrWhiteSpace(email) || email.Length > 320 ? null : email.Trim();
        return new ExternalIdentity(issuer, subject, name, mail);
    }
}

/// <summary>Maps an external identity to the internal subject id, creating the subject on first sign-in.</summary>
public interface ISubjectDirectory
{
    Task<Guid> ResolveAsync(ExternalIdentity identity, CancellationToken ct);
}
