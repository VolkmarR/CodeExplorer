using System.Net;
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
    ///     Attribution is written for the whole index on every build, from the runs of every repository
    ///     at once, so a refresh that appends commits to one repository re-runs the write over the
    ///     other's lines too. What it writes there has to be what was there before: the second
    ///     repository was not walked and its runs did not move, so every line of it must come back
    ///     naming the same commit.
    /// </summary>
    [Fact]
    public async Task A_first_build_attributes_every_line_in_the_project()
    {
        await IndexTwoCommitProjectAsync();

        // The whole-index write is what a first build needs and the case a scoped one would have
        // broken, so it is asserted as a count of what is NOT attributed rather than by reading the
        // lines of one file: nothing anywhere may be left without a commit.
        Assert.Equal(["0"], await _host.ScalarsAsync("alpha",
            "SELECT count(*)::VARCHAR FROM lines WHERE commit_id IS NULL"));
        Assert.Equal(["3"], await _host.ScalarsAsync("alpha",
            "SELECT count(*)::VARCHAR FROM lines"));
    }

    [Fact]
    public async Task A_refresh_of_one_repository_leaves_the_other_repositorys_attribution_alone()
    {
        await IndexTwoRepositoryProjectAsync();
        const string sql =
            """
            SELECT f.path || ':' || l.line_number || ' ' || c.sha
            FROM lines l JOIN files f USING (file_id) JOIN repositories r USING (repo_id)
            JOIN commits c USING (commit_id)
            WHERE r.slug = 'two' ORDER BY f.path, l.line_number
            """;
        var before = await _host.ScalarsAsync("gamma", sql);
        Assert.NotEmpty(before);

        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Check.cs"] = "first\nsecond-changed\nlast\n" },
            "Rename the third line", "Linus", "linus@example.invalid", 4);
        await _host.RefreshAsync("gamma");

        Assert.Equal(before, await _host.ScalarsAsync("gamma", sql));
        // The half that says the refresh happened at all: the walked repository did move.
        var walked = await _host.ScalarsAsync("gamma",
            """
            SELECT c.subject FROM lines l JOIN files f USING (file_id) JOIN repositories r USING (repo_id)
            JOIN commits c USING (commit_id) WHERE r.slug = 'one' ORDER BY l.line_number
            """);
        Assert.Equal(["Add the validator", "Tighten the check", "Rename the third line"], walked);
    }

    /// <summary>
    ///     A file deleted and added again is two spans of file-level history and one file at HEAD. The
    ///     replay forgets the path on the deletion, so the lines that come back are the re-adding
    ///     commit's and not the original author's — the deletion is not a rename and nothing followed
    ///     the content across it.
    /// </summary>
    [Fact]
    public async Task A_file_deleted_and_added_again_attributes_to_the_commit_that_brought_it_back()
    {
        await IndexTwoCommitProjectAsync();
        _host.RemoveInGitRepositoryAs("one", ["src/Check.cs"], "Drop the validator", "Linus",
            "linus@example.invalid", 2);
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Check.cs"] = "first\nsecond-changed\nthird\n" },
            "Bring the validator back", "Linus", "linus@example.invalid", 3);
        await _host.RefreshAsync("alpha");

        var attributed = await _host.ScalarsAsync("alpha",
            "SELECT c.subject FROM lines l JOIN commits c USING (commit_id) ORDER BY l.line_number");
        Assert.Equal(
            ["Bring the validator back", "Bring the validator back", "Bring the validator back"], attributed);
    }

    /// <summary>
    ///     A refresh rewrites the attribution of only the paths its new commits touched, so what it leaves
    ///     must be what rewriting every path would have: the same runs, naming the same commits. The full
    ///     rewrite is forced by declaring the index an older schema, which re-walks from the root. Each
    ///     kind of change is here because each reaches the state differently — a rename through the path
    ///     it moved from, a deletion by leaving nothing to write — and one path is left alone, so
    ///     "untouched" is checked against something that was there to lose. The copy arrives as an
    ///     addition: the walk diffs with libgit2's defaults, which detect renames and not copies, so the
    ///     replay's copy branch is unreachable from a real repository and is not what this exercises.
    /// </summary>
    [Fact]
    public async Task A_refresh_leaves_the_attribution_a_full_rewrite_would()
    {
        string source = _host.CreateEmptyGitRepository("one");
        _host.CommitToGitRepositoryAs("one", new Dictionary<string, string>
        {
            ["src/Edit.cs"] = "a\nb\nc\n", ["src/Move.cs"] = "m\nn\no\n", ["src/Copy.cs"] = "x\ny\nz\n",
            ["src/Drop.cs"] = "d\n", ["src/Keep.cs"] = "k\nl\n"
        }, "Start", "Ada", "ada@example.invalid", 0);
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Move.cs"] = "m\nn-changed\no\n", ["src/Keep.cs"] = "k\nl2\n" },
            "Touch before the refresh", "Grace", "grace@example.invalid", 1);
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "one", source);
        await _host.RefreshAsync("alpha");

        _host.CommitToGitRepositoryAs("one", new Dictionary<string, string>
        {
            ["src/Edit.cs"] = "a\nb-changed\nc\n", ["src/Copied.cs"] = "x\ny\nz\n"
        }, "Edit and copy", "Linus", "linus@example.invalid", 2);
        _host.MoveInGitRepositoryAs("one", new Dictionary<string, string> { ["src/Move.cs"] = "src/Moved.cs" },
            "Move", "Linus", "linus@example.invalid", 3);
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Moved.cs"] = "m\nn-changed\no\np\n" },
            "Extend the moved file", "Linus", "linus@example.invalid", 4);
        _host.RemoveInGitRepositoryAs("one", ["src/Drop.cs"], "Drop", "Linus", "linus@example.invalid", 5);
        await _host.RefreshAsync("alpha");

        const string sql =
            """
            SELECT a.path || ':' || a.start_line || '-' || a.end_line || ' ' || c.sha
            FROM attribution a JOIN commits c USING (commit_id) ORDER BY a.path, a.start_line
            """;
        var incremental = await _host.ScalarsAsync("alpha", sql);
        var paths = await _host.ScalarsAsync("alpha", "SELECT DISTINCT path FROM attribution ORDER BY path");
        // Not vacuous: the untouched path survived the refresh, the moved one arrived under its new name
        // and the deleted one is gone, so the comparison below is over a state that actually moved.
        Assert.Equal(["src/Copied.cs", "src/Copy.cs", "src/Edit.cs", "src/Keep.cs", "src/Moved.cs"], paths);

        await _host.ExecuteAsync("alpha", "UPDATE index_info SET schema_version = schema_version - 1");
        await _host.RefreshAsync("alpha");

        Assert.Equal(await _host.ScalarsAsync("alpha", sql), incremental);
    }

    /// <summary>
    ///     The runs are expanded to one row per line to write attribution, and that expansion is scratch
    ///     for one statement. A table left behind would be in the file about to be swapped in, roughly
    ///     doubling it, and would then be carried nowhere and read by nothing.
    /// </summary>
    [Fact]
    public async Task The_built_index_holds_no_trace_of_the_expansion_attribution_is_written_from()
    {
        await IndexTwoCommitProjectAsync();

        var tables = await _host.ScalarsAsync("alpha", "SELECT table_name FROM duckdb_tables()");
        Assert.DoesNotContain("attributed_lines", tables);
    }

    /// <summary>
    ///     Two runs of one path covering the same line is a state the replay cannot produce, so reaching
    ///     the write with one means the history carried into this build was not written by it. Both the
    ///     old range join and the new equality join would take whichever row they reached last and ship
    ///     a line naming a commit that never touched it. The build fails instead, naming where.
    /// </summary>
    [Fact]
    public async Task Attribution_runs_that_overlap_fail_the_build_naming_the_repository_and_the_path()
    {
        await IndexTwoCommitProjectAsync();
        // Planted on the live index, which the next build carries its history over from. The refresh
        // finds no new commit, so the replay does not rewrite the runs and the overlap reaches the write.
        await _host.ExecuteAsync("alpha",
            "INSERT INTO attribution SELECT * FROM attribution WHERE start_line = 1");

        using (var response = await _host.RequestRefreshAsync("alpha"))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await _host.WaitForRefreshesAsync();

        var status = await _host.RefreshStatusAsync("alpha");
        Assert.Equal(RefreshState.Failed, status.State);
        Assert.Contains("src/Check.cs", status.Error);
        Assert.Contains("'one'", status.Error);
    }

    [Fact]
    public async Task Attribution_runs_that_overlap_on_a_path_no_longer_at_HEAD_do_not_fail_the_build()
    {
        await IndexTwoCommitProjectAsync();
        // attribution keeps runs for paths the walk no longer sees. They join to no file and so are
        // written onto no line, which is why an overlap among them is not a reason to refuse a build
        // whose only remedy would be a rebuild from scratch.
        await _host.ExecuteAsync("alpha",
            "INSERT INTO attribution SELECT repo_slug, 'src/Gone.cs', start_line, end_line, commit_id "
            + "FROM attribution WHERE path = 'src/Check.cs'");
        await _host.ExecuteAsync("alpha",
            "INSERT INTO attribution SELECT * FROM attribution WHERE path = 'src/Gone.cs'");

        using (var response = await _host.RequestRefreshAsync("alpha"))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await _host.WaitForRefreshesAsync();

        var status = await _host.RefreshStatusAsync("alpha");
        Assert.Equal(RefreshState.Succeeded, status.State);
        // And the file that is still there is attributed as it was.
        var attributed = await _host.ScalarsAsync("alpha",
            """
            SELECT coalesce(c.subject, 'none') FROM lines l
            LEFT JOIN commits c USING (commit_id)
            ORDER BY l.line_number
            """);
        Assert.Equal(["Add the validator", "Tighten the check", "Add the validator"], attributed);
    }

    /// <summary>
    ///     Two commits on one file: the first writes three lines, the second rewrites the middle one.
    ///     Every attribution assertion here rests on that shape, so it is built once.
    /// </summary>
    /// <summary>
    ///     A rename is persisted as the path it moved from, and nothing else is (#131). It is the edge
    ///     libgit2 already detects and the walk already consumes to carry attribution across a move; it
    ///     was thrown away at append time until there was a column for it.
    /// </summary>
    [Fact]
    public async Task A_rename_records_the_path_it_moved_from_and_no_other_change_does()
    {
        await IndexTwoCommitProjectAsync();
        _host.MoveInGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Check.cs"] = "src/Domain/Check.cs" },
            "Move the validator", "Linus", "linus@example.invalid", 2);
        await _host.RefreshAsync("alpha");

        var rows = await _host.ScalarsAsync("alpha",
            """
            SELECT cf.change_kind || ' ' || cf.path || ' <- ' || coalesce(cf.old_path, 'nothing')
            FROM commit_files cf JOIN commits c USING (commit_id)
            ORDER BY c.commit_id, cf.path
            """);
        Assert.Equal([
            "added src/Check.cs <- nothing",
            "modified src/Check.cs <- nothing",
            "renamed src/Domain/Check.cs <- src/Check.cs"
        ], rows);
    }

    /// <summary>
    ///     The history tables are the only ones a refresh inherits, and they are inherited column for
    ///     column — so a live index an older schema wrote must be inherited from not at all (ADR-0007).
    ///     Asserted by planting a row no walk would produce and then declaring the index old: a refresh
    ///     that carried it over would keep it, and a re-walk cannot.
    /// </summary>
    [Fact]
    public async Task A_live_index_of_an_older_schema_is_re_walked_rather_than_carried_over()
    {
        await IndexTwoCommitProjectAsync();
        await _host.ExecuteAsync("alpha",
            "INSERT INTO commits VALUES (9999, 'one', 'deadbeef', 'Ghost', 'ghost@example.invalid', "
            + "now(), 'Carried over from an older schema', '')");
        await _host.ExecuteAsync("alpha", "UPDATE index_info SET schema_version = schema_version - 1");
        await _host.RefreshAsync("alpha");

        var subjects = await _host.ScalarsAsync("alpha", "SELECT subject FROM commits ORDER BY commit_id");
        Assert.Equal(["Add the validator", "Tighten the check"], subjects);
    }

    /// <summary>
    ///     And the other direction, which is what makes the guard a guard rather than a switch that
    ///     turned carry-over off: an index of the current schema is still inherited, so a refresh
    ///     appends to the history it has instead of walking it again (ADR-0007).
    /// </summary>
    [Fact]
    public async Task A_live_index_of_this_schema_is_still_carried_over()
    {
        await IndexTwoCommitProjectAsync();
        await _host.ExecuteAsync("alpha",
            "INSERT INTO commits VALUES (9999, 'one', 'deadbeef', 'Ghost', 'ghost@example.invalid', "
            + "now(), 'Carried over from this schema', '')");
        await _host.RefreshAsync("alpha");

        var subjects = await _host.ScalarsAsync("alpha", "SELECT subject FROM commits ORDER BY commit_id");
        Assert.Contains("Carried over from this schema", subjects);
    }

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

    /// <summary>
    ///     One project over two repositories, each with a history of its own. The second holds two files
    ///     and three commits so that "untouched" means several paths and several commits and not one row
    ///     that could match by luck.
    /// </summary>
    private async Task IndexTwoRepositoryProjectAsync()
    {
        string first = _host.CreateEmptyGitRepository("one");
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Check.cs"] = "first\nsecond\nthird\n" },
            "Add the validator", "Ada", "ada@example.invalid", 0);
        _host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Check.cs"] = "first\nsecond-changed\nthird\n" },
            "Tighten the check", "Grace", "grace@example.invalid", 1);

        string second = _host.CreateEmptyGitRepository("two");
        _host.CommitToGitRepositoryAs("two",
            new Dictionary<string, string> { ["src/Parse.cs"] = "alpha\nbeta\ngamma\n", ["README.md"] = "docs\n" },
            "Add the parser", "Ada", "ada@example.invalid", 2);
        _host.CommitToGitRepositoryAs("two",
            new Dictionary<string, string> { ["src/Parse.cs"] = "alpha\nbeta-changed\ngamma\ndelta\n" },
            "Extend the parser", "Grace", "grace@example.invalid", 3);

        await _host.CreateProjectAsync("gamma");
        await _host.AddRepositoryAsync("gamma", "one", first);
        await _host.AddRepositoryAsync("gamma", "two", second);
        await _host.RefreshAsync("gamma");
    }
}
