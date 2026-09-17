namespace CodeExplorer;

/// <summary>
///     How an answer about a file's language was reached. Every answer carries one, because a
///     heuristic answer and a parsed one are claims of different strength and an agent weighing them
///     has to be able to tell them apart — the same distinction CONTEXT.md draws between a reference
///     and proof.
/// </summary>
public enum Evidence
{
    /// <summary>Read from the text by a <see cref="LanguageProfile" />: strong evidence, never proof.</summary>
    Text,

    /// <summary>Produced by a real parser for the language, which is proof of what the text says.</summary>
    Parsed
}

/// <summary>What a position on a line sits in.</summary>
public enum Lexical
{
    /// <summary>Live code.</summary>
    Code,

    /// <summary>Inside a comment, so anything named there is a mention and not a use.</summary>
    Comment,

    /// <summary>Inside a string literal, which is a mention too — though the one that often matters.</summary>
    Literal,

    /// <summary>
    ///     Not established. The lines before this one were not all read — a file scanned only to a
    ///     bound, a line handed over on its own — so whether a comment or a literal is open cannot be
    ///     said. It is its own answer and not <see cref="Code" />, because "this is live code" is the
    ///     claim an agent acts on and a guess wearing it is the failure this module is written around.
    /// </summary>
    Unknown
}

/// <summary>
///     Where a file stands at the start of a line: what the lines above it left open. Opaque to
///     callers — an analyser makes one with <see cref="ILanguageAnalyzer.Start" /> and moves it on
///     with <see cref="ILanguageAnalyzer.After" />, and nothing outside the analyser that produced it
///     can read what is in it.
///     Opaque because the state is the implementation's: a text profile carries the comments and
///     literals still open, and a parser-backed analyser would carry a node. Both answer the same
///     questions about the line in front of them, which is what lets one be swapped for the other
///     (ADR-0008).
///     It is also where a second kind of file-level position goes. Telling a Delphi <c>interface</c>
///     section from its <c>implementation</c>, or a PL/SQL package spec from its body, is one more
///     field on the analyser's own record: the signatures that carry it do not change when it is
///     added.
/// </summary>
public abstract record FilePosition
{
    protected FilePosition() { }

    /// <summary>
    ///     Nothing is known about what the lines above left open. Every position on a line read from
    ///     here is <see cref="Lexical.Unknown" />, so the references on it are kept and reported as
    ///     unplaced rather than guessed either way.
    /// </summary>
    public static FilePosition Unknown { get; } = new Unplaced();

    private sealed record Unplaced : FilePosition;
}

/// <summary>
///     Which side of the declaration/implementation split a declaration line sits on, for the
///     languages that have one: a Delphi unit's <c>interface</c> section against its
///     <c>implementation</c>, a PL/SQL package spec against its body.
/// </summary>
public enum DeclarationRole
{
    Declaration,
    Implementation
}

/// <summary>
///     Which lines an analyser needs to see to answer <see cref="ILanguageAnalyzer.Declares" />, for
///     the engine to narrow a file down with (CODING_STANDARDS: the candidate set is chosen by
///     DuckDB, and .NET says what each candidate is).
///     It is a question about which lines, not a regex, because a parser-backed analyser has no regex
///     to give and would have to invent one to answer at all. <see cref="Matching" /> is what a text
///     profile answers, <see cref="All" /> what a parser does, and <see cref="None" /> what a
///     language with no declarations this can read — HTML, CSS — means, so the engine is not asked
///     for lines that will all be thrown away.
/// </summary>
public abstract record CandidateLines
{
    private CandidateLines() { }

    /// <summary>Every line of the file.</summary>
    public static CandidateLines All { get; } = new EveryLine();

    /// <summary>No line, because this language declares nothing this can read.</summary>
    public static CandidateLines None { get; } = new NoLine();

    /// <summary>The lines an RE2 pattern matches.</summary>
    public static CandidateLines Matching(string pattern) => new Re2Pattern(pattern);

    public sealed record EveryLine : CandidateLines;

    public sealed record NoLine : CandidateLines;

    public sealed record Re2Pattern(string Pattern) : CandidateLines;
}

/// <summary>
///     What one appearance of an identifier looks like. The names are the ones CONTEXT.md uses for a
///     reference: a place where an identifier appears in code in a way that looks like real use, as
///     against a mention in a comment, a string or an import. <see cref="Other" /> is deliberate —
///     one this cannot place is kept and labelled unplaced rather than dropped, because a dropped
///     reference is a wrong answer shaped like a right one.
/// </summary>
public enum ReferenceKind
{
    /// <summary>The line appears to declare the identifier.</summary>
    Definition,

    /// <summary>
    ///     The identifier is assigned to: <c>x.Status = …</c>, <c>Status += …</c>, the X# and Delphi
    ///     <c>Status := …</c>, or the receiver-less object-initializer form <c>Status = dao.Status</c>.
    ///     This is the one that answers "what changes this?", so it is kept apart from a plain read.
    /// </summary>
    Write,

    /// <summary>The identifier is invoked: <c>Symbol(</c> or <c>x.Symbol(</c>.</summary>
    Call,

