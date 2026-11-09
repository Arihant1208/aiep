using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aiep.Migrator;

/// <summary>One versioned SQL migration file (<c>V0001__short_name.sql</c>).</summary>
public sealed record MigrationScript(int Version, string Name, string Sql, string ChecksumSha256)
{
    public string DisplayName => $"V{Version:D4}__{Name}";

    public static MigrationScript FromText(int version, string name, string text)
    {
        // Normalize line endings so a Windows checkout cannot change the checksum of an applied script.
        var sql = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        return new MigrationScript(version, name, sql, checksum);
    }
}

public static partial class MigrationDiscovery
{
    [GeneratedRegex(@"^V(?<version>\d{4})__(?<name>[a-z0-9]+(?:_[a-z0-9]+)*)\.sql$")]
    private static partial Regex FileNamePattern();

    /// <summary>
    /// Loads every <c>*.sql</c> file in <paramref name="directory"/>, ordered by version. Any badly named SQL file,
    /// version 0 or duplicate version is an error: silently skipping a file would skip a schema change.
    /// </summary>
    public static IReadOnlyList<MigrationScript> Load(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new MigrationException($"Migrations directory '{directory}' does not exist.");
        }

        var scripts = new List<MigrationScript>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.sql"))
        {
            var fileName = Path.GetFileName(path);
            var match = FileNamePattern().Match(fileName);
            if (!match.Success)
            {
                throw new MigrationException(
                    $"Migration file '{fileName}' does not match V0001__lower_snake_name.sql.");
            }

            var version = int.Parse(match.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (version == 0)
            {
                throw new MigrationException($"Migration file '{fileName}' uses version 0; versions start at 1.");
            }

            scripts.Add(MigrationScript.FromText(version, match.Groups["name"].Value, File.ReadAllText(path)));
        }

        var duplicate = scripts.GroupBy(s => s.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new MigrationException(
                $"Duplicate migration version {duplicate.Key:D4}: {string.Join(", ", duplicate.Select(s => s.DisplayName))}.");
        }

        return [.. scripts.OrderBy(s => s.Version)];
    }
}

public sealed class MigrationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
