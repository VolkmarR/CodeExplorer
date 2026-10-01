using System.Net;
using System.Net.Http.Json;
using System.Text;
using CodeExplorer.Index;
using CodeExplorer.Refresh;
using LibGit2Sharp;
using Xunit;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CodeExplorer.Tests;

/// <summary>
///     Repositories added through the operator API, and the local copy a refresh makes of them: a
///     full bare clone that nothing but the refresh reads (CONTEXT.md, Local copy; ADR-0007). Fixtures
///     are local repositories built with LibGit2Sharp, so the suite never touches the network; the
///     local transport cannot serve a shallow clone (ADR-0003), so the shallow copy an older server
///     left behind is made by hand (<see cref="TestHost.MakeLocalCopyShallow" />). What the index built
///     from a clone answers is asserted where the tools are tested.
/// </summary>
public sealed class RepositoryTests : IDisposable
{
    private const string Secret = "pat-secret-token-3f9a";

    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_refresh_clones_bare_and_the_clone_is_not_the_answer()
    {
        string source = _host.CreateGitRepository("source", new Dictionary<string, string>
        {
            ["README.md"] = "hello",
            ["src/Program.cs"] = "class P {}"
        });
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "main", source);

        // No clone before the refresh: a tool call must never make the server talk to a remote.
        string cloneDir = _host.ClonePath("alpha", "main");
        Assert.False(Directory.Exists(cloneDir));

        var summary = await _host.RefreshAsync("alpha");
        Assert.Equal(2, summary.Files);

