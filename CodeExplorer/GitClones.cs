using System.Collections.Concurrent;
using System.Security.Cryptography;
using LibGit2Sharp;
using Microsoft.AspNetCore.DataProtection;
using ModelContextProtocol;

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

    private readonly string _cloneRoot = Path.Combine(configuration["Storage:DataDirectory"] ?? "data", "clones");
    private readonly IDataProtector _protector = dataProtection.CreateProtector(ControlDatabase.CredentialPurpose);

    // One gate per clone directory, so two first uses of the same repository clone it once and the
    // second waits for the first instead of racing it on the same folder.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _cloneGates = new();

    /// <summary>
    ///     Opens the repository, cloning it first if no clone exists. Throws <see cref="McpException" />
    ///     when the clone fails; the message names the repository and what to check and never carries
    ///     the credential, which only ever reaches libgit2 through <c>CredentialsProvider</c>.
    /// </summary>
    public async Task<Repository> OpenAsync(ProjectRepository repository, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_cloneRoot, repository.ProjectSlug, repository.Slug + ".git");
        var gate = _cloneGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!Repository.IsValid(path)) await Task.Run(() => Clone(repository, path, cancellationToken), cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        return new Repository(path);
    }

    /// <summary>
    ///     True when any <c>.gitattributes</c> in the HEAD tree declares <c>filter=lfs</c>. Walks the whole
    ///     tree because git honours attributes files at any depth, not only at the root.
    /// </summary>
    public static bool DeclaresLfs(Repository repository)
    {
        if (HeadTree(repository) is not { } tree) return false;
        foreach (var entry in Walk(tree))
        {
            if (entry.TargetType != TreeEntryTargetType.Blob || entry.Name != ".gitattributes") continue;
            using var stream = ((Blob)entry.Target).GetContentStream();
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                // Attribute lines are `pattern attr attr...`; the pattern itself is never `filter=lfs`.
                if (line.Split(' ', '\t').Skip(1).Any(a => a == "filter=lfs")) return true;
            }
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
        foreach (var entry in tree.OrderBy(e => e.TargetType != TreeEntryTargetType.Tree).ThenBy(e => e.Name, StringComparer.Ordinal))
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
        // ADR-0003: the local transport rejects shallow clones ("shallow fetch is not supported by the
        // local transport"), so a path or file URL clones in full. Only tests and mirrors use those.
        if (RepositoryUrl.Classify(repository.Url) == RepositoryUrlKind.Remote) options.FetchOptions.Depth = 1;
        options.FetchOptions.OnTransferProgress = _ => !cancellationToken.IsCancellationRequested;
        if (repository.ProtectedCredential is { } ciphertext)
        {
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
            options.FetchOptions.CredentialsProvider = (_, _, _) =>
                new UsernamePasswordCredentials { Username = "token", Password = token };
        }

        // The credential is deliberately absent from this line and every other log line.
        if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information))
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

    /// <summary>libgit2 writes pack files read-only, which a recursive delete refuses until cleared.</summary>
    private static void DeleteClone(string path)
    {
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        Directory.Delete(path, true);
    }
}
