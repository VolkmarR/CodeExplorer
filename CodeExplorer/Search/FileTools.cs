using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace CodeExplorer;

/// <summary>
///     The index-backed MCP tools that are not searches (ADR-0005, <c>Search/</c>): reading a file,
///     finding files by name shape, and counting extensions. Every answer comes from the project
///     index alone; what the control database knows about the project is <c>repo_info</c>'s business.
/// </summary>
[McpServerToolType]
internal sealed partial class FileTools(IHttpContextAccessor httpContextAccessor, ProjectIndexes indexes)
{
    /// <summary>
    ///     A whole large class in one read. Higher would let a single default read spend the reply
    ///     budget on a generated file; the byte cap still bites first on such a file and says where to continue.
    /// </summary>
    private const int MaxLinesPerRead = 2000;

    private const int DefaultLinesPerRead = 400;

    /// <summary>Enough to show every handler in a mid-sized service; beyond it the glob is too wide to act on.</summary>
    private const int MaxGlobFiles = 2000;

    private const int DefaultGlobFiles = 500;

    /// <summary>A "did you mean" longer than this is a glob result, and glob is the better tool for it.</summary>
    private const int MaxSuggestions = 5;

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
        CancellationToken cancellationToken = default)
    {
        var project = BoundProject.Get(httpContextAccessor);
        if (paths.Length == 0) return "No paths given. Pass at least one qualified path such as `repo/src/File.cs`.";

        startLine = Math.Max(1, startLine);
        maxLines = Math.Clamp(maxLines, 1, MaxLinesPerRead);
        var targets = new List<ReadTarget>();
        foreach (string entry in paths)
        {
            var parsed = ReadTarget.Parse(entry, startLine, maxLines);
            if (parsed.Problem is not null) return parsed.Problem;
            targets.Add(parsed);
        }

        using var index = await FileQueries.OpenAsync(indexes, project.Slug, cancellationToken);
        if (index is null) return ToolReply.NoIndex(project.Slug);
        var scope = new Scope(project, index, await index.RepositoriesAsync(cancellationToken),
            await index.PathsAsync(project.Slug, cancellationToken));

        var text = new StringBuilder();
        for (int i = 0; i < targets.Count; i++)
        {
            if (text.Length > 0) text.Append('\n');
            // Whatever the entries before this one left unused is handed on, so four small windows
            // and one large one read in full where an equal split would truncate the large one.
            int allowance = Math.Max(0, ToolReply.MaxOutputChars - text.Length) / (targets.Count - i);
            await AppendReadAsync(text, scope, targets[i], allowance, cancellationToken);
        }

        return text.ToString();
    }

    private static async Task AppendReadAsync(
        StringBuilder text, Scope scope, ReadTarget target, int allowance, CancellationToken cancellationToken)
    {
        var paths = scope.Paths;
        var qualified = paths.Parse(target.Path);
        if (qualified is null || qualified.PathInRepository.Length == 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"'{target.Path}' names no file: {scope.PathRule} Write it like `{paths.Example()}`.\n");
            return;
        }

        // The slug is matched like the rest of the path, case-insensitively, and the file lookup gets
        // the spelling the index holds so the two cannot disagree. A single-repository project wrote no
        // slug for us to match, and the one it resolves to is the only one there is.
        var repository = scope.Find(qualified.RepositorySlug);
        if (repository is null)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{scope.UnknownRepository(qualified.RepositorySlug)} The first path segment must be one of these.\n");
            return;
        }

        qualified = qualified with { RepositorySlug = repository.Slug };
        string spelled = paths.Format(qualified);
        var file = await scope.Index.FindFileAsync(spelled, cancellationToken);
        if (file is null)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"No indexed file '{spelled}' in repository '{repository.Slug}' of project '{scope.Project.Slug}'. ");
            string name = qualified.PathInRepository[(qualified.PathInRepository.LastIndexOf('/') + 1)..];
            var similar = await scope.Index.FilesNamedAsync(name, MaxSuggestions, cancellationToken);
            text.Append(similar.Count > 0
                ? $"Did you mean {string.Join(" or ", similar)}? Otherwise use glob or list_tree to locate it.\n"
                : "Use glob or list_tree to locate it; the path is case-insensitive here but must otherwise match the committed path.\n");
            return;
        }

        if (file.SkipReason is not null)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{file.QualifiedPath}  -  not indexed: {file.SkipReason} ({ToolReply.Bytes(file.SizeBytes)}). Its content is not in the index, so it cannot be read here.\n");
            return;
        }

        text.Append(CultureInfo.InvariantCulture,
            $"{file.QualifiedPath}  -  {file.LineCount} {ToolReply.Plural(file.LineCount, "line")}, {ToolReply.Bytes(file.SizeBytes)}");
        if (target.Start > file.LineCount)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"\n  (only {file.LineCount} {ToolReply.Plural(file.LineCount, "line")}; startLine {target.Start} is past the end)\n");
            return;
        }

        int end = Math.Min(file.LineCount, target.End);
        if (target.Start > 1 || end < file.LineCount)
            text.Append(CultureInfo.InvariantCulture, $" (lines {target.Start}-{end} of {file.LineCount})");
        text.Append("\n\n");

        var lines = await scope.Index.LinesAsync(file.FileId, target.Start, end, cancellationToken);
        int width = end.ToString(CultureInfo.InvariantCulture).Length;
        int budget = text.Length + allowance;
        int last = end;
        for (int i = 0; i < lines.Count; i++)
        {
            if (text.Length >= budget)
            {
                last = target.Start + i - 1;
                text.Append(CultureInfo.InvariantCulture,
                    $"  ... {end - last} more {ToolReply.Plural(end - last, "line")} in this window omitted: the reply is capped at {ToolReply.MaxOutputChars / 1024} KB shared by every entry.\n");
                break;
            }

            text.Append((target.Start + i).ToString(CultureInfo.InvariantCulture).PadLeft(width)).Append("  ")
                .Append(ToolReply.Clip(lines[i])).Append('\n');
        }

        // An explicit range is what the caller asked for; only a default window or a cap stopped short of
        // what they wanted, and then the next call is spelled out.
        if (last < file.LineCount && (!target.ExplicitRange || last < end))
            text.Append(CultureInfo.InvariantCulture, $"Continue with \"{file.QualifiedPath}:{last + 1}\".\n");
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
        string pattern = glob.Trim().Replace('\\', '/');
        if (MalformedGlob(pattern) is { } malformed) return malformed;
        limit = Math.Clamp(limit, 1, MaxGlobFiles);

        using var index = await FileQueries.OpenAsync(indexes, project.Slug, cancellationToken);
        if (index is null) return ToolReply.NoIndex(project.Slug);
        var scope = new Scope(project, index, await index.RepositoriesAsync(cancellationToken),
            await index.PathsAsync(project.Slug, cancellationToken));
        (var repository, string? unknown) = scope.Resolve(repo);
        if (unknown is not null) return $"{unknown} Drop `repo` to search all of them.";

        var result = await index.GlobAsync(pattern, repository?.Slug, limit, cancellationToken);
        if (result.Total == 0)
        {
            if (repository is null)
                return
                    $"No indexed file matches \"{pattern}\" in project '{project.Slug}' ({scope.Repositories.Sum(r => r.FileCount)} files in repositories {scope.Slugs}). "
                    + "Remember `*` crosses directories, so a bare \"*Commands.cs\" is usually the right shape, and the first path segment is the repository slug.";
            return $"No indexed file matches \"{pattern}\" in repository '{repository.Slug}'. "
                   + (result.MatchesInOtherRepositories > 0
                       ? $"{result.MatchesInOtherRepositories} {ToolReply.Plural(result.MatchesInOtherRepositories.Value, "file")} match in the other repositories of project '{project.Slug}'; drop `repo` to see them."
                       : $"Nothing matches in the other repositories of project '{project.Slug}' either; try a wider glob.");
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{result.Total} {ToolReply.Plural(result.Total, "file")} matching \"{pattern}\"");
        if (repository is not null) text.Append(CultureInfo.InvariantCulture, $" in repository '{repository.Slug}'");
        if (result.Total > result.Files.Count)
            text.Append(CultureInfo.InvariantCulture,
                $"; showing the first {result.Files.Count} by path (raise limit or narrow the glob)");
        text.Append(":\n");
        foreach (var file in result.Files)
        {
            text.Append(CultureInfo.InvariantCulture, $"{file.LineCount,6}L  {file.QualifiedPath}");
            if (file.SkipReason is not null)
                text.Append(CultureInfo.InvariantCulture, $"  (not indexed: {file.SkipReason})");
            text.Append('\n');
        }

        return ToolReply.Cap(text.ToString(), "Narrow the glob, or lower limit.");
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

        using var index = await FileQueries.OpenAsync(indexes, project.Slug, cancellationToken);
        if (index is null) return ToolReply.NoIndex(project.Slug);
        var scope = new Scope(project, index, await index.RepositoriesAsync(cancellationToken),
            await index.PathsAsync(project.Slug, cancellationToken));
        (var repository, string? unknown) = scope.Resolve(repo);
        if (unknown is not null) return $"{unknown} Drop `repo` to cover all of them.";

        var extensions = await index.ExtensionsAsync(repository?.Slug, cancellationToken);
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

    /// <summary>
    ///     Glob shapes the SQL operator accepts and matches nothing with. Malformed input must never look
    ///     like a real negative: an empty answer is something the caller acts on.
    /// </summary>
    private static string? MalformedGlob(string glob)
    {
        if (glob.Length == 0)
            return "The glob is empty. Pass a pattern such as \"*.cs\" or \"main/src/**/*Handler.cs\".";
        if (glob.Contains('{') || glob.Contains('}'))
            return $"Brace expansion is not supported, so \"{glob}\" matches nothing. Use one call per alternative, "
                   + "or widen the glob (\"**/*.cs\" then read the list) and filter the result yourself.";
        if (glob.EndsWith('/'))
            return $"A trailing slash matches nothing: \"{glob}\" is a directory, not a file pattern. "
                   + $"Use \"{glob}**\" for everything under it, or list_tree to see the layout.";
        // A '[' opens a character class; without its ']' the operator matches nothing and says so to no one.
        if (glob.Count(c => c == '[') != glob.Count(c => c == ']'))
            return
                $"\"{glob}\" has an unbalanced [ ]: a [ opens a character class such as [0-9] and matches nothing without its ]. "
                + "Close it, or write the character you meant.";
        return null;
    }

    // "file.cs:1:60" would otherwise parse as the file "file.cs:1" read from line 60, a real-looking
    // "no indexed file" answer to a typo; it is caught before the range is read.
    [GeneratedRegex(@"^(?<path>.+?):(?<a>\d+):(?<b>\d+)$")]
    private static partial Regex ColonRange();

    [GeneratedRegex(@"^(?<path>.+?):(?<a>\d+)(?:-(?<b>\d+))?$")]
    private static partial Regex Range();

    /// <summary>
    ///     Result of one <c>read_file</c> entry's parse: the path and the inclusive window, or the <see cref="Problem" />
    ///     with it.
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

    /// <summary>
    ///     What every tool call knows once the index is open: the project, the connection and the
    ///     repositories in the index. Owns repository resolution so the three tools name an unknown
    ///     slug the same way.
    /// </summary>
    /// <param name="Project">The project the session is bound to, for the messages that name it.</param>
    /// <param name="Index">The open index, bound to that project for this one call.</param>
    /// <param name="Repositories">What the last build read, which is what a path may name.</param>
    /// <param name="Paths">
    ///     How this project names files (ADR-0006), read from the index by
    ///     <see cref="FileQueries.PathsAsync" />. It comes from the index rather than the control
    ///     database so that a tool parses and prints paths the way the index it is reading spells them,
    ///     and so that nothing in <c>Search/</c> needs anything but the index (ADR-0005).
    /// </param>
    private sealed record Scope(
        Project Project,
        FileQueries Index,
        IReadOnlyList<IndexedRepository> Repositories,
        ProjectPaths Paths)
    {
        public string Slugs => string.Join(", ", Repositories.Select(r => r.Slug));

        /// <summary>
        ///     What a path in this project is made of, for the message that says one was not. The two
        ///     shapes are described in one place so a tool cannot explain one project's naming in the
        ///     other's words (ADR-0006).
        /// </summary>
        public string PathRule => Paths.SingleRepository
            ? "this project holds one repository, so a path is the path inside it."
            : $"a qualified path must start with a repository slug, then the path inside it. Repositories: {Slugs}.";

        public IndexedRepository? Find(string slug) =>
            Repositories.FirstOrDefault(r => string.Equals(r.Slug, slug, StringComparison.OrdinalIgnoreCase));

        /// <summary>An optional <c>repo</c> argument: null repository for "all", or the explanation when the slug is unknown.</summary>
        public (IndexedRepository? Repository, string? Unknown) Resolve(string? repo)
        {
            if (string.IsNullOrWhiteSpace(repo)) return (null, null);
            var repository = Find(repo.Trim());
            return repository is null ? (null, UnknownRepository(repo.Trim())) : (repository, null);
        }

        public string UnknownRepository(string slug) =>
            $"No repository '{slug}' in project '{Project.Slug}'. Repositories: {Slugs}.";
    }
}
