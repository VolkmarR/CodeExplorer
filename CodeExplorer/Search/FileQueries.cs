namespace CodeExplorer;

/// <summary>One entry of a read: a qualified path and the inclusive line window asked of it.</summary>
public sealed record FileWindow(string Path, int Start, int End);

/// <summary>
///     Everything a file read asks for. Several windows in one request share one open of the index,
///     which is the usual case after a grep that hit several places. <see cref="Suggestions" /> is
///     whether a miss offers the files elsewhere with the same leaf name: worth it where an agent chose
///     the path, noise where a browser followed a link this server printed.
/// </summary>
public sealed record ReadRequest(IReadOnlyList<FileWindow> Windows, bool WithHistory, bool Suggestions);

/// <summary>
///     One entry's answer: the file and the lines of its window, or the <see cref="Problem" /> saying why
///     the path names none. Never both. A miss is one entry's answer and not the request's, because the
///     other entries are still read: an agent that quoted four paths and got one wrong is shown three
///     files and one sentence.
///     <see cref="Window" /> is the one that was read, clamped to the file: a window starting past the
///     end has no lines, and the renderer says so from the counts. <see cref="History" /> is set when
///     it was asked for, with both commits null where none is recorded — which is a different fact from
///     not having asked.
/// </summary>
public sealed record FileRead(
    FileWindow Window,
    IndexedFile? File,
    Problem? Problem,
    IReadOnlyList<string> Lines,
    FileCommits? History);

/// <summary>The entries of a read, in the order they were asked for.</summary>
public sealed record ReadResult(IReadOnlyList<FileRead> Files) : Outcome;

/// <summary>
///     Everything a glob asks for. The limit is clamped to <see cref="IndexReader.MaxFiles" />, and is
///     the size of a page: <see cref="Page" /> walks a match too wide to read at once. An agent asks
///     for one page of a limit it chose and reads the total; the operator's view walks them.
/// </summary>
public sealed record GlobRequest(string Glob, string? Repository, int Limit, int Page = 1);

/// <summary>
///     The files a glob matched, within <see cref="Repository" /> when one was named. An empty answer
///     is told apart from a miss by what rides along with it: <see cref="MatchesInOtherRepositories" />
///     says whether the scope hid them, and <see cref="Repositories" /> is what the project holds, so a
///     renderer can say what was searched rather than only that nothing was found.
/// </summary>
public sealed record GlobListing(
    string Glob,
    IndexedRepository? Repository,
    int Total,
    IReadOnlyList<IndexedFile> Files,
    int? MatchesInOtherRepositories,
    IReadOnlyList<IndexedRepository> Repositories,
    int Page = 1) : Outcome;

/// <summary>Everything a tree listing asks for: a directory, blank for the project level, and a depth.</summary>
public sealed record TreeRequest(string Path, int Depth);

/// <summary>
///     One level of the tree, or more with a depth. <see cref="RepositoryLevel" /> says the entries are
///     repositories rather than directories, which an empty path no longer implies: a single-repository
///     project's root is already inside its one repository (ADR-0006). An empty listing here is a level
///     that exists and holds nothing — a project or repository with no indexed files — because a path
///     that names no directory is a <see cref="Problem" /> and not an empty answer.
/// </summary>
public sealed record TreeListing(
    IndexedDirectory Directory,
    bool RepositoryLevel,
    IReadOnlyList<TreeItem> Entries,
    int Repositories) : Outcome;

/// <summary>Everything an extension count asks for.</summary>
public sealed record ExtensionsRequest(string? Repository);

/// <summary>What each extension accounts for, most files first, within <see cref="Repository" /> when one was named.</summary>
public sealed record ExtensionListing(IndexedRepository? Repository, IReadOnlyList<ExtensionCount> Extensions) : Outcome;

