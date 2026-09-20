using System.Globalization;
using System.Net.Http.Json;
using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The history tools as an agent reaches them: <c>git_log</c>, <c>file_history</c> and
///     <c>blame</c>, over MCP and against a real index. None of them matches text, so the engine is
///     pinned to Substring once rather than run twice.
///     What is asserted here is mostly the prose. These tools answer a question an agent will act on —
///     who to ask about a line — and a reply that reads as authorship when it means "last touched" is
///     the failure mode, not a wrong row.
/// </summary>
public sealed class HistoryToolsTests(HistoryToolsFixture fixture) : IClassFixture<HistoryToolsFixture>
{
    private readonly TestHost _host = fixture.Host;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
    ///     <c>author</c> narrows the log, and the header carries the filter: a narrowed log that
    ///     introduces itself as "5 commits" is the wrong fact #86 was about, arrived at from the other
    ///     direction.
    ///     One address whose commits all fit is the whole answer, so nothing is listed under it. The
    ///     line is for what the header cannot say.
    /// </summary>
    [Fact]
    public async Task Git_log_scopes_to_one_author_by_address()
    {
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Mixed);

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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Mixed);

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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Mixed);

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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Mixed);

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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Churn);

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
    ///     The listing `author` is read from, so the address an agent types into the filter is one it
    ///     saw here. Ranked by commits, and scoped the way every other history tool is.
    /// </summary>
    [Fact]
    public async Task Authors_lists_who_committed_with_the_address_each_commits_from()
    {
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Mixed);

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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Churn);

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
        await OnlyRepositoryProjectAsync(_host, "empty",
            new Dictionary<string, string> { ["a.cs"] = "class A;\n" });
        await _host.ExecuteAsync("empty", "DELETE FROM commits");
        await using var client = await _host.ConnectAsync("empty");

        Assert.Equal(ToolReply.NoHistory, await TestHost.CallAsync(client, "authors", []));
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
        await OnlyRepositoryProjectAsync(_host, "blanked",
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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Mixed);
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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Mixed);

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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Mixed);
        string reply = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["repo"] = "one", ["committer"] = "Grace" });

        Assert.StartsWith(Caveat, reply, StringComparison.Ordinal);
        Assert.Contains("5 commits in repository 'one'", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("src/Other.cs", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_history_lists_the_commits_that_changed_one_file()
    {
        var client = await StartAsync();
        string reply = await TestHost.CallAsync(client, "file_history",
            new Dictionary<string, object?> { ["path"] = "one/src/Check.cs" });

        Assert.Contains("2 commits changed one/src/Check.cs", reply, StringComparison.Ordinal);
        Assert.Contains("Ada <ada@example.invalid>", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blame_groups_consecutive_lines_and_names_who_last_changed_them()
    {
        var client = await StartAsync();
        string reply = await TestHost.CallAsync(client, "blame",
            new Dictionary<string, object?> { ["path"] = "one/src/Check.cs" });

        // Line 2 is its own run between two lines the first commit still owns, which is the shape that
        // proves the runs are built from the attribution and not from the file.
        Assert.Contains("Tighten the check", reply, StringComparison.Ordinal);
        Assert.Contains("Grace", reply, StringComparison.Ordinal);
        Assert.Contains("\n2  ", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A miss must not read like a real answer: a path that names no file is a sentence saying so,
    ///     never an empty blame that an agent would take to mean "nobody has ever touched this".
    /// </summary>
    [Fact]
    public async Task An_unknown_path_is_an_answer_and_not_an_empty_result()
    {
        var client = await StartAsync();
        string reply = await TestHost.CallAsync(client, "blame",
            new Dictionary<string, object?> { ["path"] = "one/src/Missing.cs" });

        Assert.Contains("No indexed file", reply, StringComparison.Ordinal);
        Assert.Contains("glob or list_tree", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The distinction the whole tool surface rests on. A project whose index holds no history must
    ///     say so, because "no commits" and "nobody changed this" read identically to an agent and mean
    ///     opposite things (CODING_STANDARDS, Errors).
    /// </summary>
    [Fact]
    public async Task A_project_without_history_says_so_rather_than_answering_nothing()
    {
        await OnlyRepositoryProjectAsync(_host, "beta",
            new Dictionary<string, string> { ["a.cs"] = "class A;\n" });
        // The fixture's own commits are real history, so the tables are emptied to make the index look
        // like one built before history was imported — which is exactly what every existing index is.
        await _host.ExecuteAsync("beta", "DELETE FROM commits");

        await using var client = await _host.ConnectAsync("beta");
        string reply = await TestHost.CallAsync(client, "git_log", []);

        Assert.Contains("holds no history", reply, StringComparison.Ordinal);
        Assert.Contains("Ask the operator to refresh", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The opt-in on the tools that already existed. Default off is the contract: these replies are
    ///     rationed on payload, and attribution on every line of every search would spend that budget on
    ///     a question most searches are not asking.
    /// </summary>
    [Fact]
    public async Task Grep_annotates_lines_only_when_history_is_asked_for()
    {
        var client = await StartAsync();

        string plain = await TestHost.CallAsync(client, "grep",
            new Dictionary<string, object?> { ["query"] = "second-changed" });
        Assert.DoesNotContain("Grace", plain, StringComparison.Ordinal);

        string annotated = await TestHost.CallAsync(client, "grep",
            new Dictionary<string, object?> { ["query"] = "second-changed", ["withHistory"] = true });
        Assert.Contains("Grace", annotated, StringComparison.Ordinal);
        // The code still starts where it did; the attribution is appended, not prepended.
        Assert.Contains("2: second-changed", annotated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_file_adds_one_history_line_per_file_when_asked()
    {
        var client = await StartAsync();

        string reply = await TestHost.CallAsync(client, "read_file",
            new Dictionary<string, object?>
            {
                ["paths"] = Paths("one/src/Check.cs"),
                ["withHistory"] = true
            });

        Assert.Contains("history: since", reply, StringComparison.Ordinal);
        Assert.Contains("last changed", reply, StringComparison.Ordinal);
        Assert.Contains("Grace", reply, StringComparison.Ordinal);
    }

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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Mixed);

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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Mixed);

        using var http = _host.CreateClient();
        var page = await http.GetFromJsonAsync<CommitListResponse>(
            $"/api/projects/{HistoryToolsFixture.Mixed}/commits?repository=one", TestContext.Current.CancellationToken);
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
        await BuildChurnProjectAsync(_host, "gamma", withSecondRepository: true);
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
        await OnlyRepositoryProjectAsync(_host, "solo",
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
        await OnlyRepositoryProjectAsync(_host, "delta",
            new Dictionary<string, string> { ["a.cs"] = "class A;\n" });
        await _host.ExecuteAsync("delta", "DELETE FROM commits");

        await using var client = await _host.ConnectAsync("delta");
        string reply = await TestHost.CallAsync(client, "hot_files", []);

        Assert.Contains("holds no history", reply, StringComparison.Ordinal);
        Assert.Contains("Ask the operator to refresh", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The coupling itself: the files a window's commits kept changing alongside one file, most
    ///     shared commits first. The fixture couples Api.cs to Store.cs more often than to Dto.cs and
    ///     never to Lonely.cs, so a ranking that counted the window's commits rather than the shared
    ///     ones would fail rather than happen to agree.
    /// </summary>
    [Fact]
    public async Task Co_changed_ranks_the_files_that_keep_moving_with_a_file()
    {
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Coupled);
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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Coupled);
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
        await using var wide = await _host.ConnectAsync(HistoryToolsFixture.Coupled);
        string included = await TestHost.CallAsync(wide, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Api.cs", ["days"] = 30 });
        // Under the shipped ceiling the reformat is an ordinary commit, and every path it touched
        // is coupled to Api.cs once — which is what swamps the ranking and what the ceiling is for.
        Assert.Contains("vendor/Bulk01.cs", included, StringComparison.Ordinal);
        Assert.DoesNotContain("left out of the pairing", included, StringComparison.Ordinal);

        using var tight = new TestHost(SearchEngine.Substring, maxCommitPaths: 5);
        await BuildCoupledProjectAsync(tight, HistoryToolsFixture.Coupled);
        await using var client = await tight.ConnectAsync(HistoryToolsFixture.Coupled);
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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Coupled);
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
        await BuildCoupledProjectAsync(tight, "swept");
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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Coupled);
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
        await BuildCoupledProjectAsync(_host, "severed");
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
        await OnlyRepositoryProjectAsync(_host, "epsilon",
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

    /// <summary>
    ///     A commit's own record, which is where the message body is: git_log lists subjects and
    ///     nothing under them, so a subject that says "BugFix 558185" is followed up here or not at all.
    /// </summary>
    [Fact]
    public async Task Commit_answers_one_commits_own_record_with_the_body_git_log_omits()
    {
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Commits);
        string sha = await ShaOfAsync(HistoryToolsFixture.Commits, "BugFix 558185");

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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Commits);

        string reply = await TestHost.CallAsync(client, "commit",
            new Dictionary<string, object?> { ["sha"] = await ShaOfAsync(HistoryToolsFixture.Commits, "Add the module") });

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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Commits);
        string sha = await ShaOfAsync(HistoryToolsFixture.Commits, "BugFix 558185");

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
        await BuildCommitProjectAsync(_host, "twins");
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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Commits);

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
        await BuildCommitProjectAsync(_host, "hollow");
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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Commits);
        var unknown = new Dictionary<string, object?> { ["sha"] = new string('0', 40) };

        string record = await TestHost.CallAsync(client, "commit", unknown);
        string files = await TestHost.CallAsync(client, "commit_files", unknown);

        Assert.Equal(record, files);
        Assert.Contains("No commit '0000000000000000000000000000000000000000'", record, StringComparison.Ordinal);
        Assert.Contains($"project '{HistoryToolsFixture.Commits}'", record, StringComparison.Ordinal);
    }

    /// <summary>
    ///     What a commit touched, which is the question that cost sixty calls of guessing before this
    ///     tool: every path, what the commit did to it, and how much.
    /// </summary>
    [Fact]
    public async Task Commit_files_lists_every_path_with_its_change_kind_and_line_sums()
    {
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Commits);

        string reply = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = await ShaOfAsync(HistoryToolsFixture.Commits, "BugFix 558185") });

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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Commits);

        string reply = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = await ShaOfAsync(HistoryToolsFixture.Commits, "BugFix 558185") });

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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Commits);

        string reply = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = await ShaOfAsync(HistoryToolsFixture.Commits, "Drop the dead file") });

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
        await BuildCommitProjectAsync(_host, "renamed");
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
        await OnlyRepositoryProjectAsync(_host, "onlyone",
            new Dictionary<string, string> { ["src/Widget.cs"] = "class Widget { }\n" }, true);
        await using var client = await _host.ConnectAsync("onlyone");

        using var http = _host.CreateClient();
        var page = await http.GetFromJsonAsync<CommitListResponse>("/api/projects/onlyone/commits",
            TestContext.Current.CancellationToken);
        Assert.NotNull(page);

        string reply = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = page.Commits[0].Sha });

        Assert.Contains("src/Widget.cs", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("only/src/Widget.cs", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     One repository with a commit worth asking about: a message with a body under its subject, a
    ///     file it modified and one it added, and a later commit that deletes the second so a path the
    ///     index no longer holds is in the list.
    /// </summary>
    internal static async Task BuildCommitProjectAsync(TestHost host, string project)
    {
        string name = project + "-one";
        string source = host.CreateEmptyGitRepository(name);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["docs/Note.md"] = "note\n", ["src/Api.cs"] = "a\n" },
            "Add the module", "Ada", "ada@example.invalid", 0);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["src/Api.cs"] = "a2\n", ["src/Gone.cs"] = "gone\n" },
            "BugFix 558185 - tighten the check\n\nThe validator accepted an empty name.\n",
            "Grace", "grace@example.invalid", 1);
        host.RemoveInGitRepositoryAs(name, ["src/Gone.cs"], "Drop the dead file", "Grace",
            "grace@example.invalid", 2);

        await host.CreateProjectAsync(project);
        await host.AddRepositoryAsync(project, "one", source);
        await host.RefreshAsync(project);
    }

    /// <summary>
    ///     The full SHA of the commit whose subject starts with this text, read from the API because
    ///     no tool prints one: the abbreviation is the point of the tests above, not an accident here.
    /// </summary>
    private async Task<string> ShaOfAsync(string project, string subject)
    {
        using var http = _host.CreateClient();
        var page = await http.GetFromJsonAsync<CommitListResponse>($"/api/projects/{project}/commits",
            TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page.Commits.Single(c => c.Subject.StartsWith(subject, StringComparison.Ordinal)).Sha;
    }

    /// <summary>
    ///     A repository with coupling to find. Api.cs moves with Store.cs three times, with Dto.cs
    ///     twice and with Old.cs once before that file is deleted; Lonely.cs moves on its own; and one
    ///     reformat touches Api.cs and a dozen vendored paths at once, which is the mass commit the
    ///     ceiling exists for. Ancient.cs sits ten days before the rest so a narrow window can miss it.
    /// </summary>
    internal static async Task BuildCoupledProjectAsync(TestHost host, string project)
    {
        const int tenDays = 10 * 24 * 60;
        string name = project + "-one";
        string source = host.CreateEmptyGitRepository(name);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["old/Ancient.cs"] = "one\n" },
            "Import the old code", "Ada", "ada@example.invalid", 0);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string>
            {
                ["src/Api.cs"] = "a\n",
                ["src/Dto.cs"] = "d\n",
                ["src/Old.cs"] = "o\n",
                ["src/Store.cs"] = "s\n"
            },
            "Add the feature", "Ada", "ada@example.invalid", tenDays);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["src/Api.cs"] = "a2\n", ["src/Store.cs"] = "s2\n" },
            "Extend the feature", "Grace", "grace@example.invalid", tenDays + 1);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["src/Api.cs"] = "a3\n", ["src/Store.cs"] = "s3\n" },
            "Extend it again", "Grace", "grace@example.invalid", tenDays + 2);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["src/Api.cs"] = "a4\n", ["src/Dto.cs"] = "d2\n" },
            "Reshape the payload", "Grace", "grace@example.invalid", tenDays + 3);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["src/Lonely.cs"] = "l\n" },
            "Add a file nothing moves with", "Ada", "ada@example.invalid", tenDays + 4);

        var reformat = new Dictionary<string, string> { ["src/Api.cs"] = "a5\n" };
        for (int i = 1; i <= 12; i++)
            reformat[string.Create(CultureInfo.InvariantCulture, $"vendor/Bulk{i:00}.cs")] = $"bulk {i}\n";
        host.CommitToGitRepositoryAs(name, reformat, "Reformat everything", "Ada", "ada@example.invalid",
            tenDays + 5);
        host.RemoveInGitRepositoryAs(name, ["src/Old.cs"], "Drop the old file", "Grace",
            "grace@example.invalid", tenDays + 6);

        await host.CreateProjectAsync(project);
        await host.AddRepositoryAsync(project, "one", source);
        await host.RefreshAsync(project);
    }

    /// <summary>Routes an inline array argument through a parameter so CA1861 does not ask for a static field per call.</summary>
    private static string[] Paths(params string[] paths) => paths;

    /// <summary>
    ///     "Who owns this folder" and "what has happened in this folder" are one call each (#118). They
    ///     used to be a file_history per file, because a path argument was accepted and ignored.
    /// </summary>
    [Fact]
    public async Task Authors_and_git_log_scope_to_a_path()
    {
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Churn);

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
        await BuildChurnProjectAsync(_host, "quiet", withSecondRepository: false);
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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Churn);

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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Churn);

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
        await BuildChurnProjectAsync(_host, slug, withSecondRepository: false);
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
        await BuildChurnProjectAsync(_host, slug, withSecondRepository: false);
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
        await BuildChurnProjectAsync(_host, "emptied", withSecondRepository: false);
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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Churn);

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
        await BuildChurnProjectAsync(_host, "moved", withSecondRepository: false);
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
        await BuildChurnProjectAsync(_host, "states", withSecondRepository: false);
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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Churn);

        string read = await TestHost.CallAsync(client, "read_file",
            new Dictionary<string, object?> { ["paths"] = Paths("one/src/Gone.cs") });
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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Mixed);

        string reply = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["repo"] = "two", ["path"] = "one/src" });

        Assert.Contains("name different repositories", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("No commits", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A ranking counts machine-authored commits exactly like hand-written ones, so a directory of
    ///     regenerated output outranks the code that drives it (#117). No suffix list is hard-coded
    ///     anywhere, so the filter is the caller's and the reply has to say it ran.
    /// </summary>
    [Fact]
    public async Task Hot_files_takes_an_exclude_filter_and_says_how_much_it_hid()
    {
        await BuildGeneratedProjectAsync(_host, "generated");
        await using var client = await _host.ConnectAsync("generated");

        string unfiltered = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30 });
        // The distortion, before anything is done about it: the generated file wins.
        Assert.True(unfiltered.IndexOf("one/src/Model.g.cs", StringComparison.Ordinal)
                    < unfiltered.IndexOf("one/src/Service.cs", StringComparison.Ordinal));
        Assert.DoesNotContain("exclude hid", unfiltered, StringComparison.Ordinal);

        string filtered = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30, ["exclude"] = "*.g.cs,/migrations/" });
        Assert.DoesNotContain("Model.g.cs", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("migrations", filtered, StringComparison.Ordinal);
        Assert.Contains("one/src/Service.cs", filtered, StringComparison.Ordinal);
        // An excluded ranking must never read as an unfiltered one.
        Assert.Contains("exclude hid 2 paths, so this is a filtered ranking", filtered,
            StringComparison.Ordinal);

        // The rollup reads the same scope, so it hides the same files rather than answering differently.
        string rolled = await TestHost.CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["days"] = 30, ["depth"] = 2, ["exclude"] = "/migrations/" });
        Assert.DoesNotContain("one/migrations", rolled, StringComparison.Ordinal);
        Assert.Contains("exclude hid 1 path", rolled, StringComparison.Ordinal);
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
        await BuildRoutedProjectAsync(_host, "routed");
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

    /// <summary>
    ///     The cutover as a real one arrives: one commit of many `renamed` rows, content unchanged
    ///     (#131). Every path-scoped tool scoped to the new name must say what the old name was, how
    ///     many commits the whole chain accounts for, and how to read the old name — and must go on
    ///     counting only what its own path recorded, because renames are signalled and not followed.
    /// </summary>
    [Fact]
    public async Task A_directory_renamed_in_one_commit_is_signalled_by_every_path_scoped_tool()
    {
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Renames);

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
        await BuildRenamedProjectAsync(_host, "quietened");
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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Renames);

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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Renames);

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
        await BuildRenamedProjectAsync(_host, "twice");
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
        await using var client = await _host.ConnectAsync(HistoryToolsFixture.Renames);

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
        await BuildRenamedProjectAsync(_host, "stray");
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
    ///     A cutover: `model/` built up over several commits, then moved to `src/Model` wholesale in one,
    ///     beside an `src/Api` that never moved and an `src/Odds` that one file later leaves.
    /// </summary>
    internal static async Task BuildRenamedProjectAsync(TestHost host, string project)
    {
        string source = host.CreateEmptyGitRepository(project + "-one");
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string>
            {
                ["model/Contact.cs"] = "contact\n",
                ["model/Order.cs"] = "order\n",
                ["src/Api/Handler.cs"] = "handler\n",
                ["src/Odds/Helper.cs"] = "helper\n"
            },
            "Import the model", "Ada", "ada@example.invalid", 30000);
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["model/Contact.cs"] = "contact\nmore\n" },
            "Add the contact feature", "Ada", "ada@example.invalid", 30001);
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["model/Order.cs"] = "order\nmore\n" },
            "Extend the order", "Grace", "grace@example.invalid", 30002);
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["src/Api/Handler.cs"] = "handler\nmore\n" },
            "Tighten the handler", "Grace", "grace@example.invalid", 30003);
        // Alone in its own commit, so the only commit it ever shares with Contact.cs is the cutover.
        // It is what says the rename is excluded from the pairing rather than merely diluted: a file
        // that moved beside the anchor and nothing more must not rank as coupled to it.
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["model/Solo.cs"] = "solo\n" },
            "Add the solo model", "Ada", "ada@example.invalid", 30004);
        // The cutover, as the real one arrived: one commit, every path renamed, no content changed.
        host.MoveInGitRepositoryAs(project + "-one",
            new Dictionary<string, string>
            {
                ["model/Contact.cs"] = "src/Model/Contact.cs",
                ["model/Order.cs"] = "src/Model/Order.cs",
                ["model/Solo.cs"] = "src/Model/Solo.cs"
            },
            "Merged PR 39331: Moved Model to src\\Model", "Grace", "grace@example.invalid", 31000);

        await host.CreateProjectAsync(project);
        await host.AddRepositoryAsync(project, "one", source);
        await host.RefreshAsync(project);
    }

    /// <summary>A repository whose regenerated output and migrations outrank its hand-written code.</summary>
    private static async Task BuildGeneratedProjectAsync(TestHost host, string project)
    {
        const int tenDays = 10 * 24 * 60;
        string name = project + "-one";
        string source = host.CreateEmptyGitRepository(name);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string>
            {
                ["src/Service.cs"] = "a\n",
                ["src/Model.g.cs"] = "g\n",
                ["migrations/0001.sql"] = "create table t (id integer);\n"
            },
            "Add the module", "Ada", "ada@example.invalid", tenDays);
        // Two regenerations and one migration against one hand-written change: the shape the ranking
        // gets wrong, and it gets it wrong by counting correctly.
        for (int i = 1; i <= 3; i++)
            host.CommitToGitRepositoryAs(name,
                new Dictionary<string, string>
                {
                    ["src/Model.g.cs"] = $"g{i}\n",
                    ["migrations/0001.sql"] = $"create table t (id integer, c{i} integer);\n"
                },
                "Regenerate", "Ada", "ada@example.invalid", tenDays + i);
        host.CommitToGitRepositoryAs(name, new Dictionary<string, string> { ["src/Service.cs"] = "a2\n" },
            "Change the service", "Grace", "grace@example.invalid", tenDays + 4);

        await host.CreateProjectAsync(project);
        await host.AddRepositoryAsync(project, "one", source);
        await host.RefreshAsync(project);
    }

    /// <summary>A repository with a route directory and the sibling its character class would match.</summary>
    private static async Task BuildRoutedProjectAsync(TestHost host, string project)
    {
        const int tenDays = 10 * 24 * 60;
        string name = project + "-one";
        string source = host.CreateEmptyGitRepository(name);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string>
            {
                ["app/[slug]/page.tsx"] = "export default function Page() {}\n",
                ["app/s/page.tsx"] = "export default function S() {}\n"
            },
            "Add the routes", "Ada", "ada@example.invalid", tenDays);
        // The sibling churns harder, so a scope that took it in would rank it first and look right.
        for (int i = 1; i <= 3; i++)
            host.CommitToGitRepositoryAs(name,
                new Dictionary<string, string> { ["app/s/page.tsx"] = $"export default function S{i}() {{}}\n" },
                "Work on the sibling", "Grace", "grace@example.invalid", tenDays + i);

        await host.CreateProjectAsync(project);
        await host.AddRepositoryAsync(project, "one", source);
        await host.RefreshAsync(project);
    }

    /// <summary>
    ///     A repository with something to rank: one old commit, then four ten days later that touch
    ///     three files unequally, then one that deletes a fourth. Every date is decades before today, so
    ///     a window measured from the clock rather than from the history would rank nothing at all.
    /// </summary>
    private Task<McpClient> ChurnAsync() => _host.ConnectAsync(HistoryToolsFixture.Churn);

    /// <inheritdoc cref="ChurnAsync" />
    internal static async Task BuildChurnProjectAsync(TestHost host, string project, bool withSecondRepository)
    {
        const int tenDays = 10 * 24 * 60;
        string source = host.CreateEmptyGitRepository(project + "-one");
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["old/Ancient.cs"] = "one\n" },
            "Import the old code", "Ada", "ada@example.invalid", 0);
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string>
            {
                ["docs/Note.md"] = "note\n",
                ["src/Cold.cs"] = "cold\n",
                ["src/Gone.cs"] = "gone\n",
                ["src/Hot.cs"] = "a\nb\nc\n"
            },
            "Add the module", "Ada", "ada@example.invalid", tenDays);
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["src/Hot.cs"] = "a\nb2\nc\n" },
            "Fix the check", "Grace", "grace@example.invalid", tenDays + 1);
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["src/Hot.cs"] = "a\nb3\nc\n", ["src/Cold.cs"] = "cold2\n" },
            "Tighten it again", "Grace", "grace@example.invalid", tenDays + 2);
        host.RemoveInGitRepositoryAs(project + "-one", ["src/Gone.cs"], "Drop the dead file", "Grace",
            "grace@example.invalid", tenDays + 3);

        await host.CreateProjectAsync(project);
        await host.AddRepositoryAsync(project, "one", source);
        if (withSecondRepository)
            await host.AddRepositoryAsync(project, "two",
                host.CreateGitRepository(project + "-two", new Dictionary<string, string>
                    { ["src/Other.cs"] = "other\n" }));
        await host.RefreshAsync(project);
    }

    private Task<McpClient> StartAsync() => _host.ConnectAsync(HistoryToolsFixture.Alpha);

    /// <summary>
    ///     A project of one repository called <c>only</c>, which is what the tests about an index with
    ///     no history are built on. <c>TestHost.IndexedProjectAsync</c> would do it, but it names the
    ///     fixture directory after the repository slug, and a slug is unique only within its project
    ///     while the fixture directory is shared by every project on the host. Seven tests here ask for
    ///     a repository called <c>only</c>, which was seven directories while each had a host of its
    ///     own and is one now: the second commit into it is "no changes; nothing to commit". The slug
    ///     stays <c>only</c>, because the qualified paths these tests assert on are spelled with it.
    /// </summary>
    private static async Task OnlyRepositoryProjectAsync(TestHost host, string project,
        Dictionary<string, string> files, bool singleRepository = false)
    {
        await host.CreateProjectAsync(project, singleRepository);
        await host.AddRepositoryAsync(project, "only", host.CreateGitRepository($"{project}-only", files));
        await host.RefreshAsync(project);
    }
}

