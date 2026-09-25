using System.Text;
using System.Text.Unicode;

namespace CodeExplorer.Git;

/// <summary>
///     How a committed file's bytes become text (#236). A byte order mark decides, as it did when
///     libgit2's own <c>GetContentText</c> decoded every file. Without one, content that is valid UTF-8
///     is UTF-8, and anything else is Windows-1252: VO and X# sources are routinely saved in it, and
///     decoding them as UTF-8 turned each umlaut into U+FFFD, which neither search nor a reader can
///     get back. No other single-byte code page is guessed at; 1252 is the one these sources use, and
///     it decodes every byte, so no file can come back with replacement characters of its own.
/// </summary>
internal static class BlobText
{
    /// <summary>
    ///     From the provider rather than <see cref="Encoding.GetEncoding(int)" />: .NET ships code page
    ///     1252 only there, and asking the provider directly spares a process-wide registration.
    /// </summary>
    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    /// <summary>
    ///     The encodings a byte order mark can name, longest mark first so the UTF-32 LE mark is not
    ///     read as UTF-16 LE's followed by a NUL. The same set, in the same order, that the
    ///     <see cref="StreamReader" /> behind libgit2's decoding detected, so a file with a mark decodes
    ///     exactly as it did before.
    /// </summary>
    private static readonly Encoding[] Marked =
    [
        new UTF32Encoding(false, true), new UTF32Encoding(true, true), Encoding.UTF8, Encoding.Unicode,
        Encoding.BigEndianUnicode
    ];

    /// <summary>The whole content as text, with any byte order mark removed.</summary>
    public static string Decode(ReadOnlySpan<byte> content)
    {
        foreach (var encoding in Marked)
        {
            var mark = encoding.Preamble;
            if (content.StartsWith(mark)) return encoding.GetString(content[mark.Length..]);
        }

        return Utf8.IsValid(content) ? Encoding.UTF8.GetString(content) : Windows1252.GetString(content);
    }
}
