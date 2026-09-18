using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace CodeExplorer;

/// <summary>
///     The index-backed MCP tools that are not searches (ADR-0005, <c>Search/</c>): reading a file,
///     finding files by name shape, counting extensions, and listing what one file declares. Each is
///     a rendering of a <see cref="FileQueries" /> or <see cref="FileDeclarations" /> answer, the way
///     <see cref="SearchTools" /> renders the searches: the tool parses what an agent typed and words
///     what came back, and the decisions in between are the query module's, shared with the
///     operator's pages.
/// </summary>
[McpServerToolType]
internal sealed partial class FileTools(
    IHttpContextAccessor httpContextAccessor,
    FileQueries files,
    FileDeclarations declarations)
{
    /// <summary>
    ///     A whole large class in one read. Higher would let a single default read spend the reply
    ///     budget on a generated file; the byte cap still bites first on such a file and says where to continue.
    /// </summary>
    private const int MaxLinesPerRead = 2000;

    private const int DefaultLinesPerRead = 400;

    private const int DefaultGlobFiles = 500;

    /// <summary>
    ///     Enough to show a whole mid-sized tree in one call while keeping a runaway `depth` on a large
    ///     monorepo from returning megabytes; the agent is told how to narrow down.
    /// </summary>
    private const int MaxTreeEntries = 2000;

    /// <summary>
    ///     How far the name column of a declaration listing is padded out. It is what the short names
    ///     line up against and not a truncation: a name is what the next call is made with, and half
    ///     of one is worse than a row that overhangs. A generated file's three-hundred-character name
    ///     therefore pushes its own signature right, and nobody else's.
    /// </summary>
    private const int MaxLabelWidth = 40;

    [McpServerTool(Name = "read_file", ReadOnly = true, Idempotent = true, Title = "Read files from the index")]
    [Description("""
                 Returns one or more indexed files with line numbers, so the numbers line up with what grep reports. Paths are qualified: the repository slug, then the path inside it (`main/src/Api/Foo.cs`), exactly as grep, glob and list_tree print them.

                 - Pass several entries in one call; it is much cheaper than one call per file, and the usual case after a grep that hit several places.
                 - Append a line range to any entry to read just that window: `main/src/Api/Foo.cs:120-180`, or `main/src/Api/Foo.cs:120` to start there. The same file may appear several times with different ranges, so three hits in one 800-line file cost one call, not three.
                 - An entry without a range uses startLine and maxLines, which apply to every such entry.
                 - If you only need to know what surrounds a grep hit, grep with context=N is cheaper than reading the file at all.
                 - Output is capped and the budget is shared between the entries, so asking for many windows gives you less of each. When one is cut short the reply says what to pass to continue.
                 - A file that exists but was not indexed (binary, oversized) is reported with the reason instead of its content.
                 """)]
    public async Task<string> ReadFile(
        [Description(
            "Qualified paths, each optionally suffixed with a line range: [\"main/src/Program.cs\", \"main/src/Users.cs:120-180\", \"main/src/Users.cs:430\"].")]
        string[] paths,
        [Description("1-based line to start at for entries without their own range. Default 1.")]
        int startLine = 1,
        [Description("Lines to return per entry without its own range, 1-2000. Default 400.")]
        int maxLines = DefaultLinesPerRead,
        [Description("""
                     Add a line per file naming the commits it was first and last changed by. Cheap — one line per file, not per line of code — and the quickest way to find out who to ask about a file. Use blame for the same question about a single line.
                     """)]
        bool withHistory = false,
        CancellationToken cancellationToken = default)
    {
        var project = BoundProject.Get(httpContextAccessor);

        startLine = Math.Max(1, startLine);
        maxLines = Math.Clamp(maxLines, 1, MaxLinesPerRead);
        var targets = new List<ReadTarget>();
        foreach (string entry in paths)
        {
            var parsed = ReadTarget.Parse(entry, startLine, maxLines);
            if (parsed.Problem is not null) return parsed.Problem;
            targets.Add(parsed);
        }

        return ToolReply.Render<ReadResult>(
            await files.ReadAsync(project.Slug,
                new ReadRequest(targets.Select(t => new FileWindow(t.Path, t.Start, t.End)).ToList(), withHistory,
                    true), cancellationToken),
            Format, "Read fewer paths at once, or pass a narrower line range.");

        string Format(ReadResult result)
        {
            var reads = result.Files;
            var text = new StringBuilder();
            for (int i = 0; i < reads.Count; i++)
            {
                if (text.Length > 0) text.Append('\n');
                // Whatever the entries before this one left unused is handed on, so four small windows
                // and one large one read in full where an equal split would truncate the large one.
                int allowance = Math.Max(0, ToolReply.MaxOutputChars - text.Length) / (reads.Count - i);
                Append(text, reads[i], targets[i].ExplicitRange, allowance);
            }

            return text.ToString();
        }
    }

    private static void Append(StringBuilder text, FileRead read, bool explicitRange, int allowance)
    {
        if (read.File is not { } file)
        {
            text.Append(read.Problem!.Explanation).Append('\n');
            return;
        }

        if (file.SkipReason is not null)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{file.QualifiedPath}  -  not indexed: {file.SkipReason} ({ToolReply.Bytes(file.SizeBytes)}). Its content is not in the index, so it cannot be read here.\n");
            return;
        }

        int start = read.Window.Start;
        text.Append(CultureInfo.InvariantCulture,
            $"{file.QualifiedPath}  -  {file.LineCount} {ToolReply.Plural(file.LineCount, "line")}, {ToolReply.Bytes(file.SizeBytes)}");
        if (start > file.LineCount)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"\n  (only {file.LineCount} {ToolReply.Plural(file.LineCount, "line")}; startLine {start} is past the end)\n");
            return;
        }

        int end = read.Window.End;
        if (start > 1 || end < file.LineCount)
            text.Append(CultureInfo.InvariantCulture, $" (lines {start}-{end} of {file.LineCount})");
        text.Append('\n');
        if (read.History is { } history) text.Append(History(history));
        text.Append('\n');

        int width = end.ToString(CultureInfo.InvariantCulture).Length;
        int budget = text.Length + allowance;
        int last = end;
        for (int i = 0; i < read.Lines.Count; i++)
        {
            if (text.Length >= budget)
            {
                last = start + i - 1;
                text.Append(CultureInfo.InvariantCulture,
                    $"  ... {end - last} more {ToolReply.Plural(end - last, "line")} in this window omitted: the reply is capped at {ToolReply.MaxOutputChars / 1024} KB shared by every entry.\n");
                break;
            }

            text.Append((start + i).ToString(CultureInfo.InvariantCulture).PadLeft(width)).Append("  ")
                .Append(ToolReply.Clip(read.Lines[i])).Append('\n');
        }

        // An explicit range is what the caller asked for; only a default window or a cap stopped short of
        // what they wanted, and then the next call is spelled out.
        if (last < file.LineCount && (!explicitRange || last < end))
            text.Append(CultureInfo.InvariantCulture, $"Continue with \"{file.QualifiedPath}:{last + 1}\".\n");
    }

    /// <summary>
    ///     One line saying which commits a file was first and last changed by, or that there is none. A
    ///     file whose history is absent and one that was never changed must not read alike, so neither
    ///     is an empty line.
    /// </summary>
    private static string History(FileCommits span)
    {
        if (span.Last is null) return "  history: none recorded for this file\n";

        // Named "since"/"last changed" rather than "created"/"author": history begins where the file was
        // last renamed, so the first commit recorded for a path is often a move and not its origin.
        return string.Create(CultureInfo.InvariantCulture,
            $"  history: since {Short(span.First)}; last changed {Short(span.Last)}\n");

        static string Short(AttributedBy? by) => by is null
            ? "unknown"
            : string.Create(CultureInfo.InvariantCulture, $"{by.Sha[..8]} {by.AuthoredAt:yyyy-MM-dd} {by.AuthorName}");
    }

    [McpServerTool(Name = "glob", ReadOnly = true, Idempotent = true, Title = "Find files by path pattern")]
    [Description("""
                 Lists indexed files whose qualified path matches a glob, with their line counts, e.g. `main/src/**/Features/**/*Commands.cs` or `*Handler.cs`. Matching is case-insensitive and `*` crosses directory separators, so a bare `*Handler.cs` finds every handler in every repository of the project.

                 - Use glob when you know the shape of a filename but not where it lives; one call replaces a directory walk.
                 - Use grep when you need the files that *contain* something, and list_tree when you want to understand the layout rather than find a known name.
                 - The glob spans every repository; pass `repo` to scope it to one. Brace expansion (`*.{cs,ts}`) is not supported and is refused rather than silently matching nothing.
                 - A file that is committed but not indexed (binary, oversized) is listed with the reason, so a name you expect never quietly disappears.
                 """)]
    public async Task<string> Glob(
        [Description(
            "Glob over the qualified path (`repo/path/in/repo`), e.g. \"main/src/**/*Commands.cs\" or \"*Entity.cs\".")]
        string glob,
        [Description("Repository slug to scope the glob to. Default: every repository in the project.")]
        string? repo = null,
        [Description("Maximum files to return, 1-2000. Default 500.")]
        int limit = DefaultGlobFiles,
        CancellationToken cancellationToken = default)
    {
        var project = BoundProject.Get(httpContextAccessor);
        return ToolReply.Render<GlobListing>(
            await files.GlobAsync(project.Slug, new GlobRequest(glob, repo, limit), cancellationToken), Format,
            "Narrow the glob, or lower limit.");

        string Format(GlobListing listing)
        {
            string pattern = listing.Glob;
            var repository = listing.Repository;
            if (listing.Total == 0)
            {
                if (repository is null)
                    return
                        $"No indexed file matches \"{pattern}\" in project '{project.Slug}' ({listing.Repositories.Sum(r => r.FileCount)} files in repositories {string.Join(", ", listing.Repositories.Select(r => r.Slug))}). "
                        + "Remember `*` crosses directories, so a bare \"*Commands.cs\" is usually the right shape, and the first path segment is the repository slug.";

                return $"No indexed file matches \"{pattern}\" in repository '{repository.Slug}'. "
                       + (listing.MatchesInOtherRepositories > 0
                           ? $"{listing.MatchesInOtherRepositories} {ToolReply.Plural(listing.MatchesInOtherRepositories.Value, "file")} match in the other repositories of project '{project.Slug}'; drop `repo` to see them."
                           : $"Nothing matches in the other repositories of project '{project.Slug}' either; try a wider glob.");
            }

            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture,
                $"{listing.Total} {ToolReply.Plural(listing.Total, "file")} matching \"{pattern}\"");
            if (repository is not null) text.Append(CultureInfo.InvariantCulture, $" in repository '{repository.Slug}'");
            bool truncated = listing.Total > listing.Files.Count;
            if (truncated)
            {
                // "Raise limit" is only a move where raising it can reach the total. Past MaxFiles the
                // advice is arithmetic the caller cannot do, and it reads like one they can.
                text.Append(CultureInfo.InvariantCulture,
                    $"; showing the first {listing.Files.Count} by path ({(listing.Total <= IndexReader.MaxFiles
                        ? "raise limit or narrow the glob"
                        : $"{IndexReader.MaxFiles} is the most limit can return, so narrow the glob")})");
            }

            text.Append(":\n");
            // The rule that produced the answer, where it bit. It is in the tool description too, which
            // is the wrong place to read it at the moment a one-level glob returned the whole tree.
            if (truncated && SwallowedDirectories(pattern))
                text.Append(CultureInfo.InvariantCulture,
                    $"`*` crosses directory separators, so \"{pattern}\" matched every level below it rather than one. For the layout of a directory use list_tree.\n");
            foreach (var file in listing.Files)
            {
                text.Append(CultureInfo.InvariantCulture, $"{file.LineCount,6}L  {file.QualifiedPath}");
                if (file.SkipReason is not null)
                    text.Append(CultureInfo.InvariantCulture, $"  (not indexed: {file.SkipReason})");
                text.Append('\n');
            }

            return text.ToString();
        }
    }

    /// <summary>
    ///     Whether the glob asked for one directory level and got every level below it: a lone <c>*</c>
    ///     standing as a whole path segment, as a shell glob writes "the entries of this directory".
    ///     Here <c>*</c> crosses separators, so that shape matches the subtree.
    ///     A name shape — <c>*Handler.cs</c>, or <c>src/*Commands.cs</c> — is what the tool is for and is
    ///     not warned about however many it matches, and neither is <c>**</c>, which is the caller asking
    ///     for every level. A glob with no <c>/</c> at all is excluded for the same reason: it named no
    ///     directory, so a bare <c>*</c> asked for the project and got it, and there is no level it
    ///     expected to stop at.
    /// </summary>
    private static bool SwallowedDirectories(string glob) =>
        glob.Contains('/', StringComparison.Ordinal)
        && glob.Split('/').Any(segment => segment == "*");

    [McpServerTool(Name = "list_tree", ReadOnly = true, Idempotent = true, Title = "List directories and files")]
    [Description("""
                 Lists directories and files of this project, like `tree -L depth`. Paths are qualified: the first segment is the repository slug, the rest is the path inside that repository (`main/src/Lib`). An empty path lists the repositories, and with depth 2 or more their top-level entries as well. Directories end with `/`.

                 - The listing is the index: the files the last refresh committed, with no working copy and no .gitignore filtering. A repository added since is not here yet; repo_info names it.
                 - A file that is committed but not indexed (binary, oversized) is listed with the reason, so a name you expect never quietly disappears.
                 - Use list_tree to understand the layout, glob to find a name whose shape you know, and grep for the files that contain something.
                 """)]
    public async Task<string> ListTree(
        [Description("Qualified path of the directory to list: `repo` or `repo/dir/sub`. Empty for the project root.")]
        string path = "",
        [Description("How many levels to descend, at least 1. Default 1 lists only direct children.")]
        int depth = 1,
        CancellationToken cancellationToken = default)
    {
        var project = BoundProject.Get(httpContextAccessor);
        return ToolReply.Render<TreeListing>(
            await files.TreeAsync(project.Slug, new TreeRequest(path, depth), cancellationToken), Format,
            "List a subdirectory, or use a smaller depth.");

        string Format(TreeListing listing)
        {
            string listed = listing.Directory.QualifiedPath;
            if (listing.Entries.Count == 0)
                return
                    $"{(listed.Length == 0 ? $"Project '{project.Slug}'" : $"Repository '{listed}'")} has no indexed files. Call repo_info to see what the index holds.";

            var text = new StringBuilder();
            if (listing.RepositoryLevel)
                text.Append(CultureInfo.InvariantCulture,
                    $"{project.Slug} (depth {depth}, {listing.Repositories} {ToolReply.Plural(listing.Repositories, "repository", "repositories")})\n");
            else
                // A single-repository project's root formats as the empty path (ADR-0006); it is headed by
                // the project, which is what the caller asked for.
                text.Append(CultureInfo.InvariantCulture,
                    $"{(listed.Length == 0 ? project.Slug : listed + "/")} (depth {depth}, {listing.Entries.Count} entries)\n");

            // Entries are printed relative to what was listed, as `tree` does; at the repository level the
            // qualified path already starts with the slug and nothing is stripped.
            int skip = listed.Length == 0 ? 0 : listed.Length + 1;
            foreach (var entry in listing.Entries.Take(MaxTreeEntries))
            {
                text.Append(entry.QualifiedPath, skip, entry.QualifiedPath.Length - skip);
                if (entry.Files is not null) text.Append('/');
                else if (entry.SkipReason is not null)
                    text.Append(CultureInfo.InvariantCulture, $"  (not indexed: {entry.SkipReason})");
                text.Append('\n');
            }

            if (listing.Entries.Count > MaxTreeEntries)
                text.Append(CultureInfo.InvariantCulture,
                    $"... {listing.Entries.Count - MaxTreeEntries} more entries omitted. List a subdirectory or use a smaller depth.\n");
            return text.ToString();
        }
    }

    [McpServerTool(Name = "list_extensions", ReadOnly = true, Idempotent = true, Title = "List file extensions")]
    [Description("""
                 Lists the file extensions in this project with how many files and lines each has, most files first. Use it to pick a value for grep's `ext` filter and to see what the project is written in. Pass `repo` to scope it to one repository. Files without an extension are grouped as `(none)`; files committed but not indexed (binary, oversized) are counted separately.
                 """)]
    public async Task<string> ListExtensions(
        [Description("Repository slug to scope to. Default: every repository in the project.")]
        string? repo = null,
        CancellationToken cancellationToken = default)
    {
        var project = BoundProject.Get(httpContextAccessor);
        return ToolReply.Render<ExtensionListing>(
            await files.ExtensionsAsync(project.Slug, new ExtensionsRequest(repo), cancellationToken), Format,
            "Scope to one repository with repo.");

        string Format(ExtensionListing listing)
        {
            var repository = listing.Repository;
            var extensions = listing.Extensions;
            if (extensions.Count == 0)
                return repository is null
                    ? $"Project '{project.Slug}' has no files in its index. Its repositories may be empty; repo_info shows what was indexed."
                    : $"Repository '{repository.Slug}' has no files in the index of project '{project.Slug}'.";

            var text = new StringBuilder();
            int total = extensions.Sum(e => e.Files);
            text.Append(repository is null
                ? $"Extensions in project '{project.Slug}'"
                : $"Extensions in repository '{repository.Slug}' of project '{project.Slug}'");
            text.Append(CultureInfo.InvariantCulture, $" ({total} {ToolReply.Plural(total, "file")}), most files first:\n");
            int width = Math.Max(6, extensions.Max(e => Label(e).Length));
            foreach (var extension in extensions)
            {
                text.Append(Label(extension).PadRight(width)).Append(CultureInfo.InvariantCulture,
                    $"  {extension.Files} {ToolReply.Plural(extension.Files, "file")} ({extension.Lines} {ToolReply.Plural(extension.Lines, "line")})");
                if (extension.Skipped > 0)
                    text.Append(CultureInfo.InvariantCulture, $", {extension.Skipped} not indexed");
                text.Append('\n');
            }

            return text.ToString();

            static string Label(ExtensionCount e)
            {
                return e.Extension.Length == 0 ? "(none)" : e.Extension;
            }
        }
    }

    // Named for the concept and not `outline`, though an editor's word for this is exactly that.
    // CODING_STANDARDS asks that a tool name trade on a shell verb a model already knows, and
    // `outline` is not one — it is an IDE's noun, and a model that has not met it has nothing to
    // transfer. `list_declarations` reads beside `list_tree`, `list_extensions` and `list_matches`,
    // and it spells the word CONTEXT.md defines, so the reply's vocabulary is the name's.
    [McpServerTool(Name = "list_declarations", ReadOnly = true, Idempotent = true,
        Title = "List what one file declares")]
    [Description("""
                 Lists the types and routines one file declares, in the order they are written, with the line each is on. It is a file's outline: the cheap first move after grep, glob or list_tree lands you on a file you do not know, costing one row per declaration instead of a read of the whole file.

                 - Takes a qualified path exactly as grep, glob, list_tree and read_file print one (`main/src/Api/Orders.cs`).
                 - Use it to orient, then read_file the line ranges that turn out to matter. find_definition is the other direction: it takes a name and finds the file, this takes a file and lists the names.
                 - IMPORTANT: declarations are read from the shape of each line in the language the file is written in, not from a compiler. A form no profile knows is one this did not find rather than one that is not there. Strong evidence, not proof.
                 - An empty answer always says which kind of empty it is: no profile covers the extension, or the language has no declarations that can be read from a line, or the file genuinely declares none. Those are three different facts and are never worded alike.
                 - Where a language announces a routine in one place and writes it in another — Delphi, a C header beside its source — each entry says which of the two it is.
                 """)]
    public async Task<string> ListDeclarations(
        [Description("Qualified path of the file, e.g. \"main/src/Api/Orders.cs\".")]
        string path,
        CancellationToken cancellationToken = default)
    {
        var project = BoundProject.Get(httpContextAccessor);
        return ToolReply.Render<DeclarationsResult>(
            await declarations.ForFileAsync(project.Slug, path, cancellationToken), Format,
            "Ask about a smaller file, or read the ranges you need with read_file.");
    }

    /// <summary>
    ///     The sentence a declaration reply ends with when every entry was read from the text, which
    ///     is every reply until a parser-backed analyser is registered. It is the same claim the file
    ///     page makes beside its Declarations panel (<c>declarations.ts</c>), said here too because an
    ///     agent reads the reply and not the panel, and a list without it reads like a parser's.
    /// </summary>
    private const string TextualCaveat =
        "Read from the shape of each line, not from a compiler. A form no profile knows is one this "
        + "did not find rather than one that is not there. Strong evidence, not proof.";

    /// <summary>
    ///     How the declarations were reached, which is what the reply's last line claims (ADR-0008).
    ///     Derived from the answers rather than written as a fact, and in three branches and not two:
    ///     a reply that is entirely a parser's must not talk about a textual half that is not there,
    ///     which is the sentence a two-way check prints the day the first parser is registered.
    /// </summary>
    private static string How(IReadOnlyList<FileDeclaration> declarations)
    {
        if (declarations.All(d => d.Evidence == Evidence.Text)) return TextualCaveat;
        return declarations.All(d => d.Evidence == Evidence.Parsed)
            ? "Parsed by a real parser for this language, so this is what the file declares and not what its lines look like."
            : "Parsed where the language has a parser here and read from the shape of the line elsewhere; "
              + "the textual half is strong evidence, not proof.";
    }

    private static string Format(DeclarationsResult result)
    {
        // The three empty answers before anything is counted, because two of them mean nothing was
        // scanned and a count of zero would be a fact about a scan that never ran.
        if (result.Coverage == DeclarationCoverage.Unreadable)
            return string.Create(CultureInfo.InvariantCulture,
                $"{result.QualifiedPath} is {result.LanguageName}, whose declarations are not something that can be read from a line. Nothing was scanned here, which is a different thing from the file declaring nothing. Read it with read_file, or grep it for what you are after.\n");

        var text = new StringBuilder();
        if (result.Coverage == DeclarationCoverage.Unprofiled)
            text.Append(CultureInfo.InvariantCulture,
                $"NOTE: no language profile covers this extension, so {result.QualifiedPath} was read with the conservative default shapes. What follows is thinner than a covered language's answer would be.\n");

        if (result.Declarations.Count == 0)
            return text.Append(result.Coverage == DeclarationCoverage.Unprofiled
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"Those shapes found no declaration in {result.QualifiedPath}.\n")
                    : string.Create(CultureInfo.InvariantCulture,
                        $"{result.QualifiedPath} ({result.LanguageName}) declares nothing its language writes as a type or a routine. Its lines were scanned and none of them is a declaration.\n"))
                // Nothing was found, so there is no evidence to derive the claim from; what a scan of
                // line shapes can say is what it would have said had it found something.
                .Append('\n').Append(TextualCaveat).Append('\n').ToString();

        text.Append(CultureInfo.InvariantCulture,
            $"{result.QualifiedPath} ({result.LanguageName}) declares {result.Declarations.Count} {ToolReply.Plural(result.Declarations.Count, "name")}, in file order:\n");
        // A list that stopped at the ceiling reads as the whole outline unless it says otherwise. "Has
        // more" and not "may have": the scan reads one declaration past what it reports, so a capped
        // list is one it has actually seen past the end of.
        if (result.Capped)
            text.Append(CultureInfo.InvariantCulture,
                $"NOTE: the first {FileDeclarations.MaxDeclarations} are listed and the file has more. A file declaring that many is generated; read it directly for the rest.\n");
        text.Append('\n');

        // Labelled once. The column width and the rows ask the same question of the same list, and the
        // label is a small allocation per declaration in a list that can be five hundred long.
        string[] labels = result.Declarations.Select(Label).ToArray();
        int width = Math.Min(MaxLabelWidth, labels.Max(l => l.Length));
        for (int i = 0; i < labels.Length; i++)
        {
            var declaration = result.Declarations[i];
            text.Append(CultureInfo.InvariantCulture, $"  {declaration.LineNumber,6}: {labels[i].PadRight(width)}  ")
                .Append(ToolReply.Clip(declaration.Text.Trim()));
            // Only where the language has the split. A blank marker on every C# line would train an
            // agent to skip the column on the languages where it carries the answer.
            if (declaration.Role is { } role)
                text.Append(role == DeclarationRole.Implementation ? "  (implementation)" : "  (declaration)");
            text.Append('\n');
        }

        return text.Append('\n').Append(How(result.Declarations)).Append('\n').ToString();
    }

    /// <summary>
    ///     What to call a declaration in the list: the member where there is one, with the type in
    ///     front of it where the line names both — Delphi's <c>procedure TCustomer.Save;</c> says which
    ///     type the routine is on, and dropping it would list three <c>Save</c>s that look like one.
    ///     The same rule as the file page's <c>declarationLabel</c>, because an agent and a reader
    ///     comparing the two surfaces are looking at one index.
    /// </summary>
    private static string Label(FileDeclaration declaration)
    {
        if (declaration.Member is not { } member) return declaration.Type ?? "";
        return declaration.Type is { } type ? $"{type}.{member}" : member;
    }

    // "file.cs:1:60" would otherwise parse as the file "file.cs:1" read from line 60, a real-looking
    // "no indexed file" answer to a typo; it is caught before the range is read.
    [GeneratedRegex(@"^(?<path>.+?):(?<a>\d+):(?<b>\d+)$")]
    private static partial Regex ColonRange();

    [GeneratedRegex(@"^(?<path>.+?):(?<a>\d+)(?:-(?<b>\d+))?$")]
    private static partial Regex Range();

    /// <summary>
    ///     Result of one <c>read_file</c> entry's parse: the path and the inclusive window, or the <see cref="Problem" />
    ///     with it. The range grammar is this tool's — an agent writes <c>path:120-180</c> — and stays
    ///     out of the query module, which is asked for a window and not for a string.
    /// </summary>
    private sealed record ReadTarget(string Path, int Start, int End, bool ExplicitRange, string? Problem = null)
    {
        public static ReadTarget Parse(string entry, int startLine, int maxLines)
        {
            string trimmed = entry.Trim();
            if (ColonRange().Match(trimmed) is { Success: true } colon)
                return Refused(trimmed,
                    $"\"{trimmed}\" uses a colon between the line numbers. The range separator is a dash: "
                    + $"\"{colon.Groups["path"].Value}:{colon.Groups["a"].Value}-{colon.Groups["b"].Value}\".");

            if (Range().Match(trimmed) is not { Success: true } range)
                return new ReadTarget(trimmed, startLine, startLine + maxLines - 1, false);

            int start = Math.Max(1, int.Parse(range.Groups["a"].Value, CultureInfo.InvariantCulture));
            if (!range.Groups["b"].Success)
                // "path:120" reads maxLines from there; "path:120-180" reads exactly that window.
                return new ReadTarget(range.Groups["path"].Value, start, start + maxLines - 1, false);

            int end = int.Parse(range.Groups["b"].Value, CultureInfo.InvariantCulture);
            if (end < start)
                return Refused(trimmed,
                    $"\"{trimmed}\" ends before it starts. Write the range as first-last: \"{range.Groups["path"].Value}:{end}-{start}\".");
            return new ReadTarget(range.Groups["path"].Value, start, end, true);
        }

        private static ReadTarget Refused(string path, string problem) => new(path, 0, 0, false, problem);
    }
}
