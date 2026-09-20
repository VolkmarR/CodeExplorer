using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     What one file's lines said about its dependencies: the names it imports with the line each was
///     written on, and the module name it declares itself to be.
/// </summary>
/// <param name="Edges">Every name imported, in the order the file writes them.</param>
/// <param name="Module">
///     What the file declares itself to be — a C# <c>namespace</c>, a Delphi <c>unit</c> — or null
///     where it declares nothing. Null is not "no module": X# files declare none at all, and the
///     difference shows up as an import that resolves to nothing rather than as one that is wrong.
/// </param>
public sealed record ReadImports(IReadOnlyList<ImportEdge> Edges, string? Module);

/// <summary>One name a file imports, and where it was written.</summary>
public sealed record ImportEdge(int LineNumber, string Name, ImportShape Shape, Evidence Evidence);

/// <summary>
///     The import edges of a project index (CONTEXT.md, Import): read out of the line walk the build
///     already performs, then resolved to the files they name.
///     The two halves are here together because they are one claim made twice. Extraction says what a
///     file asked for, in the words the file used; resolution says which file in this project that
///     turned out to be, where it can be said at all. Neither is proof — an import line is text and a
///     text profile is what reads it — and an edge that cannot be resolved stays in the answer as the
///     raw name rather than disappearing, because a dropped edge reads as a dependency that is not
///     there.
///     Adding a language is a profile change and touches nothing here: what an import looks like is
///     the analyser's question (ADR-0008), and what is left is resolving a name of one of two shapes.
/// </summary>
public sealed class ImportBuilder
{
    /// <summary>
    ///     How far into a file imports are read. The walk has to start at line 1 — there is no point
    ///     further down that can be known to be outside a comment — so the only bound available is
    ///     where it stops, which is the bound a search already reads a file to.
    ///     Twenty thousand lines covers every hand-written file and most generated ones. Past it a
    ///     file's remaining imports are not recorded; a file that long is a bundle or a data dump,
    ///     and the alternative is a build that walks a million-line generated file for the two
    ///     <c>using</c> lines at the top of it.
    /// </summary>
    public const int MaxScanLines = 20_000;

    /// <summary>
    ///     Why an edge names no file here. They are sentences rather than codes because they are
    ///     printed to an agent as they stand: an edge marked <c>ambiguous</c> tells a reader nothing
    ///     they can act on, and the reply has no room to explain a vocabulary of its own.
    /// </summary>
    public const string Undeclared = "nothing in this project declares this name";

    public const string Several = "several files declare this name";

    public const string NoSuchFile = "no file at this path relative to the importing file";

    public const string Outside = "an address outside this project";

    public const string Package = "names a package, not a file in this project";

    /// <summary>
    ///     Whether the name is an address rather than a path: it carries a URL scheme, or it begins
    ///     with the <c>//</c> that means "whatever scheme this page came in on". Tested as the shape
    ///     of a scheme rather than against a list of the ones anyone has thought of, so that
    ///     <c>node:fs</c> and <c>chrome-extension:</c> are not reported as files the project is
    ///     missing — which is the reading that sends an agent looking for them.
    /// </summary>
    private static bool IsExternal(string name)
    {
        if (name.StartsWith("//", StringComparison.Ordinal)) return true;
        int colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return false;
        if (!char.IsAsciiLetter(name[0])) return false;
        for (int i = 1; i < colon; i++)
            if (!char.IsAsciiLetterOrDigit(name[i]) && name[i] is not ('+' or '.' or '-'))
                return false;
        return true;
    }

    /// <summary>
    ///     Whether the name is a bare specifier: no <c>./</c>, no <c>../</c>, no leading slash. It
    ///     names a package the project depends on rather than a file inside it, and the two are told
    ///     apart because "no file at this path relative to the importing file" sends a reader looking
    ///     for something that was never meant to be there — which every npm dependency in a
    ///     TypeScript project would otherwise be told.
    /// </summary>
    private static bool IsPackage(string name) =>
        !name.StartsWith("./", StringComparison.Ordinal)
        && !name.StartsWith("../", StringComparison.Ordinal)
        && !name.StartsWith('/')
        && !name.Contains('.', StringComparison.Ordinal);

