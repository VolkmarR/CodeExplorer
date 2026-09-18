using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;

namespace CodeExplorer;

/// <summary>The MCP tools that answer from a project's index (ADR-0005, <c>Search/</c>).</summary>
[McpServerToolType]
internal sealed class SearchTools(
    IHttpContextAccessor httpContextAccessor,
    GrepSearch grep,
    ReferenceSearch references,
    DefinitionSearch definitions,
    MatchList matches)
{
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
        [Description("""
                     Annotate each returned line with the commit that last changed it: date, author and subject. Use it when the question is who to ask about a hit, not only where it is. It is who touched the line LAST — a reformat counts — so it never proves who introduced something. Off by default because it makes every line of the reply longer.
                     """)]
        bool withHistory = false,
        CancellationToken cancellationToken = default)
    {
        var project = BoundProject.Get(httpContextAccessor);
        var request = new GrepRequest(query, regex, caseSensitive, path, exclude, ext, multiline, wholeWord, context,
            filesOnly, maxLinesPerFile, page, pageSize, withHistory);

        return ToolReply.Render<GrepResult>(await grep.SearchAsync(project.Slug, request, cancellationToken),
            result => result.TotalFiles == 0 ? NoMatches(request, result) : Format(request, result),
            "Narrow with path/ext/exclude, lower pageSize or maxLinesPerFile, or use filesOnly=true to see the shape of the answer first.");
    }

    private static string NoMatches(GrepRequest request, GrepResult result)
    {
        var text = new StringBuilder($"No matches for \"{request.Query}\" ({result.Engine} engine). ");
        text.Append(FilterVerdict(result.FilesMatchingWithoutFilters, "pattern",
            "Nothing matches anywhere in the project"));
        // Where the pattern does match outside the filters, the filters are the whole answer: an engine
        // hint there would send the caller to change a pattern that is already right.
        if (result.FilesMatchingWithoutFilters is not > 0)
            text.Append(Hint(request, result.FilesMatchingWithoutFilters is null));

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
            foreach (var file in result.Files) AppendFile(text, request, file);
        }

        if (result.Page < lastPage)
            text.Append(CultureInfo.InvariantCulture,
                $"\nMore files match. Call grep again with page={result.Page + 1}.\n");

        return text.ToString();
    }

    private static void AppendFile(StringBuilder text, GrepRequest request, GrepFile file)
    {
        text.Append(CultureInfo.InvariantCulture,
            $"\n{file.QualifiedPath}  -  {file.MatchCount} {ToolReply.Plural(file.MatchCount, "match", "matches")}\n");

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
                .Append(line.IsMatch ? ':' : '-').Append(' ').Append(ToolReply.Clip(line.Text));
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

    [McpServerTool(Name = "find_references", ReadOnly = true, Idempotent = true,
        Title = "Find references to an identifier")]
    [Description("""
                 Finds where an identifier is declared, written, called and read, each reference labelled with the enclosing `Type.Member` it sits in. It answers "who calls this?" and "what changes this?" in one call.

                 - IMPORTANT: this is a heuristic over text, not a compiler. It reads one line at a time and decides what each appearance of the name looks like, so an unrelated symbol that happens to share the name is reported too, and a call routed through an interface, a delegate or reflection is not reported at all, because the name never appears where that call is made. Treat a clean result as strong evidence and an empty one as weak evidence; confirm anything you are about to change by reading the file.
                 - Prefer it over grep for an identifier: it matches whole names only, and it tells a declaration from a call from a mention in a comment instead of handing you one flat list.
                 - To answer "what changes this field?", use writesOnly=true. Writes are assignments — `x.Status = …`, `Status += …`, and the receiver-less object-initializer form `Status = dao.Status` that a `\.Status\s*=` regex silently misses. Comparisons (`==`, `>=`) and lambda arrows stay out, so a write list is not padded with reads.
                 - Comments, strings and import lines are counted but not listed unless includeNoise=true.
                 - For an interface, a call names the method rather than the interface, so run it again on the member you care about.
                 - Scope with `repo`, `path`, `ext` and `exclude` exactly as grep does. When a filter is set the reply says how many further files matched outside it, because a declaration hidden by `exclude` makes a thin answer look complete.
                 """)]
    public async Task<string> FindReferences(
        [Description(
            "The identifier to look for, e.g. \"UpdateDeliveryNoteStatus\" or \"OrderEntity\". One name, matched whole and case-sensitively.")]
        string symbol,
        [Description("Repository slug to scope to. Default: every repository in the project.")]
        string? repo = null,
        [Description(
            "Only look in files whose qualified path matches; comma-separated terms are OR-ed. Same syntax as grep.")]
        string? path = null,
        [Description(
            "Skip files whose qualified path matches any of these comma-separated terms, e.g. \"*.g.cs,/tests/\".")]
        string? exclude = null,
        [Description("Only look in files with this extension, without the dot, e.g. \"cs\".")]
        string? ext = null,
        [Description("Show only declarations and writes — the answer to \"what changes this?\".")]
        bool writesOnly = false,
        [Description("Also list the comment, string and import mentions instead of only counting them.")]
        bool includeNoise = false,
        [Description("Maximum files to examine, 1-100. Default 60. The files naming it most often come first.")]
        int maxFiles = ReferenceSearch.DefaultMaxFiles,
        CancellationToken cancellationToken = default)
    {
        var project = BoundProject.Get(httpContextAccessor);
        var request = new ReferenceRequest(symbol, new FileFilter(repo, path, exclude, ext), maxFiles);

        return ToolReply.Render<ReferenceResult>(await references.FindAsync(project.Slug, request, cancellationToken),
            result => result.TotalFiles == 0
                ? NoReferences(symbol.Trim(), result)
                : FormatReferences(symbol.Trim(), result, writesOnly, includeNoise),
            "Narrow with repo/path/ext/exclude, or lower maxFiles.");
    }

    private static string NoReferences(string symbol, ReferenceResult result)
    {
        // The opening says only that this answer is empty. That nothing in the project spells the name is
        // the stronger claim and belongs to the verdict, which is the one place that knows whether the
        // filters were what emptied it.
        var text = new StringBuilder($"No references to \"{symbol}\". ");
        text.Append(FilterVerdict(result.FilesMatchingWithoutFilters, "name",
            "Nothing in this project spells it"));
        // The spelling hint names a mechanism the verdict does not, so it follows every miss the filters
        // do not already explain.
        if (result.FilesMatchingWithoutFilters is not > 0)
            text.Append(
                "The name is matched whole and case-sensitively, so check the spelling and the case, or search for the interface that declares it. grep with regex=true finds a partial name.");
        return text.ToString();
    }

    private static string FormatReferences(
        string symbol, ReferenceResult result, bool writesOnly, bool includeNoise)
    {
        int Count(ReferenceKind kind) => result.Count(kind);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
                $"\"{symbol}\" in {result.FilesExamined} {ToolReply.Plural(result.FilesExamined, "file")}: {result.CodeReferences} {ToolReply.Plural(result.CodeReferences, "reference")}, {result.Noise} in comments, strings or imports\n")
            .Append(CultureInfo.InvariantCulture,
                $"  {Count(ReferenceKind.Definition)} {ToolReply.Plural(Count(ReferenceKind.Definition), "declaration")}, {Count(ReferenceKind.Write)} {ToolReply.Plural(Count(ReferenceKind.Write), "write")}, ")
            .Append(CultureInfo.InvariantCulture,
                $"{Count(ReferenceKind.Call)} {ToolReply.Plural(Count(ReferenceKind.Call), "call")}, {Count(ReferenceKind.Instantiation)} {ToolReply.Plural(Count(ReferenceKind.Instantiation), "instantiation")}, ")
            .Append(CultureInfo.InvariantCulture,
                $"{Count(ReferenceKind.TypeUse)} type {ToolReply.Plural(Count(ReferenceKind.TypeUse), "use")}, {Count(ReferenceKind.MemberAccess)} {ToolReply.Plural(Count(ReferenceKind.MemberAccess), "read")}, ")
            .Append(CultureInfo.InvariantCulture, $"{Count(ReferenceKind.Other)} unplaced\n");

        if (result.TotalFiles > result.FilesExamined)
            text.Append(CultureInfo.InvariantCulture,
                $"  NOTE: {result.TotalFiles} files hold the name in total; only the {result.FilesExamined} that hold it most often were examined. Narrow with repo/path/ext/exclude, or raise maxFiles.\n");

        // A file cut short at the per-file ceiling would otherwise be indistinguishable from one that
        // simply holds that many lines, and the unread ones could include the declaration.
        var cutShort = result.CutShortFiles;
        if (cutShort.Count > 0)
            text.Append(CultureInfo.InvariantCulture,
                $"  NOTE: {ToolReply.Plural(cutShort.Count, "one file holds", $"{cutShort.Count} files hold")} {ReferenceSearch.MaxLinesPerFile} or more lines naming it and {ToolReply.Plural(cutShort.Count, "was", "were")} read no further ({string.Join(", ", cutShort)}). The name is too common to enumerate; narrow with path, or search a more distinctive one.\n");

        // A thin answer under a filter is the footgun: the declaration may sit in a file the filter
        // hid, and a generated partial is the usual case.
        int hiddenFiles = (result.FilesMatchingWithoutFilters ?? result.TotalFiles) - result.TotalFiles;
        if (hiddenFiles > 0)
            text.Append(CultureInfo.InvariantCulture,
                $"  NOTE: {ToolReply.PartlyHiddenByFilters(hiddenFiles)}\n");

        Section("DECLARATIONS", ReferenceKind.Definition);
        Section("WRITES", ReferenceKind.Write);
        if (writesOnly)
        {
            if (Count(ReferenceKind.Definition) + Count(ReferenceKind.Write) == 0)
                text.Append(
                    "\nNo declarations or writes. The name is only read here — drop writesOnly to see the calls, reads and type uses.\n");
        }
        else
        {
            Section("CALLS", ReferenceKind.Call);
            Section("INSTANTIATIONS", ReferenceKind.Instantiation);
            Section("TYPE USES", ReferenceKind.TypeUse);
            Section("READS", ReferenceKind.MemberAccess);
            Section("UNPLACED", ReferenceKind.Other);
            if (includeNoise)
            {
                Section("COMMENTS", ReferenceKind.Comment);
                Section("STRINGS", ReferenceKind.StringLiteral);
                Section("IMPORTS", ReferenceKind.Import);
            }
        }

        // An interface asked about by its own name looks inert: callers write _service.DoThing(), so
        // the type name appears only where it is declared or injected, never where the call is made.
        // Answering "who uses this?" means searching the member, and saying so beats a bare zero.
        if (Count(ReferenceKind.Call) == 0 && Count(ReferenceKind.TypeUse) > 0
                                           && symbol.Length > 1 && symbol[0] == 'I' && char.IsUpper(symbol[1]))
            text.Append(CultureInfo.InvariantCulture,
                $"\n0 calls but {Count(ReferenceKind.TypeUse)} type uses, and \"{symbol}\" looks like an interface. A call names the METHOD, not the interface, so this cannot answer \"who uses it?\". Read one implementation for its member names, then run find_references on the method you care about.\n");

        // What the footer may claim is decided by how the answers were reached and not by what was
        // true when it was written (ADR-0008): once a parser-backed analyser is registered for a
        // language, a reply that still called itself textual would be understating what it knows.
        // Only the lead clause varies; the caveat after it is true either way and is stored once.
        string how = result.References.All(r => r.Evidence == Evidence.Text)
            ? "Classification is textual"
            : "Classification is parsed where the language has a parser here and textual elsewhere";
        text.Append(CultureInfo.InvariantCulture,
            $"\n{how} — line shape, no compiler. An unrelated symbol of the same name is included, and a call made through an interface, a delegate or reflection is not. Strong evidence, not proof.\n");
        return text.ToString();

        void Section(string title, ReferenceKind kind)
        {
            var items = result.References.Where(r => r.Kind == kind).ToList();
            if (items.Count == 0) return;

            // The heading counts the references, but the same line is printed once however many of
            // them it holds: two type uses on one line are two references and one thing to read.
            text.Append(CultureInfo.InvariantCulture, $"\n{title}  ({items.Count})\n");
            AppendHits(text, items.DistinctBy(r => (r.QualifiedPath, r.LineNumber))
                .Select(r => (r.QualifiedPath, r.LineNumber, r.Scope, r.Text)));
        }
    }

    [McpServerTool(Name = "find_definition", ReadOnly = true, Idempotent = true,
        Title = "Find where a symbol is declared")]
    [Description("""
                 Finds where a symbol is DECLARED — the class, method, function, procedure or type of that name — with its qualified path, line number and the declaring line. Use it when you want the definition; use find_references when you want the uses.

                 - Delphi and PL/SQL declare a routine twice: announced in the `interface` section or the package spec, written in `implementation` or the package body, usually in two different files. Both are listed, each labelled, and IMPLEMENTATIONS come first because the body is almost always what was wanted. C#, X#, TypeScript and JavaScript declare once, and the answer simply says so.
                 - IMPORTANT: this is a heuristic over text, not a compiler. It knows the declaration forms its language profile knows, so a form it does not know is a miss and not proof there is none. A miss degrades to find_references and grep, and the reply says so rather than pretending the symbol has no declaration.
                 - The name is matched whole and case-sensitively, exactly as find_references matches it.
                 - A name declared more than once — an overload, a partial class, the same name on two unrelated types — returns every site, ranked.
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
        var project = BoundProject.Get(httpContextAccessor);
        var request = new DefinitionRequest(symbol, new FileFilter(repo, path, exclude, ext));

        return ToolReply.Render<DefinitionResult>(await definitions.FindAsync(project.Slug, request, cancellationToken),
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
        return text.ToString();
    }

    private static string FormatDefinitions(string symbol, DefinitionResult result)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"\"{symbol}\" is declared in {result.TotalSites} {ToolReply.Plural(result.TotalSites, "place")}");
        text.Append(result.TotalSites > result.Sites.Count
            ? string.Create(CultureInfo.InvariantCulture,
                $"; showing the first {result.Sites.Count}. The name is declared too often to enumerate — narrow with repo/path/ext/exclude.\n")
            : ".\n");

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
                text.Append("  ")
                    .Append(hit.LineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(width))
                    .Append(": ")
                    .Append(hit.Label is null ? "" : $"[{hit.Label}] ")
                    .Append(ToolReply.Clip(hit.Text).TrimStart())
                    .Append('\n');
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
        var project = BoundProject.Get(httpContextAccessor);
        var request = new MatchListRequest(query, new FileFilter(repo, path, exclude, ext), group, caseSensitive,
            wholeWord, limit);

        return ToolReply.Render<MatchListResult>(await matches.ListAsync(project.Slug, request, cancellationToken),
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
            text.Append(CultureInfo.InvariantCulture,
                $"{match.Count,5}  {match.Files,5}  {ToolReply.Clip(match.Value.Trim())}\n");

        return text.ToString();
    }
}
