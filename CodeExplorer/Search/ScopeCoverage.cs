using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     One extension a symbol search met that no language profile covers, and how many of the files
///     holding the name carry it. <see cref="Extension" /> is spelled the way a reply names a language
///     — <c>.py</c>, or <see cref="Languages.NoExtension" /> — so the note reads like the one
///     <c>imports</c> and <c>list_declarations</c> already print for the same fact.
/// </summary>
public sealed record UnprofiledFiles(string Extension, int Files);

/// <summary>
///     How much of what a symbol search looked at it was in a position to read (#126). `imports`,
///     `who_imports` and `list_declarations` each say outright when a file's extension has no profile,
///     so their empty answers cannot be read as "this file has none of what you asked about";
///     `find_definition` and `find_references` had no such signal, and they are the two tools an agent
///     reaches for most — the place where mistaking "not searched the way this language writes it" for
///     "not there" is most expensive.
///     Counted over the files that hold the name and not over the whole scope, which is the difference
///     between a note that fires when it matters and one every answer carries. A project with
///     Markdown, JSON and YAML in it would earn the second on every call, and a note an agent has
///     learned to skip is the same as no note at all (CODING_STANDARDS, Comments).
/// </summary>
internal static class ScopeCoverage
{
    /// <summary>
    ///     How many unprofiled extensions a note names before it becomes a list. Four is the point
    ///     past which the reply is reporting the project's file types rather than a caveat about this
    ///     answer; the rest are counted, because how many there are is the part that still matters.
    /// </summary>
    public const int MaxExtensionsNamed = 4;

    /// <summary>
    ///     The unprofiled extensions among the files a search's own predicate matches, most files
    ///     first. Empty where every extension in the project either has a profile or is not code at
    ///     all, which is the cheap answer and the common one: the extensions are read from
    ///     <c>files</c> first — a small table, and the same read <see cref="DefinitionSearch" />
    ///     already makes to build its declaration shapes — so a project whose code this build profiles
    ///     never reaches the count over <c>lines</c>. A project that really does hold a language with
    ///     no profile pays one aggregate for the fact, which is the fact it most needs.
    /// </summary>
    /// <param name="connection">The index, already bound to its project.</param>
    /// <param name="matchPredicate">What the search counts as a match, against the <c>lines</c> alias <c>l</c>.</param>
    /// <param name="fileFilter">The caller's <see cref="FileFilter" /> tail, against the <c>files</c> alias <c>f</c>.</param>
    /// <param name="parameters">Everything those two spell; copied, never appended to.</param>
    /// <param name="cancellationToken">Threaded to both commands, as every read here is.</param>
    public static async Task<IReadOnlyList<UnprofiledFiles>> OfMatchesAsync(DuckDBConnection connection,
        string matchPredicate, string fileFilter, IReadOnlyList<DuckDBParameter> parameters,
        CancellationToken cancellationToken)
    {
        var unprofiled = new List<string>();
        using (var command = connection.Query("SELECT DISTINCT extension FROM files", []))
        using (var reader = await command.ReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                string extension = reader.Text("extension");
                // Prose, data and build metadata are skipped rather than reported: a README that
                // mentions the name is not a file this failed to read, and a caveat about Markdown on
                // every reply is what teaches an agent to skip the caveat that matters. It is also
                // what keeps the count below off a well-profiled project entirely.
                if (Languages.MightHoldCode(extension) && !Languages.Name(extension).Mapped)
                    unprofiled.Add(extension);
            }
        }

        if (unprofiled.Count == 0) return [];

        // The caller's parameters plus this query's own. A copy because the caller goes on using its
        // list for the queries after this one, and a name bound here would ride along into them.
        var counted = new List<DuckDBParameter>(parameters);
        var names = new List<string>();
        foreach (string extension in unprofiled)
        {
            string name = $"u{counted.Count}";
            counted.Add(new DuckDBParameter(name, extension));
            names.Add($"${name}");
        }

        // The extension test is written first so the planner can drop the files that cannot
        // contribute before the line predicate is evaluated over them: the question here is only
        // which unprofiled extensions hold the name, never how often.
        var found = new List<UnprofiledFiles>();
        using (var command = connection.Query($"""
                                               SELECT f.extension, count(DISTINCT l.file_id)::INTEGER AS files
                                               FROM lines l JOIN files f USING (file_id)
                                               WHERE f.extension IN ({string.Join(", ", names)}){fileFilter}
                                                 AND {matchPredicate}
                                               GROUP BY f.extension
                                               ORDER BY files DESC, f.extension
                                               """, counted))
        using (var reader = await command.ReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                found.Add(new UnprofiledFiles(Languages.Name(reader.Text("extension")).Name,
                    reader.Int32("files")));
        }

        return found;
    }
}