    /// <summary>
    ///     What one file's lines import and what they declare it to be. The walk is the analyser's
    ///     own — a position per line, carried — because a <c>using</c> inside a block comment imports
    ///     nothing and the line above is the only thing that says so.
    /// </summary>
    /// <param name="analyzer">The file's language. A language with no import forms is not walked at all.</param>
    /// <param name="lines">The file's lines, as the ingest split them.</param>
    public ReadImports Read(ILanguageAnalyzer analyzer, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(lines);
        // An extension no profile covers has no declared import forms, whatever the fallback reads
        // for the sake of classifying a reference: an edge read out of it would be a guess with a
        // path on the end of it, which is the one thing this table must not hold.
        if (analyzer.Language is null || !analyzer.HasImports) return Nothing;

        var edges = new List<ImportEdge>();
        string? module = null;
        var position = analyzer.Start;
        int through = Math.Min(lines.Count, MaxScanLines);
        for (int i = 0; i < through; i++)
        {
            string line = lines[i];
            var found = analyzer.ImportsOn(position, line);
            foreach (var name in found.Value.Imports)
                edges.Add(new ImportEdge(i + 1, name.Name, name.Shape, found.Evidence));
            // The first declaration wins. A file that writes two is writing two namespaces into one
            // file, and picking the later one would silently rename the file's own identity half way
            // down it.
            module ??= found.Value.Declares;

            position = analyzer.After(position, line);
        }

        return edges.Count == 0 && module is null ? Nothing : new ReadImports(edges, module);
    }

    private static readonly ReadImports Nothing = new([], null);

