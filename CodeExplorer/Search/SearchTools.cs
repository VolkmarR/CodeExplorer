using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;

namespace CodeExplorer;

/// <summary>The MCP tools that answer from a project's index (ADR-0005, <c>Search/</c>).</summary>
[McpServerToolType]
internal sealed class SearchTools(IHttpContextAccessor httpContextAccessor, GrepSearch grep)
{
    /// <summary>
    ///     Hard ceiling on one reply, roughly 10k tokens. A line cap alone is not enough: two hundred
    ///     ordinary hits still flood a context. An exact multiple of 1024, so the "capped at N KB" the
    ///     caller reads is the real number.
    /// </summary>
    private const int MaxOutputChars = 40 * 1024;

    /// <summary>
    ///     Longer than any hand-written source line, shorter than a minified bundle or a data literal,
    ///     which would otherwise spend the whole reply budget on one hit.
    /// </summary>
    private const int MaxLineChars = 500;

    [McpServerTool(Name = "grep", ReadOnly = true, Idempotent = true, Title = "Search the project's code")]
    [Description("""
                 Searches every indexed line of every repository in this project and returns the matching lines grouped by file, with qualified paths (`repo/path/in/repo`) and line numbers. This is the fastest way to locate code; reach for it before reading files.

                 - Two modes. Text (the default) finds lines containing every whitespace-separated token of the query. regex=true is an RE2 regular expression over each line: use it for a partial name, a prefix, alternation (`Foo|Bar`) or any pattern. RE2 has no lookbehind, no lookahead and no backreferences; such a pattern gets an explanation, not an empty result.
                 - Every reply names the engine that answered: `full-text` (BM25 over identifier tokens, exact-verified) or `substring scan` for text queries, `regex scan` or `multiline regex scan` otherwise. They rank files the same way but the full-text path can only find whole identifier tokens, so a text query that misses a partial name should be retried with regex=true.
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
                     Only search files whose qualified path matches. Comma-separated terms are OR-ed, so "main/src/Api,main/src/Domain" searches both in one call. A term with * or ? is a glob over the whole qualified path (* crosses directory separators); otherwise it is a plain substring. Case-insensitive.
                     """)]
        string? path = null,
        [Description("""
                     Skip files whose qualified path matches any of these comma-separated terms, same syntax as `path`: "*.g.cs" drops generated files anywhere, "/tests/" drops any test directory. Example: "*.g.cs,/tests/,/obj/".
                     """)]
        string? exclude = null,
        [Description("Only search files with this extension, without the dot, e.g. \"cs\" or \"tsx\".")]
        string? ext = null,
        [Description("""
                     Match across line breaks, so a pattern can span a wrapped statement such as `repo.Update(entity,\n  e => e.Status = ...)`. Implies regex. Same RE2 syntax, and `.` also crosses newlines here. Every line a match spans is returned and marked as matched. Slower than single-line mode; give the pattern a distinctive literal so candidate files can be narrowed first.
                     """)]
        bool multiline = false,
        [Description("""
                     Only match whole words, by anchoring the pattern on word boundaries. Regex mode only; it is what you lose by switching to regex for alternation: without it `ERP|SAP` also matches "property" and "interpreter".
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
        CancellationToken cancellationToken = default)
    {
        var project = BoundProject.Get(httpContextAccessor);
        var request = new GrepRequest(query, regex, caseSensitive, path, exclude, ext, multiline, wholeWord, context,
            filesOnly, maxLinesPerFile, page, pageSize);

        var outcome = await grep.SearchAsync(project.Slug, request, cancellationToken);
        if (outcome is GrepProblem problem) return problem.Explanation;
        var result = (GrepResult)outcome;
        return result.TotalFiles == 0 ? NoMatches(request, result) : Format(request, result);
    }

    private static string NoMatches(GrepRequest request, GrepResult result)
    {
        var text = new StringBuilder($"No matches for \"{request.Query}\" ({result.Engine} engine). ");
        switch (result.FilesMatchingWithoutFilters)
        {
            case > 0:
                text.Append(CultureInfo.InvariantCulture,
                    $"The pattern does match in {result.FilesMatchingWithoutFilters} {Plural(result.FilesMatchingWithoutFilters.Value, "file")} outside your path/ext/exclude filters; the filters hid every match. Widen or drop them to see those.");
                break;
            case 0:
                text.Append("Nothing matches anywhere in the project, with or without your filters. ");
                text.Append(Hint(request));
                break;
            default:
                text.Append(Hint(request));
                break;
        }

        return text.ToString();

        static string Hint(GrepRequest request)
        {
            return request.Regex || request.Multiline
                ? request.WholeWord
                    ? "Try again without wholeWord=true: the match may be part of a longer identifier."
                    : "Try a looser pattern."
                : request.Query.Any(c => !char.IsLetterOrDigit(c) && c != '_' && !char.IsWhiteSpace(c))
                    ? "Text mode requires every token on one line. Retry with regex=true and escape metacharacters with a backslash, or search a single distinctive token."
                    : "Text mode requires every token on one line, and the full-text path matches whole identifier tokens. Retry with regex=true for a partial name.";
        }
    }

    private static string Format(GrepRequest request, GrepResult result)
    {
        int lastPage = (result.TotalFiles + result.PageSize - 1) / result.PageSize;
        var text = new StringBuilder();
        // Spell out that the counts are project-wide totals, not this page; read as per-page numbers they
        // turn a paging decision into a guess.
        text.Append(CultureInfo.InvariantCulture,
                $"{result.TotalFiles} {Plural(result.TotalFiles, "file")} match in total")
            .Append(CultureInfo.InvariantCulture,
                $" ({result.TotalLines} matching {Plural(result.TotalLines, "line")})")
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
            foreach (var file in result.Files) AppendFile(text, request, file);
        }

        if (result.Page < lastPage)
            text.Append(CultureInfo.InvariantCulture,
                $"\nMore files match. Call grep again with page={result.Page + 1}.\n");

        return Cap(text.ToString());
    }

    private static void AppendFile(StringBuilder text, GrepRequest request, GrepFile file)
    {
        text.Append(CultureInfo.InvariantCulture,
            $"\n{file.QualifiedPath}  -  {file.MatchCount} {Plural(file.MatchCount, "match", "matches")}\n");

        int width = file.Lines.Count == 0 ? 1 : file.Lines[^1].LineNumber.ToString(CultureInfo.InvariantCulture).Length;
        string pad = new(' ', width);
        int previous = 0;
        foreach (var line in file.Lines)
        {
            // A gap means two context windows did not touch; mark it so the numbers read right. Pointless
            // without context, where every line is already its own hit.
            if (request.Context > 0 && previous > 0 && line.LineNumber > previous + 1)
                text.Append(pad).Append("  ...\n");
            // ':' marks a match and '-' a context line, the way grep does it.
            text.Append(line.LineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(width))
                .Append(line.IsMatch ? ':' : '-').Append(' ').Append(Clip(line.Text)).Append('\n');
            previous = line.LineNumber;
        }

        int hidden = file.MatchCount - file.MatchesShown;
        if (hidden > 0)
            text.Append(pad).Append("  ... ").Append(hidden).Append(" more ")
                .Append(Plural(hidden, "match", "matches")).Append(" in this file (raise maxLinesPerFile)\n");
    }

    private static string Cap(string text)
    {
        if (text.Length <= MaxOutputChars) return text;

        int cut = text.LastIndexOf('\n', MaxOutputChars);
        if (cut < MaxOutputChars / 2) cut = MaxOutputChars;
        string notice = string.Create(CultureInfo.InvariantCulture,
            $"\n\n... results truncated at {MaxOutputChars / 1024} KB ({text.Length - cut:N0} more characters). ");
        return text[..cut] + notice
                           + "Narrow with path/ext/exclude, lower pageSize or maxLinesPerFile, or use filesOnly=true to see the shape of the answer first.\n";
    }

    private static string Clip(string text)
    {
        string trimmed = text.TrimEnd();
        return trimmed.Length <= MaxLineChars
            ? trimmed
            : string.Create(CultureInfo.InvariantCulture,
                $"{trimmed[..MaxLineChars]} ... [{trimmed.Length - MaxLineChars} more characters on this line]");
    }

    private static string Plural(long n, string one, string? many = null) => n == 1 ? one : many ?? one + "s";
}
