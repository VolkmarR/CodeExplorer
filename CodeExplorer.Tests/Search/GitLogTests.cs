using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     <c>git_log</c> and <c>authors</c> as an agent reaches them, over MCP and against a real
///     index. Neither matches text, so the engine is pinned to Substring once rather than run twice.
///     What is asserted here is mostly the prose. These tools answer a question an agent will act on —
///     who to ask about a line — and a reply that reads as authorship when it means "last touched" is
///     the failure mode, not a wrong row.
/// </summary>
public sealed class GitLogTests(GitLogFixture fixture) : IClassFixture<GitLogFixture>
{
    private readonly TestHost _host = fixture.Host;

    /// <summary>
    ///     What a call carrying one name git_log does not have is read with, whatever the answer under
    ///     it turns out to be. Written once because the point of the assertions below is that the four
    ///     outcomes are introduced by the same sentence: one that varied with the answer would be
    ///     telling an agent something about a log that may not be there.
    /// </summary>
    private const string Caveat =
        "`git_log` has no `committer` argument; it was ignored. "
        + "It takes `repo`, `author`, `message`, `limit`, `page` and `path`.";

    [Fact]
    public async Task Git_log_lists_the_commits_newest_first_with_their_authors()
    {
        var client = await StartAsync();
        string reply = await TestHost.CallAsync(client, "git_log", []);

        Assert.Contains("2 commits", reply, StringComparison.Ordinal);
        Assert.Contains("Grace <grace@example.invalid>", reply, StringComparison.Ordinal);
        Assert.Contains("Tighten the check", reply, StringComparison.Ordinal);
        // Newest first: the second commit's subject must precede the first's.
        Assert.True(reply.IndexOf("Tighten the check", StringComparison.Ordinal)
                    < reply.IndexOf("Add the validator", StringComparison.Ordinal));
        // Nothing was ignored, so nothing is said about it: the caveat below has to stay rare enough
        // that an agent still reads it.
        Assert.DoesNotContain("`git_log` has no", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The largest page a caller can spell is a page past the end like any other (#233). Its offset
    ///     does not fit an <c>int</c>, and wrapped negative it reached DuckDB as an error; the count in
    ///     the sentence is the same product, so it has to be computed as wide as the offset.
    /// </summary>
    [Fact]
    public async Task Git_log_answers_the_largest_page_as_a_page_past_the_end()
    {
        var client = await StartAsync();
        string reply = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["page"] = int.MaxValue, ["limit"] = 50 });

        Assert.Contains("No commits on page 2147483647. There are fewer than 107374182301 commits", reply,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>author</c> narrows the log, and the header carries the filter: a narrowed log that
    ///     introduces itself as "5 commits" is the wrong fact #86 was about, arrived at from the other
    ///     direction.
    ///     One address whose commits all fit is the whole answer, so nothing is listed under it. The
    ///     line is for what the header cannot say.
    /// </summary>
    [Fact]
    public async Task Git_log_scopes_to_one_author_by_address()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Mixed);

        string reply = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["author"] = "grace" });

        Assert.Contains("3 commits by an address matching 'grace'", reply, StringComparison.Ordinal);
        // Ada's commits are in the same repository and the same page, so their absence is the filter.
        Assert.DoesNotContain("Add the module", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("Import the old code", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("matched 1 address", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     What the header cannot say: a substring caught more than one address, or more commits than
    ///     the page holds. Both are what a caller needs to know it did not get the person it meant, and
    ///     neither is visible in a page of commits that all look plausible.
    /// </summary>
    [Fact]
    public async Task Git_log_lists_the_addresses_a_filter_caught_when_it_caught_more_than_the_page_shows()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Mixed);

        string reply = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["author"] = "example.invalid", ["limit"] = 2 });

