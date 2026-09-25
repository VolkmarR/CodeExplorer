using System.Text.Json.Serialization;
using CodeExplorer.Language;
using DuckDB.NET.Data;

namespace CodeExplorer.Reading;

/// <summary>
///     What an overview is computed over. <see cref="Stored" /> is the one the build takes for the row
///     <c>project_overview</c> answers from; the overview page asks for its own (#216), with the window
///     and the repository it was filtered to and the paths the project's setting leaves out.
/// </summary>
/// <param name="Days">How far back the churn ranking reaches from the newest recorded commit.</param>
/// <param name="RepositorySlug">One repository, or null for every one in the project.</param>
/// <param name="Excluded">The paths no section counts, ranks or lists.</param>
/// <param name="AuthorsInWindow">
///     Whether Most commits counts the window's commits rather than the whole imported history. The page
///     sets it, so that every card under its filter bar answers for the same window; the stored row does
///     not, because an agent asking who knows the code wants everyone who ever wrote it.
/// </param>
public sealed record OverviewScope(
    int Days,
    string? RepositorySlug,
    ExcludedPaths Excluded,
    bool AuthorsInWindow = false)
{
    /// <summary>The whole project over the default window with nothing left out: the stored row's scope.</summary>
    public static readonly OverviewScope Stored = new(HistoryWindow.DefaultDays, null, ExcludedPaths.None);
}

/// <summary>
///     How many files the excluded paths kept out of each kind of section, so that a short section
///     cannot pass for a small project or a quiet one (CODING_STANDARDS, Errors). Three numbers and not
///     one, because the sections read three different sets: the files at HEAD, the files a window's
///     commits touched, and the files the commits Most commits counts touched.
/// </summary>
/// <param name="Files">Files at HEAD left out of Languages, Top level and Largest files.</param>
/// <param name="ChangedFiles">Paths the window's commits touched that Most changed left out.</param>
/// <param name="CommittedFiles">
///     Paths that Most commits left out, over the commits it counts: the window's where
///     <see cref="OverviewScope.AuthorsInWindow" /> is set, every imported one otherwise. A commit is
///     dropped from an author's count only where every file it touched was excluded.
/// </param>
public sealed record OverviewExcluded(int Files, int ChangedFiles, int CommittedFiles);

/// <summary>
///     One file of the overview page's Hotspots card (#211): large and busy at once. Only a file at HEAD
///     has one, since its lines are read there, so it carries no <c>AtHead</c> the way a churned file does.
/// </summary>
/// <param name="QualifiedPath">The file, spelled the way the project spells it (ADR-0006).</param>
/// <param name="Commits">Commits in the window that touched it, counted as Most changed counts them.</param>
/// <param name="Lines">Its lines at HEAD.</param>
/// <param name="Score"><paramref name="Commits" /> times <paramref name="Lines" />, what the card ranks by.</param>
public sealed record Hotspot(string QualifiedPath, int Commits, int Lines, long Score);

/// <summary>
///     The Hotspots card: the page's alone, never in the stored row, so <c>project_overview</c> answers
///     as it did before it (#211).
/// </summary>
/// <param name="Files">The top of the ranking; empty where the scope holds no history.</param>
/// <param name="Excluded">
///     Files at HEAD that the window's commits touched and the exclusions left out; null where the scope
///     excludes nothing.
/// </param>
public sealed record OverviewHotspots(IReadOnlyList<Hotspot> Files, int? Excluded);

/// <summary>
///     One file of the overview page's Most authors per file card (#212), over the whole imported history
///     and by the path each commit recorded, so a rename starts its count again.
/// </summary>
/// <param name="QualifiedPath">The file at HEAD, spelled the way the project spells it (ADR-0006).</param>
/// <param name="Authors">Distinct author emails among the commits that touched it.</param>
/// <param name="Commits">The commits that touched it.</param>
/// <param name="First">The share of <paramref name="Commits" /> its most frequent author made, 0 to 1.</param>
/// <param name="Second">The same for the second; 0 where it has one author.</param>
/// <param name="Third">The same for the third; 0 where it has fewer than three.</param>
public sealed record AuthoredFile(
    string QualifiedPath, int Authors, int Commits, double First, double Second, double Third);

/// <summary>
///     The Most authors per file card: the page's alone like <see cref="OverviewHotspots" />, so
///     <c>project_overview</c> answers as it did before it (#212).
/// </summary>
/// <param name="Files">The top of the ranking; empty where the scope holds no history.</param>
/// <param name="Excluded">
///     Files at HEAD that a commit touched and the exclusions left out; null where the scope excludes
///     nothing.
/// </param>
public sealed record OverviewAuthorsPerFile(IReadOnlyList<AuthoredFile> Files, int? Excluded);

/// <summary>
///     The cards only the overview page draws, over the page's scope: never in the stored row, so
///     <c>project_overview</c> answers as it did before any of them. One record rather than a field
///     each on the page's answer, so that a card is added here and in <see cref="OverviewQueries.CardsAsync" />
///     and nowhere else on the server.
/// </summary>
public sealed record OverviewCards(
    OverviewHotspots Hotspots,
    OverviewAuthorsPerFile AuthorsPerFile,
    OverviewFolderCoupling FolderCoupling,
    OverviewFileChanges FileChanges);

/// <summary>How long one bar of the Files added and deleted card is, chosen from the window's length.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChangePeriod>))]
public enum ChangePeriod
{
    /// <summary>A UTC calendar day.</summary>
    Day,

    /// <summary>A week from Monday, in UTC.</summary>
    Week,

    /// <summary>A UTC calendar month.</summary>
    Month
}

/// <summary>One bar of the Files added and deleted card.</summary>
/// <param name="Start">
///     The day the period starts on. The first and last periods are clipped to the window, so they can
///     hold fewer days than the others.
/// </param>
/// <param name="Added">Files the period's commits added.</param>
/// <param name="Deleted">Files they deleted.</param>
/// <param name="Renamed">
///     Files git detected as moved, counted apart from both: a move neither grows nor shrinks the code.
/// </param>
public sealed record PeriodChanges(DateOnly Start, int Added, int Deleted, int Renamed);

/// <summary>
///     The Files added and deleted card (#214): the page's alone like <see cref="OverviewHotspots" />.
/// </summary>
/// <param name="Period">How long each of <paramref name="Periods" /> is.</param>
/// <param name="Periods">
///     Oldest first, covering the churn section's window, a period without commits an explicit zero;
///     empty where the scope holds no history. The true counts: capping an outlier is the card's to do.
/// </param>
public sealed record OverviewFileChanges(ChangePeriod Period, IReadOnlyList<PeriodChanges> Periods);

