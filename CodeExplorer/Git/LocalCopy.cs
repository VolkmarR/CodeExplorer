using LibGit2Sharp;

namespace CodeExplorer.Git;

/// <summary>
///     What opening a repository's local copy for a refresh came to. The answers a clone can give are
///     cases and not a null, a bool and a scan the caller has to run in the right order:
///     <see cref="Opened" /> hands over the copy to read, and the <see cref="Refused" /> cases carry
///     the sentence the refresh reports in the repository's place. Nothing is open behind a refusal.
/// </summary>
public abstract record CloneOpen
{
    private CloneOpen() { }

    /// <summary>
    ///     The copy is the caller's to read and to dispose. <see cref="Note" /> is a sentence for the
    ///     refresh status about a choice made on the operator's behalf while opening it, such as the
    ///     branch a detached remote is followed on (#288), and null when nothing was chosen.
    /// </summary>
    public sealed record Opened(LocalCopy Copy, string? Note = null) : CloneOpen;

    /// <summary>The repository is not to be read, and <see cref="Explanation" /> says why in operator-facing prose.</summary>
    public abstract record Refused(string Explanation) : CloneOpen;

    /// <summary>A remote with no commits yet: an answer, not a failure.</summary>
    public sealed record Empty(string Explanation) : Refused(Explanation);

    /// <summary>A <c>.gitattributes</c> in HEAD declares <c>filter=lfs</c>, which this server cannot read honestly.</summary>
    public sealed record UsesLfs(string Explanation) : Refused(Explanation);

    /// <summary>
    ///     A repository on the server's own disk while local repositories are switched off. Decided
    ///     before libgit2 is asked, so nothing is cloned or fetched from it (GHSA-5373-pppr-q3q9).
    /// </summary>
    public sealed record LocalNotAllowed(string Explanation) : Refused(Explanation);

    /// <summary>
    ///     A stored credential beside a URL that would carry it unencrypted. Decided before libgit2 is
    ///     asked, so the token is never handed to it (GHSA-4f8q-c6jj-fr44).
    /// </summary>
    public sealed record ClearTextCredential(string Explanation) : Refused(Explanation);
}

/// <summary>
///     One file committed at HEAD. The size is read from the object header and the content only on
///     <see cref="Text" />, so a build can decide to skip a file without loading it. The blob id
///     stays here: no LibGit2Sharp type leaves <c>Git/</c>.
/// </summary>
public sealed class CommittedFile
{
    private readonly ObjectId _id;
    private readonly BlobReader _blobs;

    internal CommittedFile(string path, ObjectId id, long size, BlobReader blobs)
    {
        Path = path;
        _id = id;
        Size = size;
        _blobs = blobs;
    }

    /// <summary>Repository-relative, with forward slashes, as git stores it.</summary>
    public string Path { get; }

    /// <summary>
    ///     In bytes, from the object header alone: for a delta, the size the chain resolves to, which
    ///     libgit2 reads off the start of the delta without applying the chain.
    /// </summary>
    public long Size { get; }

    /// <summary>
    ///     The whole content decoded as text, the way <see cref="BlobText" /> decides, or null when
    ///     libgit2 calls the file binary. Both answers come from one load of the blob, which inflates
    ///     all of it, so ask only of a file <see cref="Size" /> has not already ruled out. Call it once;
    ///     there is no cache behind it. The verdict is libgit2's because a heuristic of our own would
    ///     change which files are skipped.
    /// </summary>
    public string? Text() => _blobs.Text(_id);
}

/// <summary>
///     One path a commit touched: how it changed, by how many lines, and where — the edits are what the
///     build replays onto the previous attribution to get this commit's (ADR-0007).
/// </summary>
/// <param name="Path">Repository-relative, as git stores it. A path a commit deleted is still named here.</param>
/// <param name="OldPath">Where the content was before, when the commit moved it; otherwise the same as <paramref name="Path" />.</param>
/// <param name="ChangeKind">git's own word for it, lowercased: added, modified, deleted, renamed.</param>
/// <param name="Added">Lines the commit added to this path.</param>
/// <param name="Deleted">Lines the commit removed from it.</param>
/// <param name="IsBinary">Git could not diff it as text, so there are no edits and the file has no lines to attribute.</param>
/// <param name="Edits">The commit's edits to this path, in ascending position. Empty for a pure move.</param>
public sealed record ChangedPath(
    string Path,
    string OldPath,
    string ChangeKind,
    int Added,
    int Deleted,
    bool IsBinary,
    IReadOnlyList<LineEdit> Edits);

