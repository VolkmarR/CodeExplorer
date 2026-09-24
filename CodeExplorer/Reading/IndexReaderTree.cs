using DuckDB.NET.Data;

namespace CodeExplorer.Reading;

/// <summary>
///     What an index lists rather than what it locates: the stored overview, the lines of a file, a
///     glob over the qualified paths, and the project as a tree. They are the half of
///     <see cref="IndexReader" /> that answers "what is in here", against the other half's "where is
///     this" — and they are the half that is all SQL, so they sit apart from the locating and the
///     advice rather than between them.
/// </summary>
public sealed partial class IndexReader
{
    /// <summary>
    ///     The overview the build stored with this index (#51): one row, no joins and no aggregates, so
    ///     a caller orienting itself pays a row read rather than five passes over <c>files</c> and the
    ///     commit tables.
    ///     Always there for an index a reader can be opened on: the build writes the row before
    ///     <c>index_info</c>, and a durable copy from a schema without it is rebuilt rather than
    ///     restored. A missing row is therefore an index this build cannot read, which is the same
    ///     infrastructure failure an unreadable document is and throws the same way — not a state the
    ///     callers branch on.
    /// </summary>
    public async Task<IndexOverview> OverviewAsync(CancellationToken cancellationToken)
    {
        await using var command = Connection.Query("SELECT document FROM project_overview LIMIT 1", []);
        await using var reader = await command.ReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw IndexOverview.Unreadable($"the index of project '{ProjectSlug}' holds no overview row");

        return IndexOverview.FromDocument(reader.Text("document"));
    }

    /// <summary>
    ///     The overview computed now, over the repository <see cref="ScopeToAsync" /> narrowed to, a
    ///     window of <paramref name="days" /> and without <paramref name="excluded" />: the overview
    ///     page's own view of this index (#216), which never reads the stored row. The sections are the
    ///     build's statements, so with nothing filtered the answer is the stored one; a few hundred
    ///     milliseconds on the largest index here, which an operator opening a page can afford and an
    ///     agent's first call should not be made to. The page's own cards ride along: the page draws them
    ///     and the stored row does not hold them.
    /// </summary>
    public async Task<(IndexOverview Overview, OverviewExcluded? Excluded, OverviewCards Cards)>
        LiveOverviewAsync(int days, ExcludedPaths excluded, CancellationToken cancellationToken)
    {
        var paths = await PathsAsync(cancellationToken);
        var scope = new OverviewScope(days, Repository?.Slug, excluded);
        var (overview, left) = await OverviewQueries.ComputeAsync(Connection, paths, scope, cancellationToken);
        return (overview, left,
            await OverviewQueries.CardsAsync(Connection, paths, scope, overview.Churn.Window(), cancellationToken));
    }