/// <summary>
///     A folder of one repository the coupling chart pairs, and the commits in the window that touched
///     it: a top-level folder, or a child of the folder that holds nearly all of the repository.
/// </summary>
/// <param name="Folder">The folder's name, its first path segment in the repository.</param>
/// <param name="Commits">Distinct commits under the ceiling that touched anything beneath it.</param>
public sealed record FolderCommits(string Folder, int Commits);

/// <summary>Two folders of one repository that the same commits touched.</summary>
/// <param name="First">The folder that sorts first by name.</param>
/// <param name="Second">The other.</param>
/// <param name="Commits">Distinct commits that touched something under both.</param>
public sealed record FolderPair(string First, string Second, int Commits);

/// <summary>
///     One repository's folder coupling. Pairs never cross a repository, because a commit never does
///     (CONTEXT.md, Co-Change).
/// </summary>
/// <param name="RepositorySlug">The repository.</param>
/// <param name="Commits">Distinct commits under the ceiling that touched one of its folders.</param>
/// <param name="Folders">Its busiest folders, most commits first, capped.</param>
/// <param name="Pairs">The pairs among <paramref name="Folders" /> that share a commit, strongest first.</param>
public sealed record RepositoryCoupling(
    string RepositorySlug, int Commits, IReadOnlyList<FolderCommits> Folders, IReadOnlyList<FolderPair> Pairs);

/// <summary>
///     The Folders that change together card (#213): the page's alone like <see cref="OverviewHotspots" />.
/// </summary>
/// <param name="Repositories">Most commits first; empty where the window holds no history.</param>
/// <param name="MaxCommitPaths">The ceiling the pairing ran under, <c>History:MaxCommitPaths</c>.</param>
/// <param name="CeilingExcluded">Commits in the window that touched more paths than the ceiling.</param>
public sealed record OverviewFolderCoupling(
    IReadOnlyList<RepositoryCoupling> Repositories, int MaxCommitPaths, int CeilingExcluded);

/// <summary>
///     The sections of an overview, as statements over a bare connection: the build runs them once over
///     <see cref="OverviewScope.Stored" /> and stores the result, and the overview page runs them per
///     view over its own scope (#216). One copy of the SQL for both, so that the page and the stored
///     row can differ only by what the page was asked to leave out — never by how a section is counted
///     — and so that a rule such as folding a repository's root files into one entry reaches both.
///     Every clause a scope adds is left out entirely where the scope does not ask for it, so the
///     stored row is computed by the statements it always was.
/// </summary>
internal static class OverviewQueries
{
    /// <summary>
    ///     Languages listed before the tail is summarised. Long enough to hold every language a real
    ///     project is written in plus its configuration and documentation extensions, short enough that
    ///     one repository of scattered one-off extensions does not become the whole answer.
    /// </summary>
    private const int _languagesShown = 20;

    /// <summary>
    ///     Top-level folders kept per repository. A repository root is a screenful in every codebase
    ///     anyone would want an overview of; the cap is there so that a generated tree of thousands of
    ///     root folders cannot make this row the largest thing in the index, and what it drops is counted
    ///     rather than hidden. Root files need no cap: they are one count per repository however many
    ///     there are.
    /// </summary>
    private const int _foldersShown = 100;

    /// <summary>
    ///     Enough to warn a caller off the files that would swamp a read, and not a size ranking of the
    ///     project: the tail of one is every file, in order.
    /// </summary>
    private const int _largestFilesShown = 10;

    /// <summary>A screenful of a ranking read from the top down, the same judgement <c>hot_files</c> makes.</summary>
    private const int _churnFilesShown = 10;

    /// <summary>
    ///     Hotspots ranked. The card numbers each one on its scatter plot, and past ten the numbers stop
    ///     being findable there; the list is read from the top down like Most changed's.
    /// </summary>
    private const int _hotspotsShown = 10;

    /// <summary>
    ///     Authors named. Past ten it stops being "who knows this code" and becomes a contributor list,
    ///     which is a question about a repository rather than about the code in it.
    /// </summary>
    private const int _authorsShown = 10;

    /// <summary>
    ///     Files ranked by their number of authors: a screenful read from the top down, like Most
    ///     changed. The tail is every file one person ever touched, which says nothing.
    /// </summary>
    private const int _authoredFilesShown = 10;

    /// <summary>
    ///     Folders per repository on the co-change heatmap. Twelve is 66 cells in the upper
    ///     triangle, about what a card can label legibly; the quieter folders are the ones least likely
    ///     to couple anything.
    /// </summary>
    private const int _coupledFoldersShown = 12;

    /// <summary>
    ///     The longest window drawn a bar per day, and the longest drawn a bar per week: about a month of
    ///     days and half a year of weeks, so the card holds 20 to 40 bars at the windows offered, and a
    ///     longer window is months.
    /// </summary>
    private const int _dailyUpTo = 31, _weeklyUpTo = 186;

    /// <summary>
    ///     Every section over one scope, and how many files the scope's exclusions kept out of them —
    ///     null where it excludes nothing, so a call that leaves nothing out pays for no count.
    ///     The churn section's window is anchored to the newest commit in the scope and not to the
    ///     clock, which for an overview matters twice over: the stored row outlives the build that wrote
    ///     it, and either one is read from a replica that may be days behind (CONTEXT.md, Window).
    /// </summary>
    /// <param name="connection">Bound to the index: a live project, or a shadow being built.</param>
    /// <param name="paths">How this project names its files (ADR-0006), for the paths in the answer.</param>
    /// <param name="scope">What to compute the sections over.</param>
    /// <param name="cancellationToken">Threaded through every statement.</param>
    public static async Task<(IndexOverview Overview, OverviewExcluded? Excluded)> ComputeAsync(
        DuckDBConnection connection, ProjectPaths paths, OverviewScope scope, CancellationToken cancellationToken)
    {
        var (languages, others) = await LanguagesAsync(connection, scope, cancellationToken);
        var (tree, otherFolders) = await TreeAsync(connection, paths, scope, cancellationToken);
        var largest = await LargestFilesAsync(connection, scope, cancellationToken);
        var window = await IndexQueries.WindowAsync(connection, scope.Days, scope.RepositorySlug, cancellationToken);
        var filters = new ChurnFilters(Excluded: scope.Excluded);
        var churn = window is null
            ? OverviewChurn.None(HistoryWindow.Clamp(scope.Days))
            : new OverviewChurn(window.Days, window.Since, window.Until,
                await IndexQueries.RankAsync(connection, paths, window, scope.RepositorySlug, null, filters,
                    _churnFilesShown, cancellationToken));
        var authorsWindow = scope.AuthorsInWindow ? window : null;
        var authors = await AuthorsAsync(connection, paths, scope, authorsWindow, cancellationToken);
        var overview = new IndexOverview(languages, others, tree, otherFolders, largest, churn, authors);
        if (!scope.Excluded.Any) return (overview, null);

        return (overview, new OverviewExcluded(
            await ExcludedFilesAsync(connection, scope, cancellationToken),
            window is null
                ? 0
                : await IndexQueries.HiddenAsync(connection, paths, window, scope.RepositorySlug, null, filters,
                    cancellationToken),
            await ExcludedCommittedAsync(connection, paths, scope, authorsWindow, cancellationToken)));
    }

