using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;

namespace CodeExplorer;

/// <summary>
///     The two MCP tools over the import graph (ADR-0005, <c>Search/</c>): what a file imports, and
///     what imports it. They ship together because <c>imports</c> alone is a tool an agent can get by
///     reading the top of the file, and the reverse lookup is the part that cannot be got any other
///     way.
/// </summary>
[McpServerToolType]
internal sealed class ImportTools(IHttpContextAccessor httpContextAccessor, ImportGraph graph)
{
    /// <summary>The project this call is bound to, read the way every tool class here reads it.</summary>
    private Project Bound => BoundProject.Get(httpContextAccessor);

    /// <summary>
    ///     The sentence every reply here ends with. Said once because both tools make the same claim
    ///     and one of them wording it more confidently than the other would be the one an agent
    ///     believes.
    /// </summary>
    private const string Caveat =
        "and a name is resolved only where it names exactly one file in this project. Strong evidence, not proof.";

    /// <summary>
    ///     What a reply with no edges of its own claims. The reverse lookup answers with other files'
    ///     edges and the empty replies with none, and neither has an evidence of its own to report.
    /// </summary>
    private static readonly Evidence[] Textual = [Evidence.Text];

    /// <summary>
    ///     The pivot both tools make when the import graph has nothing to say. An empty answer here is
    ///     about import lines and never about dependency, so it must not be left as the last word: a
    ///     project-local dependency expressed by global visibility, inheritance, reflection, dynamic
    ///     construction or a project-level reference writes no import line to find, and the answer that
    ///     does not depend on one is the reverse lookup on the names the file declares.
    ///     An instruction and not a description (#133). It used to say that the two tools exist, which
    ///     reads as background beside the listing above it; the parallel guidance in the tool
    ///     descriptions was already imperative, and one pivot stated two ways is how an agent decides
    ///     the weaker one is optional. One sentence for all three call sites, so the register cannot
    ///     drift branch by branch.
    /// </summary>
    private const string WayIn =
        "Run list_declarations on it, then find_references on one of the names it declares, to find the files that use it.";

    /// <summary>
    ///     How the edges were read, which decides the lead clause of the caveat. Derived from the
    ///     answers rather than written as a fact (ADR-0008): the day a parser-backed analyser is
    ///     registered for a language, a reply that still called itself textual would understate what
    ///     it knows, and nothing here would have to change for it to stop.
    /// </summary>
    private static string How(IEnumerable<Evidence> evidence) =>
        evidence.All(e => e == Evidence.Text)
            ? "Import edges are read from the text of the import lines, not from a compiler,"
            : "Import edges are parsed where the language has a parser here and read from the text elsewhere,";

    [McpServerTool(Name = "imports", ReadOnly = true, Idempotent = true, Title = "What a file imports")]
    [Description("""
                 Lists what one file imports — its `using`, `import`, `uses`, `#include`, `<script src>` or `@import` lines — with the qualified path of the file each name turned out to be, where that can be said. Takes a qualified path exactly as grep, glob and read_file print one.

                 - Use it to see a file's dependencies without reading it, and use who_imports for the other direction.
                 - A name that resolves is shown with the file it names. One that does not is shown as written, with the reason: nothing in this project declares it (a framework or a package), several files declare it, or the path names nothing here. An unresolved name is still a dependency; it is just one outside what this index can see.
                 - IMPORTANT: this reads import lines as text, in the forms the file's language profile knows. A language with no import concept — SQL, PL/SQL — says so, and an extension no profile covers says that instead, so an empty list never has to be read as "this file depends on nothing".
                 - A C# namespace or a Delphi unit resolves against what another file declares itself to be, so a namespace spread over several files resolves to none of them and says so. A relative path resolves against the importing file's own directory.
                 - IMPORTANT: an answer where every name is a framework or a package is NOT evidence that the file has no project-local dependencies. Where a language or dialect expresses those by global visibility, inheritance, reflection, dynamic construction or a project-level reference, there is no import line to find, and this tool is the wrong question. The one that works is list_declarations on the file, then find_references on a name it declares.
                 """)]
    public async Task<string> Imports(
        [Description("Qualified path of the file, e.g. \"main/src/Api/Orders.cs\".")]
        string path,
        CancellationToken cancellationToken = default)
    {
        return ToolReply.Render<ImportsResult>(await graph.ImportsAsync(Bound.Slug, path, cancellationToken),
            Format, "Read the file's import lines directly for the rest.");
    }