        // Bare: the clone directory has objects but no working copy of README.md.
        Assert.True(Directory.Exists(Path.Combine(cloneDir, "objects")));
        Assert.False(File.Exists(Path.Combine(cloneDir, "README.md")));
    }

    /// <summary>
    ///     A local copy that is this repository's and full is fetched into, and one left shallow by a
    ///     server from before ADR-0007 is cloned over, since a fetch never deepens it. A file dropped into
    ///     the copy tells the two apart: a fetch leaves it, a clone over the folder deletes it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_full_local_copy_is_fetched_into_and_a_shallow_one_is_cloned_over(bool shallow)
    {
        string source = _host.CreateGitRepository("source", new Dictionary<string, string> { ["README.md"] = "hello" });
        _host.CommitToGitRepository("source", new Dictionary<string, string> { ["README.md"] = "hello again" });
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "main", source);
        await _host.RefreshAsync("alpha");
        string copy = _host.ClonePath("alpha", "main");
        if (shallow) _host.MakeLocalCopyShallow("alpha", "main", "source");
        string marker = Path.Combine(copy, "marker");
        await File.WriteAllTextAsync(marker, "", TestContext.Current.CancellationToken);

        await _host.RefreshAsync("alpha");

        Assert.Equal(!shallow, File.Exists(marker));
        using (var repository = new Repository(copy))
        {
            Assert.False(repository.Info.IsShallow);
            Assert.Equal(2, repository.Commits.Count());
        }

        if (shallow) _host.Logs.Only(LogLevel.Warning, "is shallow");
    }

    [Fact]
    public async Task Clone_failure_is_an_actionable_message_that_never_carries_the_credential()
    {
        await _host.CreateProjectAsync("alpha");
        string missing = _host.ScratchFile("does-not-exist");
        await _host.AddRepositoryAsync("alpha", "broken", missing, Secret);

        // The only repository could not be read, so the refresh fails as a whole and its error is the
        // clone's message.
        using (var response = await _host.RequestRefreshAsync("alpha"))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await _host.WaitForRefreshesAsync();
        var status = await _host.RefreshStatusAsync("alpha");
        Assert.Equal(RefreshState.Failed, status.State);
        string error = Assert.IsType<string>(status.Error);

        Assert.Contains("broken", error);
        Assert.Contains("credential", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, error);
        Assert.DoesNotContain("   at ", error);
    }

    [Fact]
    public async Task Credential_is_write_only_through_the_api()
    {
        await _host.CreateProjectAsync("alpha");
        using var http = _host.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var created = await http.PostAsJsonAsync("/api/projects/alpha/repositories",
            new { slug = "main", url = "https://example.invalid/repo.git", credential = Secret }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string createdBody = await created.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain(Secret, createdBody);
        Assert.Contains("\"hasCredential\":true", createdBody);

        string listBody = await http.GetStringAsync("/api/projects/alpha/repositories", ct);
        Assert.DoesNotContain(Secret, listBody);
        Assert.Contains("main", listBody);
    }

    [Fact]
    public async Task Adding_a_repository_validates_project_slug_url_and_duplicates()
    {
        await _host.CreateProjectAsync("alpha");
        using var http = _host.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var body = new { slug = "main", url = "https://example.invalid/repo.git" };

        using var noProject = await http.PostAsJsonAsync("/api/projects/nope/repositories", body, ct);
        Assert.Equal(HttpStatusCode.NotFound, noProject.StatusCode);

        using var badSlug = await http.PostAsJsonAsync("/api/projects/alpha/repositories",
            new { slug = "Bad Slug", body.url }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, badSlug.StatusCode);

        // A token in the URL would land on disk in the clone's remote config; the API refuses it.
        using var userInfo = await http.PostAsJsonAsync("/api/projects/alpha/repositories",
            new { slug = "main", url = $"https://user:{Secret}@example.invalid/repo.git" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, userInfo.StatusCode);
        Assert.DoesNotContain(Secret, await userInfo.Content.ReadAsStringAsync(ct));

        using var first = await http.PostAsJsonAsync("/api/projects/alpha/repositories", body, ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var dup = await http.PostAsJsonAsync("/api/projects/alpha/repositories", body, ct);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    /// <summary>
    ///     A branch whose lock file fits under Windows' 260 characters in <c>refs/heads/</c> and not in
    ///     <c>refs/remotes/origin/</c>, eight characters longer, is fetched into the copy. Before the copy's
    ///     refspec was its fetch's, libgit2 wrote every branch under both names, and the longer one failed
    ///     the whole fetch with "path too long"; <c>core.longpaths</c> does not lift libgit2's limit. The
    ///     name is sized from this run's clone path, since temp paths differ between machines.
    /// </summary>
    [Fact]
    public async Task A_branch_too_long_to_track_under_refs_remotes_is_still_fetched()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows limits libgit2's paths to 260 characters.");
        string source = _host.CreateGitRepository("source", new Dictionary<string, string> { ["README.md"] = "hello" });
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "main", source);
        await _host.RefreshAsync("alpha");
        string copy = _host.ClonePath("alpha", "main");
        string branch = BranchFilling(copy, 254);
        Assert.True(copy.Length + "/refs/remotes/origin/".Length + branch.Length + ".lock".Length > 260);
        _host.CreateBranch("source", branch);

        await _host.RefreshAsync("alpha");

        using var repository = new Repository(copy);
        Assert.Equal(_host.HeadOf("source"), repository.Refs["refs/heads/" + branch]?.TargetIdentifier);
        AssertMirrorsBranchesOnly(repository);
    }

    /// <summary>
    ///     A path too long sends the operator to the data directory, not to the URL and the credential,
    ///     which are fine. A first clone is how to reach it: libgit2's clone still writes each branch
    ///     under <c>refs/remotes/origin/</c> before any of this server's code runs, and that copy is too
    ///     long here although the branch's own ref would fit.
    /// </summary>
    [Fact]
    public async Task A_path_too_long_names_the_data_directory_and_not_the_credential()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows limits libgit2's paths to 260 characters.");
        string copy = _host.ClonePath("alpha", "main");
        string branch = BranchFilling(copy, 254);
        string source = _host.CreateGitRepository("source", new Dictionary<string, string> { ["README.md"] = "hello" });
        _host.CreateBranch("source", branch);
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "main", source);

        string error = await _host.FailedRefreshErrorAsync("alpha");

        Assert.Contains("path too long", error);
        Assert.Contains("Storage:DataDirectory", error);
        Assert.Contains($"{copy.Length} characters", error);
        Assert.DoesNotContain("credential", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(copy, error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A first clone leaves the copy as a fetch would: every branch of the remote a local branch,
    ///     the refspec the fetch uses, nothing under <c>refs/remotes</c>. And a branch deleted upstream
    ///     is still pruned once the prune has only the local branches to go by.
    /// </summary>
    [Fact]
    public async Task A_first_clone_mirrors_every_branch_locally_and_a_fetch_prunes_one_deleted_upstream()
    {
        string source = _host.CreateGitRepository("source", new Dictionary<string, string> { ["README.md"] = "hello" });
        _host.CreateBranch("source", "feature/one");
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "main", source);
        await _host.RefreshAsync("alpha");
        string copy = _host.ClonePath("alpha", "main");
        using (var repository = new Repository(copy))
        {
            Assert.Equal(_host.HeadOf("source"), repository.Refs["refs/heads/feature/one"]?.TargetIdentifier);
            AssertMirrorsBranchesOnly(repository);
        }

        using (var upstream = new Repository(source)) upstream.Branches.Remove("feature/one");
        await _host.RefreshAsync("alpha");

        using (var repository = new Repository(copy))
        {
            Assert.Null(repository.Refs["refs/heads/feature/one"]);
            Assert.Equal("refs/heads/" + _host.BranchOf("source"), repository.Refs.Head.TargetIdentifier);
            AssertMirrorsBranchesOnly(repository);
        }
    }

    /// <summary>
    ///     A copy made before its refspec was changed — git's default refspec configured and a
    ///     remote-tracking ref for every branch, loose and packed, beside the local branches its fetches
    ///     wrote — is changed over on its next fetch, and nothing a reader sees moves: the branches, HEAD
    ///     and the index built from it are what they were.
    /// </summary>
    [Fact]
    public async Task A_copy_tracking_its_remote_the_old_way_stops_on_its_next_fetch()
    {
        string source = _host.CreateGitRepository("source", new Dictionary<string, string> { ["README.md"] = "hello" });
        _host.CreateBranch("source", "feature/one");
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "main", source);
        await _host.RefreshAsync("alpha");
        var before = await AnswersAsync();
        string copy = _host.ClonePath("alpha", "main");
        List<string> branches;
        string head;
        using (var repository = new Repository(copy))
        {
            repository.Network.Remotes.Update("origin",
                r => r.FetchRefSpecs = ["+refs/heads/*:refs/remotes/origin/*"]);
            branches = LocalBranches(repository);
            head = repository.Refs.Head.TargetIdentifier;
            repository.Refs.Add("refs/remotes/origin/" + _host.BranchOf("source"), _host.HeadOf("source"));
            repository.Refs.Add("refs/remotes/origin/HEAD", "refs/remotes/origin/" + _host.BranchOf("source"));
        }

        // Packed as well as loose, because a ref in packed-refs has no file of its own to delete.
        string packedRefs = Path.Combine(copy, "packed-refs");
        var packed = File.Exists(packedRefs)
            ? File.ReadAllLines(packedRefs).Where(line => !line.StartsWith('#')).ToList()
            : [];
        packed.Add($"{_host.HeadOf("source")} refs/remotes/origin/feature/one");
        File.WriteAllLines(packedRefs, packed);
        using (var repository = new Repository(copy))
            Assert.Equal(3,
                repository.Refs.Count(r => r.CanonicalName.StartsWith("refs/remotes/", StringComparison.Ordinal)));

        await _host.RefreshAsync("alpha");

        using (var repository = new Repository(copy))
        {
            AssertMirrorsBranchesOnly(repository);
            Assert.Equal(branches, LocalBranches(repository));
            Assert.Equal(head, repository.Refs.Head.TargetIdentifier);
        }

        if (File.Exists(packedRefs))
            Assert.DoesNotContain("refs/remotes/",
                await File.ReadAllTextAsync(packedRefs, TestContext.Current.CancellationToken),
                StringComparison.Ordinal);
        Assert.Equal(before, await AnswersAsync());
    }

    /// <summary>The copy's one refspec is the fetch's own, and no remote-tracking ref is left in it.</summary>
    private static void AssertMirrorsBranchesOnly(Repository repository)
    {
        Assert.Equal("+refs/heads/*:refs/heads/*",
            Assert.Single(repository.Network.Remotes["origin"].FetchRefSpecs).Specification);
        Assert.Empty(repository.Refs.Where(r => r.CanonicalName.StartsWith("refs/remotes/", StringComparison.Ordinal))
            .Select(r => r.CanonicalName));
    }

    /// <summary>Every local branch of the copy with its tip, in order.</summary>
    private static List<string> LocalBranches(Repository repository) =>
    [
        .. repository.Refs.Where(r => r.CanonicalName.StartsWith("refs/heads/", StringComparison.Ordinal))
            .Select(r => $"{r.CanonicalName} {r.TargetIdentifier}").Order(StringComparer.Ordinal)
    ];

    /// <summary>What the index built from the copy answers: every file, and every commit with its subject.</summary>
    private async Task<List<string>[]> AnswersAsync() =>
    [
        await _host.ScalarsAsync("alpha", "SELECT qualified_path FROM files ORDER BY qualified_path"),
        await _host.ScalarsAsync("alpha", "SELECT sha || ' ' || subject FROM commits ORDER BY sha")
    ];

    /// <summary>
    ///     A branch name that makes <c>{copy}/refs/heads/{name}.lock</c> exactly <paramref name="lockLength" />
    ///     characters, in folders of under a hundred, since NTFS limits a single name to 255.
    /// </summary>
    private static string BranchFilling(string copy, int lockLength)
    {
        int length = lockLength - (copy.Length + "/refs/heads/".Length + ".lock".Length);
        var name = new StringBuilder("x");
        while (name.Length < length)
            name.Append(name.Length % 100 == 99 && name.Length < length - 1 ? '/' : 'n');
        return name.ToString();
    }
}
