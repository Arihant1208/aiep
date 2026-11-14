using Aiep.Application.Tenancy;

namespace Aiep.Application.Execution;

/// <summary>
/// The result of reserving an idempotency key (ADR-0015). <see cref="Existing"/> is set when the key was seen before:
/// the caller returns that run (same payload) or a 409 (different payload), decided by <see cref="PayloadMatches"/>.
/// </summary>
public sealed record IdempotencyReservation(Guid? Existing, bool PayloadMatches)
{
    public static readonly IdempotencyReservation Fresh = new(null, true);
}

/// <summary>
/// Records and looks up idempotency keys, scoped to (organization, workspace, caller) (ADR-0015). The key and payload
/// are stored only as SHA-256 hashes.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Look up an existing mapping for this key within the caller's scope. Returns <see cref="IdempotencyReservation.Fresh"/>
    /// when the key is new.
    /// </summary>
    Task<IdempotencyReservation> LookupAsync(
        TenantContext context,
        Guid workspaceId,
        Guid callerId,
        string idempotencyKey,
        string payload,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Record that <paramref name="runId"/> answers this key within the caller's scope. Returns false if another request
    /// won the race and already recorded a mapping for the same key (the caller then re-reads it).
    /// </summary>
    Task<bool> TryRecordAsync(
        TenantContext context,
        Guid workspaceId,
        Guid callerId,
        string idempotencyKey,
        string payload,
        Guid runId,
        TimeSpan retention,
        CancellationToken cancellationToken = default);
}