/// <summary>
///     One commit of a repository's history (CONTEXT.md), as plain records: the author, never the
///     committer (ADR-0007), and every path it touched. No LibGit2Sharp type is in it, so a build
///     reads history the same way it reads files.
/// </summary>
/// <param name="Sha">The commit's own id.</param>
/// <param name="ParentSha">The first parent, which the edits are against; null for a root commit, whose edits add every file.</param>
/// <param name="AuthorName">Who wrote it, as the author line says.</param>
/// <param name="AuthorEmail">The author's address, as the author line says.</param>
/// <param name="AuthoredAt">When the author made it — the committer's date is not recorded.</param>
/// <param name="Subject">The first line of the message, as git shows it.</param>
/// <param name="Body">The rest of the message, trimmed; empty rather than null when there is none.</param>
/// <param name="Files">Every path the commit touched, with its edits.</param>
public sealed record RecordedCommit(
    string Sha,
    string? ParentSha,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    string Subject,
    string Body,
    IReadOnlyList<ChangedPath> Files);

/// <summary>
///     A repository's local copy (CONTEXT.md) as the one reader it has, the refresh, sees it: the commit
///     HEAD is at and the files committed there. There is no working copy, so the files come from the
///     HEAD tree and their content from blobs (ADR-0003); a submodule is another repository and is not a
///     file of this one. Opened only through <see cref="GitClones.OpenRefreshedAsync" />, which has
///     already established that HEAD has a commit and that the tree declares no LFS filter.
/// </summary>
public sealed class LocalCopy : IDisposable
{
    private readonly Repository _repository;
    private readonly NativeRepository _native;
    private readonly BlobReader _blobs;
    private readonly NativeDiff _diffs;

    internal LocalCopy(Repository repository)
    {
        _repository = repository;
        HeadSha = repository.Head.Tip.Sha;
        _native = NativeRepository.Open(repository.Info.Path);
        _blobs = new BlobReader(_native);
        try
        {
            _diffs = new NativeDiff(_native);
        }
        catch
        {
            _native.Dispose();
            throw;
        }
    }

    /// <summary>How many blobs <see cref="CommittedFile.Text" /> has loaded, for the test that holds it to one a file.</summary>
    internal int BlobLoads => _blobs.Loads;

    /// <summary>The commit the copy is at, which the index records as what each repository was built from.</summary>
    public string HeadSha { get; }

    /// <summary>Every file at HEAD, in tree order: a directory's entries together, as git lists them.</summary>
    public IEnumerable<CommittedFile> Files() => Files(_repository.Head.Tip.Tree, "");

