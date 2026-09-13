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

/// <summary>The <c>index_info</c> row: when the build completed and whether BM25 exists.</summary>
public sealed record IndexInfo(DateTimeOffset BuiltAt, bool FtsIndexed);

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
public sealed class FileQueries(DuckDBConnection connection) : IDisposable
{
    // The join is for the slug only; qualified_path already carries it as a prefix, but splitting a
    // string to recover what a column holds would be the worse choice.
    private const string FileColumns =
        "SELECT f.file_id, f.qualified_path, r.slug, f.line_count, f.size_bytes, f.skip_reason";

    private const string FileSource = "FROM files f JOIN repositories r USING (repo_id)";
    public void Dispose() => connection.Dispose();

    /// <summary>Null when the project has no index, which is an answer for the tool to phrase, not a failure.</summary>
    public static async Task<FileQueries?> OpenAsync(ProjectIndexes indexes, string slug,
        CancellationToken cancellationToken)
    {
        var connection = await indexes.OpenAsync(slug, cancellationToken);
        return connection is null ? null : new FileQueries(connection);
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
        using var command = Command("SELECT epoch(built_at), fts_indexed FROM index_info", []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new IndexInfo(DateTimeOffset.FromUnixTimeSeconds((long)reader.GetDouble(0)), reader.GetBoolean(1))
            : null;
    }

    public async Task<IReadOnlyList<IndexedRepository>> RepositoriesAsync(CancellationToken cancellationToken)
    {
        using var command = Command(
            "SELECT slug, url, head_commit, file_count, line_count FROM repositories ORDER BY repo_id", []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<IndexedRepository>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new IndexedRepository(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt64(4)));
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
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
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
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>
    ///     Case-insensitive <c>GLOB</c> over the qualified path, optionally within one repository. The
    ///     window count rides along with the rows so one statement yields both the total and the page.
    /// </summary>
    public async Task<GlobResult> GlobAsync(string glob, string? repositorySlug, int limit,
        CancellationToken cancellationToken, int offset = 0)
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
                                      LIMIT {limit} OFFSET {offset}
                                      """, parameters))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                files.Add(ReadFile(reader));
                total = (int)reader.GetInt64(6);
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
    ///     subdirectories and files of <paramref name="path" /> inside one of them. Directories are
    ///     not rows in the index — <c>files.directory</c> holds each file's whole repository-relative
    ///     directory — so a level is the distinct first segment below the prefix, aggregated over
    ///     everything beneath it. Directories come first, each alphabetical, as a tree reads.
    /// </summary>
    public async Task<IReadOnlyList<TreeItem>> TreeAsync(string path, CancellationToken cancellationToken)
    {
        // Parsed here rather than taken apart by the caller: `QualifiedPath` is internal to the host and
        // this class is public, and a level is addressed by the same string a file is.
        if (QualifiedPath.Parse(path) is not { } location) return await RepositoryLevelAsync(cancellationToken);

        // "" at a repository root, "src/" below one. Every directory under this level starts with it,
        // and the next segment begins where it ends. The length is taken in SQL rather than in C#
        // because a .NET string counts UTF-16 units and `substr` counts characters, which part ways on
        // any path outside the BMP.
        string directory = location.PathInRepository;
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
                       new DuckDBParameter("r", location.RepositorySlug), new DuckDBParameter("p", prefix),
                       new DuckDBParameter("d", directory)
                   ]))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                string segment = reader.GetString(0);
                entries.Add(new TreeItem(segment,
                    $"{location.RepositorySlug}/{prefix}{segment}",
                    (int)reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), null));
            }
        }

        using (var command = Command("""
                                     SELECT f.name, f.qualified_path, f.line_count, f.size_bytes, f.skip_reason
                                     FROM files f JOIN repositories r USING (repo_id)
                                     WHERE r.slug = $r AND f.directory = $d
                                     ORDER BY f.name
                                     """,
                   [new DuckDBParameter("r", location.RepositorySlug), new DuckDBParameter("d", directory)]))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                entries.Add(new TreeItem(reader.GetString(0), reader.GetString(1), null,
                    reader.GetInt32(2), reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return entries;
    }

    /// <summary>
    ///     The root of a project: its repositories, which are what qualified paths begin with. A
    ///     repository indexed from an empty tree still belongs here, hence the left join and the
    ///     coalesce — it is a repository with no files, not an absent one.
    /// </summary>
    private async Task<IReadOnlyList<TreeItem>> RepositoryLevelAsync(CancellationToken cancellationToken)
    {
        using var command = Command("""
                                    SELECT r.slug,
                                           CAST(count(f.file_id) AS BIGINT) AS files,
                                           CAST(coalesce(sum(f.line_count), 0) AS BIGINT) AS lines,
                                           CAST(coalesce(sum(f.size_bytes), 0) AS BIGINT) AS bytes
                                    FROM repositories r LEFT JOIN files f USING (repo_id)
                                    GROUP BY r.slug
                                    ORDER BY r.slug
                                    """, []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<TreeItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            string slug = reader.GetString(0);
            entries.Add(new TreeItem(slug, slug, (int)reader.GetInt64(1), reader.GetInt64(2),
                reader.GetInt64(3), null));
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
                                     SELECT f.extension, count(*), sum(f.line_count)::BIGINT, count(f.skip_reason)
                                     FROM files f JOIN repositories r USING (repo_id){scope}
                                     GROUP BY f.extension
                                     ORDER BY count(*) DESC, f.extension
                                     """, parameters);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ExtensionCount>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ExtensionCount(reader.GetString(0), (int)reader.GetInt64(1),
                // sum() yields a HUGEINT, which the reader surfaces as BigInteger; the cast above keeps it a long.
                reader.GetInt64(2), (int)reader.GetInt64(3)));
        return result;
    }

    private static IndexedFile ReadFile(DbDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt64(4),
        reader.IsDBNull(5) ? null : reader.GetString(5));

    private DuckDBCommand Command(string sql, IEnumerable<DuckDBParameter> parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.Add(parameter);
        return command;
    }
}
