using CodeExplorer.Infrastructure;
using CodeExplorer.Reading;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     <c>authors</c>: who has committed here, ranked by how much. It answers "who do I ask about
///     this", so the ordering and the cut are what is asserted — a listing that stopped at the limit
///     without saying so would read as the whole team, and an empty one as a project nobody has
///     worked on rather than as an index with no history.
///     It shares its fixtures and its scoping rules with <c>git_log</c>: the facts the two answer
///     together — a path scope, an author filter that matched nobody — are asserted once, beside the
///     tool whose reply states them.
/// </summary>
public sealed class AuthorsTests(AuthorsFixture fixture) : IClassFixture<AuthorsFixture>
{
    private readonly TestHost _host = fixture.Host;

    /// <summary>
    ///     The listing `author` is read from, so the address an agent types into the filter is one it
    ///     saw here. Ranked by commits, and scoped the way every other history tool is.
    /// </summary>
    [Fact]
    public async Task Authors_lists_who_committed_with_the_address_each_commits_from()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Mixed);

        string scoped = await TestHost.CallAsync(client, "authors",
            new Dictionary<string, object?> { ["repo"] = "one" });

        Assert.StartsWith("2 authors in repository 'one', most commits first:", scoped, StringComparison.Ordinal);
        Assert.Contains("3 commits  Grace <grace@example.invalid>, last on ", scoped, StringComparison.Ordinal);
        Assert.Contains("2 commits  Ada <ada@example.invalid>, last on ", scoped, StringComparison.Ordinal);
        // Most commits first, which is what makes the first row "who to ask".
        Assert.True(scoped.IndexOf("Grace", StringComparison.Ordinal) < scoped.IndexOf("Ada", StringComparison.Ordinal));

        // The second repository has its own author, so the unscoped listing is wider than the scoped one.
        string whole = await TestHost.CallAsync(client, "authors", []);
        Assert.DoesNotContain("in repository 'one'", whole, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A cut listing says so, because a list that stops at the limit and says nothing reads as the
    ///     whole team — and "who has worked here" is a question an agent answers once and carries.
    /// </summary>
    [Fact]
    public async Task Authors_says_how_many_there_are_when_the_limit_cuts_the_list()
    {
        // The single-repository fixture: this is about the limit, not about spanning repositories.
        await using var client = await _host.ConnectAsync(HistoryFixtures.Churn);

        string reply = await TestHost.CallAsync(client, "authors", new Dictionary<string, object?> { ["limit"] = 1 });

        Assert.StartsWith("2 authors", reply, StringComparison.Ordinal);
        Assert.Contains("(1 shown, limit 1):", reply, StringComparison.Ordinal);
        Assert.Contains("Grace <grace@example.invalid>", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("Ada <ada@example.invalid>", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An index with no history has no authors, and says the one sentence every history tool says
    ///     about it: an empty list here would read as a project nobody has worked on, which is a fact,
    ///     where this is the absence of one (CONTEXT.md, History).
    /// </summary>
    [Fact]
    public async Task Authors_says_an_index_without_history_has_none_in_the_shared_words()
    {
        await HistoryFixtures.OnlyRepositoryProjectAsync(_host, "empty",
            new Dictionary<string, string> { ["a.cs"] = "class A;\n" });
        await _host.ExecuteAsync("empty", "DELETE FROM commits");
        await using var client = await _host.ConnectAsync("empty");

        Assert.Equal(ToolReply.NoHistory, await TestHost.CallAsync(client, "authors", []));
    }

    /// <summary>
    ///     The same under a path, which is the branch the closing note can reach and the plain call
    ///     cannot: a path is resolved before the history is looked for, so an index with none still
    ///     carries the scope it was asked about. The caveat about how scoping matches recorded commit
    ///     paths, said under the sentence that nothing was recorded at all, would describe a read that
    ///     never happened — so the answer stays the one sentence, exactly as the unscoped call's does.
    /// </summary>
    [Fact]
    public async Task An_index_without_history_says_only_that_even_under_a_path()
    {
        await HistoryFixtures.OnlyRepositoryProjectAsync(_host, "unscoped",
            new Dictionary<string, string> { ["src/a.cs"] = "class A;\n" });
        await _host.ExecuteAsync("unscoped", "DELETE FROM commits");
        await using var client = await _host.ConnectAsync("unscoped");

        var scoped = new Dictionary<string, object?> { ["path"] = "only/src/a.cs" };
        Assert.Equal(ToolReply.NoHistory, await TestHost.CallAsync(client, "authors", scoped));
        Assert.Equal(ToolReply.NoHistory, await TestHost.CallAsync(client, "git_log", scoped));
    }

    /// <summary>
    ///     The totals ride in the scan that lists the rows (#179): the authors listing and an address
    ///     filter each read <c>commits</c> once for who is there, not a second time to count them. The
    ///     authors call is cut below the addresses it has, so a total taken from the listed rows rather
    ///     than from every group would read too low and fail the header asserted beside the plan.
    /// </summary>
    [Fact]
    public async Task Authors_and_an_address_filter_count_in_the_scan_that_lists_them()
    {
        string source = _host.CreateEmptyGitRepository("tallied-one");
        _host.CommitToGitRepositoryAs("tallied-one", new Dictionary<string, string> { ["a.txt"] = "a\n" },
            "Add a", "Ada", "ada@example.invalid", 0);
        _host.CommitToGitRepositoryAs("tallied-one", new Dictionary<string, string> { ["b.txt"] = "b\n" },
            "Add b", "Grace", "grace@example.invalid", 1);
        _host.CommitToGitRepositoryAs("tallied-one", new Dictionary<string, string> { ["b.txt"] = "b2\n" },
            "Change b", "Grace", "grace@example.invalid", 2);
        await _host.CreateProjectAsync("tallied");
        await _host.AddRepositoryAsync("tallied", "tallied", source);
        await _host.RefreshAsync("tallied");
        await using var client = await _host.ConnectAsync("tallied");

        string authorsPlans = _host.ScratchFile("authors-plans");
        string authors;
        using (QueryPlan.Recording(authorsPlans))
            authors = await TestHost.CallAsync(client, "authors",
                new Dictionary<string, object?> { ["repo"] = "tallied", ["limit"] = 1 });
        string logPlans = _host.ScratchFile("log-plans");
        string log;
        using (QueryPlan.Recording(logPlans))
            log = await TestHost.CallAsync(client, "git_log",
                new Dictionary<string, object?> { ["repo"] = "tallied", ["author"] = "example", ["limit"] = 1 });

        Assert.StartsWith("2 authors in repository 'tallied'", authors, StringComparison.Ordinal);
        Assert.Contains("'example' matched 2 addresses, 3 commits in all:", log, StringComparison.Ordinal);
        Assert.Equal(["AuthorsAsync"], ReadsOfCommits(authorsPlans));
        // The page of commits is read where it is turned into rows, so it is filed under that method.
        Assert.Equal(["AuthorsAsync", "ChangesAsync"], ReadsOfCommits(logPlans));

        // A filter matching nobody has no row to carry a total, and reads the authors it is measured
        // against — the one case the separate count is kept for.
        string missPlans = _host.ScratchFile("miss-plans");
        string miss;
        using (QueryPlan.Recording(missPlans))
            miss = await TestHost.CallAsync(client, "git_log",
                new Dictionary<string, object?> { ["repo"] = "tallied", ["author"] = "nobody" });
        Assert.Contains("2 addresses recorded", miss, StringComparison.Ordinal);
        Assert.Equal(["AuthorsAsync", "AuthorCountAsync"], ReadsOfCommits(missPlans));
    }

    /// <summary>
    ///     The commit-log reads of the scoped repository a recording caught, by method, in the order
    ///     they ran. Filtered by the scope's parameter because the switch is process-wide and another
    ///     test's reads may land in the same directory.
    /// </summary>
    private static List<string> ReadsOfCommits(string plans) =>
        Directory.EnumerateFiles(plans, "*CommitLogQueries-*.sql.txt")
            .Where(dump => File.ReadAllText(dump).Contains("$r = tallied", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(dump => Path.GetFileName(dump).Split("CommitLogQueries-")[1].Replace(".sql.txt", "",
                StringComparison.Ordinal))
            .ToList();
}

/// <inheritdoc cref="HistoryFixture" />
public sealed class AuthorsFixture : HistoryFixture
{
    protected override IReadOnlyList<string> Projects => [HistoryFixtures.Churn, HistoryFixtures.Mixed];
}
