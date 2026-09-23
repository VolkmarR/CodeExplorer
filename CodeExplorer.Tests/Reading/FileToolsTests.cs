using LibGit2Sharp;
using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The index-backed tools that are not searches (#6): <c>read_file</c>, <c>glob</c>,
///     <c>list_tree</c>, <c>list_extensions</c> and <c>repo_info</c>. None of them matches text, so the
///     engine is pinned to Substring once rather than run twice.
/// </summary>
public sealed class FileToolsTests(FileToolsFixture fixture) : IClassFixture<FileToolsFixture>
{
    private const string Orders =
        "class Orders\n{\n    void Needle() {}\n    // needle in a comment\n    int Count;\n}\n";

    /// <summary>
    ///     One repository, for the project shape ADR-0006 names files in without a slug. Its slug is
    ///     the one the slug-prefix tests write in front of a path, and nothing in it is called `one`,
    ///     so a path that reads as a directory called `one` can only be that mistake.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> OneRepository = new()
    {
        ["one"] = new Dictionary<string, string>
        {
            ["src/Orders.cs"] = Orders,
            ["README.md"] = "the only repository\n"
        }
    };

    internal static readonly Dictionary<string, Dictionary<string, string>> TwoRepositories = new()
    {
        ["one"] = new Dictionary<string, string>
        {
            ["src/Orders.cs"] = Orders,
            ["src/Orders.g.cs"] = "// generated\nvoid Needle() {}\n",
            ["README.md"] = "first repository\n",
            // A NUL byte makes libgit2 classify the blob as binary, so the index keeps the row without lines.
            ["assets/logo.bin"] = "\0\0binary"
        },
        ["two"] = new Dictionary<string, string>
        {
            ["lib/index.ts"] = "export function needle() {}\nexport const haystack = 1;\n",
            ["lib/util.ts"] = "export const util = 2;\n",
            ["Makefile"] = "all:\n\techo hi\n"
        }
    };

    /// <summary>
    ///     A repository written to exercise <c>list_declarations</c> and nothing else: a covered
    ///     language that declares names, one that declares none, a covered language whose declarations
    ///     cannot be read from a line at all, an extension no profile covers, and a unit with the
    ///     declaration/implementation split. Its own fixture rather than more files in
    ///     <see cref="TwoRepositories" />, whose counts half the assertions above are written against.
    /// </summary>
    internal static readonly Dictionary<string, Dictionary<string, string>> Declaring = new()
    {
        ["one"] = new Dictionary<string, string>
        {
            ["src/Orders.cs"] =
                "namespace Shop;\n\nclass Orders\n{\n    public void Save() { }\n    // public void Removed() { }\n}\n",
            ["src/Quiet.cs"] = "// a file of comments\n// and nothing else\n",
            ["src/site.css"] = ".header { color: red; }\n",
            ["notes.md"] = "# Title\n\nProse about the orders, not code.\n",
            ["src/Customers.pas"] =
                "unit Customers;\n\ninterface\n\ntype\n  TCustomer = class(TObject)\n    procedure Save;\n  end;\n\nimplementation\n\nprocedure TCustomer.Save;\nbegin\nend;\n\nend.\n"
        }
    };

    /// <summary>
    ///     One repository with more files than <see cref="IndexReader.MaxFiles" />, in three directories,
    ///     so a glob can be run past the page both within reach of a higher <c>limit</c> and far beyond
    ///     it. The files are one line each: what is under test is the count and the reply around it.
    /// </summary>
    internal static readonly Dictionary<string, Dictionary<string, string>> Wide = new()
    {
        ["radix"] = Enumerable.Range(0, 2100).ToDictionary(
            i => $"src/g{i / 700}/f{i:0000}.cs", i => $"class F{i};\n")
    };

    private readonly TestHost _host = fixture.Host;

    [Fact]
    public async Task Read_file_returns_numbered_content_and_honours_line_ranges()
    {
        await using var client = await StartAsync();

        string whole = await ReadAsync(client, "one/src/Orders.cs");
        Assert.Contains("one/src/Orders.cs  -  6 lines", whole);
        Assert.Contains("1  class Orders", whole);
        Assert.Contains("6  }", whole);

        string range = await ReadAsync(client, "one/src/Orders.cs:3-4");
        Assert.Contains("(lines 3-4 of 6)", range);
        Assert.Contains("3      void Needle() {}", range);
        Assert.Contains("4      // needle in a comment", range);
        Assert.DoesNotContain("class Orders", range);
        Assert.DoesNotContain("Count", range);

        string open = await ReadAsync(client, "one/src/Orders.cs:5");
        Assert.Contains("5      int Count;", open);
        Assert.DoesNotContain("Needle", open);

        string defaults = await CallAsync(client, "read_file",
            new Dictionary<string, object?>
                { ["paths"] = Paths("one/src/Orders.cs", "two/lib/index.ts"), ["startLine"] = 2, ["maxLines"] = 1 });
        Assert.Contains("2  {", defaults);
        Assert.DoesNotContain("class Orders", defaults);
        Assert.Contains("2  export const haystack = 1;", defaults);
        Assert.Contains("Continue with \"one/src/Orders.cs:3\"", defaults);

        string past = await ReadAsync(client, "one/src/Orders.cs:40");
        Assert.Contains("only 6 lines", past);
        Assert.Contains("startLine 40 is past the end", past);
    }

    [Fact]
    public async Task Read_file_explains_a_missing_path_by_naming_what_was_searched()
    {
        await using var client = await StartAsync();

        string missing = await ReadAsync(client, "one/src/Nope.cs");
        Assert.Contains("No indexed file 'one/src/Nope.cs' in repository 'one' of project 'alpha'", missing);
        Assert.Contains("glob", missing);

        string wrongRepo = await ReadAsync(client, "three/src/Orders.cs");
        Assert.Contains("No repository 'three' in project 'alpha'", wrongRepo);
        Assert.Contains("one, two", wrongRepo);

        // The leaf name exists elsewhere: say where, so a wrong directory costs one call, not a glob.
        string wrongDir = await ReadAsync(client, "one/Orders.cs");
        Assert.Contains("Did you mean one/src/Orders.cs", wrongDir);

        string binary = await ReadAsync(client, "one/assets/logo.bin");
        Assert.Contains("one/assets/logo.bin", binary);
        Assert.Contains("not indexed: binary", binary);

        string colon = await ReadAsync(client, "one/src/Orders.cs:1:3");
        Assert.Contains("dash", colon);
        Assert.Contains("one/src/Orders.cs:1-3", colon);

        string noRepo = await ReadAsync(client, "Orders.cs");
        Assert.Contains("must start with a repository slug", noRepo);

        // The slug is matched like the rest of the path: case-insensitively, answering with the committed spelling.
        string wrongCase = await ReadAsync(client, "One/SRC/orders.cs:1-1");
        Assert.Contains("one/src/Orders.cs  -  6 lines", wrongCase);

        string inverted = await ReadAsync(client, "one/src/Orders.cs:4-2");
        Assert.Contains("ends before it starts", inverted);
        Assert.Contains("one/src/Orders.cs:2-4", inverted);
        Assert.DoesNotContain("class Orders", inverted);
    }

    /// <summary>
    ///     A malformed entry is that entry's answer and not the call's: the reply used to be the first
    ///     refusal alone, so an agent that mistyped one of five ranges lost the four reads it had asked
    ///     for and could not see which entry was the bad one.
    /// </summary>
    [Fact]
    public async Task Read_file_answers_a_malformed_entry_per_entry()
    {
        await using var client = await StartAsync();

        string mixed = await ReadAsync(client, "one/src/Orders.cs:1:3", "one/src/Orders.cs:1-1",
            "two/lib/index.ts:4-2");
        Assert.Contains("dash", mixed);
        Assert.Contains("one/src/Orders.cs  -  6 lines", mixed);
        Assert.Contains("ends before it starts", mixed);

        // Nothing well-formed to read is still an answer per entry, and no query at all.
        string none = await ReadAsync(client, "one/src/Orders.cs:1:3", "two/lib/index.ts:4-2");
        Assert.Contains("dash", none);
        Assert.Contains("ends before it starts", none);

        // Having asked for nothing is the request's refusal and not an entry's, so it stays the query
        // module's to word: no entries means no refusals to list, and an empty reply would say nothing.
        string nothing = await ReadAsync(client);
        Assert.Contains("No paths given", nothing);
    }

    /// <summary>
    ///     The mistake three agents in the Edilverso evaluation made: a path prefixed with the slug the
    ///     project is known by, in a project that names its files without one. It parses as a directory,
    ///     misses, and the miss then names the repository the agent thought it was addressing — which
    ///     reads as confirmation that the prefix was right. Both tools that resolve a path say what
    ///     actually happened and print the path that works.
    /// </summary>
    [Fact]
    public async Task A_path_prefixed_with_the_slug_is_diagnosed_in_a_single_repository_project()
    {
        // Its own server: OneRepository names its repository `one`, which the shared fixture has
        // already committed under that name, and a fixture directory belongs to the host.
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", OneRepository, singleRepository: true);
        await using var client = await host.ConnectAsync("alpha");

        // 'alpha' is both slugs at once: the one repository of such a project is named after the
        // project (ADR-0006), so the slug read off a project list and the slug read off a path are the
        // same string, and one prefix is both halves of the mistake.
        string file = await ReadAsync(client, "alpha/src/Orders.cs");
        Assert.Contains("'alpha' is the repository, not a directory in it", file);
        Assert.Contains("The path is 'src/Orders.cs'.", file);
        // Refused and not quietly accepted: two spellings for one path is what ADR-0006 argues against,
        // and an agent corrected once stops sending the other one.
        Assert.DoesNotContain("class Orders", file);

        string tree = await ListTreeAsync(client, "alpha/src", 1);
        Assert.Contains("'alpha' is the repository, not a directory in it", tree);
        Assert.Contains("The path is 'src'.", tree);

        // A file named as a directory is the same mistake twice over, and the slug is the half the
        // "that is a file" sentence cannot explain on its own.
        string fileAsTree = await ListTreeAsync(client, "alpha/README.md", 1);
        Assert.Contains("'alpha' is the repository, not a directory in it", fileAsTree);
        Assert.Contains("The path is 'README.md'.", fileAsTree);

        // hot_files resolves a directory too, and an unexplained empty ranking there reads as "nothing
        // changed in this module" — a fact, and the wrong one.
        string churn = await CallAsync(client, "hot_files",
            new Dictionary<string, object?> { ["directory"] = "alpha/src" });
        Assert.Contains("'alpha' is the repository, not a directory in it", churn);
        Assert.Contains("The path is 'src'.", churn);
    }

    /// <summary>
    ///     What the diagnosis must not do: fire where the first segment really is a repository, and
    ///     fire where the path is simply wrong. Both would teach an agent a rule this project does not
    ///     have — the first that its own qualified paths are malformed, the second that a typo is a
    ///     naming mistake — so a miss that is not this mistake keeps every word it had.
    /// </summary>
    [Fact]
    public async Task The_slug_diagnosis_stays_out_of_a_multi_repository_project_and_out_of_an_ordinary_miss()
    {
        await using var client = await StartAsync();

        // 'one' is a repository here, so the path is right in shape and wrong only in its leaf.
        string real = await ReadAsync(client, "one/src/Nope.cs");
        Assert.Contains("No indexed file 'one/src/Nope.cs' in repository 'one' of project 'alpha'", real);
        Assert.DoesNotContain("is the repository, not a directory in it", real);

        // Its own server, for the reason the single-repository test above takes one.
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync("beta", OneRepository, singleRepository: true);
        await using var single = await host.ConnectAsync("beta");

        // A path with no slug prefix keeps today's wording, "did you mean" and all.
        string typo = await ReadAsync(single, "Orders.cs");
        Assert.Contains("Did you mean src/Orders.cs", typo);
        Assert.DoesNotContain("is the repository, not a directory in it", typo);

        // The prefix is there, but nothing is at the corrected path either: a repository whose slug is
        // also a directory in it would otherwise be told to drop a segment that was correct.
        string neither = await ReadAsync(single, "beta/src/Nope.cs");
        Assert.DoesNotContain("is the repository, not a directory in it", neither);
        Assert.Contains("No indexed file", neither);
    }

    [Fact]
    public async Task Glob_matches_across_the_project_and_can_be_scoped_to_one_repository()
    {
        await using var client = await StartAsync();

        string all = await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = "*.cs" });
        Assert.Contains("2 files matching \"*.cs\"", all);
        Assert.Contains("one/src/Orders.cs", all);
        Assert.Contains("one/src/Orders.g.cs", all);
        Assert.DoesNotContain("index.ts", all);
        // Line counts let the agent size a read before making it.
        Assert.Contains("6L  one/src/Orders.cs", all);

        string scoped = await CallAsync(client, "glob",
            new Dictionary<string, object?> { ["glob"] = "*", ["repo"] = "two" });
        Assert.Contains("3 files matching \"*\" in repository 'two'", scoped);
        Assert.DoesNotContain("one/", scoped);
        Assert.Contains("two/Makefile", scoped);

        string prefixed = await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = "one/src/*" });
        Assert.Contains("2 files", prefixed);

        string skipped = await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = "*.bin" });
        Assert.Contains("one/assets/logo.bin  (not indexed: binary)", skipped);

        string limited = await CallAsync(client, "glob",
            new Dictionary<string, object?> { ["glob"] = "*", ["limit"] = 2 });
        Assert.Contains("7 files matching", limited);
        // A total a higher limit could reach is still offered one, in the wording it always had.
        Assert.Contains("showing the first 2 by path (raise limit or narrow the glob)", limited);
    }

    /// <summary>
    ///     The two ways a glob can run past its page, which need different advice: a total a higher
    ///     <c>limit</c> could still reach, and one it never can because <see cref="IndexReader.MaxFiles" />
    ///     tops out below it. And the rule that produced an oversized answer — a lone <c>*</c> crossing
    ///     directory separators — named where it bit rather than only in the tool description.
    /// </summary>
    [Fact]
    public async Task Glob_past_the_page_offers_a_higher_limit_only_when_one_could_reach_the_total()
    {
        await using var client = await _host.ConnectAsync(FileToolsFixture.Wide);

        // A one-level shell glob that matched the whole repository: "raise limit" cannot reach 2100.
        string swallowed = await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = "radix/*" });
        Assert.Contains("2100 files matching \"radix/*\"", swallowed);
        Assert.Contains("showing the first 500 by path", swallowed);
        Assert.DoesNotContain("raise limit", swallowed);
        Assert.Contains("2000", swallowed);
        Assert.Contains("crosses directory separators", swallowed);
        Assert.Contains("list_tree", swallowed);

        // Past the page but within reach, so the advice that works is still the one offered — and the
        // `*` note is about the shape of the pattern, not about how far past the cap the total is.
        string reachable =
            await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = "radix/src/g0/*" });
        Assert.Contains("700 files matching", reachable);
        Assert.Contains("showing the first 500 by path (raise limit or narrow the glob)", reachable);
        Assert.Contains("crosses directory separators", reachable);

        // A prefix segment reads as "the directories starting with g" and is the whole subtree (#113).
        string prefix = await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = "radix/src/g*" });
        Assert.Contains("2100 files matching \"radix/src/g*\"", prefix);
        Assert.Contains("crosses directory separators", prefix);
        Assert.Contains("list_tree", prefix);

        // The case the tool is sold on: a name shape, no directory in it, nothing to warn about however
        // many it matches.
        string shape = await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = "*.cs" });
        Assert.Contains("2100 files matching \"*.cs\"", shape);
        Assert.DoesNotContain("raise limit", shape);
        Assert.DoesNotContain("crosses directory separators", shape);
        Assert.DoesNotContain("list_tree", shape);

        // `**` is the caller asking for every level, so it is not told that it got them.
        string deliberate =
            await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = "radix/src/**/*.cs" });
        Assert.Contains("2100 files matching", deliberate);
        Assert.DoesNotContain("crosses directory separators", deliberate);

        // A page that holds everything is the reply it always was: no advice, no note.
        string small =
            await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = "radix/src/g0/f000*" });
        Assert.Contains("10 files matching \"radix/src/g0/f000*\":\n", small);
        Assert.DoesNotContain("crosses directory separators", small);
        Assert.DoesNotContain("narrow the glob", small);
    }

    [Fact]
    public async Task Glob_misses_and_malformed_globs_are_explained_not_empty()
    {
        await using var client = await StartAsync();

        string nowhere = await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = "*.py" });
        Assert.Contains("No indexed file matches \"*.py\" in project 'alpha' (7 files in repositories one, two)",
            nowhere);

        string elsewhere = await CallAsync(client, "glob",
            new Dictionary<string, object?> { ["glob"] = "*.ts", ["repo"] = "one" });
        Assert.Contains("No indexed file matches \"*.ts\" in repository 'one'", elsewhere);
        Assert.Contains("2 files match in the other repositories of project 'alpha'", elsewhere);

        string unknownRepo = await CallAsync(client, "glob",
            new Dictionary<string, object?> { ["glob"] = "*.ts", ["repo"] = "three" });
        Assert.Contains("No repository 'three' in project 'alpha'", unknownRepo);

        string braces = await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = "*.{cs,ts}" });
        Assert.Contains("Brace expansion is not supported", braces);
        Assert.DoesNotContain("No indexed file", braces);

        string slash = await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = "one/src/" });
        Assert.Contains("trailing slash", slash);
        // The subtree pattern, not "one/src/**": `**` is the same wildcard twice and teaching it as a
        // second operator is what makes an agent expect `one/src/*` to be one level.
        Assert.Contains("one/src/*", slash);
        Assert.Contains("list_tree", slash);

        string empty = await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = "  " });
        Assert.Contains("empty", empty);

        string bracket = await CallAsync(client, "glob", new Dictionary<string, object?> { ["glob"] = "*[0-9.cs" });
        Assert.Contains("unbalanced [ ]", bracket);
        Assert.DoesNotContain("No indexed file", bracket);

        // A repository scope is matched case-insensitively, like every other slug an agent types.
        string upper = await CallAsync(client, "glob",
            new Dictionary<string, object?> { ["glob"] = "*.ts", ["repo"] = "TWO" });
        Assert.Contains("2 files matching \"*.ts\" in repository 'two'", upper);
    }

    [Fact]
    public async Task List_extensions_reports_file_counts_and_scopes_to_a_repository()
    {
        await using var client = await StartAsync();

        string all = await CallAsync(client, "list_extensions", new Dictionary<string, object?>());
        Assert.Contains("cs", all);
        Assert.Matches(@"cs\s+2 files", all);
        Assert.Matches(@"ts\s+2 files", all);
        Assert.Matches(@"md\s+1 file\b", all);
        Assert.Matches(@"\(none\)\s+1 file\b", all);
        Assert.Matches(@"bin\s+1 file .*not indexed", all);

        string scoped =
            await CallAsync(client, "list_extensions", new Dictionary<string, object?> { ["repo"] = "two" });
        Assert.Contains("repository 'two'", scoped);
        Assert.DoesNotMatch(@"\bcs\b", scoped);
        Assert.Matches(@"ts\s+2 files", scoped);
    }

    [Fact]
    public async Task List_tree_answers_from_the_index_to_the_given_depth()
    {
        await using var client = await StartAsync();

        string shallow = await ListTreeAsync(client, "one", 1);
        Assert.Contains("one/ (depth 1, 3 entries)", shallow);
        Assert.Contains("\nassets/\n", shallow);
        Assert.Contains("\nsrc/\n", shallow);
        Assert.Contains("\nREADME.md\n", shallow);
        Assert.DoesNotContain("Orders.cs", shallow);

        // Directories before files, each directory followed by its own entries, as `tree` prints.
        string deep = await ListTreeAsync(client, "one", 2);
        Assert.Contains("assets/\nassets/logo.bin  (not indexed: binary)\nsrc/\nsrc/Orders.cs\nsrc/Orders.g.cs\nREADME.md\n",
            deep);

        string root = await ListTreeAsync(client, "", 2);
        Assert.Contains("alpha (depth 2, 2 repositories)", root);
        Assert.Contains("one/\none/assets/\none/src/\none/README.md\ntwo/\ntwo/lib/\ntwo/Makefile\n", root);
    }

    [Fact]
    public async Task List_tree_explains_an_unknown_repository_a_missing_directory_and_a_file()
    {
        await using var client = await StartAsync();

        string noRepo = await ListTreeAsync(client, "nope", 1);
        Assert.Contains("No repository 'nope' in project 'alpha'", noRepo);
        Assert.Contains("one, two", noRepo);

        string noPath = await ListTreeAsync(client, "one/missing/dir", 1);
        Assert.Contains("'missing/dir' is not a directory in repository 'one'", noPath);

        string file = await ListTreeAsync(client, "one/src/Orders.cs", 1);
        Assert.Contains("is a file, not a directory", file);
        Assert.Contains("read_file", file);

        // The slug the index holds, whatever case the caller typed.
        string cased = await ListTreeAsync(client, "ONE", 1);
        Assert.Contains("one/ (depth 1", cased);

        string depth = await ListTreeAsync(client, "one", 0);
        Assert.Contains("depth must be at least 1", depth);
    }

    [Fact]
    public async Task List_tree_of_a_single_repository_project_starts_inside_the_repository()
    {
        // On the shared server: its repository is called `main`, which nothing else commits.
        await _host.IndexedProjectAsync("solo", new Dictionary<string, Dictionary<string, string>>
        {
            ["main"] = new() { ["src/Program.cs"] = "class P {}\n", ["README.md"] = "hello\n" }
        }, true);
        await using var client = await _host.ConnectAsync("solo");

        string root = await ListTreeAsync(client, "", 2);
        Assert.Contains("solo (depth 2, 3 entries)", root);
        Assert.Contains("src/\nsrc/Program.cs\nREADME.md\n", root);
        Assert.DoesNotContain("main/", root);
    }

    [Fact]
    public async Task Repo_info_reports_each_repository_its_commit_and_the_index_time()
    {
        // Its own server: this adds a repository after the build, which the shared project must not
        // have, and rebuilding TwoRepositories needs the fixture names the shared one holds.
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", TwoRepositories);
        string later = host.CreateGitRepository("later", new Dictionary<string, string> { ["a.txt"] = "a\n" });
        await host.AddRepositoryAsync("alpha", "later", later);
        await using var client = await host.ConnectAsync("alpha");

        string text = await CallAsync(client, "repo_info", new Dictionary<string, object?>());
        Assert.Contains("Project 'alpha'", text);
        Assert.Contains("Indexed at", text);
        Assert.Contains("Tokenised: no", text);
        Assert.Contains("one  ", text);
        Assert.Contains("4 files", text);
        Assert.Contains("two  ", text);
        Assert.Contains("3 files", text);
        using (var repo = new Repository(host.FixturePath("one")))
        {
            Assert.Contains(repo.Head.Tip.Sha[..12], text);
        }

        // Added after the build: present in the project, absent from the index, and said so.
        Assert.Contains("later", text);
        Assert.Contains("not indexed yet", text);
    }

    /// <summary>
    ///     How much history a repository holds and what it spans, which is the pair of facts an agent
    ///     otherwise establishes by bisecting the log: one measured evaluation spent 15 of its 28 calls
    ///     on `git_log(limit=1, page=N)` doing exactly that (#108).
    /// </summary>
    [Fact]
    public async Task Repo_info_reports_how_much_history_each_repository_holds_and_what_it_spans()
    {
        // Its own server: it commits its own history into a repository called `one`.
        using var host = new TestHost(SearchEngine.Substring);
        string source = host.CreateEmptyGitRepository("one");
        host.CommitToGitRepositoryAs("one", new Dictionary<string, string> { ["src/A.cs"] = "a\n" },
            "Import the old code", "Ada", "ada@example.invalid", 0);
        host.CommitToGitRepositoryAs("one", new Dictionary<string, string> { ["src/A.cs"] = "a2\n" },
            "Change it later", "Grace", "grace@example.invalid", 60 * 24 * 40);
        await host.CreateProjectAsync("alpha");
        await host.AddRepositoryAsync("alpha", "one", source);
        await host.RefreshAsync("alpha");
        await using var client = await host.ConnectAsync("alpha");

        string text = await CallAsync(client, "repo_info", new Dictionary<string, object?>());

        // Two commits forty days apart, so the span is a fact about dates and not about the walk.
        Assert.Contains("2 commits imported, 1970-01-01 to 1970-02-10", text);
        Assert.Contains("may begin later than the repository itself does", text);
    }

    /// <summary>
    ///     A repository whose history was never walked. "No commits" and "nobody changed it" read alike
    ///     and mean opposite things (CONTEXT.md, History), and this is the reply an agent reads before
    ///     it asks any history tool about the repository at all.
    /// </summary>
    [Fact]
    public async Task Repo_info_says_when_a_repository_has_no_imported_history()
    {
        // Its own server: it deletes rows from the index it reads, which the shared one must keep.
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", TwoRepositories);
        await host.ExecuteAsync("alpha", "DELETE FROM commits WHERE repo_slug = 'two'");
        await using var client = await host.ConnectAsync("alpha");

        string text = await CallAsync(client, "repo_info", new Dictionary<string, object?>());

        Assert.Contains("no history imported", text);
        // The repository that does have history still reports it, so the two are distinguishable in
        // one reply rather than the whole project reading as historyless. One commit, so singular.
        Assert.Contains("1 commit imported, ", text);
    }

    [Fact]
    public async Task A_project_without_an_index_gets_an_explanation_from_every_tool()
    {
        // On the shared server: a project nothing ever builds, which needs no repository at all.
        await _host.CreateProjectAsync("unbuilt");
        await using var client = await _host.ConnectAsync("unbuilt");

        foreach ((string tool, var arguments) in new[]
                 {
                     ("read_file", new Dictionary<string, object?> { ["paths"] = Paths("one/a.cs") }),
                     ("glob", new Dictionary<string, object?> { ["glob"] = "*.cs" }),
                     ("list_extensions", new Dictionary<string, object?>()),
                     ("list_tree", new Dictionary<string, object?>()),
                     ("repo_info", new Dictionary<string, object?>())
                 })
        {
            string text = await CallAsync(client, tool, arguments);
            Assert.Contains("no index", text);
            Assert.Contains("POST /api/projects/unbuilt/refresh", text);
        }
    }

    [Fact]
    public async Task List_declarations_answers_in_file_order_and_says_which_side_of_a_split_each_is()
    {
        await using var client = await DeclaringAsync();

        string orders = await DeclarationsAsync(client, "one/src/Orders.cs");
        Assert.Contains("one/src/Orders.cs (C#) declares 2 names, in file order:", orders);
        Assert.Contains("3: Orders", orders);
        Assert.Contains("5: Save", orders);
        // The line itself, so the signature is in the reply and the next call can be a read of the
        // range rather than a second orienting call.
        Assert.Contains("public void Save() { }", orders);
        // The commented-out declaration is shaped exactly like the live one, and only where the name
        // sits tells them apart. It must not be listed, or the outline is fiction.
        Assert.DoesNotContain("Removed", orders);
        // C# has no declaration/implementation split, so no entry claims a side.
        Assert.DoesNotContain("(implementation)", orders);
        Assert.Contains("Strong evidence, not proof.", orders);

        string customers = await DeclarationsAsync(client, "one/src/Customers.pas");
        Assert.Contains("(Delphi) declares 3 names", customers);
        // The announcement and the body are one routine written in two places, and an agent choosing
        // which to read needs to be told which is which.
        Assert.Contains("7: Save", customers);
        Assert.Contains("(declaration)", customers);
        Assert.Contains("12: TCustomer.Save", customers);
        Assert.Contains("(implementation)", customers);
    }

    /// <summary>
    ///     The three empty answers, which are three different facts: nothing was scanned because no
    ///     profile covers the extension, nothing was scanned because the language writes no
    ///     declaration a line can hold, and a scan that ran and found none. An agent told "no
    ///     declarations" three times in one wording would act on the first two as if they were the
    ///     third.
    /// </summary>
    [Fact]
    public async Task List_declarations_tells_the_three_kinds_of_empty_apart()
    {
        await using var client = await DeclaringAsync();

        string css = await DeclarationsAsync(client, "one/src/site.css");
        Assert.Contains("CSS, whose declarations are not something that can be read from a line", css);
        Assert.Contains("Nothing was scanned here", css);

        string notes = await DeclarationsAsync(client, "one/notes.md");
        Assert.Contains("no language profile covers this extension", notes);
        Assert.Contains("conservative default shapes", notes);
        Assert.Contains("found no declaration", notes);
        // A file nothing looked at properly must not be described as having been scanned and found bare.
        Assert.DoesNotContain("declares nothing its language writes", notes);

        string quiet = await DeclarationsAsync(client, "one/src/Quiet.cs");
        Assert.Contains("declares nothing its language writes as a type or a routine", quiet);
        Assert.Contains("none of them is a declaration", quiet);
        Assert.Contains("Strong evidence, not proof.", quiet);
    }

    [Fact]
    public async Task List_declarations_explains_a_path_that_names_no_file()
    {
        await using var client = await DeclaringAsync();

        string text = await DeclarationsAsync(client, "one/src/Nowhere.cs");
        // The refusal the index reader writes for every file-scoped tool, pinned rather than merely
        // asserted to be silent about declarations: a bare or wrong sentence naming the path would
        // pass a negative assertion, and this is the reply an agent reads most often after a typo.
        Assert.Contains(
            $"No indexed file 'one/src/Nowhere.cs' in repository 'one' of project '{FileToolsFixture.Declaring}'",
            text);
        Assert.Contains("glob or list_tree", text);
        Assert.DoesNotContain("declares", text);
    }

    /// <summary>
    ///     The back half of a big file (#112). A 500-row cap with no continuation is a hard ceiling on
    ///     any repository holding one long file, and the note that used to sit on it called that file
    ///     generated and sent the reader to read it — the single most expensive move available, and
    ///     false besides. The page is now a page: it says what it listed, where it got to, and what to
    ///     ask for next, and asking for it eventually reaches the end rather than looping.
    /// </summary>
    [Fact]
    public async Task List_declarations_pages_past_its_cap_and_says_where_it_got_to()
    {
        // One declaration past a full second page, so three pages: full, full, and a remainder.
        const int routines = (2 * FileDeclarations.MaxDeclarations) + 1;
        // Its own server: its repository is called `one`, which the shared fixture already holds.
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                // The class itself is a declaration, so the file declares routines + 1 names.
                ["src/Big.cs"] = "public class Big\n{\n"
                                 + string.Concat(Enumerable.Range(0, routines)
                                     .Select(i => $"    public void M{i}() {{ }}\n"))
                                 + "}\n"
            }
        });
        await using var client = await host.ConnectAsync("alpha");

        string first = await DeclarationsAsync(client, "one/src/Big.cs");
        Assert.Contains($"declares {FileDeclarations.MaxDeclarations} names, in file order", first);
        Assert.Contains($"NOTE: {FileDeclarations.MaxDeclarations} listed, reaching line ", first);
        Assert.Contains($"offset={FileDeclarations.MaxDeclarations} for the next page", first);
        // The claim that made the advice wrong, and the advice it led to.
        Assert.DoesNotContain("is generated", first);
        Assert.DoesNotContain("read it directly", first);
        // The first page stops where it says it does, and the name after it is not on it.
        Assert.Contains("M498", first);
        Assert.DoesNotContain("M499", first);

        string second = await PagedDeclarationsAsync(client, "one/src/Big.cs", FileDeclarations.MaxDeclarations);
        Assert.Contains($"skipping the first {FileDeclarations.MaxDeclarations}", second);
        // The back half is reachable, which is the whole point.
        Assert.Contains("M499", second);
        Assert.Contains("M998", second);

        string third = await PagedDeclarationsAsync(client, "one/src/Big.cs", 2 * FileDeclarations.MaxDeclarations);
        Assert.Contains("declares 2 names", third);
        Assert.Contains("M999", third);
        Assert.Contains("M1000", third);
        Assert.DoesNotContain("NOTE:", third);

        // Past the end is the end of the listing and not a file that declares nothing.
        string past = await PagedDeclarationsAsync(client, "one/src/Big.cs", 3 * FileDeclarations.MaxDeclarations);
        Assert.Contains("has no declaration past the first 1500", past);
        Assert.DoesNotContain("declares nothing its language writes", past);
    }

    private Task<McpClient> DeclaringAsync() => _host.ConnectAsync(FileToolsFixture.Declaring);

    private static Task<string> DeclarationsAsync(McpClient client, string path) =>
        CallAsync(client, "list_declarations", new Dictionary<string, object?> { ["path"] = path });

    private static Task<string> PagedDeclarationsAsync(McpClient client, string path, int offset) =>
        CallAsync(client, "list_declarations",
            new Dictionary<string, object?> { ["path"] = path, ["offset"] = offset });

    /// <summary>
    ///     The root of a tree listing reads one small table (#149). It is what an agent opens a project
    ///     with, and it was summing <c>size_bytes</c> over every file in the index to report a number
    ///     the build already knew — the one read on this path that touched the largest table at all.
    ///     Asserted on the statement the reader built, through the query-plan switch, because the
    ///     answer is identical either way: the byte totals below would pass over the join as happily as
    ///     over the column, which is exactly why the join survived this long.
    /// </summary>
    [Fact]
    public async Task The_root_of_a_tree_listing_does_not_join_the_files_table()
    {
        var client = await StartAsync();
        string plans = _host.ScratchFile("tree-plans");
        string reply;
        using (QueryPlan.Recording(plans))
            reply = await ListTreeAsync(client, "", 1);

        Assert.Contains("one/", reply, StringComparison.Ordinal);

        string statement = Directory.EnumerateFiles(plans, "*IndexReaderTree-RepositoryLevelAsync.sql.txt")
            .Select(File.ReadAllText).First();
        Assert.DoesNotContain("JOIN", statement, StringComparison.Ordinal);

        // And the number it reports is the one the join used to compute, which is the half of this a
        // plan assertion cannot see. The column sums every entry the tree held and the join summed
        // every row of `files`, and those are the same files — a skipped binary still has a row,
        // carrying its size — so the root must still agree with what `files` holds.
        var root = await _host.ScalarsAsync(FileToolsFixture.Alpha,
            "SELECT sum(byte_count)::VARCHAR FROM repositories");
        var summed = await _host.ScalarsAsync(FileToolsFixture.Alpha,
            "SELECT sum(size_bytes)::VARCHAR FROM files");
        Assert.Equal(summed, root);
    }

    /// <summary>
    ///     Several windows into one file locate it and read its history once (#180). The tool invites
    ///     three windows into one 800-line file, and each was locating the file — a scan of `files` with
    ///     <c>lower()</c> — and reading its commits again. A miss is remembered too, suggestions and all.
    ///     Counted on the statements the reader ran, through the query-plan switch, because the reply is
    ///     identical either way; that half is asserted against the entries read one at a time.
    /// </summary>
    [Fact]
    public async Task Several_windows_into_one_file_locate_it_and_read_its_history_once()
    {
        await using var client = await StartAsync();
        string[] entries =
            ["one/src/Orders.cs:1-2", "one/Orders.cs", "one/src/Orders.cs:3-4", "one/Orders.cs:2", "one/src/Orders.cs:5"];

        var singles = new List<string>();
        foreach (string entry in entries) singles.Add(await HistoryReadAsync(client, entry));

        string plans = _host.ScratchFile("read-plans");
        string reply;
        using (QueryPlan.Recording(plans))
            reply = await HistoryReadAsync(client, entries);

        Assert.Equal(string.Join("\n", singles), reply);
        Assert.Contains("Did you mean one/src/Orders.cs", reply, StringComparison.Ordinal);

        string fileId = Assert.Single(await _host.ScalarsAsync(FileToolsFixture.Alpha,
            "SELECT file_id::VARCHAR FROM files WHERE qualified_path = 'one/src/Orders.cs'"));
        Assert.Equal(1, TimesRun("IndexReader-FindFileAsync", "$p = one/src/Orders.cs"));
        Assert.Equal(1, TimesRun("IndexReader-FindFileAsync", "$p = one/Orders.cs"));
        Assert.Equal(1, TimesRun("IndexReader-FilesNamedAsync", "$n = Orders.cs"));
        Assert.Equal(1, TimesRun("IndexReader-FileCommitsAsync", $"$f = {fileId}"));

        int TimesRun(string label, string parameter) =>
            Directory.EnumerateFiles(plans, $"*{label}.sql.txt")
                .Count(file => File.ReadAllText(file).Split('\n').Contains($"-- {parameter}"));
    }

    private static Task<string> HistoryReadAsync(McpClient client, params string[] paths) =>
        CallAsync(client, "read_file", new Dictionary<string, object?> { ["paths"] = paths, ["withHistory"] = true });

    private Task<McpClient> StartAsync() => _host.ConnectAsync(FileToolsFixture.Alpha);

    private static Task<string> ReadAsync(McpClient client, params string[] paths) =>
        CallAsync(client, "read_file", new Dictionary<string, object?> { ["paths"] = paths });

    private static Task<string> ListTreeAsync(McpClient client, string path, int depth) =>
        CallAsync(client, "list_tree", new Dictionary<string, object?> { ["path"] = path, ["depth"] = depth });

    /// <summary>Routes an inline array argument through a parameter so CA1861 does not ask for a static field per call.</summary>
    private static string[] Paths(params string[] paths) => paths;

    private static Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments) =>
        TestHost.CallAsync(client, tool, arguments);
}

