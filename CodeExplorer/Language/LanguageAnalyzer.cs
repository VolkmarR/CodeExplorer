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
    Literal
}

/// <summary>
///     Which side of the declaration/implementation split a declaration line sits on, for the
///     languages that have one: a Delphi unit's <c>interface</c> section against its
///     <c>implementation</c>, a PL/SQL package spec against its body.
///     Languages without the split answer <see cref="Declaration" /> for everything, which is what
///     they mean.
/// </summary>
public enum DeclarationRole
{
    Declaration,
    Implementation
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
///     What one line declares. Either name may be null — a member declaration names no type and a
///     type declaration no member — and a line that reads as both fills both.
/// </summary>
public sealed record Declared(string? Type, string? Member, DeclarationRole Role);

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

    /// <summary>How every answer from this analyser was reached.</summary>
    Evidence Evidence { get; }

    /// <summary>
    ///     Whether this language splits a declaration from its implementation — a Delphi unit's
    ///     <c>interface</c> and <c>implementation</c> sections, a PL/SQL package spec and body. Where
    ///     it does, <c>find_definition</c> has two answers to tell apart rather than one to find.
    /// </summary>
    bool SeparatesDeclarationFromImplementation { get; }

    /// <summary>
    ///     The RE2 pattern that narrows a file to the lines <see cref="Declares" /> could place, for
    ///     the engine to run (CODING_STANDARDS: the candidate set is chosen by DuckDB, and .NET says
    ///     what each candidate is). It is the engine-side half of the declaration question and is
    ///     published beside it so the two cannot drift: a line the engine skipped is a declaration
    ///     this would never have found anyway. A parser-backed analyser returns a wider net here, or
    ///     one matching every line.
    /// </summary>
    string DeclarationCandidatePattern { get; }

    /// <summary>What the position at <paramref name="index" /> on this line sits in.</summary>
    Answer<Lexical> StateAt(string line, int index);

    /// <summary>What this line declares, or null when it turns out to declare nothing.</summary>
    Answer<Declared?> Declares(string line);

    /// <summary>What this line imports, as written, or null when it is not an import line.</summary>
    Answer<string?> ImportOn(string line);

    /// <summary>Whether a file at this qualified path is generated rather than hand-written.</summary>
    Answer<bool> IsGenerated(string qualifiedPath);

    /// <summary>
    ///     What the appearance of an identifier at <paramref name="index" /> looks like — the question
    ///     <c>find_references</c> asks, and the one that cannot be answered from
    ///     <see cref="StateAt" /> alone, because <c>:=</c> is an assignment in X# and a syntax error in
    ///     C#. It is here rather than at the caller for the same reason the other four are: the
    ///     operators that decide it are the profile's, and a caller reading them would weld the
    ///     classification to a regex for good.
    /// </summary>
    /// <param name="line">The whole line, which is what a declaration head is judged from.</param>
    /// <param name="index">Where the identifier starts.</param>
    /// <param name="length">How long it is.</param>
    Answer<ReferenceKind> Occurrence(string line, int index, int length);
}
