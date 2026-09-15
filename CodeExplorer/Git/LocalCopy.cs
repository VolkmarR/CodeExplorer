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

    public long Size => _blob.Size;

    /// <summary>libgit2's call, made the way git makes it: a NUL in the first bytes.</summary>
    public bool IsBinary => _blob.IsBinary;

    /// <summary>The whole content decoded as text. Call it once; there is no cache behind it.</summary>
    public string Text() => _blob.GetContentText();
}

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
