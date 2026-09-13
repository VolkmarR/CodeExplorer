using System.Collections.Concurrent;
using System.Security.Cryptography;
using LibGit2Sharp;
using Microsoft.AspNetCore.DataProtection;
using ModelContextProtocol;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CodeExplorer;

/// <summary>One entry of a tree listing: a path relative to the listed directory.</summary>
public sealed record TreeEntryInfo(string RelativePath, bool IsDirectory);

/// <summary>
///     Keeps the local copy of every repository: a shallow bare clone under
///     <c>{DataDirectory}/clones/{project}/{repository}.git</c>, made on first use. Files are read
///     from the HEAD tree, so there is no working copy and nothing to walk on disk (ADR-0003).
/// </summary>
public sealed class GitClones(
    IConfiguration configuration,
    IDataProtectionProvider dataProtection,
    ILogger<GitClones> logger)
{
    /// <summary>
    ///     Refusal text for a repository declaring <c>filter=lfs</c>. libgit2 has no LFS support and would
    ///     serve pointer files as though they were source, which an agent cannot tell from the real thing.
    /// </summary>
    public const string LfsRefusal =
        "This repository uses Git LFS (a .gitattributes file declares filter=lfs). CodeExplorer cannot read LFS "
        + "content and would show pointer files as if they were source, so it refuses the repository rather "
        + "than answer wrongly. Ask the operator to point the project at a repository without LFS.";

    // One gate per clone directory, so two first uses of the same repository clone it once and the
    // second waits for the first instead of racing it on the same folder. Both dictionaries hold one
    // entry per repository for the life of the process, which is bounded by the control database.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _cloneGates = new();

    private readonly string _cloneRoot = Path.Combine(configuration["Storage:DataDirectory"] ?? "data", "clones");
    private readonly ConcurrentDictionary<string, bool> _lfsByPath = new();
    private readonly IDataProtector _protector = dataProtection.CreateProtector(ControlDatabase.CredentialPurpose);

    /// <summary>
    ///     Opens the repository, cloning it first if no clone exists. Throws <see cref="McpException" />
    ///     when the clone fails; the message names the repository and what to check and never carries
    ///     the credential, which only ever reaches libgit2 through <c>CredentialsProvider</c>.
    /// </summary>
    public Task<Repository> OpenAsync(ProjectRepository repository, CancellationToken cancellationToken) =>
        OpenAsync(repository, false, cancellationToken);

    /// <summary>
    ///     Opens the repository with its local copy brought up to date: a shallow fetch when it is
    ///     already cloned, and the clone itself when it is not, which is already current. This is the
    ///     first half of a refresh (CONTEXT.md); without it a rebuild re-reads whatever was fetched
    ///     when the repository was first added, however long ago that was.
    /// </summary>
    public Task<Repository> OpenRefreshedAsync(ProjectRepository repository,
        CancellationToken cancellationToken) =>
        OpenAsync(repository, true, cancellationToken);

    /// <param name="repository">The repository to open, as the control database holds it.</param>
    /// <param name="fetch">
    ///     Whether an existing clone is brought up to date first. Only a refresh asks for it: a tool
    ///     call reads what is already there, because an agent must not make the server talk to a remote.
    /// </param>
    /// <param name="cancellationToken">Cancels the transfer, which is the long part.</param>
    private async Task<Repository> OpenAsync(ProjectRepository repository, bool fetch,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(_cloneRoot, repository.ProjectSlug, repository.Slug + ".git");
        var gate = _cloneGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Both branches are synchronous libgit2 over the network; a worker thread keeps them off
            // the request thread. A clone is already up to date, so it is never followed by a fetch.
            if (!Repository.IsValid(path))
                await Task.Run(() => Clone(repository, path, cancellationToken), cancellationToken);
            else if (fetch)
                await Task.Run(() => Fetch(repository, path, cancellationToken), cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        return new Repository(path);
    }

    /// <summary>
    ///     Deletes the local copy of one repository, or of a whole project when
    ///     <paramref name="repositorySlug" /> is null. Called after the control database has forgotten
    ///     them, so a failure here leaves disused bytes behind rather than a repository the operator
    ///     removed and still sees.
    /// </summary>
    public async Task RemoveAsync(string projectSlug, string? repositorySlug,
        CancellationToken cancellationToken)
    {
        string path = repositorySlug is null
            ? Path.Combine(_cloneRoot, projectSlug)
            : Path.Combine(_cloneRoot, projectSlug, repositorySlug + ".git");
        var gate = _cloneGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // The gate is keyed by clone path, so removing a project does not exclude a clone of one of
            // its repositories running under a different key. The control database forgot them first,
            // so nothing can start a new clone; one already in flight loses the race and leaves a folder.
            await Task.Run(() => Delete(path), cancellationToken);
            foreach (string known in _lfsByPath.Keys.Where(p => p.StartsWith(path, StringComparison.Ordinal)))
                _lfsByPath.TryRemove(known, out _);
        }
        finally
        {
            gate.Release();
        }

        // Dropped after the gate is released, so the dictionary does not grow by one entry for every
        // repository ever removed. A clone starting now takes a fresh gate for a path that no longer
        // exists in the control database, which is the same race the comment above describes.
        _cloneGates.TryRemove(path, out _);
    }

    /// <summary>
    ///     libgit2 marks pack files read-only, and <c>Directory.Delete</c> refuses a read-only file; the
    ///     attributes are cleared first rather than left to fail on the first pack.
    /// </summary>
    private static void Delete(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        Directory.Delete(path, true);
    }

    /// <summary>
    ///     True when any <c>.gitattributes</c> in the HEAD tree declares <c>filter=lfs</c>. Walks the whole
    ///     tree because git honours attributes files at any depth, not only at the root. Remembered per
    ///     clone, since HEAD only moves on a refresh, which is where the entry will be dropped.
    /// </summary>
    public bool DeclaresLfs(Repository repository) =>
        _lfsByPath.GetOrAdd(repository.Info.Path, static (_, repo) => ScanForLfs(repo), repository);

    private static bool ScanForLfs(Repository repository)
    {
        if (HeadTree(repository) is not { } tree) return false;
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

    /// <summary>
    ///     Lists the HEAD tree under <paramref name="directory" /> (empty for the root) down to
    ///     <paramref name="depth" /> levels. Returns null when the path is not a directory in HEAD, or
    ///     there is no HEAD commit at all.
    /// </summary>
    public static IReadOnlyList<TreeEntryInfo>? ListTree(Repository repository, string directory, int depth)
    {
        if (HeadTree(repository) is not { } tree) return null;
        if (directory.Length > 0)
        {
            var entry = tree[directory];
            if (entry?.TargetType != TreeEntryTargetType.Tree) return null;
            tree = (Tree)entry.Target;
        }

        var entries = new List<TreeEntryInfo>();
        Collect(tree, "", depth, entries);
        return entries;
    }

    private static void Collect(Tree tree, string prefix, int depth, List<TreeEntryInfo> entries)
    {
        foreach (var entry in tree.OrderBy(e => e.TargetType != TreeEntryTargetType.Tree)
                     .ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            string relative = prefix + entry.Name;
            // A submodule (GitLink) is another repository; list it as a directory that cannot be entered.
            bool isDirectory = entry.TargetType is TreeEntryTargetType.Tree or TreeEntryTargetType.GitLink;
            entries.Add(new TreeEntryInfo(relative, isDirectory));
            if (entry.TargetType == TreeEntryTargetType.Tree && depth > 1)
                Collect((Tree)entry.Target, relative + "/", depth - 1, entries);
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

    /// <summary>False for a freshly initialised remote: an empty repository is an answer, not a failure.</summary>
    public static bool HasCommits(Repository repository) => repository.Head.Tip is not null;

    private static Tree? HeadTree(Repository repository) => repository.Head.Tip?.Tree;

    private void Clone(ProjectRepository repository, string path, CancellationToken cancellationToken)
    {
        // A folder that exists but is not a valid repository is a clone that failed half-way; start over.
        if (Directory.Exists(path)) DeleteClone(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var options = new CloneOptions { IsBare = true };
        Configure(options.FetchOptions, repository, cancellationToken);

        // The credential is deliberately absent from this line and every other log line.
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Cloning repository {Repository} of project {Project} from {Url}",
                repository.Slug, repository.ProjectSlug, repository.Url);
        try
        {
            Repository.Clone(repository.Url, path, options);
        }
        catch (Exception ex) when (ex is LibGit2SharpException or IOException or UnauthorizedAccessException)
        {
            if (Directory.Exists(path)) DeleteClone(path);
            cancellationToken.ThrowIfCancellationRequested();
            // The URL carries no password (RepositoryUrl refuses one), and the token only ever reached
            // libgit2 through CredentialsProvider, so neither the URL nor libgit2's message can hold it.
            // The exception is not attached as InnerException, so nothing beyond this text is serialised.
            throw new McpException(
                $"Cloning repository '{repository.Slug}' from '{repository.Url}' failed: {ex.Message.TrimEnd('.')}. "
                + "Ask the operator to check the URL and the stored credential for this repository, then try again.");
        }
    }

    /// <summary>
    ///     Brings an existing clone up to date. The refspec writes the local branch refs directly
    ///     rather than remote-tracking ones, because the clone is bare: HEAD points at a local branch
    ///     and that is what the tree walk reads, so a fetch that only moved <c>refs/remotes</c> would
    ///     download the commits and index none of them. It is forced because there is no working copy
    ///     and nothing to merge — the remote's history replaces ours outright, including after a force
    ///     push.
    /// </summary>
    private void Fetch(ProjectRepository repository, string path, CancellationToken cancellationToken)
    {
        // Pruned, because the clone is a mirror of the remote and nothing here merges: a branch deleted
        // upstream must go, or the index keeps serving a branch that no longer exists. If the pruned
        // branch is the one HEAD points at — a renamed default branch — HEAD resolves to nothing and
        // the refresh reports the repository as having no commits rather than indexing the stale tree.
        var options = new FetchOptions { Prune = true };
        Configure(options, repository, cancellationToken);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Fetching repository {Repository} of project {Project}",
                repository.Slug, repository.ProjectSlug);
        using var clone = new Repository(path);
        try
        {
            Commands.Fetch(clone, "origin", ["+refs/heads/*:refs/heads/*"], options, null);
        }
        catch (Exception ex) when (ex is LibGit2SharpException or IOException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The clone is left in place: it still holds the commits of the last successful fetch, so
            // a refresh that cannot reach the remote reports the repository and indexes nothing newer,
            // rather than losing what is already there. Neither the URL nor libgit2's message can
            // carry the credential, for the reason Clone gives.
            throw new McpException(
                $"Fetching repository '{repository.Slug}' from '{repository.Url}' failed: {ex.Message.TrimEnd('.')}. "
                + "Ask the operator to check the URL and the stored credential for this repository, then try again.");
        }

        // HEAD has moved, so what a previous scan concluded about LFS is about the old tree.
        _lfsByPath.TryRemove(clone.Info.Path, out _);
    }

    /// <summary>
    ///     The transfer settings a clone and a fetch share: shallow where the transport allows it,
    ///     cancellable, and carrying the stored credential to libgit2 and nowhere else.
    /// </summary>
    private void Configure(FetchOptions options, ProjectRepository repository, CancellationToken cancellationToken)
    {
        // ADR-0003: the local transport rejects shallow clones and fetches ("shallow fetch is not
        // supported by the local transport"), so a path or file URL transfers in full. Only tests and
        // mirrors use those.
        if (RepositoryUrl.Classify(repository.Url) == RepositoryUrlKind.Remote) options.Depth = 1;
        options.OnTransferProgress = _ => !cancellationToken.IsCancellationRequested;
        if (repository.ProtectedCredential is not { } ciphertext) return;

        string token;
        try
        {
            token = _protector.Unprotect(ciphertext);
        }
        catch (CryptographicException)
        {
            // The key ring that protected it is gone (a restart without #13, or a rotated key).
            throw new McpException(
                $"The stored credential for repository '{repository.Slug}' cannot be decrypted because the "
                + "Data Protection key ring has changed. Ask the operator to set the credential again.");
        }

        // GitHub and Azure DevOps both accept a token as the password with any user name.
        options.CredentialsProvider = (_, _, _) =>
            new UsernamePasswordCredentials { Username = "token", Password = token };
    }

    /// <summary>libgit2 writes pack files read-only, which a recursive delete refuses until cleared.</summary>
    private static void DeleteClone(string path)
    {
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        Directory.Delete(path, true);
    }
}
