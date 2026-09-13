using System.Text.RegularExpressions;
using DuckDB.NET.Data;

namespace CodeExplorer.Api;

/// <summary>A project as stored in the control database: the stable slug plus a free display name.</summary>
public sealed record Project(string Slug, string Name);

/// <summary>Outcome of creating a project. The handler maps each case to a status code and nothing more.</summary>
public enum CreateProjectOutcome
{
    Created,
    InvalidSlug,
    MissingName,
    SlugTaken
}

/// <summary>
/// Owns <c>control.duckdb</c>: projects, and later repositories and credentials (ADR-0004). It is a
/// plain file next to the project indexes and is never shadow-rebuilt. It is opened standalone, not
/// attached, so the "USE slug before every query" rule for project indexes does not apply here.
/// </summary>
public sealed partial class ControlDatabase
{
    public const string SlugRule =
        "Slug must be 1-64 lowercase letters, digits or hyphens, starting and ending with a letter or digit.";

    private readonly string _connectionString;

    public ControlDatabase(IConfiguration configuration)
    {
        // Absent configuration selects a local folder, so `dotnet run` needs no settings at all.
        string directory = configuration["Storage:DataDirectory"] ?? "data";
        Directory.CreateDirectory(directory);
        _connectionString = $"Data Source={Path.Combine(directory, "control.duckdb")}";

        // Synchronous on purpose: this runs once at startup, before any request could cancel it.
        using var connection = new DuckDBConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS projects (slug VARCHAR PRIMARY KEY, name VARCHAR NOT NULL)";
        command.ExecuteNonQuery();
    }

    /// <summary>
    ///     A slug is a URL path segment agents keep in their configuration, so it is limited to what
    ///     survives every client's URL handling unescaped: lowercase ASCII letters, digits and hyphens,
    ///     at most 64 characters.
    /// </summary>
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex SlugPattern { get; }

    public async Task<CreateProjectOutcome> CreateAsync(string slug, string? name, CancellationToken cancellationToken)
    {
        if (!SlugPattern.IsMatch(slug)) return CreateProjectOutcome.InvalidSlug;

        if (string.IsNullOrWhiteSpace(name)) return CreateProjectOutcome.MissingName;

        using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        // ON CONFLICT DO NOTHING keeps the existence check and the insert one statement, so two
        // concurrent creates cannot both succeed.
        command.CommandText = "INSERT INTO projects (slug, name) VALUES ($slug, $name) ON CONFLICT DO NOTHING";
        command.Parameters.Add(new DuckDBParameter("slug", slug));
        command.Parameters.Add(new DuckDBParameter("name", name.Trim()));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? CreateProjectOutcome.Created
            : CreateProjectOutcome.SlugTaken;
    }

    public async Task<Project?> FindAsync(string slug, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM projects WHERE slug = $slug";
        command.Parameters.Add(new DuckDBParameter("slug", slug));
        object? name = await command.ExecuteScalarAsync(cancellationToken);
        return name is string n ? new Project(slug, n) : null;
    }

    private async Task<DuckDBConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new DuckDBConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
