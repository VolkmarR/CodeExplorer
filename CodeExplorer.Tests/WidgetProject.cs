using CodeExplorer.Index;

namespace CodeExplorer.Tests;

/// <summary>
///     Two repositories that each hold a <c>src/Widget.cs</c>, plus a doc file: three files holding
///     "Widget" on three lines. The project the endpoint tests browse and search, and the one the
///     telemetry tests search to have something to record. Here and not beside either, because both
///     folders read it.
/// </summary>
internal static class WidgetProject
{
    /// <summary>A new copy each time, so no test can change what the next one is built from.</summary>
    public static Dictionary<string, Dictionary<string, string>> Repositories() => new()
    {
        ["one"] = new()
        {
            ["docs/Widget.md"] = "widget notes\n",
            ["src/Widget.cs"] = "class Widget\n{\n    int Size;\n}\n"
        },
        ["two"] = new() { ["src/Widget.cs"] = "class Widget { }\n" }
    };

    /// <summary>
    ///     A server of its own holding the project as <paramref name="slug" />, for a test that changes
    ///     the project or must be the only one on its server.
    /// </summary>
    public static async Task<TestHost> HostAsync(SearchEngine engine, string slug)
    {
        var host = new TestHost(engine);
        try
        {
            await host.IndexedProjectAsync(slug, Repositories());
            return host;
        }
        catch
        {
            // The host owns a data directory and an open DuckDB instance; a failure here would leak both.
            host.Dispose();
            throw;
        }
    }
}
