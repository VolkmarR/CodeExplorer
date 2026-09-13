using System.Text.RegularExpressions;
using DuckDB.NET.Data;

namespace CodeExplorer.Api;

/// <summary>A project as stored in the control database: the stable slug plus a free display name.</summary>
public sealed record Project(string Slug, string Name);

/// <summary>
/// Owns <c>control.duckdb</c>: projects, and later repositories and credentials (ADR-0004). It is a
/// plain file next to the project indexes and is never shadow-rebuilt.
/// </summary>
public sealed partial class ControlDatabase
{
    /// <summary>
    /// A slug is a URL path segment agents keep in their configuration, so it is limited to what
    /// survives every client's URL handling unescaped: lowercase ASCII letters, digits and hyphens.
    /// </summary>
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex SlugPattern { get; }

    private readonly string _connectionString;

    public ControlDatabase(IConfiguration configuration)
    {
        // Absent configuration selects a local folder, so `dotnet run` needs no settings at all.
        var directory = configuration["Storage:DataDirectory"] ?? "data";
        Directory.CreateDirectory(directory);
        _connectionString = $"Data Source={Path.Combine(directory, "control.duckdb")}";

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS projects (slug VARCHAR PRIMARY KEY, name VARCHAR NOT NULL)";
        command.ExecuteNonQuery();
    }

    public static bool IsValidSlug(string slug) => SlugPattern.IsMatch(slug);

    /// <summary>Inserts a project; returns <c>false</c> when the slug is already taken.</summary>
    public async Task<bool> TryCreateAsync(Project project, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        // ON CONFLICT DO NOTHING keeps the existence check and the insert one statement, so two
        // concurrent creates cannot both succeed.
        command.CommandText = "INSERT INTO projects (slug, name) VALUES ($slug, $name) ON CONFLICT DO NOTHING";
        command.Parameters.Add(new DuckDBParameter("slug", project.Slug));
        command.Parameters.Add(new DuckDBParameter("name", project.Name));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<Project?> FindAsync(string slug, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM projects WHERE slug = $slug";
        command.Parameters.Add(new DuckDBParameter("slug", slug));
        var name = await command.ExecuteScalarAsync(cancellationToken);
        return name is string n ? new Project(slug, n) : null;
    }

    private DuckDBConnection Open()
    {
        var connection = new DuckDBConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