    [McpServerTool(Name = "who_imports", ReadOnly = true, Idempotent = true,
        Title = "What depends on a file")]
    [Description("""
                 Lists the files in this project that import a given file, with the name each of them wrote and the line it is on. This is the question a codebase cannot answer by being read: it is the reverse of every import line in the project, resolved at index time.

                 - Use it before changing or deleting a file, to see what would notice; use imports for what the file itself depends on.
                 - The answer is what RESOLVED to this file. A name that named several files, or none, was left unresolved and is not counted here — so the reply says how many such edges there are rather than letting a short list read as complete.
                 - A file whose declared namespace or unit is shared with other files cannot be the sole target of an import of that name, and the reply says so instead of answering with an empty list. That branch can arrive alongside resolved importers, so it is not covered by the empty-answer advice below: run list_declarations on the file and find_references on a name it declares, which is the lookup that does not depend on a name resolving.
                 - IMPORTANT: this is read from import lines, not from a compiler. A dependency expressed some other way — reflection, a generated file, a name in configuration — is not an import edge and is not here.
                 - IMPORTANT: an empty answer means no import edge resolved to this file, NOT that nothing depends on it. Where a language or dialect makes project-local names visible without an import line, a file every other file calls answers empty here. Before concluding a file is unused, run list_declarations on it and find_references on a name it declares.
                 """)]
    public async Task<string> WhoImports(
        [Description("Qualified path of the file, e.g. \"main/src/Api/Orders.cs\".")]
        string path,
        CancellationToken cancellationToken = default)
    {
        return ToolReply.Render<DependentsResult>(await graph.DependentsAsync(Bound.Slug, path, cancellationToken),
            Format, "Narrow by asking about a more specific file.");
    }

    private static string Format(ImportsResult result)
    {
        if (!result.Profiled)
            return string.Create(CultureInfo.InvariantCulture,
                $"{result.QualifiedPath} is a {result.LanguageName} file, and no language profile covers that extension. Its import lines were never read, which is a different thing from it importing nothing — read the top of the file, or grep it.\n");

        if (!result.HasImports)
            return string.Create(CultureInfo.InvariantCulture,
                $"{result.QualifiedPath} is {result.LanguageName}, which has no import concept: a file in it names no other file to depend on. Nothing was extracted, and nothing was missed. Use grep or find_references to find what it touches.\n");

        // Partitioned once. The counts, the two sections and the headline all ask the same question
        // of the same list, and asking it four times is four walks for one answer.
        var resolved = new List<ImportedFrom>();
        var rest = new List<ImportedFrom>();
        foreach (var edge in result.Imports) (edge.TargetPath is null ? rest : resolved).Add(edge);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{result.QualifiedPath} ({result.LanguageName}) imports {result.Imports.Count} {ToolReply.Plural(result.Imports.Count, "name")}");
        text.Append(CultureInfo.InvariantCulture,
            $", {resolved.Count} of them resolved to a file in this project.\n");
        if (result.Module is { } module)
            text.Append(CultureInfo.InvariantCulture,
                $"It declares itself as `{module}`, which is what another file's import of that name resolves against.\n");

        if (result.Imports.Count == 0)
            return Finish(text.Append(
                "\nNo import line was read in it. In a language that has them, that means the file imports nothing — not that nothing was looked for.\n"), Textual);

        if (result.Capped)
            text.Append(CultureInfo.InvariantCulture,
                $"NOTE: the first {ImportGraph.MaxEdges} import lines are listed and the file has more. Read it directly for the rest.\n");

        // Names to resolve and none of them resolved. The listing below is accurate and reads as
        // "this file depends on nothing in this project", which is a finding an agent acts on and one
        // this tool cannot make: a dependency with no import line to write is invisible here.
        if (resolved.Count == 0)
            text.Append(CultureInfo.InvariantCulture,
                $"NOTE: none of the {result.Imports.Count} {ToolReply.Plural(result.Imports.Count, "name")} resolved to a file in this project. That is not evidence the file has no project-local dependencies — where a language or dialect makes them visible without an import line, there is nothing here to resolve. {WayIn}\n");

        if (resolved.Count > 0)
        {
            text.Append("\nRESOLVED  (").Append(resolved.Count).Append(")\n");
            foreach (var edge in resolved)
                text.Append(CultureInfo.InvariantCulture,
                    $"  {edge.LineNumber,6}: {edge.Name}  ->  {edge.TargetPath}\n");
        }

        if (rest.Count > 0)
        {
            text.Append("\nUNRESOLVED  (").Append(rest.Count).Append(")\n");
            foreach (var edge in rest)
                text.Append(CultureInfo.InvariantCulture,
                    $"  {edge.LineNumber,6}: {edge.Name}  -  {edge.Unresolved ?? "not resolved"}\n");
        }

        return Finish(text, result.Imports.Select(edge => edge.Evidence));
    }

