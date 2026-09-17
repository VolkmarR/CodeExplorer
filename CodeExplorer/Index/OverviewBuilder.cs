using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     Computes a project's overview and stores it with the index that produced it (<c>#51</c>).
///     It runs inside the build, after the files and the history are in the shadow, because every
///     section is an aggregate over those tables: the counts come from <c>files</c>, the ranking from
///     the commit tables. Computing it here rather than per call is the same reasoning
///     <c>files.qualified_path</c> and the per-line attribution follow — the read path stays free of
///     joins, and a caller orienting itself pays a single row read rather than five aggregates over
///     the largest tables in the index.
///     The churn section is <see cref="HistoryQueries" />'s ranking, called rather than reimplemented, so
///     the overview page and <c>hot_files</c> cannot come to disagree about what a project is busy with.
/// </summary>
public sealed class OverviewBuilder
{
    /// <summary>
    ///     Languages listed before the tail is summarised. Long enough to hold every language a real
    ///     project is written in plus its configuration and documentation extensions, short enough that
    ///     one repository of scattered one-off extensions does not become the whole answer.
    /// </summary>
    private const int LanguagesShown = 20;

    /// <summary>
    ///     Top-level entries kept per repository. A repository root is a screenful in every codebase
    ///     anyone would want an overview of; the cap is there so that a generated tree of thousands of
    ///     root files cannot make this row the largest thing in the index, and what it drops is counted
    ///     rather than hidden.
    /// </summary>
    private const int TreeEntriesShown = 100;

    /// <summary>
    ///     Enough to warn a caller off the files that would swamp a read, and not a size ranking of the
    ///     project: the tail of one is every file, in order.
    /// </summary>
    private const int LargestFilesShown = 10;

    /// <summary>A screenful of a ranking read from the top down, the same judgement <c>hot_files</c> makes.</summary>
    private const int ChurnFilesShown = 10;

    /// <summary>
    ///     Authors named. Past ten it stops being "who knows this code" and becomes a contributor list,
    ///     which is a question about a repository rather than about the code in it.
    /// </summary>
    private const int AuthorsShown = 10;

    /// <summary>
    ///     Fills the overview row of a shadow index. Nothing is reported to the refresh status: every
    ///     statement below is one aggregate over tables already on this connection, so the step is
    ///     shorter than the interval the status is polled at and a sixth step would only ever be seen
    ///     as a flicker.
    /// </summary>
    /// <param name="shadow">The shadow being built, its connection already bound to it.</param>
    /// <param name="singleRepository">How this project names its files (ADR-0006), for the paths in the row.</param>
    /// <param name="cancellationToken">Threaded through every statement.</param>
    public async Task<IndexOverview> FillAsync(ShadowIndex shadow, bool singleRepository,
        CancellationToken cancellationToken)
    {
        var connection = shadow.Connection;
        // Anchored to the first repository of the build, which is the one a single-repository project
        // names its files after; a project with no repositories at all has no paths to format anyway.
        string anchor = await AnchorSlugAsync(connection, shadow.Slug, cancellationToken);
        var paths = new ProjectPaths(singleRepository, anchor);

        var (languages, others) = await LanguagesAsync(connection, cancellationToken);
        var (tree, otherEntries) = await TreeAsync(connection, paths, cancellationToken);
        var overview = new IndexOverview(
            languages, others,
            tree, otherEntries,
            await LargestFilesAsync(connection, cancellationToken),
            await ChurnAsync(connection, paths, cancellationToken),
            await AuthorsAsync(connection, cancellationToken));

        using var insert = connection.Query("INSERT INTO project_overview VALUES ($document)",
            [new DuckDBParameter("document", overview.ToDocument())]);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return overview;
    }

    /// <summary>
    ///     The repository a single-repository project names its files after. It is the first by
    ///     <c>repo_id</c>, which is the order the build numbered them in, so it is the same repository
    ///     <c>ProjectPaths.For</c> picks on the read side. The project slug stands in for a project with
    ///     no repositories, which matches nothing and keeps every path readable.
    /// </summary>
    private static async Task<string> AnchorSlugAsync(DuckDBConnection connection, string slug,
        CancellationToken cancellationToken)
    {
        using var command = connection.Query("SELECT slug FROM repositories ORDER BY repo_id LIMIT 1", []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? reader.Text("slug") : slug;
    }

    /// <summary>
    ///     Counts by language, with every extension no profile covers standing for itself. The grouping
    ///     is done here and not in SQL because the extension-to-language table is
    ///     <see cref="Languages" />'s and a <c>CASE</c> in this statement would be a second copy of it.
    /// </summary>
    private static async Task<(IReadOnlyList<LanguageShare> Shown, int Others)> LanguagesAsync(
        DuckDBConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.Query("""
                                             SELECT extension,
                                                    count(*)::INTEGER AS files,
                                                    sum(line_count)::BIGINT AS lines,
                                                    count(skip_reason)::INTEGER AS skipped
                                             FROM files
                                             GROUP BY extension
                                             """, []);
        var byName = new Dictionary<string, LanguageShare>(StringComparer.Ordinal);
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var (name, mapped) = Languages.Name(reader.Text("extension"));
                int files = reader.Int32("files");
                long lines = reader.Int64("lines");
                int skipped = reader.Int32("skipped");
                // Several extensions fold into one language, so the row is accumulated rather than
                // added: X# counts its headers with its sources.
                byName[name] = byName.TryGetValue(name, out var running)
                    ? running with
                    {
                        Files = running.Files + files, Lines = running.Lines + lines,
                        Skipped = running.Skipped + skipped
                    }
                    : new LanguageShare(name, mapped, files, lines, skipped);
            }
        }

