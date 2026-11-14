using System.Text;
using Aiep.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aiep.Infrastructure.Persistence;

/// <summary>
/// EF Core mappings for schema <c>aiep</c> (ADR-0013: mappings only, no EF migrations).
/// Only <see cref="TenantDatabase"/> can create one, bound to its tenant-scoped connection and transaction, so there
/// is no way to query tenant tables outside a tenant context.
/// </summary>
public sealed class AiepDbContext : DbContext
{
    private AiepDbContext(DbContextOptions<AiepDbContext> options)
        : base(options)
    {
    }

    public DbSet<Subject> Subjects => Set<Subject>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMembership> WorkspaceMemberships => Set<WorkspaceMembership>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<TaskDefinition> TaskDefinitions => Set<TaskDefinition>();
    public DbSet<TaskDefinitionVersion> TaskDefinitionVersions => Set<TaskDefinitionVersion>();
    public DbSet<TaskRun> TaskRuns => Set<TaskRun>();
    public DbSet<TaskRunAttempt> TaskRunAttempts => Set<TaskRunAttempt>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    internal static AiepDbContext Create(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var options = new DbContextOptionsBuilder<AiepDbContext>()
            .UseNpgsql(connection)
            .Options;
        var context = new AiepDbContext(options);
        context.Database.UseTransaction(transaction);
        return context;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("aiep");

        modelBuilder.Entity<Subject>().ToTable("subjects");
        modelBuilder.Entity<Organization>().ToTable("organizations");
        modelBuilder.Entity<Membership>().ToTable("memberships");
        modelBuilder.Entity<Workspace>().ToTable("workspaces");
        modelBuilder.Entity<WorkspaceMembership>().ToTable("workspace_memberships");
        modelBuilder.Entity<ApiKey>().ToTable("api_keys");
        modelBuilder.Entity<AuditEvent>().ToTable("audit_events");
        modelBuilder.Entity<TaskDefinition>().ToTable("task_definitions");
        modelBuilder.Entity<TaskDefinitionVersion>().ToTable("task_definition_versions");
        modelBuilder.Entity<TaskRun>().ToTable("task_runs");
        modelBuilder.Entity<TaskRunAttempt>().ToTable("task_run_attempts");
        modelBuilder.Entity<IdempotencyKey>().ToTable("idempotency_keys");

        // jsonb columns: stored and read as JSON strings (Application owns their shape).
        modelBuilder.Entity<TaskDefinitionVersion>().Property(v => v.InputSchema).HasColumnType("jsonb");
        modelBuilder.Entity<TaskDefinitionVersion>().Property(v => v.OutputSchema).HasColumnType("jsonb");
        modelBuilder.Entity<TaskDefinitionVersion>().Property(v => v.ModelConfig).HasColumnType("jsonb");
        modelBuilder.Entity<TaskRun>().Property(r => r.Input).HasColumnType("jsonb");
        modelBuilder.Entity<TaskRun>().Property(r => r.Output).HasColumnType("jsonb");

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));

                // Ids are application-generated UUIDv7; never let EF generate them.
                if (property.Name == "Id")
                {
                    property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }

                // Unset timestamps take the column default (now()).
                if (property.Name is "CreatedAt" or "UpdatedAt" or "OccurredAt")
                {
                    property.SetDefaultValueSql("now()");
                }
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }
}
