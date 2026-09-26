using CodeExplorer.Git;
using LibGit2Sharp;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     What <see cref="UnifiedDiff" /> reads out of the text libgit2 renders, and the one property the
///     history walk's speed now rests on: that the edits do not depend on how much unchanged text was
///     rendered around them.
///     The fixture is built with LibGit2Sharp directly rather than through <c>TestHost</c>. Nothing
///     here needs a server, an index or a project — it is a question about a diff — and a repository in
///     a temporary directory is the whole world it needs.
/// </summary>
public sealed class UnifiedDiffTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ce-diff-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() => TestHost.DeleteTree(_root);

    /// <summary>
    ///     <see cref="NativeDiff" /> asks libgit2 for no context lines, which is worth several times
    ///     the wall clock of a first history import because every rendered line is marshalled into
    ///     managed strings before it is thrown away. This is what says it threw away only what it was
    ///     already throwing away.
    ///     The shapes below are the ones where the two renderings differ structurally: with three lines
    ///     of context two nearby edits land in one hunk and the lines between them are walked, while
    ///     with none they are two hunks whose headers have to place them — so a reading that leaned on
    ///     the context lines would come apart exactly here.
    /// </summary>
    [Fact]
    public void Edits_are_the_same_whether_or_not_libgit2_renders_context()
    {
        string path = Build();
        using var repository = new Repository(path);

        var withContext = new CompareOptions { ContextLines = 3 };
        var without = new CompareOptions { ContextLines = 0 };

        int comparedFiles = 0, comparedEdits = 0;
        foreach (var commit in repository.Commits)
        {
            var parent = commit.Parents.FirstOrDefault();
            var three = Read(repository, parent, commit, withContext);
            var none = Read(repository, parent, commit, without);

            // The same files, in the same state, with the same old path: ContextLines must change what
            // is rendered and nothing about what the diff found. Rename detection in particular is
            // left at libgit2's defaults by both, and a rename that stopped being detected would
            // reattribute a file's whole history.
            Assert.Equal(three.Keys.Order(), none.Keys.Order());
            foreach ((string file, var edits) in three)
            {
                Assert.Equal(edits.Status, none[file].Status);
                Assert.Equal(edits.OldPath, none[file].OldPath);
                Assert.Equal(edits.Edits, none[file].Edits);
                comparedFiles++;
                comparedEdits += edits.Edits.Count;
            }
        }

        // An equality that held because both sides were empty would pass every assertion above and
        // prove nothing, so the fixture is made to say how much it actually compared.
        Assert.True(comparedFiles >= 12, $"only {comparedFiles} files compared");
        Assert.True(comparedEdits >= 15, $"only {comparedEdits} edits compared");
    }

    /// <summary>
    ///     One absolute reading, so the test above cannot pass by both renderings being wrong the same
    ///     way. Two edits one line apart: with no context they are two hunks, and their positions are
    ///     0-based indexes into the file as it was before the commit.
    /// </summary>
    [Fact]
    public void Two_edits_a_line_apart_are_read_as_two_edits_at_their_own_positions()
    {
        string path = Build();
        using var repository = new Repository(path);

        var commit = repository.Commits.Single(c => c.MessageShort == "near");
        var edits = Read(repository, commit.Parents.First(), commit, new CompareOptions { ContextLines = 0 });

        // Lines 2 and 4 of the file were rewritten, which is one line replaced at index 1 and one at
        // index 3, with the untouched line 3 between them.
        Assert.Equal([new LineEdit(1, 1, 1), new LineEdit(3, 1, 1)], edits["lines.txt"].Edits);
    }

    /// <summary>
    ///     <see cref="NativeDiff" /> reads each edit off a hunk header instead of the rendered text
    ///     (#292), which is sound only because a hunk with no context is exactly one edit. Held here
    ///     against the reading of LibGit2Sharp's patch at three lines of context, where hunks do merge
    ///     edits, so the two share nothing but libgit2's diff itself: the paths, kinds, line counts and
    ///     edits of every commit must come out the same.
    /// </summary>
    [Fact]
    public void Edits_read_from_hunk_headers_are_the_ones_the_rendered_patch_holds()
    {
        string path = Build();
        using var repository = new Repository(path);
        using var native = NativeRepository.Open(repository.Info.Path);
        using var diff = new NativeDiff(native);

        int compared = 0;
        foreach (var commit in repository.Commits)
        {
            var parent = commit.Parents.FirstOrDefault();
            var rendered = repository.Diff.Compare<Patch>(parent?.Tree, commit.Tree, null, null,
                    new CompareOptions { ContextLines = 3 })
                .Select(change => new ChangedPath(change.Path, change.OldPath, LocalCopy.KindName(change.Status),
                    change.LinesAdded, change.LinesDeleted, change.IsBinaryComparison, UnifiedDiff.Edits(change.Patch)))
                .OrderBy(change => change.Path, StringComparer.Ordinal).ToList();
            var read = diff.Diff(parent?.Tree.Id, commit.Tree.Id, long.MaxValue)
                .OrderBy(change => change.Path, StringComparer.Ordinal).ToList();

            Assert.Equal(rendered.Select(Describe), read.Select(Describe));
            compared += read.Count;
        }

        Assert.True(compared >= 12, $"only {compared} files compared");

        static string Describe(ChangedPath change) =>
            $"{change.OldPath} -> {change.Path} {change.ChangeKind} +{change.Added} -{change.Deleted} "
            + string.Join(" ", change.Edits);
    }

    private sealed record FileEdits(string Status, string? OldPath, IReadOnlyList<LineEdit> Edits);

    private static Dictionary<string, FileEdits> Read(Repository repository, Commit? parent, Commit commit,
        CompareOptions options)
    {
        var read = new Dictionary<string, FileEdits>(StringComparer.Ordinal);
        foreach (var change in repository.Diff.Compare<Patch>(parent?.Tree, commit.Tree, null, null, options))
            read[change.Path] = new FileEdits(change.Status.ToString(), change.OldPath,
                change.IsBinaryComparison ? [] : UnifiedDiff.Edits(change.Patch));
        return read;
    }

    /// <summary>
    ///     A history whose commits are the diff shapes that could tell the two renderings apart: an
    ///     edit against each end of a file, edits near enough to share a hunk and far enough not to, a
    ///     pure insertion, a pure deletion, an append, a file with no closing newline, and a rename
    ///     carrying edits with it.
    /// </summary>
    private string Build()
    {
        Repository.Init(_root);
        using var repository = new Repository(_root);

        string[] twenty = [.. Enumerable.Range(1, 20).Select(n => $"line {n}")];

        // A root commit, which is the whole-file add every first import starts with.
        Commit(repository, "root", new Dictionary<string, string> { ["lines.txt"] = Join(twenty) });

        var edited = twenty.ToArray();
        edited[9] = "line 10 changed";
        Commit(repository, "middle", new Dictionary<string, string> { ["lines.txt"] = Join(edited) });

        // Two edits one line apart: one hunk at three lines of context, two hunks at none.
        edited[1] = "line 2 changed";
        edited[3] = "line 4 changed";
        Commit(repository, "near", new Dictionary<string, string> { ["lines.txt"] = Join(edited) });

        // The first and the last line, where three lines of context are clipped by the file's edges.
        edited[0] = "line 1 changed";
        edited[19] = "line 20 changed";
        Commit(repository, "edges", new Dictionary<string, string> { ["lines.txt"] = Join(edited) });

        // A pure insertion, whose hunk header counts zero old lines — the one header shape that means
        // something different from all the others.
        var inserted = edited.Take(1).Concat(["inserted a", "inserted b"]).Concat(edited.Skip(1)).ToArray();
        Commit(repository, "insert", new Dictionary<string, string> { ["lines.txt"] = Join(inserted) });

        var deleted = inserted.Take(6).Concat(inserted.Skip(9)).ToArray();
        Commit(repository, "delete", new Dictionary<string, string> { ["lines.txt"] = Join(deleted) });

        var appended = deleted.Concat(["appended a", "appended b"]).ToArray();
        Commit(repository, "append", new Dictionary<string, string> { ["lines.txt"] = Join(appended) });

        // No closing newline, so libgit2 renders "\ No newline at end of file" — a body line that is
        // neither a context line nor an edit and must not be counted as one.
        Commit(repository, "no trailing newline",
            new Dictionary<string, string> { ["lines.txt"] = Join(appended).TrimEnd('\n') });

        // A second file, so a commit carries more than one changed path.
        Commit(repository, "second file", new Dictionary<string, string>
        {
            ["other.txt"] = Join(["alpha", "beta", "gamma"]),
            ["lines.txt"] = Join(appended)
        });

        // A rename with edits in the same commit, which is what the replay carries attribution across
        // and what rename detection has to keep finding.
        File.Delete(Path.Combine(_root, "other.txt"));
        Commit(repository, "rename with edits",
            new Dictionary<string, string> { ["moved.txt"] = Join(["alpha", "beta changed", "gamma", "delta"]) });

        Commit(repository, "drop a file", new Dictionary<string, string>(), ["lines.txt"]);
        return _root;
    }

    private static string Join(IEnumerable<string> lines) => string.Join('\n', lines) + "\n";

    private static void Commit(Repository repository, string message, Dictionary<string, string> files,
        string[]? remove = null)
    {
        foreach ((string path, string content) in files)
        {
            string full = Path.Combine(repository.Info.WorkingDirectory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        foreach (string path in remove ?? [])
            File.Delete(Path.Combine(repository.Info.WorkingDirectory, path));

        Commands.Stage(repository, "*");
        var who = new Signature("Fixture", "fixture@example.com", DateTimeOffset.UnixEpoch.AddMinutes(files.Count));
        repository.Commit(message, who, who);
    }
}
