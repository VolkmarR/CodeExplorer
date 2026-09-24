using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CodeExplorer.Infrastructure;
using CodeExplorer.Reading;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.DataProtection;

namespace CodeExplorer.Control;

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
    ///     Where the control database's backup lives in the durable store. It is backed up as a file
    ///     and never exported to Parquet (ADR-0004): it is not shadow-rebuilt, it is small, and what is
    ///     in it — credentials above all — cannot be rebuilt from anything else if it is lost.
    /// </summary>
    private const string _backupName = "control/control.duckdb";

    // Backups are serialised so that two operator actions at once cannot land out of order and leave
    // the store holding the older of the two states. Each takes its own consistent snapshot inside the
    // engine, so the wait is on the upload and not on the work.
    private readonly SemaphoreSlim _backupGate = new(1, 1);

    // DuckDB.NET has no connection pool. What it has is one native instance per file, reference
    // counted, and closed with the last connection to it — so a call that opened and closed its own
    // connection opened the whole database each time: the file, the WAL replay, the thread pool and a
    // checkpoint on the way out, twice for a write and once more for its backup (#172). This
    // connection is never queried after the constructor; it only keeps the instance, which makes
    // every connection a call opens a cheap one onto it. ProjectIndexes holds its instance the same way.
    private readonly DuckDBConnection _anchor;

    // The projects <see cref="FindAsync" /> has resolved. Bounded by what exists, because only a
    // project that was found goes in, and emptied of a slug by each of the two writers that can end
    // its life. Concurrent because every request reads it and any request may be the one that fills it.
    private readonly ConcurrentDictionary<string, Project> _bySlug = new(StringComparer.Ordinal);

    private readonly string _connectionString;

    /// <summary>The file itself, which the backup snapshots beside and the restore writes.</summary>
    private readonly string _path;

    private readonly IDataProtector _protector;
    private readonly DurableStore _store;

    public ControlDatabase(IConfiguration configuration, IDataProtectionProvider dataProtection, DurableStore store,
        ILogger<ControlDatabase> logger)
    {
        _protector = dataProtection.CreateProtector(KeyRing.CredentialPurpose);
        _store = store;

        // Absent configuration selects a local folder, so `dotnet run` needs no settings at all.
        string directory = configuration["Storage:DataDirectory"] ?? "data";
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "control.duckdb");
        _connectionString = $"Data Source={_path}";

        // The container's disk is wiped on every stop, so an absent file is the ordinary state of a
        // replica waking up rather than a first run. Restoring before the CREATE TABLEs below is what
        // keeps the statements harmless: against a restored file they all find their tables already there.
        if (!File.Exists(_path) && _store.Fetch(_backupName, _path))
        {
            // The backup is a clean copy taken inside the engine, so anything named .wal beside this
            // path belongs to an earlier life of it and would replay a tail from a different file.
            File.Delete(_path + ".wal");
            logger.LogInformation("Restored the control database from its backup");
        }

        // Synchronous on purpose: this runs once at startup, before any request could cancel it.
        _anchor = new DuckDBConnection(_connectionString);
        _anchor.Open();
        try
        {
            Migrate(logger);
        }
        catch
        {
            // A constructor that throws leaves the container nothing to dispose, so the anchor would
            // hold the file open for the rest of the process.
            _anchor.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Brings the file to the shape this build reads, and stores that shape when it was not the
    ///     shape it restored.
    /// </summary>
    private void Migrate(ILogger logger)
    {
        using var command = _anchor.CreateCommand();
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
                              -- The overview page's excluded paths (#216), one row per pattern in the order
                              -- the operator wrote them. A table rather than a column on projects: a
                              -- project record is cached as never changing (FindAsync), and this is the
                              -- one thing about a project an operator edits. Arriving as a new table, it
                              -- needs no migration backup: a restored file without it has no patterns to lose.
                              CREATE TABLE IF NOT EXISTS excluded_paths (
                                  project_slug VARCHAR NOT NULL,
                                  position INTEGER NOT NULL,
                                  pattern VARCHAR NOT NULL,
                                  PRIMARY KEY (project_slug, position));
                              """;
        command.ExecuteNonQuery();

        if (!migrating) return;

        // No gate: this is still the constructor, so nothing else can be holding this instance to back
        // it up at the same time. The anchor is used rather than a connection of its own, which the
        // snapshot allows because it is the source catalog it copies from.
        Snapshot(command);
        _store.Store(_backupName, SnapshotPath);
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

    public void Dispose()
    {
        // Once the requests have finished with theirs, the anchor is the last connection, and closing
        // it is what checkpoints the file and releases it — so a restart over the same directory, a
        // test's or a replica's, finds it closed.
        _anchor.Dispose();
        _backupGate.Dispose();
    }

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

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
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

        // Nothing positive can be cached for a slug that was free a statement ago, so this is the
        // belt to the delete below: the rule is that every writer forgets, not that the one that
        // matters does.
        _bySlug.TryRemove(slug, out _);

        await BackupAsync();
        return CreateProjectOutcome.Created;
    }

    /// <summary>
    ///     Every project, by slug. This is the only list of what exists: MCP has no discovery, so the
    ///     operator UI is where a project becomes visible at all.
    /// </summary>
    public async Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT slug, name, single_repository FROM projects ORDER BY slug";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var projects = new List<Project>();
        while (await reader.ReadAsync(cancellationToken))
            projects.Add(new Project(reader.Text("slug"), reader.Text("name"), reader.Flag("single_repository")));
        return projects;
    }

    /// <summary>
    ///     The project a slug names, or null. Every request under a project route and every MCP call
    ///     asks this once, and it opened a control-database connection to do it (#149) — so a found
    ///     project is remembered.
    ///     What makes that safe is that a project record cannot change: <c>name</c> and
    ///     <c>single_repository</c> are written by <see cref="CreateAsync" /> and never updated,
    ///     deliberately so (ADR-0006). A slug therefore has exactly two states, and the writer that
    ///     ends one forgets it here.
    ///     Only a project that exists is remembered. A miss is the unknown-slug path, where a stranger
    ///     picks the key, and a cache a stranger fills is a cache with no bound — the cost of leaving
    ///     it out is one query on a request that is about to be answered with 404 anyway.
    /// </summary>
    public async Task<Project?> FindAsync(string slug, CancellationToken cancellationToken)
    {
        if (_bySlug.TryGetValue(slug, out var known)) return known;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, single_repository FROM projects WHERE slug = $slug";
        command.Parameters.Add(new DuckDBParameter("slug", slug));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var project = new Project(slug, reader.Text("name"), reader.Flag("single_repository"));
        _bySlug[slug] = project;
        return project;
    }

    /// <summary>
    ///     How many repositories each project has, for the operator's list. One query rather than one
    ///     per project: the list is a screen long, but it was a connection and a query per row on a
    ///     page an operator refreshes (#149). A project with none is absent from the result, which is
    ///     the caller's zero.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> CountRepositoriesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT project_slug, count(*) AS repositories FROM repositories GROUP BY project_slug";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
            counts[reader.Text("project_slug")] = (int)reader.Int64("repositories");
        return counts;
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
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
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
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT slug, url, credential FROM repositories WHERE project_slug = $project ORDER BY slug";
        command.Parameters.Add(new DuckDBParameter("project", projectSlug));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
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
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (DuckDBTransaction)transaction;
        command.CommandText = "DELETE FROM repositories WHERE project_slug = $slug";
        command.Parameters.Add(new DuckDBParameter("slug", slug));
        await command.ExecuteNonQueryAsync(cancellationToken);
        // Or a project created later under the same slug would open with this one's exclusions.
        command.CommandText = "DELETE FROM excluded_paths WHERE project_slug = $slug";
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "DELETE FROM projects WHERE slug = $slug";
        int deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        // Forgotten whether or not a row went: the next request then asks the database, which is the
        // only thing that knows.
        _bySlug.TryRemove(slug, out _);
        if (deleted != 1) return false;

        await BackupAsync();
        return true;
    }

    /// <summary>
    ///     The globs the project's overview page leaves out (#216), in the order they were written;
    ///     empty for a project nobody configured. Read per page load rather than cached, so a save
    ///     applies on the next one — it is one indexed read beside an overview that costs hundreds of
    ///     milliseconds.
    /// </summary>
    public async Task<IReadOnlyList<string>> ExcludedPathsAsync(string projectSlug,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pattern FROM excluded_paths WHERE project_slug = $project ORDER BY position";
        command.Parameters.Add(new DuckDBParameter("project", projectSlug));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var patterns = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) patterns.Add(reader.Text("pattern"));
        return patterns;
    }

    /// <summary>
    ///     Replaces the project's excluded paths with <paramref name="requested" />, normalised by
    ///     <see cref="ExcludedPaths.Normalize" />, and answers the list as stored — or, storing nothing,
    ///     the sentence saying which limit it broke. Replaced whole in one transaction, because the form
    ///     sends the whole list and two saves interleaving row by row would store neither.
    /// </summary>
    public async Task<(IReadOnlyList<string>? Saved, string? Problem)> SetExcludedPathsAsync(string projectSlug,
        IEnumerable<string>? requested, CancellationToken cancellationToken)
    {
        var (patterns, problem) = ExcludedPaths.Normalize(requested);
        if (patterns is null) return (null, problem);

        await using (var connection = await OpenAsync(cancellationToken))
        {
            // Compiled here by the engine that will run it, one pattern at a time so the refusal can
            // name the one at fault. A reversed range such as `[z-a]` is a class GLOB would accept
            // and RE2 refuses; stored, it would fail every overview load until someone removed it.
            foreach (string pattern in patterns)
                if (await RefusedPatternAsync(connection, pattern, cancellationToken) is { } refused)
                    return (null, $"'{pattern}' is not a pattern that can be matched: {refused}");

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (DuckDBTransaction)transaction;
            command.CommandText = "DELETE FROM excluded_paths WHERE project_slug = $project";
            command.Parameters.Add(new DuckDBParameter("project", projectSlug));
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = "INSERT INTO excluded_paths VALUES ($project, $position, $pattern)";
            for (int position = 0; position < patterns.Count; position++)
            {
                command.Parameters.Clear();
                command.Parameters.Add(new DuckDBParameter("project", projectSlug));
                command.Parameters.Add(new DuckDBParameter("position", position));
                command.Parameters.Add(new DuckDBParameter("pattern", patterns[position]));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await BackupAsync();
        return (patterns, null);
    }

    /// <summary>Why RE2 refuses a pattern's translation, or null where it compiles.</summary>
    private static async Task<string?> RefusedPatternAsync(DuckDBConnection connection, string pattern,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT regexp_matches('', $p, 'i')";
        command.Parameters.Add(new DuckDBParameter("p", new ExcludedPaths([pattern]).Expression));
        try
        {
            await command.ExecuteScalarAsync(cancellationToken);
            return null;
        }
        catch (DuckDBException exception)
        {
            // Swallowed because it is the answer: the engine's own message is what was wrong with the
            // pattern, and the caller refuses the save with it rather than storing the pattern.
            return exception.Message;
        }
    }

    /// <summary>Forgets one repository of a project. False when the project or the repository is unknown.</summary>
    public async Task<bool> DeleteRepositoryAsync(string projectSlug, string slug,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
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
            await using (var connection = await OpenAsync(CancellationToken.None))
            {
                await using var command = connection.CreateCommand();
                Snapshot(command);
            }

            await _store.StoreAsync(_backupName, SnapshotPath, CancellationToken.None);
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
        // A snapshot whose COPY threw never reached its DETACH, and the instance now lives for the
        // process (#172), so the catalog would still be attached: the ATTACH below would fail on the
        // name, and every backup after it with it. First, because the file cannot be deleted while
        // the instance holds it.
        command.CommandText = "DETACH DATABASE IF EXISTS backup";
        command.ExecuteNonQuery();
        Delete(SnapshotPath);
        command.CommandText = $"""
                               ATTACH {IndexQuery.Literal(SnapshotPath)} AS backup;
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

    /// <summary>
    ///     A connection of the call's own onto the instance the anchor holds, which costs a native
    ///     connect and nothing more. Not the anchor itself: one connection cannot run two requests'
    ///     statements at once, and requests arrive together.
    /// </summary>
    private async Task<DuckDBConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new DuckDBConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
