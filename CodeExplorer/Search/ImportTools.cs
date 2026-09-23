using System.ComponentModel;
using System.Globalization;
using System.Text;
using CodeExplorer.Infrastructure;
using CodeExplorer.Language;
using ModelContextProtocol.Server;

namespace CodeExplorer.Search;

/// <summary>
///     The MCP tool over the import graph (ADR-0005, <c>Search/</c>): what a file imports.
///     It shipped beside <c>who_imports</c>, the reverse lookup, which is gone (#160). The reverse
///     direction was the half that could not be got any other way, and it was also the half that
///     almost never resolved: three evaluation runs across Radix and Edilverso have it answering
///     empty or ambiguous every time it was asked, with every agent falling back to
///     <c>list_declarations</c> plus <c>find_references</c>. A tool whose answer an agent has to
///     learn to distrust costs its description in every context window and returns nothing for it.
/// </summary>
[McpServerToolType]
internal sealed class ImportTools(IHttpContextAccessor httpContextAccessor, ImportGraph graph)
{
    /// <summary>The project this call is bound to, read the way every tool class here reads it.</summary>
    private Project Bound => BoundProject.Get(httpContextAccessor);

    /// <summary>The sentence every reply here ends with, whatever the listing above it came to.</summary>
    private const string _caveat =
        "and a name is resolved only where it names exactly one file in this project. Strong evidence, not proof.";

    /// <summary>
    ///     What a reply with no edges of its own claims: a file that wrote no import line has no
    ///     evidence of its own to report, and the caveat still has to say how it was read.
    /// </summary>
    private static readonly Evidence[] _textual = [Evidence.Text];

    /// <summary>
    ///     The pivot this tool makes when the import graph has nothing to say. An empty answer here is
    ///     about import lines and never about dependency, so it must not be left as the last word: a
    ///     project-local dependency expressed by global visibility, inheritance, reflection, dynamic
    ///     construction or a project-level reference writes no import line to find, and the answer that
    ///     does not depend on one is a reference lookup on the names the file declares.
    ///     An instruction and not a description (#133). It used to say that the two tools exist, which
    ///     reads as background beside the listing above it; the parallel guidance in the tool
    ///     description was already imperative, and one pivot stated two ways is how an agent decides
    ///     the weaker one is optional.
    ///     It is now the only answer to "what depends on this file" this server offers, since the
    ///     reverse lookup that used to sit beside it is gone (#160) — so it says the whole route and
    ///     not just the next tool.
    /// </summary>
    private const string _wayIn =
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

                 - Use it to see a file's dependencies without reading it. There is no reverse tool: for what depends on THIS file, run list_declarations on it and find_references on a name it declares, which answers whether or not the language writes import lines at all.
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
                "\nNo import line was read in it. In a language that has them, that means the file imports nothing — not that nothing was looked for.\n"), _textual);

        if (result.Capped)
            text.Append(CultureInfo.InvariantCulture,
                $"NOTE: the first {ImportGraph.MaxEdges} import lines are listed and the file has more. Read it directly for the rest.\n");

        // Names to resolve and none of them resolved. The listing below is accurate and reads as
        // "this file depends on nothing in this project", which is a finding an agent acts on and one
        // this tool cannot make: a dependency with no import line to write is invisible here.
        if (resolved.Count == 0)
            text.Append(CultureInfo.InvariantCulture,
                $"NOTE: none of the {result.Imports.Count} {ToolReply.Plural(result.Imports.Count, "name")} resolved to a file in this project. That is not evidence the file has no project-local dependencies — where a language or dialect makes them visible without an import line, there is nothing here to resolve. {_wayIn}\n");

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
    ///     Ends the reply with the caveat, which is the claim the listing above it rests on rather
    ///     than a line of the listing: how the edges were read, and what a resolved name is worth.
    /// </summary>
    private static string Finish(StringBuilder text, IEnumerable<Evidence> evidence) =>
        text.Append(CultureInfo.InvariantCulture, $"\n{How(evidence)} {_caveat}\n").ToString();
}
