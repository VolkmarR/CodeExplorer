using LibGit2Sharp;

namespace CodeExplorer;

/// <summary>
///     What opening a repository's local copy for a refresh came to. The three answers a fetched clone
///     can give are cases and not a null, a bool and a scan the caller has to run in the right order:
///     <see cref="Opened" /> hands over the copy to read, and the two <see cref="Refused" /> cases carry
///     the sentence the refresh reports in the repository's place. Nothing is open behind a refusal.
/// </summary>
public abstract record CloneOpen
{
    private CloneOpen() { }

    /// <summary>The copy is the caller's to read and to dispose.</summary>
    public sealed record Opened(LocalCopy Copy) : CloneOpen;

    /// <summary>The repository is not to be read, and <see cref="Explanation" /> says why in operator-facing prose.</summary>
    public abstract record Refused(string Explanation) : CloneOpen;

    /// <summary>A remote with no commits yet: an answer, not a failure.</summary>
    public sealed record Empty(string Explanation) : Refused(Explanation);

    /// <summary>A <c>.gitattributes</c> in HEAD declares <c>filter=lfs</c>, which this server cannot read honestly.</summary>
    public sealed record UsesLfs(string Explanation) : Refused(Explanation);
}

/// <summary>
///     One file committed at HEAD. Size and the binary flag are read from the blob header, and the text
///     only on <see cref="Text" />, so a build can decide to skip a file without loading it. The blob
///     itself stays here: no LibGit2Sharp type leaves <c>Git/</c>.
/// </summary>
public sealed class CommittedFile
{
    private readonly Blob _blob;

    internal CommittedFile(string path, Blob blob)
    {
        Path = path;
        _blob = blob;
    }

    /// <summary>Repository-relative, with forward slashes, as git stores it.</summary>
    public string Path { get; }

    /// <summary>
    ///     The blob's object id: git's hash of this content and of nothing else. It is what attribution
    ///     is keyed by (ADR-0007), because it is the one identifier that survives a rebuild — a file's
    ///     id is assigned by the walk and shifts whenever anything sorting before it is added — and
    ///     because two files with the same content, or one file that only moved, share it.
    /// </summary>
    public string Sha => _blob.Sha;

    public long Size => _blob.Size;

    /// <summary>libgit2's call, made the way git makes it: a NUL in the first bytes.</summary>
    public bool IsBinary => _blob.IsBinary;

    /// <summary>The whole content decoded as text. Call it once; there is no cache behind it.</summary>
    public string Text() => _blob.GetContentText();
}

/// <summary>One path a commit touched, with how it changed and by how many lines.</summary>
/// <param name="Path">Repository-relative, as git stores it. A path a commit deleted is still named here.</param>
/// <param name="ChangeKind">git's own word for it, lowercased: added, modified, deleted, renamed.</param>
/// <param name="Added">Lines the commit added to this path.</param>
/// <param name="Deleted">Lines the commit removed from it.</param>
public sealed record ChangedPath(string Path, string ChangeKind, int Added, int Deleted);

/// <summary>
///     One commit of a repository's history (CONTEXT.md), as plain records: the author, never the
///     committer (ADR-0007), and every path it touched. No LibGit2Sharp type is in it, so a build
///     reads history the same way it reads files.
/// </summary>
public sealed record RecordedCommit(
    string Sha,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    string Subject,
    string Body,
    IReadOnlyList<ChangedPath> Files);

