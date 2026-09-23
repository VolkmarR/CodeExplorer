using System.Globalization;
using CodeExplorer.Language;
using CodeExplorer.Reading;
using DuckDB.NET.Data;

namespace CodeExplorer.Search;

/// <summary>
///     Where each line a search cares about stands in its own file: inside a block comment or a
///     literal opened further up, on which side of a declaration/implementation split, or outside
///     everything (#53, ADR-0008). Without it a commented-out block reads as a comment on its first
///     line and as calls, writes and declarations on every line after it — under the headings an
///     agent trusts most.
///     One walk per file and not one per line asked about, which is what decides the cost: the lines
///     above are read once and the position is recorded for every wanted line as the walk passes it.
///     The whole answer is one query, the lines of every file read in one pass.
///     It sits here rather than in one of the two searches that need it because the walk is the same
///     walk — a second copy could read one file two ways, and the language seam exists so that one
///     answer can be given about a line however it was reached.
/// </summary>
internal static class FilePositions
{
    /// <summary>
    ///     How far into a file the lines above a wanted one are read. The scan has to start at line 1 —
    ///     there is no point further down that can be known to be outside everything — so the only
    ///     bound available is where it stops.
    ///     Twenty thousand lines covers every hand-written file and most generated ones, at a read of a
    ///     few hundred kilobytes for the largest of them. Past it the position is reported as unknown
    ///     and what sits on those lines is kept and listed as unplaced: a file long enough to hit this
    ///     is one where a guess would be wrong quietly and often.
    /// </summary>
    public const int MaxScanLines = 20_000;

    /// <summary>
    ///     One line as the walk passes it: where it is, what it says, whether the caller asked about
    ///     it, and where the file stood at the start of it.
    /// </summary>
    public readonly record struct WalkedLine(int LineNumber, string Content, bool Wanted, FilePosition Position);

    /// <summary>
    ///     Walks one file's lines in order, handing <paramref name="visit" /> each of them with the
    ///     position the file stood in at the start of it, and stopping early when it says to.
    ///     This is the other shape the same walk is needed in. <see cref="ReadAsync" /> answers for a
    ///     set of lines already known — which is what a search across files has — where a caller
    ///     reading one file cannot know which lines it wants until the rows arrive, because that is
    ///     what the row says. Rather than query the file twice, once for the candidates and once for
    ///     the lines above them, it selects every line with a column saying which is which and walks
    ///     the result. The walk itself is this method and not the caller's, for the reason the class
    ///     comment gives: a second copy could read one file two ways.
    ///     Stopping early is what makes it cheaper and not merely tidier — a caller that has filled
    ///     its answer by line 2000 of a generated file reads no further.
    /// </summary>
    /// <param name="command">
    ///     The caller's own query, selecting <c>line_number</c>, <c>content</c> and a boolean
    ///     <c>wanted</c>, ordered by <c>line_number</c> ascending, over exactly one file.
    /// </param>
    /// <param name="analyzer">The analyser the file's extension resolved to.</param>
    /// <param name="visit">
    ///     Called for every line in order; returns false to stop the walk. Called for unwanted lines
    ///     too, because only the caller knows whether it has seen enough.
    /// </param>
    /// <param name="cancellationToken">Threaded to the command, as every async path here is.</param>
    public static async Task WalkAsync(DuckDBCommand command, ILanguageAnalyzer analyzer,
        Func<WalkedLine, bool> visit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(visit);

        await using var reader = await command.ReaderAsync(cancellationToken);
        var position = analyzer.Start;
        while (await reader.ReadAsync(cancellationToken))
        {
            int lineNumber = reader.Int32("line_number");
            string content = reader.Text("content");
            // Past the bound the line is still handed over, with its position unknown rather than
            // guessed — the same policy ReadAsync applies by leaving the line out of its answer, which
            // a caller reads back as unknown. Dropping the line instead would lose whatever it
            // declares, which is a quiet wrong answer where an unplaced one is merely a thinner right
            // one.
            bool placed = lineNumber <= MaxScanLines;
            if (!visit(new WalkedLine(lineNumber, content, reader.FlagOrFalse("wanted"),
                    placed ? position : FilePosition.Unknown)))
                return;

            // Nothing below the bound is placed, so the walk stops advancing rather than paying for a
            // state no caller will be given.
            if (placed) position = analyzer.After(position, content);
        }
    }

