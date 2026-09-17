using DuckDB.NET.Data;

namespace CodeExplorer;

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
/// </summary>
public sealed record ImportsResult(
    string QualifiedPath,
    string LanguageName,
    bool Profiled,
    bool HasImports,
    string? Module,
    IReadOnlyList<ImportedFrom> Imports) : SearchOutcome;

/// <summary>One file that imports the file asked about, and the line that does it.</summary>
public sealed record Dependent(string QualifiedPath, string Name, int LineNumber);

/// <summary>
///     What imports a file. <see cref="ShareTheModule" /> is how many other files declare the same
///     module name as this one: where it is more than zero, a name importing that module resolves to
///     none of them, and the reverse lookup is thinner than the project is — which the reply has to
///     say rather than answer with a short list that reads as complete.
/// </summary>
public sealed record DependentsResult(
    string QualifiedPath,
    string? Module,
    int ShareTheModule,
    int Unplaced,
    IReadOnlyList<Dependent> Dependents) : SearchOutcome;

/// <summary>
///     The two directions of the import graph the build recorded (#55): what a file imports, and what
///     imports it. Both are reads of one table, which is why they are one service — and the second is
///     the half that cannot be got any other way, since an agent can read the top of a file for the
///     first.
///     Every answer is evidence and not proof, in the sense CONTEXT.md gives the word: an import line
///     is text, a text profile is what read it, and a name that resolved did so against what another
///     file declared itself to be. It says so, and it says which edges it could not place rather than
///     leaving them out.
/// </summary>
public sealed class ImportGraph(ProjectIndexes indexes)
{
    /// <summary>Named on the search telemetry, so a dashboard can tell this apart from a line scan.</summary>
    public const string Engine = "import graph";

    /// <summary>
    ///     How many edges one answer carries. A file with more imports than this is generated or is a
    ///     barrel that re-exports a package, and past the first few hundred the list has stopped being
    ///     the answer to "what does this depend on".
    /// </summary>
    public const int MaxEdges = 500;

    public async Task<SearchOutcome> ImportsAsync(string slug, string path, CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await ReadImportsAsync(slug, path, cancellationToken);
        if (outcome is ImportsResult result) recording.Matched(Engine, 1, result.Imports.Count);
        else recording.Problem();
        return outcome;
    }

    public async Task<SearchOutcome> DependentsAsync(string slug, string path, CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await ReadDependentsAsync(slug, path, cancellationToken);
        if (outcome is DependentsResult result)
            recording.Matched(Engine, result.Dependents.Count, result.Dependents.Count);
        else recording.Problem();
        return outcome;
    }

    private Task<SearchOutcome> ReadImportsAsync(string slug, string path, CancellationToken cancellationToken) =>
        OverFileAsync(slug, path, async (index, file, token) =>
        {
            string extension = Languages.ExtensionOf(file.QualifiedPath);
            var analyzer = Languages.Default.For(extension);
            // One lookup answers both: what to call the language, and whether a profile claims the
            // extension at all. Asked twice they could only ever disagree by accident.
            var (name, profiled) = Languages.Name(extension);

            var edges = new List<ImportedFrom>();
            using var command = index.Connection.Query($"""
                                                        SELECT i.name, i.shape, i.line_number, i.unresolved,
                                                               i.evidence, t.qualified_path AS target_path
                                                        FROM imports i
                                                        LEFT JOIN files t ON t.file_id = i.target_file
                                                        WHERE i.file_id = $f
                                                        ORDER BY i.line_number, i.import_id
                                                        LIMIT {MaxEdges}
                                                        """, [new DuckDBParameter("f", file.FileId)]);
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                edges.Add(new ImportedFrom(reader.Text("name"), ImportBuilder.Shape(reader.Text("shape")),
                    reader.Int32("line_number"), reader.TextOrNull("target_path"),
                    reader.TextOrNull("unresolved"), ImportBuilder.Strength(reader.Text("evidence"))));

            return new ImportsResult(file.QualifiedPath, name, profiled, analyzer.HasImports, file.Module,
                edges);
        }, cancellationToken);

