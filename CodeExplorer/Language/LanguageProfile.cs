namespace CodeExplorer;

/// <summary>How a string literal protects its own closing delimiter.</summary>
public enum StringEscape
{
    /// <summary>Nothing escapes; the first closing delimiter ends the literal. X# and HTML.</summary>
    None,

    /// <summary>A backslash escapes the next character. The C family and JavaScript.</summary>
    Backslash,

    /// <summary>The delimiter doubled stands for itself. Delphi, SQL and PL/SQL.</summary>
    Doubled
}

/// <summary>
///     A hole of live code inside a string literal: <c>{…}</c> in a C# interpolated string,
///     <c>${…}</c> in a JavaScript template literal.
///     A literal that does not name its holes reports every call written inside one as a string
///     mention, which loses real calls — the reason the template literal was left out of the profiles
///     until the scan could carry the state a hole needs.
/// </summary>
/// <param name="Open">What opens one. Doubled it stands for itself, which is how C# writes a literal brace.</param>
/// <param name="Close">What closes one.</param>
/// <param name="Nest">
///     What a brace nested inside the hole looks like, so that the <c>}</c> of a collection expression
///     in it does not end it. Spelled out rather than taken from the last character of
///     <paramref name="Open" />: that is true of both forms here and is a guess about the next one.
/// </param>
public sealed record Hole(string Open, string Close, string Nest);

/// <summary>One kind of string literal: what opens it, what closes it, and how it escapes.</summary>
public sealed record StringDelimiter(string Open, string Close, StringEscape Escape)
{
    /// <summary>
    ///     Whether an unclosed one carries on onto the next line. C#'s verbatim and raw literals and a
    ///     JavaScript template literal do; an ordinary quoted literal does not, and treating one as
    ///     though it did would let a single stray quote turn the rest of a file into a string.
    ///     A form that may span is only ever read as spanning where the profile says so: an unknown
    ///     multi-line form costs unplaced references on the lines it covers, an invented one costs
    ///     every line below it.
    /// </summary>
    public bool SpansLines { get; init; }

    /// <summary>The holes of live code this literal may carry, or null where it has none.</summary>
    public Hole? Hole { get; init; }
}

/// <summary>
///     The text facts about one language, in the shape <see cref="TextAnalyzer" /> reads them. This is
///     the extension point for <em>a new language</em>: a profile and one line in the registration
///     table, touching no caller. (The other extension point — a <em>better implementation</em> for a
///     language already covered — is a different <see cref="ILanguageAnalyzer" />, not a profile.)
///     Nothing outside <see cref="TextAnalyzer" /> reads these lists. They are data behind the seam,
///     not a table callers consult.
///     The rule that keeps them from sprawling is the one the declaration modifiers already follow: a
///     form a profile does not know costs an unplaced reference, a form it invents costs a wrong
///     answer reported as a right one. Leave a doubtful form out.
/// </summary>
/// <param name="Name">The language as <c>CONTEXT.md</c> means it, or null for the fallback profile.</param>
/// <param name="Extensions">Lowercase, without the dot, as <c>files.extension</c> stores them.</param>
public sealed record LanguageProfile(string? Name, IReadOnlyList<string> Extensions)
{
    /// <summary>
    ///     Comment openers recognised anywhere on a line, so that code before one is still code and
    ///     everything after it is prose. <c>//</c> in most of the C family, <c>--</c> in SQL, <c>&amp;&amp;</c>
    ///     in X#. Empty where the language has no line comment at all, which is the point for CSS.
    /// </summary>
    public IReadOnlyList<string> LineComments { get; init; } = [];

    /// <summary>
    ///     Openers recognised only at the start of a trimmed line. <c>*</c> is here and not above
    ///     because it is the continuation of a doc comment block and a multiplication anywhere else.
    /// </summary>
    public IReadOnlyList<string> LineStartComments { get; init; } = [];

    /// <summary>
    ///     Line-start forms that look like a comment opener and are not: <c>#include</c> against a
    ///     leading <c>#</c>, Delphi's <c>{$IFDEF}</c> against its <c>{ }</c> comments. Without these a
    ///     compiler directive reads as prose, which is how <c>#region</c> used to hide a whole line.
    /// </summary>
    public IReadOnlyList<string> DirectivePrefixes { get; init; } = [];

