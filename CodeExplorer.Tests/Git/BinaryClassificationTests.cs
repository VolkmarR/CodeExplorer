using System.Text;
using CodeExplorer.Index;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     Which committed files a build calls binary, and therefore does not index (#263). The verdict is
///     libgit2's and must stay exactly that: a heuristic of our own, however close, changes which files
///     an agent can find. Pinned before the read was reworked to inflate each blob once, and asserted
///     through the <c>files</c> rows a refresh writes, which is where the verdict becomes visible.
/// </summary>
public sealed class BinaryClassificationTests : IDisposable
{
    private TestHost? _host;

    public void Dispose() => _host?.Dispose();

    [Fact]
    public async Task A_build_calls_binary_exactly_what_libgit2_does()
    {
        _host = new TestHost(SearchEngine.Substring);
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "one", _host.CreateGitRepository("one",
            new Dictionary<string, byte[]>
            {
                ["zero.dat"] = "ab\0cd\n"u8.ToArray(),
                // libgit2 calls any blob with a UTF-16 or UTF-32 mark binary, before looking at a byte of it (#264).
                ["wide.prg"] = [.. Encoding.Unicode.Preamble, .. Encoding.Unicode.GetBytes("// Größe\r\n")],
                ["wide-be.prg"] = [.. Encoding.BigEndianUnicode.Preamble, .. Encoding.BigEndianUnicode.GetBytes("x\n")],
                ["legacy.prg"] = [.. "// Gr"u8, 0xF6, 0xDF, .. "e\r\n"u8],
                ["modern.prg"] = "// Größe\n"u8.ToArray(),
                ["marked.prg"] = [.. Encoding.UTF8.Preamble, .. "// Größe\n"u8],
                ["empty.txt"] = [],
                // No NUL, but more than one control character in every 128 printable ones.
                ["control.txt"] = [.. "abc"u8, 0x01, 0x02, 0x03, .. "\n"u8],
                // A NUL past the first 8000 bytes is not looked at.
                ["late-nul.txt"] = [.. Encoding.ASCII.GetBytes(new string('x', 8000)), 0, .. "\n"u8]
            }));
        await _host.RefreshAsync("alpha");

        var files = await _host.ScalarsAsync("alpha",
            "SELECT path || '|' || coalesce(skip_reason, 'text') FROM files ORDER BY path");
        Assert.Equal([
            "control.txt|binary", "empty.txt|text", "late-nul.txt|text", "legacy.prg|text", "marked.prg|text",
            "modern.prg|text", "wide-be.prg|binary", "wide.prg|binary", "zero.dat|binary"
        ], files);
    }
}
