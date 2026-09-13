using System.Data.Common;
using System.Globalization;
using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     A row of <c>files</c>. <see cref="SkipReason" /> is set when the file is committed but has no lines in the
///     index.
/// </summary>
public sealed record IndexedFile(
    long FileId,
    string QualifiedPath,
    string RepositorySlug,
    int LineCount,
    long SizeBytes,
    string? SkipReason);

/// <summary>A row of <c>repositories</c>: what the last build read and where it stood.</summary>
public sealed record IndexedRepository(string Slug, string Url, string HeadCommit, int FileCount, long LineCount);

/// <summary>
///     The <c>index_info</c> row: when the build completed, whether BM25 exists, and how the build
///     named its files. <see cref="SingleRepository" /> comes from here rather than from the control
///     database so that a read path can parse a qualified path without leaving the index (ADR-0005,
///     ADR-0006).
/// </summary>
public sealed record IndexInfo(DateTimeOffset BuiltAt, bool FtsIndexed, bool SingleRepository);

/// <summary><see cref="Skipped" /> of the <see cref="Files" /> have no lines; <see cref="Lines" /> covers the rest.</summary>
public sealed record ExtensionCount(string Extension, int Files, long Lines, int Skipped);

/// <summary>
///     One row of a directory listing. A directory carries what lies beneath it — <see cref="Files" />
///     counts every file at any depth, not just its immediate children — and a file carries its own
///     size, with <see cref="Files" /> null to tell the two apart. <see cref="QualifiedPath" /> is what
///     the next listing is asked for, or what the file view opens.
/// </summary>
public sealed record TreeItem(
    string Name,
    string QualifiedPath,
    int? Files,
    long Lines,
    long SizeBytes,
    string? SkipReason);

/// <summary>
///     <see cref="Total" /> counts every match, <see cref="Files" /> the first <c>limit</c> of them.
///     <see cref="MatchesInOtherRepositories" /> is filled only when a repository-scoped glob matched
///     nothing, so a scoped miss is told apart from a pattern that matches nowhere.
/// </summary>
public sealed record GlobResult(int Total, IReadOnlyList<IndexedFile> Files, int? MatchesInOtherRepositories);

/// <summary>
///     Read-only questions about a project's <c>files</c>, <c>lines</c> and <c>repositories</c> that
///     are not text searches: what exists, what a file says, how big things are. One instance is one
///     connection bound to the project for one tool call (ADR-0003); callers dispose it and never keep
///     it. Glob matching is the SQL <c>GLOB</c> operator (ADR-0004), so <c>*</c> crosses <c>/</c> and
///     there is no brace expansion.
/// </summary>
public sealed class FileQueries(IndexLease lease) : IDisposable
{
    // The join is for the slug only; qualified_path already carries it as a prefix, but splitting a
    // string to recover what a column holds would be the worse choice.
    private const string FileColumns =
        "SELECT f.file_id, f.qualified_path, r.slug, f.line_count, f.size_bytes, f.skip_reason";

    private const string FileSource = "FROM files f JOIN repositories r USING (repo_id)";
    /// <summary>Releases the lease as well as the connection, which is what lets a swap proceed.</summary>
    public void Dispose() => lease.Dispose();

    /// <summary>Null when the project has no index, which is an answer for the tool to phrase, not a failure.</summary>
    public static async Task<FileQueries?> OpenAsync(ProjectIndexes indexes, string slug,
        CancellationToken cancellationToken)
    {
        var lease = await indexes.OpenAsync(slug, cancellationToken);
        return lease is null ? null : new FileQueries(lease);
    }

    /// <summary>
    ///     The same against an index already on disk, without restoring one that is not: what a read
    ///     spanning every project uses, so that opening the operator's project list does not wake every
    ///     durable copy at once (#9).
    /// </summary>
    public static async Task<FileQueries?> PeekAsync(ProjectIndexes indexes, string slug,
        CancellationToken cancellationToken)
    {
        var lease = await indexes.PeekAsync(slug, cancellationToken);
        return lease is null ? null : new FileQueries(lease);
    }

