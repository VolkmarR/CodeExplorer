using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     A <c>path</c> argument, across the tools that take one. There are four states a path can be
///     in — live, recorded and gone from HEAD, in neither, and in a repository the call also named —
///     and the point of these is that they are four different sentences: an agent that met one of them
///     for another would take a typo for a quiet module, or a deleted file for a misspelling.
/// </summary>
public sealed class PathScopeTests(PathScopeFixture fixture) : IClassFixture<PathScopeFixture>
{
    private readonly TestHost _host = fixture.Host;

    /// <summary>
    ///     "Who owns this folder" and "what has happened in this folder" are one call each (#118). They
    ///     used to be a file_history per file, because a path argument was accepted and ignored.
    /// </summary>
    [Fact]
    public async Task Authors_and_git_log_scope_to_a_path()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Churn);

        // src holds the four recent commits; old/Ancient.cs holds the one import, by Ada alone.
        string owners = await TestHost.CallAsync(client, "authors",
            new Dictionary<string, object?> { ["path"] = "one/old" });
        Assert.Contains("under 'one/old'", owners, StringComparison.Ordinal);
        Assert.Contains("ada@example.invalid", owners, StringComparison.Ordinal);
        Assert.DoesNotContain("grace@example.invalid", owners, StringComparison.Ordinal);
        Assert.Contains("matched by the path each commit recorded", owners, StringComparison.Ordinal);

        string log = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["path"] = "one/src" });
        Assert.Contains("under 'one/src'", log, StringComparison.Ordinal);
        Assert.Contains("Drop the dead file", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Import the old code", log, StringComparison.Ordinal);

        // One exact file is the same argument, and it combines with repo and author.
        string one = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?>
                { ["path"] = "one/src/Hot.cs", ["repo"] = "one", ["author"] = "grace" });
        Assert.Contains("under 'one/src/Hot.cs'", one, StringComparison.Ordinal);
        Assert.Contains("Fix the check", one, StringComparison.Ordinal);
        Assert.DoesNotContain("Add the module", one, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A scope nothing was committed under and a scope that is not there are opposite facts, and the
    ///     first must not read as the second (#118). The fixture's docs folder is committed once, so a
    ///     path with no commits at all is made by deleting the rows rather than by finding a corner.
    /// </summary>
    [Fact]
    public async Task A_path_scope_with_no_commits_is_told_apart_from_a_path_that_is_not_there()
    {
        await HistoryFixtures.BuildChurnProjectAsync(_host, "quiet", withSecondRepository: false);
        await _host.ExecuteAsync("quiet", "DELETE FROM commit_files WHERE path LIKE 'docs/%'");

        await using var client = await _host.ConnectAsync("quiet");

        string quiet = await TestHost.CallAsync(client, "authors",
            new Dictionary<string, object?> { ["path"] = "one/docs" });
        Assert.Contains("No commits are recorded under 'one/docs', so no authors are", quiet,
            StringComparison.Ordinal);
        Assert.Contains("begins where a file was last renamed", quiet, StringComparison.Ordinal);

        string nowhere = await TestHost.CallAsync(client, "authors",
            new Dictionary<string, object?> { ["path"] = "one/nowhere" });
        Assert.Contains("names nothing in this index", nowhere, StringComparison.Ordinal);
        Assert.Contains("glob or list_tree", nowhere, StringComparison.Ordinal);
        Assert.DoesNotContain("so no authors are", nowhere, StringComparison.Ordinal);
    }

    /// <summary>
    ///     hot_files ranks a path a later commit deleted, and git_log and authors used to refuse the same
    ///     path in the same session — in the words reserved for a path the index never heard of, which
    ///     tells an agent it made a typo (#132). Three tools, one index, one path: they agree it exists.
    /// </summary>
    [Fact]
    public async Task Git_log_and_authors_scope_to_a_path_that_history_records_and_head_no_longer_holds()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Churn);

        string ranked = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30 });
        Assert.Contains("one/src/Gone.cs  (no longer at HEAD)", ranked, StringComparison.Ordinal);

        string log = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["path"] = "one/src/Gone.cs" });
        Assert.DoesNotContain("names nothing in this index", log, StringComparison.Ordinal);
        Assert.Contains("under 'one/src/Gone.cs'", log, StringComparison.Ordinal);
        // Its own commits, newest first, and nothing from the file beside it.
        Assert.True(log.IndexOf("Drop the dead file", StringComparison.Ordinal)
                    < log.IndexOf("Add the module", StringComparison.Ordinal));
        Assert.Contains("grace@example.invalid", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Fix the check", log, StringComparison.Ordinal);
        Assert.Contains("Nothing is at 'one/src/Gone.cs' now (no longer at HEAD)", log, StringComparison.Ordinal);

        string owners = await TestHost.CallAsync(client, "authors",
            new Dictionary<string, object?> { ["path"] = "one/src/Gone.cs" });
        Assert.DoesNotContain("names nothing in this index", owners, StringComparison.Ordinal);
        Assert.Contains("ada@example.invalid", owners, StringComparison.Ordinal);
        Assert.Contains("grace@example.invalid", owners, StringComparison.Ordinal);
        Assert.Contains("Nothing is at 'one/src/Gone.cs' now (no longer at HEAD)", owners, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The log-shaped read of one path answers for a path HEAD no longer holds, the same way the log
    ///     itself does (#136). git_log's own description routes an agent here for "the same read for one
    ///     exact path", so refusing the very path git_log had just answered for reported a typo the
    ///     caller had not made. The not-at-HEAD sentence is asserted against git_log's, word for word:
    ///     one fact, one wording, or an agent learns there are two.
    /// </summary>
    [Fact]
    public async Task File_history_lists_a_path_history_records_and_head_no_longer_holds()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Churn);

        string listed = await TestHost.CallAsync(client, "file_history",
            new Dictionary<string, object?> { ["path"] = "one/src/Gone.cs" });

        Assert.DoesNotContain("No indexed file", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("glob or list_tree", listed, StringComparison.Ordinal);
        Assert.Contains("changed one/src/Gone.cs, newest first", listed, StringComparison.Ordinal);
        // Its own commits, newest first, and nothing from the file beside it.
        Assert.True(listed.IndexOf("Drop the dead file", StringComparison.Ordinal)
                    < listed.IndexOf("Add the module", StringComparison.Ordinal));
        Assert.DoesNotContain("Fix the check", listed, StringComparison.Ordinal);

        const string note = "Nothing is at 'one/src/Gone.cs' now (no longer at HEAD)";
        Assert.Contains(note, listed, StringComparison.Ordinal);
        string log = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["path"] = "one/src/Gone.cs" });
        Assert.Contains(note, log, StringComparison.Ordinal);

        // A live path answers exactly as it did, with nothing said about HEAD.
        string live = await TestHost.CallAsync(client, "file_history",
            new Dictionary<string, object?> { ["path"] = "one/src/Hot.cs" });
        Assert.Contains("changed one/src/Hot.cs, newest first", live, StringComparison.Ordinal);
        Assert.DoesNotContain("no longer at HEAD", live, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The two tools whose answer is about the file that is there keep refusing, which is correct —
    ///     blame attributes the lines of the newest recorded commit (ADR-0007) and there are none, and
    ///     co_changed's historical anchor is a decision deferred to #115/#127. What changes is the
    ///     refusal: it names the reason and the reads that do answer, instead of advising on a spelling
    ///     that was right (#136).
    /// </summary>
    [Theory]
    [InlineData("blame")]
    [InlineData("co_changed")]
    public async Task Blame_and_co_changed_refuse_a_historical_path_by_naming_the_reason_and_the_reads_that_answer(
        string tool)
    {
        // A project slug takes no underscore, and `co_changed` has one.
        string slug = "goneread" + tool.Replace("_", "", StringComparison.Ordinal);
        await HistoryFixtures.BuildChurnProjectAsync(_host, slug, withSecondRepository: false);
        await using var client = await _host.ConnectAsync(slug);

        string refused = await TestHost.CallAsync(client, tool,
            new Dictionary<string, object?> { ["path"] = "one/src/Gone.cs" });

        Assert.Contains("'one/src/Gone.cs' is recorded in this project's history and HEAD no longer holds it",
            refused, StringComparison.Ordinal);
        Assert.Contains($"{tool} cannot answer for it", refused, StringComparison.Ordinal);
        Assert.Contains("git_log and file_history read the recorded history and still answer for this path",
            refused, StringComparison.Ordinal);
        // The fault this replaces: a refusal shaped like a typo report, for a path spelled correctly.
        Assert.DoesNotContain("No indexed file", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("glob or list_tree", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("Did you mean", refused, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The real typo case, which must stay distinguishable from the one above in all three tools: a
    ///     path neither HEAD nor any recorded commit holds keeps the locator's refusal, spelling advice
    ///     and all. Folding the two into one sentence is the fault, pointed the other way.
    /// </summary>
    [Theory]
    [InlineData("file_history")]
    [InlineData("blame")]
    [InlineData("co_changed")]
    public async Task A_path_neither_head_nor_history_holds_keeps_the_not_here_refusal(string tool)
    {
        string slug = "nowhere" + tool.Replace("_", "", StringComparison.Ordinal);
        await HistoryFixtures.BuildChurnProjectAsync(_host, slug, withSecondRepository: false);
        await using var client = await _host.ConnectAsync(slug);

        string refused = await TestHost.CallAsync(client, tool,
            new Dictionary<string, object?> { ["path"] = "one/src/Nowhere.cs" });

        Assert.Contains("No indexed file 'one/src/Nowhere.cs'", refused, StringComparison.Ordinal);
        Assert.Contains("glob or list_tree", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("no longer holds it", refused, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A directory a rename emptied is a scope git_log answers for and not a file these three can:
    ///     `one/legacy` records commits beneath it and none of its own. The single-file tools ask about
    ///     the exact path, so it stays the not-here refusal rather than becoming a historical file with
    ///     no commits to list — a quiet wrong answer where a refusal is the right one.
    /// </summary>
    [Fact]
    public async Task A_renamed_away_directory_is_not_a_historical_file()
    {
        await HistoryFixtures.BuildChurnProjectAsync(_host, "emptied", withSecondRepository: false);
        _host.CommitToGitRepositoryAs("emptied-one",
            new Dictionary<string, string> { ["legacy/Mover.cs"] = "m\n" },
            "Add the legacy helper", "Ada", "ada@example.invalid", 20000);
        _host.CommitToGitRepositoryAs("emptied-one",
            new Dictionary<string, string> { ["src/Mover.cs"] = "m\n" },
            "Move the helper", "Grace", "grace@example.invalid", 20001);
        _host.RemoveInGitRepositoryAs("emptied-one", ["legacy/Mover.cs"], "Move the helper, second half", "Grace",
            "grace@example.invalid", 20002);
        await _host.RefreshAsync("emptied");

        await using var client = await _host.ConnectAsync("emptied");

        // The scope-shaped read answers for it, as #132 made it.
        string log = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["path"] = "one/legacy" });
        Assert.Contains("Add the legacy helper", log, StringComparison.Ordinal);

        string listed = await TestHost.CallAsync(client, "file_history",
            new Dictionary<string, object?> { ["path"] = "one/legacy" });
        Assert.Contains("No indexed file 'one/legacy'", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("no longer holds it", listed, StringComparison.Ordinal);

        // The file under it, which a commit did record, is the historical case and is listed.
        string mover = await TestHost.CallAsync(client, "file_history",
            new Dictionary<string, object?> { ["path"] = "one/legacy/Mover.cs" });
        Assert.Contains("changed one/legacy/Mover.cs, newest first", mover, StringComparison.Ordinal);
        Assert.Contains("Nothing is at 'one/legacy/Mover.cs' now (no longer at HEAD)", mover,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     An address that matches nobody is answered before the scope is described, and the count it
    ///     offers was taken under that scope. Naming only the repository would hand an agent the
    ///     addresses of a path as the project's — and under a path HEAD no longer holds, a miss that
    ///     did not say so reads as a miss under a live one.
    /// </summary>
    [Fact]
    public async Task An_author_miss_under_a_path_names_the_path_and_says_it_is_gone()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Churn);

        string reply = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["path"] = "one/src/Gone.cs", ["author"] = "holger" });

        Assert.StartsWith("No address contains 'holger'", reply, StringComparison.Ordinal);
        Assert.Contains("under 'one/src/Gone.cs'", reply, StringComparison.Ordinal);
        Assert.Contains("Nothing is at 'one/src/Gone.cs' now (no longer at HEAD)", reply, StringComparison.Ordinal);

        // A live path says the scope without the gone sentence, so the two misses stay apart.
        string live = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["path"] = "one/src/Hot.cs", ["author"] = "holger" });
        Assert.Contains("under 'one/src/Hot.cs'", live, StringComparison.Ordinal);
        Assert.DoesNotContain("no longer at HEAD", live, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The other two halves of the same case: a path a rename rather than a deletion took away, and a
    ///     directory prefix rather than an exact file. A moved folder is the scope an agent asks about
    ///     most and the one a refusal misleads it about worst — "one/legacy names nothing" reads as a
    ///     folder that never existed rather than one whose whole history is still here.
    /// </summary>
    [Fact]
    public async Task A_directory_a_rename_emptied_is_scopeable_by_its_recorded_path()
    {
        await HistoryFixtures.BuildChurnProjectAsync(_host, "moved", withSecondRepository: false);
        // A rename as git records one, written the way Commit_files_marks_a_path_a_later_commit_renamed_away
        // writes it: the content at the new path, and then the old path gone.
        _host.CommitToGitRepositoryAs("moved-one",
            new Dictionary<string, string> { ["legacy/Mover.cs"] = "m\n" },
            "Add the legacy helper", "Ada", "ada@example.invalid", 20000);
        _host.CommitToGitRepositoryAs("moved-one",
            new Dictionary<string, string> { ["src/Mover.cs"] = "m\n" },
            "Move the helper", "Grace", "grace@example.invalid", 20001);
        _host.RemoveInGitRepositoryAs("moved-one", ["legacy/Mover.cs"], "Move the helper, second half", "Grace",
            "grace@example.invalid", 20002);
        await _host.RefreshAsync("moved");

        await using var client = await _host.ConnectAsync("moved");

        string log = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["path"] = "one/legacy" });
        Assert.Contains("under 'one/legacy'", log, StringComparison.Ordinal);
        Assert.Contains("Add the legacy helper", log, StringComparison.Ordinal);
        Assert.Contains("Move the helper, second half", log, StringComparison.Ordinal);
        Assert.Contains("Nothing is at 'one/legacy' now (no longer at HEAD)", log, StringComparison.Ordinal);

        string owners = await TestHost.CallAsync(client, "authors",
            new Dictionary<string, object?> { ["path"] = "one/legacy" });
        Assert.Contains("ada@example.invalid", owners, StringComparison.Ordinal);
        Assert.Contains("grace@example.invalid", owners, StringComparison.Ordinal);
        Assert.Contains("Nothing is at 'one/legacy' now (no longer at HEAD)", owners, StringComparison.Ordinal);

        // A path a live scope covers says nothing about HEAD, so the note marks the call it is about.
        string live = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["path"] = "one/src" });
        Assert.DoesNotContain("no longer at HEAD", live, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Four states, four sentences, asserted against each other rather than one at a time: a live
    ///     scope, a scope history records and HEAD has lost, a live scope nothing was committed under,
    ///     and a path this index never heard of. Folding any two of them into one sentence is the fault
    ///     #132 is (CODING_STANDARDS, Errors).
    /// </summary>
    [Fact]
    public async Task The_four_path_scope_answers_are_four_different_sentences()
    {
        await HistoryFixtures.BuildChurnProjectAsync(_host, "states", withSecondRepository: false);
        await _host.ExecuteAsync("states", "DELETE FROM commit_files WHERE path LIKE 'docs/%'");
        await using var client = await _host.ConnectAsync("states");

        async Task<string> AuthorsUnder(string path) => await TestHost.CallAsync(client, "authors",
            new Dictionary<string, object?> { ["path"] = path });

        string live = await AuthorsUnder("one/src/Hot.cs");
        string historical = await AuthorsUnder("one/src/Gone.cs");
        string quiet = await AuthorsUnder("one/docs");
        string nowhere = await AuthorsUnder("one/nowhere");

        Assert.Contains("under 'one/src/Hot.cs', most commits first", live, StringComparison.Ordinal);
        Assert.DoesNotContain("no longer at HEAD", live, StringComparison.Ordinal);

        Assert.Contains("under 'one/src/Gone.cs', most commits first", historical, StringComparison.Ordinal);
        Assert.Contains("no longer at HEAD", historical, StringComparison.Ordinal);
        Assert.DoesNotContain("No commits are recorded", historical, StringComparison.Ordinal);
        Assert.DoesNotContain("names nothing in this index", historical, StringComparison.Ordinal);

        Assert.Contains("No commits are recorded under 'one/docs', so no authors are", quiet,
            StringComparison.Ordinal);
        Assert.DoesNotContain("no longer at HEAD", quiet, StringComparison.Ordinal);

        Assert.Contains("names nothing in this index", nowhere, StringComparison.Ordinal);
        Assert.Contains("glob or list_tree", nowhere, StringComparison.Ordinal);
        Assert.DoesNotContain("no longer at HEAD", nowhere, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The widened check is the history tools' alone. A historical path has commits to list and
    ///     nothing to open, so the read side must go on refusing it — a read_file that offered it would
    ///     be the same false promise in the opposite direction.
    /// </summary>
    [Fact]
    public async Task The_read_side_still_refuses_a_path_only_history_records()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Churn);

        string read = await TestHost.CallAsync(client, "read_file",
            new Dictionary<string, object?> { ["paths"] = HistoryFixtures.Paths("one/src/Gone.cs") });
        Assert.DoesNotContain("gone\n", read, StringComparison.Ordinal);

        string listed = await TestHost.CallAsync(client, "list_tree",
            new Dictionary<string, object?> { ["path"] = "one/src" });
        Assert.DoesNotContain("Gone.cs", listed, StringComparison.Ordinal);

        string globbed = await TestHost.CallAsync(client, "glob",
            new Dictionary<string, object?> { ["glob"] = "**/Gone.cs" });
        Assert.DoesNotContain("one/src/Gone.cs", globbed, StringComparison.Ordinal);
    }

    /// <summary>
    ///     `repo` and `path` naming different repositories cannot both hold, and the quiet answer that
    ///     falls out of it — "no commits under 'one/src'" about a busy folder — is exactly the false
    ///     negative #118 exists to stop. It is refused, naming both.
    /// </summary>
    [Fact]
    public async Task A_repo_and_a_path_naming_different_repositories_are_refused_rather_than_answered_quietly()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Mixed);

        string reply = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["repo"] = "two", ["path"] = "one/src" });

        Assert.Contains("name different repositories", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("No commits", reply, StringComparison.Ordinal);
    }
}

/// <inheritdoc cref="HistoryFixture" />
public sealed class PathScopeFixture : HistoryFixture
{
    protected override IReadOnlyList<string> Projects => [HistoryFixtures.Churn, HistoryFixtures.Mixed];
}
