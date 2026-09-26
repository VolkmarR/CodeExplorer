using CodeExplorer.Index;
using LibGit2Sharp;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     A tree entry such as <c>dir:$I30</c> names an NTFS alternate data stream on Windows, and opening
///     one is how a crafted path corrupts a volume or reaches a file under another name (#299). The
///     server never gives it that chance: the clone is bare, so libgit2 writes no tree entry out as a
///     file, and every path the server writes itself is built from a validated slug, a table name or a
///     timestamp. A tree path is only ever text in the index. This pins that: were a tree path ever
///     joined onto a directory, the stream's host <c>dir</c> would appear on the disk.
/// </summary>
public sealed class NtfsStreamPathTests : IDisposable
{
    private const string StreamPath = "dir:$I30/a.cs";

    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_tree_path_naming_an_ntfs_stream_is_indexed_as_text_and_never_touches_the_disk()
    {
        // Written into the object database directly: committing it from a working tree would itself open
        // the stream on this disk, which is the thing under test.
        string fixture = _host.CreateEmptyGitRepository("streams");
        using (var repository = new Repository(fixture))
        {
            var author = new Signature("Author", "author@example.invalid", DateTimeOffset.UnixEpoch);
            var blob = repository.ObjectDatabase.CreateBlob(new MemoryStream("class A {}\n"u8.ToArray()));
            var tree = repository.ObjectDatabase.CreateTree(new TreeDefinition()
                .Add(StreamPath, blob, Mode.NonExecutableFile)
                .Add("b.cs", blob, Mode.NonExecutableFile));
            var commit = repository.ObjectDatabase.CreateCommit(author, author, "Streams", tree, [], false);
            repository.Refs.Add(repository.Refs.Head.TargetIdentifier, commit.Id);
        }

        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "streams", fixture);
        var summary = await _host.RefreshAsync("alpha");

        Assert.Empty(summary.Skipped);
        Assert.Equal(["b.cs", StreamPath], await _host.ScalarsAsync("alpha", "SELECT path FROM files ORDER BY path"));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_host.DataDirectory, "*", SearchOption.AllDirectories),
            entry => Path.GetFileName(entry).Equals("dir", StringComparison.OrdinalIgnoreCase));
    }
}
