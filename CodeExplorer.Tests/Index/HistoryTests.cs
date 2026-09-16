using LibGit2Sharp;
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
    ///     The replay carries a renamed file's lines to its new path, so a file that only moved keeps
    ///     its attribution. The file-level history still begins at the move: <c>commit_files</c> names
    ///     paths, and following renames there is the half ADR-0007 does not pay for.
    /// </summary>
    [Fact]
    public async Task A_moved_file_keeps_the_attribution_of_its_content()
    {
        await IndexTwoCommitProjectAsync();
        string fixture = _host.FixturePath("one");
        File.Delete(Path.Combine(fixture, "src", "Check.cs"));
        using (var repository = new Repository(fixture))
            Commands.Remove(repository, "src/Check.cs");
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
    ///     A pure insertion is the one hunk shape whose header names the line before it rather than the
    ///     line it starts at (<c>@@ -0,0 +1 @@</c> for the top of a file). Read as every other hunk is,
    ///     it lands one line early and every file a commit created attributes to nothing — which is
    ///     exactly what the spike behind this code did first. The lines below the insertion must keep
    ///     their commits, shifted down.
    /// </summary>
    [Fact]
    public async Task Lines_inserted_at_the_top_of_a_file_shift_the_rest_down_without_re_attributing_them()
    {
        await IndexTwoCommitProjectAsync();
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Check.cs"] = "header\nfirst\nsecond-changed\nthird\n" },
            "Add a header", "Linus", "linus@example.invalid", 2);
        await _host.RefreshAsync("alpha");

        var attributed = await _host.ScalarsAsync("alpha",
            "SELECT c.subject FROM lines l JOIN commits c USING (commit_id) ORDER BY l.line_number");
        Assert.Equal(["Add a header", "Add the validator", "Tighten the check", "Add the validator"], attributed);
    }

    /// <summary>
    ///     A merge is one commit and its side branch is not walked (ADR-0007), so a line the merge
    ///     brought in attributes to the merge itself: the first-parent diff of a merge is everything the
    ///     branch did. Blame would have named the branch commit, which the index does not hold.
    /// </summary>
    [Fact]
    public async Task A_line_merged_in_from_a_branch_attributes_to_the_merge_commit()
    {
        await IndexTwoCommitProjectAsync();
        string fixture = _host.FixturePath("one");
        using (var repository = new Repository(fixture))
        {
            var trunk = repository.Head;
            Commands.Checkout(repository, repository.CreateBranch("feature"));
            File.WriteAllText(Path.Combine(fixture, "src", "Extra.cs"), "extra\n");
            Commands.Stage(repository, "src/Extra.cs");
            var branchAuthor = new Signature("Linus", "linus@example.invalid",
                DateTimeOffset.UnixEpoch.AddMinutes(2));
            repository.Commit("Work on the branch", branchAuthor, branchAuthor);
            Commands.Checkout(repository, trunk);
            var merger = new Signature("Grace", "grace@example.invalid", DateTimeOffset.UnixEpoch.AddMinutes(3));
            repository.Merge(repository.Branches["feature"], merger,
                new MergeOptions { FastForwardStrategy = FastForwardStrategy.NoFastForward });
        }

        await _host.RefreshAsync("alpha");

        // Three commits and not four: the branch commit is not on the first-parent line.
        Assert.Equal(["3"], await _host.ScalarsAsync("alpha", "SELECT count(*)::VARCHAR FROM commits"));
        var attributed = await _host.ScalarsAsync("alpha",
            """
            SELECT c.subject FROM lines l JOIN files f USING (file_id) JOIN commits c USING (commit_id)
            WHERE f.path = 'src/Extra.cs'
            """);
        Assert.StartsWith("Merge branch 'feature'", Assert.Single(attributed), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The replay counts lines the way the file walk splits them — on LF, dropping a CR before it
    ///     (<c>IndexBuilder.SplitLines</c>) — because a patch's hunk positions and <c>lines.line_number</c>
    ///     have to mean the same line. A CRLF file is where the two rules would first disagree.
    /// </summary>
    [Fact]
    public async Task A_file_with_windows_line_endings_attributes_line_for_line()
    {
        string source = _host.CreateEmptyGitRepository("crlf");
        _host.CommitToGitRepositoryAs("crlf",
            new Dictionary<string, string> { ["Check.cs"] = "first\r\nsecond\r\nthird\r\n" },
            "Add the validator", "Ada", "ada@example.invalid", 0);
        _host.CommitToGitRepositoryAs("crlf",
            new Dictionary<string, string> { ["Check.cs"] = "first\r\nsecond-changed\r\nthird\r\n" },
            "Tighten the check", "Grace", "grace@example.invalid", 1);
        await _host.CreateProjectAsync("beta");
        await _host.AddRepositoryAsync("beta", "crlf", source);
        await _host.RefreshAsync("beta");

        var attributed = await _host.ScalarsAsync("beta",
            "SELECT c.subject FROM lines l JOIN commits c USING (commit_id) ORDER BY l.line_number");
        Assert.Equal(["Add the validator", "Tighten the check", "Add the validator"], attributed);
    }

    /// <summary>
    ///     A history rewritten under the watermark — a force push that drops the recorded commits — walks
    ///     back to a root, and the carried-over attribution describes a tree no commit in that walk
    ///     descends from. The replay starts from nothing rather than applying the new commits onto it,
    ///     so every line attributes to a commit of the new history.
    /// </summary>
    [Fact]
    public async Task A_rewritten_history_attributes_from_its_new_root_and_not_onto_the_old_lines()
    {
        await IndexTwoCommitProjectAsync();
        string fixture = _host.FixturePath("one");
        using (var repository = new Repository(fixture))
        {
            // The same tree as HEAD, as a commit with no parent: the content did not change, the
            // history under it did. That is the case where replaying onto the old state would look
            // right by accident, since the edits of the new root are the whole file.
            var author = new Signature("Rewriter", "rewriter@example.invalid",
                DateTimeOffset.UnixEpoch.AddMinutes(5));
            var root = repository.ObjectDatabase.CreateCommit(author, author, "Start over",
                repository.Head.Tip.Tree, [], false);
            repository.Refs.UpdateTarget(repository.Head.CanonicalName, root.Sha);
        }

        await _host.RefreshAsync("alpha");

        var attributed = await _host.ScalarsAsync("alpha",
            "SELECT DISTINCT c.subject FROM lines l JOIN commits c USING (commit_id)");
        Assert.Equal(["Start over"], attributed);
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
