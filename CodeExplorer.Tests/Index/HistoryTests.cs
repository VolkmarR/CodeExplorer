using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The history a build imports (ADR-0007): the commits of the default branch, the paths each one
///     touched, and the attribution of every line at HEAD. Asserted through the index a refresh
///     produced, because that is the only thing any tool ever reads.
///     The invariant these tests exist for is that a rebuild does not silently repoint attribution at
///     the wrong commit — commit ids are carried over and lines are not, so a build that renumbered
///     them would leave an index that is confidently wrong and looks fine.
/// </summary>
public sealed class HistoryTests : IDisposable
{
    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_build_records_the_commits_of_the_default_branch_with_their_authors()
    {
        await IndexTwoCommitProjectAsync();

        var subjects = await _host.ScalarsAsync("alpha", "SELECT subject FROM commits ORDER BY commit_id");
        // Oldest first: commit_id ascends with history, which is what lets max(commit_id) mean "last
        // changed by" without consulting a date that a rebase can move backwards.
        Assert.Equal(["Add the validator", "Tighten the check"], subjects);

        var authors = await _host.ScalarsAsync("alpha", "SELECT author_email FROM commits ORDER BY commit_id");
        Assert.Equal(["ada@example.invalid", "grace@example.invalid"], authors);
    }

    [Fact]
    public async Task A_line_is_attributed_to_the_commit_that_last_changed_it()
    {
        await IndexTwoCommitProjectAsync();

        // Line 2 is the one the second commit rewrote; lines 1 and 3 are untouched since the first.
        var attributed = await _host.ScalarsAsync("alpha",
            """
            SELECT coalesce(c.subject, 'none') FROM lines l
            LEFT JOIN commits c USING (commit_id)
            ORDER BY l.line_number
            """);
        Assert.Equal(["Add the validator", "Tighten the check", "Add the validator"], attributed);
    }

    [Fact]
    public async Task A_file_carries_the_first_and_last_commit_that_touched_it()
    {
        await IndexTwoCommitProjectAsync();

        var span = await _host.ScalarsAsync("alpha",
            """
            SELECT first.subject || ' -> ' || last.subject FROM files f
            JOIN commits first ON first.commit_id = f.first_commit
            JOIN commits last ON last.commit_id = f.last_commit
            """);
        Assert.Equal(["Add the validator -> Tighten the check"], span);
    }

    /// <summary>
    ///     The test ADR-0007 asks for by name. A second refresh rebuilds <c>files</c> and <c>lines</c>
    ///     from the clone while <c>commits</c> is carried over, so a build that re-sequenced commit ids
    ///     would leave every line pointing at a different commit than before — with nothing failing and
    ///     nothing looking wrong. Asserting the SHA and not the id is the point: the id may legitimately
    ///     be anything, as long as it still names the same commit.
    /// </summary>
    [Fact]
    public async Task Rebuilding_leaves_a_line_attributed_to_the_same_commit()
    {
        await IndexTwoCommitProjectAsync();
        const string sql =
            """
            SELECT c.sha FROM lines l JOIN commits c USING (commit_id)
            WHERE l.line_number = 2
            """;
        var before = await _host.ScalarsAsync("alpha", sql);
        string sha = Assert.Single(before);

        await _host.RefreshAsync("alpha");

        Assert.Equal([sha], await _host.ScalarsAsync("alpha", sql));
    }

    /// <summary>
    ///     A refresh that finds nothing new walks nothing: the second build's commits are the carried-over
    ///     ones and not a second copy of them. Without the watermark the table would double on every
    ///     refresh, which a count is the shortest way to catch.
    /// </summary>
    [Fact]
    public async Task Refreshing_an_unchanged_repository_appends_no_commits()
    {
        await IndexTwoCommitProjectAsync();
        await _host.RefreshAsync("alpha");

        Assert.Equal(["2"], await _host.ScalarsAsync("alpha", "SELECT count(*)::VARCHAR FROM commits"));
    }

    /// <summary>
    ///     A commit pushed after the first build is appended to the history already recorded, and takes
    ///     an id above every existing one. The line it changed re-attributes to it; the lines it did not
    ///     touch keep the commits they had.
    /// </summary>
    [Fact]
    public async Task A_new_commit_is_appended_and_re_attributes_only_the_lines_it_changed()
    {
        await IndexTwoCommitProjectAsync();
        // Only the third line differs from what the second commit left: line 2 must keep its commit,
        // which is the half of this assertion that a too-broad edit would quietly stop testing.
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Check.cs"] = "first\nsecond-changed\nlast\n" },
            "Rename the third line", "Linus", "linus@example.invalid", 2);
        await _host.RefreshAsync("alpha");

        var attributed = await _host.ScalarsAsync("alpha",
            """
            SELECT c.subject FROM lines l JOIN commits c USING (commit_id) ORDER BY l.line_number
            """);
        Assert.Equal(["Add the validator", "Tighten the check", "Rename the third line"], attributed);
    }

    /// <summary>
    ///     Attribution is keyed by blob hash, so a file that only moved keeps it without the walk doing
    ///     any rename detection. The file-level history begins at the move, which is the half of
    ///     rename-following ADR-0007 deliberately does not pay for.
    /// </summary>
    [Fact]
    public async Task A_moved_file_keeps_the_attribution_of_its_content()
    {
        await IndexTwoCommitProjectAsync();
        string fixture = _host.FixturePath("one");
        File.Delete(Path.Combine(fixture, "src", "Check.cs"));
        using (var repository = new LibGit2Sharp.Repository(fixture))
            LibGit2Sharp.Commands.Remove(repository, "src/Check.cs");
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Domain/Check.cs"] = "first\nsecond-changed\nthird\n" },
            "Move the validator", "Linus", "linus@example.invalid", 2);
        await _host.RefreshAsync("alpha");

        var attributed = await _host.ScalarsAsync("alpha",
            """
            SELECT f.path || ':' || l.line_number || ' ' || c.subject
            FROM lines l JOIN files f USING (file_id) JOIN commits c USING (commit_id)
            ORDER BY l.line_number
            """);
        // The content is byte-identical to what the second commit produced, so every line keeps the
        // commit it had — the move itself attributes nothing.
        Assert.Equal([
            "src/Domain/Check.cs:1 Add the validator",
            "src/Domain/Check.cs:2 Tighten the check",
            "src/Domain/Check.cs:3 Add the validator"
        ], attributed);
    }

    /// <summary>
    ///     Two commits on one file: the first writes three lines, the second rewrites the middle one.
    ///     Every attribution assertion here rests on that shape, so it is built once.
    /// </summary>
    private async Task IndexTwoCommitProjectAsync()
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
    }
}
