using CodeExplorer.Index;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The arrangement the container image is built on (#14): the build installs <c>fts</c> into a
///     directory, and the server started over that directory finds it there rather than downloading
///     it. What no in-process test can prove is the half that matters most — that a replica with no
///     egress still answers by BM25 — because blocking the network is the container's job; what these
///     prove is that the two halves name the same place and that the second reads what the first wrote.
///     Both reach the network, and are the only tests here that do: each installs into a directory of
///     its own, so neither can be served by a copy an earlier run left behind. On a machine with no
///     egress they fail where the rest of the suite passes, which is the right way round — the install
///     failing is the thing the image exists to make impossible, so it should not pass quietly.
/// </summary>
public sealed class FtsExtensionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "CodeExplorer.Tests", Guid.NewGuid().ToString("N"));

    private TestHost? _host;

    private string Extensions => Path.Combine(_root, "extensions");

    public void Dispose()
    {
        _host?.Dispose();
        try
        {
            TestHost.DeleteTree(_root);
        }
        catch (UnauthorizedAccessException)
        {
            // Safe to swallow: LOAD maps the extension into this process and Windows refuses to delete
            // a mapped file, so nothing can remove it until the test run ends. Clearing the read-only
            // attribute — which is all the other temp trees need — does not help. Leaving the copy to
            // the operating system's temp sweep is the only option, and failing the test over it would
            // report a passing assertion as a failure, which is what happened before this catch.
        }
    }

    [Fact]
    public void Install_writes_the_extension_under_the_named_directory()
    {
        FtsExtension.InstallTo(Extensions);

        // Beneath a version and a platform folder DuckDB picks, which is the whole reason the install
        // runs through DuckDB rather than fetching a file: only the binary that loads it knows those.
        Assert.NotEmpty(Directory.GetFiles(Extensions, "fts.duckdb_extension", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_server_over_an_installed_directory_searches_through_full_text()
    {
        FtsExtension.InstallTo(Extensions);

        // Fts and not Auto: Auto would fall back to substring scan and pass while proving nothing,
        // which is exactly the silent downgrade the image exists to prevent.
        _host = new TestHost(SearchEngine.Fts, extensionDirectory: Extensions);
        await _host.IndexedProjectAsync("alpha", new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new() { ["src/Orders.cs"] = "class Orders\n{\n    void Needle() {}\n}\n" }
        });

        Assert.True(_host.Indexes.FtsAvailable);

        await using var client = await _host.ConnectAsync("alpha");
        string text = await TestHost.CallAsync(client, "grep",
            new Dictionary<string, object?> { ["query"] = "needle" });

        Assert.Contains($"({Search.GrepSearch.TokenEngine} engine)", text);
        Assert.Contains("one/src/Orders.cs", text);
    }
}
