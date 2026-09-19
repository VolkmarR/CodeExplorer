using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     How much of a file's declarations this was in a position to read at all, which is what keeps an
///     empty list from reading as "this file declares nothing" — the one sentence a declaration answer
///     must never say by accident.
///     One field and not a pair of flags: only three of the four combinations two booleans can spell
///     are reachable — the fallback profile reads the C-family shapes, so an extension no profile
///     covers is never also unreadable — and a fourth state that compiles is a fourth state a caller
///     can render the wrong sentence for.
/// </summary>
public enum DeclarationCoverage
{
    /// <summary>
    ///     No profile claims the extension, so the file was read with the conservative default shapes.
    ///     A list may still come back; it is thinner than a profiled language's would be.
    /// </summary>
    Unprofiled,

    /// <summary>
    ///     The language is covered, and its declarations are not something this can read from a line —
    ///     CSS. Nothing was scanned, which is not the same as having scanned and found nothing.
    /// </summary>
    Unreadable,

    /// <summary>The language is covered and its declaration shapes were read.</summary>
    Read
}

/// <summary>
///     One name a file introduces, as CONTEXT.md's <em>Declaration</em> defines the word: the line
///     that introduces it, the line's own text, and what it turned out to declare. Either name may be
///     null — a member declaration names no type and a type declaration no member — and a line that
///     reads as both fills both.
///     <see cref="Role" /> is which side of a declaration/implementation split the line sits on, for
///     the languages that have one, and null where the language has no split or the scan could not
///     place the line. <see cref="Evidence" /> is how the reading was reached (ADR-0008), so a list
///     read from line shape cannot read like one a parser produced.
/// </summary>
public sealed record FileDeclaration(
    int LineNumber,
    string Text,
    string? Type,
    string? Member,
    DeclarationRole? Role,
    Evidence Evidence);

/// <summary>
///     What one file declares. <see cref="Coverage" /> is what an empty list means, and
///     <see cref="Capped" /> says the list stopped at <see cref="FileDeclarations.MaxDeclarations" />
///     rather than at the end of the file.
///     <see cref="Offset" /> is how many declarations were skipped to reach this page, so a caller can
///     say where the listing starts and what to ask for next. An empty list with an offset past every
///     declaration the file has is the end of the paging and not a file that declares nothing — two
///     answers that must not read alike.
/// </summary>
public sealed record DeclarationsResult(
    string QualifiedPath,
    string LanguageName,
    DeclarationCoverage Coverage,
    bool Capped,
    int Offset,
    IReadOnlyList<FileDeclaration> Declarations) : Outcome;

/// <summary>
///     What a file declares, read from the index for the file page's own use. The same division as
///     every other search here: DuckDB narrows the file to the lines that could be a declaration in
///     the language it is written in, and the file's analyser then says what each of them declares
///     (ADR-0008).
///     It is a file-scoped read and not <c>find_definition</c> turned around. That search takes a
///     symbol and looks across the project for it; this takes a path and reports its own lines in the
///     order they are written, which is the question the page beside the code asks and the one no
///     tool answered.
///     Textual throughout, and therefore evidence rather than proof: a declaration form no profile
///     knows is one this does not find rather than one that is not there, and the reply says as much.
/// </summary>
public sealed class FileDeclarations(ProjectIndexes indexes)
{
    /// <summary>Named on the search telemetry, so a dashboard can tell this apart from a symbol search.</summary>
    public const string Engine = "file declarations";

    /// <summary>
    ///     How many declarations one answer carries. Past the first few hundred the list has stopped
    ///     being the answer to "what is in this file", so the page ends here and the reply says it was
    ///     cut rather than reading as complete. It is a page and not a ceiling: the declarations past
    ///     it are reached with an offset, because a long-lived core class is as likely to be behind
    ///     this number as a generated one, and a file no one can page through is a file no one can see
    ///     the back half of (#112).
    /// </summary>
    public const int MaxDeclarations = 500;

