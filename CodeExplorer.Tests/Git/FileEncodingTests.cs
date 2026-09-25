using System.Text;
using CodeExplorer.Git;
using CodeExplorer.Index;
using ModelContextProtocol.Client;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     How a committed file's bytes become the text the index holds (#236). VO and X# sources are
///     often saved as Windows-1252, and decoding those as UTF-8 turned every umlaut into U+FFFD, so
///     neither search found the word and <c>read_file</c> showed replacement characters.
/// </summary>
public sealed class FileEncodingTests : IDisposable
{
    /// <summary>"Größe" in Windows-1252: ö is 0xF6 and ß is 0xDF, neither of which is valid UTF-8 on its own.</summary>
    private static readonly byte[] Legacy =
        [.. "// Gr"u8, 0xF6, 0xDF, .. "e der Liste\r\nreturn Gr"u8, 0xF6, 0xDF, .. "e\r\n"u8];

    private TestHost? _host;

    public void Dispose() => _host?.Dispose();

    [Theory]
    [InlineData(SearchEngine.Fts)]
    [InlineData(SearchEngine.Substring)]
    public async Task A_windows_1252_file_is_found_and_read_as_written(SearchEngine engine)
    {
        _host = new TestHost(engine);
        await _host.CreateProjectAsync("alpha");
        await _host.AddRepositoryAsync("alpha", "one", _host.CreateGitRepository("one",
            new Dictionary<string, byte[]>
            {
                ["src/Legacy.prg"] = Legacy,
                ["src/Modern.prg"] = "// Größe, Maß und Übermaß\r\nlocal cText := \"naïve\"\n"u8.ToArray(),
                // libgit2 calls any blob with a UTF-16 mark binary, and this change leaves that call alone.
                ["src/Wide.prg"] = [.. Encoding.Unicode.Preamble, .. Encoding.Unicode.GetBytes("// Größe\r\n")]
            }));
        await _host.RefreshAsync("alpha");
        await using var client = await _host.ConnectAsync("alpha");

        string found = await TestHost.CallAsync(client, "grep", new Dictionary<string, object?> { ["query"] = "Größe" });
        Assert.Contains("one/src/Legacy.prg  -  2 matches", found);
        Assert.Contains("1: // Größe der Liste", found);
        Assert.Contains("one/src/Modern.prg", found);
        Assert.DoesNotContain("Wide.prg", found);
        Assert.DoesNotContain(ReplacementCharacter, found);

        string legacy = await ReadAsync(client, "one/src/Legacy.prg");
        Assert.Contains("one/src/Legacy.prg  -  2 lines", legacy);
        Assert.Contains("2  return Größe", legacy);

        // Valid UTF-8 stays UTF-8: decoding it as Windows-1252 would read each umlaut as two letters.
        string modern = await ReadAsync(client, "one/src/Modern.prg");
        Assert.Contains("one/src/Modern.prg  -  2 lines", modern);
        Assert.Contains("1  // Größe, Maß und Übermaß", modern);
        Assert.Contains("2  local cText := \"naïve\"", modern);
    }

    /// <summary>
    ///     Asked of the decoder rather than through a search: libgit2 classifies any blob with a UTF-16
    ///     mark as binary, so the index skips such a file before it is ever decoded. The mark still has
    ///     to win over the UTF-8 check, which UTF-16 text with ASCII in it would otherwise fail and
    ///     send to Windows-1252.
    /// </summary>
    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("utf-8")]
    public void A_byte_order_mark_decides_the_encoding_and_is_not_part_of_the_text(string name)
    {
        var encoding = Encoding.GetEncoding(name);
        byte[] content = [.. encoding.Preamble, .. encoding.GetBytes("// Größe\r\nreturn 1\n")];

        Assert.Equal("// Größe\r\nreturn 1\n", BlobText.Decode(content));
    }

    /// <summary>What a UTF-8 decoder puts in place of a byte it cannot read.</summary>
    private const char ReplacementCharacter = (char)0xFFFD;

    private static Task<string> ReadAsync(McpClient client, params string[] paths) =>
        TestHost.CallAsync(client, "read_file", new Dictionary<string, object?> { ["paths"] = paths });
}
