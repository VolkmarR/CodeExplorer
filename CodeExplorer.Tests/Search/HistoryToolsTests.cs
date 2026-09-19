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
public sealed class HistoryToolsTests : IDisposable
{
    private readonly TestHost _host = new(SearchEngine.Substring);

    /// <summary>
    ///     What a call carrying one name git_log does not have is read with, whatever the answer under
    ///     it turns out to be. Written once because the point of the assertions below is that the four
    ///     outcomes are introduced by the same sentence: one that varied with the answer would be
    ///     telling an agent something about a log that may not be there.
    /// </summary>
    private const string Caveat =
        "`git_log` has no `committer` argument; it was ignored. "
        + "It takes `repo`, `author`, `message`, `limit` and `page`.";

    public void Dispose() => _host.Dispose();

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
        await BuildChurnProjectAsync("mixed", true);
        await using var client = await _host.ConnectAsync("mixed");

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
        await BuildChurnProjectAsync("mixed", true);
        await using var client = await _host.ConnectAsync("mixed");

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
        await BuildChurnProjectAsync("tickets", true);
        await using var client = await _host.ConnectAsync("tickets");

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
        await BuildChurnProjectAsync("both", true);
        await using var client = await _host.ConnectAsync("both");

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
        await BuildChurnProjectAsync("miss", false);
        await using var client = await _host.ConnectAsync("miss");

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
        await BuildChurnProjectAsync("mixed", true);
        await using var client = await _host.ConnectAsync("mixed");

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
        await BuildChurnProjectAsync("mixed", false);
        await using var client = await _host.ConnectAsync("mixed");

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
        await _host.IndexedProjectAsync("empty",
            new Dictionary<string, Dictionary<string, string>> { ["only"] = new() { ["a.cs"] = "class A;\n" } });
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
            + "It takes `repo`, `author`, `message`, `limit` and `page`.", reply, StringComparison.Ordinal);
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
        await _host.IndexedProjectAsync("empty",
            new Dictionary<string, Dictionary<string, string>> { ["only"] = new() { ["a.cs"] = "class A;\n" } });
        await _host.ExecuteAsync("empty", "DELETE FROM commits");
        await using var bare = await _host.ConnectAsync("empty");
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
        await BuildChurnProjectAsync("mixed", true);
        await using var client = await _host.ConnectAsync("mixed");
        string reply = await TestHost.CallAsync(client, "git_log",
            new Dictionary<string, object?> { ["repository"] = "one" });

        Assert.StartsWith(
            "`git_log` has no `repository` argument; it was ignored. "
            + "It takes `repo`, `author`, `message`, `limit` and `page`.", reply, StringComparison.Ordinal);
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
        await BuildChurnProjectAsync("mixed", true);
        await using var client = await _host.ConnectAsync("mixed");

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
        await BuildChurnProjectAsync("mixed", true);
        await using var client = await _host.ConnectAsync("mixed");
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
        await _host.IndexedProjectAsync("beta",
            new Dictionary<string, Dictionary<string, string>>
            {
                ["only"] = new() { ["a.cs"] = "class A;\n" }
            });
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
        await BuildChurnProjectAsync("rolled", withSecondRepository: true);
        await using var client = await _host.ConnectAsync("rolled");

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
        await BuildChurnProjectAsync("agree", true);
        await using var client = await _host.ConnectAsync("agree");

        using var http = _host.CreateClient();
        var page = await http.GetFromJsonAsync<CommitListResponse>("/api/projects/agree/commits?repository=one", TestContext.Current.CancellationToken);
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
        await BuildChurnProjectAsync("gamma", withSecondRepository: true);
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
        await _host.IndexedProjectAsync("solo", new Dictionary<string, Dictionary<string, string>>
            { ["only"] = new() { ["src/Widget.cs"] = "class Widget { }\n" } }, true);

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
        await _host.IndexedProjectAsync("delta",
            new Dictionary<string, Dictionary<string, string>>
            {
                ["only"] = new() { ["a.cs"] = "class A;\n" }
            });
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
        await BuildCoupledProjectAsync(_host, "coupled");
        await using var client = await _host.ConnectAsync("coupled");
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
        await BuildCoupledProjectAsync(_host, "gone");
        await using var client = await _host.ConnectAsync("gone");
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
        await BuildCoupledProjectAsync(_host, "bulk");
        await using var wide = await _host.ConnectAsync("bulk");
        string included = await TestHost.CallAsync(wide, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Api.cs", ["days"] = 30 });
        // Under the shipped ceiling the reformat is an ordinary commit, and every path it touched
        // is coupled to Api.cs once — which is what swamps the ranking and what the ceiling is for.
        Assert.Contains("vendor/Bulk01.cs", included, StringComparison.Ordinal);
        Assert.DoesNotContain("left out of the pairing", included, StringComparison.Ordinal);

        using var tight = new TestHost(SearchEngine.Substring, maxCommitPaths: 5);
        await BuildCoupledProjectAsync(tight, "bulk");
        await using var client = await tight.ConnectAsync("bulk");
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
        await BuildCoupledProjectAsync(_host, "alone");
        await using var client = await _host.ConnectAsync("alone");
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/src/Lonely.cs", ["days"] = 30 });

        Assert.Contains("No other file", reply, StringComparison.Ordinal);
        Assert.Contains("one/src/Lonely.cs", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A window that reaches none of a file's commits is not a file that moves alone, and the two
    ///     answers have to read differently or the shorter window teaches the agent a wrong fact.
    /// </summary>
    [Fact]
    public async Task Co_changed_says_when_the_window_reached_none_of_a_files_commits()
    {
        await BuildCoupledProjectAsync(_host, "narrow");
        await using var client = await _host.ConnectAsync("narrow");
        string reply = await TestHost.CallAsync(client, "co_changed",
            new Dictionary<string, object?> { ["path"] = "one/old/Ancient.cs", ["days"] = 1 });

        Assert.Contains("No commit", reply, StringComparison.Ordinal);
        Assert.Contains("raise days", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Co_changed_on_a_project_without_history_says_so_rather_than_pairing_nothing()
    {
        await _host.IndexedProjectAsync("epsilon",
            new Dictionary<string, Dictionary<string, string>>
            {
                ["only"] = new() { ["a.cs"] = "class A;\n" }
            });
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
        await BuildCommitProjectAsync("touched");
        await using var client = await _host.ConnectAsync("touched");
        string sha = await ShaOfAsync("touched", "BugFix 558185");

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
        await BuildCommitProjectAsync("bodyless");
        await using var client = await _host.ConnectAsync("bodyless");

        string reply = await TestHost.CallAsync(client, "commit",
            new Dictionary<string, object?> { ["sha"] = await ShaOfAsync("bodyless", "Add the module") });

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
        await BuildCommitProjectAsync("short");
        await using var client = await _host.ConnectAsync("short");
        string sha = await ShaOfAsync("short", "BugFix 558185");

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
        await BuildCommitProjectAsync("twins");
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
        await BuildCommitProjectAsync("stub");
        await using var client = await _host.ConnectAsync("stub");

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
        await BuildCommitProjectAsync("hollow");
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
        await BuildCommitProjectAsync("absent");
        await using var client = await _host.ConnectAsync("absent");
        var unknown = new Dictionary<string, object?> { ["sha"] = new string('0', 40) };

        string record = await TestHost.CallAsync(client, "commit", unknown);
        string files = await TestHost.CallAsync(client, "commit_files", unknown);

        Assert.Equal(record, files);
        Assert.Contains("No commit '0000000000000000000000000000000000000000'", record, StringComparison.Ordinal);
        Assert.Contains("project 'absent'", record, StringComparison.Ordinal);
    }

    /// <summary>
    ///     What a commit touched, which is the question that cost sixty calls of guessing before this
    ///     tool: every path, what the commit did to it, and how much.
    /// </summary>
    [Fact]
    public async Task Commit_files_lists_every_path_with_its_change_kind_and_line_sums()
    {
        await BuildCommitProjectAsync("paths");
        await using var client = await _host.ConnectAsync("paths");

        string reply = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = await ShaOfAsync("paths", "BugFix 558185") });

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
    ///     A path the commit deleted is named and marked, never dropped and never offered as something
    ///     to open: an agent sent to read a file that is not there has been told something false, and
    ///     the mark is the one every ranking drawn from history already uses.
    /// </summary>
    [Fact]
    public async Task Commit_files_marks_a_path_that_is_no_longer_at_head()
    {
        await BuildCommitProjectAsync("gone");
        await using var client = await _host.ConnectAsync("gone");

        string reply = await TestHost.CallAsync(client, "commit_files",
            new Dictionary<string, object?> { ["sha"] = await ShaOfAsync("gone", "Drop the dead file") });

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
        await BuildCommitProjectAsync("renamed");
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
        await _host.IndexedProjectAsync("onlyone", new Dictionary<string, Dictionary<string, string>>
            { ["only"] = new() { ["src/Widget.cs"] = "class Widget { }\n" } }, true);
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
    private async Task BuildCommitProjectAsync(string project)
    {
        string name = project + "-one";
        string source = _host.CreateEmptyGitRepository(name);
        _host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["docs/Note.md"] = "note\n", ["src/Api.cs"] = "a\n" },
            "Add the module", "Ada", "ada@example.invalid", 0);
        _host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["src/Api.cs"] = "a2\n", ["src/Gone.cs"] = "gone\n" },
            "BugFix 558185 - tighten the check\n\nThe validator accepted an empty name.\n",
            "Grace", "grace@example.invalid", 1);
        _host.RemoveInGitRepositoryAs(name, ["src/Gone.cs"], "Drop the dead file", "Grace",
            "grace@example.invalid", 2);

        await _host.CreateProjectAsync(project);
        await _host.AddRepositoryAsync(project, "one", source);
        await _host.RefreshAsync(project);
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
    private static async Task BuildCoupledProjectAsync(TestHost host, string project)
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
    ///     A repository with something to rank: one old commit, then four ten days later that touch
    ///     three files unequally, then one that deletes a fourth. Every date is decades before today, so
    ///     a window measured from the clock rather than from the history would rank nothing at all.
    /// </summary>
    private async Task<McpClient> ChurnAsync()
    {
        await BuildChurnProjectAsync("churn", false);
        return await _host.ConnectAsync("churn");
    }

    /// <inheritdoc cref="ChurnAsync" />
    private async Task BuildChurnProjectAsync(string project, bool withSecondRepository)
    {
        const int tenDays = 10 * 24 * 60;
        string source = _host.CreateEmptyGitRepository(project + "-one");
        _host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["old/Ancient.cs"] = "one\n" },
            "Import the old code", "Ada", "ada@example.invalid", 0);
        _host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string>
            {
                ["docs/Note.md"] = "note\n",
                ["src/Cold.cs"] = "cold\n",
                ["src/Gone.cs"] = "gone\n",
                ["src/Hot.cs"] = "a\nb\nc\n"
            },
            "Add the module", "Ada", "ada@example.invalid", tenDays);
        _host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["src/Hot.cs"] = "a\nb2\nc\n" },
            "Fix the check", "Grace", "grace@example.invalid", tenDays + 1);
        _host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["src/Hot.cs"] = "a\nb3\nc\n", ["src/Cold.cs"] = "cold2\n" },
            "Tighten it again", "Grace", "grace@example.invalid", tenDays + 2);
        _host.RemoveInGitRepositoryAs(project + "-one", ["src/Gone.cs"], "Drop the dead file", "Grace",
            "grace@example.invalid", tenDays + 3);

        await _host.CreateProjectAsync(project);
        await _host.AddRepositoryAsync(project, "one", source);
        if (withSecondRepository)
            await _host.AddRepositoryAsync(project, "two",
                _host.CreateGitRepository(project + "-two", new Dictionary<string, string>
                    { ["src/Other.cs"] = "other\n" }));
        await _host.RefreshAsync(project);
    }

    private async Task<McpClient> StartAsync()
    {
        string source = _host.CreateEmptyGitRepository("one");
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Check.cs"] = "first\nsecond\nthird\n" },
            "Add the validator", "Ada", "ada@example.invalid", 0);
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Check.cs"] = "first\nsecond-changed\nthird\n" },
            "Tighten the check", "Grace", "grace@example.invalid", 1);

        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "one", source);
        await _host.RefreshAsync("alpha");
        return await _host.ConnectAsync("alpha");
    }
}
