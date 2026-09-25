using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using CodeExplorer.Infrastructure;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.AspNetCore.DataProtection;
using ModelContextProtocol;
using GitReference = LibGit2Sharp.Reference;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CodeExplorer.Git;

/// <summary>
///     Keeps the local copy of every repository (CONTEXT.md): a full bare clone under
///     <c>{DataDirectory}/clones/{project}/{repository}.git</c>, made on the first refresh and
///     brought up to date on every later one. The refresh is its only reader, and reads it through
///     the <see cref="LocalCopy" /> this hands out; the copy is temporary and nothing may depend on
///     its being there. Files are read from the HEAD tree, so there is no working copy and nothing to
///     walk on disk (ADR-0003).
///     Full and no longer shallow since ADR-0007: history is what the index is built to answer from,
///     and a clone kept between refreshes is what stops every refresh re-downloading it. That makes
///     the clones the largest thing on the ephemeral disk, which <see cref="Footprint" /> is here for.
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
    private const string _lfsRefusal =
        "This repository uses Git LFS (a .gitattributes file declares filter=lfs). CodeExplorer cannot read LFS "
        + "content and would show pointer files as if they were source, so it refuses the repository rather "
        + "than answer wrongly. Ask the operator to point the project at a repository without LFS.";

    // One gate per clone directory, so two first uses of the same repository clone it once and the
    // second waits for the first instead of racing it on the same folder. The dictionary holds one
    // entry per repository for the life of the process, which is bounded by the control database.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _cloneGates = new();

    // Absolute, because libgit2 resolves a relative data directory before it names a path in an error,
    // and the path has to be written the same way to be recognised and kept out of a message (#232).
    private readonly string _cloneRoot =
        Path.GetFullPath(Path.Combine(configuration["Storage:DataDirectory"] ?? "data", "clones"));
    private readonly bool _localAllowed = RepositoryUrl.LocalAllowed(configuration);
    private readonly IDataProtector _protector = dataProtection.CreateProtector(KeyRing.CredentialPurpose);

    // Set when the one class that talks to a remote is built, which is before its first transfer.
    private readonly int _stallSeconds = TransferStallLimit.Apply(configuration);

    /// <summary>
    ///     Opens the repository with its local copy brought up to date: a fetch when it is already
    ///     cloned, and the clone itself when it is not, which is already current. This is the
    ///     first half of a refresh (CONTEXT.md), and the only way a local copy is ever read; a tool
    ///     call answers from the index, because an agent must not make the server talk to a remote.
    ///     Throws <see cref="McpException" /> when the transfer fails; the message names the repository
    ///     and what to check and never carries the credential, which only ever reaches libgit2 through
    ///     <c>CredentialsProvider</c>. A copy that transferred but is not to be read comes back as a
    ///     <see cref="CloneOpen.Refused" /> with the sentence to report, and nothing left open.
    /// </summary>
    /// <param name="repository">The repository to open, as the control database holds it.</param>
    /// <param name="cancellationToken">Cancels the transfer, which is the long part.</param>
    public async Task<CloneOpen> OpenRefreshedAsync(ProjectRepository repository,
        CancellationToken cancellationToken)
    {
        // Before the clone and the fetch alike, and on the stored URL: a repository added while the
        // setting was on must stop being read once it is off, and a copy that already exists is
        // fetched from that same URL, which its origin was cloned from (GHSA-5373-pppr-q3q9).
        if (!_localAllowed && RepositoryUrl.Classify(repository.Url) == RepositoryUrlKind.Local)
            return new CloneOpen.LocalNotAllowed(
                $"Repository '{repository.Slug}' was not read. {RepositoryUrl.LocalRefusal}");

        // The API refuses this pair now, but a server that predates the refusal may hold one. Here and
        // not in Credentials, so nothing is cloned, cleared or fetched either: every transfer this class
        // makes starts behind this line (GHSA-4f8q-c6jj-fr44). An https remote that redirects to http is
        // libgit2's to refuse, and it does: it will not follow a redirect off https.
        if (RepositoryUrl.SendsCredentialInClear(repository.Url, repository.HasCredential))
            return new CloneOpen.ClearTextCredential(
                $"Repository '{repository.Slug}' was not read. {RepositoryUrl.ClearTextCredentialRefusal}");

        string path = Path.Combine(_cloneRoot, repository.ProjectSlug, repository.Slug + ".git");
        try
        {
            return await RefreshAndOpenAsync(repository, path, cancellationToken);
        }
        catch (Exception ex) when (ex is LibGit2SharpException or IOException or UnauthorizedAccessException)
        {
            // What the transfer itself raised is already an McpException; this is the rest, the local
            // copy failing on the server's own disk while it is cleared, opened or read. The message
            // is the operating system's or libgit2's and can name the path, which the reader of the
            // refresh status must not see and the operator must (#232).
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning(ex, "The local copy of repository {Repository} of project {Project} at {Path} "
                                      + "could not be used", repository.Slug, repository.ProjectSlug, path);
            throw new McpException(
                $"The local copy of repository '{repository.Slug}' could not be used on the server: "
                + $"{WithoutPath(ex.Message, path).TrimEnd('.')}. Ask the operator to check the server's log "
                + "for the local copy of this repository, then try again.");
        }
    }

    private async Task<CloneOpen> RefreshAndOpenAsync(ProjectRepository repository, string path,
        CancellationToken cancellationToken)
    {
        var gate = _cloneGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Both branches are synchronous libgit2 over the network; a worker thread keeps them off
            // the request thread. A clone is already up to date, so it is never followed by a fetch.
            if (!IsCopyOf(repository, path))
                await Task.Run(() => Clone(repository, path, cancellationToken), cancellationToken);
            else
                await Task.Run(() => Fetch(repository, path, cancellationToken), cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        var clone = new Repository(path);
        try
        {
            // Decided here, once, in this order: an empty remote has no tree to scan, and a tree that
            // declares LFS is refused before anyone reads a pointer file as source.
            if (clone.Head.Tip is null)
            {
                clone.Dispose();
                return new CloneOpen.Empty($"Repository '{repository.Slug}' has no commits yet.");
            }

            // The LFS scan reads every .gitattributes in the tree, which is a walk of the whole tree; off
            // the request thread like the transfer, and before the thread is handed a copy to read.
            if (await Task.Run(() => LocalCopy.DeclaresLfs(clone), cancellationToken))
            {
                clone.Dispose();
                return new CloneOpen.UsesLfs($"Repository '{repository.Slug}': {_lfsRefusal}");
            }

            return new CloneOpen.Opened(new LocalCopy(clone));
        }
        catch
        {
            clone.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     What a project's local copies occupy right now, in bytes, and zero for a project that has
    ///     none yet. The free-space gate sizes a refresh against it (ADR-0007): a clone that exists
    ///     grows by a fetch, and one that does not exist yet is about to download an entire object
    ///     store, and those are different amounts of room to insist on.
    ///     Walked rather than remembered, because a clone is deleted and repacked behind this class's
    ///     back and a cached figure would be wrong in the direction that fills the disk.
    /// </summary>
    public long Footprint(string projectSlug)
    {
        var directory = new DirectoryInfo(Path.Combine(_cloneRoot, projectSlug));
        if (!directory.Exists) return 0;
        try
        {
            return directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Safe to swallow: a clone being written or removed while this walks is the normal case,
            // and the gate only needs a figure to reason with. Zero reads as "nothing to reserve for",
            // which is the same answer as for a project not cloned yet — the floor then stands in.
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug(ex, "Could not size the local copies of project {Project}", projectSlug);
            return 0;
        }
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
    ///     Whether the folder is this repository's local copy: a valid repository whose origin is the
    ///     stored URL. A fetch goes to the folder's own origin, so a folder a removal left behind
    ///     (<see cref="RemoveAsync" /> says how) and a repository added later under the same slug would
    ///     otherwise share it, and one cloned from a local path would read the server's disk past the
    ///     check in <see cref="OpenRefreshedAsync" /> (GHSA-5373-pppr-q3q9). Nothing changes a stored
    ///     URL, so a mismatch is always such a folder, and it is cloned over rather than repointed:
    ///     repointing would keep the other repository's objects and tags.
    /// </summary>
    private bool IsCopyOf(ProjectRepository repository, string path)
    {
        if (!Repository.IsValid(path)) return false;
        using var clone = new Repository(path);
        if (clone.Network.Remotes["origin"]?.Url == repository.Url) return true;

        if (logger.IsEnabled(LogLevel.Warning))
            logger.LogWarning("The folder at {Path} is not the local copy of repository {Repository} of project "
                              + "{Project}, whose URL it does not name, and is cloned over", path, repository.Slug,
                repository.ProjectSlug);
        return false;
    }

    private void Clone(ProjectRepository repository, string path, CancellationToken cancellationToken)
    {
        // A folder that exists but is not a valid repository is a clone that failed half-way; start over.
        Delete(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var options = new CloneOptions { IsBare = true };
        var watch = Configure(options.FetchOptions, repository, cancellationToken);

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
            // Judged before the delete, whose time would otherwise count as the remote's silence.
            var failure = TransferFailed("Cloning", repository, path, ex, watch);
            Delete(path);
            cancellationToken.ThrowIfCancellationRequested();
            throw failure;
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
        // upstream must go, or the index keeps serving a branch that no longer exists. That takes the
        // branch HEAD names with it when the default was renamed, which is why AlignHead runs after.
        var options = new FetchOptions { Prune = true };
        var watch = Configure(options, repository, cancellationToken);

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
            // rather than losing what is already there.
            throw TransferFailed("Fetching", repository, path, ex, watch);
        }

        AlignHead(clone, repository, path, cancellationToken);
    }

    /// <summary>
    ///     Points the clone's HEAD at the branch the remote advertises as its own, so the mirror
    ///     follows the remote's HEAD the way the refspec follows its branches. Without it a default
    ///     branch renamed or deleted upstream leaves HEAD naming a branch the prune removed: the tree
    ///     walk reads HEAD, so the repository reports itself as having no commits, and nothing else
    ///     ever writes HEAD, so it stays that way for every later refresh (#31). A remote whose HEAD is
    ///     detached names no branch, and the clone keeps the one it has (#260).
    /// </summary>
    private void AlignHead(Repository clone, ProjectRepository repository, string path,
        CancellationToken cancellationToken)
    {
        if (AdvertisedReferences(repository, cancellationToken) is { } advertised)
        {
            // A remote with no branches is empty, and an empty repository is an answer rather than a
            // local copy to complain about. Decided on the branches and not on the HEAD target, because
            // protocol v2 advertises an unborn HEAD with a symref target: an empty remote can name a
            // default branch that exists nowhere yet, and that must not read as a failure to resolve one.
            if (!advertised.Any(r => r.CanonicalName.StartsWith("refs/heads/", StringComparison.Ordinal)))
                return;

            // Only a symbolic HEAD names the branch the remote defaults to, and the refspec just fetched
            // it. A detached HEAD is advertised as a direct reference to a commit id, which libgit2
            // throws on when it is looked up as a reference name (#260).
            if (advertised.FirstOrDefault(r => r.CanonicalName == "HEAD")
                    is SymbolicReference { TargetIdentifier: var branch }
                && clone.Refs[branch] is not null)
            {
                if (clone.Refs.Head.TargetIdentifier != branch) clone.Refs.UpdateTarget(clone.Refs.Head, branch);
                return;
            }
        }

        // Either the advertisement could not be read or it named nothing this clone has. A HEAD that
        // still resolves keeps serving the branch it has and the next refresh tries again; one that
        // resolves to nothing is a local copy the operator has to hear about, because the indexer
        // would otherwise report the remote as empty when the remote is fine.
        if (clone.Head.Tip is not null) return;

        // The path is for the operator, who can reach the disk, and so it goes to the log only. The
        // message reaches whoever reads the refresh status, who cannot, and to whom a path discloses
        // nothing useful but the server's layout (#232); removing the repository deletes its local
        // copy, which is the way out that goes through the product.
        if (logger.IsEnabled(LogLevel.Warning))
            logger.LogWarning("The local copy of repository {Repository} of project {Project} at {Path} has a "
                              + "HEAD naming no branch it holds, and the remote's default branch could not be read "
                              + "to repair it", repository.Slug, repository.ProjectSlug, path);
        throw new McpException(
            $"The local copy of repository '{repository.Slug}' has a HEAD naming "
            + $"'{clone.Refs.Head.TargetIdentifier}', which is no branch it holds, and the default branch of "
            + $"'{repository.Url}' could not be read to repair it. Ask the operator to retry the refresh, "
            + $"or to remove repository '{repository.Slug}' from project '{repository.ProjectSlug}' and add it "
            + "again, with its credential if it had one, which discards the local copy so the next refresh "
            + "makes a fresh one.");
    }

    /// <summary>
    ///     What the remote advertises, or null when it could not be reached. Separate
    ///     from the fetch because the refspec writes branches only: nothing in a fetch carries the
    ///     remote's HEAD, and <c>refs/remotes/origin/HEAD</c> in the clone is written once at clone
    ///     time and never updated, so it is stale exactly when this is needed.
    /// </summary>
    private List<GitReference>? AdvertisedReferences(ProjectRepository repository,
        CancellationToken cancellationToken)
    {
        // Checked here and nowhere inside: ListRemoteReferences takes no FetchOptions, so there is no
        // OnTransferProgress to hang a cancellation on the way Fetch does. The advertisement is one
        // round-trip with no transfer behind it, and the stall limit bounds it like any other wait on
        // the remote, so the window this leaves open is the short one.
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var credentials = Credentials(repository);
            return (credentials is null
                ? Repository.ListRemoteReferences(repository.Url)
                : Repository.ListRemoteReferences(repository.Url, credentials)).ToList();
        }
        catch (Exception ex) when (ex is LibGit2SharpException or IOException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Safe to swallow: the fetch above already succeeded, so the commits are here and a clone
            // whose HEAD resolves keeps serving them. Only an unborn HEAD turns this into a failure,
            // which the caller raises with the remediation in it.
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning("Could not read the default branch of repository {Repository} of project "
                                  + "{Project}; its local HEAD is left as it is", repository.Slug,
                    repository.ProjectSlug);
            return null;
        }
    }

    /// <summary>
    ///     The transfer settings a clone and a fetch share: cancellable, and carrying the stored
    ///     credential to libgit2 and nowhere else.
    ///     No <c>Depth</c>. It was 1 until ADR-0007, and dropping it is what makes history exist on disk
    ///     to be indexed at all — libgit2 implements no partial clone, so there is no way to take the
    ///     commits and trees without the blobs. A clone is therefore its repository's whole object
    ///     store, and the free-space gate in <c>RefreshService</c> is what stands between that and the
    ///     ephemeral disk.
    ///     Both progress callbacks are watched, the remote's own messages as well as the objects,
    ///     because either is the remote still talking; a failure after neither has fired for the stall
    ///     limit is the limit running out.
    /// </summary>
    private TransferWatch Configure(FetchOptions options, ProjectRepository repository,
        CancellationToken cancellationToken)
    {
        var watch = new TransferWatch(TimeSpan.FromSeconds(_stallSeconds));
        options.OnTransferProgress = _ => watch.Heard(cancellationToken);
        options.OnProgress = _ => watch.Heard(cancellationToken);
        options.CredentialsProvider = Credentials(repository);
        return watch;
    }

    /// <summary>
    ///     The sentence a failed clone or fetch is reported with. A remote that went quiet for the stall
    ///     limit is told apart from one that answered with an error, because the two are fixed in
    ///     different places: a quiet one is down or overloaded, an erroring one usually has the wrong URL
    ///     or credential.
    ///     The URL carries no password (RepositoryUrl refuses one), and the token only ever reached
    ///     libgit2 through CredentialsProvider, so neither the URL nor libgit2's message can hold it. The
    ///     exception is not attached as InnerException, so nothing beyond this text is serialised.
    ///     libgit2's message can hold the local copy's path, though, when the failure was on this side:
    ///     it names the directory it could not write. That path is replaced before the message leaves,
    ///     and logged whole for the operator, who is the one reader able to reach it (#232).
    /// </summary>
    private McpException TransferFailed(string verb, ProjectRepository repository, string path, Exception ex,
        TransferWatch watch)
    {
        string reason = watch.Stalled
            ? $"the remote stopped responding and sent nothing for {_stallSeconds} seconds. Ask the operator to "
              + $"check that the remote is reachable and up, then try again; {TransferStallLimit.Setting} raises "
              + "the limit for a remote that is slow to start sending."
            : $"{WithoutPath(ex.Message, path).TrimEnd('.')}. Ask the operator to check the URL and the stored "
              + "credential for this repository, then try again.";
        // The message and not the exception: RefreshService logs the failure with its stack already,
        // and what only this line can add is libgit2's own words with the path still in them.
        if (logger.IsEnabled(LogLevel.Warning))
            logger.LogWarning("{Verb} repository {Repository} of project {Project} into {Path} failed: {Reason}",
                verb, repository.Slug, repository.ProjectSlug, path, ex.Message);
        return new McpException($"{verb} repository '{repository.Slug}' from '{repository.Url}' failed: {reason}");
    }

    /// <summary>
    ///     The message with the local copy's path written as "the local copy", and then any other path
    ///     under the folder holding every local copy — a parent libgit2 could not create — written as
    ///     "the local copies". Both separators, because libgit2 writes forward slashes on Windows where
    ///     .NET writes back slashes.
    /// </summary>
    private string WithoutPath(string message, string path) =>
        Without(Without(message, path, "the local copy"), _cloneRoot, "the local copies");

    private static string Without(string message, string path, string replacement) =>
        message.Replace(path, replacement, StringComparison.OrdinalIgnoreCase)
            .Replace(path.Replace('\\', '/'), replacement, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     When a transfer last heard from its remote. This, and not the exception, is how a stall is
    ///     recognised: LibGit2Sharp drops libgit2's error code, so a timeout arrives as a plain
    ///     <see cref="LibGit2SharpException" /> whose message is the operating system's, in its language.
    ///     No callback fires during the reference discovery, so an error that ends a discovery which
    ///     trickled for longer than the limit reads as a stall too; that is a remote slow enough to be
    ///     reported as one.
    /// </summary>
    private sealed class TransferWatch(TimeSpan limit)
    {
        // libgit2 starts its wait after the callback that stamped this returns, so a real timeout is
        // always at least the limit away from it; the tenth off absorbs the two clocks disagreeing.
        private readonly TimeSpan _threshold = limit * 0.9;
        // Plain, not volatile: libgit2 calls back on the thread running the transfer, which is the
        // thread that reads Stalled once the transfer has thrown.
        private long _lastHeard = Stopwatch.GetTimestamp();

        public bool Stalled => Stopwatch.GetElapsedTime(_lastHeard) >= _threshold;

        /// <summary>Stamps the remote as heard from, and answers libgit2 whether to carry on.</summary>
        public bool Heard(CancellationToken cancellationToken)
        {
            _lastHeard = Stopwatch.GetTimestamp();
            return !cancellationToken.IsCancellationRequested;
        }
    }

    /// <summary>
    ///     The stored credential as libgit2 wants it, or null when the repository has none. This is the
    ///     only place the plaintext exists, and it reaches nothing but libgit2 from here.
    /// </summary>
    private CredentialsHandler? Credentials(ProjectRepository repository)
    {
        if (repository.ProtectedCredential is not { } ciphertext) return null;

        string token;
        try
        {
            token = _protector.Unprotect(ciphertext);
        }
        catch (CryptographicException)
        {
            // The key ring that protected it is gone: a restart with the key ring still local (see
            // KeyRing, which logs which shape the replica came up in), or a rotated Key Vault key.
            throw new McpException(
                $"The stored credential for repository '{repository.Slug}' cannot be decrypted because the "
                + "Data Protection key ring has changed. Ask the operator to set the credential again.");
        }

        // GitHub and Azure DevOps both accept a token as the password with any user name.
        return (_, _, _) => new UsernamePasswordCredentials { Username = "token", Password = token };
    }
}
