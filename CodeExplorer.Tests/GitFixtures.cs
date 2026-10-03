using System.Text;
using LibGit2Sharp;

namespace CodeExplorer.Tests;

/// <summary>
///     A temporary directory of git repositories built with LibGit2Sharp, deleted on dispose. For a
///     test about git alone it is the whole world it needs; <see cref="TestHost" /> is one with a server
///     added, so the remotes a test points its server at and the server's own data directory share one
///     root and go together. Nothing here touches a server.
/// </summary>
public class GitFixtures : IDisposable
{
    /// <summary>Everything this instance writes is under here, and goes with it.</summary>
    protected string Root { get; } =
        Path.Combine(Path.GetTempPath(), "CodeExplorer.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Deletes the root. A subclass that holds files open under it releases them first.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing) DeleteTree(Root);
    }

    /// <summary>
    ///     Removes a directory git has written into. libgit2 marks pack files read-only and
    ///     <c>Directory.Delete</c> refuses a read-only file, so the attributes are cleared first rather
    ///     than left to fail on the first pack.
    ///     A refused delete is tried again for a moment, because a file DuckDB has just removed can
    ///     linger: closing a database's last connection deletes its <c>.wal</c>, and while another
    ///     process — a virus scanner, the search indexer — still has the freshly written file open, it
    ///     is listed but refuses deletion with "access denied". Measured on this machine it is gone
    ///     61 ms later, and before this retry it failed whichever test's cleanup met it, so a
    ///     different test each run. The same file can also vanish between being listed and having
    ///     its attributes cleared, which needs nothing more than skipping it. It does not defeat a file
    ///     mapped into this process, which no wait releases: a caller with such a tree passes
    ///     <paramref name="retry" /> false and catches the failure itself.
    /// </summary>
    public static void DeleteTree(string path, bool retry = true)
    {
        if (!Directory.Exists(path)) return;

        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                file.Attributes = FileAttributes.Normal;
            }
            catch (Exception gone) when (gone is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deleted since it was listed: nothing left to clear.
            }
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (Exception refused) when (retry && attempt < DeleteAttempts
                                             && refused is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(DeleteRetryMilliseconds);
                // The attempt that threw may have removed everything but the directory's own entry.
                if (!Directory.Exists(path)) return;
            }
        }
    }

    /// <summary>
    ///     Half a second in all, several times the 61 ms a lingering WAL was measured to need, and short
    ///     enough that a file that will never go fails the cleanup promptly instead of stalling it.
    /// </summary>
    private const int DeleteAttempts = 10;

    private const int DeleteRetryMilliseconds = 50;

    /// <summary>Builds a non-bare repository with one commit holding the given files and returns its path.</summary>
    public string CreateGitRepository(string name, Dictionary<string, string> files) =>
        Commit(CreateEmptyGitRepository(name), files);

    /// <summary>
    ///     The same with each file's bytes written as given, for a test about how content is decoded:
    ///     the string overload always writes UTF-8, so it cannot commit a Windows-1252 or UTF-16 file.
    /// </summary>
    public string CreateGitRepository(string name, Dictionary<string, byte[]> files) =>
        Commit(CreateEmptyGitRepository(name), files);

    /// <summary>
    ///     An initialised repository with no commits: a remote that really is empty, as opposed to a
    ///     clone whose HEAD lost the branch it named. The two look alike from HEAD's tip and must not
    ///     be reported alike.
    /// </summary>
    public string CreateEmptyGitRepository(string name)
    {
        string path = FixturePath(name);
        Repository.Init(path);
        return path;
    }

    /// <summary>
    ///     Renames the branch a fixture's HEAD is on, which is what a default branch renamed upstream
    ///     looks like from here: the old name is gone, so the next fetch prunes it out of the clone
    ///     that was made while HEAD still named it.
    /// </summary>
    public void RenameDefaultBranch(string name, string branch)
    {
        using var repo = new Repository(FixturePath(name));
        repo.Branches.Rename(repo.Head, branch);
    }

    /// <summary>A second branch on a fixture, at the commit its HEAD is at.</summary>
    public void CreateBranch(string name, string branch)
    {
        using var repo = new Repository(FixturePath(name));
        repo.CreateBranch(branch);
    }

    /// <summary>The commit a fixture's HEAD is at, for a test that resets the fixture back to it later.</summary>
    public string HeadOf(string name)
    {
        using var repo = new Repository(FixturePath(name));
        return repo.Head.Tip.Sha;
    }

    /// <summary>
    ///     <c>reset --hard</c> on a fixture, which the next forced fetch mirrors as a force push would.
    ///     Hard, so a commit made on top afterwards starts from that commit's tree.
    /// </summary>
    public void ResetGitRepository(string name, string sha)
    {
        using var repo = new Repository(FixturePath(name));
        repo.Reset(ResetMode.Hard, repo.Lookup<Commit>(sha));
    }

    /// <summary>
    ///     Points a repository's HEAD at a branch that does not exist: the state #31 left a clone in,
    ///     and, on a fixture, a remote whose own default branch cannot be resolved. Written as a file
    ///     because that is all HEAD is, and because libgit2 refuses such a symbolic reference.
    /// </summary>
    public static void BreakHead(string gitDirectory, string branch) =>
        WriteHead(gitDirectory, $"ref: refs/heads/{branch}");

    /// <summary>
    ///     Detaches a repository's HEAD at a commit, so a fixture advertises HEAD as a commit id rather
    ///     than as a symbolic reference naming a branch (#260).
    /// </summary>
    public static void DetachHead(string gitDirectory, string sha) => WriteHead(gitDirectory, sha);

    /// <summary>
    ///     A push to a branch of a fixture whose HEAD is detached, which stays detached at
    ///     <paramref name="detachedAt" />: the remote's branch moves on and its HEAD does not (#288).
    /// </summary>
    public void PushWhileDetached(string name, string branch, string detachedAt, Dictionary<string, string> files)
    {
        BreakHead(FixtureGitPath(name), branch);
        CommitToGitRepository(name, files);
        DetachHead(FixtureGitPath(name), detachedAt);
    }

    /// <summary>The branch a fixture's HEAD is on, which is whatever <c>init.defaultBranch</c> made it.</summary>
    public string BranchOf(string name)
    {
        using var repo = new Repository(FixturePath(name));
        return repo.Head.FriendlyName;
    }

    private static void WriteHead(string gitDirectory, string content) =>
        File.WriteAllText(Path.Combine(gitDirectory, "HEAD"), content + "\n");

    /// <summary>The <c>.git</c> directory of a fixture, which is non-bare.</summary>
    public string FixtureGitPath(string name) => Path.Combine(FixturePath(name), ".git");

    /// <summary>
    ///     Adds a commit to a fixture already created, which is what a push to the remote looks like
    ///     from here: the clone made earlier still holds the old tree until something fetches.
    /// </summary>
    public string CommitToGitRepository(string name, Dictionary<string, string> files) =>
        Commit(FixturePath(name), files);

    /// <summary>
    ///     A commit with an author and a message of its own, for a history test: the default fixture
    ///     author and subject are the same on every commit, which is exactly what a test asserting who
    ///     wrote what cannot use. The date advances with <paramref name="minute" /> so that two commits
    ///     are orderable, which at the shared epoch they are not.
    /// </summary>
    public string CommitToGitRepositoryAs(string name, Dictionary<string, string> files, string subject,
        string authorName, string authorEmail, int minute) =>
        Commit(FixturePath(name), files, subject,
            new Signature(authorName, authorEmail, DateTimeOffset.UnixEpoch.AddMinutes(minute)));

    /// <summary>
    ///     A commit that deletes paths rather than writing them, for a history test about a file that
    ///     the recorded window changed and HEAD no longer holds. The two cases cannot be one call: a
    ///     deletion is an absent key, and an absent key is indistinguishable from a file the commit
    ///     simply did not touch.
    /// </summary>
    public void RemoveInGitRepositoryAs(string name, IEnumerable<string> paths, string subject, string authorName,
        string authorEmail, int minute)
    {
        using var repo = new Repository(FixturePath(name));
        foreach (string relative in paths) Commands.Remove(repo, relative);
        var author = new Signature(authorName, authorEmail, DateTimeOffset.UnixEpoch.AddMinutes(minute));
        repo.Commit(subject, author, author);
    }

    /// <summary>
    ///     A commit that moves paths, content unchanged — one commit holding the removal of each old
    ///     path and the addition of each new one, which is how git records a move and the only shape
    ///     libgit2's rename detection reports as <c>renamed</c>. A remove commit followed by an add
    ///     commit is two unrelated changes, and a test built that way proves nothing about renames
    ///     (#131).
    /// </summary>
    /// <param name="name">The fixture repository.</param>
    /// <param name="moves">Old repository-relative path to new, all in one commit.</param>
    /// <param name="subject">The commit's subject.</param>
    /// <param name="authorName">Who to record as the author.</param>
    /// <param name="authorEmail">Their address.</param>
    /// <param name="minute">Minutes past the epoch, which is how these fixtures order their history.</param>
    public void MoveInGitRepositoryAs(string name, Dictionary<string, string> moves, string subject,
        string authorName, string authorEmail, int minute)
    {
        string root = FixturePath(name);
        using var repo = new Repository(root);
        foreach ((string from, string to) in moves)
        {
            string source = Path.Combine(root, from);
            string target = Path.Combine(root, to);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);
            Commands.Remove(repo, from);
            Commands.Stage(repo, to);
        }

        var author = new Signature(authorName, authorEmail, DateTimeOffset.UnixEpoch.AddMinutes(minute));
        repo.Commit(subject, author, author);
    }

    // Encoding.UTF8.GetBytes writes no byte order mark, as File.WriteAllText did before the bytes overload.
    private static string Commit(string path, Dictionary<string, string> files, string subject = "fixture",
        Signature? author = null) =>
        Commit(path, files.ToDictionary(file => file.Key, file => Encoding.UTF8.GetBytes(file.Value)), subject,
            author);

    private static string Commit(string path, Dictionary<string, byte[]> files, string subject = "fixture",
        Signature? author = null)
    {
        using var repo = new Repository(path);
        foreach ((string relative, byte[] content) in files)
        {
            string full = Path.Combine(path, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
            Commands.Stage(repo, relative);
        }

        author ??= new Signature("Test", "test@example.invalid", DateTimeOffset.UnixEpoch);
        repo.Commit(subject, author, author);
        return path;
    }

    /// <summary>
    ///     Where <see cref="CreateGitRepository(string, Dictionary{string, string})" /> put the fixture
    ///     with this name, or would put it: a test that clones or initialises a repository of its own
    ///     names its path here too, so it goes with the rest.
    /// </summary>
    public string FixturePath(string name) => Path.Combine(Root, "fixtures", name);

    /// <summary>Deletes a fixture, which is what a remote that was removed or renamed looks like from here.</summary>
    public void RemoveGitRepository(string name) => DeleteTree(FixturePath(name));
}
