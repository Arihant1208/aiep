using Aiep.Migrator;

namespace Aiep.Database.Tests;

public sealed class MigrationDiscoveryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aiep-migrations-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Loads_scripts_in_version_order_and_ignores_non_sql_files()
    {
        File.WriteAllText(Path.Combine(_dir, "V0002__second.sql"), "SELECT 2;");
        File.WriteAllText(Path.Combine(_dir, "V0001__first_one.sql"), "SELECT 1;");
        File.WriteAllText(Path.Combine(_dir, "README.md"), "docs");

        var scripts = MigrationDiscovery.Load(_dir);

        Assert.Equal(["V0001__first_one", "V0002__second"], scripts.Select(s => s.DisplayName));
    }

    [Theory]
    [InlineData("V1__short_version.sql")]
    [InlineData("V0001_single_underscore.sql")]
    [InlineData("V0001__Upper_Case.sql")]
    [InlineData("0001__no_prefix.sql")]
    [InlineData("V0000__zero.sql")]
    public void Rejects_badly_named_or_zero_version_files(string fileName)
    {
        File.WriteAllText(Path.Combine(_dir, fileName), "SELECT 1;");

        Assert.Throws<MigrationException>(() => MigrationDiscovery.Load(_dir));
    }

    [Fact]
    public void Rejects_duplicate_versions()
    {
        File.WriteAllText(Path.Combine(_dir, "V0001__a.sql"), "SELECT 1;");
        File.WriteAllText(Path.Combine(_dir, "V0001__b.sql"), "SELECT 1;");

        var ex = Assert.Throws<MigrationException>(() => MigrationDiscovery.Load(_dir));
        Assert.Contains("Duplicate migration version 0001", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Checksum_ignores_line_ending_style_but_not_content()
    {
        var lf = MigrationScript.FromText(1, "a", "SELECT 1;\nSELECT 2;\n");
        var crlf = MigrationScript.FromText(1, "a", "SELECT 1;\r\nSELECT 2;\r\n");
        var edited = MigrationScript.FromText(1, "a", "SELECT 1;\nSELECT 3;\n");

        Assert.Equal(lf.ChecksumSha256, crlf.ChecksumSha256);
        Assert.NotEqual(lf.ChecksumSha256, edited.ChecksumSha256);
    }

    [Fact]
    public void Repository_migrations_are_well_formed()
    {
        var scripts = MigrationDiscovery.Load(PostgresFixture.MigrationsDirectory);

        Assert.NotEmpty(scripts);
        Assert.Equal(1, scripts[0].Version);
        Assert.Equal(Enumerable.Range(1, scripts.Count), scripts.Select(s => s.Version));
    }
}
