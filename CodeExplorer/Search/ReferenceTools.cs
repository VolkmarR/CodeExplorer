using System.ComponentModel;
using System.Globalization;
using System.Text;
using CodeExplorer.Infrastructure;
using CodeExplorer.Language;
using ModelContextProtocol.Server;

namespace CodeExplorer.Search;

/// <summary>
///     <c>find_references</c>: where an identifier is used (CONTEXT.md, Reference), and what the
///     answer leaves uncovered. The coverage note is the larger half of it, which is why it is written
///     apart from the grep it is built on.
/// </summary>
internal sealed partial class SearchTools
{
    [McpServerTool(Name = "find_references", ReadOnly = true, Idempotent = true,
        Title = "Find references to an identifier")]
    [Description("""
                 Finds where an identifier is declared, written, called and read, each reference labelled with the enclosing `Type.Member` it sits in. It answers "who calls this?" and "what changes this?" in one call.

                 - IMPORTANT: this is a heuristic over text, not a compiler. It reads one line at a time and decides what each appearance of the name looks like, so an unrelated symbol that happens to share the name is reported too, and a call routed through an interface, a delegate or reflection is not reported at all, because the name never appears where that call is made. Treat a clean result as strong evidence and an empty one as weak evidence; confirm anything you are about to change by reading the file.
                 - Prefer it over grep for an identifier: it matches whole names only, and it tells a declaration from a call from a mention in a comment instead of handing you one flat list.
                 - To answer "what changes this field?", use writesOnly=true. Writes are assignments — `x.Status = …`, `Status += …`, and the receiver-less object-initializer form `Status = dao.Status` that a `\.Status\s*=` regex silently misses. Comparisons (`==`, `>=`) and lambda arrows stay out, so a write list is not padded with reads.
                 - Comments, strings and import lines are counted but not listed unless includeNoise=true.
                 - For an interface, a call names the method rather than the interface, so run it again on the member you care about.
                 - **The per-kind counts are a floor for the files examined, never a project total.** Only the `maxFiles` files naming it most often are classified, so "412 calls" means 412 in that sample. Where the name is spread wider than the cap the reply gives the project-wide occurrence count beside it, and `maxFiles` stops at 100 however many files hold the name — for breadth past that, `grep(filesOnly=true)` counts the files and `list_matches` the distinct spellings.
                 - Scope with `repo`, `path`, `ext` and `exclude` exactly as grep does. When a filter is set the reply says how many further files matched outside it, because a declaration hidden by `exclude` makes a thin answer look complete.
                 - Where files holding the name are written in a language no profile covers, the reply names those extensions and how many files they are: the lines were matched, but what each appearance is was read from shapes that are not that language's.
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
        var request = new ReferenceRequest(symbol, new FileFilter(repo, path, exclude, ext), maxFiles);

        return ToolReply.Render<ReferenceResult>(await references.FindAsync(Bound.Slug, request, cancellationToken),
            result => result.TotalFiles == 0
                ? NoReferences(symbol.Trim(), result)
                : FormatReferences(symbol.Trim(), result, writesOnly, includeNoise),
            "Narrow with repo/path/ext/exclude, or lower maxFiles.");
    }

    /// <summary>
    ///     What a symbol search could not read the way its language writes it (#126). `imports`,
    ///     and `list_declarations` both say outright when an extension has no profile, so
    ///     an empty answer from them is never mistaken for a fact about the file; these two tools —
    ///     the two an agent reaches for most — said nothing, and a file read with the conservative
    ///     default shapes contributed the same silence as a file that was searched and held nothing.
    ///     One sentence for both of them, because a caller running one after the other must be told
    ///     one fact and not two shapes of it. What follows from it differs and is the caller's clause.
    ///     Empty where every matching file's extension is profiled, which is the common case: a note
    ///     on every reply is one an agent stops reading.
    ///     A clause per reason and one consequence for the note (#129): "read with shapes that may not
    ///     fit" and "not read at all" are different claims and each says its own, but what an agent
    ///     does about either is the same thing, and saying it twice in two consecutive notes is how a
    ///     caveat becomes something to skim. The unreadable clause is only ever reached by
    ///     `find_definition`; `find_references` classifies an occurrence with the analyser whatever
    ///     its language, so a shapeless profile costs it nothing and it reports none.
    /// </summary>
    /// <param name="uncovered">Every gap the search counted, as the search ordered them.</param>
    /// <param name="consequence">What the gaps cost this tool's answer, in the tool's own words.</param>
    private static string CoverageNote(IReadOnlyList<UncoveredFiles> uncovered, string consequence)
    {
        var unprofiled = WithGap(uncovered, CoverageGap.Unprofiled);
        var unreadable = WithGap(uncovered, CoverageGap.Unreadable);
        if (unprofiled.Count == 0 && unreadable.Count == 0) return "";

        var text = new StringBuilder("NOTE: ");
        if (unprofiled.Count > 0)
        {
            int files = unprofiled.Sum(file => file.Files);
            text.Append(CultureInfo.InvariantCulture,
                $"{files} {ToolReply.Plural(files, "file")} spelling the name {ToolReply.Plural(files, "is", "are")} {Named(unprofiled, "extension")}, which no language profile covers, so {ToolReply.Plural(files, "it was", "they were")} read with the conservative default shapes rather than the forms that language writes. ");
        }

        if (unreadable.Count > 0)
        {
            int files = unreadable.Sum(file => file.Files);
            // "further" only where the clause above already counted some. The two reasons are counted
            // apart — the same file cannot be both — and a note that opens with this one has nothing
            // to be further than.
            string counted = unprofiled.Count > 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{files} further {ToolReply.Plural(files, "file")} spelling the name")
                : string.Create(CultureInfo.InvariantCulture,
                    $"{files} {ToolReply.Plural(files, "file")} spelling the name");
            text.Append(CultureInfo.InvariantCulture,
                $"{counted} {ToolReply.Plural(files, "is", "are")} {Named(unreadable, "language")}, whose declarations are not something that can be read from a line, so nothing in {ToolReply.Plural(files, "it", "them")} was scanned. ");
        }

        return text.Append(consequence).Append('\n').ToString();
    }

    /// <summary>The gaps of one reason, most files first, as the search already ordered them.</summary>
    private static List<UncoveredFiles> WithGap(IReadOnlyList<UncoveredFiles> uncovered, CoverageGap gap) =>
        [.. uncovered.Where(file => file.Gap == gap)];

    /// <summary>
    ///     The languages a note names, and a count of the rest. The remainder is counted rather than
    ///     listed: how many kinds of file this covers is the part that still says something once the
    ///     list would be the project's file types.
    /// </summary>
    private static string Named(List<UncoveredFiles> uncovered, string kind)
    {
        var named = uncovered.Take(ScopeCoverage.MaxExtensionsNamed).ToList();
        string languages = string.Join(", ", named.Select(file => file.Language));
        return uncovered.Count > named.Count
            ? languages + string.Create(CultureInfo.InvariantCulture,
                $" and {uncovered.Count - named.Count} further {ToolReply.Plural(uncovered.Count - named.Count, kind)}")
            : languages;
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

    /// <summary>
    ///     What an unprofiled extension costs a reference answer: the matching is unaffected — a
    ///     pattern is a pattern in every language — and the classification is what was guessed at.
    /// </summary>
    private const string _referenceCost =
        "Their lines were matched like any other, but what each appearance is — a call, a write, a "
        + "comment — was read from shapes that are not this language's, so treat those rows as weaker "
        + "evidence and read the file where one of them matters.";

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

        // The per-kind counts above are the sample's, and a sample read of the heaviest files reads as a
        // project total unless it is denied. The occurrence count is the project-wide number the caller
        // wanted; it is unclassified, so it is never printed as if it were the same kind of number.
        // "Raise maxFiles" is only a move where raising it can reach the total — against thousands of
        // files it is advice the caller cannot complete, so past the ceiling the pivot is named instead.
        if (result.TotalFiles > result.FilesExamined)
            text.Append(CultureInfo.InvariantCulture,
                $"  NOTE: the counts above are a floor for the {result.FilesExamined} files examined, not a project total. "
                + $"{result.TotalFiles} files hold the name, {result.TotalOccurrences} {ToolReply.Plural(result.TotalOccurrences, "occurrence")} in all; only the files holding it most often were read. "
                + $"{(result.TotalFiles <= ReferenceSearch.MaxFiles
                    ? "Raise maxFiles for the rest, or narrow with repo/path/ext/exclude"
                    : $"{ReferenceSearch.MaxFiles} is the most maxFiles can examine, so narrow with repo/path/ext/exclude to classify a slice, and use grep(filesOnly=true) for breadth")}.\n");

        // A file cut short at the per-file ceiling would otherwise be indistinguishable from one that
        // simply holds that many lines, and the unread ones could include the declaration.
        var cutShort = result.CutShortFiles;
        if (cutShort.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"  NOTE: {ToolReply.Plural(cutShort.Count, "one file holds", $"{cutShort.Count} files hold")} {ReferenceSearch.MaxLinesPerFile} or more lines naming it and {ToolReply.Plural(cutShort.Count, "was", "were")} read no further ({string.Join(", ", cutShort)}). The name is too common to enumerate; narrow with path, or search a more distinctive one.\n");
            // Every file was examined and the answer is still a sample, because a file was cut short.
            // The note above already carries the project-wide count where files were left unread.
            if (result.TotalFiles <= result.FilesExamined)
                text.Append(CultureInfo.InvariantCulture,
                    $"  NOTE: the counts above are a floor for the lines that were read; the name occurs {result.TotalOccurrences} {ToolReply.Plural(result.TotalOccurrences, "time", "times")} project-wide.\n");
        }

        // A thin answer under a filter is the footgun: the declaration may sit in a file the filter
        // hid, and a generated partial is the usual case.
        int hiddenFiles = (result.FilesMatchingWithoutFilters ?? result.TotalFiles) - result.TotalFiles;
        if (hiddenFiles > 0)
            text.Append(CultureInfo.InvariantCulture,
                $"  NOTE: {ToolReply.PartlyHiddenByFilters(hiddenFiles)}\n");

        // Beside the other notes about what this answer does not cover, and above the listing for the
        // reason they are: a caveat under a long list is one the reply cap can cut (#126).
        if (CoverageNote(result.Uncovered, _referenceCost) is { Length: > 0 } unprofiled)
            text.Append("  ").Append(unprofiled);

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

}
