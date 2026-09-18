using DuckDB.NET.Data;

namespace CodeExplorer;

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
///     What one file declares. <see cref="Profiled" /> and <see cref="ReadsDeclarations" /> are the two ways an
///     empty list means something other than "this file declares nothing": an extension no profile
///     covers was read with the conservative default shapes, and a language whose declarations this
///     cannot read was not scanned at all. Three answers, kept apart, because they send a reader to
///     three different places — the same distinction the import panels draw beside them.
///     <see cref="Capped" /> says the list is short of what the file declares, whichever ceiling it
///     was that cut it (<see cref="FileDeclarations.MaxDeclarations" />).
/// </summary>
public sealed record DeclarationsResult(
    string QualifiedPath,
    string LanguageName,
    bool Profiled,
    bool ReadsDeclarations,
    bool Capped,
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
    ///     How many declarations one answer carries. A file declaring more than this is generated, and
    ///     past the first few hundred the list has stopped being the answer to "what is in this file" —
    ///     the reply says it was cut rather than reading as complete.
    /// </summary>
    public const int MaxDeclarations = 500;

    /// <summary>
    ///     How many candidate lines are read before they are placed. A candidate is already a line
    ///     shaped like a declaration in this one file, so this is generous for anything but a
    ///     generated file or a minified bundle — where the declarations the ceiling costs would have
    ///     been thrown away by <see cref="MaxDeclarations" /> regardless, and the reply says the list
    ///     is short either way.
    /// </summary>
    private const int MaxCandidates = 5_000;

    /// <summary>
    ///     Every read of a file's declarations goes through here, which is what makes this the one
    ///     place such a read is recorded.
    /// </summary>
    public async Task<Outcome> ForFileAsync(string slug, string path, CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await ReadAsync(slug, path, cancellationToken);
        if (outcome is DeclarationsResult result) recording.Matched(Engine, 1, result.Declarations.Count);
        else recording.Problem();
        return outcome;
    }

    private Task<Outcome> ReadAsync(string slug, string path, CancellationToken cancellationToken) =>
        IndexReader.OverFileAsync(indexes, slug, path, true, async (index, file, token) =>
        {
            string extension = Languages.ExtensionOf(file.QualifiedPath);
            var analyzer = Languages.Default.For(extension);
            // One lookup answers both: what to call the language, and whether a profile claims the
            // extension at all. Asked twice they could only ever disagree by accident.
            var (name, profiled) = Languages.Name(extension);

            var parameters = new List<DuckDBParameter> { new("f", file.FileId) };
            // Null for a language that declares nothing this can read, which is not an empty
            // narrowing and must not become one: CSS is scanned for no lines rather than for every
            // line of it, and the answer says the scan never ran.
            if (SearchQuery.Narrowing(analyzer.DeclarationCandidates, "d", parameters) is not { } narrowing)
                return new DeclarationsResult(file.QualifiedPath, name, profiled, false, false, []);

            var candidates = new List<(int LineNumber, string Content)>();
            using (var command = index.Connection.Query($"""
                                                         SELECT line_number, content FROM lines
                                                         WHERE file_id = $f{narrowing}
                                                         ORDER BY line_number
                                                         LIMIT {MaxCandidates}
                                                         """, parameters))
            using (var reader = await command.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                    candidates.Add((reader.Int32("line_number"), reader.Text("content")));
            }

            // The lines above each candidate, so that a declaration inside a commented-out block is
            // not one, and one below a Delphi `implementation` is known to be the body. The same walk
            // the two symbol searches use, for the same reason: one answer about a line however it
            // was reached.
            var positions = await FilePositions.ReadAsync(index.Connection,
                candidates.Select(line => (file.FileId, analyzer, line.LineNumber)), token);

            var declarations = new List<FileDeclaration>();
            foreach (var (lineNumber, content) in candidates)
            {
                var position = positions.GetValueOrDefault((file.FileId, lineNumber), FilePosition.Unknown);
                var declared = analyzer.Declares(position, content);
                // The engine narrowed the file to lines shaped like a declaration; this is what says
                // the shape declares a name rather than merely looking like it might.
                if (declared.Value is not { } what) continue;
                // The more specific of the two names, which is the one to ask about: a commented-out
                // `// public void Removed() { }` is shaped exactly like the live declaration, and
                // only where the name sits tells them apart.
                string named = what.Member ?? what.Type!;
                if (SearchQuery.OnlyInProse(analyzer, position, content, named)) continue;
                declarations.Add(new FileDeclaration(lineNumber, content, what.Type, what.Member, what.Role,
                    declared.Evidence));
            }

            // Either ceiling leaves a list short of what the file declares, and the panel says the
            // same sentence for both: the candidate read stopped, or the answer did.
            bool capped = candidates.Count == MaxCandidates || declarations.Count > MaxDeclarations;
            if (declarations.Count > MaxDeclarations)
                declarations.RemoveRange(MaxDeclarations, declarations.Count - MaxDeclarations);

            return new DeclarationsResult(file.QualifiedPath, name, profiled, true, capped, declarations);
        }, cancellationToken);
}
