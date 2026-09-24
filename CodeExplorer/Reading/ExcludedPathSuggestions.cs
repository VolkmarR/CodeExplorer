using System.Globalization;
using System.Text.Json.Serialization;
using DuckDB.NET.Data;

namespace CodeExplorer.Reading;

/// <summary>Which of the rules in <see cref="ExcludedPathSuggestions" /> proposed a pattern.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SuggestionRule>))]
public enum SuggestionRule
{
    GitAttributes,
    WellKnownName,
    History
}

/// <summary>
///     One pattern proposed for the excluded paths (#217), with the rule that proposed it, the
///     sentence saying why, and how many files at HEAD it matches, so the operator can judge it.
/// </summary>
public sealed record ExcludedPathSuggestion(string Pattern, SuggestionRule Rule, string Reason, int Files);

/// <summary>
///     Patterns for a project's excluded paths proposed from its own index (#217). The rules run in
///     order — what a <c>.gitattributes</c> declares, well-known generated names, then files the
///     history only ever bumps — and a pattern is offered only where it matches a file at HEAD that
///     neither the setting nor an earlier suggestion already leaves out. Nothing here stores anything:
///     the operator keeps or drops each one in the form, and a save is still theirs.
/// </summary>
internal static class ExcludedPathSuggestions
{
    /// <summary>
    ///     How many commits of the window a file must be changed by before its history marks it as a
    ///     bump. A version file is touched by every release, which in a quarter is well past ten; an
    ///     ordinary small file touched ten times is being worked on, and its changes are rarely all tiny.
    /// </summary>
    public const int MinCommits = 10;

    /// <summary>
    ///     The most lines, added plus deleted, any one of those commits may change. A version bump
    ///     replaces a line or two (Radix's <c>AssemblyInfo.prg</c>: 41 commits, +61/−61); four allows
    ///     a file carrying two version numbers and still refuses any commit that edits code.
    /// </summary>
    public const int MaxLinesPerCommit = 4;

    /// <summary>
    ///     Names generated or locked by a tool on every run, which no one reads in a dashboard. Each is
    ///     offered only where the index holds a file matching it.
    /// </summary>
    private static readonly string[] _wellKnown =
    [
        "**/*.Designer.cs", "**/Connected Services/**/Reference.cs", "**/AssemblyInfo.*", "**/*.verified.txt",
        "**/*.received.txt", "**/*.min.js", "**/package-lock.json", "**/pnpm-lock.yaml", "**/yarn.lock",
        "**/packages.lock.json"
    ];

    /// <summary>The attributes linguist reads as "not written here", in the words the reason uses.</summary>
    private static readonly string[] _linguist = ["linguist-generated", "linguist-vendored"];

    /// <param name="connection">Bound to the live index.</param>
    /// <param name="paths">How this project names its files (ADR-0006).</param>
    /// <param name="existing">The project's setting, whose patterns and files are not offered again.</param>
    /// <param name="cancellationToken">Threaded through every statement.</param>
    public static async Task<IReadOnlyList<ExcludedPathSuggestion>> SuggestAsync(DuckDBConnection connection,
        ProjectPaths paths, IReadOnlyList<string> existing, CancellationToken cancellationToken)
    {
        var accepted = new List<ExcludedPathSuggestion>();
        var candidates = new List<(string Pattern, SuggestionRule Rule, string Reason)>();
        candidates.AddRange(await GitAttributesAsync(connection, cancellationToken));
        candidates.AddRange(_wellKnown.Select(p => (p, SuggestionRule.WellKnownName, "A well-known generated name")));
        foreach (var candidate in candidates)
            await OfferAsync(connection, existing, accepted, candidate, cancellationToken);

        // Last, and read after the others are accepted, so a bump they already cover is not proposed.
        foreach (var candidate in await HistoryAsync(connection, paths, Covering(existing, accepted), cancellationToken))
            await OfferAsync(connection, existing, accepted, candidate, cancellationToken);
        return accepted;
    }

