using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.DataProtection;

namespace CodeExplorer;

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
    SlugTaken,

    /// <summary>The project was declared single-repository and already has its one (ADR-0006).</summary>
    ProjectIsFull
}

/// <summary>
///     Owns <c>control.duckdb</c>: projects, repositories and credentials (ADR-0004). It is a
///     plain file next to the project indexes and is never shadow-rebuilt. It is opened standalone, not
///     attached, so the "USE slug before every query" rule for project indexes does not apply here.
/// </summary>
public sealed partial class ControlDatabase : IDisposable
{
    public const string SlugRule =
        "Slug must be 1-64 lowercase letters, digits or hyphens, starting and ending with a letter or digit.";

    /// <summary>
    ///     The Data Protection purpose string for stored credentials. Ciphertext protected under one
    ///     purpose cannot be unprotected under another, so this constant is the only shared secret between
    ///     the writer here and the reader in <see cref="GitClones" />.
    /// </summary>
    public const string CredentialPurpose = "CodeExplorer.RepositoryCredential";

    /// <summary>
    ///     Where the control database's backup lives in the durable store. It is backed up as a file
    ///     and never exported to Parquet (ADR-0004): it is not shadow-rebuilt, it is small, and what is
    ///     in it — credentials above all — cannot be rebuilt from anything else if it is lost.
    /// </summary>
    private const string BackupName = "control/control.duckdb";

    // Backups are serialised so that two operator actions at once cannot land out of order and leave
    // the store holding the older of the two states. Each takes its own consistent snapshot inside the
    // engine, so the wait is on the upload and not on the work.
    private readonly SemaphoreSlim _backupGate = new(1, 1);

    private readonly string _connectionString;

    /// <summary>The file itself, which the backup snapshots beside and the restore writes.</summary>
    private readonly string _path;

    private readonly IDataProtector _protector;
    private readonly DurableStore _store;

    public ControlDatabase(IConfiguration configuration, IDataProtectionProvider dataProtection, DurableStore store,
        ILogger<ControlDatabase> logger)
    {
        _protector = dataProtection.CreateProtector(CredentialPurpose);
        _store = store;

        // Absent configuration selects a local folder, so `dotnet run` needs no settings at all.
        string directory = configuration["Storage:DataDirectory"] ?? "data";
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "control.duckdb");
        _connectionString = $"Data Source={_path}";

        // The container's disk is wiped on every stop, so an absent file is the ordinary state of a
        // replica waking up rather than a first run. Restoring before the CREATE TABLEs below is what
        // keeps the statements harmless: against a restored file they all find their tables already there.
        if (!File.Exists(_path) && _store.Fetch(BackupName, _path))
        {
            // The backup is a clean copy taken inside the engine, so anything named .wal beside this
            // path belongs to an earlier life of it and would replay a tail from a different file.
            File.Delete(_path + ".wal");
            logger.LogInformation("Restored the control database from its backup");
        }

        // Synchronous on purpose: this runs once at startup, before any request could cancel it.
        using var connection = new DuckDBConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        // Asked before the statements below run, because afterwards there is no telling whether they
        // did anything. A wake that migrates nothing must not upload the file again every time, and a
        // wake that does migrate must not leave the store holding the shape the older build wrote —
        // the next wake would restore that one and migrate it again, for as long as nobody happens to
        // create a project. A database with no projects table at all is not a migration but a first
        // run: it has nothing to lose, and its first operator write backs it up.
        command.CommandText = """
                              SELECT count(*) > 0
                                  AND count(*) FILTER (WHERE column_name = 'single_repository') = 0
                                  AS migrating
                              FROM duckdb_columns() WHERE table_name = 'projects'
                              """;
        bool migrating = command.ExecuteScalar() is true;