    /// <summary>
    ///     Null when the file exists but the build that was filling it never finished — the row is
    ///     written last, so its absence is what an interrupted build leaves behind. That is a state a
    ///     caller can catch the server in, and the project list has to show every other project even
    ///     when one is in it.
    /// </summary>
    public async Task<IndexInfo?> InfoAsync(CancellationToken cancellationToken)
    {
        // epoch() hands back seconds as a double, which is the one representation of a TIMESTAMPTZ that
        // does not depend on whether the ICU extension is loaded to decide the session time zone.
        using var command = Command(
            "SELECT epoch(built_at) AS built_seconds, fts_indexed, single_repository FROM index_info", []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new IndexInfo(DateTimeOffset.FromUnixTimeSeconds((long)reader.Double("built_seconds")),
                reader.Flag("fts_indexed"), reader.Flag("single_repository"))
            : null;
    }

    /// <summary>
    ///     How this index named its files, read from the index itself rather than from the control
    ///     database: <c>Search/</c> answers from the index alone (ADR-0005), and an index names things
    ///     the way the build that wrote it was told to, which is the only shape its rows can be read in.
    ///     One statement, because a path cannot be parsed until both halves are known.
    /// </summary>
    /// <param name="projectSlug">Only used to anchor the shape when the index holds no repositories yet.</param>
    /// <param name="cancellationToken">Threaded through to the DuckDB command.</param>
    public async Task<ProjectPaths> PathsAsync(string projectSlug, CancellationToken cancellationToken)
    {
        using var command = Command("""
                                    SELECT (SELECT single_repository FROM index_info) AS single_repository,
                                           (SELECT slug FROM repositories ORDER BY repo_id LIMIT 1) AS slug
                                    """, []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new ProjectPaths(false, projectSlug);

        // Both subqueries answer null on an index still being filled: no index_info row until the build
        // completes, and no repositories until the first one is ingested.
        return new ProjectPaths(reader.FlagOrFalse("single_repository"), reader.TextOrNull("slug") ?? projectSlug);
    }

    public async Task<IReadOnlyList<IndexedRepository>> RepositoriesAsync(CancellationToken cancellationToken)
    {
        using var command = Command(
            "SELECT slug, url, head_commit, file_count, line_count FROM repositories ORDER BY repo_id", []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<IndexedRepository>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new IndexedRepository(reader.Text("slug"), reader.Text("url"), reader.Text("head_commit"),
                reader.Int32("file_count"), reader.Int64("line_count")));
        return result;
    }

    /// <summary>
    ///     Compared lower-cased on both sides: git paths are case-sensitive, but an agent quoting a path
    ///     from memory gets the case wrong far more often than a repository holds two files differing
    ///     only by case. An exact match wins if both exist.
    /// </summary>
    public async Task<IndexedFile?> FindFileAsync(string qualifiedPath, CancellationToken cancellationToken)
    {
        using var command = Command($"""
                                     {FileColumns}
                                     {FileSource}
                                     WHERE lower(f.qualified_path) = lower($p)
                                     ORDER BY f.qualified_path = $p DESC
                                     LIMIT 1
                                     """, [new DuckDBParameter("p", qualifiedPath)]);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFile(reader) : null;
    }

    /// <summary>Files anywhere in the project with this leaf name, for a "did you mean" after a miss.</summary>
    public async Task<IReadOnlyList<string>> FilesNamedAsync(string name, int limit,
        CancellationToken cancellationToken)
    {
        using var command = Command(
            $"SELECT qualified_path FROM files WHERE lower(name) = lower($n) ORDER BY qualified_path LIMIT {limit}",
            [new DuckDBParameter("n", name)]);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.Text("qualified_path"));
        return result;
    }

    /// <summary>
    ///     Lines <paramref name="first" /> to <paramref name="last" /> inclusive, in order; fewer when the file ends
    ///     first.
    /// </summary>
    public async Task<IReadOnlyList<string>> LinesAsync(long fileId, int first, int last,
        CancellationToken cancellationToken)
    {
        using var command = Command("""
                                    SELECT content FROM lines
                                    WHERE file_id = $f AND line_number BETWEEN $a AND $b
                                    ORDER BY line_number
                                    """,
            [new DuckDBParameter("f", fileId), new DuckDBParameter("a", first), new DuckDBParameter("b", last)]);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.Text("content"));
        return result;
    }