    /// <summary>A type is constructed: <c>new Symbol(...)</c>.</summary>
    Instantiation,

    /// <summary>Used as a type: <c>: Symbol</c>, <c>Symbol x</c>, <c>List&lt;Symbol&gt;</c>.</summary>
    TypeUse,

    /// <summary>Read as a member: <c>x.Symbol</c> or X#'s <c>x:Symbol</c>, with no call parentheses and no assignment.</summary>
    MemberAccess,

    /// <summary>A using, import, include or namespace line.</summary>
    Import,

    /// <summary>In a comment, so it is a mention and not a use.</summary>
    Comment,

    /// <summary>Inside a string literal, which is a mention too — though the one that often matters.</summary>
    StringLiteral,

    /// <summary>A real reference in code that none of the shapes above fits.</summary>
    Other
}

/// <summary>
///     An answer and how it was reached. A bare value would let a parsed answer and a guessed one
///     read alike at every call site that passes one on.
/// </summary>
public readonly record struct Answer<T>(T Value, Evidence Evidence);

/// <summary>
///     What kind of name an import names, which is the whole of what decides how it can be resolved
///     to a file. A namespace does not map to a path the same way in any two languages, and a
///     relative path does not map to a namespace at all; telling them apart is what lets one
///     resolver serve every language without inventing a rule for any of them.
/// </summary>
public enum ImportShape
{
    /// <summary>
    ///     A namespace, unit or package name: C#'s <c>using System.Text</c>, Delphi's
    ///     <c>uses Customers</c>. It resolves against what a file declares itself to be, never
    ///     against where the file sits.
    /// </summary>
    Module,

    /// <summary>
    ///     A file, named the way the importing file reaches it: <c>./orders</c>, <c>../lib/a.css</c>,
    ///     <c>Common.vh</c>. It resolves against the importing file's own directory.
    /// </summary>
    Path
}

/// <summary>
///     How short a path this language lets an import write. TypeScript's <c>./orders</c> may name
///     <c>orders.ts</c> or <c>orders/index.ts</c>; CSS's <c>@import "theme"</c> names
///     <c>theme</c> and nothing else.
///     It is here and not in the resolver because it is a language fact (ADR-0008): a resolver that
///     tried every extension for every language answered a <c>&lt;script src="a"&gt;</c> with
///     <c>a.html</c>, which is Node's rule applied to markup that does not have it.
/// </summary>
/// <param name="Extensions">
///     What may be appended to a name that has none, in the order the language would try them.
///     Empty where a path always names its own extension, which is every language here but two.
/// </param>
/// <param name="DirectoryIndex">
///     The file a directory stands for — <c>index</c> — or null where naming a directory names
///     nothing.
/// </param>
public sealed record ImportPathRules(IReadOnlyList<string> Extensions, string? DirectoryIndex)
{
    /// <summary>A path names the file it spells, which is what most languages mean.</summary>
    public static ImportPathRules AsWritten { get; } = new([], null);
}

/// <summary>
///     One name a line imports, as the line wrote it. The name is kept verbatim because an edge that
///     cannot be resolved is still an answer — the raw name tells a reader what the file asked for,
///     where a dropped edge tells them the file asked for nothing.
/// </summary>
public sealed record ImportedName(string Name, ImportShape Shape);

/// <summary>
///     What one line says about the files around it: the names it imports, and the name it declares
///     this file to be. Both, because a line is rarely both and the two travel together — a C# file's
///     <c>namespace</c> is what another file's <c>using</c> resolves against, and reading the one
///     without the other leaves the reverse direction unanswerable.
/// </summary>
/// <param name="Imports">Names this line imports, in the order written. Empty where it imports none.</param>
/// <param name="Declares">
///     What this line declares the file to be — C#'s <c>namespace</c>, Delphi's <c>unit</c> — or null
///     where it declares nothing.
/// </param>
public sealed record ImportsOnLine(IReadOnlyList<ImportedName> Imports, string? Declares)
{
    /// <summary>The line said nothing about imports, which is what almost every line says.</summary>
    public static ImportsOnLine Nothing { get; } = new([], null);
}

/// <summary>
///     What one line declares. Either name may be null — a member declaration names no type and a
///     type declaration no member — and a line that reads as both fills both.
///     <see cref="Role" /> is null where the analyser cannot tell which side of the split the line
///     sits on. Null and not <see cref="DeclarationRole.Declaration" />: a Delphi
///     <c>implementation</c> line labelled a declaration is a guess reported as a fact, which is the
///     one failure this module is written to avoid.
/// </summary>
public sealed record Declared(string? Type, string? Member, DeclarationRole? Role);

/// <summary>
///     Everything this server knows about one language, as questions rather than as tables. This is
///     the seam ADR-0008 draws.
///     Callers never read a profile's punctuation. A caller that asked "what are this language's
///     comment prefixes" and scanned for them itself would have hardcoded that a regex is how the
///     answer is found, and no parser could then be substituted underneath it. Every question here is
///     one a <see cref="LanguageProfile" /> can answer from the text today and a tree-sitter or
///     Roslyn analyser could answer better tomorrow, and the callers see no difference.
/// </summary>
public interface ILanguageAnalyzer
{
    /// <summary>
    ///     What this analyses, as <c>CONTEXT.md</c>'s Language, or null for the fallback that covers
    ///     every extension no profile claims. Null is the honest answer there: an extension nobody
    ///     declared stands for itself rather than being guessed at.
    /// </summary>
    string? Language { get; }

