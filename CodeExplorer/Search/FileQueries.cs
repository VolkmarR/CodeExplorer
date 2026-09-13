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
