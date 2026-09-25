using System.ComponentModel;
using System.Globalization;
using System.Text;
using CodeExplorer.Infrastructure;
using CodeExplorer.Reading;
using ModelContextProtocol.Server;

namespace CodeExplorer.Search;

/// <summary>The MCP tools that answer from a project's index (ADR-0005, <c>Search/</c>).</summary>
[McpServerToolType]
internal sealed partial class SearchTools(
    IHttpContextAccessor httpContextAccessor,
    GrepSearch grep,
    ReferenceSearch references,
    DefinitionSearch definitions,
    MatchList matches)
{
    /// <summary>The project this call is bound to, read the way every tool class here reads it.</summary>
    private Project Bound => BoundProject.Get(httpContextAccessor);

    [McpServerTool(Name = "grep", ReadOnly = true, Idempotent = true, Title = "Search the project's code")]
    [Description("""
                 Searches every indexed line of every repository in this project and returns the matching lines grouped by file, with qualified paths (`repo/path/in/repo`) and line numbers. This is the fastest way to locate code; reach for it before reading files.

                 - Two modes. Text (the default) finds lines containing every whitespace-separated token of the query. regex=true is an RE2 regular expression over each line: use it for a partial name, a prefix, alternation (`Foo|Bar`) or any pattern. RE2 has no lookbehind, no lookahead and no backreferences; such a pattern gets an explanation, not an empty result.
                 - Every reply names the engine that answered: `token scan` (every identifier piece of the query matched as a whole token, then exact-verified) or `substring scan` for text queries, `regex scan` or `multiline regex scan` otherwise. They rank files the same way but the token path only finds whole identifier tokens, so a text query that misses a partial name should be retried with regex=true.
                 - On a large or generated code base four parameters pay for themselves: `exclude="*.g.cs,/tests/"` strips noise; `context=4` tells you what a hit means without opening the file; `filesOnly=true` sizes a broad query for almost nothing; `multiline=true` matches a statement wrapped over several lines.
                 - A workflow that works: filesOnly first to see how big the answer is, then the same query with context to read the hits.
                 - Do not run the same pattern once per folder or once per repository. `path` takes a comma-separated list and ORs the terms, and a search always spans every repository in the project.
                 - Do not run one call per pattern either. With regex=true, alternation does it in one.
                 - "No matches" replies say whether matches existed outside your path/ext/exclude filters, so a filtered miss is never mistaken for a clean negative.
                 """)]
    public async Task<string> Grep(
        [Description(
            "What to search for: text tokens, or an RE2 regular expression when regex=true or multiline=true.")]
        string query,
        [Description(
            "Treat the query as an RE2 regular expression. Needed for substring and prefix matches and for alternation.")]
        bool regex = false,
        [Description("Match case exactly. Default false.")]
        bool caseSensitive = false,
        [Description("""
                     Only search files whose qualified path matches. Comma-separated terms are OR-ed, so "main/src/Api,main/src/Domain" searches both in one call. A term with * or ? is a glob over the whole qualified path (* crosses directory separators); otherwise it is a plain substring. Case-insensitive. At most 32 terms, each at most 256 characters.
                     """)]
        string? path = null,
        [Description("""
                     Skip files whose qualified path matches any of these comma-separated terms, same syntax as `path`: "*.g.cs" drops generated files anywhere, "/tests/" drops any test directory. Example: "*.g.cs,/tests/,/obj/".
                     """)]
        string? exclude = null,
        [Description("Only search files with this extension, without the dot, e.g. \"cs\" or \"tsx\".")]
        string? ext = null,
        [Description("""
                     Match across line breaks, so a pattern can span a wrapped statement such as `repo.Update(entity,\n  e => e.Status = ...)`. Implies regex. Same RE2 syntax, and `.` also crosses newlines here. Every line a match spans is returned and marked as matched. Slower than single-line mode; give the pattern a distinctive literal so candidate files can be narrowed first. A page reads at most 8 MiB of file content to mark its matches; a file past that is listed with its match count and the page to ask for to see its lines.
                     """)]
        bool multiline = false,
        [Description("""
                     Only match whole words: no letter, digit or underscore may sit right before or after the match, whatever its alphabet, so `bar` does not match inside "fooÄbar". Regex mode only; it is what you lose by switching to regex for alternation: without it `ERP|SAP` also matches "property" and "interpreter".
                     """)]
        bool wholeWord = false,
        [Description("Lines of context to return around each match, 0-10. Default 0.")]
        int context = 0,
        [Description("Return only file paths and match counts, no lines. Cheap way to size a broad query.")]
        bool filesOnly = false,
        [Description("Matching lines to return per file, 1-200. Default 20.")]
        int maxLinesPerFile = 20,
        [Description("1-based page of results. Files are ordered by match count, most first.")]
        int page = 1,
        [Description("Files per page, 1-100. Default 20.")]
        int pageSize = 20,
        [Description("""
                     Annotate each returned line with the commit that last changed it: date, author and subject. Use it when the question is who to ask about a hit, not only where it is. It is who touched the line LAST — a reformat counts — so it never proves who introduced something. Off by default because it makes every line of the reply longer.
                     """)]
        bool withHistory = false,
        CancellationToken cancellationToken = default)
    {
        var request = new GrepRequest(query, regex, caseSensitive, path, exclude, ext, multiline, wholeWord, context,
            filesOnly, maxLinesPerFile, page, pageSize, withHistory);

        return ToolReply.Render<GrepResult>(await grep.SearchAsync(Bound.Slug, request, cancellationToken),
            result => result.TotalFiles == 0 ? NoMatches(request, result) : Format(request, result),
            "Narrow with path/ext/exclude, lower pageSize or maxLinesPerFile, or use filesOnly=true to see the shape of the answer first.");
    }

    private static string NoMatches(GrepRequest request, GrepResult result)
    {
        var text = new StringBuilder($"No matches for \"{request.Query}\" ({result.Engine} engine). ");
        text.Append(FilterVerdict(result.FilesMatchingWithoutFilters, "pattern",
            "Nothing matches anywhere in the project"));
        // Where the pattern does match outside the filters, the filters are the whole answer: an engine
        // hint there would send the caller to change a pattern that is already right. The token path
        // is the exception, because what it counted outside is whole tokens: a longer name inside the
        // filters — MsgErrorDB under MsgError — is one it never counts, and "widen them" alone sent
        // agents away from the files they had scoped to and on to the wrong routine.
        if (result.FilesMatchingWithoutFilters is not > 0)
            text.Append(Hint(request, result.FilesMatchingWithoutFilters is null));
        else if (result.Engine == GrepSearch.TokenEngine)
            text.Append(" Those are whole-token matches: a longer name that contains the query is not counted and may still be inside your filters, so retry with regex=true before widening them.");

        return text.ToString();

        static string Hint(GrepRequest request, bool unfiltered)
        {
            return request.Regex || request.Multiline
                ? request.WholeWord
                    ? "Try again without wholeWord=true: the match may be part of a longer identifier."
                    // "Try a looser pattern" names no mechanism, and once the search has already spanned
                    // the project there is nothing left to loosen; say what a caller can act on instead (#87).
                    : unfiltered
                        ? "If this is a partial name, try a shorter fragment."
                        : "Try a looser pattern."
                : request.Query.Any(c => !char.IsLetterOrDigit(c) && c != '_' && !char.IsWhiteSpace(c))
                    ? "Text mode requires every token on one line. Retry with regex=true and escape metacharacters with a backslash, or search a single distinctive token."
                    : "Text mode requires every token on one line, and the token path matches whole identifier tokens. Retry with regex=true for a partial name.";
        }
    }

    /// <summary>
    ///     What a miss knows about the filters, read once for the three searches whose result carries
    ///     the same nullable count. The <c>null</c> case — nothing was filtered — is the one that knows
    ///     the most and said the least until #87: the search already spanned every file, so the miss is
    ///     the project's answer and the reply says so before falling back to any hint.
    ///     Each tool supplies <paramref name="miss" /> rather than this reading spelling it, because what
    ///     an empty result means is not the same fact in all three: <c>list_matches</c> drops an empty
    ///     capture value before it counts, so a pattern that matched every line with an empty group
    ///     arrives here by the same door as one that matched nothing, and only it must say both.
    ///     The filter clause says the search spanned every file rather than that nothing narrowed it:
    ///     <c>wholeWord</c>, <c>caseSensitive</c> and <c>group</c> narrow a search too, and the hint that
    ///     follows may well name one of them.
    /// </summary>
    /// <param name="filesMatchingWithoutFilters">
    ///     How many files hold a match once the filters are dropped: zero when nothing matches anywhere,
    ///     <c>null</c> when there were no filters to drop.
    /// </param>
    /// <param name="subject">What the tool searched for, as its own reply names it.</param>
    /// <param name="miss">What this tool knows an empty result to mean, as a sentence without its end.</param>
    private static string FilterVerdict(int? filesMatchingWithoutFilters, string subject, string miss) =>
        filesMatchingWithoutFilters switch
        {
            > 0 => $"The {subject} does match in {ToolReply.HiddenByFilters(filesMatchingWithoutFilters.Value)}",
            0 => $"{miss}, with or without your filters. ",
            _ => $"{miss}; no filters narrowed the search, which spanned every file. "
        };

    private static string Format(GrepRequest request, GrepResult result)
    {
        int lastPage = (result.TotalFiles + result.PageSize - 1) / result.PageSize;
        var text = new StringBuilder();
        // Spell out that the counts are project-wide totals, not this page; read as per-page numbers they
        // turn a paging decision into a guess.
        text.Append(CultureInfo.InvariantCulture,
                $"{result.TotalFiles} {ToolReply.Plural(result.TotalFiles, "file")} match in total")
            .Append(CultureInfo.InvariantCulture,
                $" ({result.TotalLines} matching {ToolReply.Plural(result.TotalLines, "line")})")
            .Append(lastPage == 1
                ? ", all shown below"
                : string.Create(CultureInfo.InvariantCulture,
                    $"; showing {result.Files.Count} of them, page {result.Page} of {lastPage}"))
            .Append(CultureInfo.InvariantCulture, $" ({result.Engine} engine)\n");

        if (result.Files.Count == 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"\nPage {result.Page} is past the end; the last page is {lastPage}.\n");
        }
        else if (request.FilesOnly)
        {
            text.Append('\n');
            foreach (var file in result.Files)
                text.Append(CultureInfo.InvariantCulture, $"{file.MatchCount,6}  {file.QualifiedPath}\n");
        }
        else
        {
            // A page of one is read whatever the file's size, so an unread file is pointed at the page
            // it is on at that size.
            long first = Paging.Skip(result.Page, result.PageSize) + 1;
            for (int i = 0; i < result.Files.Count; i++) AppendFile(text, request, result.Files[i], first + i);
        }

        if (result.Page < lastPage)
            text.Append(CultureInfo.InvariantCulture,
                $"\nMore files match. Call grep again with page={result.Page + 1}.\n");

        return text.ToString();
    }

    private static void AppendFile(StringBuilder text, GrepRequest request, GrepFile file, long pageOfOne)
    {
        text.Append(CultureInfo.InvariantCulture,
            $"\n{file.QualifiedPath}  -  {file.MatchCount} {ToolReply.Plural(file.MatchCount, "match", "matches")}\n");
        if (file.Unread)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"  ... lines not shown: a multiline page reads at most {GrepSearch.MaxMultilinePageMiB} MiB of file content, and the files above used it. "
                + $"Call grep with pageSize=1 and page={pageOfOne} to see this file's lines.\n");
            return;
        }

        int width = file.Lines.Count == 0 ? 1 : ToolReply.Digits(file.Lines[^1].LineNumber);
        string pad = new(' ', width);
        int previous = 0;
        foreach (var line in file.Lines)
        {
            // A gap means two context windows did not touch; mark it so the numbers read right. Pointless
            // without context, where every line is already its own hit.
            if (request.Context > 0 && previous > 0 && line.LineNumber > previous + 1)
                text.Append(pad).Append("  ...\n");
            // ':' marks a match and '-' a context line, the way grep does it.
            ToolReply.LineNumber(text, line.LineNumber, width).Append(line.IsMatch ? ':' : '-').Append(' ');
            ToolReply.Clip(text, line.Text);
            // The attribution goes after the code and not before it, so the code still starts at a fixed
            // column and a reply with history reads like one without.
            if (line.By is { } by)
                text.Append(string.Create(CultureInfo.InvariantCulture,
                    $"    [{by.AuthoredAt:yyyy-MM-dd} {by.AuthorName}]"));
            else if (request.WithHistory) text.Append("    [not attributed]");
            text.Append('\n');
            previous = line.LineNumber;
        }

        int hidden = file.MatchCount - file.MatchesShown;
        if (hidden > 0)
            text.Append(pad).Append("  ... ").Append(hidden).Append(" more ")
                .Append(ToolReply.Plural(hidden, "match", "matches")).Append(" in this file (raise maxLinesPerFile)\n");
    }

}
