using LibGit2Sharp;
using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The index-backed tools that are not searches (#6): <c>read_file</c>, <c>glob</c>,
///     <c>list_extensions</c> and <c>repo_info</c>. None of them matches text, so the engine is pinned
///     to Substring once rather than run twice.
/// </summary>
public sealed class FileToolsTests : IDisposable
{
    private const string Orders =
        "class Orders\n{\n    void Needle() {}\n    // needle in a comment\n    int Count;\n}\n";

    private static readonly Dictionary<string, Dictionary<string, string>> TwoRepositories = new()
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

    private TestHost? _host;

    public void Dispose() => _host?.Dispose();

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
        Assert.Contains("showing the first 2", limited);
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
        Assert.Contains("one/src/**", slash);

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
    public async Task Repo_info_reports_each_repository_its_commit_and_the_index_time()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("alpha", TwoRepositories);
        string later = _host.CreateGitRepository("later", new Dictionary<string, string> { ["a.txt"] = "a\n" });
        await _host.AddRepositoryAsync("alpha", "later", later);
        await using var client = await _host.ConnectAsync("alpha");

        string text = await CallAsync(client, "repo_info", new Dictionary<string, object?>());
        Assert.Contains("Project 'alpha'", text);
        Assert.Contains("Indexed at", text);
        Assert.Contains("Full-text index: not built", text);
        Assert.Contains("one  ", text);
        Assert.Contains("4 files", text);
        Assert.Contains("two  ", text);
        Assert.Contains("3 files", text);
        using (var repo = new Repository(_host.FixturePath("one")))
        {
            Assert.Contains(repo.Head.Tip.Sha[..12], text);
        }

        // Added after the build: present in the project, absent from the index, and said so.
        Assert.Contains("later", text);
        Assert.Contains("not indexed yet", text);
    }

    [Fact]
    public async Task A_project_without_an_index_gets_an_explanation_from_every_tool()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.CreateProjectAsync("alpha");
        await using var client = await _host.ConnectAsync("alpha");

        foreach ((string tool, var arguments) in new[]
                 {
                     ("read_file", new Dictionary<string, object?> { ["paths"] = Paths("one/a.cs") }),
                     ("glob", new Dictionary<string, object?> { ["glob"] = "*.cs" }),
                     ("list_extensions", new Dictionary<string, object?>()),
                     ("repo_info", new Dictionary<string, object?>())
                 })
        {
            string text = await CallAsync(client, tool, arguments);
            Assert.Contains("no index", text);
            Assert.Contains("POST /api/projects/alpha/refresh", text);
        }
    }

    private async Task<McpClient> StartAsync()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.IndexedProjectAsync("alpha", TwoRepositories);
        return await _host.ConnectAsync("alpha");
    }

    private static Task<string> ReadAsync(McpClient client, params string[] paths) =>
        CallAsync(client, "read_file", new Dictionary<string, object?> { ["paths"] = paths });

    /// <summary>Routes an inline array argument through a parameter so CA1861 does not ask for a static field per call.</summary>
    private static string[] Paths(params string[] paths) => paths;

    private static Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments) =>
        TestHost.CallAsync(client, tool, arguments);
}