/// <summary>
///     The one server this class runs against, and the projects more than one of its tests reads.
///     Both are built once. The class used to build a whole server per test — xunit constructs the
///     test class for every <c>[Fact]</c>, so <c>new TestHost(...)</c> ran 87 times — and then a git
///     repository and an index on top of it. Measured on this machine: the first client costs 265ms
///     because that is where the host actually boots, and a project on an already-booted host costs
///     430ms against 1150ms on a cold one. Nothing about what the tests assert needed either.
///     A project is shared only where every test that reads it is a reader. The history tools answer
///     from the index and write nothing, so most are; the ones that hollow a commit out with SQL, or
///     refresh again over new commits, still build a project of their own, on this host rather than
///     on a server of their own. CODING_STANDARDS asks for a temp directory and a real database file
///     per test <em>class</em>, which is what this now is, rather than the per-test one it had drifted
///     into.
/// </summary>
public sealed class HistoryToolsFixture : IAsyncLifetime
{
    /// <summary>One repository, two commits, one file changed by the second. The plainest log there is.</summary>
    public const string Alpha = "alpha";

    /// <summary>Something to rank: one ancient commit, four ten days later, one deletion.</summary>
    public const string Churn = "churn";

    /// <summary><see cref="Churn" /> with a second repository, for the answers that span both.</summary>
    public const string Mixed = "mixed";

