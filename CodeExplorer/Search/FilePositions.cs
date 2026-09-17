using System.Globalization;
using DuckDB.NET.Data;

namespace CodeExplorer;

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
        using var command = connection.Query($"""
                                                 SELECT l.file_id, l.line_number, l.content
                                                 FROM lines l JOIN (VALUES {bounds}) AS b(file_id, through)
                                                   ON l.file_id = b.file_id AND l.line_number <= b.through
                                                 ORDER BY l.file_id, l.line_number
                                                 """, []);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);

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
