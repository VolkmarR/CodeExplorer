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
}

/// <inheritdoc cref="HistoryFixture" />
public sealed class AuthorsFixture : HistoryFixture
{
    protected override IReadOnlyList<string> Projects => [HistoryFixtures.Churn, HistoryFixtures.Mixed];
}
