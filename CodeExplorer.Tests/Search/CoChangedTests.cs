using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     <c>co_changed</c>: which files keep moving with one file, and the four different reasons the
///     pairing can come back empty. They read alike to an agent and mean opposite things, so each has
///     its own sentence and each is asserted on.
/// </summary>
public sealed class CoChangedTests(CoChangedFixture fixture) : IClassFixture<CoChangedFixture>
{
    private readonly TestHost _host = fixture.Host;

    /// <summary>
    ///     The coupling itself: the files a window's commits kept changing alongside one file, most
    ///     shared commits first. The fixture couples Api.cs to Store.cs more often than to Dto.cs and
    ///     never to Lonely.cs, so a ranking that counted the window's commits rather than the shared
    ///     ones would fail rather than happen to agree.
    /// </summary>
    [Fact]
    public async Task Co_changed_ranks_the_files_that_keep_moving_with_a_file()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Coupled);
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Api.cs", ["days"] = 30 });

        // Three commits changed Store.cs with Api.cs, two Dto.cs, one Old.cs, and the order must be that.
        Assert.True(reply.IndexOf("one/src/Store.cs", StringComparison.Ordinal)
                    < reply.IndexOf("one/src/Dto.cs", StringComparison.Ordinal));
        Assert.True(reply.IndexOf("one/src/Dto.cs", StringComparison.Ordinal)
                    < reply.IndexOf("one/src/Old.cs", StringComparison.Ordinal));
        Assert.Contains("3 shared commits", reply, StringComparison.Ordinal);
        // Lonely.cs was committed on its own, so no commit of Api.cs's could have carried it.
        Assert.DoesNotContain("Lonely.cs", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A path the window changed and HEAD no longer holds is ranked here for the reason it is ranked
    ///     in hot_files: it is coupling that happened, and an agent sent to read a file that is not
    ///     there has been told something false.
    /// </summary>
    [Fact]
    public async Task Co_changed_marks_a_path_that_is_no_longer_at_head()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Coupled);
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Api.cs", ["days"] = 30 });

        Assert.Contains("one/src/Old.cs  (no longer at HEAD)", reply, StringComparison.Ordinal);
        Assert.Contains("one/src/Store.cs\n", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The whole risk in this ranking. One reformat, vendor drop or initial import pairs every path
    ///     it touched with every other, and those pairs are not coupling — they are one commit. The
    ///     ceiling keeps them out, and because what counts as a mass commit differs between a repository
    ///     of two hundred files and one of eighty thousand, it is a setting: the same fixture answers
    ///     differently on a host that was told a dozen paths is a mass commit.
    /// </summary>
    [Fact]
    public async Task Co_changed_leaves_a_mass_commit_out_of_the_pairing_and_says_it_did()
    {
        await using var wide = await _host.ConnectAsync(HistoryFixtures.Coupled);
        string included = await TestHost.CallAsync(wide, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Api.cs", ["days"] = 30 });
        // Under the shipped ceiling the reformat is an ordinary commit, and every path it touched
        // is coupled to Api.cs once — which is what swamps the ranking and what the ceiling is for.
        Assert.Contains("vendor/Bulk01.cs", included, StringComparison.Ordinal);
        Assert.DoesNotContain("left out of the pairing", included, StringComparison.Ordinal);

        using var tight = new TestHost(SearchEngine.Substring, maxCommitPaths: 5);
        await HistoryFixtures.BuildCoupledProjectAsync(tight, HistoryFixtures.Coupled);
        await using var client = await tight.ConnectAsync(HistoryFixtures.Coupled);
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Api.cs", ["days"] = 30 });

        Assert.DoesNotContain("vendor/Bulk", reply, StringComparison.Ordinal);
        Assert.Contains("left out of the pairing", reply, StringComparison.Ordinal);
        Assert.Contains("History:MaxCommitPaths", reply, StringComparison.Ordinal);
        // The coupling that is real survives the exclusion.
        Assert.Contains("one/src/Store.cs", reply, StringComparison.Ordinal);

        // The header counts every commit that touched the file, which is the number the note below it
        // then divides: Api.cs has five, one of them the reformat. Counting only the paired four there
        // made the two sentences contradict each other, with the smaller number leading (#116).
        Assert.Contains("alongside one/src/Api.cs, out of the 5 commits that touched it", reply,
            StringComparison.Ordinal);
        Assert.Contains("1 of its 5 commits", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A file nothing moves with must say so. An empty list reads as "this file has no couplings
    ///     worth knowing", which is a finding, and it must not be what a caller gets from a file whose
    ///     history simply has not been imported (CODING_STANDARDS, Errors).
    /// </summary>
    [Fact]
    public async Task Co_changed_says_a_file_moves_alone_rather_than_answering_nothing()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Coupled);
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Lonely.cs", ["days"] = 30 });

        Assert.Contains("No other file", reply, StringComparison.Ordinal);
        Assert.Contains("one/src/Lonely.cs", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A file whose every commit in the window was a mass change (#127). The ceiling excluded all of
    ///     them, so nothing was paired and nothing can be — which is the opposite fact from a file that
    ///     moves alone, and the answer a bulk rename leaves behind on every path it touched.
    /// </summary>
    [Fact]
    public async Task Co_changed_says_a_files_only_commits_were_mass_changes_rather_than_that_it_moves_alone()
    {
        using var tight = new TestHost(SearchEngine.Substring, maxCommitPaths: 5);
        await HistoryFixtures.BuildCoupledProjectAsync(tight, "swept");
        await using var client = await tight.ConnectAsync("swept");

        // Bulk01.cs was created by the thirteen-path reformat and touched by nothing else.
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/vendor/Bulk01.cs", ["days"] = 30 });

        Assert.Contains("no usable co-change history", reply, StringComparison.Ordinal);
        Assert.Contains("one/vendor/Bulk01.cs", reply, StringComparison.Ordinal);
        Assert.Contains("more than 5 paths", reply, StringComparison.Ordinal);
        Assert.Contains("file_history", reply, StringComparison.Ordinal);
        // The two empty answers must not read alike: this file's history is unusable, where Lonely.cs
        // has a usable history that holds no coupling. The words "moves alone" do appear — this
        // branch denies the reading rather than avoiding it — but the other branch's claim must not.
        Assert.DoesNotContain("It moves alone in the history", reply, StringComparison.Ordinal);
        // Nor may it read as the count it would have carried: none of its commits were pairable, and
        // "any of the 0 commits that touched it" is the sentence this branch exists to replace.
        Assert.DoesNotContain("the 0 commits", reply, StringComparison.Ordinal);

        string alone = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Lonely.cs", ["days"] = 30 });
        Assert.Contains("moves alone", alone, StringComparison.Ordinal);
        Assert.DoesNotContain("no usable co-change history", alone, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A window that reaches none of a file's commits is not a file that moves alone, and the two
    ///     answers have to read differently or the shorter window teaches the agent a wrong fact.
    /// </summary>
    [Fact]
    public async Task Co_changed_says_when_the_window_reached_none_of_a_files_commits()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Coupled);
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/old/Ancient.cs", ["days"] = 1 });

        Assert.Contains("No commit", reply, StringComparison.Ordinal);
        Assert.Contains("raise days", reply, StringComparison.Ordinal);
        // "Older than the window" and "never recorded" are opposite facts, so this one names its
        // newest recorded commit rather than leaving the caller to guess which it got (#115).
        Assert.Contains("1 commit is recorded under this path, the newest from", reply, StringComparison.Ordinal);
        Assert.Contains("matched by the path a commit recorded", reply, StringComparison.Ordinal);
        Assert.Contains("blame follows content across a rename", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The branch a renamed file lands in (#115). A bulk rename moves a long-lived file to a new
    ///     path, the commits that made it are recorded under the old one, and every window that ends
    ///     after the rename reaches nothing — a real coupling the tool would otherwise go quiet about.
    /// </summary>
    [Fact]
    public async Task Co_changed_says_a_path_records_nothing_rather_than_only_offering_a_wider_window()
    {
        await HistoryFixtures.BuildCoupledProjectAsync(_host, "severed");
        // No commit_files row spells this path, which is what a severed history looks like from here.
        await _host.ExecuteAsync("severed",
            "DELETE FROM commit_files WHERE path = 'old/Ancient.cs'");

        await using var client = await _host.ConnectAsync("severed");
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/old/Ancient.cs", ["days"] = 3650 });

        Assert.Contains("No commit at all is recorded under this path", reply, StringComparison.Ordinal);
        Assert.Contains("bulk rename", reply, StringComparison.Ordinal);
        // The one piece of advice that cannot work here is the one the branch used to give.
        Assert.Contains("Widening days will not reach it", reply, StringComparison.Ordinal);
        Assert.Contains("blame follows content across a rename", reply, StringComparison.Ordinal);

        // The neighbouring branch is a different answer and must stay one.
        string alone = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Lonely.cs", ["days"] = 3650 });
        Assert.Contains("It moves alone in the history that was imported", alone, StringComparison.Ordinal);
        Assert.DoesNotContain("No commit at all is recorded", alone, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Co_changed_on_a_project_without_history_says_so_rather_than_pairing_nothing()
    {
        await HistoryFixtures.OnlyRepositoryProjectAsync(_host, "epsilon",
            new Dictionary<string, string> { ["a.cs"] = "class A;\n" });
        await _host.ExecuteAsync("epsilon", "DELETE FROM commits");

        await using var client = await _host.ConnectAsync("epsilon");
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "only/a.cs" });

        Assert.Contains("holds no history", reply, StringComparison.Ordinal);
        Assert.Contains("Ask the operator to refresh", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Co_changed_on_an_unknown_path_is_an_answer_and_not_an_empty_ranking()
    {
        var client = await StartAsync();
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Missing.cs" });

        Assert.Contains("No indexed file", reply, StringComparison.Ordinal);
        Assert.Contains("glob or list_tree", reply, StringComparison.Ordinal);
    }

    private Task<McpClient> StartAsync() => _host.ConnectAsync(HistoryFixtures.Alpha);
}

/// <inheritdoc cref="HistoryFixture" />
public sealed class CoChangedFixture : HistoryFixture
{
    protected override IReadOnlyList<string> Projects => [HistoryFixtures.Alpha, HistoryFixtures.Coupled];
}
