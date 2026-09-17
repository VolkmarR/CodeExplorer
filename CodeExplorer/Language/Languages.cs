namespace CodeExplorer;

/// <summary>
///     Every language this build knows, and everything it knows about them. The registration below is
///     the whole of it, so a reader sees the supported set at once and adding a language touches no
///     caller.
///     It answers null for an extension no profile covers rather than guessing. An extension nobody
///     declared stands for itself in an answer, which is a weaker statement than a wrong language
///     name — the same rule the declaration modifiers follow: a form this does not know costs a
///     vaguer answer, a form it invents costs a wrong one reported as right.
///     Only the languages this server exists to serve are here. A general-purpose list would make the
///     coverage look complete when it is not, and an extension left out degrades to the conservative
///     <see cref="Fallback" />, which is the intended failure.
/// </summary>
public static class Languages
{
    /// <summary>
    ///     What an unmapped extension is called where one has to be named, for the files that have no
    ///     extension at all. They are one group and not one per file, and "" would print as nothing.
    /// </summary>
    public const string NoExtension = "(no extension)";

    /// <summary>
    ///     What the C family calls a modifier. Shared because it is the same list in C#, TypeScript and
    ///     JavaScript, and a per-language copy would drift.
    /// </summary>
    private static readonly string[] CFamilyModifiers =
    [
        "public", "private", "protected", "internal", "static", "async", "override", "virtual",
        "abstract", "sealed", "partial", "extern", "new", "readonly", "const", "export", "declare",
        "function", "def", "val", "let"
    ];

    /// <summary>
    ///     What may introduce a declaration in SQL, which is a phrase rather than a modifier.
    ///     <c>or replace</c> is one entry and not two: a bare <c>or</c> would make the wrapped line of
    ///     any <c>WHERE</c> clause read as a declaration, and a false declaration is worse than an
    ///     unplaced reference because DECLARATIONS is the section an agent trusts most.
    /// </summary>
    private static readonly string[] SqlModifiers =
        ["create", "alter", "or replace", "declare", "procedure", "function", "view", "table", "trigger", "type"];

    private static readonly StringDelimiter CBlockComment = new("/*", "*/", StringEscape.None);
    private static readonly StringDelimiter DoubleQuoted = new("\"", "\"", StringEscape.Backslash);
    private static readonly StringDelimiter SingleQuotedEscaped = new("'", "'", StringEscape.Backslash);

    /// <summary>The xBase and SQL family's quote, where doubling it stands for the character itself.</summary>
    private static readonly StringDelimiter SingleQuoted = new("'", "'", StringEscape.Doubled);

    /// <summary>Quotes that escape nothing: the first closer ends the literal, whatever precedes it.</summary>
    private static readonly StringDelimiter RawDouble = new("\"", "\"", StringEscape.None);

    private static readonly StringDelimiter RawSingle = new("'", "'", StringEscape.None);

    /// <summary>
    ///     The holes of live code in an interpolated literal. One for C# and another for the template
    ///     literal, because the languages spell the opener differently; both close and nest on the brace.
    /// </summary>
    private static readonly Hole CSharpHole = new("{", "}", "{");

    private static readonly Hole TemplateHole = new("${", "}", "{");

    /// <summary>
    ///     The C# literals that carry on past the end of a line, which is what makes them #53's: a
    ///     verbatim or raw literal holds newlines, and an identifier on its second line is a mention
    ///     and not a use. The interpolated forms name their holes, so a call written inside one is
    ///     still read as a call — a literal that swallowed its holes would lose real calls, which is
    ///     the worse of the two errors.
    ///     Written longest opener first for a reader; the analyser orders them itself.
    /// </summary>
    private static readonly StringDelimiter[] CSharpLiterals =
    [
        new StringDelimiter("$\"\"\"", "\"\"\"", StringEscape.None)
            { SpansLines = true, Hole = CSharpHole },
        new StringDelimiter("\"\"\"", "\"\"\"", StringEscape.None) { SpansLines = true },
        new StringDelimiter("$@\"", "\"", StringEscape.Doubled)
            { SpansLines = true, Hole = CSharpHole },
        new StringDelimiter("@$\"", "\"", StringEscape.Doubled)
            { SpansLines = true, Hole = CSharpHole },
        new StringDelimiter("@\"", "\"", StringEscape.Doubled) { SpansLines = true },
        new StringDelimiter("$\"", "\"", StringEscape.Backslash)
            { Hole = CSharpHole },
        DoubleQuoted
    ];

    /// <summary>
    ///     The JavaScript and TypeScript template literal. It was left out of both profiles until now
    ///     because it spans lines and holds <c>${…}</c> holes that are code, and a delimiter that knew
    ///     neither reported every call made inside one as a string mention.
    /// </summary>
    private static readonly StringDelimiter Template =
        new("`", "`", StringEscape.Backslash)
            { SpansLines = true, Hole = TemplateHole };

    /// <summary>The C family builds an object with a word in front of the type.</summary>
    private static readonly string[] New = ["new"];

