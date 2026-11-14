using System.Reflection;
using Aiep.Api.Endpoints;
using Aiep.Application.Tenancy;
using Aiep.Domain.Runs;
using Aiep.Infrastructure;
using Aiep.Worker;

namespace Aiep.Architecture.Tests;

/// <summary>
/// Enforces the module boundaries of ADR-0002 by inspecting each assembly's manifest references (the compiler only
/// records a reference to an assembly whose types are actually used, so these catch real dependencies, not just project
/// references). Domain depends on nothing of ours; Application never reaches Infrastructure; the hosts never touch the
/// database libraries directly — all tenant access goes through the Infrastructure unit of work (ADR-0006, ADR-0013).
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(TaskRunStatus).Assembly;
    private static readonly Assembly Application = typeof(TenantContext).Assembly;
    private static readonly Assembly Infrastructure = typeof(InfrastructureServiceCollectionExtensions).Assembly;
    private static readonly Assembly Api = typeof(OrganizationEndpoints).Assembly;
    private static readonly Assembly Worker = typeof(RunProcessor).Assembly;

    [Fact]
    public void Domain_depends_only_on_the_base_class_library()
    {
        var external = References(Domain).Where(name => !IsBaseClassLibrary(name)).ToList();

        Assert.True(external.Count == 0,
            "Domain must have no framework or infrastructure dependencies, but references: " + string.Join(", ", external));
    }

    [Fact]
    public void Application_does_not_reference_infrastructure_hosts_or_database_libraries()
    {
        AssertDoesNotReference(Application,
            "Aiep.Infrastructure", "Aiep.Api", "Aiep.Worker",
            "Npgsql", "Microsoft.EntityFrameworkCore");
    }

    [Fact]
    public void Hosts_do_not_touch_the_database_libraries_directly()
    {
        // All tenant data access goes through the Infrastructure unit of work, never Npgsql or EF from a host.
        AssertDoesNotReference(Api, "Npgsql", "Microsoft.EntityFrameworkCore");
        AssertDoesNotReference(Worker, "Npgsql", "Microsoft.EntityFrameworkCore");
    }

    [Fact]
    public void Infrastructure_and_hosts_do_not_depend_on_each_other_the_wrong_way()
    {
        // Infrastructure is referenced by the hosts, never the reverse.
        AssertDoesNotReference(Infrastructure, "Aiep.Api", "Aiep.Worker");
    }

    private static void AssertDoesNotReference(Assembly assembly, params string[] forbidden)
    {
        var references = References(assembly);
        var violations = forbidden.Where(f => references.Contains(f, StringComparer.Ordinal)).ToList();

        Assert.True(violations.Count == 0,
            $"{assembly.GetName().Name} must not reference: {string.Join(", ", violations)}.");
    }

    private static IReadOnlyCollection<string> References(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(a => a.Name!)];

    private static bool IsBaseClassLibrary(string name) =>
        name.StartsWith("System", StringComparison.Ordinal)
        || name is "netstandard" or "mscorlib" or "WindowsBase";
}