        Assert.Contains("2 commits by an address matching 'example.invalid'", reply, StringComparison.Ordinal);
        // Three people share the domain, and the page shows two commits of the six they have between
        // them — neither fact is visible in the page itself.
        Assert.Contains("'example.invalid' matched 3 addresses, 6 commits in all:", reply, StringComparison.Ordinal);
        Assert.Contains("Grace <grace@example.invalid>", reply, StringComparison.Ordinal);
        Assert.Contains("Ada <ada@example.invalid>", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An empty `author` is no filter, and the two ways it could go wrong are opposite: `ILIKE '%%'`
    ///     matches every commit and would be reported as a filtered log, which is the #86 failure again.
    ///     The `_` is the other half — a LIKE metacharacter in caller text, which must match a literal
    ///     underscore rather than any character, or a filter silently widens to a stranger's commits.
    /// </summary>
    [Fact]
    public async Task Git_log_takes_an_empty_author_as_no_filter_and_a_wildcard_as_text()
    {
        var client = await StartAsync();

        string blank = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["author"] = "  " });
        Assert.StartsWith("2 commits", blank, StringComparison.Ordinal);
        Assert.DoesNotContain("matching", blank, StringComparison.Ordinal);

        // "gr_ce" is "grace" with a LIKE wildcard where the `a` is: a match means the pattern is live.
        string wildcard = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["author"] = "gr_ce" });
        Assert.StartsWith("No address contains 'gr_ce'", wildcard, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The entry point into history an agent actually arrives with: a ticket number, a PR number,
    ///     a release name — a token that exists in the commit message and nowhere in the code. Without
    ///     this, finding its commit was paging the log until it appeared, and one measured evaluation
    ///     instead grepped the source for the ticket number and hit nothing, because that is not where
    ///     ticket numbers live (#110).
    /// </summary>
    [Fact]
    public async Task Git_log_finds_a_commit_by_text_in_its_subject()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Mixed);

        string reply = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["message"] = "the module" });

        // The header says it was narrowed and by what: a filtered log that introduces itself as the
        // log is the fault #86 was about, arrived at from a third direction.
        Assert.Contains("1 commit whose subject contains 'the module'", reply, StringComparison.Ordinal);
        Assert.Contains("Add the module", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("Fix the check", reply, StringComparison.Ordinal);

        // Case-insensitive, like the address filter, because a subject is quoted from memory.
        string shouted = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["message"] = "ADD THE MODULE" });
        Assert.Contains("Add the module", shouted, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The two filters narrow one log together, and the header names both. One that mentioned the
    ///     address and not the subject would be describing a page it did not return.
    /// </summary>
    [Fact]
    public async Task Git_log_combines_the_subject_filter_with_author_and_repo()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Mixed);

        string reply = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["message"] = "it", ["author"] = "grace", ["repo"] = "one" });

        Assert.Contains("by an address matching 'grace'", reply, StringComparison.Ordinal);
        Assert.Contains("whose subject contains 'it'", reply, StringComparison.Ordinal);
        Assert.Contains("in repository 'one'", reply, StringComparison.Ordinal);
        // Ada's "Import the old code" also contains "it"... it does not: the filter is a substring of
        // the subject, and only Grace's two commits carry one. Her "Fix the check" does not either.
        Assert.Contains("Tighten it again", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("Import the old code", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A subject nobody wrote, and the two ways that is not an empty log: it is a filter that
    ///     matched nothing, and the thing it searched is the subject and not the code. An agent that
    ///     reads it as "this project has no such commits" goes looking in the wrong place next
    ///     (CODING_STANDARDS, Errors).
    ///     The wildcard half is the same trap the address filter has: `%` is caller text here, and a
    ///     filter that silently matched everything would report the whole log as a search result.
    /// </summary>
    [Fact]
    public async Task Git_log_says_a_subject_filter_missed_and_treats_a_wildcard_as_text()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Churn);

        string missed = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["message"] = "BugFix 558185" });
        Assert.StartsWith("No commit's subject contains 'BugFix 558185'", missed, StringComparison.Ordinal);
        Assert.Contains("grep searches the code", missed, StringComparison.Ordinal);
        // Not the sentence for a project with no commits, which is the opposite fact.
        Assert.DoesNotContain("No commits are recorded", missed, StringComparison.Ordinal);

        string wildcard = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["message"] = "%" });
        Assert.StartsWith("No commit's subject contains '%'", wildcard, StringComparison.Ordinal);

        // Whitespace is no filter at all, not a filter matching everything.
        string blank = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["message"] = "  " });
        Assert.DoesNotContain("whose subject contains", blank, StringComparison.Ordinal);
        Assert.Contains("Add the module", blank, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The decision the parameter's description states: the address is the identity git records, so
    ///     a display name matches nothing. Asserted with a name that is not part of its own address,
    ///     because the fixtures elsewhere commit as "Grace &lt;grace@…&gt;" and would pass either way —
    ///     the one shape that cannot tell the two rules apart.
    /// </summary>
    [Fact]
    public async Task Git_log_matches_the_address_and_never_the_display_name()
    {
        string source = _host.CreateEmptyGitRepository("named");
        _host.CommitToGitRepositoryAs("named", new Dictionary<string, string> { ["src/A.cs"] = "a\n" },
            "Add it", "Holger Meyer", "hm@example.invalid", 0);
        await _host.CreateProjectAsync("named");
        await _host.AddRepositoryAsync("named", "one", source);
        await _host.RefreshAsync("named");
        await using var client = await _host.ConnectAsync("named");

        string byName = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["author"] = "Holger" });
        Assert.StartsWith("No address contains 'Holger'", byName, StringComparison.Ordinal);
        Assert.Contains("`author` matches the address, not the name", byName, StringComparison.Ordinal);

        string byAddress = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["author"] = "hm@" });
        Assert.Contains("1 commit by an address matching 'hm@'", byAddress, StringComparison.Ordinal);
        Assert.Contains("Add it", byAddress, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A filtered miss is not a clean negative (CODING_STANDARDS, Errors). "No commits by Holger"
    ///     and "no address here contains 'Holger'" mean opposite things to an agent, and the first is
    ///     what an empty log says on its own — so the reply says which it is, how many addresses there
    ///     are to have got wrong, and which tool lists them.
    /// </summary>
    [Fact]
    public async Task Git_log_says_an_address_that_matched_nobody_is_not_the_same_as_no_commits()
    {
        var client = await StartAsync();

        string reply = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["author"] = "nobody@example.invalid" });

        Assert.StartsWith("No address contains 'nobody@example.invalid'", reply,
            StringComparison.Ordinal);
        Assert.Contains("2 addresses recorded; call authors to list them.", reply, StringComparison.Ordinal);
        Assert.Contains("call authors to list them.", reply, StringComparison.Ordinal);
        // The log itself must not be under it: an unfiltered log below that sentence is the #86 bug.
        Assert.DoesNotContain("Tighten the check", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The failure this tool is most dangerous at, and the reason it is asserted here as well as
    ///     across the surface (ToolReplyTests). Every parameter of git_log is optional, so an argument
    ///     it does not have binds nowhere and the method runs with its defaults — and an unfiltered log
    ///     answering a filtered question is well-formed, plausible and wrong. The names here are what a
    ///     real session sent (issue #86), with `committer` for the `author` it has since grown: git
    ///     records both and this records the author, so it is the next spelling to arrive.
    /// </summary>
    [Fact]
    public async Task Git_log_says_which_arguments_it_ignored_rather_than_answering_as_though_it_filtered()
    {
        var client = await StartAsync();
        string reply = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?>
            {
                ["committer"] = "Holger",
                ["grep"] = "Holger",
                ["commit"] = "a41267be",
                ["limit"] = 3
            });

        Assert.StartsWith(
            "`git_log` has no `committer`, `grep` or `commit` argument; they were ignored. "
            + "It takes `repo`, `author`, `message`, `limit`, `page` and `path`.", reply, StringComparison.Ordinal);
        // The log is still answered: the caveat is what the answer is read with, not a refusal.
        Assert.Contains("2 commits", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The caveat says what was ignored and nothing about what follows it, because what follows is
    ///     not always a log. A sentence calling an unbuilt index, an empty page or a project with no
    ///     history "the unfiltered log" would be an absence dressed as a result — the very move this
    ///     change exists to stop (CODING_STANDARDS, Errors). All three outcomes are covered here because
    ///     the caveat is built outside the answer and would otherwise be asserted only on the happy path.
    /// </summary>
    [Fact]
    public async Task Git_log_says_what_it_ignored_without_claiming_there_is_a_log()
    {
        var client = await StartAsync();
        var ignoredArgument = new Dictionary<string, object?> { ["committer"] = "Holger" };

        // A problem: Render returns the explanation instead of an answer, and the caveat still arrives.
        string unknownRepository = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?>(ignoredArgument) { ["repo"] = "nope" });
        Assert.StartsWith(Caveat, unknownRepository, StringComparison.Ordinal);
        Assert.Contains("Drop `repo` to cover every repository.", unknownRepository, StringComparison.Ordinal);
        Assert.DoesNotContain("unfiltered log", unknownRepository, StringComparison.Ordinal);

        // An empty page: there is no log below at all, only a sentence saying how few commits there are.
        string pastTheEnd = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?>(ignoredArgument) { ["page"] = 9 });
        Assert.StartsWith(Caveat, pastTheEnd, StringComparison.Ordinal);
        Assert.Contains("No commits on page 9", pastTheEnd, StringComparison.Ordinal);

        // No history: the reply is the absence itself, which must not be introduced as a log.
        await HistoryFixtures.OnlyRepositoryProjectAsync(_host, "blanked",
            new Dictionary<string, string> { ["a.cs"] = "class A;\n" });
        await _host.ExecuteAsync("blanked", "DELETE FROM commits");
        await using var bare = await _host.ConnectAsync("blanked");
        string noHistory = await TestHost.CallAsync(bare, "git_log", ignoredArgument);
        Assert.StartsWith(Caveat, noHistory, StringComparison.Ordinal);
        Assert.Contains("holds no history", noHistory, StringComparison.Ordinal);
        Assert.DoesNotContain("unfiltered log", noHistory, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The spelling this tool itself took until the scope was renamed <c>repo</c> to match the other
    ///     five. It is reported and not accepted: both spellings would leave one concept with two names,
    ///     which is what caused this. A caller that saved the old spelling therefore learns what to send,
    ///     instead of being answered for the whole project as though it had been scoped.
    /// </summary>
    [Fact]
    public async Task Git_log_reports_the_old_repository_spelling_instead_of_binding_it()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Mixed);
        string reply = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["repository"] = "one" });

        Assert.StartsWith(
            "`git_log` has no `repository` argument; it was ignored. "
            + "It takes `repo`, `author`, `message`, `limit`, `page` and `path`.", reply, StringComparison.Ordinal);
        // It bound nowhere, so the answer really does still span the second repository.
        Assert.Contains("[two]", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>repo</c> scopes, and the per-line repository tag is the existing signal that it did:
    ///     <c>Append</c> prints the slug only when an answer spans more than one repository, so the tag's
    ///     presence in the unscoped reply is what makes its absence in the scoped one evidence rather
    ///     than a tag nobody prints.
    /// </summary>
    [Fact]
    public async Task Git_log_scopes_to_repo_and_drops_the_per_line_repository_tag()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Mixed);

        string whole = await TestHost.CallAsync(client, "git_log", []);
        Assert.Contains("[one]", whole, StringComparison.Ordinal);
        Assert.Contains("[two]", whole, StringComparison.Ordinal);

        string scoped = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["repo"] = "one" });
        Assert.Contains("5 commits in repository 'one'", scoped, StringComparison.Ordinal);
        Assert.DoesNotContain("[one]", scoped, StringComparison.Ordinal);
        Assert.DoesNotContain("[two]", scoped, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The scope that does exist still scopes while the caveat names the one that does not. The
    ///     caveat speaks only of what it names, so a scoped log keeps its scope and is not introduced
    ///     as unfiltered — the same wrong fact in the other direction.
    /// </summary>
    [Fact]
    public async Task Git_log_keeps_its_scope_while_saying_what_it_ignored()
    {
        await using var client = await _host.ConnectAsync(HistoryFixtures.Mixed);
        string reply = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["repo"] = "one", ["committer"] = "Grace" });

        Assert.StartsWith(Caveat, reply, StringComparison.Ordinal);
        Assert.Contains("5 commits in repository 'one'", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("src/Other.cs", reply, StringComparison.Ordinal);
    }

    private Task<McpClient> StartAsync() => _host.ConnectAsync(HistoryFixtures.Alpha);
}

/// <inheritdoc cref="HistoryFixture" />
public sealed class GitLogFixture : HistoryFixture
{
    protected override IReadOnlyList<string> Projects =>
        [HistoryFixtures.Alpha, HistoryFixtures.Churn, HistoryFixtures.Mixed];
}