    /// <summary>
    ///     Block comment pairs. An opener anywhere on a line opens one, and it stays open across the
    ///     lines below until its closer: the scan carries that between lines, which is what stops the
    ///     second line of a commented-out block from reading as a call.
    /// </summary>
    public IReadOnlyList<StringDelimiter> BlockComments { get; init; } = [];

    /// <summary>What opens a string literal, and how it escapes.</summary>
    public IReadOnlyList<StringDelimiter> Strings { get; init; } = [];

    /// <summary>
    ///     What assigns. <c>=</c> in the C family, <c>:=</c> in X#, Delphi and PL/SQL — where <c>=</c>
    ///     is a comparison, so listing it would report every equality test as a write. The compound
    ///     forms (<c>+=</c> and its family) are added by <see cref="TextAnalyzer" />, which is the same
    ///     list in every language that has any of them.
    /// </summary>
    public IReadOnlyList<string> AssignmentOperators { get; init; } = [];

    /// <summary>
    ///     What reaches a member through a receiver. <c>.</c> nearly everywhere, and <c>:</c> as well
    ///     in X#, where <c>oCustomer:Name</c> is the send and the dot is for .NET types.
    /// </summary>
    public IReadOnlyList<string> MemberAccessOperators { get; init; } = [];

    /// <summary>
    ///     Punctuation which, immediately before an identifier, marks it as a type: C#'s <c>:</c> base
    ///     list, a generic argument after <c>&lt;</c> or <c>,</c>. Kept apart from
    ///     <see cref="MemberAccessOperators" /> because X# spells member access with the character C#
    ///     spells a base type with.
    /// </summary>
    public IReadOnlyList<string> TypePrefixOperators { get; init; } = [];

    /// <summary>
    ///     Words which, immediately before a type, construct one: <c>new</c> in the C family. A word
    ///     and not an operator, so it is matched on a word boundary and under this profile's own case
    ///     rule — <c>renew(</c> constructs nothing. Empty for the languages that build an object some
    ///     other way, which is what X#'s <c>Foo{...}</c>, SQL and CSS mean.
    /// </summary>
    public IReadOnlyList<string> InstantiationKeywords { get; init; } = [];

    /// <summary>How an import line begins, matched against the trimmed line.</summary>
    public IReadOnlyList<string> ImportPrefixes { get; init; } = [];

    /// <summary>
    ///     Words that may introduce a member declaration. Keeping the list short and boring is the
    ///     point: a modifier this does not know costs an unplaced reference, while one it invents
    ///     costs a call reported as a declaration.
    /// </summary>
    public IReadOnlyList<string> DeclarationModifiers { get; init; } = [];

    /// <summary>Words that introduce a type declaration: <c>class</c>, <c>record</c>, <c>enum</c>.</summary>
    public IReadOnlyList<string> DeclarationKeywords { get; init; } = [];

    /// <summary>
    ///     Whether a member's name follows its introducing word directly — X#'s <c>method Foo()</c>,
    ///     Delphi's <c>procedure Foo(</c>, SQL's <c>create function foo(</c> — as against the C-family
    ///     shape where a return type sits between them. Both shapes are read where this is set,
    ///     because a language that has the first usually has the second somewhere too; only the
    ///     C-family shape is read where it is not, which is what the languages that never write the
    ///     first mean.
    /// </summary>
    public bool DeclarationNamesFollowKeyword { get; init; }

    /// <summary>Whether the language's keywords are case-insensitive, as X#, Delphi and SQL's are.</summary>
    public bool CaseInsensitiveKeywords { get; init; }

    /// <summary>See <see cref="ILanguageAnalyzer.SeparatesDeclarationFromImplementation" />.</summary>
    public bool SeparatesDeclarationFromImplementation { get; init; }

    /// <summary>
    ///     Qualified paths that mean a generated file, as globs where <c>*</c> crosses <c>/</c> — the
    ///     same shape the search filters take, so an operator reading both sees one syntax.
    /// </summary>
    public IReadOnlyList<string> GeneratedPathPatterns { get; init; } = [];
}