    /// <summary>
    ///     What answers for an extension no profile claims. It is today's behaviour exactly, kept that
    ///     way on purpose: a project written in a language nobody declared must read no worse after
    ///     this seam than before it, and the way to be sure is for the fallback to be the old rules
    ///     unchanged.
    /// </summary>
    private static readonly LanguageProfile FallbackProfile = new(null, [])
    {
        LineComments = ["//"],
        LineStartComments = ["*", "--", "#"],
        DirectivePrefixes = ["#include"],
        BlockComments = [CBlockComment],
        Strings = [DoubleQuoted],
        AssignmentOperators = ["="],
        MemberAccessOperators = ["."],
        TypePrefixOperators = [":", "<", ","],
        ImportPrefixes = ["using ", "import ", "namespace ", "#include", "from ", "package ", "require("],
        InstantiationKeywords = New,
        DeclarationModifiers = CFamilyModifiers,
        DeclarationKeywords = ["class", "interface", "struct", "record", "enum"]
    };

    /// <summary>
    ///     The languages, each claiming its extensions. Lowercase and without the dot, which is how
    ///     <c>files.extension</c> is stored.
    /// </summary>
    private static readonly LanguageProfile[] Registered =
    [
        // The dialect this repository's operators mostly read. `.vh` and `.xh` are its headers, which
        // are X# source and are counted as such rather than as a category of their own.
        // `=` is a comparison here and `:=` the assignment, so listing `=` would report every equality
        // test as a write. `:` is the send and `.` reaches .NET members, so both are member access —
        // which is why `:` is not in TypePrefixOperators as it is for C#.
        new LanguageProfile("X#", ["prg", "vh", "xh", "ch"])
        {
            LineComments = ["//", "&&"],
            LineStartComments = ["*"],
            BlockComments = [CBlockComment],
            // The VO dialect has no backslash escape: a path in a literal is a path, not an escape.
            Strings = [RawDouble, RawSingle],
            AssignmentOperators = [":="],
            MemberAccessOperators = [":", "."],
            TypePrefixOperators = ["<", ","],
            ImportPrefixes = ["using ", "#using", "#include"],
            // `local`, `instance` and `define` are deliberately absent. They introduce a name, but not
            // a scope, and every modifier here is also what DeclarationScope reads as one: with them
            // in, every reference below a `local cLabel := …` was labelled with the local instead of
            // with the method it sits in.
            DeclarationModifiers =
            [
                "public", "private", "protected", "internal", "export", "hidden", "static", "virtual",
                "override", "abstract", "sealed", "partial", "const",
                "function", "procedure", "method", "access", "assign", "property", "event", "delegate"
            ],
            DeclarationKeywords = ["class", "interface", "struct", "structure", "vostruct", "union", "enum"],
            DeclarationNamesFollowKeyword = true,
            CaseInsensitiveKeywords = true,
            GeneratedPathPatterns = ["*_vo.prg", "*.designer.prg"]
        },
        new LanguageProfile("C#", ["cs", "csx"])
        {
            LineComments = ["//"],
            LineStartComments = ["*"],
            BlockComments = [CBlockComment],
            Strings = CSharpLiterals,
            AssignmentOperators = ["="],
            MemberAccessOperators = ["."],
            TypePrefixOperators = [":", "<", ","],
            // `#` opens no comment here, so `#if` and `#region` no longer read as prose.
            ImportPrefixes = ["using ", "global using ", "namespace "],
            InstantiationKeywords = New,
            DeclarationModifiers = CFamilyModifiers,
            DeclarationKeywords = ["class", "interface", "struct", "record", "enum"],
            GeneratedPathPatterns = ["*.g.cs", "*.designer.cs", "*.generated.cs"]
        },
        new LanguageProfile("TypeScript", ["ts", "tsx", "mts", "cts"])
        {
            LineComments = ["//"],
            LineStartComments = ["*"],
            BlockComments = [CBlockComment],
            // `--` is a decrement here, not a comment: treating it as one hid the rest of every line
            // a trimmed `--i` started.
            Strings = [Template, DoubleQuoted, SingleQuotedEscaped],
            AssignmentOperators = ["="],
            MemberAccessOperators = ["."],
            TypePrefixOperators = [":", "<", ","],
            ImportPrefixes = ["import "],
            InstantiationKeywords = New,
            DeclarationModifiers = CFamilyModifiers,
            DeclarationKeywords = ["class", "interface", "enum", "type"]
        },
        new LanguageProfile("JavaScript", ["js", "jsx", "mjs", "cjs"])
        {
            LineComments = ["//"],
            LineStartComments = ["*"],
            BlockComments = [CBlockComment],
            Strings = [Template, DoubleQuoted, SingleQuotedEscaped],
            AssignmentOperators = ["="],
            MemberAccessOperators = ["."],
            TypePrefixOperators = ["<", ","],
            ImportPrefixes = ["import ", "require("],
            InstantiationKeywords = New,
            DeclarationModifiers = CFamilyModifiers,
            DeclarationKeywords = ["class"]
        },
        new LanguageProfile("Delphi", ["pas", "dpr", "dpk", "dfm"])
        {
            LineComments = ["//"],
            // `{ }` and `(* *)` are comments, but `{$IFDEF}` is a compiler directive and not one.
            BlockComments = [new StringDelimiter("{", "}", StringEscape.None), new StringDelimiter("(*", "*)", StringEscape.None), CBlockComment],
            DirectivePrefixes = ["{$", "(*$"],
            Strings = [SingleQuoted],
            AssignmentOperators = [":="],
            MemberAccessOperators = ["."],
            TypePrefixOperators = [":", ","],
            ImportPrefixes = ["uses ", "unit "],
            // `var` and `const` are left out for the reason X#'s `local` is: they open a block of
            // names, not a scope, and DeclarationScope would label everything under one with it.
            DeclarationModifiers =
            [
                "procedure", "function", "constructor", "destructor", "property",
                "type", "class", "published", "private", "protected", "public", "strict", "override",
                "virtual", "overload", "static"
            ],
            DeclarationKeywords = ["class", "record", "interface", "object"],
            DeclarationNamesFollowKeyword = true,
            CaseInsensitiveKeywords = true,
            SeparatesDeclarationFromImplementation = true
        },
        new LanguageProfile("HTML", ["html", "htm"])
        {
            BlockComments = [new StringDelimiter("<!--", "-->", StringEscape.None)],
            // An attribute value may hold a newline and is still not marked as spanning one, for the
            // reason SQL's literal is not: an unclosed quote in markup is far likelier to be a typo,
            // a stray apostrophe or a tag this does not parse than a genuine multi-line value, and a
            // literal read as spanning takes every line below it with it.
            Strings = [RawDouble, RawSingle],
            AssignmentOperators = ["="],
            MemberAccessOperators = ["."],
            CaseInsensitiveKeywords = true
        },
        new LanguageProfile("CSS", ["css"])
        {
            // `/* */` is the only comment form CSS has; `//` is not one, whatever a preprocessor does.
            BlockComments = [CBlockComment],
            Strings = [DoubleQuoted, SingleQuotedEscaped],
            // A declaration here is `property: value`, which is the nearest thing CSS has to a write.
            AssignmentOperators = [":"],
            ImportPrefixes = ["@import"],
            CaseInsensitiveKeywords = true
        },
        // `.sql` alone is SQL. PL/SQL is told apart by the package and program-unit extensions, which
        // is the only signal an extension carries: a `.sql` file holding a package body is counted as
        // SQL, because deciding otherwise would mean reading it.
        new LanguageProfile("SQL", ["sql"])
        {
            LineComments = ["--"],
            BlockComments = [CBlockComment],
            // A SQL literal may legally hold a newline, and it is not marked as spanning one anyway.
            // A literal that spans is read as open until its closing quote, so one odd quote — in a
            // dialect this does not know, in a string this misreads — would turn the rest of the file
            // into a string. The multi-line literal is rare in this code and the cost of getting it
            // wrong is every line below it.
            Strings = [SingleQuoted],
            AssignmentOperators = ["="],
            MemberAccessOperators = ["."],
            DeclarationModifiers = SqlModifiers,
            DeclarationNamesFollowKeyword = true,
            CaseInsensitiveKeywords = true
        },
        new LanguageProfile("PL/SQL", ["pks", "pkb", "plsql", "prc", "fnc", "trg"])
        {
            LineComments = ["--"],
            BlockComments = [CBlockComment],
            Strings = [SingleQuoted],
            AssignmentOperators = [":="],
            MemberAccessOperators = ["."],
            DeclarationModifiers = [.. SqlModifiers, "package", "body", "cursor", "exception"],
            DeclarationNamesFollowKeyword = true,
            CaseInsensitiveKeywords = true,
            SeparatesDeclarationFromImplementation = true
        }
    ];

    private static ILanguageAnalyzer Fallback { get; } = new TextAnalyzer(FallbackProfile);

    /// <summary>The set every caller resolves against unless it was handed another.</summary>
    public static LanguageRegistry Default { get; } =
        new([.. Registered.Select(profile => (ILanguageAnalyzer)new TextAnalyzer(profile))], Fallback);

    /// <summary>
    ///     The language this extension means, or null when no profile covers it. The extension is
    ///     spelled the way <c>files.extension</c> stores it — lowercase, no leading dot — and a leading
    ///     dot is tolerated so a caller quoting a file name does not have to strip it.
    /// </summary>
    public static string? Of(string extension) => Default.For(extension).Language;

    /// <summary>
    ///     What to call this extension in an answer: its language where one is known, the extension
    ///     itself where none is. The second half of the pair is what lets a reply say which of the two
    ///     it is, because "X#" and ".vh" are claims of different strength and must not read alike.
    /// </summary>
    public static (string Name, bool Mapped) Name(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        if (Of(extension) is { } language) return (language, true);
        return (extension.Length == 0 ? NoExtension : "." + extension, false);
    }
}