    /// <summary>
    ///     Case-insensitive <c>GLOB</c> over the qualified path, optionally within one repository. The
    ///     window count rides along with the rows so one statement yields both the total and the page.
    /// </summary>
    public async Task<GlobResult> GlobAsync(string glob, string? repositorySlug, int limit,
        CancellationToken cancellationToken)
    {
        var parameters = new List<DuckDBParameter> { new("g", glob.ToLowerInvariant()) };
        string scope = "";
        if (repositorySlug is not null)
        {
            scope = " AND r.slug = $r";
            parameters.Add(new DuckDBParameter("r", repositorySlug));
        }

        var files = new List<IndexedFile>();
        int total = 0;
        using (var command = Command($"""
                                      {FileColumns}, count(*) OVER () AS total
                                      {FileSource}
                                      WHERE lower(f.qualified_path) GLOB $g{scope}
                                      ORDER BY f.qualified_path
                                      LIMIT {limit}
                                      """, parameters))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                files.Add(ReadFile(reader));
                total = (int)reader.Int64("total");
            }
        }

        int? elsewhere = null;
        if (total == 0 && repositorySlug is not null)
        {
            using var command = Command("SELECT count(*) FROM files f WHERE lower(f.qualified_path) GLOB $g",
                [new DuckDBParameter("g", glob.ToLowerInvariant())]);
            elsewhere = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        return new GlobResult(total, files, elsewhere);
    }

    /// <summary>
    ///     One level of the project as a tree: the repositories at the root, or the immediate
    ///     subdirectories and files of <paramref name="location" /> inside one of them. Directories are
    ///     not rows in the index — <c>files.directory</c> holds each file's whole repository-relative
    ///     directory — so a level is the distinct first segment below the prefix, aggregated over
    ///     everything beneath it. Directories come first, each alphabetical, as a tree reads.
    /// </summary>
    /// <param name="paths">
    ///     The project's shape, which names the entries: children are formatted through the same thing
    ///     that parsed the level, so a listing cannot be spelled differently from what it links to.
    /// </param>
    /// <param name="location">
    ///     Null asks for the repositories themselves, which is the root of a multi-repository project.
    ///     A single-repository project has no such level, and <see cref="ProjectPaths.Parse" /> never
    ///     hands back null for one.
    /// </param>
    /// <param name="cancellationToken">Threaded through to both DuckDB commands.</param>
    public async Task<IReadOnlyList<TreeItem>> TreeAsync(ProjectPaths paths, QualifiedPath? location,
        CancellationToken cancellationToken)
    {
        if (location is null) return await RepositoryLevelAsync(cancellationToken);

        string repositorySlug = location.RepositorySlug;
        string directory = location.PathInRepository;

        // "" at a repository root, "src/" below one. Every directory under this level starts with it,
        // and the next segment begins where it ends. The length is taken in SQL rather than in C#
        // because a .NET string counts UTF-16 units and `substr` counts characters, which part ways on
        // any path outside the BMP.
        string prefix = directory.Length == 0 ? "" : directory + "/";
        var entries = new List<TreeItem>();

        using (var command = Command("""
                                     SELECT split_part(substr(f.directory, length($p) + 1), '/', 1) AS segment,
                                            CAST(count(*) AS BIGINT) AS files,
                                            CAST(sum(f.line_count) AS BIGINT) AS lines,
                                            CAST(sum(f.size_bytes) AS BIGINT) AS bytes
                                     FROM files f JOIN repositories r USING (repo_id)
                                     WHERE r.slug = $r AND f.directory LIKE $p || '%' AND f.directory <> $d
                                     GROUP BY segment
                                     ORDER BY segment
                                     """,
                   [
                       new DuckDBParameter("r", repositorySlug), new DuckDBParameter("p", prefix),
                       new DuckDBParameter("d", directory)
                   ]))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                string segment = reader.Text("segment");
                entries.Add(new TreeItem(segment, paths.Format(repositorySlug, prefix + segment),
                    (int)reader.Int64("files"), reader.Int64("lines"), reader.Int64("bytes"), null));
            }
        }

        using (var command = Command("""
                                     SELECT f.name, f.qualified_path, f.line_count, f.size_bytes, f.skip_reason
                                     FROM files f JOIN repositories r USING (repo_id)
                                     WHERE r.slug = $r AND f.directory = $d
                                     ORDER BY f.name
                                     """,
                   [new DuckDBParameter("r", repositorySlug), new DuckDBParameter("d", directory)]))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                entries.Add(new TreeItem(reader.Text("name"), reader.Text("qualified_path"), null,
                    reader.Int32("line_count"), reader.Int64("size_bytes"), reader.TextOrNull("skip_reason")));
        }

        return entries;
    }

    /// <summary>
    ///     The root of a project: its repositories, which are what qualified paths begin with. The file
    ///     and line counts are the ones the build recorded on <c>repositories</c>, not a second count
    ///     over <c>files</c>: two definitions of "how many files are in this repository" would disagree
    ///     the moment a build skips something, and the tree would then contradict the project page. Only
    ///     the byte total has to be summed. A repository indexed from an empty tree still belongs here,
    ///     hence the left join and the coalesce — it is a repository with no files, not an absent one.
    /// </summary>
    private async Task<IReadOnlyList<TreeItem>> RepositoryLevelAsync(CancellationToken cancellationToken)
    {
        using var command = Command("""
                                    SELECT r.slug,
                                           CAST(r.file_count AS BIGINT) AS files,
                                           CAST(r.line_count AS BIGINT) AS lines,
                                           CAST(coalesce(sum(f.size_bytes), 0) AS BIGINT) AS bytes
                                    FROM repositories r LEFT JOIN files f USING (repo_id)
                                    GROUP BY r.slug, r.file_count, r.line_count
                                    ORDER BY r.slug
                                    """, []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<TreeItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            string slug = reader.Text("slug");
            entries.Add(new TreeItem(slug, slug, (int)reader.Int64("files"), reader.Int64("lines"),
                reader.Int64("bytes"), null));
        }

        return entries;
    }

    public async Task<IReadOnlyList<ExtensionCount>> ExtensionsAsync(string? repositorySlug,
        CancellationToken cancellationToken)
    {
        var parameters = new List<DuckDBParameter>();
        string scope = "";
        if (repositorySlug is not null)
        {
            scope = " WHERE r.slug = $r";
            parameters.Add(new DuckDBParameter("r", repositorySlug));
        }

        using var command = Command($"""
                                     SELECT f.extension,
                                            count(*) AS files,
                                            sum(f.line_count)::BIGINT AS lines,
                                            count(f.skip_reason) AS skipped
                                     FROM files f JOIN repositories r USING (repo_id){scope}
                                     GROUP BY f.extension
                                     ORDER BY count(*) DESC, f.extension
                                     """, parameters);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ExtensionCount>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ExtensionCount(reader.Text("extension"), (int)reader.Int64("files"),
                // sum() yields a HUGEINT, which the reader surfaces as BigInteger; the cast above keeps it a long.
                reader.Int64("lines"), (int)reader.Int64("skipped")));
        return result;
    }

    /// <summary>
    ///     Reads the columns <see cref="FileColumns" /> selects, by name: the glob query appends a
    ///     window count to that list, so a positional read here would break the moment another caller
    ///     prepends anything to its own projection.
    /// </summary>
    private static IndexedFile ReadFile(DbDataReader reader) => new(
        reader.Int64("file_id"), reader.Text("qualified_path"), reader.Text("slug"),
        reader.Int32("line_count"), reader.Int64("size_bytes"), reader.TextOrNull("skip_reason"));

    private DuckDBCommand Command(string sql, IEnumerable<DuckDBParameter> parameters)
    {
        var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.Add(parameter);
        return command;
    }
}
