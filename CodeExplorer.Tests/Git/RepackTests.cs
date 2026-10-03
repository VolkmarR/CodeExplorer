using CodeExplorer.Git;
using CodeExplorer.Index;
using LibGit2Sharp;
using Xunit;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CodeExplorer.Tests;

/// <summary>
///     The repack a fetch makes due once a local copy holds too many packs or loose objects
///     (<c>LocalCopyRepack</c>; ADR-0007, revisited on 2026-09-29). The clone of a local fixture is
///     made of loose objects, because libgit2 copies a local repository's object folder rather than
///     transferring it, and every fetch from it adds one pack; so the thresholds are lowered to a few,
///     and a handful of commit-and-refresh cycles build up what months of real fetches would.
/// </summary>
public sealed class RepackTests : IDisposable
{
    /// <summary>
    ///     Lowered to a few packs so a handful of commit-and-refresh cycles cross it, where the shipped
    ///     fifty would take fifty. The loose-object threshold is lowered the same way where a test is
    ///     about loose objects, which the clone of a local fixture starts out as.
    /// </summary>
    private const int PackThreshold = 3;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host =
        new(SearchEngine.Substring, (LocalCopyRepack.PackThresholdSetting, PackThreshold));

    public void Dispose() => _host.Dispose();

    /// <summary>
    ///     The refresh that takes the copy past the threshold leaves it one pack and no loose objects,
    ///     with every object of the remote still in it, and the index built from it answers exactly what
    ///     one built from the unrepacked copy answers. The reference is a second server that never
    ///     repacks at this size, fed the same commits, which have the same ids because the fixture's
    ///     author and date are fixed.
    /// </summary>
    [Fact]
    public async Task Crossing_the_pack_threshold_repacks_the_copy_and_the_index_answers_the_same()
    {
        using var reference = new TestHost(SearchEngine.Substring);
        var packs = await CyclesAsync(_host, PackThreshold + 1);
        await CyclesAsync(reference, PackThreshold + 1);

        // One pack per fetch until the fetch that makes it one more than the threshold.
        Assert.Equal<int>([1, 2, 3, 1], packs);
        string copy = _host.ClonePath("alpha", "main");
        AssertRepacked(copy);
        AssertHoldsEveryObject(copy, _host.FixtureGitPath("main"));
        Assert.Equal(PackThreshold + 1, Packs(reference.ClonePath("alpha", "main")));
        Assert.Equal(await AnswersAsync(reference), await AnswersAsync(_host));
    }

    /// <summary>
    ///     A copy already holding one large pack — the one an earlier repack wrote — folds only the
    ///     small packs fetched since, as git's <c>--geometric=2</c> would: the large pack holds more than
    ///     twice what they do, so its files stay as they were, and the next repack costs what was fetched
    ///     rather than the whole history again.
    /// </summary>
    [Fact]
    public async Task A_large_pack_stays_and_only_the_packs_fetched_since_are_folded()
    {
        using var reference = new TestHost(SearchEngine.Substring);
        foreach (var host in new[] { _host, reference })
        {
            await host.CreateProjectAsync("alpha");
            await host.AddRepositoryAsync("alpha", "main", host.CreateGitRepository("main", Large()));
            await host.RefreshAsync("alpha");
        }

        string copy = _host.ClonePath("alpha", "main");
        string packDirectory = Path.Combine(copy, "objects", "pack");
        using (var clone = new Repository(copy))
            clone.ObjectDatabase.Pack(new PackBuilderOptions(packDirectory));
        foreach (string loose in Directory.EnumerateDirectories(Path.Combine(copy, "objects"), "??"))
            GitFixtures.DeleteTree(loose);
        string large = Path.ChangeExtension(Directory.GetFiles(packDirectory, "*.pack").Single(), null);
        var packs = new List<int>();
        for (int cycle = 1; cycle <= PackThreshold; cycle++)
        {
            foreach (var host in new[] { _host, reference })
            {
                host.CommitToGitRepository("main", Files(cycle));
                await host.RefreshAsync("alpha");
            }

            packs.Add(Packs(copy));
        }

        Assert.Equal<int>([2, 3, 2], packs);
        Assert.True(File.Exists(large + ".pack"));
        Assert.True(File.Exists(large + ".idx"));
        Assert.Equal(4, Directory.GetFiles(packDirectory).Length);
        Assert.Equal(0, LooseObjects(copy));
        AssertHoldsEveryObject(copy, _host.FixtureGitPath("main"));
        Assert.Equal(await AnswersAsync(reference), await AnswersAsync(_host));
    }