    /// <summary>The page's own cards, over its scope.</summary>
    /// <param name="connection">Bound to the live index.</param>
    /// <param name="paths">How this project names its files (ADR-0006).</param>
    /// <param name="scope">The page's window, repository and excluded paths.</param>
    /// <param name="window">The churn section's window, for the cards that read one; null where there is no history.</param>
    /// <param name="maxCommitPaths">The co-change ceiling, <c>History:MaxCommitPaths</c>.</param>
    /// <param name="cancellationToken">Threaded through every statement.</param>
    public static async Task<OverviewCards> CardsAsync(DuckDBConnection connection, ProjectPaths paths,
        OverviewScope scope, HistoryWindow? window, int maxCommitPaths, CancellationToken cancellationToken) =>
        new(await HotspotsAsync(connection, paths, scope, window, cancellationToken),
            await AuthorsPerFileAsync(connection, scope, cancellationToken),
            await FolderCouplingAsync(connection, paths, scope, window, maxCommitPaths, cancellationToken),
            await FileChangesAsync(connection, paths, scope, window, cancellationToken));

    /// <summary>
    ///     Files added, deleted and renamed over the churn section's window (#214), a bar per day, week
    ///     or month by the window's length (<see cref="PeriodOf" />). The periods are calendar ones cut in
    ///     C# as UTC epoch seconds, the first and last clipped to the window and compared the way the
    ///     window is (<see cref="IndexQueries.ChurnScope" />), so the bars add up to the window's own
    ///     changes and a commit near midnight lands in one period whatever the session's time zone. A
    ///     move that also changed more than half of a file is outside git's rename detection and counts
    ///     here as a delete and an add.
    /// </summary>
    /// <param name="connection">Bound to the live index.</param>
    /// <param name="paths">How this project names its files (ADR-0006), for the excluded paths.</param>
    /// <param name="scope">The page's repository and excluded paths.</param>
    /// <param name="window">The churn section's window; null where there is no history.</param>
    /// <param name="cancellationToken">Threaded through the statement.</param>
    public static async Task<OverviewFileChanges> FileChangesAsync(DuckDBConnection connection,
        ProjectPaths paths, OverviewScope scope, HistoryWindow? window, CancellationToken cancellationToken)
    {
        if (window is null) return new OverviewFileChanges(ChangePeriod.Month, []);

        var period = PeriodOf(window.Days);
        long since = window.Since.ToUnixTimeSeconds();
        // One past the newest second, because the window's BETWEEN includes its end and a period's
        // upper bound is exclusive.
        long until = window.Until.ToUnixTimeSeconds() + 1;
        var periods = new List<(DateOnly Start, long Since, long Until)>();
        for (var start = PeriodStart(window.Since.UtcDateTime, period);
             start.ToUnixTimeSeconds() < until;
             start = NextPeriod(start, period))
            periods.Add((DateOnly.FromDateTime(start.UtcDateTime),
                Math.Max(start.ToUnixTimeSeconds(), since),
                Math.Min(NextPeriod(start, period).ToUnixTimeSeconds(), until)));
        // Inlined rather than bound: integers this method computed, never input, and a VALUES list of
        // parameters would be two per period for no gain.
        string periodRows = string.Join(", ", periods.Select(p => $"({p.Since}, {p.Until})"));

        var parameters = new List<DuckDBParameter> { new("since", since), new("until", until) };
        var conditions = CommitScope(scope, parameters);
        // The join onto the periods would cut the span by itself; filtered here as well so that the join
        // sees only the window's rows and not the whole history.
        conditions.Add("epoch(c.authored_at) >= $since AND epoch(c.authored_at) < $until");
        conditions.Add("cf.change_kind IN ('added', 'deleted', 'renamed')");
        if (scope.Excluded.Matching(IndexQueries.CommittedPath(paths), "x", parameters) is { } excluded)
            conditions.Add($"NOT {excluded}");

        await using var command = connection.Query($"""
                                                    WITH periods(since, until) AS (VALUES {periodRows}),
                                                    changes AS (
                                                        SELECT epoch(c.authored_at) AS at, cf.change_kind
                                                        FROM commit_files cf JOIN commits c USING (commit_id)
                                                        {Where(conditions)})
                                                    SELECT count(*) FILTER (WHERE change_kind = 'added')::INTEGER AS added,
                                                           count(*) FILTER (WHERE change_kind = 'deleted')::INTEGER AS deleted,
                                                           count(*) FILTER (WHERE change_kind = 'renamed')::INTEGER AS renamed
                                                    FROM periods p
                                                    LEFT JOIN changes ch ON ch.at >= p.since AND ch.at < p.until
                                                    GROUP BY p.since
                                                    ORDER BY p.since
                                                    """, parameters);
        await using var reader = await command.ReaderAsync(cancellationToken);
        // One row per period in the same order, so each row takes its start from the list it was cut from.
        var result = new List<PeriodChanges>(periods.Count);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new PeriodChanges(periods[result.Count].Start, reader.Int32("added"),
                reader.Int32("deleted"), reader.Int32("renamed")));

