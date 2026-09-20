using System.Net.Http.Json;
using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     <c>hot_files</c>: the churn ranking, its rollup to directories, and the two things a ranking
///     must never leave unsaid — what its window reached, and which of the paths it names are still at
///     HEAD.
/// </summary>
public sealed class HotFilesTests(HotFilesFixture fixture) : IClassFixture<HotFilesFixture>
{
    private readonly TestHost _host = fixture.Host;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    ///     The ranking itself: most commits first, with the lines each file gained and lost. Written
    ///     against a fixture whose busiest file is not its largest, so a ranking that fell back to size
    ///     or to path order would fail rather than happen to agree.
    /// </summary>
    [Fact]
    public async Task Hot_files_ranks_the_files_the_window_changed_most()
    {
        await using var client = await ChurnAsync();
        string reply = await TestHost.CallAsync(client, "hot_files", []);

        Assert.Contains("most-changed", reply, StringComparison.Ordinal);
        // Three commits touched Hot.cs, two Cold.cs, one Note.md, and the order must be that.
        Assert.True(reply.IndexOf("one/src/Hot.cs", StringComparison.Ordinal)
                    < reply.IndexOf("one/src/Cold.cs", StringComparison.Ordinal));
        Assert.True(reply.IndexOf("one/src/Cold.cs", StringComparison.Ordinal)
                    < reply.IndexOf("one/docs/Note.md", StringComparison.Ordinal));
        Assert.Contains("3 commits", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The window is measured back from the newest recorded commit and not from today, which is the
    ///     decision the next two tickets inherit: an index built a month ago must still answer "the last
    ///     week" with the last week it holds, rather than with nothing.
    /// </summary>
    [Fact]
    public async Task Hot_files_measures_the_window_back_from_the_newest_recorded_commit()
    {
        await using var client = await ChurnAsync();

        // The fixture's newest commits are ten days after its first. A day-wide window reaches the
        // recent ones and not the old one, although every one of them is decades before today.
        string recent = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 1 });
        Assert.Contains("one/src/Hot.cs", recent, StringComparison.Ordinal);
        Assert.DoesNotContain("one/old/Ancient.cs", recent, StringComparison.Ordinal);

        string wide = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30 });
        Assert.Contains("one/old/Ancient.cs", wide, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hot_files_scopes_to_a_directory_and_to_a_repository()
    {
        await using var client = await ChurnAsync();

        string scoped = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["directory"] = "one/src" });
        Assert.Contains("one/src/Hot.cs", scoped, StringComparison.Ordinal);
        Assert.DoesNotContain("Note.md", scoped, StringComparison.Ordinal);

        // A bare slug is the whole repository, which is the same answer with the docs back in it.
        string repository = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["directory"] = "one" });
        Assert.Contains("repository 'one'", repository, StringComparison.Ordinal);
        Assert.Contains("one/docs/Note.md", repository, StringComparison.Ordinal);

        string unknown = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["directory"] = "nowhere/src" });
        Assert.Contains("No repository 'nowhere'", unknown, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The rollup ranks directories, and a directory's count is the commits that touched anything
    ///     beneath it — each once. The fixture's <c>src</c> holds three files whose own counts sum to
    ///     seven across four commits, so a rollup that added its files up would say seven and fail here
    ///     rather than happen to agree.
    /// </summary>
    [Fact]
    public async Task Hot_files_rolls_up_to_directories_counting_each_commit_once()
    {
        await using var client = await ChurnAsync();
        string reply = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30, ["depth"] = 2 });

        Assert.Contains("most-changed directories", reply, StringComparison.Ordinal);
        // The window covers every commit of the fixture, so each directory's own total is known:
        // src was touched by four of the five, docs and old by one each.
        Assert.Contains("   4 commits  ", reply, StringComparison.Ordinal);
        Assert.Contains("one/src\n", reply, StringComparison.Ordinal);
        Assert.Contains("one/docs\n", reply, StringComparison.Ordinal);
        Assert.Contains("one/old\n", reply, StringComparison.Ordinal);
        // A rollup is a ranking of directories and nothing else: the files beneath them are what the
        // caller calls again without `depth` to see.
        Assert.DoesNotContain("Hot.cs", reply, StringComparison.Ordinal);
        Assert.True(reply.IndexOf("one/src\n", StringComparison.Ordinal)
                    < reply.IndexOf("one/docs\n", StringComparison.Ordinal));
        // The dates are said for the rollup exactly as they are for the file ranking: the window ends
        // at the newest recorded commit, and a stale index has to show as one either way.
        Assert.Contains("the 30 days to the newest recorded commit", reply, StringComparison.Ordinal);
        Assert.Contains("distinct commits", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Depth counts segments beneath whatever scope the call already had, so `directory` and the
    ///     rollup compose: one repository, rolled up to its own top level. Depth counted from the
    ///     project root instead would answer a scoped call with the scope itself, one row of no use.
    /// </summary>
    [Fact]
    public async Task Hot_files_counts_depth_beneath_the_directory_it_was_scoped_to()
    {
        await using var client = await ChurnAsync();
        string reply = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30, ["directory"] = "one", ["depth"] = 1 });

        Assert.Contains("repository 'one'", reply, StringComparison.Ordinal);
        Assert.Contains("one/src\n", reply, StringComparison.Ordinal);
        Assert.Contains("one/docs\n", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("Note.md", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The first segment of a qualified path in a multi-repository project is the repository, so
    ///     depth 1 there ranks repositories. It is the same rule and not a special case, and it is the
    ///     answer to "which of these repositories is moving" in one call.
    /// </summary>
    [Fact]
    public async Task Hot_files_rolled_up_to_the_first_segment_ranks_repositories()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Mixed);

        string reply = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30, ["depth"] = 1 });

        Assert.Contains("   5 commits  ", reply, StringComparison.Ordinal);
        Assert.Contains("one\n", reply, StringComparison.Ordinal);
        Assert.Contains("two\n", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("one/src", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A directory the window changed and HEAD no longer holds is ranked and marked, for the reason
    ///     a file is: a module that was renamed away is exactly the churn somebody is looking for, and
    ///     an unmarked row sends an agent to list a directory that is not there.
    /// </summary>
    [Fact]
    public async Task Hot_files_marks_a_rolled_up_directory_that_is_no_longer_at_head()
    {
        string source = _host.CreateEmptyGitRepository("retired-one");
        _host.CommitToGitRepositoryAs("retired-one",
            new Dictionary<string, string> { ["legacy/Old.cs"] = "old\n", ["src/New.cs"] = "new\n" },
            "Add both", "Ada", "ada@example.invalid", 0);
        _host.RemoveInGitRepositoryAs("retired-one", ["legacy/Old.cs"], "Retire the legacy module", "Grace",
            "grace@example.invalid", 1);

        await _host.CreateProjectAsync("retired");
        await _host.AddRepositoryAsync("retired", "one", source);
        await _host.RefreshAsync("retired");
        await using var client = await _host.ConnectAsync("retired");

        string reply = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["depth"] = 2 });

        Assert.Contains("one/legacy  (no longer at HEAD)", reply, StringComparison.Ordinal);
        Assert.Contains("one/src\n", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A directory name is data and not a pattern. `[xy]` is a route directory in half the web
    ///     frameworks there are, and it is a character class to GLOB — so a glob built from it would
    ///     match the sibling `x` and report a directory that was deleted as still being at HEAD. That
    ///     is the mark saying the opposite of what happened, which is worse than no mark at all.
    /// </summary>
    [Fact]
    public async Task Hot_files_reads_a_rolled_up_directory_name_as_a_name_and_not_as_a_glob()
    {
        string source = _host.CreateEmptyGitRepository("routes-one");
        _host.CommitToGitRepositoryAs("routes-one",
            new Dictionary<string, string> { ["pages/[xy]/a.ts"] = "a\n", ["pages/x/b.ts"] = "b\n" },
            "Add the routes", "Ada", "ada@example.invalid", 0);
        _host.RemoveInGitRepositoryAs("routes-one", ["pages/[xy]/a.ts"], "Drop the dynamic route", "Grace",
            "grace@example.invalid", 1);

        await _host.CreateProjectAsync("routes");
        await _host.AddRepositoryAsync("routes", "one", source);
        await _host.RefreshAsync("routes");
        await using var client = await _host.ConnectAsync("routes");

        string reply = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["depth"] = 3 });

        Assert.Contains("one/pages/[xy]  (no longer at HEAD)", reply, StringComparison.Ordinal);
        // The sibling the character class would have matched is still there, and unmarked.
        Assert.Contains("one/pages/x\n", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A path the window changed and HEAD no longer holds is ranked and marked. Leaving it out
    ///     would understate the churn of the area it was in; leaving it unmarked would send an agent to
    ///     read a file that is not there.
    /// </summary>
    [Fact]
    public async Task Hot_files_marks_a_path_that_is_no_longer_at_head()
    {
        await using var client = await ChurnAsync();
        string reply = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30 });

        Assert.Contains("one/src/Gone.cs  (no longer at HEAD)", reply, StringComparison.Ordinal);
        Assert.Contains("one/src/Hot.cs\n", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The overview's ranking and <c>hot_files</c>' are the same rows, mark and all. An agent reads
    ///     the overview to orient itself and then calls the tool for more, so a row that changed shape
    ///     between the two reads as a different fact — and the mark is the half that matters: one
    ///     surface forgetting it sends the agent to open a file that is not there.
    /// </summary>
    [Fact]
    public async Task The_overview_ranks_in_the_rows_hot_files_prints_down_to_the_mark_on_a_deleted_path()
    {
        await using var client = await ChurnAsync();

        string overview = await TestHost.CallAsync(client, "project_overview", []);
        string hot = await TestHost.CallAsync(client, "hot_files", []);

        // The overview indents its sections; the rows are otherwise drawn by the same helper, which is
        // what this compares. Taken from the overview because it is the shorter of the two rankings.
        var rows = overview[overview.IndexOf("Most changed", StringComparison.Ordinal)..]
            .Split('\n')
            .Skip(1)
            .TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal))
            .Select(line => line[2..])
            .ToList();

        Assert.Contains(rows, row => row.EndsWith("one/src/Gone.cs  (no longer at HEAD)", StringComparison.Ordinal));
        foreach (string row in rows) Assert.Contains(row + "\n", hot, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The tool and the page read one history. They page it differently — the page counts what it
    ///     has not shown, the tool does not — but a caller comparing the two is comparing one scope, and
    ///     a scope that meant different things on the two surfaces is the drift this pins.
    ///     The two spell the scope differently on purpose: the tool surface settled on <c>repo</c>, the
    ///     HTTP API keeps <c>repository</c> for the callers it already has, and that is a difference in
    ///     the query string and not in the scope.
    /// </summary>
    [Fact]
    public async Task Git_log_and_the_change_log_page_agree_about_a_repository()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Mixed);

        using var http = _host.CreateClient();
        var page = await http.GetFromJsonAsync<ChangeLogAnswer>(
            $"/api/projects/{HistoryFixtures.Mixed}/commits?repository=one", TestContext.Current.CancellationToken);
        Assert.NotNull(page);

        string reply = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["repo"] = "one" });

        // Five commits in the fixture's first repository and one in its second: a tool that dropped the
        // scope, or a page that kept it, would disagree here rather than both saying five.
        Assert.Equal(5, page.Total);
        Assert.Contains($"{page.Total} commits in repository 'one'", reply, StringComparison.Ordinal);
        // Both are newest first, so the same commit heads each.
        Assert.Contains(page.Commits[0].Sha[..8], reply, StringComparison.Ordinal);
        Assert.DoesNotContain("src/Other.cs", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Two repositories, one of which was never walked. The ranking cannot show what it has not got,
    ///     so it has to say which repositories it is speaking for — otherwise it reads as the project's
    ///     whole story (CONTEXT.md, History).
    /// </summary>
    [Fact]
    public async Task Hot_files_says_which_repositories_have_history_and_which_have_none()
    {
        await HistoryFixtures.BuildChurnProjectAsync(_host, "gamma", withSecondRepository: true);
        // What a repository whose walk found nothing leaves behind: files in the index, no commits.
        await _host.ExecuteAsync("gamma", "DELETE FROM commits WHERE repo_slug = 'two'");

        await using var client = await _host.ConnectAsync("gamma");
        string reply = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30 });

        Assert.Contains("History was imported for one and for none of two", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("two/src/Other.cs", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A single-repository project names its files without a slug (ADR-0006), and a ranking that
    ///     spelled one in would hand back paths every other tool refuses.
    /// </summary>
    [Fact]
    public async Task Hot_files_names_paths_the_way_a_single_repository_project_does()
    {
        await HistoryFixtures.OnlyRepositoryProjectAsync(_host, "solo",
            new Dictionary<string, string> { ["src/Widget.cs"] = "class Widget { }\n" }, true);

        await using var client = await _host.ConnectAsync("solo");
        string reply = await TestHost.CallAsync(client, "hot_files", []);

        Assert.Contains(" src/Widget.cs\n", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("only/src/Widget.cs", reply, StringComparison.Ordinal);

        // And it takes a directory in the same spelling, with no slug to strip off first.
        string scoped = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["directory"] = "src" });
        Assert.Contains(" src/Widget.cs\n", scoped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hot_files_on_a_project_without_history_says_so_rather_than_ranking_nothing()
    {
        await HistoryFixtures.OnlyRepositoryProjectAsync(_host, "delta",
            new Dictionary<string, string> { ["a.cs"] = "class A;\n" });
        await _host.ExecuteAsync("delta", "DELETE FROM commits");

        await using var client = await _host.ConnectAsync("delta");
        string reply = await TestHost.CallAsync(client, "hot_files", []);

        Assert.Contains("holds no history", reply, StringComparison.Ordinal);
        Assert.Contains("Ask the operator to refresh", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A ranking counts machine-authored commits exactly like hand-written ones, so a directory of
    ///     regenerated output outranks the code that drives it (#117). No suffix list is hard-coded
    ///     anywhere, so the filter is the caller's and the reply has to say it ran.
    /// </summary>
    [Fact]
    public async Task Hot_files_takes_an_exclude_filter_and_says_how_much_it_hid()
    {
        await HistoryFixtures.BuildGeneratedProjectAsync(_host, "generated");
        await using var client = await _host.ConnectAsync("generated");

        string unfiltered = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30 });
        // The distortion, before anything is done about it: the generated file wins.
        Assert.True(unfiltered.IndexOf("one/src/Model.g.cs", StringComparison.Ordinal)
                    < unfiltered.IndexOf("one/src/Service.cs", StringComparison.Ordinal));
        Assert.DoesNotContain("The filter hid", unfiltered, StringComparison.Ordinal);

        string filtered = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30, ["exclude"] = "*.g.cs,/migrations/" });
        Assert.DoesNotContain("Model.g.cs", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("migrations", filtered, StringComparison.Ordinal);
        Assert.Contains("one/src/Service.cs", filtered, StringComparison.Ordinal);
        // An excluded ranking must never read as an unfiltered one.
        Assert.Contains("The filter hid 2 paths, so this is a filtered ranking", filtered,
            StringComparison.Ordinal);

        // The rollup reads the same scope, so it hides the same files rather than answering differently.
        string rolled = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30, ["depth"] = 2, ["exclude"] = "/migrations/" });
        Assert.DoesNotContain("one/migrations", rolled, StringComparison.Ordinal);
        Assert.Contains("The filter hid 1 path", rolled, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The other polarity of the same filter (#161): naming the extensions that are the code,
    ///     which on a real project is shorter than listing everything that is not. Asked of both
    ///     grains, because the rollup ranks the files it kept and a rollup filtered differently from
    ///     the files under it is a directory whose churn nothing on screen adds up to.
    /// </summary>
    [Fact]
    public async Task Hot_files_ranks_only_the_extensions_it_is_given()
    {
        await HistoryFixtures.BuildGeneratedProjectAsync(_host, "byext");
        await using var client = await _host.ConnectAsync("byext");

        string sharp = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30, ["extensions"] = ".cs" });
        Assert.Contains("one/src/Service.cs", sharp, StringComparison.Ordinal);
        Assert.Contains("one/src/Model.g.cs", sharp, StringComparison.Ordinal);
        Assert.DoesNotContain("0001.sql", sharp, StringComparison.Ordinal);
        Assert.Contains("The filter hid 1 path", sharp, StringComparison.Ordinal);

        // Written without the dot, which is how a person types it, and with it, which is how the
        // reply spells it. One term, two spellings, one ranking.
        string bare = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30, ["extensions"] = "cs" });
        Assert.DoesNotContain("0001.sql", bare, StringComparison.Ordinal);

        // Anchored at the end of the path, which is what keeps an extension from behaving like the
        // substring it looks like: `.sql` selects the migration and nothing that merely contains it.
        string sql = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30, ["extensions"] = ".sql" });
        Assert.Contains("one/migrations/0001.sql", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Service.cs", sql, StringComparison.Ordinal);
        Assert.Contains("The filter hid 2 paths", sql, StringComparison.Ordinal);

        // Both filters at once, and the count is paths hidden by EITHER rather than the sum: the
        // generated file is dropped twice over and must still be one path.
        string both = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?>
            {
                ["days"] = 30, ["extensions"] = ".cs", ["exclude"] = "*.g.cs"
            });
        Assert.Contains("one/src/Service.cs", both, StringComparison.Ordinal);
        Assert.DoesNotContain("Model.g.cs", both, StringComparison.Ordinal);
        Assert.Contains("The filter hid 2 paths", both, StringComparison.Ordinal);

        // The rollup reads the same filters, so it rolls up the files the caller can still see.
        string rolled = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30, ["depth"] = 2, ["extensions"] = ".sql" });
        Assert.Contains("one/migrations", rolled, StringComparison.Ordinal);
        Assert.DoesNotContain("one/src", rolled, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The menu the filter is chosen from (#161). An agent that has just read a ranking of
    ///     project files needs the spellings this scope actually holds, not the ones it would guess —
    ///     a project written in `.prg` is exactly where guessing `.cs` answers with an empty ranking
    ///     that reads as a quiet repository.
    /// </summary>
    [Fact]
    public async Task Hot_files_names_the_extensions_the_window_holds()
    {
        await HistoryFixtures.BuildGeneratedProjectAsync(_host, "menu");
        await using var client = await _host.ConnectAsync("menu");

        string reply = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30 });

        Assert.Contains("Extensions changed in this window", reply, StringComparison.Ordinal);
        // Commits then paths: two generated files accounting for more commits than the code is the
        // shape the line exists to make visible.
        Assert.Contains(".cs 5c/2f", reply, StringComparison.Ordinal);
        Assert.Contains(".sql 4c/1f", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A directory scope is a name and not a pattern (#122). `[slug]`, `[id]` and `[...rest]` are
    ///     route directories in every file-system router, and each is a character class to GLOB — so
    ///     the scope used to answer with whatever the sibling `app/s` held and call it the churn of
    ///     `app/[slug]`. The file ranking and the rollup read one scope, so both are asked.
    /// </summary>
    [Fact]
    public async Task Hot_files_reads_a_bracketed_directory_scope_as_a_name()
    {
        await HistoryFixtures.BuildRoutedProjectAsync(_host, "routed");
        await using var client = await _host.ConnectAsync("routed");

        string scoped = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30, ["directory"] = "one/app/[slug]" });
        Assert.Contains("one/app/[slug]/page.tsx", scoped, StringComparison.Ordinal);
        // The character class `[slug]` matches `s`, `l`, `u` and `g`, so this is the sibling it took in.
        Assert.DoesNotContain("one/app/s/", scoped, StringComparison.Ordinal);

        string rolled = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?>
                { ["days"] = 30, ["directory"] = "one/app/[slug]", ["depth"] = 1 });
        Assert.DoesNotContain("one/app/s", rolled, StringComparison.Ordinal);
    }

    /// <summary>
    ///     hot_files returns a number, and a wrong number is indistinguishable from a right one: a
    ///     directory that was renamed mid-history ranks its post-rename slice alone. git_log, authors
    ///     and file_history all warn about that in their own descriptions; this is the tool where it
    ///     matters most, and it did not (#135). Asserted against the sibling wording rather than for a
    ///     sentence of its own, because a fourth phrasing of one fact is how an agent ends up believing
    ///     the tools differ.
    /// </summary>
    [Fact]
    public async Task Hot_files_says_its_directory_scope_is_matched_by_the_recorded_path()
    {
        await using var client = await ChurnAsync();
        var listed = (await client.ListToolsAsync(cancellationToken: Ct))
            .ToDictionary(tool => tool.Name, tool => tool.Description ?? "", StringComparer.Ordinal);

        string churn = listed["hot_files"];
        Assert.Contains("begins where a directory was last renamed or moved", churn, StringComparison.Ordinal);
        Assert.Contains("reads as a quiet one unless you know that", churn, StringComparison.Ordinal);
        // The siblings' clause, word for word, so the four descriptions cannot drift into four facts.
        Assert.Contains("Scoping is by the path each commit recorded", churn, StringComparison.Ordinal);
        Assert.Contains("Scoping is by the path each commit recorded", listed["authors"], StringComparison.Ordinal);
        // The row-level note is about what the ranking returns, not about the scope it was given, and
        // stays its own bullet.
        Assert.Contains("Files a later commit deleted or renamed away are ranked too and marked", churn,
            StringComparison.Ordinal);
    }

    private Task<McpClient> ChurnAsync() => _host.ConnectAsync(HistoryFixtures.Churn);
}

/// <inheritdoc cref="HistoryFixture" />
public sealed class HotFilesFixture : HistoryFixture
{
    protected override IReadOnlyList<string> Projects => [HistoryFixtures.Churn, HistoryFixtures.Mixed];
}