    /// <summary>
    ///     The commits of this repository's history, newest first, stopping before
    ///     <paramref name="stopAt" /> — which is how a refresh walks only what it has not recorded yet.
    ///     First-parent only and on HEAD alone (ADR-0007): a merge is one commit and its side branch is
    ///     not walked, so a pull request reads as a single change, and nothing outside the default
    ///     branch is recorded for files the index does not hold either.
    ///     Stopping at one commit is sound only because the walk is first-parent: that makes it a line
    ///     and not a graph, so everything past the newest recorded commit is recorded too. A walk that
    ///     never meets it runs to a root, which is how the caller learns HEAD's line no longer holds it.
    /// </summary>
    /// <param name="stopAt">The newest commit already recorded for this repository, or null for none.</param>
    /// <param name="maxBlobBytes">
    ///     The index's <c>Index:MaxFileBytes</c>. A change with a blob above it on either side is not
    ///     diffed, and is recorded the way the file pass treats that blob: as binary, with no lines.
    /// </param>
    /// <param name="cancellationToken">Checked per commit, which is where the diff cost is.</param>
    public IEnumerable<RecordedCommit> History(string? stopAt, long maxBlobBytes, CancellationToken cancellationToken)
    {
        // No SortBy, so the walk streams. First-parent makes the history a line, and a line has one
        // order however it is sorted — but GIT_SORT_TOPOLOGICAL makes libgit2 pre-traverse and buffer
        // the whole walk before it yields anything, which is paid before the `stopAt` early-out below
        // can fire. On a ten-thousand-commit repository that is the entire cost of a refresh that
        // turns out to have nothing new.
        var filter = new CommitFilter
        {
            IncludeReachableFrom = _repository.Head.Tip,
            FirstParentOnly = true
        };
        foreach (var commit in _repository.Commits.QueryBy(filter))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (commit.Sha == stopAt) yield break;
            yield return Describe(commit, maxBlobBytes);
        }
    }

    /// <summary>
    ///     The stored name of a change, the enum name lower-cased. The kinds a tree diff produces are
    ///     spelled out so that no path allocates one; anything else keeps the general expression.
    /// </summary>
    internal static string KindName(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "added",
        ChangeKind.Deleted => "deleted",
        ChangeKind.Modified => "modified",
        ChangeKind.Renamed => "renamed",
        ChangeKind.Copied => "copied",
        ChangeKind.TypeChanged => "typechanged",
        _ => kind.ToString().ToLowerInvariant()
    };

    /// <summary>
    ///     One commit with the paths it touched, diffed against its first parent — or against nothing
    ///     for the root commit, which adds every file it holds.
    ///     A patch and not a list of changed paths: the line counts and the edits are the point, and
    ///     only a patch computes them. It is the expensive part of a history walk — measured at 7 ms a
    ///     commit over the whole of a 4,300-commit repository — and is paid once per commit, because a
    ///     later refresh stops at the commits already recorded. Attribution rides on the same patch
    ///     (ADR-0007), and <see cref="NativeDiff" /> makes it.
    ///     Renames are detected with libgit2's defaults, so a moved file's edits are recorded against
    ///     its new path with the old one beside it, and the replay carries the attribution across.
    ///     A blob over <paramref name="maxBlobBytes" /> is never inflated (#263): its change is recorded
    ///     without lines, as the file pass treats that blob.
    /// </summary>
    private RecordedCommit Describe(Commit commit, long maxBlobBytes)
    {
        var parent = commit.Parents.FirstOrDefault();
        var files = _diffs.Diff(parent?.Tree.Id, commit.Tree.Id, maxBlobBytes);

        // MessageShort is the subject git itself would show; the body is what is left, and an empty
        // string rather than null because the column is NOT NULL and "no body" is not a missing value.
        string message = commit.Message;
        string subject = commit.MessageShort;
        string body = message.StartsWith(subject, StringComparison.Ordinal)
            ? message[subject.Length..].Trim()
            : message.Trim();
        return new RecordedCommit(commit.Sha, parent?.Sha, commit.Author.Name, commit.Author.Email,
            commit.Author.When, subject, body, files);
    }

    public void Dispose()
    {
        _diffs.Dispose();
        _native.Dispose();
        _repository.Dispose();
    }

    /// <summary>
    ///     True when any <c>.gitattributes</c> in the HEAD tree declares <c>filter=lfs</c>. Walks the whole
    ///     tree because git honours attributes files at any depth, not only at the root.
    /// </summary>
    internal static bool DeclaresLfs(Repository repository)
    {
        if (repository.Head.Tip?.Tree is not { } tree) return false;
        foreach (var entry in Walk(tree))
        {
            if (entry.TargetType != TreeEntryTargetType.Blob || entry.Name != ".gitattributes") continue;
            using var stream = ((Blob)entry.Target).GetContentStream();
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                // Attribute lines are `pattern attr attr...`; the pattern itself is never `filter=lfs`.
                if (line.Split(' ', '\t').Skip(1).Any(a => a == "filter=lfs"))
                    return true;
        }

        return false;
    }

    private IEnumerable<CommittedFile> Files(Tree tree, string prefix)
    {
        foreach (var entry in tree)
            switch (entry.TargetType)
            {
                case TreeEntryTargetType.Blob:
                    yield return new CommittedFile(prefix + entry.Name, entry.Target.Id,
                        _repository.ObjectDatabase.RetrieveObjectMetadata(entry.Target.Id).Size, _blobs);
                    break;
                case TreeEntryTargetType.Tree:
                    foreach (var child in Files((Tree)entry.Target, prefix + entry.Name + "/")) yield return child;
                    break;
            }
    }

    private static IEnumerable<TreeEntry> Walk(Tree tree)
    {
        foreach (var entry in tree)
        {
            yield return entry;
            if (entry.TargetType != TreeEntryTargetType.Tree) continue;
            foreach (var child in Walk((Tree)entry.Target)) yield return child;
        }
    }
}