/// <summary>
///     A run of consecutive lines attributed to one commit (CONTEXT.md, Attribution). Line numbers are
///     1-based and inclusive, matching <c>lines.line_number</c>, so a range needs no adjusting to join.
/// </summary>
public sealed record AttributedRange(int StartLine, int EndLine, string Sha);

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

    internal LocalCopy(Repository repository)
    {
        _repository = repository;
        HeadSha = repository.Head.Tip.Sha;
    }

    /// <summary>The commit the copy is at, which the index records as what each repository was built from.</summary>
    public string HeadSha { get; }

    /// <summary>Every file at HEAD, in tree order: a directory's entries together, as git lists them.</summary>
    public IEnumerable<CommittedFile> Files() => Files(_repository.Head.Tip.Tree, "");

    /// <summary>
    ///     The commits of this repository's history, newest first, stopping at the first one in
    ///     <paramref name="known" /> — which is how a refresh walks only what it has not recorded yet.
    ///     First-parent only and on HEAD alone (ADR-0007): a merge is one commit and its side branch is
    ///     not walked, so a pull request reads as a single change, and nothing outside the default
    ///     branch is recorded for files the index does not hold either.
    ///     Stopping at the first known commit is sound only because the walk is first-parent: that makes
    ///     it a line and not a graph, so everything past a recorded commit is recorded too.
    /// </summary>
    /// <param name="known">Commit SHAs already recorded for this repository.</param>
    /// <param name="cancellationToken">Checked per commit, which is where the diff cost is.</param>
    public IEnumerable<RecordedCommit> History(IReadOnlySet<string> known, CancellationToken cancellationToken)
    {
        var filter = new CommitFilter
        {
            IncludeReachableFrom = _repository.Head.Tip,
            FirstParentOnly = true,
            SortBy = CommitSortStrategies.Topological
        };
        foreach (var commit in _repository.Commits.QueryBy(filter))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (known.Contains(commit.Sha)) yield break;
            yield return Describe(commit);
        }
    }

    /// <summary>
    ///     Which commit each line of a file at HEAD was last changed by, as runs. Throws nothing for an
    ///     ordinary file; a caller that blames a path not at HEAD is asking about a file this copy does
    ///     not have, which is a bug rather than an answer.
    /// </summary>
    public IReadOnlyList<AttributedRange> Attribution(string path)
    {
        var ranges = new List<AttributedRange>();
        foreach (var hunk in _repository.Blame(path))
        {
            // FinalStartLineNumber is 0-based: libgit2 reports 1-based and LibGit2Sharp subtracts one
            // on the way out, which its own documentation does not say. AttributionBaseTests pins it,
            // because the whole feature is off by one line in every file if this is wrong and nothing
            // about the result looks broken.
            int start = hunk.FinalStartLineNumber + 1;
            ranges.Add(new AttributedRange(start, start + hunk.LineCount - 1, hunk.FinalCommit.Sha));
        }

        return ranges;
    }

    /// <summary>
    ///     One commit with the paths it touched, diffed against its first parent — or against nothing
    ///     for the root commit, which adds every file it holds.
    ///     A <c>Patch</c> and not a <c>TreeChanges</c>: the added and deleted line counts are the point,
    ///     and only a patch computes them. That is the expensive part of a history walk and is paid once
    ///     per commit, because a later refresh stops at the commits already recorded.
    /// </summary>
    private RecordedCommit Describe(Commit commit)
    {
        var parent = commit.Parents.FirstOrDefault();
        var files = new List<ChangedPath>();
        foreach (var change in _repository.Diff.Compare<Patch>(parent?.Tree, commit.Tree))
            files.Add(new ChangedPath(change.Path, change.Status.ToString().ToLowerInvariant(),
                change.LinesAdded, change.LinesDeleted));

        // MessageShort is the subject git itself would show; the body is what is left, and an empty
        // string rather than null because the column is NOT NULL and "no body" is not a missing value.
        string message = commit.Message;
        string subject = commit.MessageShort;
        string body = message.StartsWith(subject, StringComparison.Ordinal)
            ? message[subject.Length..].Trim()
            : message.Trim();
        return new RecordedCommit(commit.Sha, commit.Author.Name, commit.Author.Email, commit.Author.When,
            subject, body, files);
    }

    public void Dispose() => _repository.Dispose();

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

    private static IEnumerable<CommittedFile> Files(Tree tree, string prefix)
    {
        foreach (var entry in tree)
            switch (entry.TargetType)
            {
                case TreeEntryTargetType.Blob:
                    yield return new CommittedFile(prefix + entry.Name, (Blob)entry.Target);
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