/// <summary>
///     The index-backed answers that are not text searches: what a file says, which files a name
///     shape matches, how the tree is laid out, what the project is written in. One module behind the
///     MCP tools and the operator's pages alike, so the decisions inside — what an empty level means,
///     what a malformed glob is refused with, how far one window may reach — are taken once and both
///     answer the same way; only the rendering differs. Every answer is an <see cref="Outcome" />:
///     a semantic failure is an answer, never an exception (CODING_STANDARDS).
/// </summary>
public sealed class FileQueries(ProjectIndexes indexes)
{
    /// <summary>Named on the search telemetry, so a dashboard can tell a listing apart from a scan.</summary>
    private const string Engine = "index listing";

    /// <summary>
    ///     The most lines one window may reach, whoever asks. A file view scrolls and asks for the whole
    ///     file, and the index refuses files over <c>Index:MaxFileBytes</c> (4 MiB by default), which
    ///     bounds this well below it. A tool that protects an agent's context sets a lower ceiling of its
    ///     own; this one protects the server.
    /// </summary>
    private const int MaxLinesPerWindow = 100_000;

    /// <summary>
    ///     The windows asked for, each answered on its own, over one open of the index. The one refusal
    ///     that is the request's rather than an entry's is having asked for nothing.
    /// </summary>
    public async Task<Outcome> ReadAsync(string slug, ReadRequest request, CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await RunReadAsync(slug, request, cancellationToken);
        if (outcome is ReadResult result)
            recording.Matched(Engine, result.Files.Count(f => f.File is not null),
                result.Files.Sum(f => (long)f.Lines.Count));
        else recording.Problem();
        return outcome;
    }

    private Task<Outcome> RunReadAsync(string slug, ReadRequest request, CancellationToken cancellationToken)
    {
        if (request.Windows.Count == 0)
            return Task.FromResult<Outcome>(
                new Problem("No paths given. Pass at least one qualified path such as `repo/src/File.cs`."));

        return IndexReader.OverIndexAsync(indexes, slug, null, async (index, token) =>
        {
            var reads = new List<FileRead>(request.Windows.Count);
            foreach (var window in request.Windows)
            {
                // The path rule and the refusal sentences are the reader's, so an agent that got the path
                // wrong is told the same thing here as by imports or file_history.
                var (file, problem) = await index.LocateAsync(window.Path, request.Suggestions, token);
                if (file is null)
                {
                    reads.Add(new FileRead(window, null, problem, [], null));
                    continue;
                }

                int start = Math.Max(1, window.Start);
                int end = (int)Math.Min(file.LineCount, Math.Min(window.End, (long)start + MaxLinesPerWindow - 1));
                IReadOnlyList<string> lines = file.SkipReason is null && start <= end
                    ? await index.LinesAsync(file.FileId, start, end, token)
                    : [];
                // Carried on the read and not fetched separately: they are columns on the row the read
                // already has in hand, so a second request would be one for data this one was holding.
                var history = request.WithHistory ? await index.FileCommitsAsync(file.FileId, token) : null;
                reads.Add(new FileRead(window with { Start = start, End = end }, file, null, lines, history));
            }

            return new ReadResult(reads);
        }, cancellationToken);
    }

    public async Task<Outcome> GlobAsync(string slug, GlobRequest request, CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await RunGlobAsync(slug, request, cancellationToken);
        if (outcome is GlobListing listing)
            recording.Matched(Engine, listing.Total, listing.Files.Sum(f => (long)f.LineCount));
        else recording.Problem();
        return outcome;
    }

    private Task<Outcome> RunGlobAsync(string slug, GlobRequest request, CancellationToken cancellationToken)
    {
        string pattern = request.Glob.Trim().Replace('\\', '/');
        if (MalformedGlob(pattern) is { } malformed) return Task.FromResult<Outcome>(new Problem(malformed));

        return IndexReader.OverIndexAsync(indexes, slug, request.Repository, async (index, token) =>
        {
            int page = Math.Max(1, request.Page);
            var result = await index.GlobAsync(pattern, request.Limit, (page - 1) * request.Limit, token);
            return new GlobListing(pattern, index.Repository, result.Total, result.Files,
                result.MatchesInOtherRepositories, await index.RepositoriesAsync(token), page);
        }, cancellationToken);
    }