/// <summary>
///     The one server this class runs against, and the three projects its read-only tests share.
///     A server per test meant seventeen of them, each booting an in-process host before it built
///     anything; the host is built once here instead, and so are the fixtures nothing writes to.
///     <see cref="Wide" /> is the reason this matters most: two thousand one hundred files committed
///     and indexed, which is most of what this class costs, and it was being paid where it is now
///     built once.
///     Five tests still take a server of their own, and say so where they do. They need a second copy
///     of a repository this fixture has already committed — <c>TwoRepositories</c> and
///     <c>OneRepository</c> both name theirs <c>one</c>, and a fixture directory belongs to the host
///     rather than to the project — so a shared host would have them committing the same content into
///     the same repository, which git answers with "nothing to commit".
/// </summary>
public sealed class FileToolsFixture : IAsyncLifetime
{
    /// <summary>Two repositories, one of them holding a binary and a generated file.</summary>
    public const string Alpha = "alpha";

    /// <summary>The list_declarations fixture: five languages and one unit with a split.</summary>
    public const string Declaring = "declaring";

    /// <summary>More files than one glob page holds, in three directories.</summary>
    public const string Wide = "wide";

    public TestHost Host { get; } = new(SearchEngine.Substring);

    public async ValueTask InitializeAsync()
    {
        await Host.IndexedProjectAsync(Alpha, FileToolsTests.TwoRepositories);
        await Host.IndexedProjectAsync(Declaring, FileToolsTests.Declaring);
        await Host.IndexedProjectAsync(Wide, FileToolsTests.Wide);
    }

    public ValueTask DisposeAsync()
    {
        Host.Dispose();
        return ValueTask.CompletedTask;
    }
}