    /// <summary>
    ///     The position at the start of each wanted line. A line past <see cref="MaxScanLines" /> is
    ///     left out of the result, which is <see cref="FilePosition.Unknown" /> to a caller reading it
    ///     with <c>GetValueOrDefault</c> — the answer that keeps what is on the line rather than
    ///     placing it from a scan that never reached it.
    /// </summary>
    /// <param name="connection">Already bound to the project being searched.</param>
    /// <param name="wanted">
    ///     The lines to answer for, each with the analyser its file resolved to. File ids must come
    ///     from a query and never from a request: they are inlined into the SQL below.
    /// </param>
    /// <param name="cancellationToken">Threaded to the command, as every async path here is.</param>
    public static async Task<Dictionary<(long File, int Line), FilePosition>> ReadAsync(
        DuckDBConnection connection,
        IEnumerable<(long FileId, ILanguageAnalyzer Analyzer, int LineNumber)> wanted,
        CancellationToken cancellationToken)
    {
        var positions = new Dictionary<(long, int), FilePosition>();
        var files = wanted.GroupBy(line => line.FileId)
            // A file whose every wanted line is past the bound is not read at all: scanning the twenty
            // thousand lines above them would end in the same unknown position it started from.
            .Where(group => group.Min(line => line.LineNumber) <= MaxScanLines)
            .ToDictionary(
                group => group.Key,
                group => (group.First().Analyzer,
                    Through: Math.Min(group.Max(line => line.LineNumber), MaxScanLines),
                    Wanted: group.Select(line => line.LineNumber).ToHashSet()));
        if (files.Count == 0) return positions;

        // The ids and line numbers came from a query and never from a request, so inlining them is
        // safe. A join against the bounds rather than a chain of ORed ranges, which DuckDB has to evaluate
        // per row of `lines` where this is a hash probe; and one bound per file rather than one for
        // all of them, so that a file whose last wanted line is 12 is not read to the end because
        // another file's is on line 4000.
        string bounds = string.Join(", ", files.Select(file => string.Create(CultureInfo.InvariantCulture,
            $"({file.Key}, {file.Value.Through})")));
        await using var command = connection.Query($"""
                                                    SELECT l.file_id, l.line_number, l.content
                                                    FROM lines l JOIN (VALUES {bounds}) AS b(file_id, through)
                                                      ON l.file_id = b.file_id AND l.line_number <= b.through
                                                    ORDER BY l.file_id, l.line_number
                                                    """, []);
        await using var reader = await command.ReaderAsync(cancellationToken);

        long walking = -1;
        // Seeded from any file so the walk's state is definitely assigned; the first row replaces it,
        // because no file id is -1.
        var scanning = files.Values.First();
        FilePosition position = FilePosition.Unknown;
        while (await reader.ReadAsync(cancellationToken))
        {
            long fileId = reader.Int64("file_id");
            // The rows arrive grouped by file and in line order, so a new file id is the top of one.
            if (fileId != walking)
            {
                walking = fileId;
                scanning = files[fileId];
                position = scanning.Analyzer.Start;
            }

            int lineNumber = reader.Int32("line_number");
            if (scanning.Wanted.Contains(lineNumber)) positions[(fileId, lineNumber)] = position;
            // Nothing reads the position after the last line asked for, and on a minified bundle that
            // line is the whole file.
            if (lineNumber < scanning.Through)
                position = scanning.Analyzer.After(position, reader.Text("content"));
        }

        return positions;
    }
}