        command.CommandText = """
                              CREATE TABLE IF NOT EXISTS projects (slug VARCHAR PRIMARY KEY, name VARCHAR NOT NULL);
                              -- Added after the table existed, so it arrives as an ALTER rather than in the
                              -- CREATE above. DuckDB refuses a constraint on an added column ("Adding columns
                              -- with constraints not yet supported"), so it is nullable here and filled in
                              -- the next statement instead. False is the shape every project had before
                              -- ADR-0006, which is what an existing row must keep: the flag decides how its
                              -- files are named.
                              ALTER TABLE projects ADD COLUMN IF NOT EXISTS single_repository BOOLEAN;
                              UPDATE projects SET single_repository = false WHERE single_repository IS NULL;
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

        if (!migrating) return;

        // No gate: this is still the constructor, so nothing else can be holding this instance to back
        // it up at the same time. The connection above is reused rather than opened again, which the
        // snapshot allows because it is the source catalog it copies from.
        Snapshot(command);
        _store.Store(BackupName, SnapshotPath);
        Delete(SnapshotPath);
        logger.LogInformation("Migrated the control database and stored the migrated shape as its backup");
    }

    /// <summary>
    ///     A slug is a URL path segment agents keep in their configuration, so it is limited to what
    ///     survives every client's URL handling unescaped: lowercase ASCII letters, digits and hyphens,
    ///     at most 64 characters. The same rule applies to repository slugs, which head every
    ///     qualified path.
    /// </summary>
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex SlugPattern { get; }

    public void Dispose() => _backupGate.Dispose();

    public static bool IsValidSlug(string slug) => SlugPattern.IsMatch(slug);

    /// <summary>
    ///     <paramref name="singleRepository" /> is written once, here. There is deliberately no update
    ///     path for it (ADR-0006): it decides how every file in the project is named, and a name that
    ///     can change is one agents cannot hold.
    /// </summary>
    public async Task<CreateProjectOutcome> CreateAsync(string slug, string? name, bool singleRepository,
        CancellationToken cancellationToken)
    {
        if (!IsValidSlug(slug)) return CreateProjectOutcome.InvalidSlug;

        if (string.IsNullOrWhiteSpace(name)) return CreateProjectOutcome.MissingName;

        using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        // ON CONFLICT DO NOTHING keeps the existence check and the insert one statement, so two
        // concurrent creates cannot both succeed.
        command.CommandText = """
                              INSERT INTO projects (slug, name, single_repository)
                              VALUES ($slug, $name, $single) ON CONFLICT DO NOTHING
                              """;
        command.Parameters.Add(new DuckDBParameter("slug", slug));
        command.Parameters.Add(new DuckDBParameter("name", name.Trim()));
        command.Parameters.Add(new DuckDBParameter("single", singleRepository));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return CreateProjectOutcome.SlugTaken;

        await BackupAsync();
        return CreateProjectOutcome.Created;
    }

    /// <summary>
    ///     Every project, by slug. This is the only list of what exists: MCP has no discovery, so the
    ///     operator UI is where a project becomes visible at all.
    /// </summary>
    public async Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT slug, name, single_repository FROM projects ORDER BY slug";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var projects = new List<Project>();
        while (await reader.ReadAsync(cancellationToken))
            projects.Add(new Project(reader.Text("slug"), reader.Text("name"), reader.Flag("single_repository")));
        return projects;
    }

    public async Task<Project?> FindAsync(string slug, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, single_repository FROM projects WHERE slug = $slug";
        command.Parameters.Add(new DuckDBParameter("slug", slug));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new Project(slug, reader.Text("name"), reader.Flag("single_repository"))
            : null;
    }

    /// <summary>
    ///     Adds a repository. The credential arrives in plaintext once, here, and is stored protected;
    ///     nothing on this class reads it back in the clear.
    /// </summary>
    /// <param name="projectSlug">The project the repository is added to.</param>
    /// <param name="slug">
    ///     Ignored for a single-repository project, which heads no path with it and so is not asked for
    ///     one: the project's own slug is used, and it is unique in that project by construction.
    /// </param>
    /// <param name="url">The git remote, validated by <see cref="RepositoryUrl" />.</param>
    /// <param name="credential">Plaintext, accepted once and stored protected; never read back in the clear.</param>
    /// <param name="cancellationToken">Threaded through to the DuckDB command.</param>
    public async Task<(AddRepositoryOutcome Outcome, ProjectRepository? Repository)> AddRepositoryAsync(
        string projectSlug, string slug, string url, string? credential, CancellationToken cancellationToken)
    {
        if (await FindAsync(projectSlug, cancellationToken) is not { } project)
            return (AddRepositoryOutcome.NoProject, null);

        if (project.SingleRepository)
        {
            // The one repository is what the whole project is named after, so a second one would have
            // nothing to be called and nowhere to be named (ADR-0006).
            if ((await ListRepositoriesAsync(projectSlug, cancellationToken)).Count > 0)
                return (AddRepositoryOutcome.ProjectIsFull, null);
            slug = projectSlug;
        }

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
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return (AddRepositoryOutcome.SlugTaken, null);

        await BackupAsync();
        return (AddRepositoryOutcome.Created, repository);
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
            repositories.Add(new ProjectRepository(projectSlug, reader.Text("slug"), reader.Text("url"),
                reader.TextOrNull("credential")));
        return repositories;
    }

    /// <summary>
    ///     Forgets the project and every repository of it. What remains on disk — the clones and the
    ///     index file — is the caller's to remove; this class owns <c>control.duckdb</c> and nothing else.
    ///     False when there was no such project.
    /// </summary>
    public async Task<bool> DeleteProjectAsync(string slug, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.Transaction = (DuckDBTransaction)transaction;
        command.CommandText = "DELETE FROM repositories WHERE project_slug = $slug";
        command.Parameters.Add(new DuckDBParameter("slug", slug));
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "DELETE FROM projects WHERE slug = $slug";
        int deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (deleted != 1) return false;

        await BackupAsync();
        return true;
    }

    /// <summary>Forgets one repository of a project. False when the project or the repository is unknown.</summary>
    public async Task<bool> DeleteRepositoryAsync(string projectSlug, string slug,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM repositories WHERE project_slug = $project AND slug = $slug";
        command.Parameters.Add(new DuckDBParameter("project", projectSlug));
        command.Parameters.Add(new DuckDBParameter("slug", slug));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;

        await BackupAsync();
        return true;
    }

    /// <summary>
    ///     Backs the control database up to the durable store, as a file and not as Parquet (ADR-0004).
    ///     Called after every write, and from the constructor when a startup migrated the file, rather
    ///     than on a schedule: operator actions are rare, and a schedule on a server that scales to zero
    ///     has nothing running to fire it (#9).
    ///     The snapshot is taken by the engine rather than by copying the live file, because a copy
    ///     started while another connection is committing would store a torn database — and what makes
    ///     this worth doing at all is that it is the only copy of the credentials.
    ///     Not the caller's cancellation token: the write it follows is already committed, and a browser
    ///     closing a tab must not be what leaves the store holding the state before it.
    /// </summary>
    private async Task BackupAsync()
    {
        await _backupGate.WaitAsync(CancellationToken.None);
        try
        {
            using (var connection = await OpenAsync(CancellationToken.None))
            {
                using var command = connection.CreateCommand();
                Snapshot(command);
            }

            await _store.StoreAsync(BackupName, SnapshotPath, CancellationToken.None);
            Delete(SnapshotPath);
        }
        finally
        {
            _backupGate.Release();
        }
    }

    /// <summary>
    ///     Where the copy is taken before it is stored: beside the database, on the volume ADR-0003
    ///     budgets, and named so that the two are obviously the same thing.
    /// </summary>
    private string SnapshotPath => _path + ".backup";

    /// <summary>
    ///     Writes the consistent copy the store is given, on a command whose connection has the control
    ///     database as its default catalog. Synchronous because <c>ExecuteNonQuery</c> is what both
    ///     callers have: the constructor cannot await, and the write paths await the connection and not
    ///     this. <c>control</c> is the catalog DuckDB names after the file stem, which this class fixes
    ///     as <c>control.duckdb</c>; detaching is what checkpoints and closes the copy before it is read
    ///     off disk.
    /// </summary>
    private void Snapshot(DuckDBCommand command)
    {
        Delete(SnapshotPath);
        command.CommandText = $"""
                               ATTACH '{SnapshotPath.Replace("'", "''")}' AS backup;
                               COPY FROM DATABASE control TO backup;
                               DETACH backup;
                               """;
        command.ExecuteNonQuery();
    }

    /// <summary>A database file and the write-ahead log beside it, which outlives it after an unclean stop.</summary>
    private static void Delete(string path)
    {
        File.Delete(path);
        File.Delete(path + ".wal");
    }

    private async Task<DuckDBConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new DuckDBConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