        return new OverviewFileChanges(period, result);
    }

    /// <summary>
    ///     The share of a repository's files at HEAD one folder has to hold before the coupling chart
    ///     looks inside it instead: a folder with nearly everything in it pairs with every other folder
    ///     and says nothing, as <c>src/</c> does where it holds the whole solution.
    /// </summary>
    private const double _dominantShare = 0.8;

    /// <summary>How many levels the coupling chart descends at most, so a deep single chain stops somewhere.</summary>
    private const int _couplingDepth = 3;

    /// <summary>
    ///     Per repository, the folder the coupling chart pairs the children of: empty where no folder
    ///     dominates, <c>src</c> where it holds the solution, <c>src/app</c> where that holds nearly all of
    ///     it in turn. Read from the files at HEAD rather than the window's commits, so the grouping is the
    ///     repository's layout and does not change with the window. A folder is descended into only where
    ///     it holds <see cref="_dominantShare" /> of the repository's files and has two subfolders or more
    ///     to show in its place.
    /// </summary>
    private static async Task<Dictionary<string, string>> CouplingPrefixesAsync(DuckDBConnection connection,
        OverviewScope scope, CancellationToken cancellationToken)
    {
        var parameters = new List<DuckDBParameter>();
        string where = Where(FileScope(scope, parameters));
        // A file's folders, down to the deepest level looked at: its path without the file name,
        // at most _couplingDepth segments. Root files have none and count only towards the total.
        await using var command = connection.Query($"""
                                                    WITH split AS (
                                                        SELECT repo_id, string_split(path, '/') AS parts
                                                        FROM files {where})
                                                    SELECT r.slug AS repo_slug,
                                                           list_slice(parts, 1, least(len(parts) - 1, {_couplingDepth})) AS dirs,
                                                           count(*)::INTEGER AS files
                                                    FROM split JOIN repositories r USING (repo_id)
                                                    GROUP BY ALL
                                                    """, parameters);
        await using var reader = await command.ReaderAsync(cancellationToken);
        var rows = new List<(string Slug, string[] Dirs, int Files)>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((reader.Text("repo_slug"), reader.GetFieldValue<List<string>>(1).ToArray(),
                reader.Int32("files")));

        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var repository in rows.GroupBy(r => r.Slug, StringComparer.Ordinal))
        {
            int total = repository.Sum(r => r.Files);
            var prefix = new List<string>();
            for (int depth = 0; depth < _couplingDepth; depth++)
            {
                var inside = repository.Where(r => r.Dirs.Length > depth && StartsWith(r.Dirs, prefix)).ToList();
                var largest = inside.GroupBy(r => r.Dirs[depth], StringComparer.Ordinal)
                    .Select(g => (Folder: g.Key, Files: g.Sum(r => r.Files),
                        Subfolders: g.Where(r => r.Dirs.Length > depth + 1)
                            .Select(r => r.Dirs[depth + 1]).Distinct(StringComparer.Ordinal).Count()))
                    .OrderByDescending(f => f.Files).FirstOrDefault();
                if (largest.Folder is null || largest.Files < _dominantShare * total || largest.Subfolders < 2)
                    break;
                prefix.Add(largest.Folder);
            }

            if (prefix.Count > 0) prefixes[repository.Key] = string.Join('/', prefix);
        }

        return prefixes;

        static bool StartsWith(string[] dirs, List<string> prefix) =>
            prefix.Select((segment, i) => dirs[i] == segment).All(same => same);
    }

    /// <summary>
    ///     Narrows a query on <c>commits c</c> to a window, compared in epoch seconds and inclusive at
    ///     both ends as the churn section compares it (<see cref="IndexQueries.ChurnScope" />). Nothing
    ///     where the window is null.
    /// </summary>
    private static void InWindow(HistoryWindow? window, List<string> conditions, List<DuckDBParameter> parameters)
    {
        if (window is null) return;
        parameters.Add(new DuckDBParameter("window_since", window.Since.ToUnixTimeSeconds()));
        parameters.Add(new DuckDBParameter("window_until", window.Until.ToUnixTimeSeconds()));
        conditions.Add("epoch(c.authored_at) BETWEEN $window_since AND $window_until");
    }

    /// <summary>A bar per day up to a month of window, per week up to half a year, and per month beyond.</summary>
    internal static ChangePeriod PeriodOf(int days) =>
        days <= _dailyUpTo ? ChangePeriod.Day : days <= _weeklyUpTo ? ChangePeriod.Week : ChangePeriod.Month;

    /// <summary>The start of the calendar period holding <paramref name="at" />, in UTC.</summary>
    private static DateTimeOffset PeriodStart(DateTime at, ChangePeriod period)
    {
        var day = new DateTimeOffset(at.Year, at.Month, at.Day, 0, 0, 0, TimeSpan.Zero);
        return period switch
        {
            ChangePeriod.Day => day,
            // Monday, the ISO week's first day: DayOfWeek counts from Sunday.
            ChangePeriod.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
            _ => new DateTimeOffset(at.Year, at.Month, 1, 0, 0, 0, TimeSpan.Zero)
        };
    }

    private static DateTimeOffset NextPeriod(DateTimeOffset start, ChangePeriod period) => period switch
    {
        ChangePeriod.Day => start.AddDays(1),
        ChangePeriod.Week => start.AddDays(7),
        _ => start.AddMonths(1)
    };

    /// <summary>
    ///     The pairs of folders, within one repository, that the window's commits keep touching together
    ///     (#213): the top-level folders, except that a folder holding nearly the whole repository is
    ///     replaced by its children (<see cref="CouplingPrefixesAsync" />), because it pairs with
    ///     everything and says nothing. A pair counts distinct commits, so one commit touching forty files in a folder
    ///     counts once, the Churn rule for directories. A commit that touched more paths than the ceiling
    ///     is left out, as the co-change tool leaves it out, and counted. The ceiling is measured on every
    ///     path the commit touched, before the exclusions, so it means what it means there; the excluded
    ///     paths are then dropped before pairing. A folder's own count is over the same commits the pairs
    ///     are, so a pair's share of the smaller folder is never above one.
    /// </summary>
    /// <param name="connection">Bound to the live index.</param>
    /// <param name="paths">How this project names its files (ADR-0006), for the excluded paths.</param>
    /// <param name="scope">The page's repository and excluded paths.</param>
    /// <param name="window">The churn section's window; null where there is no history.</param>
    /// <param name="maxCommitPaths">The co-change ceiling.</param>
    /// <param name="cancellationToken">Threaded through the statement.</param>
    public static async Task<OverviewFolderCoupling> FolderCouplingAsync(DuckDBConnection connection,
        ProjectPaths paths, OverviewScope scope, HistoryWindow? window, int maxCommitPaths,
        CancellationToken cancellationToken)
    {
        if (window is null) return new OverviewFolderCoupling([], maxCommitPaths, 0);

        var prefixes = await CouplingPrefixesAsync(connection, scope, cancellationToken);
        var (inWindow, parameters) =
            IndexQueries.ChurnScope(paths, window, scope.RepositorySlug, null, ChurnFilters.None);
        parameters.Add(new DuckDBParameter("mc", maxCommitPaths));
        // Bound, not inlined: the prefixes are folder names out of the repositories.
        var prefixRows = prefixes.Select((p, i) =>
        {
            parameters.Add(new DuckDBParameter($"ps{i}", p.Key));
            parameters.Add(new DuckDBParameter($"pp{i}", p.Value));
            return $"($ps{i}, $pp{i})";
        }).ToList();
        string prefixTable = prefixRows.Count == 0
            ? "SELECT NULL::VARCHAR AS repo_slug, NULL::VARCHAR AS prefix WHERE false"
            : $"SELECT * FROM (VALUES {string.Join(", ", prefixRows)}) AS v(repo_slug, prefix)";
        // A file at the repository root is in no top-level folder, so it pairs with nothing.
        var kept = new List<string> { "s.paired", "contains(w.path, '/')" };
        // The qualified path is carried only where an exclusion reads it.
        string committed = "";
        if (scope.Excluded.Matching("w.committed", "x", parameters) is { } excluded)
        {
            kept.Add($"NOT {excluded}");
            committed = $", {IndexQueries.CommittedPath(paths)} AS committed";
        }

        // in_window is MATERIALIZED because sized and folders both read it, and inlined it would be a
        // second scan of commit_files for the same rows.
        await using var command = connection.Query($"""
                                                    WITH in_window AS MATERIALIZED (
                                                        SELECT c.commit_id, c.repo_slug, cf.path{committed}
                                                        FROM commit_files cf JOIN commits c USING (commit_id)
                                                        WHERE {string.Join(" AND ", inWindow)}),
                                                    sized AS (
                                                        SELECT commit_id, count(*) <= $mc AS paired
                                                        FROM in_window GROUP BY commit_id),
                                                    prefixes AS ({prefixTable}),
                                                    -- A file's folder is its top-level one, or under a
                                                    -- repository's prefix the child of the prefix it is in.
                                                    -- A file directly in the prefix is in no child, so like
                                                    -- a root file it pairs with nothing.
                                                    placed AS (
                                                        SELECT w.repo_slug, w.commit_id,
                                                               CASE WHEN p.prefix IS NULL
                                                                         OR NOT starts_with(w.path, p.prefix || '/')
                                                                    THEN split_part(w.path, '/', 1)
                                                                    WHEN contains(substr(w.path, length(p.prefix) + 2), '/')
                                                                    THEN p.prefix || '/' ||
                                                                         split_part(substr(w.path, length(p.prefix) + 2), '/', 1)
                                                               END AS folder
                                                        FROM in_window w JOIN sized s USING (commit_id)
                                                        LEFT JOIN prefixes p ON p.repo_slug = w.repo_slug
                                                        WHERE {string.Join(" AND ", kept)}),
                                                    folders AS (
                                                        SELECT DISTINCT repo_slug, commit_id, folder
                                                        FROM placed WHERE folder IS NOT NULL),
                                                    per_folder AS (
                                                        SELECT repo_slug, folder, count(*)::INTEGER AS commits,
                                                               row_number() OVER (PARTITION BY repo_slug
                                                                   ORDER BY count(*) DESC, folder) AS rank
                                                        FROM folders GROUP BY repo_slug, folder),
                                                    top AS (
                                                        SELECT * FROM per_folder WHERE rank <= {_coupledFoldersShown}),
                                                    shown AS (
                                                        SELECT f.* FROM folders f SEMI JOIN top USING (repo_slug, folder))
                                                    SELECT 'folder' AS kind, repo_slug, folder AS first,
                                                           NULL AS second, commits
                                                    FROM top
                                                    UNION ALL
                                                    SELECT 'repository', repo_slug, NULL, NULL,
                                                           count(DISTINCT commit_id)::INTEGER
                                                    FROM folders GROUP BY repo_slug
                                                    UNION ALL
                                                    SELECT 'ceiling', '', NULL, NULL,
                                                           count(*) FILTER (WHERE NOT paired)::INTEGER
                                                    FROM sized
                                                    UNION ALL
                                                    -- Joined on the commit alone: a commit is in one
                                                    -- repository, so a pair never crosses one.
                                                    SELECT 'pair', a.repo_slug, a.folder, b.folder,
                                                           count(*)::INTEGER
                                                    FROM shown a JOIN shown b
                                                        ON a.commit_id = b.commit_id AND a.folder < b.folder
                                                    GROUP BY a.repo_slug, a.folder, b.folder
                                                    """, parameters);
        await using var reader = await command.ReaderAsync(cancellationToken);
        var repositories = new Dictionary<string, int>(StringComparer.Ordinal);
        var folderRows = new List<(string Slug, FolderCommits Folder)>();
        var pairRows = new List<(string Slug, FolderPair Pair)>();
        int ceilingExcluded = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            string slug = reader.Text("repo_slug");
            int commits = reader.Int32("commits");
            string kind = reader.Text("kind");
            switch (kind)
            {
                case "ceiling": ceilingExcluded = commits; break;
                case "repository": repositories[slug] = commits; break;
                case "folder": folderRows.Add((slug, new FolderCommits(reader.Text("first"), commits))); break;
                case "pair":
                    pairRows.Add((slug, new FolderPair(reader.Text("first"), reader.Text("second"), commits)));
                    break;
                default: throw new InvalidOperationException($"Unknown folder coupling row kind '{kind}'.");
            }
        }

        var folders = folderRows.ToLookup(f => f.Slug, f => f.Folder, StringComparer.Ordinal);
        var pairs = pairRows.ToLookup(p => p.Slug, p => p.Pair, StringComparer.Ordinal);
        var coupling = repositories
            .OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => new RepositoryCoupling(r.Key, r.Value,
                folders[r.Key]
                    .OrderByDescending(f => f.Commits).ThenBy(f => f.Folder, StringComparer.Ordinal).ToList(),
                pairs[r.Key]
                    .OrderByDescending(p => p.Commits).ThenBy(p => p.First, StringComparer.Ordinal)
                    .ThenBy(p => p.Second, StringComparer.Ordinal).ToList()))
            .ToList();
        return new OverviewFolderCoupling(coupling, maxCommitPaths, ceilingExcluded);
    }

    /// <summary>
    ///     The files at HEAD that are both large and busy (#211): commits in the scope's window times
    ///     lines at HEAD, highest first. Commits and not lines changed, as churn counts them, so a sweep
    ///     that touches every file adds one to each and lifts none of them. A tie goes to the file with
    ///     more lines changed and then to the path, the order Most changed breaks its ties in.
    ///     Not a section of <see cref="ComputeAsync" />: the stored row has no hotspots, and the build
    ///     should not pay for a card only the page draws.
    /// </summary>
    /// <param name="connection">Bound to the live index.</param>
    /// <param name="paths">How this project names its files (ADR-0006).</param>
    /// <param name="scope">The page's repository and excluded paths.</param>
    /// <param name="cancellationToken">Threaded through both statements.</param>
    /// <param name="window">
    ///     The churn section's window, handed over rather than looked up again, so that the two cards
    ///     cannot disagree about whether there is history; null where there is none.
    /// </param>
    public static async Task<OverviewHotspots> HotspotsAsync(DuckDBConnection connection, ProjectPaths paths,
        OverviewScope scope, HistoryWindow? window, CancellationToken cancellationToken)
    {
        if (window is null) return new OverviewHotspots([], scope.Excluded.Any ? 0 : null);

        var (touched, parameters) = HotspotScope(paths, window, scope, false);
        await using var command = connection.Query($"""
                                                    {touched}
                                                    SELECT h.qualified_path, t.commits, h.line_count,
                                                           t.commits::BIGINT * h.line_count AS score
                                                    FROM touched t JOIN at_head h USING (repo_slug, path)
                                                    -- Spelled out for the reason RankAsync gives.
                                                    ORDER BY t.commits::BIGINT * h.line_count DESC, t.changed DESC,
                                                             h.qualified_path
                                                    LIMIT {_hotspotsShown}
                                                    """, parameters);
        await using var reader = await command.ReaderAsync(cancellationToken);
        var files = new List<Hotspot>();
        while (await reader.ReadAsync(cancellationToken))
            files.Add(new Hotspot(reader.Text("qualified_path"), reader.Int32("commits"), reader.Int32("line_count"),
                reader.Int64("score")));
        if (!scope.Excluded.Any) return new OverviewHotspots(files, null);

        var (excluded, excludedParameters) = HotspotScope(paths, window, scope, true);
        return new OverviewHotspots(files, (int)await connection.CountAsync($"""
                 {excluded}
                 SELECT count(*) FROM touched JOIN at_head USING (repo_slug, path)
                 """, excludedParameters, cancellationToken));
    }

    /// <summary>
    ///     The files at HEAD changed by the most different people over the whole imported history (#212),
    ///     with the commit share of their first three authors, so the card can say whether anyone owns
    ///     one. Authors are told apart by email, the identity git records, so one person under two names
    ///     counts once. Commits and not lines, so a sweep adds one name to each file it touches and never
    ///     makes its author the owner. A tie goes to the file with more commits and then to the path.
    ///     Not a section of <see cref="ComputeAsync" />, for the reason <see cref="HotspotsAsync" /> is not.
    /// </summary>
    /// <param name="connection">Bound to the live index.</param>
    /// <param name="scope">The page's repository and excluded paths; the window does not reach this card.</param>
    /// <param name="cancellationToken">Threaded through both statements.</param>
    public static async Task<OverviewAuthorsPerFile> AuthorsPerFileAsync(DuckDBConnection connection,
        OverviewScope scope, CancellationToken cancellationToken)
    {
        // No short cut where the scope holds no history: authored is empty then, so the ranking is empty
        // and the count is 0, which is the answer.
        var (authored, parameters) = AuthoredScope(scope, false);
        await using var command = connection.Query($"""
                                                    {authored},
                                                    by_author AS (
                                                        SELECT qualified_path, count(*)::INTEGER AS commits
                                                        FROM authored
                                                        GROUP BY qualified_path, author_email),
                                                    per_file AS (
                                                        SELECT qualified_path,
                                                               count(*)::INTEGER AS authors,
                                                               sum(commits)::INTEGER AS commits,
                                                               list(commits ORDER BY commits DESC) AS shares
                                                        FROM by_author
                                                        GROUP BY qualified_path)
                                                    -- An index past the list's end is NULL: a file with
                                                    -- fewer than three authors.
                                                    SELECT qualified_path, authors, commits,
                                                           shares[1] / commits AS first,
                                                           coalesce(shares[2] / commits, 0) AS second,
                                                           coalesce(shares[3] / commits, 0) AS third
                                                    FROM per_file
                                                    ORDER BY authors DESC, commits DESC, qualified_path
                                                    LIMIT {_authoredFilesShown}
                                                    """, parameters);
        await using var reader = await command.ReaderAsync(cancellationToken);
        var files = new List<AuthoredFile>();
        while (await reader.ReadAsync(cancellationToken))
            files.Add(new AuthoredFile(reader.Text("qualified_path"), reader.Int32("authors"),
                reader.Int32("commits"), reader.Double("first"), reader.Double("second"), reader.Double("third")));
        if (!scope.Excluded.Any) return new OverviewAuthorsPerFile(files, null);

        var (excluded, excludedParameters) = AuthoredScope(scope, true);
        return new OverviewAuthorsPerFile(files, (int)await connection.CountAsync($"""
                 {excluded}
                 SELECT count(DISTINCT qualified_path) FROM authored
                 """, excludedParameters, cancellationToken));
    }

    /// <summary>
    ///     The two CTEs the author ranking and its excluded count read: <c>at_head</c>, and
    ///     <c>authored</c>, one row per commit that touched one of those files, with its author. Joined
    ///     onto the files at HEAD before anything is grouped, so the groupings never see a path history
    ///     recorded and HEAD no longer holds — on Radix, 122,172 paths of which 49,523 are at HEAD. A
    ///     file at HEAD has one qualified path, so the rest of the statement groups by that alone.
    /// </summary>
    private static (string Sql, List<DuckDBParameter> Parameters) AuthoredScope(OverviewScope scope,
        bool excludedOnly)
    {
        var parameters = new List<DuckDBParameter>();
        string atHead = AtHead(scope, excludedOnly, parameters);
        return ($"""
                 WITH {atHead},
                 authored AS (
                     SELECT h.qualified_path, c.author_email
                     FROM commit_files cf
                     JOIN commits c USING (commit_id)
                     JOIN at_head h ON h.repo_slug = c.repo_slug AND h.path = cf.path
                     {Where(CommitScope(scope, parameters))})
                 """, parameters);
    }

    /// <summary>
    ///     The two CTEs the hotspot ranking and its excluded count join: <c>touched</c>, each path the
    ///     window's commits touched with its commits and lines changed, and <c>at_head</c>, the indexed
    ///     files at HEAD in the scope — without the excluded paths, or where
    ///     <paramref name="excludedOnly" /> is set, only them.
    ///     Grouped before the join: the window reduces to one row per path, and joining files onto those
    ///     is a hash join over the files at HEAD rather than one per commit row. A skipped file is left
    ///     out of <c>at_head</c>, since a binary or an oversized file has no lines to multiply.
    /// </summary>
    private static (string Sql, List<DuckDBParameter> Parameters) HotspotScope(ProjectPaths paths,
        HistoryWindow window, OverviewScope scope, bool excludedOnly)
    {
        var (inWindow, parameters) =
            IndexQueries.ChurnScope(paths, window, scope.RepositorySlug, null, ChurnFilters.None);
        return ($"""
                 WITH touched AS (
                     SELECT c.repo_slug, cf.path,
                            count(*)::INTEGER AS commits,
                            -- Cast for the reason RankAsync casts.
                            sum(cf.added)::BIGINT + sum(cf.deleted)::BIGINT AS changed
                     FROM commit_files cf
                     JOIN commits c USING (commit_id)
                     WHERE {string.Join(" AND ", inWindow)}
                     GROUP BY c.repo_slug, cf.path),
                 {AtHead(scope, excludedOnly, parameters)}
                 """, parameters);
    }

    /// <summary>
    ///     The <c>at_head</c> CTE the history cards join their per-path counts onto: the indexed files at
    ///     HEAD in the scope, keyed by repository slug and path as a commit records them — without the
    ///     excluded paths, or where <paramref name="excludedOnly" /> is set, only them. A skipped file is
    ///     left out: a binary or an oversized file has no lines to multiply and nothing a link to it can
    ///     show, the reason Largest files leaves it out (#215). <c>line_count</c> is the hotspots'; a
    ///     statement that never reads it has it pruned.
    /// </summary>
    private static string AtHead(OverviewScope scope, bool excludedOnly, List<DuckDBParameter> parameters)
    {
        var conditions = new List<string> { "f.skip_reason IS NULL" };
        // The join on repo_slug would narrow at_head to the repository anyway, but only after at_head has
        // matched every file in the project against the excluded paths: dropping this condition took the
        // one-repository hotspot ranking on Radix from 11 ms to 36 ms. Bound under its own name rather
        // than leaning on the one the caller's commit scope chose.
        if (scope.RepositorySlug is not null)
        {
            conditions.Add("r.slug = $hr");
            parameters.Add(new DuckDBParameter("hr", scope.RepositorySlug));
        }

        if (scope.Excluded.Matching("f.qualified_path", "x", parameters) is { } matching)
            conditions.Add(excludedOnly ? matching : $"NOT {matching}");

        return $"""
                at_head AS (
                    SELECT r.slug AS repo_slug, f.path, f.qualified_path, f.line_count
                    FROM files f JOIN repositories r USING (repo_id)
                    {Where(conditions)})
                """;
    }

    /// <summary>
    ///     The conditions over an unaliased <c>files</c> that narrow it to the scope, or none. The
    ///     repository is matched through a scalar subquery rather than a join, so the grouping queries
    ///     below keep grouping <c>files</c> alone and join onto the few rows they produce.
    /// </summary>
    private static List<string> FileScope(OverviewScope scope, List<DuckDBParameter> parameters)
    {
        var conditions = new List<string>(2);
        if (scope.RepositorySlug is not null)
        {
            conditions.Add("repo_id = (SELECT repo_id FROM repositories WHERE slug = $r)");
            parameters.Add(new DuckDBParameter("r", scope.RepositorySlug));
        }

        if (scope.Excluded.Matching("qualified_path", "x", parameters) is { } excluded)
            conditions.Add($"NOT {excluded}");
        return conditions;
    }

    /// <summary>
    ///     The same for <c>commits c</c>: the repository alone, because each history section applies the
    ///     exclusions its own way — a path at a time, or a commit at a time.
    /// </summary>
    private static List<string> CommitScope(OverviewScope scope, List<DuckDBParameter> parameters)
    {
        var conditions = new List<string>(2);
        if (scope.RepositorySlug is not null)
        {
            conditions.Add("c.repo_slug = $r");
            parameters.Add(new DuckDBParameter("r", scope.RepositorySlug));
        }

        return conditions;
    }

    /// <summary>A WHERE clause from conditions ANDed, or nothing where there are none.</summary>
    internal static string Where(List<string> conditions) =>
        conditions.Count == 0 ? "" : $"WHERE {string.Join(" AND ", conditions)}";

    /// <summary>
    ///     Counts by language. What an extension counts as is <see cref="IndexQueries" />'s, so this and
    ///     <c>list_extensions</c> cannot come to different totals for one index; the folding into
    ///     languages is done here and not in SQL because the extension-to-language table is
    ///     <see cref="Languages" />'s and a <c>CASE</c> in that statement would be a second copy of it.
    /// </summary>
    private static async Task<(IReadOnlyList<LanguageShare> Shown, int Others)> LanguagesAsync(
        DuckDBConnection connection, OverviewScope scope, CancellationToken cancellationToken)
    {
        var byName = new Dictionary<string, LanguageShare>(StringComparer.Ordinal);
        foreach (var count in await IndexQueries.ExtensionCountsAsync(connection, scope.RepositorySlug,
                     scope.Excluded, cancellationToken))
        {
            var (name, mapped) = Languages.Name(count.Extension);
            // Several extensions fold into one language, so the row is accumulated rather than added:
            // X# counts its headers with its sources.
            byName[name] = byName.TryGetValue(name, out var running)
                ? running with
                {
                    Files = running.Files + count.Files, Lines = running.Lines + count.Lines,
                    Skipped = running.Skipped + count.Skipped
                }
                : new LanguageShare(name, mapped, count.Files, count.Lines, count.Skipped);
        }

        var ordered = byName.Values
            .OrderByDescending(share => share.Lines)
            .ThenByDescending(share => share.Files)
            .ThenBy(share => share.Name, StringComparer.Ordinal)
            .ToList();
        return (ordered.Take(_languagesShown).ToList(), Math.Max(0, ordered.Count - _languagesShown));
    }

    /// <summary>
    ///     The top level of every repository: the folders a qualified path can begin with, each carrying
    ///     everything beneath it, and the files directly at the root as one count. One statement for the
    ///     whole project rather than one per repository, because an overview is drawn once and the
    ///     grouping is the same at every root.
    /// </summary>
    private static async Task<(IReadOnlyList<OverviewRoot> Shown, int Others)> TreeAsync(
        DuckDBConnection connection, ProjectPaths paths, OverviewScope scope, CancellationToken cancellationToken)
    {
        var parameters = new List<DuckDBParameter>();
        string where = Where(FileScope(scope, parameters));
        // Grouped before the join, not after: the aggregate reduces every file in the project to a few
        // dozen top-level rows, and joining repositories onto those costs a lookup per row instead of
        // one per source file.
        await using var command = connection.Query($"""
                                                    WITH tops AS (
                                                        SELECT repo_id,
                                                               -- A file directly at the root has no folder, so
                                                               -- every root file of a repository lands in its
                                                               -- one NULL group: the count the section shows in
                                                               -- place of a row per file.
                                                               CASE WHEN contains(path, '/')
                                                                    THEN split_part(path, '/', 1) END AS folder,
                                                               count(*)::INTEGER AS files,
                                                               sum(line_count)::BIGINT AS lines,
                                                               sum(size_bytes)::BIGINT AS bytes
                                                        FROM files
                                                        {where}
                                                        GROUP BY repo_id, folder)
                                                    SELECT r.slug AS repo_slug, tops.*
                                                    FROM tops JOIN repositories r USING (repo_id)
                                                    ORDER BY r.repo_id, folder NULLS LAST
                                                    """, parameters);
        await using var reader = await command.ReaderAsync(cancellationToken);
        // Read whole before grouping: one row per top-level folder plus one per repository is a few
        // hundred rows at most, and grouping a list reads more plainly than tracking a repository change
        // across the reader loop. A root-file row's path is the repository's root.
        var rows = new List<(string Slug, bool IsRoot, OverviewFolder Entry)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            string slug = reader.Text("repo_slug");
            string? folder = reader.TextOrNull("folder");
            rows.Add((slug, folder is null, new OverviewFolder(paths.Format(slug, folder ?? ""),
                reader.Int32("files"), reader.Int64("lines"), reader.Int64("bytes"))));
        }

        // Capped per repository and not across the project: one repository of a thousand folders would
        // otherwise spend the whole cap and drop every later repository's top level entirely, which is
        // the one thing this section exists to show.
        var roots = rows.GroupBy(row => row.Slug, StringComparer.Ordinal)
            .Select(repository => new OverviewRoot(paths.Format(repository.Key, ""),
                repository.Where(row => !row.IsRoot).Select(row => row.Entry).Take(_foldersShown).ToList(),
                repository.Where(row => row.IsRoot).Select(row => row.Entry).FirstOrDefault()))
            .ToList();
        return (roots, rows.Count(row => !row.IsRoot) - roots.Sum(root => root.Folders.Count));
    }

    /// <summary>
    ///     The largest indexed files by bytes rather than by lines, because the point is what a read
    ///     costs. A skipped file is left out: it has no lines to read, Languages counts it as not
    ///     indexed, and ranking it here spent the section on binaries nobody can open (#215).
    /// </summary>
    private static async Task<IReadOnlyList<OverviewFile>> LargestFilesAsync(DuckDBConnection connection,
        OverviewScope scope, CancellationToken cancellationToken)
    {
        var parameters = new List<DuckDBParameter>();
        var conditions = FileScope(scope, parameters);
        conditions.Insert(0, "skip_reason IS NULL");
        // No join: qualified_path already names the repository wherever the project's naming puts one
        // there (ADR-0006), so repositories has nothing to add to a row of this section.
        await using var command = connection.Query($"""
                                                    SELECT qualified_path, line_count, size_bytes
                                                    FROM files
                                                    {Where(conditions)}
                                                    ORDER BY size_bytes DESC, qualified_path
                                                    LIMIT {_largestFilesShown}
                                                    """, parameters);
        await using var reader = await command.ReaderAsync(cancellationToken);
        var files = new List<OverviewFile>();
        while (await reader.ReadAsync(cancellationToken))
            files.Add(new OverviewFile(reader.Text("qualified_path"), reader.Int32("line_count"),
                reader.Int64("size_bytes")));
        return files;
    }

    /// <summary>
    ///     Who has touched the project most: over the whole imported history for the stored row, where
    ///     "who knows this code" is a longer question than "what is moving now", and over the window on
    ///     the page (<see cref="OverviewScope.AuthorsInWindow" />). Grouped by email, which is the identity
    ///     git records; the name is taken from the most recent commit, because a person who changed how
    ///     they spell their name would otherwise appear under whichever spelling sorted first. A null
    ///     window is the whole imported history.
    /// </summary>
    private static async Task<IReadOnlyList<OverviewAuthor>> AuthorsAsync(DuckDBConnection connection,
        ProjectPaths paths, OverviewScope scope, HistoryWindow? window, CancellationToken cancellationToken)
    {
        var parameters = new List<DuckDBParameter>();
        var conditions = CommitScope(scope, parameters);
        InWindow(window, conditions, parameters);
        if (scope.Excluded.Matching(IndexQueries.CommittedPath(paths), "x", parameters) is { } excluded)
            // A commit counts while it touched one file the page still shows. One that recorded no
            // files at all is kept too: there is nothing in it to exclude, and dropping it would count
            // an author's merges as excluded paths.
            conditions.Add($"""
                            (NOT EXISTS (SELECT 1 FROM commit_files cf WHERE cf.commit_id = c.commit_id)
                             OR EXISTS (SELECT 1 FROM commit_files cf
                                        WHERE cf.commit_id = c.commit_id AND NOT {excluded}))
                            """);

        await using var command = connection.Query($"""
                                                    SELECT author_email,
                                                           arg_max(author_name, authored_at) AS author_name,
                                                           count(*)::INTEGER AS commits,
                                                           -- epoch() for the reason ReaderColumns.EpochInstant
                                                           -- gives.
                                                           epoch(max(authored_at)) AS last_commit
                                                    FROM commits c
                                                    {Where(conditions)}
                                                    GROUP BY author_email
                                                    ORDER BY commits DESC, author_email
                                                    LIMIT {_authorsShown}
                                                    """, parameters);
        await using var reader = await command.ReaderAsync(cancellationToken);
        var authors = new List<OverviewAuthor>();
        while (await reader.ReadAsync(cancellationToken))
            authors.Add(new OverviewAuthor(reader.Text("author_name"), reader.Text("author_email"),
                reader.Int32("commits"),
                reader.EpochInstant("last_commit")));
        return authors;
    }

    /// <summary>The files at HEAD in the scope that the exclusions match, which the file sections left out.</summary>
    private static async Task<int> ExcludedFilesAsync(DuckDBConnection connection, OverviewScope scope,
        CancellationToken cancellationToken)
    {
        // The scope without the exclusions, and then only what they match: the same repository, so the
        // number is about the sections on screen and not about the project.
        var parameters = new List<DuckDBParameter>();
        var conditions = FileScope(scope with { Excluded = ExcludedPaths.None }, parameters);
        conditions.Add(scope.Excluded.Matching("qualified_path", "x", parameters)!);
        return (int)await connection.CountAsync($"SELECT count(*) FROM files {Where(conditions)}", parameters,
            cancellationToken);
    }

    /// <summary>
    ///     The distinct paths any commit in the scope touched that the exclusions match: what the author
    ///     ranking no longer counts. Over the same commits as the ranking itself: its window, or the
    ///     whole imported history where that is null.
    /// </summary>
    private static async Task<int> ExcludedCommittedAsync(DuckDBConnection connection, ProjectPaths paths,
        OverviewScope scope, HistoryWindow? window, CancellationToken cancellationToken)
    {
        var parameters = new List<DuckDBParameter>();
        var conditions = CommitScope(scope, parameters);
        InWindow(window, conditions, parameters);
        conditions.Add(scope.Excluded.Matching(IndexQueries.CommittedPath(paths), "x", parameters)!);
        return (int)await connection.CountAsync($"""
                                                 SELECT count(*) FROM (
                                                     SELECT DISTINCT c.repo_slug, cf.path
                                                     FROM commit_files cf JOIN commits c USING (commit_id)
                                                     {Where(conditions)})
                                                 """, parameters, cancellationToken);
    }
}