    [Fact]
    public async Task A_copy_at_the_threshold_is_not_repacked()
    {
        var packs = await CyclesAsync(_host, PackThreshold);

        Assert.Equal<int>([1, 2, 3], packs);
        Assert.NotEqual(0, LooseObjects(_host.ClonePath("alpha", "main")));
    }

    /// <summary>
    ///     Switched off, a copy past both thresholds is left as the fetches made it. Raising one threshold
    ///     cannot do this, because a copy is repacked when it crosses either.
    /// </summary>
    [Fact]
    public async Task A_repack_switched_off_leaves_a_copy_past_both_thresholds_alone()
    {
        // Off only here, where the switch is the subject; the shipped default is on.
        using var host = new TestHost(SearchEngine.Substring, (LocalCopyRepack.PackThresholdSetting, PackThreshold),
            (LocalCopyRepack.LooseObjectThresholdSetting, 1), (LocalCopyRepack.EnabledSetting, false));

        var packs = await CyclesAsync(host, PackThreshold + 1);

        Assert.Equal<int>([1, 2, 3, 4], packs);
        Assert.True(LooseObjects(host.ClonePath("alpha", "main")) > 1);
    }

    /// <summary>
    ///     A first clone is never repacked, however far past a threshold it arrives: from a real remote it
    ///     is one pack already, and it is the fetch that makes a copy due. The fetch after it, bringing
    ///     nothing new, then repacks the same copy on its loose objects alone.
    /// </summary>
    [Fact]
    public async Task A_first_clone_is_not_repacked_and_the_fetch_after_it_is()
    {
        using var host = new TestHost(SearchEngine.Substring, (LocalCopyRepack.LooseObjectThresholdSetting, 1));
        string source = host.CreateGitRepository("main", Files(0));
        host.CommitToGitRepository("main", Files(1));
        await host.CreateProjectAsync("alpha");
        await host.AddRepositoryAsync("alpha", "main", source);
        await host.RefreshAsync("alpha");
        string copy = host.ClonePath("alpha", "main");

        Assert.True(LooseObjects(copy) > 1);
        Assert.Equal(0, Packs(copy));

        await host.RefreshAsync("alpha");

        AssertRepacked(copy);
        AssertHoldsEveryObject(copy, host.FixtureGitPath("main"));
    }

    /// <summary>
    ///     An old pack that cannot be deleted once the new one is in place — held open here, as libgit2
    ///     holds a pack it has mapped — leaves a copy that is whole and a refresh that succeeds, and says
    ///     so to the operator. The next fetch deletes it once it is released, well below the threshold:
    ///     left to the next repack, it doubled the copy until one ran. Windows only, because only Windows
    ///     refuses to delete a file held open.
    /// </summary>
    [Fact]
    public async Task An_old_pack_that_cannot_be_deleted_is_deleted_by_the_next_fetch()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete a file held open.");
        await CyclesAsync(_host, PackThreshold);
        string copy = _host.ClonePath("alpha", "main");
        string held = Directory.GetFiles(Path.Combine(copy, "objects", "pack"), "pack-*.pack")[0];
        string record = Path.Combine(copy, "codeexplorer-repack-leftovers");

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            _host.CommitToGitRepository("main", Files(PackThreshold + 1));
            await _host.RefreshAsync("alpha");

