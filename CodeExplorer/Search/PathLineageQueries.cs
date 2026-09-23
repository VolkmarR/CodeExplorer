using System.Globalization;
using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     One earlier spelling of a path scope, reached by a rename edge a commit recorded.
/// </summary>
/// <param name="Spelled">The qualified path as the project spells one (ADR-0006), so a reply can print the call.</param>
/// <param name="PathInRepository">The path inside the repository, which is what a further hop is resolved against.</param>
/// <param name="Commits">How many commits history records under it — the number the scope's own count is missing.</param>
public sealed record PreviousPath(string Spelled, string PathInRepository, int Commits);

/// <summary>
///     What a path scope was called before. A directory renamed mid-history answers for its post-rename
///     slice alone, matched by the path each commit recorded, and nothing in the reply used to say the
///     rest existed — measured at 9 commits reported for a directory with 1,252 (#131).
///     Renames are <b>signalled, not followed</b>: the scope's own count stays literal and this is
///     additive, because following them would invert a contract five tool descriptions state.
///     All three parts are needed together. <see cref="Previous" /> without
///     <see cref="CombinedCommits" /> reproduces the original fault one level up — an agent that wanted
///     a number is handed a path instead — and a reply must also spell out the call that reads the
///     previous path, or it has described work rather than handed it over.
/// </summary>
/// <param name="Previous">The chain, newest first, so the immediately previous name leads and the oldest is last.</param>
/// <param name="Omitted">Further hops the cap kept out, stated the way the churn ranking states what its exclude hid.</param>
/// <param name="CombinedCommits">Distinct commits recorded across the scope and its whole chain, cap or no cap.</param>
/// <param name="Chain">
///     Every earlier path the walk found, repository-relative and <b>untruncated</b> — the same set
///     <see cref="CombinedCommits" /> was counted over, and not the capped
///     <paramref name="Previous" /> a reply prints. A reader that paired over the printed list while
///     quoting a total taken over this one would hand back a ranking and a number that disagree, so
///     anything computing over the chain uses this (#143).
/// </param>
public sealed record PathLineage(IReadOnlyList<PreviousPath> Previous, int Omitted, int CombinedCommits,
    IReadOnlyList<string> Chain);

/// <summary>
///     The Previous Path of a scope (CONTEXT.md): the chain the build wrote down, read back as the
///     note every path-scoped answer carries. It is its own file because it is the one part of this
///     module that answers about a path's earlier names rather than about its commits, and because
///     every other read here only signals it (#131, #143).
/// </summary>
public sealed partial class HistoryQueries
{
    /// <summary>
    ///     What a path scope was called before, or null where nothing leads back. Read as one row per
    ///     hop, oldest last, from <c>path_lineage</c> — the chain the build walked (#148):
    ///     <c>src/Model</c> ← <c>model</c> ← <c>Model</c> arrives as three rows rather than as three
    ///     round trips discovering each other. It used to be discovered here, and cost two scans of
    ///     <c>commit_files</c> per hop and a combined count at the end — up to seventeen of the largest
    ///     history table for one scoped answer, every time the same question was asked of the same
    ///     index.
    ///     The hop cap and the rename-ring guard are the build's, so nothing here has to bound a walk
    ///     it no longer performs; <see cref="MaxPreviousPaths" /> is this side's, because it is about
    ///     what a reply prints and not about what is true.
    ///     Signalled and not followed (#131): nothing here changes what the scope's own query counts.
    ///     The combined total covers the whole chain, including the hops the cap does not name — it is
    ///     the number the caller wanted, and naming fewer paths must not shrink it.
    /// </summary>
    /// <param name="index">The index being read.</param>
    /// <param name="repositorySlug">The repository the scope resolved to. A rename never crosses one.</param>
    /// <param name="pathInRepository">The scope inside it. Empty — a repository root — has no previous path.</param>
    /// <param name="cancellationToken">Threaded to the command.</param>
    private static async Task<PathLineage?> LineageAsync(IndexReader index, string repositorySlug,
        string pathInRepository, CancellationToken cancellationToken)
    {
        if (pathInRepository.Length == 0) return null;

        // Each previous path is printed as a call to make, so it is spelled the way the project names
        // a file (ADR-0006); every caller wanted that spelling and none another.
        var paths = await index.PathsAsync(cancellationToken);

        using var command = index.Connection.Query("""
                                                   SELECT previous_path, previous_commits, combined_commits
                                                   FROM path_lineage
                                                   WHERE repo_slug = $r AND path = $p
                                                   ORDER BY hop
                                                   """,
            [new DuckDBParameter("r", repositorySlug), new DuckDBParameter("p", pathInRepository)]);
        using var reader = await command.ReaderAsync(cancellationToken);
        var found = new List<PreviousPath>();
        // The same on every row of a scope, so the last read wins and none has to be singled out.
        int combined = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            string previous = reader.Text("previous_path");
            found.Add(new PreviousPath(paths.Format(repositorySlug, previous), previous, reader.Int32("previous_commits")));
            combined = reader.Int32("combined_commits");
        }

        if (found.Count == 0) return null;
        return new PathLineage(found.Take(MaxPreviousPaths).ToList(),
            Math.Max(0, found.Count - MaxPreviousPaths), combined,
            found.Select(previous => previous.PathInRepository).ToList());
    }

    /// <summary>
    ///     One bound parameter per path, and the <c>$name</c> each of them got, in the order the paths
    ///     came in. The queries here that take a rename chain — a handful of paths at most, since the
    ///     build caps it — had each written the loop out for itself; a path spelled into the SQL
    ///     instead of bound is how user text reaches the parser, so the loop is written once.
    /// </summary>
    /// <param name="parameters">The list being built, which the names are added to.</param>
    /// <param name="paths">The paths to bind.</param>
    private static List<string> BindPaths(List<DuckDBParameter> parameters, IReadOnlyList<string> paths)
    {
        var names = new List<string>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            string name = "p" + i.ToString(CultureInfo.InvariantCulture);
            parameters.Add(new DuckDBParameter(name, paths[i]));
            names.Add("$" + name);
        }

        return names;
    }

}
