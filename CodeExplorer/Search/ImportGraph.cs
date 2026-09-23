using CodeExplorer.Infrastructure;
using CodeExplorer.Language;
using CodeExplorer.Reading;
using DuckDB.NET.Data;

namespace CodeExplorer.Search;

/// <summary>
///     One name a file imports: as it was written, and what it turned out to be.
///     <see cref="TargetPath" /> and <see cref="Unresolved" /> are exclusive — an edge either names a
///     file in this project or says why it does not — and the raw <see cref="Name" /> is there either
///     way, because an edge reported only when it resolves would tell a reader the file depends on
///     nothing when it depends on something this could not place.
/// </summary>
public sealed record ImportedFrom(
    string Name,
    ImportShape Shape,
    int LineNumber,
    string? TargetPath,
    string? Unresolved,
    Evidence Evidence);

/// <summary>
///     What a file imports, and what this server was able to say about the question at all.
///     <see cref="Profiled" /> and <see cref="HasImports" /> are the two ways an empty list can mean
///     something other than "this file imports nothing": an extension no profile covers was never
///     read for imports, and a language with no import concept has none to read. Three answers, kept
///     apart, because they send an agent to three different places.
///     <see cref="Capped" /> says the list stopped at <see cref="ImportGraph.MaxEdges" /> rather than
///     at the end of the file. It is decided where the query runs, because the ceiling is that
///     query's, and every reader of the answer would otherwise re-derive the same comparison.
/// </summary>
public sealed record ImportsResult(
    string QualifiedPath,
    string LanguageName,
    bool Profiled,
    bool HasImports,
    string? Module,
    bool Capped,
    IReadOnlyList<ImportedFrom> Imports) : Outcome;

/// <summary>
///     What a file imports, read from the edges the build recorded (#55).
///     The reverse direction was a tool here too and is gone (#160). It resolved a name only where
///     exactly one file declared it, and across three evaluation runs on two real codebases that
///     condition almost never held: X# names types by global visibility and writes no import line at
///     all, a C# namespace spread over sibling files resolved to none of them, and a TypeScript path
///     alias resolved to nothing. It answered empty or ambiguous every time it was asked, and every
///     agent that asked fell back to <c>list_declarations</c> plus <c>find_references</c> — which is
///     the lookup that does not depend on a name resolving, and is now the only one offered.
///     Every answer is evidence and not proof, in the sense CONTEXT.md gives the word: an import line
///     is text, a text profile is what read it, and a name that resolved did so against what another
///     file declared itself to be. It says so, and it says which edges it could not place rather than
///     leaving them out.
/// </summary>
public sealed class ImportGraph(IndexReaders readers)
{
    /// <summary>Named on the search telemetry, so a dashboard can tell this apart from a line scan.</summary>
    public const string Engine = "import graph";

    /// <summary>
    ///     How many edges one answer carries. A file with more imports than this is generated or is a
    ///     barrel that re-exports a package, and past the first few hundred the list has stopped being
    ///     the answer to "what does this depend on". The query reads one row past it, which is what
    ///     tells a list that ends here from one that was cut short — see <see cref="Trim" />.
    /// </summary>
    public const int MaxEdges = 500;

    public Task<Outcome> ImportsAsync(string slug, string path, CancellationToken cancellationToken) =>
        Telemetry.Search(slug, Engine, () => ReadImportsAsync(slug, path, cancellationToken),
            (ImportsResult result) => new Telemetry.Measured(1, result.Imports.Count));

    private Task<Outcome> ReadImportsAsync(string slug, string path, CancellationToken cancellationToken) =>
        readers.OverFileAsync(slug, path, true, async (index, file, token) =>
        {
            string extension = Languages.ExtensionOf(file.QualifiedPath);
            var analyzer = Languages.Default.For(extension);
            // One lookup answers both: what to call the language, and whether a profile claims the
            // extension at all. Asked twice they could only ever disagree by accident.
            var (name, profiled) = Languages.Name(extension);

            var edges = new List<ImportedFrom>();
            await using var command = index.Connection.Query($"""
                                                              SELECT i.name, i.shape, i.line_number, i.unresolved,
                                                                     i.evidence, t.qualified_path AS target_path
                                                              FROM imports i
                                                              LEFT JOIN files t ON t.file_id = i.target_file
                                                              WHERE i.file_id = $f
                                                              ORDER BY i.line_number, i.import_id
                                                              LIMIT {MaxEdges + 1}
                                                              """, [new DuckDBParameter("f", file.FileId)]);
            await using var reader = await command.ReaderAsync(token);
            while (await reader.ReadAsync(token))
                edges.Add(new ImportedFrom(reader.Text("name"), ImportColumns.Shape(reader.Text("shape")),
                    reader.Int32("line_number"), reader.TextOrNull("target_path"),
                    reader.TextOrNull("unresolved"), ImportColumns.Strength(reader.Text("evidence"))));

            bool capped = Trim(edges);
            return new ImportsResult(file.QualifiedPath, name, profiled, analyzer.HasImports, file.Module,
                capped, edges);
        }, cancellationToken);

    /// <summary>
    ///     Cuts the list back to <see cref="MaxEdges" /> and says whether there was anything to cut.
    ///     The query asks for one row more than it reports, because a list that merely reaches the
    ///     ceiling is indistinguishable from one the ceiling cut short: a file with exactly
    ///     <see cref="MaxEdges" /> imports would otherwise be told there are more.
    /// </summary>
    private static bool Trim(List<ImportedFrom> edges)
    {
        if (edges.Count <= MaxEdges) return false;
        edges.RemoveRange(MaxEdges, edges.Count - MaxEdges);
        return true;
    }
}