    /// <summary>
    ///     Adds <paramref name="candidate" /> where it is not already written and matches a file at HEAD
    ///     nothing before it leaves out. The count shown is every file it matches, covered or not,
    ///     because that is what the pattern will exclude once saved.
    /// </summary>
    private static async Task OfferAsync(DuckDBConnection connection, IReadOnlyList<string> existing,
        List<ExcludedPathSuggestion> accepted, (string Pattern, SuggestionRule Rule, string Reason) candidate,
        CancellationToken cancellationToken)
    {
        if (existing.Concat(accepted.Select(a => a.Pattern))
            .Contains(candidate.Pattern, StringComparer.OrdinalIgnoreCase)) return;

        var parameters = new List<DuckDBParameter>();
        string matching = new ExcludedPaths([candidate.Pattern]).Matching("qualified_path", "p", parameters)!;
        string covered = Covering(existing, accepted).Matching("qualified_path", "c", parameters) is { } c
            ? $"NOT {c}"
            : "true";
        await using var command = connection.Query($"""
                                                    SELECT count(*)::INTEGER AS files,
                                                           count(*) FILTER (WHERE {covered})::INTEGER AS fresh
                                                    FROM files WHERE {matching}
                                                    """, parameters);
        await using var reader = await command.ReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        if (reader.Int32("fresh") > 0)
            accepted.Add(new ExcludedPathSuggestion(candidate.Pattern, candidate.Rule, candidate.Reason,
                reader.Int32("files")));
    }

    /// <summary>What is left out already: the setting and every suggestion accepted so far.</summary>
    private static ExcludedPaths Covering(IReadOnlyList<string> existing, List<ExcludedPathSuggestion> accepted) =>
        new([.. existing, .. accepted.Select(a => a.Pattern)]);

    /// <summary>
    ///     The patterns every <c>.gitattributes</c> at HEAD marks <c>linguist-generated</c> or
    ///     <c>linguist-vendored</c>, rewritten as qualified-path globs. Git reads a pattern without a
    ///     slash at any depth below the file's folder and one with a slash relative to it, so the first
    ///     becomes <c>folder/**/pattern</c> and the second <c>folder/pattern</c>. An attribute unset
    ///     (<c>-</c>), unspecified (<c>!</c>) or set to false marks nothing, and a negated or quoted
    ///     pattern is left alone rather than half understood.
    /// </summary>
    private static async Task<List<(string, SuggestionRule, string)>> GitAttributesAsync(
        DuckDBConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.Query("""
                                                   SELECT f.qualified_path, f.name, l.content
                                                   FROM files f JOIN lines l USING (file_id)
                                                   WHERE lower(f.name) = '.gitattributes' AND f.skip_reason IS NULL
                                                   ORDER BY f.qualified_path, l.line_number
                                                   """, []);
        await using var reader = await command.ReaderAsync(cancellationToken);
        var patterns = new List<(string, SuggestionRule, string)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            string file = reader.Text("qualified_path");
            string[] tokens = reader.Text("content").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || tokens[0].StartsWith('#') || tokens[0].StartsWith('!')
                || tokens[0].StartsWith('"')) continue;
            if (tokens.Skip(1).Select(Marked).FirstOrDefault(a => a is not null) is not { } attribute) continue;