    /// <summary>
    ///     Lines <paramref name="first" /> to <paramref name="last" /> inclusive, in order; fewer when the file ends
    ///     first.
    /// </summary>
    public async Task<IReadOnlyList<string>> LinesAsync(long fileId, int first, int last,
        CancellationToken cancellationToken)
    {
        await using var command = Connection.Query("""
                                                   SELECT content FROM lines
                                                   WHERE file_id = $f AND line_number BETWEEN $a AND $b
                                                   ORDER BY line_number
                                                   """,
            [new DuckDBParameter("f", fileId), new DuckDBParameter("a", first), new DuckDBParameter("b", last)]);
        await using var reader = await command.ReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.Text("content"));
        return result;
    }

    /// <summary>
    ///     Case-insensitive <c>GLOB</c> over the qualified path, within <see cref="Repository" /> when
    ///     one was resolved. The window count rides along with the rows so one statement yields both
    ///     the total and the page. <paramref name="limit" /> is clamped to <see cref="MaxFiles" />.
    ///     <paramref name="skip" /> walks the same ordering: the sort is on the qualified path, which
    ///     is unique, so a row cannot sit on two pages or fall between them.
    /// </summary>
    public async Task<GlobResult> GlobAsync(string glob, int limit, int skip,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaxFiles);
        skip = Math.Max(skip, 0);
        var parameters = new List<DuckDBParameter> { new("g", glob.ToLowerInvariant()) };
        string scope = "";
        if (Repository is not null)
        {
            scope = " AND r.slug = $r";
            parameters.Add(new DuckDBParameter("r", Repository.Slug));
        }

        var files = new List<IndexedFile>();
        int total = 0;
        await using (var command = Connection.Query($"""
                                                     {_fileColumns}, count(*) OVER () AS total
                                                     {_fileSource}
                                                     WHERE lower(f.qualified_path) GLOB $g{scope}
                                                     ORDER BY f.qualified_path
                                                     LIMIT {limit} OFFSET {skip}
                                                     """, parameters))
        await using (var reader = await command.ReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                files.Add(ReadFile(reader));
                total = (int)reader.Int64("total");
            }
        }

        // The window count rides on the rows, so a page past the end carries none — and a total of
        // zero there would read as "nothing matched", which is the opposite of "you walked past the
        // last page". Counted separately only in that case, so the common answer stays one statement.
        if (files.Count == 0 && skip > 0)
            total = (int)await Connection.CountAsync($"""
                                                      SELECT count(*)
                                                      {_fileSource}
                                                      WHERE lower(f.qualified_path) GLOB $g{scope}
                                                      """, parameters, cancellationToken);

        int? elsewhere = null;
        if (total == 0 && Repository is not null)
            elsewhere = (int)await Connection.CountAsync(
                "SELECT count(*) FROM files f WHERE lower(f.qualified_path) GLOB $g",
                [new DuckDBParameter("g", glob.ToLowerInvariant())], cancellationToken);

        return new GlobResult(total, files, elsewhere);
    }

    /// <summary>
    ///     The project as a tree, <paramref name="depth" /> levels deep: the repositories at the root,
    ///     or the subdirectories and files of <paramref name="location" /> inside one of them. The list
    ///     reads as <c>tree</c> prints: a directory is followed by its own entries before its siblings,
    ///     directories before files at each level, each alphabetical. Directories are not rows in the
    ///     index — <c>files.directory</c> holds each file's whole repository-relative directory — so a
    ///     level is the distinct first segment below the prefix, aggregated over everything beneath it.
    ///     Children are formatted through the same <see cref="ProjectPaths" /> that parsed the level, so
    ///     a listing cannot be spelled differently from what it links to. Empty means the location is
    ///     not a directory: git has no empty directories, so neither has the index.
    /// </summary>
    /// <param name="location">
    ///     Null asks for the repositories themselves, which is the root of a multi-repository project.
    ///     A single-repository project has no such level, and <see cref="ProjectPaths.Parse" /> never
    ///     hands back null for one. The repository slug is used as given; the caller resolves it.
    /// </param>
    /// <param name="depth">How many levels to descend, at least 1. The web tree view asks for one.</param>
    /// <param name="cancellationToken">Threaded through to every DuckDB command.</param>
    public async Task<IReadOnlyList<TreeItem>> TreeAsync(QualifiedPath? location, int depth,
        CancellationToken cancellationToken)
    {
        if (location is not null) return await SubtreeAsync(location, depth, cancellationToken);

        // The repository level is not a directory level: its counts are the ones the build recorded,
        // so it is read on its own and each repository's own subtree hangs below it. Two statements per
        // repository and not two for the project, which is the shape #93 argued against — but a project
        // holds a handful of repositories where a subtree holds thousands of directories, and scoping
        // both statements to one slug is what keeps them readable.
        var entries = new List<TreeItem>();
        foreach (var repository in await RepositoryLevelAsync(cancellationToken))
        {
            entries.Add(repository);
            if (depth > 1)
                entries.AddRange(
                    await SubtreeAsync(new QualifiedPath(repository.Name, ""), depth - 1, cancellationToken));
        }

        return entries;
    }

    /// <summary>
    ///     Everything under one directory down to <paramref name="depth" />, in two statements whatever
    ///     the depth: the directories with their totals, then the files, assembled into <c>tree</c>
    ///     order here.
    ///     It was one query per directory visited until #93, on the grounds that a level costs well
    ///     under a millisecond and a listing is bounded by what an agent can read. The first half was
    ///     wrong on a real project — a level of Radix's <c>src</c> measures 7 ms, because a prefix
    ///     test over <c>directory</c> is a scan of <c>files</c> and nothing indexes it — and the
    ///     second half does not follow: the recursion visits every directory in the subtree, not every
    ///     directory listed. <c>list_tree radix/src 3</c> ran 6,822 queries over 338 million rows and
    ///     took 39.7 seconds, against 48 milliseconds for the two below.
    /// </summary>
    private async Task<IReadOnlyList<TreeItem>> SubtreeAsync(QualifiedPath location, int depth,
        CancellationToken cancellationToken)
    {
        var paths = await PathsAsync(cancellationToken);
        string repositorySlug = location.RepositorySlug;
        string directory = location.PathInRepository;

        // "" at a repository root, "src/" below one. Every directory under this level starts with it,
        // and the next segment begins where it ends. The length is taken in SQL rather than in C#
        // because a .NET string counts UTF-16 units and `substr` counts characters, which part ways on
        // any path outside the BMP.
        string prefix = directory.Length == 0 ? "" : directory + "/";
        var scope = new List<DuckDBParameter>
        {
            new("r", repositorySlug), new("p", prefix), new("d", directory)
        };

        // Children of each directory below the location, keyed by the directory they sit in, and the
        // files likewise. Both are filled in the order the statement returned, which is the order they
        // are emitted in: ordering by the whole path orders siblings by name, since they share a prefix.
        var directories = new Dictionary<string, List<(string Path, TreeItem Item)>>(StringComparer.Ordinal);
        var files = new Dictionary<string, List<TreeItem>>(StringComparer.Ordinal);

        // A file counts towards every ancestor within reach, so its path below the prefix is split once
        // and joined back at each of its first k segments. `depth` is a bound this code sets, never a
        // caller's text, so it is inlined; k is filtered rather than bounded per row because a
        // correlated range() measured three times slower, and an absurd depth costs nothing here —
        // every extra k is filtered out before the grouping (23 ms at depth 1000, 17 ms at depth 3).
        await using (var command = Connection.Query($"""
                                                     WITH below AS (
                                                         SELECT str_split(substr(f.directory, length($p) + 1), '/') AS segments,
                                                                f.line_count, f.size_bytes
                                                         FROM files f JOIN repositories r USING (repo_id)
                                                         -- starts_with and not LIKE: the prefix is a
                                                         -- directory NAME, and `_` is a LIKE wildcard, so
                                                         -- `src/my_module/` would take in `src/myXmodule/`
                                                         -- and count a sibling's files as this one's (#122).
                                                         WHERE r.slug = $r AND starts_with(f.directory, $p) AND f.directory <> $d)
                                                     SELECT array_to_string(list_slice(segments, 1, k), '/') AS directory,
                                                            CAST(count(*) AS BIGINT) AS files,
                                                            CAST(sum(line_count) AS BIGINT) AS lines,
                                                            CAST(sum(size_bytes) AS BIGINT) AS bytes
                                                     FROM below, range(1, {depth} + 1) AS t(k)
                                                     WHERE len(segments) >= k
                                                     GROUP BY directory
                                                     ORDER BY directory
                                                     """, scope))
        await using (var reader = await command.ReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                string below = reader.Text("directory");
                int cut = below.LastIndexOf('/');
                var item = new TreeItem(cut < 0 ? below : below[(cut + 1)..],
                    paths.Format(repositorySlug, prefix + below), (int)reader.Int64("files"),
                    reader.Int64("lines"), reader.Int64("bytes"), null);
                Under(directories, cut < 0 ? "" : below[..cut]).Add((below, item));
            }
        }

        // The files of every directory the listing reaches: the location's own, and those of the
        // directories above the last level, which are the ones the recursion used to descend into. At
        // depth 1 there are no such directories, and the second half is left out rather than written
        // as a test no row can pass: DuckDB cannot see that `<= 0` is unsatisfiable, so it would read
        // every file under the subtree — forty thousand of them on Radix — to discard all of them, on
        // the call the web tree view makes most.
        string deeper = depth > 1
            ? $"""

                  OR (starts_with(f.directory, $p) AND f.directory <> $d
                      AND len(str_split(substr(f.directory, length($p) + 1), '/')) <= {depth - 1})
              """
            : "";
        await using (var command = Connection.Query($"""
                                                     SELECT substr(f.directory, length($p) + 1) AS below,
                                                            f.name, f.qualified_path, f.line_count, f.size_bytes, f.skip_reason
                                                     FROM files f JOIN repositories r USING (repo_id)
                                                     WHERE r.slug = $r AND (f.directory = $d{deeper})
                                                     ORDER BY f.directory, f.name
                                                     """, scope))
        await using (var reader = await command.ReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                Under(files, reader.Text("below")).Add(new TreeItem(reader.Text("name"),
                    reader.Text("qualified_path"), null, reader.Int32("line_count"), reader.Int64("size_bytes"),
                    reader.TextOrNull("skip_reason")));
        }

        var entries = new List<TreeItem>();
        Emit("");
        return entries;

        // A directory, then everything under it, then its siblings; files after the directories they
        // sit beside. Which is what the recursive read did, now over what two statements returned.
        void Emit(string below)
        {
            if (directories.TryGetValue(below, out var children))
                foreach ((string path, var item) in children)
                {
                    entries.Add(item);
                    Emit(path);
                }

            if (files.TryGetValue(below, out var here)) entries.AddRange(here);
        }
    }

    /// <summary>The list a directory's children go in, made on first use.</summary>
    private static List<T> Under<T>(Dictionary<string, List<T>> byDirectory, string directory)
    {
        if (!byDirectory.TryGetValue(directory, out var entries)) byDirectory[directory] = entries = [];
        return entries;
    }

    /// <summary>
    ///     The root of a project: its repositories, which are what qualified paths begin with.
    ///     All three counts are the ones the build recorded on <c>repositories</c>, not a second count
    ///     over <c>files</c>: two definitions of "how many files are in this repository" would disagree
    ///     the moment a build skips something, and the tree would then contradict the project page.
    ///     The byte total was the exception and was summed here, which made the root of a tree listing
    ///     — the call an agent opens a project with — the one read that scanned <c>files</c> whole for
    ///     a number the build already had (#149). It is a column now, so this reads one small table.
    /// </summary>
    private async Task<IReadOnlyList<TreeItem>> RepositoryLevelAsync(CancellationToken cancellationToken)
    {
        await using var command = Connection.Query("""
                                                   SELECT r.slug,
                                                          CAST(r.file_count AS BIGINT) AS files,
                                                          CAST(r.line_count AS BIGINT) AS lines,
                                                          CAST(r.byte_count AS BIGINT) AS bytes
                                                   FROM repositories r
                                                   ORDER BY r.slug
                                                   """, []);
        await using var reader = await command.ReaderAsync(cancellationToken);
        var entries = new List<TreeItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            string slug = reader.Text("slug");
            entries.Add(new TreeItem(slug, slug, (int)reader.Int64("files"), reader.Int64("lines"),
                reader.Int64("bytes"), null));
        }

        return entries;
    }
}