    /// <summary>Extensions this claims, lowercase and without the dot, as <c>files.extension</c> stores them.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>
    ///     Whether this language splits a declaration from its implementation — a Delphi unit's
    ///     <c>interface</c> and <c>implementation</c> sections, a PL/SQL package spec and body. Where
    ///     it does, <c>find_definition</c> has two answers to tell apart rather than one to find.
    /// </summary>
    bool SeparatesDeclarationFromImplementation { get; }

    /// <summary>
    ///     Which lines the engine should hand to <see cref="Declares" />. The engine-side half of the
    ///     declaration question, published beside it so the two cannot drift: a line the engine
    ///     skipped is a declaration this would never have found anyway.
    /// </summary>
    CandidateLines DeclarationCandidates { get; }

    /// <summary>
    ///     Where a file begins: nothing open, nothing entered. The first line of a file is read from
    ///     here, and every line after it from what <see cref="After" /> returned for the one above.
    /// </summary>
    FilePosition Start { get; }

    /// <summary>
    ///     Where the file stands after this line, given where it stood before it. A block comment or a
    ///     literal opened here and not closed is what the next line inherits.
    ///     This is the whole of the file-level scan, and it is a walk of the file's lines in order —
    ///     which is what makes the cost one walk per file rather than one per match on it. A caller
    ///     that cannot read the lines above a match passes <see cref="FilePosition.Unknown" /> and is
    ///     told so in the answer, rather than being handed a guess.
    /// </summary>
    FilePosition After(FilePosition position, string line);

    /// <summary>
    ///     What the position at <paramref name="index" /> on this line sits in, given where the file
    ///     stood at the start of the line.
    /// </summary>
    Answer<Lexical> StateAt(FilePosition position, string line, int index);

    /// <summary>
    ///     What this line declares, or null when it turns out to declare nothing. The position says
    ///     which side of the declaration/implementation split the line sits on, for the languages that
    ///     have one; a caller that has not walked the file to here passes
    ///     <see cref="FilePosition.Unknown" /> and is answered with a null role rather than a guess.
    /// </summary>
    Answer<Declared?> Declares(FilePosition position, string line);

    /// <summary>
    ///     Whether this language has an import concept at all. False for the SQL family, which names
    ///     no file it depends on — and which must therefore be told apart from a file that imports
    ///     nothing, because an empty answer reads as "this depends on nothing" and would be the wrong
    ///     one for every stored procedure in the project.
    /// </summary>
    bool HasImports { get; }

    /// <summary>
    ///     How short a path an import in this language may be written, for the resolver that has to
    ///     turn one into a file. <see cref="ImportPathRules.AsWritten" /> where a path names exactly
    ///     the file it spells.
    /// </summary>
    ImportPathRules ImportPaths { get; }

    /// <summary>
    ///     What this line says about the files around it: what it imports, and what it declares this
    ///     file to be. <see cref="ImportsOnLine.Nothing" /> for the lines that say neither, which is
    ///     nearly all of them.
    ///     The position is here for the same two reasons <see cref="Declares" /> takes one, and a
    ///     third: a commented-out <c>using</c> imports nothing, and Delphi's <c>uses</c> clause runs
    ///     as many lines as it likes until its <c>;</c> — so the name on the second line of one is an
    ///     import only because of what the line above it left open. A caller that has not walked the
    ///     file to here passes <see cref="FilePosition.Unknown" /> and is answered from this line
    ///     alone.
    /// </summary>
    Answer<ImportsOnLine> ImportsOn(FilePosition position, string line);

    /// <summary>Whether a file at this qualified path is generated rather than hand-written.</summary>
    Answer<bool> IsGenerated(string qualifiedPath);

    /// <summary>
    ///     What every appearance of <paramref name="symbol" /> on this line looks like, in the order
    ///     they occur — the question <c>find_references</c> asks, and the one that cannot be answered
    ///     from <see cref="StateAt" /> alone, because <c>:=</c> is an assignment in X# and a syntax
    ///     error in C#. It is here rather than at the caller for the same reason the other four are:
    ///     the operators that decide it are the profile's, and a caller reading them would weld the
    ///     classification to a regex for good.
    ///     The whole line at once rather than one position at a time, because most of what decides an
    ///     appearance is a fact about the line — where its comments and literals are, what it
    ///     declares — and asking per position makes an implementation either recompute that per
    ///     appearance or keep state it cannot keep, since one analyser answers for every search at
    ///     once. Asked per position, a line naming a common identifier a thousand times cost a
    ///     thousand walks of it; asked this way it costs one.
    /// </summary>
    IReadOnlyList<Answer<ReferenceKind>> Occurrences(FilePosition position, string line, string symbol);
}
