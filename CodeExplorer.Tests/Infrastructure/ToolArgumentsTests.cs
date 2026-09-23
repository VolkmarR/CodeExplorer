using System.Text.Json;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The stray-argument check at the level of the arguments and the schema, below the MCP pipeline
///     <see cref="ToolReplyTests" /> drives. It runs on every call a client makes, so what it costs a
///     call that carried nothing stray is asserted here rather than left to a profile.
/// </summary>
public sealed class ToolArgumentsTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """{"type":"object","properties":{"query":{"type":"string"},"context":{"type":"integer"}},"required":["query"]}""")
        .RootElement;

    private static Dictionary<string, JsonElement> Arguments(params string[] names) =>
        names.ToDictionary(name => name, _ => JsonDocument.Parse("1").RootElement, StringComparer.Ordinal);

    [Fact]
    public void A_clean_call_has_no_stray_names_and_builds_no_lists_to_say_so()
    {
        var arguments = Arguments("query", "context");
        // Once to take JIT and the dictionary's cached key collection out of the measurement.
        Assert.Null(ToolArguments.Stray(arguments, Schema));

        long before = GC.GetAllocatedBytesForCurrentThread();
        var stray = ToolArguments.Stray(arguments, Schema);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(stray);
        // The one allocation left is the enumerator the SDK's IDictionary boxes. An empty list alone is
        // 32 bytes, and the check this replaced built four of them and a closure on every call.
        Assert.True(allocated <= 40, $"allocated {allocated} bytes");
    }

    [Fact]
    public void Stray_names_are_listed_in_the_order_they_arrived()
    {
        var stray = ToolArguments.Stray(Arguments("pattern", "query", "max_results"), Schema);

        Assert.Equal(["pattern", "max_results"], stray);
    }

    [Fact]
    public void Names_are_compared_ordinally()
    {
        Assert.Equal(["Query"], ToolArguments.Stray(Arguments("Query"), Schema));
    }

    [Fact]
    public void Every_name_is_stray_at_a_tool_that_declares_no_properties()
    {
        var none = JsonDocument.Parse("""{"type":"object"}""").RootElement;

        Assert.Equal(["query"], ToolArguments.Stray(Arguments("query"), none));
        Assert.Null(ToolArguments.Stray(null, none));
    }
}
