using CodeExplorer.Infrastructure;
using CodeExplorer.Reading;

namespace CodeExplorer.Search;

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
    int Page,
    int PageSize) : Outcome;

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
public sealed class FileQueries(IndexReaders readers)
{
    /// <summary>Named on the search telemetry, so a dashboard can tell a listing apart from a scan.</summary>
    private const string _engine = "index listing";

    /// <summary>
    ///     The most lines one read may load, across all of its windows, whoever asks. A file view scrolls
    ///     and asks for the whole file. <c>Index:MaxFileBytes</c> (25 MiB by default) admits files longer
    ///     than this, and a window over one stops here. It was a ceiling per window until
    ///     GHSA-v284-9964-6mjr: a thousand copies of one explicit range loaded a hundred million lines
    ///     before the reply was capped. A tool that protects an agent's context sets a lower ceiling of
    ///     its own; this one protects the server.
    /// </summary>
    public const int MaxLinesPerRead = 100_000;

    /// <summary>
    ///     The most windows one read may ask for. Each is a locate and a read of the index, and the reply
    ///     budget is shared between them, so past a hundred every window is a few lines long and the call
    ///     is better split (GHSA-v284-9964-6mjr).
    /// </summary>
    public const int MaxWindows = 100;

    /// <summary>
    ///     The deepest tree listing one call may ask for. The depth is a row generator in the tree
    ///     statement, cross-joined with every file under the listed directory, so it is work the caller
    ///     sizes and not only output (GHSA-v284-9964-6mjr). Sixty-four levels is deeper than any source
    ///     tree the index has held; past it the listing is all of the subtree anyway.
    /// </summary>
    public const int MaxTreeDepth = 64;

    /// <summary>
    ///     The windows asked for, each answered on its own, over one open of the index. The one refusal
    ///     that is the request's rather than an entry's is having asked for nothing.
    /// </summary>
    public Task<Outcome> ReadAsync(string slug, ReadRequest request, CancellationToken cancellationToken) =>
        Telemetry.Search(slug, _engine, () => RunReadAsync(slug, request, cancellationToken),
            (ReadResult result) => new Telemetry.Measured(result.Files.Count(f => f.File is not null),
                result.Files.Sum(f => (long)f.Lines.Count)));

    private Task<Outcome> RunReadAsync(string slug, ReadRequest request, CancellationToken cancellationToken)
    {
        if (request.Windows.Count == 0)
            return Task.FromResult<Outcome>(
                new Problem("No paths given. Pass at least one qualified path such as `repo/src/File.cs`."));
        if (request.Windows.Count > MaxWindows)
            return Task.FromResult<Outcome>(new Problem(
                $"One read takes at most {MaxWindows} entries, and this one has {request.Windows.Count}. Split it into several calls."));

        return readers.OverIndexAsync(slug, null, async (index, token) =>
        {
            var reads = new List<FileRead>(request.Windows.Count);
            int budget = MaxLinesPerRead;
            // Per request, because the tool invites several windows into one file and each was locating
            // it and reading its history again (#180). The lease holds the index still, so a remembered
            // answer is the answer. Keyed by the path exactly as written, because a miss quotes that
            // spelling back, so a respelled path is located on its own and gets its own sentence.
            var located = new Dictionary<string, (IndexedFile? File, Problem? Problem)>(StringComparer.Ordinal);
            var commits = new Dictionary<long, FileCommits>();
            foreach (var window in request.Windows)
            {
                // The entries after the budget is spent are each told so, in place, rather than the call
                // failing: the windows before them were read and are worth returning.
                if (budget == 0)
                {
                    reads.Add(new FileRead(window, null, new Problem(
                        $"'{window.Path}' was not read: the entries before it already read {MaxLinesPerRead} lines, the most one call reads. Read it in a call of its own."),
                        [], null));
                    continue;
                }

                // The path rule and the refusal sentences are the reader's, so an agent that got the path
                // wrong is told the same thing here as by imports or file_history.
                if (!located.TryGetValue(window.Path, out var found))
                    located[window.Path] = found = await index.LocateAsync(window.Path, request.Suggestions, token);
                var (file, problem) = found;
                if (file is null)
                {
                    reads.Add(new FileRead(window, null, problem, [], null));
                    continue;
                }

                int start = Math.Max(1, window.Start);
                int end = (int)Math.Min(file.LineCount, Math.Min(window.End, (long)start + budget - 1));
                IReadOnlyList<string> lines = file.SkipReason is null && start <= end
                    ? await index.LinesAsync(file.FileId, start, end, token)
                    : [];
                budget -= lines.Count;
                // Carried on the read and not fetched separately: they are columns on the row the read
                // already has in hand, so a second request would be one for data this one was holding.
                FileCommits? history = null;
                if (request.WithHistory && !commits.TryGetValue(file.FileId, out history))
                    commits[file.FileId] = history = await index.FileCommitsAsync(file.FileId, token);
                reads.Add(new FileRead(window with { Start = start, End = end }, file, null, lines, history));
            }

            return new ReadResult(reads);
        }, cancellationToken);
    }

