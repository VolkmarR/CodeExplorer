using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.DataProtection;

namespace CodeExplorer;

/// <summary>A project as stored in the control database: the stable slug plus a free display name.</summary>
public sealed record Project(string Slug, string Name);

/// <summary>
///     A repository of a project. <paramref name="ProtectedCredential" /> is <c>IDataProtector</c>
///     ciphertext or null; it leaves this record only through <see cref="GitClones" />, never through
///     a response, a log or an error message.
/// </summary>
public sealed record ProjectRepository(string ProjectSlug, string Slug, string Url, string? ProtectedCredential)
{
    public bool HasCredential => ProtectedCredential is not null;

    /// <summary>Keeps the ciphertext out of anything that stringifies the record, such as a log scope.</summary>
    public override string ToString() =>
        $"{ProjectSlug}/{Slug} ({Url}, credential {(HasCredential ? "set" : "not set")})";
}

/// <summary>Outcome of creating a project. The handler maps each case to a status code and nothing more.</summary>
public enum CreateProjectOutcome
{
    Created,
    InvalidSlug,
    MissingName,
    SlugTaken
}

/// <summary>Outcome of adding a repository to a project.</summary>
public enum AddRepositoryOutcome
{
    Created,
    NoProject,
    InvalidSlug,
    InvalidUrl,
    SlugTaken
}

/// <summary>
///     Owns <c>control.duckdb</c>: projects, repositories and credentials (ADR-0004). It is a
///     plain file next to the project indexes and is never shadow-rebuilt. It is opened standalone, not
///     attached, so the "USE slug before every query" rule for project indexes does not apply here.
/// </summary>
public sealed partial class ControlDatabase
{
    public const string SlugRule =
        "Slug must be 1-64 lowercase letters, digits or hyphens, starting and ending with a letter or digit.";

    /// <summary>
    ///     The Data Protection purpose string for stored credentials. Ciphertext protected under one
    ///     purpose cannot be unprotected under another, so this constant is the only shared secret between
    ///     the writer here and the reader in <see cref="GitClones" />.
    /// </summary>
    public const string CredentialPurpose = "CodeExplorer.RepositoryCredential";

    private readonly string _connectionString;
    private readonly IDataProtector _protector;

    public ControlDatabase(IConfiguration configuration, IDataProtectionProvider dataProtection)
    {
        _protector = dataProtection.CreateProtector(CredentialPurpose);

        // Absent configuration selects a local folder, so `dotnet run` needs no settings at all.
        string directory = configuration["Storage:DataDirectory"] ?? "data";
        Directory.CreateDirectory(directory);
        _connectionString = $"Data Source={Path.Combine(directory, "control.duckdb")}";

        // Synchronous on purpose: this runs once at startup, before any request could cancel it.
        using var connection = new DuckDBConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE IF NOT EXISTS projects (slug VARCHAR PRIMARY KEY, name VARCHAR NOT NULL);
                              -- No foreign key: DuckDB forbids deleting a referenced row even inside one transaction,
                              -- which would make project deletion awkward later. Project existence is checked in code.
                              CREATE TABLE IF NOT EXISTS repositories (
                                  project_slug VARCHAR NOT NULL,
                                  slug VARCHAR NOT NULL,
                                  url VARCHAR NOT NULL,
                                  credential VARCHAR,
                                  PRIMARY KEY (project_slug, slug));
                              """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    ///     A slug is a URL path segment agents keep in their configuration, so it is limited to what
    ///     survives every client's URL handling unescaped: lowercase ASCII letters, digits and hyphens,
    ///     at most 64 characters. The same rule applies to repository slugs, which head every
    ///     qualified path.
    /// </summary>
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex SlugPattern { get; }

    public static bool IsValidSlug(string slug) => SlugPattern.IsMatch(slug);

    public async Task<CreateProjectOutcome> CreateAsync(string slug, string? name, CancellationToken cancellationToken)
    {
        if (!IsValidSlug(slug)) return CreateProjectOutcome.InvalidSlug;

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

    /// <summary>
    ///     Adds a repository. The credential arrives in plaintext once, here, and is stored protected;
    ///     nothing on this class reads it back in the clear.
    /// </summary>
    public async Task<(AddRepositoryOutcome Outcome, ProjectRepository? Repository)> AddRepositoryAsync(
        string projectSlug, string slug, string url, string? credential, CancellationToken cancellationToken)
    {
        if (await FindAsync(projectSlug, cancellationToken) is null) return (AddRepositoryOutcome.NoProject, null);

        if (!IsValidSlug(slug)) return (AddRepositoryOutcome.InvalidSlug, null);

        if (RepositoryUrl.Classify(url) == RepositoryUrlKind.Invalid) return (AddRepositoryOutcome.InvalidUrl, null);

        var repository = new ProjectRepository(projectSlug, slug, url.Trim(),
            string.IsNullOrEmpty(credential) ? null : _protector.Protect(credential));
        using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO repositories (project_slug, slug, url, credential)
                              VALUES ($project, $slug, $url, $credential) ON CONFLICT DO NOTHING
                              """;
        command.Parameters.Add(new DuckDBParameter("project", repository.ProjectSlug));
        command.Parameters.Add(new DuckDBParameter("slug", repository.Slug));
        command.Parameters.Add(new DuckDBParameter("url", repository.Url));
        command.Parameters.Add(new DuckDBParameter("credential",
            (object?)repository.ProtectedCredential ?? DBNull.Value));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? (AddRepositoryOutcome.Created, repository)
            : (AddRepositoryOutcome.SlugTaken, null);
    }

    public async Task<IReadOnlyList<ProjectRepository>> ListRepositoriesAsync(
        string projectSlug, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT slug, url, credential FROM repositories WHERE project_slug = $project ORDER BY slug";
        command.Parameters.Add(new DuckDBParameter("project", projectSlug));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var repositories = new List<ProjectRepository>();
        while (await reader.ReadAsync(cancellationToken))
            repositories.Add(new ProjectRepository(projectSlug, reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        return repositories;
    }

    private async Task<DuckDBConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new DuckDBConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
