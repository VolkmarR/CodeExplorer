using CodeExplorer.Reading;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     What a path scope was called before (#131, #143): the previous-path note every path-scoped
///     tool carries, the chain behind it, and the one read that pairs over the chain instead of only
///     naming it.
///     The chain is the build's work since #148, so these also stand in for that table: every sentence
///     below is rendered from <c>path_lineage</c>, and a chain the build walked wrongly shows up here
///     as the wrong previous path rather than as a schema test nobody reads.
/// </summary>
public sealed class PathLineageTests(PathLineageFixture fixture) : IClassFixture<PathLineageFixture>
{
    private readonly TestHost _host = fixture.Host;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    ///     The cutover as a real one arrives: one commit of many `renamed` rows, content unchanged
    ///     (#131). Every path-scoped tool scoped to the new name must say what the old name was, how
    ///     many commits the whole chain accounts for, and how to read the old name — and must go on
    ///     counting only what its own path recorded, because renames are signalled and not followed.
    /// </summary>
    [Fact]
    public async Task A_directory_renamed_in_one_commit_is_signalled_by_every_path_scoped_tool()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Renames);

        string ranked = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 3650, ["directory"] = "one/src/Model" });
        Assert.Contains("its content was at 'one/model' before", ranked, StringComparison.Ordinal);
        Assert.Contains("recorded across the whole chain", ranked, StringComparison.Ordinal);
        Assert.Contains("Call hot_files with directory=\"one/model\" to rank it.", ranked, StringComparison.Ordinal);
        // Signalled, not followed: the ranking itself is still the post-rename slice.
        Assert.Contains("renames are signalled here, not followed", ranked, StringComparison.Ordinal);

        string log = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["path"] = "one/src/Model" });
        Assert.Contains("its content was at 'one/model' before", log, StringComparison.Ordinal);
        Assert.Contains("Call git_log with path=\"one/model\" to read it.", log, StringComparison.Ordinal);
        // The scope's own count is literal: only the cutover commit touched src/Model.
        Assert.DoesNotContain("Add the contact feature", log, StringComparison.Ordinal);

        string owners = await TestHost.CallAsync(client, "authors",
            new Dictionary<string, object?> { ["path"] = "one/src/Model" });
        Assert.Contains("Call authors with path=\"one/model\" to read it.", owners, StringComparison.Ordinal);

        string listed = await TestHost.CallAsync(client, "file_history",
            new Dictionary<string, object?> { ["path"] = "one/src/Model/Contact.cs" });
        Assert.Contains("its content was at 'one/model/Contact.cs' before", listed, StringComparison.Ordinal);
        Assert.Contains("Call file_history with path=\"one/model/Contact.cs\" to read it.", listed,
            StringComparison.Ordinal);

        // co_changed carries the signal and does not redirect: since #143 its pairing spans the chain,
        // so the ranking above the note already covers the earlier path.
        string coupled = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Model/Contact.cs", ["days"] = 3650 });
        Assert.Contains("its content was at 'one/model/Contact.cs' before", coupled, StringComparison.Ordinal);
        Assert.Contains("The ranking above spans that chain", coupled, StringComparison.Ordinal);
        Assert.DoesNotContain("Call git_log or file_history", coupled, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The empty answers are the ones the signal exists for. A directory renamed longer ago than the
    ///     window ranks nothing and says "raise days"; a file whose commits all predate the window lists
    ///     none and says its history may have "reached this path by a rename" — and both used to drop
    ///     the rename they were describing, along with the count and the call.
    /// </summary>
    [Fact]
    public async Task An_empty_answer_still_says_what_the_scope_was_called_before()
    {
        await HistoryFixtures.BuildRenamedProjectAsync(_host, "quietened");
        // Work elsewhere, long after the cutover: the window ends at the repository's newest commit, so
        // this is what puts the rename out of reach of a short one — which is the situation an agent
        // asking "what is busy here" with the default window actually meets.
        _host.CommitToGitRepositoryAs("quietened-one",
            new Dictionary<string, string> { ["src/Api/Handler.cs"] = "handler\nmore\nstill more\n" },
            "Work on the api a year later", "Grace", "grace@example.invalid", 31000 + 400 * 24 * 60);
        await _host.RefreshAsync("quietened");
        await using var client = await _host.ConnectAsync("quietened");

        // A window that reaches neither the cutover nor anything under the new path.
        string ranked = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 1, ["directory"] = "one/src/Model" });
        Assert.DoesNotContain("most-changed", ranked, StringComparison.Ordinal);
        Assert.Contains("its content was at 'one/model' before", ranked, StringComparison.Ordinal);
        Assert.Contains("Call hot_files with directory=\"one/model\" to rank it.", ranked, StringComparison.Ordinal);

        // co_changed's thin answer, which says the file may move alone, carries it too.
        string coupled = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Model/Contact.cs", ["days"] = 1 });
        Assert.Contains("its content was at 'one/model/Contact.cs' before", coupled, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A candidate on the scope's own branch is not a previous path. Most of `src` moving down into
    ///     `src/Model` makes `src` the dominant earlier prefix, and reporting it would give a combined
    ///     total covering all of `src` — the inflated number this feature exists to correct, pointing
    ///     backwards.
    /// </summary>
    [Fact]
    public async Task An_ancestor_of_the_scope_is_not_its_previous_path()
    {
        string source = _host.CreateEmptyGitRepository("nested-one");
        _host.CommitToGitRepositoryAs("nested-one",
            new Dictionary<string, string>
            {
                ["src/Contact.cs"] = "contact\n",
                ["src/Order.cs"] = "order\n",
                ["src/Unrelated.cs"] = "unrelated\n"
            },
            "Import the model", "Ada", "ada@example.invalid", 30000);
        _host.CommitToGitRepositoryAs("nested-one",
            new Dictionary<string, string> { ["src/Unrelated.cs"] = "unrelated\nmore\n" },
            "Work somewhere else in src", "Ada", "ada@example.invalid", 30001);
        _host.MoveInGitRepositoryAs("nested-one",
            new Dictionary<string, string>
            {
                ["src/Contact.cs"] = "src/Model/Contact.cs",
                ["src/Order.cs"] = "src/Model/Order.cs"
            },
            "Group the model together", "Grace", "grace@example.invalid", 31000);

        await _host.CreateProjectAsync("nested");
        await _host.AddRepositoryAsync("nested", "one", source);
        await _host.RefreshAsync("nested");

        await using var client = await _host.ConnectAsync("nested");
        string log = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["path"] = "one/src/Model", ["limit"] = 100 });

        Assert.DoesNotContain("This scope was renamed", log, StringComparison.Ordinal);
        // And in particular not the commit that never touched the model at all.
        Assert.DoesNotContain("Work somewhere else in src", log, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Coupling survives a move (#143). A file renamed in the cutover is at HEAD, so co_changed
    ///     answers it rather than refusing — and before this, its pairing saw only the post-rename
    ///     slice, reported that the file moves alone, and sent the caller to git_log and file_history,
    ///     neither of which answers coupling. It is the one read whose gap the previous-path signal
    ///     could name and point nowhere for, so it is the one read that follows a rename.
    /// </summary>
    [Fact]
    public async Task Co_changed_pairs_an_anchor_over_the_paths_it_was_renamed_from()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Renames);

        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Model/Contact.cs", ["days"] = 3650 });

        // The coupling it had before the move, which is all the coupling it has.
        Assert.Contains("one/src/Api/Handler.cs", reply, StringComparison.Ordinal);
        Assert.Contains("one/src/Odds/Helper.cs", reply, StringComparison.Ordinal);
        // A counterpart recorded under its own pre-rename path is named there and marked, because that
        // is where the commit recorded it and there is nothing at it now.
        Assert.Contains("one/model/Order.cs  (no longer at HEAD)", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("moves alone", reply, StringComparison.Ordinal);

        // The anchor's own earlier name is not a file it co-changed with. Asserted against the ranking
        // rows and not the whole reply: the prose legitimately names the earlier path — twice, once to
        // say what the anchor was called and once to say the ranking spans it — so paths is the
        // wrong claim and "absent from the ranking" is the right one.
        var ranked = reply.Split('\n').Where(line => line.Contains("shared commit", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(ranked);
        Assert.DoesNotContain(ranked, line => line.Contains("one/model/Contact.cs", StringComparison.Ordinal));
        // Solo.cs shares only the cutover with the anchor, and moving together is not coupling.
        Assert.DoesNotContain("Solo.cs", reply, StringComparison.Ordinal);
        // The cutover is dropped per path and not per commit, so it is still a paired commit and the
        // ceiling's excluded-commits note — which has one cause and one remedy — does not fire.
        Assert.DoesNotContain("left out of the pairing", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The pairing spans the whole chain the walk found, not the three hops a reply prints. The two
    ///     are different lists — the cap is on what is readable, and the combined total beside it is
    ///     taken over everything — so a ranking drawn from the printed list would contradict the number
    ///     printed under it. Four renames, a cap of three, and a counterpart that only exists at the
    ///     fourth hop: it ranks, or the pairing is reading the wrong list.
    /// </summary>
    [Fact]
    public async Task The_pairing_spans_the_whole_chain_and_not_the_hops_a_reply_prints()
    {
        string source = _host.CreateEmptyGitRepository("deep-one");
        _host.CommitToGitRepositoryAs("deep-one",
            new Dictionary<string, string> { ["a/Thing.cs"] = "thing\n", ["a/Friend.cs"] = "friend\n" },
            "Import the thing and its friend", "Ada", "ada@example.invalid", 40000);
        foreach ((string from, string to, int minute) in new[]
                 {
                     ("a/Thing.cs", "b/Thing.cs", 40001), ("b/Thing.cs", "c/Thing.cs", 40002),
                     ("c/Thing.cs", "d/Thing.cs", 40003), ("d/Thing.cs", "e/Thing.cs", 40004)
                 })
            _host.MoveInGitRepositoryAs("deep-one", new Dictionary<string, string> { [from] = to },
                $"Move the thing to {to}", "Grace", "grace@example.invalid", minute);

        await _host.CreateProjectAsync("deep");
        await _host.AddRepositoryAsync("deep", "one", source);
        await _host.RefreshAsync("deep");
        await using var client = await _host.ConnectAsync("deep");

        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/e/Thing.cs", ["days"] = 3650 });

        // The cap bites: three hops printed, the fourth counted and said.
        Assert.Contains("1 further earlier path not shown", reply, StringComparison.Ordinal);
        // And the counterpart that only ever shared a commit at the fourth hop is ranked anyway.
        Assert.Contains("one/a/Friend.cs", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The exception is `co_changed`'s alone. An agent that met a followed rename there and carried
    ///     the assumption to its neighbours would read every other count as wider than it is, so the
    ///     tools have to keep disagreeing on purpose — and say so.
    /// </summary>
    [Fact]
    public async Task Only_co_changed_follows_a_rename_and_its_description_says_so()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Renames);

        var listed = (await client.ListToolsAsync(cancellationToken: Ct))
            .ToDictionary(tool => tool.Name, tool => tool.Description ?? "", StringComparer.Ordinal);
        Assert.Contains("the one read here that FOLLOWS a rename", listed["co_changed"], StringComparison.Ordinal);
        foreach (string tool in new[] { "git_log", "authors", "file_history", "hot_files" })
        {
            // Each neighbour still states the recorded-path rule in its own words, and none of them
            // claims to follow a rename.
            Assert.Contains("begins where", listed[tool], StringComparison.Ordinal);
            Assert.DoesNotContain("FOLLOWS a rename", listed[tool], StringComparison.Ordinal);
        }

        // The neighbours still count what their own path recorded: git_log scoped to the new path sees
        // the cutover alone, whatever co_changed does.
        string log = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["path"] = "one/src/Model", ["limit"] = 100 });
        Assert.DoesNotContain("Add the contact feature", log, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A path renamed twice reports its chain, oldest last. One hop is the common case and the one
    ///     a walk that stopped at the first edge would get right; two is what says the walk is
    ///     transitive.
    /// </summary>
    [Fact]
    public async Task A_path_renamed_twice_reports_its_chain_oldest_last()
    {
        await HistoryFixtures.BuildRenamedProjectAsync(_host, "twice");
        _host.MoveInGitRepositoryAs("twice-one",
            new Dictionary<string, string>
            {
                ["src/Model/Contact.cs"] = "src/Domain/Contact.cs",
                ["src/Model/Order.cs"] = "src/Domain/Order.cs"
            },
            "Merged PR 40001: Moved Model to src\\Domain", "Grace", "grace@example.invalid", 32000);
        await _host.RefreshAsync("twice");

        await using var client = await _host.ConnectAsync("twice");
        string log = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["path"] = "one/src/Domain" });

        Assert.Contains("its content was at 'one/src/Model' before", log, StringComparison.Ordinal);
        Assert.Contains("Before that, 'one/model'", log, StringComparison.Ordinal);
        Assert.True(log.IndexOf("one/src/Model", StringComparison.Ordinal)
                    < log.IndexOf("Before that, 'one/model'", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The other half, and the one that keeps the change additive: a scope nothing was renamed from
    ///     answers exactly as it did. A note that fired on an ordinary directory would be read past
    ///     within a session, and then read past on the directory that needed it.
    /// </summary>
    [Fact]
    public async Task A_scope_with_no_previous_path_says_nothing_about_one()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Renames);

        foreach (string reply in new[]
                 {
                     await TestHost.CallAsync(client, "hot_files",
                         new Dictionary<string, object?> { ["days"] = 3650, ["directory"] = "one/src/Api" }),
                     await TestHost.CallAsync(client, "git_log",
                         new Dictionary<string, object?> { ["path"] = "one/src/Api" }),
                     await TestHost.CallAsync(client, "authors",
                         new Dictionary<string, object?> { ["path"] = "one/src/Api" }),
                     await TestHost.CallAsync(client, "file_history",
                         new Dictionary<string, object?> { ["path"] = "one/src/Api/Handler.cs" }),
                     await TestHost.CallAsync(client, "co_changed",
                         new Dictionary<string, object?> { ["path"] = "one/src/Api/Handler.cs", ["days"] = 3650 })
                 })
        {
            Assert.DoesNotContain("This scope was renamed", reply, StringComparison.Ordinal);
            Assert.DoesNotContain("across the whole chain", reply, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     A stray file that moved in is not a previous path. `src/Api` took one file from `src/Odds`
    ///     and holds several of its own, so `src/Odds` is below the share a previous path has to clear —
    ///     and a rule counting renamed rows alone rather than the scope's recorded paths would call it
    ///     one, because it is the only rename edge the scope has.
    /// </summary>
    [Fact]
    public async Task A_single_file_moved_in_is_not_a_previous_path()
    {
        await HistoryFixtures.BuildRenamedProjectAsync(_host, "stray");
        _host.MoveInGitRepositoryAs("stray-one",
            new Dictionary<string, string> { ["src/Odds/Helper.cs"] = "src/Api/Helper.cs" },
            "Move the helper where it is used", "Grace", "grace@example.invalid", 32000);
        await _host.RefreshAsync("stray");

        await using var client = await _host.ConnectAsync("stray");
        string log = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["path"] = "one/src/Api" });

        Assert.DoesNotContain("This scope was renamed", log, StringComparison.Ordinal);
        Assert.DoesNotContain("one/src/Odds", log, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The whole chain arrives in one query, however long it is (#148). Four renames, so the walk
    ///     this replaced would have run nine: a previous-path scan and a commit count per hop, and a
    ///     combined count at the end, each of them a pass over <c>commit_files</c>.
    ///     Asserted through the query-plan switch and not through a counter written for the test,
    ///     because that switch is what a developer investigating a slow read actually turns on — a
    ///     count taken any other way could pass while the dumps told a different story.
    ///     It is process-wide while it is held, and other classes are running, so this counts only the
    ///     dumps naming its own repository. The slug is its own for that reason.
    /// </summary>
    [Fact]
    public async Task A_scoped_call_reads_a_whole_rename_chain_in_one_query()
    {
        string source = _host.CreateEmptyGitRepository("planned-one");
        _host.CommitToGitRepositoryAs("planned-one",
            new Dictionary<string, string> { ["a/Thing.cs"] = "thing\n" },
            "Import the thing", "Ada", "ada@example.invalid", 40000);
        foreach ((string from, string to, int minute) in new[]
                 {
                     ("a/Thing.cs", "b/Thing.cs", 40001), ("b/Thing.cs", "c/Thing.cs", 40002),
                     ("c/Thing.cs", "d/Thing.cs", 40003), ("d/Thing.cs", "e/Thing.cs", 40004)
                 })
            _host.MoveInGitRepositoryAs("planned-one", new Dictionary<string, string> { [from] = to },
                $"Move the thing to {to}", "Grace", "grace@example.invalid", minute);

        await _host.CreateProjectAsync("planned");
        await _host.AddRepositoryAsync("planned", "planned", source);
        await _host.RefreshAsync("planned");

        string plans = _host.ScratchFile("plans");
        await using var client = await _host.ConnectAsync("planned");
        string reply;
        using (QueryPlan.Recording(plans))
            reply = await TestHost.CallAsync(client, "git_log",
                new Dictionary<string, object?> { ["path"] = "planned/e/Thing.cs" });

        // The chain is really four hops long, or one query would be no achievement.
        Assert.Contains("its content was at 'planned/d/Thing.cs' before", reply, StringComparison.Ordinal);
        Assert.Contains("1 further earlier path not shown", reply, StringComparison.Ordinal);

        var dumps = Directory.EnumerateFiles(plans, "*PathLineageQueries-LineageAsync.sql.txt")
            .Where(dump => File.ReadAllText(dump).Contains("$r = planned", StringComparison.Ordinal))
            .ToList();
        Assert.Single(dumps);
    }
}

/// <inheritdoc cref="HistoryFixture" />
public sealed class PathLineageFixture : HistoryFixture
{
    protected override IReadOnlyList<string> Projects => [HistoryFixtures.Renames];
}