        var ordered = byName.Values
            .OrderByDescending(share => share.Lines)
            .ThenByDescending(share => share.Files)
            .ThenBy(share => share.Name, StringComparer.Ordinal)
            .ToList();
        return (ordered.Take(LanguagesShown).ToList(), Math.Max(0, ordered.Count - LanguagesShown));
    }

    /// <summary>
    ///     The top level of every repository: the directories and files a qualified path can begin with,
    ///     each carrying everything beneath it. One statement for the whole project rather than one per
    ///     repository, because an overview is drawn once and the grouping is the same at every root.
    /// </summary>
    private static async Task<(IReadOnlyList<OverviewEntry> Shown, int Others)> TreeAsync(
        DuckDBConnection connection, ProjectPaths paths, CancellationToken cancellationToken)
    {
        using var command = connection.Query("""
                                             WITH tops AS (
                                                 SELECT r.slug AS repo_slug,
                                                        split_part(f.path, '/', 1) AS segment,
                                                        f.path, f.line_count, f.size_bytes
                                                 FROM files f JOIN repositories r USING (repo_id))
                                             SELECT repo_slug, segment,
                                                    -- A file directly at the root is its own first
                                                    -- segment; anything else is a directory, and git
                                                    -- cannot hold both names at one level.
                                                    bool_and(path = segment) AS is_file,
                                                    count(*)::INTEGER AS files,
                                                    sum(line_count)::BIGINT AS lines,
                                                    sum(size_bytes)::BIGINT AS bytes
                                             FROM tops
                                             GROUP BY repo_slug, segment
                                             ORDER BY repo_slug, is_file, segment
                                             """, []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<OverviewEntry>();
        // Counted per repository and not across the project: one repository of a thousand root files
        // would otherwise spend the whole cap and drop every later repository's top level entirely,
        // which is the one thing this section exists to show.
        var kept = new Dictionary<string, int>(StringComparer.Ordinal);
        int others = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            string slug = reader.Text("repo_slug");
            int taken = kept.GetValueOrDefault(slug);
            if (taken == TreeEntriesShown)
            {
                others++;
                continue;
            }

            kept[slug] = taken + 1;
            entries.Add(new OverviewEntry(paths.Format(slug, reader.Text("segment")), slug,
                !reader.Flag("is_file"), reader.Int32("files"), reader.Int64("lines"), reader.Int64("bytes")));
        }

        return (entries, others);
    }

    /// <summary>
    ///     The largest files by bytes rather than by lines, because the point is what a read costs and a
    ///     file the build skipped for its size has no lines at all — those are exactly the ones a caller
    ///     most needs warning about.
    /// </summary>
    private static async Task<IReadOnlyList<OverviewFile>> LargestFilesAsync(DuckDBConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.Query($"""
                                              SELECT f.qualified_path, r.slug, f.line_count, f.size_bytes
                                              FROM files f JOIN repositories r USING (repo_id)
                                              ORDER BY f.size_bytes DESC, f.qualified_path
                                              LIMIT {LargestFilesShown}
                                              """, []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var files = new List<OverviewFile>();
        while (await reader.ReadAsync(cancellationToken))
            files.Add(new OverviewFile(reader.Text("qualified_path"), reader.Text("slug"),
                reader.Int32("line_count"), reader.Int64("size_bytes")));
        return files;
    }

    /// <summary>
    ///     The churn ranking over the default window, or the explicit "no history was imported" answer.
    ///     The window is the ranking's own: anchored to the newest commit in the index and not to the
    ///     clock, which for an overview matters twice over, since the row outlives the build that wrote
    ///     it and is read from a replica that may be days behind (CONTEXT.md, Window).
    /// </summary>
    private static async Task<OverviewChurn> ChurnAsync(DuckDBConnection connection, ProjectPaths paths,
        CancellationToken cancellationToken)
    {
        var window = await HistoryQueries.WindowAsync(connection, HistoryWindow.DefaultDays, null, cancellationToken);
        if (window is null) return OverviewChurn.None(HistoryWindow.DefaultDays);

        var ranked = await HistoryQueries.RankAsync(connection, paths, window, null, null, ChurnFilesShown,
            cancellationToken);
        return new OverviewChurn(window.Days, window.Since, window.Until, ranked);
    }

    /// <summary>
    ///     Who has touched the project most, over the whole imported history rather than the churn
    ///     window. Grouped by email, which is the identity git records; the name is taken from the most
    ///     recent commit, because a person who changed how they spell their name would otherwise appear
    ///     under whichever spelling sorted first.
    /// </summary>
    private static async Task<IReadOnlyList<OverviewAuthor>> AuthorsAsync(DuckDBConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.Query($"""
                                              SELECT author_email,
                                                     arg_max(author_name, authored_at) AS author_name,
                                                     count(*)::INTEGER AS commits,
                                                     -- epoch() for the reason the window reads it that
                                                     -- way: seconds as a double do not depend on whether
                                                     -- ICU is loaded to decide the session time zone.
                                                     epoch(max(authored_at)) AS last_commit
                                              FROM commits
                                              GROUP BY author_email
                                              ORDER BY commits DESC, author_email
                                              LIMIT {AuthorsShown}
                                              """, []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var authors = new List<OverviewAuthor>();
        while (await reader.ReadAsync(cancellationToken))
            authors.Add(new OverviewAuthor(reader.Text("author_name"), reader.Text("author_email"),
                reader.Int32("commits"),
                DateTimeOffset.FromUnixTimeSeconds((long)reader.Double("last_commit"))));
        return authors;
    }
}