    private Task<SearchOutcome> ReadDependentsAsync(string slug, string path,
        CancellationToken cancellationToken) =>
        OverFileAsync(slug, path, async (index, file, token) =>
        {
            var dependents = new List<Dependent>();
            using (var command = index.Connection.Query($"""
                                                         SELECT f.qualified_path, i.name, i.line_number
                                                         FROM imports i JOIN files f USING (file_id)
                                                         WHERE i.target_file = $f
                                                         ORDER BY f.qualified_path, i.line_number
                                                         LIMIT {MaxEdges}
                                                         """, [new DuckDBParameter("f", file.FileId)]))
            using (var reader = await command.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                    dependents.Add(new Dependent(reader.Text("qualified_path"), reader.Text("name"),
                        reader.Int32("line_number")));
            }

            // What makes a short list mean less than it looks: a module several files declare resolves
            // to none of them, so every import of it was left unresolved. Answered from the index
            // rather than guessed at, because "nothing depends on this" is the one sentence a
            // dependency tool must never say by accident.
            int sharing = file.Module is null
                ? 0
                : (int)await index.Connection.CountAsync(
                    "SELECT count(*) FROM files WHERE module IS NOT NULL AND lower(module) = lower($m) AND file_id <> $f",
                    [new DuckDBParameter("m", file.Module), new DuckDBParameter("f", file.FileId)], token);

            // Only when the answer is empty and the module rule has nothing to say about it, which is
            // the one branch the reply prints it in — and it is a scan of the largest new table.
            // Narrowed to the edges whose last path segment spells this file, because a project-wide
            // count is the same number for every file in it: true, and no use to the reader.
            int unplaced = dependents.Count > 0 || sharing > 0
                ? 0
                : (int)await index.Connection.CountAsync(
                    """
                    SELECT count(*) FROM imports
                    WHERE unresolved IS NOT NULL
                      AND lower(split_part(name, '/', -1)) IN (lower($stem), lower($leaf))
                    """,
                    [
                        new DuckDBParameter("stem", Stem(file.QualifiedPath)),
                        new DuckDBParameter("leaf", Leaf(file.QualifiedPath))
                    ], token);

            return new DependentsResult(file.QualifiedPath, file.Module, sharing, unplaced, dependents);
        }, cancellationToken);

    /// <summary>
    ///     Opens the project, resolves the path, and hands the read what it asked for. Both
    ///     directions begin this way, and a second copy of it is a second place for the
    ///     open/refuse/dispose contract to be got wrong.
    /// </summary>
    private async Task<SearchOutcome> OverFileAsync(string slug, string path,
        Func<IndexReader, IndexedFile, CancellationToken, Task<SearchOutcome>> read,
        CancellationToken cancellationToken)
    {
        var open = await IndexReader.OpenAsync(indexes, slug, null, cancellationToken);
        if (open is IndexOpen.Refused refused) return new SearchProblem(refused.Explanation);
        using var index = ((IndexOpen.Opened)open).Reader;

        var (file, explanation) = await index.LocateAsync(path, true, cancellationToken);
        return file is null
            ? new SearchProblem(explanation!)
            : await read(index, file, cancellationToken);
    }

    /// <summary>The file's own name, which is the last thing an unresolved path would have spelled.</summary>
    private static string Leaf(string qualifiedPath) =>
        qualifiedPath[(qualifiedPath.LastIndexOf('/') + 1)..];

    /// <summary>The same without its extension, which is how a module-style specifier spells it.</summary>
    private static string Stem(string qualifiedPath)
    {
        string leaf = Leaf(qualifiedPath);
        int dot = leaf.LastIndexOf('.');
        return dot > 0 ? leaf[..dot] : leaf;
    }
}
