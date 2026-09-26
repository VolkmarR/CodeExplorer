namespace CodeExplorer.Language;

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

    /// <summary>
    ///     Whether the opener's last character may be repeated, with the closer then repeated as many
    ///     times: C#'s raw literal, opened by three quotes or more and closed by exactly as many. A
    ///     fixed closer read one opened with four as closed by the <c>"""</c> it was written to hold.
    /// </summary>
    public bool OpenerRepeats { get; init; }
}

/// <summary>
///     A line that puts everything below it on one side of the declaration/implementation split:
///     Delphi's <c>implementation</c>, a PL/SQL <c>create package body</c>. It holds from the line it
///     stands on — the header of a package body is itself the body's declaration — until another
///     marker replaces it, which is what lets a spec and a body in two different files be told apart
///     without either knowing about the other.
///     Matched at the start of the line's text, on a word boundary and under the profile's own case
///     rule, with one run of whitespace matching any other so that <c>CREATE  OR REPLACE  PACKAGE
///     BODY</c> is the phrase it reads as. Longest phrase first, so <c>create package body</c> is not
///     read as the <c>create package</c> it begins with.
/// </summary>
public sealed record SectionMarker(string Phrase, DeclarationRole Role);

/// <summary>
///     One way this language names a file it depends on: C#'s <c>using</c>, Delphi's <c>uses</c>
///     clause, a <c>&lt;script src&gt;</c> in markup. The forms diverge more than they look, which is
///     why this is a record of what varies rather than the list of prefixes it began as — a prefix
///     alone finds <c>uses Vcl.Forms,</c> and then loses the three units on the lines below it.
///     The rule the rest of this file follows holds here too, and harder: a form this does not know
///     costs an edge that is missing, a form it invents costs an edge reported as read from the code.
///     Leave a doubtful form out.
/// </summary>
/// <param name="Opener">
///     What begins the form, matched under the profile's own case rule. Longest first is not required
///     of the profile — <see cref="TextAnalyzer" /> orders them — so <c>global using </c> may be
///     written after <c>using </c> and is still tried before it.
/// </param>
/// <param name="Shape">What kind of name the form names, which is what decides how it is resolved.</param>
public sealed record ImportForm(string Opener, ImportShape Shape)
{
    /// <summary>
    ///     The opener without its trailing space — what can be searched for, where the whitespace
    ///     after it is matched by the run rather than by the character. Computed once with the form
    ///     rather than where it is used: a build asks for it per line of every file.
    /// </summary>
    public string Head { get; } = Opener.TrimEnd();

    /// <summary>
    ///     Whether the opener may sit anywhere on the line rather than at the start of its text.
    ///     <c>require(</c> and <c>url(</c> are written mid-expression; <c>using</c> and <c>uses</c>
    ///     begin their line, and reading them anywhere would make <c>// see the uses clause</c> an
    ///     import of "clause".
    /// </summary>
    public bool Anywhere { get; init; }

    /// <summary>
    ///     For a markup form, the attribute whose quoted value is the path: <c>src</c> on a
    ///     <c>&lt;script&gt;</c>, <c>href</c> on a <c>&lt;link&gt;</c>. The tag is the opener and this
    ///     is what is read out of it, because a bare <c>href=</c> anywhere would make every
    ///     <c>&lt;a&gt;</c> in the document a dependency.
    /// </summary>
    public string? Attribute { get; init; }

    /// <summary>
    ///     What ends the clause, where the end of the line is not it: Delphi's <c>;</c>, the <c>)</c>
    ///     of CSS's <c>url(</c>. Null means the clause ends with its line.
    /// </summary>
    public string? Closer { get; init; }

    /// <summary>Whether one clause lists several names, comma-separated. Delphi's <c>uses</c>.</summary>
    public bool Separated { get; init; }

    /// <summary>
    ///     Whether an unclosed clause carries on onto the next line, which only a form with a
    ///     <see cref="Closer" /> can do. Delphi's <c>uses</c> is the one that does: it runs as many
    ///     lines as it likes until its <c>;</c>, and it appears twice in a unit.
    /// </summary>
    public bool SpansLines { get; init; }

    /// <summary>
    ///     Whether the form says what this file <em>is</em> rather than what it needs: C#'s
    ///     <c>namespace</c>, Delphi's <c>unit</c>. It is the other half of an import edge — without it
    ///     a name has nothing in the project to resolve against, and the reverse direction cannot be
    ///     answered at all.
    /// </summary>
    public bool Declares { get; init; }
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
    ///     Openers recognised only at the start of a trimmed line, outside any block comment. xBase's
    ///     <c>*</c> is here and not above because it is a comment at the start of a line and a
    ///     multiplication anywhere else. A language whose leading <c>*</c> is only the continuation of
    ///     a <c>/* */</c> block leaves it out: that line is inside the block the scan already carries,
    ///     and at the top level the same <c>*</c> is code.
    /// </summary>
    public IReadOnlyList<string> LineStartComments { get; init; } = [];

    /// <summary>
    ///     Line-start forms that look like a comment opener and are not: <c>#include</c> against a
    ///     leading <c>#</c>, Delphi's <c>{$IFDEF}</c> against its <c>{ }</c> comments. Without these a
    ///     compiler directive reads as prose, which is how <c>#region</c> used to hide a whole line.
    /// </summary>
    public IReadOnlyList<string> DirectivePrefixes { get; init; } = [];