    /// <summary>Three commits with a body, a rename and a deletion: what <c>commit</c> is asked about.</summary>
    public const string Commits = "commits";

    /// <summary>Files that move together, one that moves alone, and one mass commit.</summary>
    public const string Coupled = "coupled";

    /// <summary>A directory cutover: every path renamed in one commit, no content changed.</summary>
    public const string Renames = "renames";

    public TestHost Host { get; } = new(SearchEngine.Substring);

    public async ValueTask InitializeAsync()
    {
        string source = Host.CreateEmptyGitRepository("one");
        Host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Check.cs"] = "first\nsecond\nthird\n" },
            "Add the validator", "Ada", "ada@example.invalid", 0);
        Host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Check.cs"] = "first\nsecond-changed\nthird\n" },
            "Tighten the check", "Grace", "grace@example.invalid", 1);
        await Host.CreateProjectAsync(Alpha);
        await Host.AddRepositoryAsync(Alpha, "one", source);
        await Host.RefreshAsync(Alpha);

        await HistoryToolsTests.BuildChurnProjectAsync(Host, Churn, false);
        await HistoryToolsTests.BuildChurnProjectAsync(Host, Mixed, true);
        await HistoryToolsTests.BuildCommitProjectAsync(Host, Commits);
        await HistoryToolsTests.BuildCoupledProjectAsync(Host, Coupled);
        await HistoryToolsTests.BuildRenamedProjectAsync(Host, Renames);
    }

    public ValueTask DisposeAsync()
    {
        Host.Dispose();
        return ValueTask.CompletedTask;
    }
}