    public Task<Outcome> GlobAsync(string slug, GlobRequest request, CancellationToken cancellationToken) =>
        Telemetry.Search(slug, _engine, () => RunGlobAsync(slug, request, cancellationToken),
            (GlobListing listing) =>
                new Telemetry.Measured(listing.Total, listing.Files.Sum(f => (long)f.LineCount)));

    private Task<Outcome> RunGlobAsync(string slug, GlobRequest request, CancellationToken cancellationToken)
    {
        string pattern = request.Glob.Trim().Replace('\\', '/');
        if (MalformedGlob(pattern) is { } malformed) return Task.FromResult<Outcome>(new Problem(malformed));

        return readers.OverIndexAsync(slug, request.Repository, async (index, token) =>
        {
            var result = await index.GlobAsync(pattern, request.Limit, request.Page, token);
            return new GlobListing(pattern, index.Repository, result.Total, result.Files,
                result.MatchesInOtherRepositories, await index.RepositoriesAsync(token), result.Page, result.PageSize);
        }, cancellationToken);
    }

    public Task<Outcome> TreeAsync(string slug, TreeRequest request, CancellationToken cancellationToken) =>
        Telemetry.Search(slug, _engine, () => RunTreeAsync(slug, request, cancellationToken),
            (TreeListing listing) =>
                new Telemetry.Measured(listing.Entries.Count, listing.Entries.Sum(e => e.Lines)));

    private Task<Outcome> RunTreeAsync(string slug, TreeRequest request, CancellationToken cancellationToken)
    {
        if (request.Depth < 1)
            return Task.FromResult<Outcome>(new Problem(
                "depth must be at least 1. Use 1 for direct children, 2 to include grandchildren, and so on."));
        if (request.Depth > MaxTreeDepth)
            return Task.FromResult<Outcome>(new Problem(
                $"depth may be at most {MaxTreeDepth}, which already reaches the bottom of any source tree. "
                + $"Pass {MaxTreeDepth} for the whole subtree, or list a subdirectory."));

        return readers.OverDirectoryAsync(slug, request.Path, async (index, directory, token) =>
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

                string miss = $"'{location.PathInRepository}' is not a directory in repository "
                              + $"'{location.RepositorySlug}'. ";
                // The same diagnosis the file miss gives, in the same words: an agent that prefixed a
                // path with the slug will do it to both tools, and being told the rule by one of them
                // and left guessing by the other is the round-trip this was meant to remove (#111).
                if (await index.DirectorySlugPrefixAdviceAsync(request.Path ?? "", token) is { } slugged)
                    return new Problem(miss + slugged);

                return new Problem(miss + "Call list_tree with a parent path to see what exists there.");
            }

            var repositories = await index.RepositoriesAsync(token);
            return new TreeListing(directory, location is null, entries, repositories.Count);
        }, cancellationToken);
    }

    public Task<Outcome> ExtensionsAsync(string slug, ExtensionsRequest request,
        CancellationToken cancellationToken) =>
        Telemetry.Search(slug, _engine,
            () => readers.OverIndexAsync(slug, request.Repository, async (index, token) =>
                new ExtensionListing(index.Repository,
                    (await IndexQueries.ExtensionCountsAsync(index.Connection, index.Repository?.Slug, ExcludedPaths.None, token))
                    .OrderByDescending(e => e.Files)
                    .ThenBy(e => e.Extension, StringComparer.Ordinal)
                    .ToList()), cancellationToken),
            (ExtensionListing listing) => new Telemetry.Measured(listing.Extensions.Sum(e => e.Files),
                listing.Extensions.Sum(e => e.Lines)));

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
        if (GlobRegex.ReversedRange(glob) is { } reversed)
            return $"\"{glob}\" has the range [{reversed}], which runs backwards, so no character falls in it and the glob matches nothing. "
                   + "Write it low to high.";
        return null;
    }
}
