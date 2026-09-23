using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;

namespace CodeExplorer;

/// <summary>
///     <c>find_definition</c>, which answers with declarations (CONTEXT.md, Declaration), and
///     <c>list_matches</c>, which answers with what a pattern extracted. Both read a line's shape
///     rather than its text alone, and both say so in the same words.
/// </summary>
internal sealed partial class SearchTools
{
    [McpServerTool(Name = "find_definition", ReadOnly = true, Idempotent = true,
        Title = "Find where a symbol is declared")]
    [Description("""
                 Finds where a symbol is DECLARED — the class, method, function, procedure or type of that name — with its qualified path, line number and the declaring line. Use it when you want the definition; use find_references when you want the uses.

                 - Delphi and PL/SQL declare a routine twice: announced in the `interface` section or the package spec, written in `implementation` or the package body, usually in two different files. Both are listed, each labelled, and IMPLEMENTATIONS come first because the body is almost always what was wanted. C#, X#, TypeScript and JavaScript declare once, and the answer simply says so.
                 - IMPORTANT: this is a heuristic over text, not a compiler. It knows the declaration forms its language profile knows, so a form it does not know is a miss and not proof there is none. A miss degrades to find_references and grep, and the reply says so rather than pretending the symbol has no declaration.
                 - The name is matched whole and case-sensitively, exactly as find_references matches it.
                 - A name declared more than once — an overload, a partial class, the same name on two unrelated types — returns every site, ranked.
                 - Where files holding the name are written in a language no profile covers, the reply names those extensions and how many files they are, because a declaration written the way that language writes one is not searched for at all there — the answer is silent about those files, not negative about them.
                 - Scope with `repo`, `path`, `ext` and `exclude` exactly as grep does.
                 """)]
    public async Task<string> FindDefinition(
        [Description(
            "The symbol to look for, e.g. \"OrderService\" or \"UpdateDeliveryNoteStatus\". One name, matched whole and case-sensitively.")]
        string symbol,
        [Description("Repository slug to scope to. Default: every repository in the project.")]
        string? repo = null,
        [Description(
            "Only look in files whose qualified path matches; comma-separated terms are OR-ed. Same syntax as grep.")]
        string? path = null,
        [Description(
            "Skip files whose qualified path matches any of these comma-separated terms, e.g. \"*.g.cs,/tests/\".")]
        string? exclude = null,
        [Description("Only look in files with this extension, without the dot, e.g. \"pas\".")]
        string? ext = null,
        CancellationToken cancellationToken = default)
    {
        var request = new DefinitionRequest(symbol, new FileFilter(repo, path, exclude, ext));

        return ToolReply.Render<DefinitionResult>(await definitions.FindAsync(Bound.Slug, request, cancellationToken),
            result => result.Sites.Count == 0
                ? NoDefinition(symbol.Trim(), result)
                : FormatDefinitions(symbol.Trim(), result),
            "Narrow with repo/path/ext/exclude.");
    }

