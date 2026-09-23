using System.Diagnostics;
using CodeExplorer.Index;
using CodeExplorer.Infrastructure;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The experiment #90's third proposal asks for: issue tool calls against one project together and
///     see whether the server answers them together. It decides whether the burst climb the transcripts
///     show — 1261 ms rising to 9236 ms over fourteen calls one second apart, resetting after a pause —
///     is a queue inside this server or something outside it.
///     It is an experiment and not a regression test, so it asserts only what must be true whatever the
///     answer, and reports the numbers for a human to read. A threshold here would either pin today's
///     behaviour as correct or fail on a loaded machine; neither is what the ticket wants.
/// </summary>
public sealed class CallConcurrencyProbe
{
    private const int Calls = 8;

    /// <summary>The same cheap read every call makes, so the only thing varying is how many are in flight.</summary>
    private static readonly Dictionary<string, object?> OneFile =
        new() { ["paths"] = new[] { "one/src/Widget.cs" } };

    [Fact]
    public async Task Concurrent_calls_against_one_project_report_how_much_they_overlap()
    {
        const string slug = "concurrency";
        using var host = new TestHost(SearchEngine.Substring);
        await host.IndexedProjectAsync(slug, new Dictionary<string, Dictionary<string, string>>
        {
            ["one"] = new()
            {
                ["src/Widget.cs"] = "class Widget\n{\n    int Size;\n}\n",
                ["src/Gadget.cs"] = "class Gadget\n{\n    int Size;\n}\n"
            }
        });

        using var probe = new TelemetryProbe(slug);
        await using var client = await host.ConnectAsync(slug);
        // Warm first, and not timed: the first call pays the first ATTACH and the first connection, and
        // measured three times here it was about 70 ms against 21 ms warm. Charging that to the
        // baseline would make the concurrent calls look three times faster than one call, which is a
        // statement about start-up and not about concurrency.
        for (int i = 0; i < 3; i++) await TestHost.CallAsync(client, "read_file", OneFile);
        double alone = await TimedAsync(() => TestHost.CallAsync(client, "read_file", OneFile));

        var started = Stopwatch.GetTimestamp();
        var timings = await Task.WhenAll(Enumerable.Range(0, Calls).Select(_ => TimedAsync(() =>
            TestHost.CallAsync(client, "read_file", OneFile))));
        double wall = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        // Serialized, the wall clock is the sum; perfectly parallel, it is the slowest one. Where it
        // falls between those is the answer, and the ratio is what makes it readable at any speed.
        double sum = timings.Sum();
        double slowest = timings.Max();
        Report(
            $"one call alone: {alone:F1} ms\n"
            + $"{Calls} together: wall {wall:F1} ms, sum {sum:F1} ms, slowest {slowest:F1} ms, "
            + $"median {timings.Order().ElementAt(Calls / 2):F1} ms\n"
            + $"wall/sum {wall / sum:F2} (1.00 = fully serialized, {1.0 / Calls:F2} = fully parallel)\n"
            + $"slowest/alone {slowest / alone:F2}\n"
            // What the server itself thinks it spent, from the instruments #90 asked for. The gap
            // between the client-side timing above and the tool duration here is the transport, and
            // the gap between the tool duration and the lease is everything the tool did.
            + $"server-side: tool median {Median(probe, Telemetry.ToolDuration):F1} ms, "
            + $"lease median {Median(probe, Telemetry.LeaseDuration):F1} ms");

        // The only claim that holds whatever the shape: nothing deadlocked and every call answered.
        Assert.All(timings, timing => Assert.True(timing > 0));
        Assert.True(wall <= sum + 1, $"wall {wall:F1} ms cannot exceed the sum of the calls, {sum:F1} ms");
    }

    /// <summary>The middle measurement this run recorded on one instrument, in milliseconds.</summary>
    private static double Median(TelemetryProbe probe, string instrument)
    {
        var seconds = probe.For(instrument).Select(m => m.Value).Order().ToList();
        return seconds.Count == 0 ? 0 : seconds[seconds.Count / 2] * 1000;
    }

    /// <summary>
    ///     To the test output and to a file beside the test assembly. The file is what makes this
    ///     runnable as the experiment it is: a passing test's output is not printed by the runner, and
    ///     the numbers are the entire point of running it.
    /// </summary>
    private static void Report(string lines)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(lines);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "call-concurrency.txt"), lines);
    }

    private static async Task<double> TimedAsync(Func<Task<string>> call)
    {
        var started = Stopwatch.GetTimestamp();
        string reply = await call();
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Assert.NotEmpty(reply);
        return elapsed;
    }
}