    /// <summary>
    ///     Directive words whose line is prose: the label after <c>#region</c> and <c>#endregion</c>,
    ///     the message after <c>#error</c> and <c>#warning</c>, as C# and X# write them.
    ///     Matched at the start of the line's text, as a whole word and under the profile's own case
    ///     rule, and the whole line is then a comment, so no literal opens in it. Without this, an
    ///     apostrophe in <c>#region Don't touch</c> opened a char literal, and a name in the label read
    ///     as a string with one and as code without (#265). The directives that name symbols —
    ///     <c>#if</c>, <c>#define</c> — are not listed, because what they name is code.
    /// </summary>
    public IReadOnlyList<string> ProseDirectives { get; init; } = [];

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

    /// <summary>
    ///     How this language names the files it depends on. Empty where it has no import concept at
    ///     all, which is what the SQL family means and which is a different answer from a file that
    ///     imports nothing — <see cref="ILanguageAnalyzer.HasImports" /> is read off this for that
    ///     reason.
    /// </summary>
    public IReadOnlyList<ImportForm> ImportForms { get; init; } = [];

    /// <summary>
    ///     How short a path an import here may be written. The default is that it may not: a path
    ///     names the file it spells, which is true of every language here but TypeScript and
    ///     JavaScript, and applying their rule to the rest answered a markup <c>src="a"</c> with
    ///     <c>a.html</c>.
    /// </summary>
    public ImportPathRules ImportPaths { get; init; } = ImportPathRules.AsWritten;

    /// <summary>
    ///     Words that may introduce a member declaration — what a declaration shape is built from.
    ///     Keeping the list short and boring is the point: a modifier this does not know costs an
    ///     unplaced reference, while one it invents costs a call reported as a declaration.
    ///     This says what introduces a <em>name</em> and nothing about scope; <see cref="ScopeModifiers" />
    ///     is the second, narrower question (ADR-0008).
    /// </summary>
    public IReadOnlyList<string> DeclarationModifiers { get; init; } = [];

    /// <summary>
    ///     The subset of <see cref="DeclarationModifiers" /> that also opens a scope — what a reference
    ///     below the line may be labelled with. Empty means every modifier does, which is what a
    ///     profile with nothing to distinguish means and what every profile meant before there were
    ///     two lists.
    ///     Two lists because one could not answer both questions (#83). X#'s <c>define</c> introduces
    ///     a name and opens nothing, so with a single list it had to be left out and a file of nothing
    ///     but named constants declared nothing at all; the C family's <c>const</c> was never left out
    ///     and every reference under a local <c>const int Max = 10;</c> was labelled <c>Max</c> instead
    ///     of with the method. A modifier listed here and not above is ignored: the narrower list is
    ///     read as a filter on the wider one and never as a way to add a shape.
    /// </summary>
    public IReadOnlyList<string> ScopeModifiers { get; init; } = [];

    /// <summary>
    ///     Words that may follow a modifier and are never the type of a member being declared, so a
    ///     line carrying one is not a declaration however much it is shaped like one. TypeScript's
    ///     <c>export default thing;</c> is the case that needs it: <c>export</c> is a modifier, so the
    ///     line is modifier-word-word-<c>;</c> — the field shape exactly — while it names a binding
    ///     declared elsewhere.
    ///     Here and not welded into the shared pattern for the reason <see cref="InstantiationKeywords" />
    ///     is here (ADR-0008): a keyword in the shared pattern is a language fact the profile author
    ///     cannot see. Empty for every language that writes no such line, which is most of them, and
    ///     matched under this profile's own case rule like every other word.
    /// </summary>
    public IReadOnlyList<string> NonTypeKeywords { get; init; } = [];

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

    /// <summary>
    ///     Whether a type is declared with its name in front of the keyword — Delphi's
    ///     <c>TCustomer = class(TBase)</c> — as against the C-family <c>class TCustomer</c>. Where this
    ///     is set both shapes are read, because a unit that writes the first usually writes
    ///     <c>type TAlias = Integer</c> somewhere too and neither costs the other anything.
    /// </summary>
    public bool TypeNamesPrecedeKeyword { get; init; }

    /// <summary>
    ///     Words that open what a declaration declares, where the language opens it with a word rather
    ///     than with a bracket: the SQL family's <c>create procedure foo is</c>, X#'s
    ///     <c>access Status as string</c>. The brackets are the shape of a declaration head and are the
    ///     same everywhere; these are the language's own, which is why they are here and not welded
    ///     into the shared pattern beside them.
    /// </summary>
    public IReadOnlyList<string> DeclarationBodyOpeners { get; init; } = [];

    /// <summary>Whether the language's keywords are case-insensitive, as X#, Delphi and SQL's are.</summary>
    public bool CaseInsensitiveKeywords { get; init; }

    /// <summary>
    ///     What moves a file from one side of the declaration/implementation split to the other, or
    ///     empty where the language has no split at all.
    /// </summary>
    public IReadOnlyList<SectionMarker> SectionMarkers { get; init; } = [];

    /// <summary>
    ///     See <see cref="ILanguageAnalyzer.SeparatesDeclarationFromImplementation" />. Read off
    ///     <see cref="SectionMarkers" /> rather than declared beside them: a profile that named the
    ///     phrases and forgot the flag would answer that its language has no split while reporting
    ///     which half every line is in, and the two could only ever disagree by mistake. (An analyser
    ///     with a parser behind it has no marker table and answers the question directly.)
    /// </summary>
    public bool SeparatesDeclarationFromImplementation => SectionMarkers.Count > 0;

    /// <summary>
    ///     Qualified paths that mean a generated file, as globs where <c>*</c> crosses <c>/</c> — the
    ///     same shape the search filters take, so an operator reading both sees one syntax.
    /// </summary>
    public IReadOnlyList<string> GeneratedPathPatterns { get; init; } = [];
}