            Assert.True(File.Exists(held));
            Assert.True(File.Exists(record));
            Assert.Equal(0, LooseObjects(copy));
            AssertHoldsEveryObject(copy, _host.FixtureGitPath("main"));
            _host.Logs.Only(LogLevel.Warning, "could not be deleted");
        }

        string[] repacked = Directory.GetFiles(Path.Combine(copy, "objects", "pack"), "*.idx");
        _host.CommitToGitRepository("main", Files(PackThreshold + 2));
        await _host.RefreshAsync("alpha");

        // The repacked pack and the one this fetch brought, which is under the threshold, so no repack
        // ran: the fetch deleted the leftover on its own.
        Assert.False(File.Exists(held));
        Assert.False(File.Exists(record));
        Assert.True(File.Exists(Assert.Single(repacked)));
        Assert.Equal(2, Packs(copy));
        AssertHoldsEveryObject(copy, _host.FixtureGitPath("main"));
    }

    /// <summary>
    ///     A leftover is deleted only while the pack that took its objects is there. Here that pack is
    ///     gone, as a later repack folding it would leave it, so nothing says the leftover's objects are
    ///     anywhere else: it stays, and the line naming it is forgotten.
    /// </summary>
    [Fact]
    public async Task A_leftover_whose_superseding_pack_is_gone_is_kept()
    {
        await CyclesAsync(_host, 1);
        string copy = _host.ClonePath("alpha", "main");
        string pack = Directory.GetFiles(Path.Combine(copy, "objects", "pack"), "*.pack").Single();
        string record = Path.Combine(copy, "codeexplorer-repack-leftovers");
        await File.WriteAllLinesAsync(record,
        [
            $"pack-0000000000000000000000000000000000000000\t{Path.GetFileName(pack)}",
            "..\t../../HEAD"
        ], Ct);

        _host.CommitToGitRepository("main", Files(2));
        await _host.RefreshAsync("alpha");

        Assert.True(File.Exists(pack));
        Assert.True(File.Exists(Path.Combine(copy, "HEAD")));
        Assert.False(File.Exists(record));
        AssertHoldsEveryObject(copy, _host.FixtureGitPath("main"));
    }

    /// <summary>
    ///     A staging folder is what a repack leaves when the process dies under it: a pack half written,
    ///     outside <c>objects</c>. The next fetch removes it whether or not it repacks, and the copy
    ///     is read as before.
    /// </summary>
    [Fact]
    public async Task A_staging_folder_left_by_a_crashed_repack_is_removed_by_the_next_fetch()
    {
        await CyclesAsync(_host, 1);
        string copy = _host.ClonePath("alpha", "main");
        string staging = Path.Combine(copy, "codeexplorer-repack");
        Directory.CreateDirectory(staging);
        string partial = Path.Combine(staging, "pack-0000000000000000000000000000000000000000.pack");
        await File.WriteAllTextAsync(partial, "half a pack", Ct);
        File.SetAttributes(partial, FileAttributes.ReadOnly);

        _host.CommitToGitRepository("main", Files(2));
        var summary = await _host.RefreshAsync("alpha");

        Assert.False(Directory.Exists(staging));
        Assert.Equal(2, Packs(copy));
        Assert.Equal(5, summary.Files);
        AssertHoldsEveryObject(copy, _host.FixtureGitPath("main"));
    }

    /// <summary>
    ///     A pack under a name libgit2 does not write is still an old pack: git maintenance's
    ///     loose-objects task names its packs <c>loose-*</c>, with a <c>.rev</c> beside each, and a real
    ///     copy the git CLI had touched held four of them after a repack that looked only for
    ///     <c>pack-*</c>. A pack marked <c>.keep</c> is the one thing a repack leaves.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pack_under_another_name_is_repacked_and_one_marked_keep_is_left(bool keep)
    {
        await CyclesAsync(_host, 1);
        string copy = _host.ClonePath("alpha", "main");
        string packDirectory = Path.Combine(copy, "objects", "pack");
        string first = Path.ChangeExtension(Directory.GetFiles(packDirectory, "*.pack").Single(), null);
        string renamed = Path.Combine(packDirectory, "loose-" + Path.GetFileName(first)["pack-".Length..]);
        File.Move(first + ".pack", renamed + ".pack");
        File.Move(first + ".idx", renamed + ".idx");
        await File.WriteAllTextAsync(renamed + ".rev", "", Ct);
        if (keep) await File.WriteAllTextAsync(renamed + ".keep", "", Ct);

        // The kept pack is not counted either, so it takes one more fetch to cross the threshold.
        for (int cycle = 2; cycle <= PackThreshold + (keep ? 2 : 1); cycle++)
        {
            _host.CommitToGitRepository("main", Files(cycle));
            await _host.RefreshAsync("alpha");
        }

        Assert.Equal(keep, File.Exists(renamed + ".pack"));
        Assert.Equal(keep, File.Exists(renamed + ".rev"));
        if (!keep) AssertRepacked(copy);
        else Assert.Equal(2, Packs(copy));
        AssertHoldsEveryObject(copy, _host.FixtureGitPath("main"));
    }

    /// <summary>
    ///     A zero would mean a repack after every fetch, or never, and the operator meant neither; the
    ///     server refuses to start over it, naming the setting. Thrown first by GitClones, it let the
    ///     server start and then failed every page and removal that needed that singleton.
    /// </summary>
    [Theory]
    [InlineData("Git:RepackPackThreshold")]
    [InlineData("Git:RepackLooseObjectThreshold")]
    public void A_threshold_of_zero_stops_the_server_from_starting_with_the_setting_named(string setting)
    {
        using var host = new TestHost(SearchEngine.Substring, (setting, 0));

        var refused = Assert.Throws<InvalidOperationException>(() => host.Services);
        Assert.Contains(setting, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The fixture, a project over it, and <paramref name="cycles" /> commits each followed by a
    ///     refresh. The first refresh clones and is not counted; answers how many packs the copy held
    ///     after each of the fetches that followed.
    /// </summary>
    private static async Task<List<int>> CyclesAsync(TestHost host, int cycles)
    {
        string source = host.CreateGitRepository("main", Files(0));
        await host.CreateProjectAsync("alpha");
        await host.AddRepositoryAsync("alpha", "main", source);
        await host.RefreshAsync("alpha");

        var packs = new List<int>();
        for (int cycle = 1; cycle <= cycles; cycle++)
        {
            host.CommitToGitRepository("main", Files(cycle));
            await host.RefreshAsync("alpha");
            packs.Add(Packs(host.ClonePath("alpha", "main")));
        }

        return packs;
    }

    /// <summary>
    ///     A commit's worth of changes: README.md rewritten, a file of its own added, and a shared file
    ///     grown by a line, so attribution has lines from several commits to tell apart.
    /// </summary>
    private static Dictionary<string, string> Files(int cycle) => new()
    {
        ["README.md"] = $"version {cycle}",
        [$"src/File{cycle}.cs"] = $"class C{cycle} {{}}",
        ["src/Shared.cs"] = string.Concat(Enumerable.Range(0, cycle + 1).Select(line => $"// line {line}\n"))
    };

    /// <summary>
    ///     A first commit many times the size of any later one, so the pack holding it outweighs twice
    ///     the few packs fetched after it.
    /// </summary>
    private static Dictionary<string, string> Large()
    {
        var files = Files(0);
        for (int module = 0; module < 40; module++) files[$"lib/Module{module}.cs"] = $"class Module{module} {{}}";
        return files;
    }

    /// <summary>One pack, its index beside it and nothing else, no loose object and no staging folder.</summary>
    private static void AssertRepacked(string copy)
    {
        string[] files = Directory.GetFiles(Path.Combine(copy, "objects", "pack"));
        Assert.Equal<string?>([".idx", ".pack"], files.Select(Path.GetExtension).Order(StringComparer.Ordinal));
        Assert.Equal(0, LooseObjects(copy));
        Assert.False(Directory.Exists(Path.Combine(copy, "codeexplorer-repack")));
    }

    /// <summary>Every object the fixture holds is in the copy, and every blob reads back byte for byte.</summary>
    private static void AssertHoldsEveryObject(string copy, string fixture)
    {
        using var remote = new Repository(fixture);
        using var local = new Repository(copy);
        int blobs = 0;
        foreach (var remoteObject in remote.ObjectDatabase)
        {
            Assert.True(local.ObjectDatabase.Contains(remoteObject.Id), $"{remoteObject.Id} is missing");
            if (remoteObject is not Blob blob) continue;
            Assert.Equal(Bytes(blob), Bytes(local.Lookup<Blob>(blob.Id)));
            blobs++;
        }

        Assert.NotEqual(0, blobs);
        Assert.Equal(remote.Commits.Count(), local.Commits.Count());
    }

    private static byte[] Bytes(Blob blob)
    {
        using var content = blob.GetContentStream();
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static int Packs(string copy) =>
        Directory.GetFiles(Path.Combine(copy, "objects", "pack"), "*.pack").Length;

    private static int LooseObjects(string copy) =>
        Directory.EnumerateDirectories(Path.Combine(copy, "objects"), "??")
            .Where(directory => Path.GetFileName(directory) is [var high, var low] && char.IsAsciiHexDigit(high) && char.IsAsciiHexDigit(low))
            .Sum(directory => Directory.GetFiles(directory).Length);

    /// <summary>
    ///     What the index says of the files, their lines and who last changed each, the commits and what
    ///     each touched, and the attribution the next build replays onto, as one list per table.
    /// </summary>
    private static async Task<List<List<string>>> AnswersAsync(TestHost host) =>
    [
        await host.ScalarsAsync("alpha", """
                                         SELECT concat_ws(' ', path, size_bytes, line_count) FROM files ORDER BY path
                                         """),
        await host.ScalarsAsync("alpha", """
                                         SELECT concat_ws(' ', f.path, l.line_number, coalesce(c.sha, '-'), l.content)
                                         FROM lines l JOIN files f USING (file_id)
                                         LEFT JOIN commits c ON c.commit_id = l.commit_id
                                         ORDER BY f.path, l.line_number
                                         """),
        await host.ScalarsAsync("alpha", "SELECT concat_ws(' ', sha, subject) FROM commits ORDER BY commit_id"),
        await host.ScalarsAsync("alpha", """
                                         SELECT concat_ws(' ', c.sha, cf.path, cf.change_kind, cf.added, cf.deleted)
                                         FROM commit_files cf JOIN commits c USING (commit_id)
                                         ORDER BY 1
                                         """),
        await host.ScalarsAsync("alpha", """
                                         SELECT concat_ws(' ', a.repo_slug, a.path, a.start_line, a.end_line, c.sha)
                                         FROM attribution a JOIN commits c USING (commit_id)
                                         ORDER BY 1
                                         """)
    ];
}