    /// <summary>
    ///     Ends a reply with the caveat. Written once because it is the one line the two replies must
    ///     agree on, and two hand-written copies would agree only until one of them was edited.
    /// </summary>
    private static string Finish(StringBuilder text, IEnumerable<Evidence> evidence) =>
        text.Append(CultureInfo.InvariantCulture, $"\n{How(evidence)} {Caveat}\n").ToString();

    private static string Format(DependentsResult result)
    {
        var text = new StringBuilder();
        // "No file imports this" and "nothing depends on this" are opposite claims, and only the first
        // one is this tool's to make. The second is what an empty list reads as, so it is denied here
        // rather than left to the caveat at the foot of the reply.
        text.Append(result.Dependents.Count == 0
            ? string.Create(CultureInfo.InvariantCulture,
                $"No import edge in this project resolved to {result.QualifiedPath}. That is not the same as nothing depending on it: a project-local dependency expressed without an import line — global visibility, inheritance, reflection, a project-level reference — leaves no edge to find. {WayIn}\n")
            : string.Create(CultureInfo.InvariantCulture,
                $"{result.Dependents.Count} {ToolReply.Plural(result.Dependents.Count, "file")} {ToolReply.Plural(result.Dependents.Count, "imports", "import")} {result.QualifiedPath}.\n"));

        // The one case where an empty answer is known to be wrong before it is printed: a namespace
        // several files declare resolves to none of them, so no edge could ever have pointed here.
        if (result.ShareTheModule > 0)
            text.Append(CultureInfo.InvariantCulture,
                $"NOTE: this file declares `{result.Module}`, and {result.ShareTheModule} other {ToolReply.Plural(result.ShareTheModule, "file declares", "files declare")} it too. An import of that name therefore names no single file and was left unresolved, so this list cannot include it. {WayIn}\n");
        else if (result.Unplaced > 0)
            text.Append(CultureInfo.InvariantCulture,
                $"NOTE: {result.Unplaced} unresolved import {ToolReply.Plural(result.Unplaced, "edge")} in this project {ToolReply.Plural(result.Unplaced, "spells", "spell")} this file's name and could not be pointed at any file. A dependency on this one may be among them; grep for the name to check.\n");

        // A list that stopped at the ceiling reads as the whole answer unless it says otherwise,
        // and "42 files depend on this" is a number an agent acts on.
        if (result.Capped)
            text.Append(CultureInfo.InvariantCulture,
                $"NOTE: the first {ImportGraph.MaxEdges} are listed and there are more. A file this widely depended on is a hub; ask about something more specific.\n");

        if (result.Dependents.Count > 0)
        {
            text.Append('\n');
            foreach (var dependent in result.Dependents)
                text.Append(CultureInfo.InvariantCulture,
                    $"  {dependent.QualifiedPath}:{dependent.LineNumber}  -  {dependent.Name}\n");
        }

        return Finish(text, Textual);
    }
}
