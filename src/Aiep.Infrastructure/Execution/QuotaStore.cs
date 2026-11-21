using Aiep.Application.ControlPlane;
using Aiep.Application.Execution;
using Aiep.Application.Tenancy;
using Aiep.Infrastructure.Persistence;
using Aiep.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aiep.Infrastructure.Execution;

/// <summary>Budget administration over the tenant unit of work (threat T12). Enforcement is in <see cref="QuotaLedger"/>.</summary>
internal sealed class QuotaStore(TenantDatabase database, TimeProvider time) : IQuota
{
    public Task SetOrganizationBudgetAsync(
        ControlPlaneActor actor, Guid organizationId, int windowSeconds, decimal limitUsd, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            var existing = await session.Db.QuotaBudgets
                .SingleOrDefaultAsync(b => b.WorkspaceId == null, token);
            if (existing is null)
            {
                session.Db.QuotaBudgets.Add(new QuotaBudget
                {
                    Id = Guid.CreateVersion7(),
                    OrganizationId = organizationId,
                    WorkspaceId = null,
                    WindowSeconds = windowSeconds,
                    LimitUsd = limitUsd,
                    CreatedBy = actor.SubjectId,
                });
            }
            else
            {
                existing.WindowSeconds = windowSeconds;
                existing.LimitUsd = limitUsd;
                existing.UpdatedAt = time.GetUtcNow();
            }

            session.Db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                ActorType = actor.ActorType,
                ActorSubjectId = actor.SubjectId,
                ActorApiKeyId = actor.ApiKeyId,
                Action = "quota.budget.set",
                TargetType = "organization",
                TargetId = organizationId,
                Outcome = "success",
                CorrelationId = actor.CorrelationId,
            });
            await session.Db.SaveChangesAsync(token);
        }, cancellationToken);

    public Task<QuotaUsage> GetOrganizationUsageAsync(
        Guid organizationId, Guid subjectId, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var budget = await session.Db.QuotaBudgets.AsNoTracking().SingleOrDefaultAsync(b => b.WorkspaceId == null, token);
            if (budget is null)
            {
                return new QuotaUsage(false, 0, 0m, 0m);
            }

            await using var sum = new NpgsqlCommand(
                """
                SELECT coalesce(sum(coalesce(actual_usd, estimated_usd)), 0)
                FROM aiep.quota_reservations
                WHERE organization_id = $1 AND created_at >= now() - make_interval(secs => $2)
                """, session.Connection, session.Transaction);
            sum.Parameters.Add(new NpgsqlParameter { Value = organizationId });
            sum.Parameters.Add(new NpgsqlParameter { Value = budget.WindowSeconds });
            var committed = (decimal)(await sum.ExecuteScalarAsync(token))!;

            return new QuotaUsage(true, budget.WindowSeconds, budget.LimitUsd, committed);
        }, cancellationToken);
}