    /// <summary>
    ///     What to say when nothing declared it. Never an empty list: "no declaration" and "no such
    ///     name" are different answers and send an agent to different tools, and a declaration search
    ///     that knows only the forms its profiles know must say which of the two this is.
    /// </summary>
    private static string NoDefinition(string symbol, DefinitionResult result)
    {
        var text = new StringBuilder();
        if (result.FilesNamingIt == 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"Nothing in this project spells \"{symbol}\". ");
            text.Append(result.FilesNamingItWithoutFilters > 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $"It does appear in {ToolReply.HiddenByFilters(result.FilesNamingItWithoutFilters.Value)}")
                : "The name is matched whole and case-sensitively, so check the spelling and the case. grep with regex=true finds a partial name.");
            return text.ToString();
        }

        text.Append(CultureInfo.InvariantCulture,
            $"No declaration of \"{symbol}\" was recognised, though the name appears in {result.FilesNamingIt} {ToolReply.Plural(result.FilesNamingIt, "file")}. ");
        if (result.FilesNamingItWithoutFilters > result.FilesNamingIt)
            text.Append(CultureInfo.InvariantCulture,
                $"{ToolReply.PartlyHiddenByFilters(result.FilesNamingItWithoutFilters.Value - result.FilesNamingIt)} ");
        text.Append(CultureInfo.InvariantCulture,
            $"This reads the declaration forms it knows, so a form it does not know is a miss and not proof there is none — the symbol may also be declared in a language this indexes without profiling, or generated rather than written. Run find_references(symbol=\"{symbol}\") and read its DECLARATIONS section, or grep for it.");
        // Which of those two a miss actually is, where the index can say: "a language this indexes
        // without profiling" is a possibility in the sentence above and a fact here, named with the
        // files it applies to (#126) — and beside it the files nothing was read from at all (#129).
        if (CoverageNote(result.Uncovered, DefinitionCost) is { Length: > 0 } uncovered)
            text.Append('\n').Append(uncovered);
        return text.ToString();
    }

    /// <summary>
    ///     What a gap in coverage costs a declaration answer. Stronger than the reference tool's
    ///     clause and deliberately so: a declaration is found by matching the shape of the line, so a
    ///     language whose shapes were never registered can hide one completely, where a reference in
    ///     the same file is still found and only its label is a guess.
    ///     One clause for both reasons, in words that fit a file read with the wrong shapes and a file
    ///     read with none: which of the two it was is the note's own sentence, and what to do about it
    ///     is the same either way.
    /// </summary>
    private const string DefinitionCost =
        "A declaration written the way those languages write one is not in this answer at all, "
        + "so it is silent about those files rather than negative about them: grep the name "
        + "there, or read one of them to see how the language declares things.";

    private static string FormatDefinitions(string symbol, DefinitionResult result)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"\"{symbol}\" is declared in {result.TotalSites} {ToolReply.Plural(result.TotalSites, "place")}");
        text.Append(result.TotalSites > result.Sites.Count
            ? string.Create(CultureInfo.InvariantCulture,
                $"; showing the first {result.Sites.Count}. The name is declared too often to enumerate — narrow with repo/path/ext/exclude.\n")
            : ".\n");

        // Above the sites, where a reply that found something is most likely to be read as the whole
        // of what there is: an answer of three declarations can still be missing the one written in a
        // language no profile covers (#126) or in one nothing was scanned from (#129).
        text.Append(CoverageNote(result.Uncovered, DefinitionCost));

        // Headings where the language draws the distinction they name, and none where it does not.
        // Whether this ANSWER holds both kinds decides nothing: a Delphi routine found only in its
        // implementation section is still an implementation, and printing it unlabelled because
        // nothing else turned up would make one language answer two ways. For C#, X#, TypeScript and
        // JavaScript the two coincide, and a pair of headings there would be a split invented for the
        // sake of one.
        if (result.Separated)
        {
            text.Append(
                "This language announces a routine and writes it elsewhere; the body comes first below.\n");
            Section("IMPLEMENTATIONS", DeclarationRole.Implementation);
            Section("DECLARATIONS", DeclarationRole.Declaration);
            // Not folded into DECLARATIONS: a line the scan could not place is exactly what the
            // analyser refuses to call a declaration, and a heading would restore the guess it refused.
            Section("SECTION UNKNOWN", null);
        }
        else
        {
            Section(null, null);
        }

        text.Append(
            "\nDeclaration forms are read from line shape, not from a compiler, so an unrelated symbol of the same name is included and a form this does not know is missing. Strong evidence, not proof.\n");
        return text.ToString();

        void Section(string? title, DeclarationRole? role)
        {
            var sites = title is null ? result.Sites : result.Sites.Where(site => site.Role == role).ToList();
            if (sites.Count == 0) return;
            if (title is not null)
                text.Append(CultureInfo.InvariantCulture, $"\n{title}  ({sites.Count})\n");
            else text.Append('\n');
            // The type only where it is not the thing being declared, so a class does not read as
            // declaring itself.
            AppendHits(text,
                sites.Select(site => (site.QualifiedPath, site.LineNumber,
                    site.Type is null || site.Type == symbol ? null : site.Type, site.Text)));
        }
    }

    /// <summary>
    ///     The lines a search is showing, grouped by file under a two-space gutter, each with its line
    ///     number right-aligned within its file and an optional bracketed label. One copy, because two
    ///     tools printing hits two ways is the drift <see cref="ToolReply" /> exists to stop — a change
    ///     to the gutter or the clip must not reach one tool and not the other.
    /// </summary>
    private static void AppendHits(StringBuilder text,
        IEnumerable<(string Path, int LineNumber, string? Label, string Text)> hits)
    {
        foreach (var file in hits.GroupBy(hit => hit.Path))
        {
            text.Append("  ").Append(file.Key).Append('\n');
            int width = file.Max(hit => hit.LineNumber.ToString(CultureInfo.InvariantCulture).Length);
            foreach (var hit in file)
            {
                text.Append("  ")
                    .Append(hit.LineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(width))
                    .Append(": ")
                    .Append(hit.Label is null ? "" : $"[{hit.Label}] ");
                // Clipped first and trimmed at the start after, as the string form did: the clip counts
                // the indentation, so trimming it first would move the cut and the count it reports.
                int from = text.Length;
                ToolReply.Clip(text, hit.Text);
                int indent = 0;
                while (from + indent < text.Length && char.IsWhiteSpace(text[from + indent])) indent++;
                text.Remove(from, indent).Append('\n');
            }
        }
    }

    [McpServerTool(Name = "list_matches", ReadOnly = true, Idempotent = true,
        Title = "List the distinct values a pattern matches")]
    [Description("""
                 Returns the DISTINCT strings an RE2 pattern matches across every repository in this project, each with how often and in how many files it occurs — the indexed equivalent of `grep -o … | sort | uniq -c`.

                 Use it whenever the question is "what values exist?" rather than "where is this?". It answers in one call what otherwise takes a grep plus reading dozens of files:

                 - What packages does this project depend on? query="PackageReference Include=\"([^\"]+)\"", group=1
                 - Which projects are in a solution? query="([^\"\\]+\.csproj)", group=1
                 - Every distinct status constant, HTTP route, config key, error code, table name or imported namespace — anything spelled out in many files that you want deduplicated.

                 - Always a pattern; there is nothing to deduplicate about a literal. `group=1` returns the first capture group instead of the whole match, which is usually what you want: put the parentheses around the part that varies and leave the boilerplate outside them.
                 - **A group aimed at the wrong parenthesis returns a tidy, confident, wrong list.** Nothing about the reply can tell you that `catch (\w+) (\w+)` with group=1 counted exception types where you asked for variable names — the values are well-formed, just not the ones you wanted. Read the top few and check they look like the kind of thing you asked for; `group=0` shows the whole match, which is where a mis-aimed group becomes obvious.
                 - Results are ordered by frequency, so the common cases come first and a one-off outlier is visible at the bottom. `count` is how often the value was matched and `files` is how many files those matches came from; the two differ where a value repeats within one file.
                 - Prefer grep when you need to see WHERE something appears. This is also the cheap way to learn a code base's vocabulary before searching it: list the distinct status constants first, then grep for the one you want.
                 - "No matches" replies say whether the pattern matched outside your filters, so a filtered miss is never mistaken for a pattern that matches nothing.
                 """)]
    public async Task<string> ListMatches(
        [Description(
            "RE2 regular expression. Put parentheses around the part you want and pass group=1. RE2 has no lookaround and no backreferences.")]
        string query,
        [Description(
            "Return this capture group instead of the whole match. 1 is usually what you want; 0 is the whole match.")]
        int group = 0,
        [Description("Match case exactly. Default false.")]
        bool caseSensitive = false,
        [Description("Repository slug to scope to. Default: every repository in the project.")]
        string? repo = null,
        [Description(
            "Only search files whose qualified path matches; comma-separated terms are OR-ed. Same syntax as grep.")]
        string? path = null,
        [Description(
            "Skip files whose qualified path matches any of these comma-separated terms, e.g. \"*.g.cs,/tests/\".")]
        string? exclude = null,
        [Description("Only search files with this extension, without the dot, e.g. \"csproj\" or \"cs\".")]
        string? ext = null,
        [Description("Only match whole words, by anchoring the pattern on word boundaries.")]
        bool wholeWord = false,
        [Description("Maximum distinct values to return, 1-1000. Default 200, ordered by frequency.")]
        int limit = MatchList.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        var request = new MatchListRequest(query, new FileFilter(repo, path, exclude, ext), group, caseSensitive,
            wholeWord, limit);

        return ToolReply.Render<MatchListResult>(await matches.ListAsync(Bound.Slug, request, cancellationToken),
            result => result.TotalDistinct == 0 ? NoValues(request, result) : FormatValues(result),
            "Lower limit, or narrow with repo/path/ext/exclude.");
    }

    private static string NoValues(MatchListRequest request, MatchListResult result)
    {
        var text = new StringBuilder($"No matches for \"{request.Query}\". ");
        // An empty capture value is dropped before the counting, so a pattern that matched every line with
        // an empty group is indistinguishable here from one that matched nothing. The miss is stated as
        // both and neither as certain: naming the group alone sends the caller off fixing a parenthesis
        // that was never wrong, and naming the pattern alone denies a match that may have happened.
        string miss = string.Create(CultureInfo.InvariantCulture,
            $"The pattern matched nothing{(request.Group > 0 ? $", or matched but capture group {request.Group} was always empty" : "")}");
        text.Append(FilterVerdict(result.FilesMatchingWithoutFilters, "pattern", miss));
        if (result.FilesMatchingWithoutFilters is not > 0)
            text.Append("Try the same pattern with grep to see whether it matches at all.");
        return text.ToString();
    }

    private static string FormatValues(MatchListResult result)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
                $"{result.TotalDistinct} distinct {ToolReply.Plural(result.TotalDistinct, "value")} from {result.TotalMatches} {ToolReply.Plural(result.TotalMatches, "match", "matches")} in {result.TotalFiles} {ToolReply.Plural(result.TotalFiles, "file")}")
            .Append(result.Matches.Count < result.TotalDistinct
                ? string.Create(CultureInfo.InvariantCulture,
                    $"; showing the {result.Matches.Count} most frequent (raise limit for the rest)\n")
                : ", all shown\n")
            .Append("\ncount  files  value\n");

        foreach (var match in result.Matches)
        {
            text.Append(CultureInfo.InvariantCulture, $"{match.Count,5}  {match.Files,5}  ");
            ToolReply.Clip(text, match.Value.AsSpan().TrimStart()).Append('\n');
        }

        return text.ToString();
    }

}
