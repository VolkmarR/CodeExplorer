using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     Why a symbol search was not in a position to read a file. Two reasons and not one, because
///     what follows from them differs: the first was read with shapes that may not fit, the second
///     was not read at all. <c>list_declarations</c> keeps the same two apart as
///     <see cref="DeclarationCoverage.Unprofiled" /> and <see cref="DeclarationCoverage.Unreadable" />
///     (#129), and a reply that folded them together would promise a scan that never ran.
/// </summary>
public enum CoverageGap
{
    /// <summary>No profile covers the extension, so the conservative default shapes read it.</summary>
    Unprofiled,

    /// <summary>
    ///     A profile covers it and declares no declaration shapes — HTML and CSS — so the search
    ///     asked the engine for none of its lines and nothing there was scanned.
    /// </summary>
    Unreadable
}

/// <summary>
///     One language a symbol search met and could not read, why, and how many of the files holding
///     the name are written in it. <see cref="Language" /> is spelled the way a reply names one —
///     <c>HTML</c> for a profiled language, <c>.py</c> or <see cref="Languages.NoExtension" /> for an
///     extension no profile covers — so the note reads like the one <c>imports</c> and
///     <c>list_declarations</c> already print for the same fact.
/// </summary>
public sealed record UncoveredFiles(string Language, int Files, CoverageGap Gap);

/// <summary>
///     How much of what a symbol search looked at it was in a position to read (#126, #129). `imports`,
///     and `list_declarations` each say outright when a file's extension has no profile,
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
    ///     How many languages a note names before it becomes a list. Four is the point past which the
    ///     reply is reporting the project's file types rather than a caveat about this answer; the
    ///     rest are counted, because how many there are is the part that still matters.
    ///     Per reason and not per note, so a reply that has both to report does not spend its whole
    ///     budget on the first: the unreadable clause can name at most the profiled languages with no
    ///     declaration shapes, which is two, so the pair cannot run away.
    /// </summary>
    public const int MaxExtensionsNamed = 4;

    /// <summary>
    ///     The languages a search could not read among the files its own predicate matches, most files
    ///     first. Empty where every extension in the project either has a profile with shapes or is
    ///     not code at all, which is the cheap answer and the common one: the extensions are read from
    ///     <c>files</c> first — a small table, and the same read <see cref="DefinitionSearch" />
    ///     already makes to build its declaration shapes — so a project whose code this build profiles
    ///     never reaches the count over <c>lines</c>. A project that really does hold a language with
    ///     no profile pays one aggregate for the fact, which is the fact it most needs.
    /// </summary>
    /// <param name="connection">The index, already bound to its project.</param>
    /// <param name="matchPredicate">What the search counts as a match, against the <c>lines</c> alias <c>l</c>.</param>
    /// <param name="fileFilter">The caller's <see cref="FileFilter" /> tail, against the <c>files</c> alias <c>f</c>.</param>
    /// <param name="parameters">Everything those two spell; copied, never appended to.</param>
    /// <param name="unreadable">
    ///     The extensions whose profile asked the search for no lines at all, as the caller that built
    ///     its shapes already knows them (#129). Empty for a search that reads every file it matches
    ///     whatever its language — <c>find_references</c> classifies an occurrence either way, so a
    ///     shapeless profile costs it nothing and it has nothing to report here.
    /// </param>
    /// <param name="cancellationToken">Threaded to both commands, as every read here is.</param>
    public static async Task<IReadOnlyList<UncoveredFiles>> OfMatchesAsync(DuckDBConnection connection,
        string matchPredicate, string fileFilter, IReadOnlyList<DuckDBParameter> parameters,
        IReadOnlyCollection<string> unreadable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unreadable);
        var gaps = new Dictionary<string, CoverageGap>(StringComparer.Ordinal);
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
                if (!Languages.MightHoldCode(extension)) continue;
                // The shapeless case is asked first: those extensions are profiled, so the test below
                // would pass over them, and they are the ones nothing was read from at all.
                if (unreadable.Contains(extension)) gaps[extension] = CoverageGap.Unreadable;
                else if (!Languages.Name(extension).Mapped) gaps[extension] = CoverageGap.Unprofiled;
            }
        }

        if (gaps.Count == 0) return [];

        // The caller's parameters plus this query's own. A copy because the caller goes on using its
        // list for the queries after this one, and a name bound here would ride along into them.
        var counted = new List<DuckDBParameter>(parameters);
        var names = new List<string>();
        foreach (string extension in gaps.Keys)
        {
            string name = $"u{counted.Count}";
            counted.Add(new DuckDBParameter(name, extension));
            names.Add($"${name}");
        }

        // The extension test is written first so the planner can drop the files that cannot
        // contribute before the line predicate is evaluated over them: the question here is only
        // which of these extensions hold the name, never how often.
        var found = new List<(string Language, int Files, CoverageGap Gap)>();
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
            {
                string extension = reader.Text("extension");
                found.Add((Languages.Name(extension).Name, reader.Int32("files"), gaps[extension]));
            }
        }

        // Folded by the name a reply prints and not by the extension it was counted under: `.html`
        // and `.htm` are one language and would otherwise be named twice in one sentence, each with
        // half the count. The unprofiled half names an extension, so folding it changes nothing.
        return
        [
            .. found
                .GroupBy(file => (file.Language, file.Gap))
                .Select(language => new UncoveredFiles(language.Key.Language, language.Sum(file => file.Files),
                    language.Key.Gap))
                .OrderByDescending(language => language.Files)
                .ThenBy(language => language.Language, StringComparer.Ordinal)
        ];
    }
}
