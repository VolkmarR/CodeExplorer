using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using CodeExplorer.Infrastructure;
using CodeExplorer.Search;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     <c>commit</c> and <c>commit_files</c>: one commit's own record, and the paths it touched. The
///     two are asked about together because a caller arrives at both from the same abbreviated SHA the
///     other tools print, and an answer that refused one of them for it would strand them.
/// </summary>
public sealed class CommitTests(CommitFixture fixture) : IClassFixture<CommitFixture>
{
    private readonly TestHost _host = fixture.Host;

    /// <summary>
    ///     A commit's own record, which is where the message body is: git_log lists subjects and
    ///     nothing under them, so a subject that says "BugFix 558185" is followed up here or not at all.
    /// </summary>
    [Fact]
    public async Task Commit_answers_one_commits_own_record_with_the_body_git_log_omits()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Commits);
        string sha = await ShaOfAsync(HistoryFixtures.Commits, "BugFix 558185");

        string reply = await TestHost.CallAsync(client, "commit", new Dictionary<string, object?> { ["sha"] = sha });

        // The full SHA, so a caller that arrived with eight characters leaves with something it can
        // quote back at any other surface.
        Assert.Contains(sha, reply, StringComparison.Ordinal);
        Assert.Contains("BugFix 558185 - tighten the check", reply, StringComparison.Ordinal);
        Assert.Contains("The validator accepted an empty name.", reply, StringComparison.Ordinal);
        Assert.Contains("Grace <grace@example.invalid>", reply, StringComparison.Ordinal);
        Assert.Contains("1970-01-01", reply, StringComparison.Ordinal);
        // One file modified and one added, four lines in and none out.
        Assert.Contains("2 files changed", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A commit whose message is its subject alone has to say so. An answer that simply stops after
    ///     the subject reads as a body that was cut, and an agent that believes there is more to read
    ///     goes looking for a tool to read it with.
    /// </summary>
    [Fact]
    public async Task Commit_says_when_a_message_is_its_subject_alone()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Commits);

        string reply = await TestHost.CallAsync(client, "commit",
            new Dictionary<string, object?> { ["sha"] = await ShaOfAsync(HistoryFixtures.Commits, "Add the module") });

        Assert.Contains("no message body", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The eight characters every history reply prints are what an agent actually holds — git_log,
    ///     file_history and blame all abbreviate — so a tool that took nothing but the full forty could
    ///     not be reached from any of them.
    /// </summary>
    [Fact]
    public async Task Commit_takes_the_abbreviated_sha_the_other_history_tools_print()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Commits);
        string sha = await ShaOfAsync(HistoryFixtures.Commits, "BugFix 558185");

        string log = await TestHost.CallAsync(client, "git_log", []);
        Assert.Contains(sha[..8], log, StringComparison.Ordinal);

        string reply = await TestHost.CallAsync(client, "commit",
            new Dictionary<string, object?> { ["sha"] = sha[..8] });
        Assert.Contains(sha, reply, StringComparison.Ordinal);
        Assert.Contains("BugFix 558185 - tighten the check", reply, StringComparison.Ordinal);

        // The same prefix reaches the file list, so the two halves of one workflow take one spelling.
        string files = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = sha[..8] });
        Assert.Contains("one/src/Api.cs", files, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A prefix two commits share is refused rather than resolved to whichever the index grouped
    ///     first: a confident record of the wrong commit is the one failure an agent cannot detect.
    ///     The SHAs are rewritten in the index because a fixture cannot choose them — git does.
    /// </summary>
    [Fact]
    public async Task Commit_refuses_a_prefix_that_fits_more_than_one_commit()
    {
        await HistoryFixtures.BuildCommitProjectAsync(_host, "twins");
        await _host.ExecuteAsync("twins",
            "UPDATE commits SET sha = 'aaaa1111111111111111111111111111111111aa' WHERE subject = 'Add the module'");
        await _host.ExecuteAsync("twins",
            "UPDATE commits SET sha = 'aaaa2222222222222222222222222222222222aa' WHERE subject LIKE 'BugFix%'");
        await using var client = await _host.ConnectAsync("twins");

        string ambiguous = await TestHost.CallAsync(client, "commit",
            new Dictionary<string, object?> { ["sha"] = "aaaa" });
        Assert.Contains("more than one commit", ambiguous, StringComparison.Ordinal);
        Assert.Contains("aaaa1111", ambiguous, StringComparison.Ordinal);
        Assert.Contains("aaaa2222", ambiguous, StringComparison.Ordinal);
        // Two of them are both of them, so the refusal claims nothing further.
        Assert.DoesNotContain("possibly others", ambiguous, StringComparison.Ordinal);
        // A refusal that named no subject would read as an answer about a commit.
        Assert.DoesNotContain("files changed", ambiguous, StringComparison.Ordinal);

        // One character more is one commit, and it is answered.
        string resolved = await TestHost.CallAsync(client, "commit",
            new Dictionary<string, object?> { ["sha"] = "aaaa1" });
        Assert.Contains("Add the module", resolved, StringComparison.Ordinal);

        // The file list refuses the same prefix the same way: a caller trying one after the other
        // must not have to reconcile two shapes of one fact.
        string files = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = "aaaa" });
        Assert.Equal(ambiguous, files);
    }

    /// <summary>
    ///     A prefix so short that the commit it fits today is not the commit it fits after the next
    ///     refresh. Refused at git's own four characters, and told apart from an argument that arrived
    ///     empty: one is a caller that abbreviated too far and the other is one that sent nothing.
    /// </summary>
    [Fact]
    public async Task Commit_refuses_a_sha_too_short_to_name_a_commit()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Commits);

        string tooShort = await TestHost.CallAsync(client, "commit",
            new Dictionary<string, object?> { ["sha"] = "ab" });
        Assert.Contains("too short to name a commit", tooShort, StringComparison.Ordinal);
        Assert.Contains("at least 4 characters", tooShort, StringComparison.Ordinal);
        // The eight an agent actually holds are enough, and the refusal says where they come from.
        Assert.Contains("first eight", tooShort, StringComparison.Ordinal);

        string blank = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = "   " });
        Assert.Contains("No commit SHA was given", blank, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A commit that touched nothing — an empty one, or a merge whose every change came from its
    ///     first parent. It is not the same fact as a SHA that names no commit, and an agent told the
    ///     second about the first goes looking for a SHA that was right all along.
    /// </summary>
    [Fact]
    public async Task Commit_files_tells_a_commit_that_touched_nothing_from_a_sha_that_names_none()
    {
        await HistoryFixtures.BuildCommitProjectAsync(_host, "hollow");
        string sha = await ShaOfAsync("hollow", "Drop the dead file");
        // What an empty commit leaves behind: the commit row, and no paths under it. A fixture cannot
        // make one through libgit2's staging, so the rows are removed instead.
        await _host.ExecuteAsync("hollow",
            $"DELETE FROM commit_files WHERE commit_id IN (SELECT commit_id FROM commits WHERE sha = '{sha}')");
        await using var client = await _host.ConnectAsync("hollow");

        string reply = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = sha });

        Assert.Contains("touched no path", reply, StringComparison.Ordinal);
        Assert.Contains("merge", reply, StringComparison.Ordinal);
        // Not the sentence for a SHA nobody has: the commit is there and the caller should keep it.
        Assert.DoesNotContain("No commit '", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A SHA the index does not hold, from both tools, in one sentence: a caller that tries one
    ///     after the other learns one fact, not two differently-shaped misses.
    /// </summary>
    [Fact]
    public async Task Commit_and_commit_files_answer_an_unknown_sha_in_the_same_words()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Commits);
        var unknown = new Dictionary<string, object?> { ["sha"] = new string('0', 40) };

        string record = await TestHost.CallAsync(client, "commit", unknown);
        string files = await TestHost.CallAsync(client, "commit_files", unknown);

        Assert.Equal(record, files);
        Assert.Contains("No commit '0000000000000000000000000000000000000000'", record, StringComparison.Ordinal);
        Assert.Contains($"project '{HistoryFixtures.Commits}'", record, StringComparison.Ordinal);
    }

    /// <summary>
    ///     What a commit touched, which is the question that cost sixty calls of guessing before this
    ///     tool: every path, what the commit did to it, and how much.
    /// </summary>
    [Fact]
    public async Task Commit_files_lists_every_path_with_its_change_kind_and_line_sums()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Commits);

        string reply = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = await ShaOfAsync(HistoryFixtures.Commits, "BugFix 558185") });

        Assert.Contains("2 paths", reply, StringComparison.Ordinal);
        // A path HEAD still holds is named the way read_file and grep name it, so it can be opened.
        Assert.Contains("modified", reply, StringComparison.Ordinal);
        Assert.Contains("one/src/Api.cs", reply, StringComparison.Ordinal);
        Assert.Contains("added", reply, StringComparison.Ordinal);
        // Path order, which is what the reply says it is in.
        Assert.True(reply.IndexOf("one/src/Api.cs", StringComparison.Ordinal)
                    < reply.IndexOf("one/src/Gone.cs", StringComparison.Ordinal));
        // The boundary, said rather than left for a follow-up call that cannot be answered.
        Assert.Contains("diff", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A commit too wide for one reply (#125). The list is a page, it says how many paths the
    ///     commit really touched, and it names the offset that reaches the rest — where before it was
    ///     cut by the reply ceiling with nothing saying so, which is how an agent reads half a commit
    ///     as the whole of it.
    /// </summary>
    [Fact]
    public async Task Commit_files_pages_a_commit_wider_than_one_reply_and_says_what_it_left()
    {
        const int paths = HistoryTools.MaxPathsListed + 20;
        var wide = new Dictionary<string, string>();
        for (int i = 1; i <= paths; i++)
            wide[string.Create(CultureInfo.InvariantCulture, $"src/Bulk{i:0000}.cs")] = $"class B{i} {{ }}\n";
        _host.CreateGitRepository("sweeping-one", wide);
        await _host.CreateProjectAsync("sweeping");
        await _host.AddRepositoryAsync("sweeping", "one", _host.FixturePath("sweeping-one"));
        await _host.RefreshAsync("sweeping");

        await using var client = await _host.ConnectAsync("sweeping");
        string sha = await ShaOfAsync("sweeping", "fixture");

        string first = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = sha });

        // The commit's own size, not the page's: a header counting the page would be the truncation
        // the note beneath it is denying.
        Assert.Contains($"{paths} paths changed by", first, StringComparison.Ordinal);
        Assert.Contains($"offset={HistoryTools.MaxPathsListed}", first, StringComparison.Ordinal);
        Assert.Contains("src/Bulk0001.cs", first, StringComparison.Ordinal);
        Assert.DoesNotContain($"src/Bulk{paths:0000}.cs", first, StringComparison.Ordinal);

        string second = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = sha, ["offset"] = HistoryTools.MaxPathsListed });

        Assert.Contains($"src/Bulk{paths:0000}.cs", second, StringComparison.Ordinal);
        Assert.DoesNotContain("src/Bulk0001.cs", second, StringComparison.Ordinal);
        // The last page is the end of the list and says nothing about a next one.
        Assert.DoesNotContain("for the next", second, StringComparison.Ordinal);

        // Past the end is the end of the paging, never a commit that touched nothing.
        string past = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = sha, ["offset"] = paths + 50 });
        Assert.Contains("no path past the first", past, StringComparison.Ordinal);
        Assert.DoesNotContain("touched no path", past, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Paths long enough that three hundred of them would overrun the reply ceiling. The count is
    ///     not the binding limit there — the characters are — and a page cut by the ceiling instead of
    ///     by this code would lose rows no offset can reach, which is the truncation #125 is about
    ///     arrived at from the inside.
    /// </summary>
    [Fact]
    public async Task Commit_files_ends_a_page_of_long_paths_before_the_reply_ceiling_cuts_it()
    {
        const int paths = 260;
        // Long names rather than deep directories: git on Windows refuses the second well before the
        // reply ceiling is reached. Each row is then about 170 characters, so the budget binds first.
        string padding = new('x', 130);
        var wide = new Dictionary<string, string>();
        for (int i = 1; i <= paths; i++)
            wide[string.Create(CultureInfo.InvariantCulture, $"Bulk{i:0000}-{padding}.cs")] = $"class B{i} {{ }}\n";
        _host.CreateGitRepository("longpaths-one", wide);
        await _host.CreateProjectAsync("longpaths");
        await _host.AddRepositoryAsync("longpaths", "one", _host.FixturePath("longpaths-one"));
        await _host.RefreshAsync("longpaths");

        await using var client = await _host.ConnectAsync("longpaths");
        string reply = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = await ShaOfAsync("longpaths", "fixture") });

        // The reply cap never bit, so nothing was cut by something that cannot say what it cut.
        Assert.DoesNotContain("capped at", reply, StringComparison.Ordinal);
        Assert.True(reply.Length <= ToolReply.MaxOutputChars);
        // Fewer than the count ceiling were listed, and the offset named is the one actually reached.
        Assert.Contains("further paths not shown", reply, StringComparison.Ordinal);
        int listed = reply.Split('\n').Count(line => line.Contains("Bulk", StringComparison.Ordinal));
        Assert.True(listed < HistoryTools.MaxPathsListed, $"listed {listed}");
        Assert.Contains($"offset={listed}", reply, StringComparison.Ordinal);
        Assert.Contains($"listing {listed} of them", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A commit that fits says nothing about paging, because a note on every reply is one an agent
    ///     learns to skip.
    /// </summary>
    [Fact]
    public async Task Commit_files_says_nothing_about_paging_when_the_whole_commit_fits()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Commits);

        string reply = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = await ShaOfAsync(HistoryFixtures.Commits, "BugFix 558185") });

        Assert.DoesNotContain("offset=", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("NOTE", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A path the commit deleted is named and marked, never dropped and never offered as something
    ///     to open: an agent sent to read a file that is not there has been told something false, and
    ///     the mark is the one every ranking drawn from history already uses.
    /// </summary>
    [Fact]
    public async Task Commit_files_marks_a_path_that_is_no_longer_at_head()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Commits);

        string reply = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = await ShaOfAsync(HistoryFixtures.Commits, "Drop the dead file") });

        Assert.Contains("deleted", reply, StringComparison.Ordinal);
        Assert.Contains("one/src/Gone.cs  (no longer at HEAD)", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The other way a path stops being openable: the file is still there, under another name. The
    ///     commit's own path is what it recorded, and it is marked for the same reason a deleted one is
    ///     — an agent sent to read it finds nothing, whatever became of the content.
    /// </summary>
    [Fact]
    public async Task Commit_files_marks_a_path_a_later_commit_renamed_away()
    {
        await HistoryFixtures.BuildCommitProjectAsync(_host, "renamed");
        // A rename as git records one: the content at the new path, and the old path gone.
        _host.CommitToGitRepositoryAs("renamed-one",
            new Dictionary<string, string> { ["docs/Readme.md"] = "note\n" },
            "Rename the note", "Ada", "ada@example.invalid", 3);
        _host.RemoveInGitRepositoryAs("renamed-one", ["docs/Note.md"], "Rename the note, second half", "Ada",
            "ada@example.invalid", 4);
        await _host.RefreshAsync("renamed");

        await using var client = await _host.ConnectAsync("renamed");
        string reply = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = await ShaOfAsync("renamed", "Add the module") });

        Assert.Contains("one/docs/Note.md  (no longer at HEAD)", reply, StringComparison.Ordinal);
        // The path it kept is offered as itself, so one reply carries both kinds without a caveat
        // covering the wrong row.
        Assert.Contains("one/src/Api.cs\n", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("one/src/Api.cs  (no longer", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A project of one repository names its files without a slug (ADR-0006), and a file list that
    ///     spelled one in would hand back paths read_file refuses.
    /// </summary>
    [Fact]
    public async Task Commit_files_names_paths_the_way_a_single_repository_project_does()
    {
        await HistoryFixtures.OnlyRepositoryProjectAsync(_host, "onlyone",
            new Dictionary<string, string> { ["src/Widget.cs"] = "class Widget { }\n" }, true);
        await using var client = await _host.ConnectAsync("onlyone");

        using var http = _host.CreateClient();
        var page = await http.GetFromJsonAsync<ChangeLogAnswer>("/api/projects/onlyone/commits",
            TestContext.Current.CancellationToken);
        Assert.NotNull(page);

        string reply = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = page.Commits[0].Sha });

        Assert.Contains("src/Widget.cs", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("only/src/Widget.cs", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     One clone added twice holds every commit twice under one SHA. The commit's own page names the
    ///     copy recorded first, which is the last the newest-first log lists, and counts it the way that
    ///     row did — whichever copy DuckDB happened to reach first would make a link open a different
    ///     commit from one refresh to the next.
    /// </summary>
    [Fact]
    public async Task One_commit_held_by_two_repositories_answers_the_copy_recorded_first()
    {
        string source = _host.CreateEmptyGitRepository("twinned-one");
        _host.CommitToGitRepositoryAs("twinned-one",
            new Dictionary<string, string> { ["src/Api.cs"] = "a\nb\n", ["docs/Note.md"] = "note\n" },
            "Add the module", "Ada", "ada@example.invalid", 0);
        _host.CommitToGitRepositoryAs("twinned-one",
            new Dictionary<string, string> { ["src/Api.cs"] = "a\nc\n" },
            "Change the module", "Grace", "grace@example.invalid", 1);
        await _host.CreateProjectAsync("twinned");
        await _host.AddRepositoryAsync("twinned", "left", source);
        await _host.AddRepositoryAsync("twinned", "right", source);
        await _host.RefreshAsync("twinned");

        using var http = _host.CreateClient();
        var log = await http.GetFromJsonAsync<ChangeLogAnswer>("/api/projects/twinned/commits",
            TestContext.Current.CancellationToken);
        Assert.NotNull(log);
        Assert.Equal(4, log.Total);
        string sha = log.Commits.Single(c => c.RepositorySlug == "left" && c.Subject == "Add the module").Sha;
        var copies = log.Commits.Where(c => c.Sha == sha).ToList();
        Assert.Equal(["left", "right"], copies.Select(c => c.RepositorySlug).Order());

        var one = await http.GetFromJsonAsync<LoggedCommit>($"/api/projects/twinned/commits/{sha}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(one);
        Assert.Equal(copies[^1], one);
        Assert.Equal(2, one.FilesChanged);
        Assert.Equal(3, one.Added);
    }

    /// <summary>
    ///     A page of the change log sums what its own commits did and nothing else (#171). Summed over
    ///     every commit in scope and cut afterwards, a page of fifty aggregated the whole of
    ///     <c>commit_files</c>, because a limit cannot be pushed beneath the aggregate it follows.
    ///     Asserted through the query-plan switch, as the rename-chain test in <c>PathLineageTests</c>
    ///     is, and on the profile it writes: that is what a developer looking at a slow page turns on. The newest commit touches one file
    ///     and the one before it three, so an aggregate over more than the page reads more than one row.
    /// </summary>
    [Fact]
    public async Task A_page_of_the_change_log_sums_only_the_commits_on_it()
    {
        string source = _host.CreateEmptyGitRepository("paged-one");
        _host.CommitToGitRepositoryAs("paged-one",
            new Dictionary<string, string> { ["a.txt"] = "a\n", ["b.txt"] = "b\n", ["c.txt"] = "c\n" },
            "Add three", "Ada", "ada@example.invalid", 0);
        _host.CommitToGitRepositoryAs("paged-one", new Dictionary<string, string> { ["a.txt"] = "a2\n" },
            "Change one", "Ada", "ada@example.invalid", 1);
        await _host.CreateProjectAsync("paged");
        await _host.AddRepositoryAsync("paged", "paged", source);
        await _host.RefreshAsync("paged");

        string plans = _host.ScratchFile("change-log-plans");
        using var http = _host.CreateClient();
        ChangeLogAnswer? page;
        using (_host.RecordPlans(plans))
            page = await http.GetFromJsonAsync<ChangeLogAnswer>("/api/projects/paged/commits?repository=paged&pageSize=1",
                TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        var commit = Assert.Single(page.Commits);
        Assert.Equal("Change one", commit.Subject);
        Assert.Equal((1, 1, 1), (commit.FilesChanged, commit.Added, commit.Deleted));

        string dump = Directory.EnumerateFiles(plans, "*CommitLogQueries-LoggedAsync.sql.txt")
            .Single(file => File.ReadAllText(file).Contains("$r = paged", StringComparison.Ordinal));
        using var profile = JsonDocument.Parse(File.ReadAllText(
            dump.Replace(".sql.txt", ".json", StringComparison.Ordinal)));
        var aggregate = Assert.Single(Operators(profile.RootElement),
            op => op.GetProperty("operator_type").GetString()!.EndsWith("GROUP_BY", StringComparison.Ordinal));
        Assert.Equal(1, Assert.Single(aggregate.GetProperty("children").EnumerateArray())
            .GetProperty("operator_cardinality").GetInt64());
    }

    /// <summary>Every operator of a DuckDB JSON profile, depth first.</summary>
    private static IEnumerable<JsonElement> Operators(JsonElement node)
    {
        if (node.TryGetProperty("operator_type", out _)) yield return node;
        if (!node.TryGetProperty("children", out var children)) yield break;
        foreach (var child in children.EnumerateArray())
        foreach (var op in Operators(child))
            yield return op;
    }

    /// <summary>
    ///     The full SHA of the commit whose subject starts with this text, read from the API because
    ///     no tool prints one: the abbreviation is the point of the tests above, not an accident here.
    /// </summary>
    private async Task<string> ShaOfAsync(string project, string subject)
    {
        using var http = _host.CreateClient();
        var page = await http.GetFromJsonAsync<ChangeLogAnswer>($"/api/projects/{project}/commits",
            TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page.Commits.Single(c => c.Subject.StartsWith(subject, StringComparison.Ordinal)).Sha;
    }
}

/// <inheritdoc cref="HistoryFixture" />
public sealed class CommitFixture : HistoryFixture
{
    protected override IReadOnlyList<string> Projects => [HistoryFixtures.Commits];
}