            string folder = file[..^reader.Text("name").Length];
            string pattern = tokens[0].TrimEnd('/');
            string glob = pattern.Contains('/', StringComparison.Ordinal)
                ? folder + pattern.TrimStart('/')
                : folder + "**/" + pattern;
            patterns.Add((glob, SuggestionRule.GitAttributes, $"{attribute} in {file}"));
        }

        return patterns;
    }

    /// <summary>The linguist attribute <paramref name="token" /> sets, or null where it sets none.</summary>
    private static string? Marked(string token)
    {
        int equals = token.IndexOf('=', StringComparison.Ordinal);
        string name = equals < 0 ? token : token[..equals];
        bool set = equals < 0 || token[(equals + 1)..].Equals("true", StringComparison.OrdinalIgnoreCase);
        return set ? _linguist.FirstOrDefault(l => l.Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
    }

    /// <summary>
    ///     The files at HEAD changed in at least <see cref="MinCommits" /> commits of the default window,
    ///     none of them by more than <see cref="MaxLinesPerCommit" /> lines, and not already
    ///     <paramref name="covered" />. Where every file at HEAD with that name is such a file, the name
    ///     is proposed — the <c>AssemblyInfo</c> of every project in a solution — and otherwise each
    ///     path, so a common name one of whose copies is a bump does not take the others with it.
    /// </summary>
    private static async Task<List<(string, SuggestionRule, string)>> HistoryAsync(DuckDBConnection connection,
        ProjectPaths paths, ExcludedPaths covered, CancellationToken cancellationToken)
    {
        if (await IndexQueries.WindowAsync(connection, HistoryWindow.DefaultDays, null, cancellationToken) is not
            { } window) return [];

        var (inWindow, parameters) = IndexQueries.ChurnScope(paths, window, null, null, ChurnFilters.None);
        parameters.Add(new DuckDBParameter("min", MinCommits));
        parameters.Add(new DuckDBParameter("most", MaxLinesPerCommit));
        var conditions = new List<string> { "f.skip_reason IS NULL" };
        if (covered.Matching("f.qualified_path", "c", parameters) is { } matching) conditions.Add($"NOT {matching}");
        await using var command = connection.Query($"""
                                                    WITH bumped AS (
                                                        SELECT c.repo_slug, cf.path, count(*)::INTEGER AS commits,
                                                               max(cf.added + cf.deleted)::INTEGER AS most
                                                        FROM commit_files cf
                                                        JOIN commits c USING (commit_id)
                                                        WHERE {string.Join(" AND ", inWindow)}
                                                        GROUP BY c.repo_slug, cf.path
                                                        HAVING count(*) >= $min AND max(cf.added + cf.deleted) <= $most),
                                                    candidates AS (
                                                        SELECT f.qualified_path, f.name, b.commits, b.most
                                                        FROM bumped b
                                                        JOIN repositories r ON r.slug = b.repo_slug
                                                        JOIN files f ON f.repo_id = r.repo_id AND f.path = b.path
                                                        {OverviewQueries.Where(conditions)})
                                                    SELECT c.*, (SELECT count(*) FROM files n WHERE lower(n.name) = lower(c.name))::INTEGER AS named
                                                    FROM candidates c
                                                    ORDER BY c.commits DESC, c.qualified_path
                                                    """, parameters);
        await using var reader = await command.ReaderAsync(cancellationToken);
        var rows = new List<(string Path, string Name, int Commits, int Most, int Named)>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((reader.Text("qualified_path"), reader.Text("name"), reader.Int32("commits"),
                reader.Int32("most"), reader.Int32("named")));

        var patterns = new List<(string, SuggestionRule, string)>();
        // A name holding a glob character would mean something else as a pattern; there is no escape.
        foreach (var group in rows.Where(r => r.Path.IndexOfAny(['*', '?', '[']) < 0)
                     .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var files = group.ToList();
            if (files.Count == files[0].Named)
                patterns.Add(($"**/{group.Key}", SuggestionRule.History,
                    Reason(files.Max(f => f.Commits), files.Max(f => f.Most), window)));
            else
                patterns.AddRange(files.Select(f =>
                    (f.Path, SuggestionRule.History, Reason(f.Commits, f.Most, window))));
        }

        return patterns;
    }

    private static string Reason(int commits, int most, HistoryWindow window) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Changed in {commits} commits of the last {window.Days} days, at most {most} {(most == 1 ? "line" : "lines")} each");
}