    /// <summary>
    ///     Every read of a file's declarations goes through here, which is what makes this the one
    ///     place such a read is recorded. <paramref name="offset" /> is how many declarations to skip
    ///     before the page, in file order; negative is read as none.
    /// </summary>
    public async Task<Outcome> ForFileAsync(string slug, string path, int offset,
        CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await ReadAsync(slug, path, Math.Max(0, offset), cancellationToken);
        if (outcome is DeclarationsResult result) recording.Matched(Engine, 1, result.Declarations.Count);
        else recording.Problem();
        return outcome;
    }

    private Task<Outcome> ReadAsync(string slug, string path, int offset, CancellationToken cancellationToken) =>
        IndexReader.OverFileAsync(indexes, slug, path, true, async (index, file, token) =>
        {
            string extension = Languages.ExtensionOf(file.QualifiedPath);
            var analyzer = Languages.Default.For(extension);
            // One lookup answers both: what to call the language, and whether a profile claims the
            // extension at all. Asked twice they could only ever disagree by accident.
            var (name, profiled) = Languages.Name(extension);

            var parameters = new List<DuckDBParameter> { new("f", file.FileId) };
            // Null for a language that declares nothing this can read, which is not a test that is
            // always false and must not become one: CSS is not scanned at all rather than scanned for
            // every line of it, and the answer says the scan never ran.
            if (SearchQuery.CandidateTest(analyzer.DeclarationCandidates, "d", parameters) is not { } test)
                return new DeclarationsResult(file.QualifiedPath, name, DeclarationCoverage.Unreadable, false,
                    offset, []);

            // Every line of the file, each saying whether it could be a declaration, in one read. The
            // lines between the candidates are not waste: placing a candidate means knowing what the
            // lines above it left open, so a declaration inside a commented-out block is not one and a
            // Delphi routine below `implementation` is known to be the body. Asked as two queries —
            // the candidates, then the lines above them — the file was scanned twice and every
            // candidate's text crossed the boundary twice.
            var declarations = new List<FileDeclaration>();
            bool scanCutShort = false;
            // Declarations seen, including the ones the offset skips. The skipping happens here and not
            // in SQL because a candidate line is only a declaration once its analyser has placed it —
            // the engine cannot count what it cannot classify, so the page is taken from the walk.
            int seen = 0;
            using (var command = index.Connection.Query($"""
                                                         SELECT line_number, content, ({test}) AS wanted
                                                         FROM lines WHERE file_id = $f
                                                         ORDER BY line_number
                                                         """, parameters))
            {
                await FilePositions.WalkAsync(command, analyzer, line =>
                {
                    if (!line.Wanted) return true;
                    var declared = analyzer.Declares(line.Position, line.Content);
                    // The engine narrowed the file to lines shaped like a declaration; this is what
                    // says the shape declares a name rather than merely looking like it might.
                    if (declared.Value is not { } what) return true;
                    // The more specific of the two names, which is the one to ask about: a
                    // commented-out `// public void Removed() { }` is shaped exactly like the live
                    // declaration, and only where the name sits tells them apart.
                    string named = what.Member ?? what.Type!;
                    if (SearchQuery.OnlyInProse(analyzer, line.Position, line.Content, named)) return true;

                    if (seen++ < offset) return true;

                    declarations.Add(new FileDeclaration(line.LineNumber, line.Content, what.Type,
                        what.Member, what.Role, declared.Evidence));
                    // One past the ceiling tells a list that ends here from one cut short, and it is
                    // also where the reading stops: the lines below cannot reach the answer, and
                    // placing each of them costs a regex and a walk of the line.
                    if (declarations.Count <= MaxDeclarations) return true;
                    scanCutShort = true;
                    return false;
                }, token);
            }

            if (scanCutShort) declarations.RemoveAt(declarations.Count - 1);

            return new DeclarationsResult(file.QualifiedPath, name,
                profiled ? DeclarationCoverage.Read : DeclarationCoverage.Unprofiled,
                scanCutShort, offset, declarations);
        }, cancellationToken);
}