    /// <summary>
    ///     Turns the names the walk recorded into the files they name, for the ones where that can be
    ///     said. Runs once the whole project is in the shadow, because a name resolves against every
    ///     other file and half a project answers half the edges wrongly.
    ///     Module names are resolved in the engine — it is a join of a column against a grouped
    ///     column, which is what DuckDB is for — and paths in .NET, because <c>../</c> has to be
    ///     walked off and a candidate tried per extension, which is neither a join nor a filter.
    /// </summary>
    public async Task ResolveAsync(ShadowIndex shadow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shadow);
        await ResolveModulesAsync(shadow.Connection, cancellationToken);
        await ResolvePathsAsync(shadow.Connection, cancellationToken);
    }

    private static async Task ResolveModulesAsync(DuckDBConnection connection, CancellationToken cancellationToken)
    {
        // Two statements and not one: an UPDATE ... FROM drops the rows its join misses, so the miss
        // has to be written first and the hits then written over it. The two that the join does find
        // are one statement, because they read the same grouping and differ only in what it counted.
        await connection.ExecuteAsync(
            $"UPDATE imports SET unresolved = '{Undeclared}' WHERE shape = '{ImportColumns.ModuleShape}'",
            cancellationToken);
        // Case-insensitively, because Delphi and X# are and a namespace that differs only in case is
        // a collision nobody writes on purpose.
        // A name several files declare is a name none of them answers for: "Orders.Domain is in
        // twelve files" is not an answer to "which file does this import", and offering one of the
        // twelve would be an answer that is wrong eleven times out of twelve.
        await connection.ExecuteAsync(
            $"""
             UPDATE imports
             SET target_file = CASE WHEN d.files = 1 THEN d.file_id END,
                 unresolved  = CASE WHEN d.files = 1 THEN NULL ELSE '{Several}' END
             FROM (SELECT lower(module) AS module, min(file_id) AS file_id, count(*) AS files
                   FROM files WHERE module IS NOT NULL AND module <> '' GROUP BY 1) d
             WHERE imports.shape = '{ImportColumns.ModuleShape}' AND lower(imports.name) = d.module
             """, cancellationToken);
    }

    private static async Task ResolvePathsAsync(DuckDBConnection connection, CancellationToken cancellationToken)
    {
        // The edges first, so that a project with none — one written entirely in C#, X# or Delphi,
        // where every name is a module — never builds the map below. On a large project that map is
        // a lowercased copy of every path in it.
        var edges = new List<(long Id, string Name, int Repo, string Directory, string Extension)>();
        using (var command = connection.Query(
                   $"""
                    SELECT i.import_id, i.name, f.repo_id, f.directory, f.extension
                    FROM imports i JOIN files f USING (file_id)
                    WHERE i.shape = '{ImportColumns.PathShape}'
                    """, []))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                edges.Add((reader.Int64("import_id"), reader.Text("name"), reader.Int32("repo_id"),
                    reader.Text("directory"), reader.Text("extension")));
        }

        if (edges.Count == 0) return;

        // Every file in the project by repository and path, which is what a relative name resolves
        // against. Lowercased on both sides — the committed path decides the case and an import line
        // rarely agrees with it — and lowered by DuckDB, so one string per file crosses the boundary
        // instead of the original and a copy.
        var byPath = new Dictionary<(int Repo, string Path), long>();
        // The extension list per extension, because the registry resolves by a walk of the profiles
        // and this asks it once per path edge otherwise.
        var extensions = new Dictionary<string, ImportPathRules>(StringComparer.Ordinal);
        using (var command = connection.Query("SELECT file_id, repo_id, lower(path) AS path FROM files", []))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                byPath[(reader.Int32("repo_id"), reader.Text("path"))] = reader.Int64("file_id");
        }

        var resolved = new List<(long Import, long? Target, string? Unresolved)>();
        {
            foreach (var (id, name, repo, directory, extension) in edges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsExternal(name))
                {
                    resolved.Add((id, null, Outside));
                    continue;
                }

                // The importing file's own language decides how short a path may be written: a
                // TypeScript `./orders` may leave off its extension and may name a directory, and a
                // CSS `@import "theme"` may do neither.
                if (!extensions.TryGetValue(extension, out var rules))
                    extensions[extension] = rules = Languages.Default.For(extension).ImportPaths;
                long? target = null;
                foreach (string candidate in Candidates(directory, name, rules))
                    if (byPath.TryGetValue((repo, candidate), out long found))
                    {
                        target = found;
                        break;
                    }

                resolved.Add((id, target,
                    target is not null ? null : IsPackage(name) ? Package : NoSuchFile));
            }
        }

        await WriteAsync(connection, resolved, cancellationToken);
    }

    /// <summary>
    ///     The paths this name could mean, best first. The name as written always; the shorter forms
    ///     only where the language says it has them, which is what keeps Node's rule off the
    ///     languages that do not share it — a <c>&lt;script src="a"&gt;</c> is not a request for
    ///     <c>a.html</c>.
    ///     First match wins rather than "all matches", because the list is ordered by how the
    ///     language would resolve it and a later candidate matching as well is not an ambiguity: it
    ///     is a file the compiler would not have picked either.
    /// </summary>
    private static IEnumerable<string> Candidates(string directory, string name, ImportPathRules rules)
    {
        string cleaned = name.Replace('\\', '/').Trim();
        // A query string or a fragment on a stylesheet or a script is a cache buster, not part of
        // the file name.
        int cut = cleaned.IndexOfAny(['?', '#']);
        if (cut >= 0) cleaned = cleaned[..cut];
        // A root-relative name is walked too: `/js/../lib/app.js` names the same file `lib/app.js`
        // does, and looking it up as written would miss a file the index holds.
        string? based = Normalize(cleaned.StartsWith('/') || directory.Length == 0
            ? cleaned
            : directory + "/" + cleaned);
        if (based is null || based.Length == 0) yield break;

        string lowered = based.ToLowerInvariant();
        yield return lowered;
        foreach (string extension in rules.Extensions) yield return $"{lowered}.{extension}";
        if (rules.DirectoryIndex is not { } index) yield break;
        foreach (string extension in rules.Extensions) yield return $"{lowered}/{index}.{extension}";
    }

    /// <summary>
    ///     A path with its <c>.</c> and <c>..</c> segments walked off, or null where it climbs past
    ///     the root of the repository — which names no file in this project and must not be answered
    ///     as though it did.
    /// </summary>
    private static string? Normalize(string path)
    {
        var segments = new List<string>();
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment != "..")
            {
                segments.Add(segment);
                continue;
            }

            if (segments.Count == 0) return null;
            segments.RemoveAt(segments.Count - 1);
        }

        return string.Join('/', segments);
    }

    /// <summary>
    ///     Writes the resolved paths back through a temporary table filled by an appender, which is
    ///     how the rest of this build writes rows. The batched list of literals it replaced joined a
    ///     thousand inline values against the whole of <c>imports</c> per batch — a scan per thousand
    ///     rows — and hand-spelled its own nulls and quotes, so the safety of the SQL rested on a
    ///     comment about where the values came from rather than on the mechanism.
    /// </summary>
    private static async Task WriteAsync(DuckDBConnection connection,
        List<(long Import, long? Target, string? Unresolved)> resolved, CancellationToken cancellationToken)
    {
        if (resolved.Count == 0) return;
        await connection.ExecuteAsync(
            "CREATE OR REPLACE TEMP TABLE resolved_imports (import_id BIGINT, target BIGINT, why VARCHAR)",
            cancellationToken);
        using (var rows = connection.CreateAppender("temp", "main", "resolved_imports"))
        {
            foreach (var (import, target, why) in resolved)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = rows.CreateRow().AppendValue(import);
                row = target is { } file ? row.AppendValue(file) : row.AppendNullValue();
                (why is null ? row.AppendNullValue() : row.AppendValue(why)).EndRow();
            }
        }

        await connection.ExecuteAsync("""
                                      UPDATE imports SET target_file = r.target, unresolved = r.why
                                      FROM resolved_imports r WHERE imports.import_id = r.import_id
                                      """, cancellationToken);
        await connection.ExecuteAsync("DROP TABLE resolved_imports", cancellationToken);
    }
}
