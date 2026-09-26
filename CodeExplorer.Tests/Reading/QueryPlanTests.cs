using CodeExplorer.Index;
using CodeExplorer.Reading;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The query-plan recording a test counts statements through (#286). The switch is process-wide
///     and test classes run in parallel, so a recording that caught every read in the process counted
///     other classes' statements as its own whenever their parameters happened to match, and two
///     dumps sharing a label and a millisecond wrote over each other.
/// </summary>
public sealed class QueryPlanTests : IDisposable
{
    private readonly TestHost _recorded = new(SearchEngine.Substring);
    private readonly TestHost _other = new(SearchEngine.Substring);

    public void Dispose()
    {
        _recorded.Dispose();
        _other.Dispose();
    }

    /// <summary>
    ///     Two servers with the same project, the same repository and the same file, so a dump from the
    ///     other one carries exactly the parameters a test would look for in its own.
    /// </summary>
    [Fact]
    public async Task A_recording_catches_the_reads_of_its_own_server_and_none_of_another()
    {
        var files = new Dictionary<string, string> { ["a.cs"] = "class A;\n" };
        await HistoryFixtures.OnlyRepositoryProjectAsync(_recorded, "same", files);
        await HistoryFixtures.OnlyRepositoryProjectAsync(_other, "same", files);
        await using var recordedClient = await _recorded.ConnectAsync("same");
        await using var otherClient = await _other.ConnectAsync("same");
        var read = new Dictionary<string, object?> { ["paths"] = new[] { "only/a.cs" } };

        string plans = _recorded.ScratchFile("plans");
        using (_recorded.RecordPlans(plans))
            await TestHost.CallAsync(otherClient, "read_file", read);
        Assert.False(Directory.Exists(plans));

        using (_recorded.RecordPlans(plans))
            await TestHost.CallAsync(recordedClient, "read_file", read);
        Assert.NotEmpty(Directory.EnumerateFiles(plans, "*IndexReader-FindFileAsync.sql.txt"));
    }

    /// <summary>Every read is its own dump, named in the order it ran (see <see cref="QueryPlan.Stamp" />).</summary>
    [Fact]
    public void Two_dumps_in_one_millisecond_get_their_own_names_in_the_order_they_ran()
    {
        string first = QueryPlan.Stamp("IndexReader.FindFileAsync");
        string second = QueryPlan.Stamp("IndexReader.FindFileAsync");
        string third = QueryPlan.Stamp("A.AAsync");

        Assert.Equal([first, second, third], new[] { third, first, second }.Order(StringComparer.Ordinal));
        Assert.EndsWith("-IndexReader-FindFileAsync", first, StringComparison.Ordinal);
    }
}