    public async Task<Outcome> TreeAsync(string slug, TreeRequest request, CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await RunTreeAsync(slug, request, cancellationToken);
        if (outcome is TreeListing listing)
            recording.Matched(Engine, listing.Entries.Count, listing.Entries.Sum(e => e.Lines));
        else recording.Problem();
        return outcome;
    }

    private Task<Outcome> RunTreeAsync(string slug, TreeRequest request, CancellationToken cancellationToken)
    {
        if (request.Depth < 1)
            return Task.FromResult<Outcome>(new Problem(
                "depth must be at least 1. Use 1 for direct children, 2 to include grandchildren, and so on."));

        return IndexReader.OverDirectoryAsync(indexes, slug, request.Path, async (index, directory, token) =>
        {
            // Null is the repository level, which a single-repository project does not have: there the
            // project level is that repository's own top level (ADR-0006).
            var paths = await index.PathsAsync(token);
            var location = directory.Repository is null
                ? paths.SingleRepository ? new QualifiedPath(paths.RepositorySlug, "") : null
                : new QualifiedPath(directory.Repository.Slug, directory.PathInRepository);

            var entries = await index.TreeAsync(location, request.Depth, token);
            // A root with nothing under it is a level that exists and is empty. Anything deeper that
            // lists nothing is not there — or is a file, which is the likelier mistake and gets its own
            // sentence, because the fix is a different tool.
            if (entries.Count == 0 && location is { PathInRepository.Length: > 0 })
            {
                if (await index.FindFileAsync(directory.QualifiedPath, token) is not null)
                    return new Problem(
                        $"'{directory.QualifiedPath}' is a file, not a directory, in project '{index.ProjectSlug}'. Use read_file to read it.");
                return new Problem(
                    $"'{location.PathInRepository}' is not a directory in repository '{location.RepositorySlug}'. Call list_tree with a parent path to see what exists there.");
            }

            var repositories = await index.RepositoriesAsync(token);
            return new TreeListing(directory, location is null, entries, repositories.Count);
        }, cancellationToken);
    }

    public async Task<Outcome> ExtensionsAsync(string slug, ExtensionsRequest request,
        CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await IndexReader.OverIndexAsync(indexes, slug, request.Repository, async (index, token) =>
            new ExtensionListing(index.Repository,
                (await IndexQueries.ExtensionCountsAsync(index.Connection, index.Repository?.Slug, token))
                .OrderByDescending(e => e.Files)
                .ThenBy(e => e.Extension, StringComparer.Ordinal)
                .ToList()), cancellationToken);
        if (outcome is ExtensionListing listing)
            recording.Matched(Engine, listing.Extensions.Sum(e => e.Files), listing.Extensions.Sum(e => e.Lines));
        else recording.Problem();
        return outcome;
    }

    /// <summary>
    ///     Glob shapes the SQL operator accepts and matches nothing with. Malformed input must never look
    ///     like a real negative: an empty answer is something the caller acts on.
    /// </summary>
    private static string? MalformedGlob(string glob)
    {
        if (glob.Length == 0)
            return "The glob is empty. Pass a pattern such as \"*.cs\" or \"main/src/*Handler.cs\".";
        if (glob.Contains('{') || glob.Contains('}'))
            return $"Brace expansion is not supported, so \"{glob}\" matches nothing. Use one call per alternative, "
                   // "*.cs" and not "**/*.cs": `*` crosses separators, so the leading "**/" adds nothing
                   // except a required '/', which drops a file sitting at the root of a path.
                   + "or widen the glob (\"*.cs\" then read the list) and filter the result yourself.";
        if (glob.EndsWith('/'))
            return $"A trailing slash matches nothing: \"{glob}\" is a directory, not a file pattern. "
                   + $"Use \"{glob}*\" for everything under it — `*` crosses separators, so that is the "
                   + "whole subtree — or list_tree for the entries of the directory itself.";
        // A '[' opens a character class; without its ']' the operator matches nothing and says so to no one.
        if (glob.Count(c => c == '[') != glob.Count(c => c == ']'))
            return
                $"\"{glob}\" has an unbalanced [ ]: a [ opens a character class such as [0-9] and matches nothing without its ]. "
                + "Close it, or write the character you meant.";
        return null;
    }
}
