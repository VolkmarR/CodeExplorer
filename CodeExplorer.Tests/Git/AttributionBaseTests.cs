using LibGit2Sharp;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     What line number LibGit2Sharp reports a blame hunk starting at. Its own XML documentation says
///     only "the line number where this hunk begins" and never which end it counts from, while libgit2
///     underneath reports 1-based and the wrapper subtracts one on the way out. That is a fact about a
///     dependency and not about this codebase, so it is pinned here rather than asserted in a comment:
///     if a future version of the library stops subtracting, every line in every project is attributed
///     to the commit that changed the line above it, and nothing about the answers looks wrong.
///     This is the one test that names LibGit2Sharp for its own sake; <c>ModuleBoundaryTests</c>
///     exempts the test project because its fixtures are repositories.
/// </summary>
public sealed class AttributionBaseTests : IDisposable
{
    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    [Fact]
    public void Blame_hunks_start_at_line_zero_so_one_is_added_to_reach_a_line_number()
    {
        // Three lines committed together, then the middle one changed on its own: the file then blames
        // to three hunks, and the middle one is the only one whose start is neither end of the file.
        // A one-hunk fixture would pass whether the base were 0 or 1, which is the trap here.
        string path = _host.CreateGitRepository("blamed",
            new Dictionary<string, string> { ["src/Check.cs"] = "first\nsecond\nthird\n" });
        _host.CommitToGitRepositoryAs("blamed",
            new Dictionary<string, string> { ["src/Check.cs"] = "first\nchanged\nthird\n" },
            "Change the middle line", "Blamer", "blamer@example.invalid", 1);

        using var repository = new Repository(path);
        var hunks = repository.Blame("src/Check.cs").ToList();

        var middle = Assert.Single(hunks, hunk => hunk.FinalCommit.MessageShort == "Change the middle line");
        Assert.Equal(1, middle.LineCount);
        // The assertion the whole feature rests on: line 2 of the file is reported as 1.
        Assert.Equal(1, middle.FinalStartLineNumber);
    }
}
