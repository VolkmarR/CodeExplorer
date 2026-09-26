using System.Collections.Frozen;

namespace CodeExplorer.Language;

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
    ///     The extensions whose missing profile costs a search nothing: prose, data and build
    ///     metadata. A repository holds these whatever it is written in, so without this list every
    ///     answer that met a README or a project file would carry a caveat about an unprofiled
    ///     language — the note an agent learns to skip, and the one case where it then skips the note
    ///     that mattered (#126).
    ///     It is a list of what is <em>not</em> code rather than a second language table: a profile
    ///     says how a language writes a declaration, and Markdown has no answer to give, where Python
    ///     or Go has one this build simply does not know. Being absent from both lists is what makes
    ///     an extension worth reporting, so a language added to neither is reported rather than
    ///     silently dropped — the safe direction.
    ///     Files with no extension are here for the same reason: LICENSE, CHANGELOG and the like are
    ///     what a repository holds without an extension, and a Makefile among them is not worth a
    ///     caveat on every reply.
    /// </summary>
    private static readonly FrozenSet<string> _notCode = new[]
    {
        "", "md", "markdown", "mdx", "txt", "rst", "adoc", "asciidoc", "json", "jsonc", "yml", "yaml",
        "toml", "ini", "cfg", "conf", "config", "properties", "env", "csv", "tsv", "xml", "resx",
        "csproj", "vbproj", "fsproj", "sqlproj", "sln", "props", "targets", "nuspec", "lock", "log",
        "svg", "png", "jpg", "jpeg", "gif", "ico", "pdf"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Whether an extension could hold code a language profile would have read differently. False
    ///     for the prose and data a repository carries whatever it is written in; true for everything
    ///     else, including the languages this build has no profile for, which is the answer a caller
    ///     reports on. The extension is spelled the way <c>files.extension</c> stores it.
    /// </summary>
    public static bool MightHoldCode(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return !_notCode.Contains(extension.TrimStart('.'));
    }

    /// <summary>
    ///     What the C family calls a modifier, as the union of what C#, TypeScript and JavaScript
    ///     write rather than the intersection: <c>export</c> and <c>declare</c> are TypeScript's and
    ///     <c>event</c> is C#'s, and none of them is a modifier in all three. One list because a word
    ///     a language does not write is a word that never appears at the head of its lines, which
    ///     costs it nothing, where a per-language copy would drift.
    /// </summary>
    private static readonly string[] _cFamilyModifiers =
    [
        "public", "private", "protected", "internal", "static", "async", "override", "virtual",
        "abstract", "sealed", "partial", "extern", "new", "readonly", "const", "export", "declare",
        "function", "def", "val", "let", "event"
    ];

    /// <summary>
    ///     What follows a C-family modifier and is never the type of a member. <c>default</c> alone,
    ///     for TypeScript's <c>export default thing;</c>: <c>export</c> is a modifier above, which
    ///     makes that line modifier-word-word-<c>;</c> — a field, read literally. Shared with the
    ///     modifier list it guards, so the two cannot come apart.
    /// </summary>
    private static readonly string[] _cFamilyNonTypes = ["default"];

    /// <summary>
    ///     Which of those also open a scope, which is the narrower of the two questions one list used
    ///     to answer at once (#83, ADR-0008). What is left is what can head a body: <c>function</c>,
    ///     <c>def</c>, and the access, static and inheritance modifiers that decorate one.
    ///     Written as the five that drop out, because that is the decision. <c>const</c>,
    ///     <c>readonly</c>, <c>val</c>, <c>let</c> and <c>event</c> introduce a name and hold no lines,
    ///     and a <b>local</b> <c>const int Max = 10;</c> or <c>readonly Span&lt;int&gt; s = …;</c>
    ///     matches the member shape like any field — so every reference indented under one was
    ///     labelled <c>Max</c> rather than with the method it sits in, in C#, TypeScript and JavaScript
    ///     alike. That is the regression the X# profile cited as its reason for leaving <c>define</c>
    ///     out, live here all along and unmitigated.
    ///     Derived from the list above rather than written out beside it, so a modifier added there
    ///     opens a scope unless it is named here and the two cannot come apart.
    /// </summary>
    private static readonly string[] _cFamilyScopeModifiers =
        [.. _cFamilyModifiers.Except(["const", "readonly", "val", "let", "event"])];

    /// <summary>
    ///     What opens a scope in X#: the routine and member words, and every visibility and inheritance
    ///     modifier that can stand in front of one. This is the list the profile carried before the two
    ///     questions came apart (#83), unchanged — the name list is this plus the three words that
    ///     introduce a name and hold no lines, which the profile explains.
    /// </summary>
    private static readonly string[] _xSharpScopeModifiers =
    [
        "public", "private", "protected", "internal", "export", "hidden", "static", "virtual",
        "override", "abstract", "sealed", "partial", "const",
        "function", "procedure", "method", "access", "assign", "property", "event", "delegate"
    ];

    /// <summary>
    ///     What opens a scope in Delphi, which is what the profile listed before <c>var</c> and
    ///     <c>const</c> could be admitted beside them (#83). A routine, a property or a type heads
    ///     something that holds lines; a <c>var</c> or <c>const</c> entry names a variable or a
    ///     constant and heads nothing.
    /// </summary>
    private static readonly string[] _delphiScopeModifiers =
    [
        "procedure", "function", "constructor", "destructor", "property",
        "type", "class", "published", "private", "protected", "public", "strict", "override",
        "virtual", "overload", "static"
    ];

    /// <summary>
    ///     What may introduce a declaration in SQL, which is a phrase rather than a modifier.
    ///     <c>or replace</c> is one entry and not two: a bare <c>or</c> would make the wrapped line of
    ///     any <c>WHERE</c> clause read as a declaration, and a false declaration is worse than an
    ///     unplaced reference because DECLARATIONS is the section an agent trusts most.
    /// </summary>
    private static readonly string[] _sqlModifiers =
        ["create", "alter", "or replace", "declare", "procedure", "function", "view", "table", "trigger", "type"];

    /// <summary>What opens a routine body in the SQL family, where the C family writes a bracket.</summary>
    private static readonly string[] _sqlBodyOpeners = ["as", "is"];

    /// <summary>
    ///     What moves an Oracle file from a package spec to a package body. Shared by SQL and PL/SQL
    ///     because a script holding both is written with the same two headers whatever it is named:
    ///     the extension says which dialect to read a file as and never which half of one it is.
    /// </summary>
    private static readonly SectionMarker[] _packageSections =
    [
        new SectionMarker("create or replace package body", DeclarationRole.Implementation),
        new SectionMarker("create package body", DeclarationRole.Implementation),
        new SectionMarker("create or replace package", DeclarationRole.Declaration),
        new SectionMarker("create package", DeclarationRole.Declaration)
    ];

    /// <summary>
    ///     The directives C# and X# both write whose rest of line is a label or a message, not code,
    ///     read as line-start comments (#265). The ones that name symbols — <c>#if</c>,
    ///     <c>#define</c> — are not here, because what they name is code.
    /// </summary>
    private static readonly string[] _proseDirectives = ["#region", "#endregion", "#error", "#warning"];

    private static readonly StringDelimiter _cBlockComment = new("/*", "*/", StringEscape.None);
    private static readonly StringDelimiter _doubleQuoted = new("\"", "\"", StringEscape.Backslash);
    private static readonly StringDelimiter _singleQuotedEscaped = new("'", "'", StringEscape.Backslash);

    /// <summary>The xBase and SQL family's quote, where doubling it stands for the character itself.</summary>
    private static readonly StringDelimiter _singleQuoted = new("'", "'", StringEscape.Doubled);

    /// <summary>Quotes that escape nothing: the first closer ends the literal, whatever precedes it.</summary>
    private static readonly StringDelimiter _rawDouble = new("\"", "\"", StringEscape.None);

    private static readonly StringDelimiter _rawSingle = new("'", "'", StringEscape.None);

    /// <summary>The C family's char literal, which opens only in its own short shape (#295).</summary>
    private static readonly StringDelimiter _charLiteral =
        new("'", "'", StringEscape.Backslash) { HoldsOneCharacter = true };

    /// <summary>
    ///     The holes of live code in an interpolated literal. One for C# and another for the template
    ///     literal, because the languages spell the opener differently; both close and nest on the brace.
    /// </summary>
    private static readonly Hole _cSharpHole = new("{", "}", "{");

    private static readonly Hole _templateHole = new("${", "}", "{");

    /// <summary>
    ///     The C# literals that carry on past the end of a line, which is what makes them #53's: a
    ///     verbatim or raw literal holds newlines, and an identifier on its second line is a mention
    ///     and not a use. The interpolated forms name their holes, so a call written inside one is
    ///     still read as a call — a literal that swallowed its holes would lose real calls, which is
    ///     the worse of the two errors.
    ///     A raw literal is opened by three quotes or more and closed by as many
    ///     (<see cref="StringDelimiter.OpenerRepeats" />). The char literal is here because without it <c>'"'</c> opened a string that took the rest
    ///     of its line (#240). It opens only in its own short shape
    ///     (<see cref="StringDelimiter.HoldsOneCharacter" />), so a stray apostrophe opens nothing (#295).
    ///     Written longest opener first for a reader; the analyser orders them itself.
    /// </summary>
    private static readonly StringDelimiter[] _cSharpLiterals =
    [
        new StringDelimiter("$\"\"\"", "\"\"\"", StringEscape.None)
            { SpansLines = true, Hole = _cSharpHole, OpenerRepeats = true },
        new StringDelimiter("\"\"\"", "\"\"\"", StringEscape.None) { SpansLines = true, OpenerRepeats = true },
        new StringDelimiter("$@\"", "\"", StringEscape.Doubled)
            { SpansLines = true, Hole = _cSharpHole },
        new StringDelimiter("@$\"", "\"", StringEscape.Doubled)
            { SpansLines = true, Hole = _cSharpHole },
        new StringDelimiter("@\"", "\"", StringEscape.Doubled) { SpansLines = true },
        new StringDelimiter("$\"", "\"", StringEscape.Backslash)
            { Hole = _cSharpHole },
        _doubleQuoted,
        _charLiteral
    ];

    /// <summary>
    ///     The JavaScript and TypeScript template literal. It was left out of both profiles until now
    ///     because it spans lines and holds <c>${…}</c> holes that are code, and a delimiter that knew
    ///     neither reported every call made inside one as a string mention.
    /// </summary>
    private static readonly StringDelimiter _template =
        new("`", "`", StringEscape.Backslash)
            { SpansLines = true, Hole = _templateHole };

    /// <summary>The C family builds an object with a word in front of the type.</summary>
    private static readonly string[] _new = ["new"];

    /// <summary>
    ///     How TypeScript and JavaScript name a module, which is the same three forms in both and so
    ///     is written once. Every one of them names a path rather than a namespace — even a bare
    ///     <c>import "react"</c>, which names a package this project does not hold and which
    ///     therefore resolves to nothing and says so.
    ///     The static <c>import</c> is read for its quoted specifier and not for the names in front
    ///     of it: <c>import { a, b } from "./c"</c> depends on <c>./c</c>, and <c>a</c> and <c>b</c>
    ///     are what it takes out of it.
    /// </summary>
    private static readonly ImportForm[] _ecmaImports =
    [
        new ImportForm("import ", ImportShape.Path),
        new ImportForm("import(", ImportShape.Path) { Anywhere = true, Closer = ")" },
        new ImportForm("require(", ImportShape.Path) { Anywhere = true, Closer = ")" }
    ];

    /// <summary>
    ///     Node's module resolution, which TypeScript and JavaScript share and nothing else here has:
    ///     a specifier may leave its extension off, and may name a directory that holds an index
    ///     file. It is declared rather than assumed because the resolver applied to every language
    ///     answered a markup <c>&lt;script src="a"&gt;</c> with <c>a.html</c> and <c>a/index.html</c>,
    ///     which is this rule read into a language that does not have it.
    /// </summary>
    private static readonly ImportPathRules _ecmaPaths =
        new(["ts", "tsx", "mts", "cts", "js", "jsx", "mjs", "cjs", "json"], "index");

    /// <summary>
    ///     What answers for an extension no profile claims. It is today's behaviour exactly, kept that
    ///     way on purpose: a project written in a language nobody declared must read no worse after
    ///     this seam than before it, and the way to be sure is for the fallback to be the old rules
    ///     unchanged.
    /// </summary>
    private static readonly LanguageProfile _fallbackProfile = new(null, [])
    {
        LineComments = ["//"],
        LineStartComments = ["*", "--", "#"],
        DirectivePrefixes = ["#include"],
        BlockComments = [_cBlockComment],
        Strings = [_doubleQuoted],
        AssignmentOperators = ["="],
        MemberAccessOperators = ["."],
        TypePrefixOperators = [":", "<", ","],
        // These decide nothing but ReferenceKind.Import here: a build extracts import edges only for
        // a language a profile claims, because an extension nobody declared is one whose import
        // forms nobody declared either, and an edge read out of it would be a guess with a path on
        // the end of it.
        ImportForms =
        [
            new ImportForm("using ", ImportShape.Module),
            new ImportForm("import ", ImportShape.Module),
            new ImportForm("namespace ", ImportShape.Module) { Declares = true },
            new ImportForm("#include", ImportShape.Path),
            new ImportForm("from ", ImportShape.Module),
            new ImportForm("package ", ImportShape.Module) { Declares = true },
            new ImportForm("require(", ImportShape.Path) { Anywhere = true, Closer = ")" }
        ],
        InstantiationKeywords = _new,
        // No scope list, and deliberately: a profile that names one list behaves as it did before
        // there were two (#83), and this one is today's behaviour exactly by contract — including the
        // rules that are wrong somewhere (ADR-0008). An extension nobody declared does not become the
        // place a C-family judgement is applied on its behalf.
        DeclarationModifiers = _cFamilyModifiers,
        NonTypeKeywords = _cFamilyNonTypes,
        DeclarationKeywords = ["class", "interface", "struct", "record", "enum"]
    };

    /// <summary>
    ///     The languages, each claiming its extensions. Lowercase and without the dot, which is how
    ///     <c>files.extension</c> is stored.
    /// </summary>
    private static readonly LanguageProfile[] _registered =
    [
        // The dialect this repository's operators mostly read. `.vh` and `.xh` are its headers, which
        // are X# source and are counted as such rather than as a category of their own.
        // `=` is a comparison here and `:=` the assignment, so listing `=` would report every equality
        // test as a write. `:` is the send and `.` reaches .NET members, so both are member access —
        // which is why `:` is not in TypePrefixOperators as it is for C#.
        new LanguageProfile("X#", ["prg", "vh", "xh", "ch"])
        {
            LineComments = ["//", "&&"],
            LineStartComments = ["*", .. _proseDirectives],
            BlockComments = [_cBlockComment],
            // The VO dialect has no backslash escape: a path in a literal is a path, not an escape.
            Strings = [_rawDouble, _rawSingle],
            AssignmentOperators = [":="],
            MemberAccessOperators = [":", "."],
            TypePrefixOperators = ["<", ","],
            // `#include` names a header file and the other two name a namespace, which is the whole
            // of the difference between resolving against a directory and resolving against what a
            // file declares itself to be. X# declares no namespace per file, so nothing here says
            // what a file IS and a `#using` resolves only where another project file happens to.
            ImportForms =
            [
                new ImportForm("using ", ImportShape.Module),
                new ImportForm("#using ", ImportShape.Module),
                new ImportForm("#include", ImportShape.Path)
            ],
            // `local`, `instance` and `define` are here and not in the scope list, which is what the
            // split is for (#83). All three introduce a name and open no scope, and with one list
            // answering both questions they had to be left out altogether to keep the scope label
            // right: the cost was that the five AcsLib files which are nothing but `define` lines
            // answered that they declared nothing at all — 952 names between them, measured on the
            // real index, of which the largest is `src/BaseGUI/_const.prg` at 787. Now
            // `find_definition` finds them and
            // `DeclarationScope` cannot reach for them, so a reference below a `local cLabel := …` is
            // still labelled with the method it sits in — by the scope list rather than by omission.
            DeclarationModifiers = [.. _xSharpScopeModifiers, "define", "local", "instance"],
            ScopeModifiers = _xSharpScopeModifiers,
            DeclarationKeywords = ["class", "interface", "struct", "structure", "vostruct", "union", "enum"],
            DeclarationNamesFollowKeyword = true,
            DeclarationBodyOpeners = ["as"],
            CaseInsensitiveKeywords = true,
            GeneratedPathPatterns = ["*_vo.prg", "*.designer.prg"]
        },
        new LanguageProfile("C#", ["cs", "csx"])
        {
            LineComments = ["//"],
            // No line-start `*`, as in every C-family profile (LanguageProfile.LineStartComments): at
            // the top level it is a multiplication or a dereference, and reading it as a comment hid
            // every name on the line (#240).
            LineStartComments = _proseDirectives,
            BlockComments = [_cBlockComment],
            Strings = _cSharpLiterals,
            AssignmentOperators = ["="],
            MemberAccessOperators = ["."],
            TypePrefixOperators = [":", "<", ","],
            // `#` opens no comment here, so `#if` and `#define` read their symbols as code.
            // The `namespace` line is here with the two `using` forms because it is the other half of
            // the same fact: it says what this file is, which is what another file's `using` has to
            // resolve against. Both the file-scoped `namespace Foo;` and the block `namespace Foo {`
            // read the same, because the name is what is wanted and the punctuation after it is not.
            ImportForms =
            [
                new ImportForm("using ", ImportShape.Module),
                new ImportForm("global using ", ImportShape.Module),
                new ImportForm("namespace ", ImportShape.Module) { Declares = true }
            ],
            InstantiationKeywords = _new,
            DeclarationModifiers = _cFamilyModifiers,
            ScopeModifiers = _cFamilyScopeModifiers,
            NonTypeKeywords = _cFamilyNonTypes,
            DeclarationKeywords = ["class", "interface", "struct", "record", "enum"],
            GeneratedPathPatterns = ["*.g.cs", "*.designer.cs", "*.generated.cs"]
        },
        new LanguageProfile("TypeScript", ["ts", "tsx", "mts", "cts"])
        {
            LineComments = ["//"],
            BlockComments = [_cBlockComment],
            // `--` is a decrement here, not a comment: treating it as one hid the rest of every line
            // a trimmed `--i` started.
            Strings = [_template, _doubleQuoted, _singleQuotedEscaped],
            AssignmentOperators = ["="],
            MemberAccessOperators = ["."],
            TypePrefixOperators = [":", "<", ","],
            ImportForms = _ecmaImports,
            ImportPaths = _ecmaPaths,
            InstantiationKeywords = _new,
            DeclarationModifiers = _cFamilyModifiers,
            ScopeModifiers = _cFamilyScopeModifiers,
            NonTypeKeywords = _cFamilyNonTypes,
            DeclarationKeywords = ["class", "interface", "enum", "type"]
        },
        new LanguageProfile("JavaScript", ["js", "jsx", "mjs", "cjs"])
        {
            LineComments = ["//"],
            BlockComments = [_cBlockComment],
            Strings = [_template, _doubleQuoted, _singleQuotedEscaped],
            AssignmentOperators = ["="],
            MemberAccessOperators = ["."],
            TypePrefixOperators = ["<", ","],
            ImportForms = _ecmaImports,
            ImportPaths = _ecmaPaths,
            InstantiationKeywords = _new,
            DeclarationModifiers = _cFamilyModifiers,
            ScopeModifiers = _cFamilyScopeModifiers,
            NonTypeKeywords = _cFamilyNonTypes,
            DeclarationKeywords = ["class"]
        },
        new LanguageProfile("Delphi", ["pas", "dpr", "dpk", "dfm"])
        {
            LineComments = ["//"],
            // `{ }` and `(* *)` are comments, but `{$IFDEF}` is a compiler directive and not one.
            BlockComments = [new StringDelimiter("{", "}", StringEscape.None), new StringDelimiter("(*", "*)", StringEscape.None), _cBlockComment],
            DirectivePrefixes = ["{$", "(*$"],
            Strings = [_singleQuoted],
            AssignmentOperators = [":="],
            MemberAccessOperators = ["."],
            TypePrefixOperators = [":", ","],
            // The awkward one. A `uses` clause is comma-separated, runs as many lines as it likes
            // until its `;`, and a unit writes one in `interface` and another in `implementation` —
            // so a prefix read one line at a time finds the first unit of each clause and loses
            // every unit under it. The `unit` line is the other half: it is what a `uses` elsewhere
            // resolves against, and Delphi is the language where that mapping is actually exact.
            ImportForms =
            [
                new ImportForm("uses ", ImportShape.Module)
                    { Separated = true, Closer = ";", SpansLines = true },
                new ImportForm("unit ", ImportShape.Module) { Declares = true }
            ],
            // `var` and `const` are in the name list and out of the scope list, for the reason X#'s
            // `local` is (#83): a `var Total: Integer;` introduces a name and opens nothing, and it
            // was left out entirely while one list answered both questions. What it names on the
            // lines under a bare `var` — the block form, where the keyword heads the section and the
            // names follow it — is still not read, because no modifier stands on those lines; the
            // one-line form is what this admits.
            DeclarationModifiers = [.. _delphiScopeModifiers, "var", "const"],
            ScopeModifiers = _delphiScopeModifiers,
            DeclarationKeywords = ["class", "record", "interface", "object"],
            DeclarationNamesFollowKeyword = true,
            // `TCustomer = class(TBase)` is how a Delphi type is declared; `class TCustomer` is not
            // written here at all, and the shared shape found neither.
            TypeNamesPrecedeKeyword = true,
            CaseInsensitiveKeywords = true,
            // A unit announces its routines in `interface` and writes them in `implementation`, and
            // the two lines are the only thing in the file that says which half a `procedure Foo;` is
            // in. Before the first of them — the `unit Foo;` line and the uses clause — neither role
            // is claimed.
            SectionMarkers =
            [
                new SectionMarker("interface", DeclarationRole.Declaration),
                new SectionMarker("implementation", DeclarationRole.Implementation)
            ]
        },
        new LanguageProfile("HTML", ["html", "htm"])
        {
            BlockComments = [new StringDelimiter("<!--", "-->", StringEscape.None)],
            // An attribute value may hold a newline and is still not marked as spanning one, for the
            // reason SQL's literal is not: an unclosed quote in markup is far likelier to be a typo,
            // a stray apostrophe or a tag this does not parse than a genuine multi-line value, and a
            // literal read as spanning takes every line below it with it.
            Strings = [_rawDouble, _rawSingle],
            AssignmentOperators = ["="],
            MemberAccessOperators = ["."],
            // The tag is the opener and the attribute is what is read out of it. A bare `src=` or
            // `href=` anywhere would make every `<a>` and every `<img>` in the document a
            // dependency, which is a great many edges that are not imports.
            ImportForms =
            [
                new ImportForm("<script", ImportShape.Path) { Anywhere = true, Attribute = "src" },
                new ImportForm("<link", ImportShape.Path) { Anywhere = true, Attribute = "href" }
            ],
            CaseInsensitiveKeywords = true
        },
        new LanguageProfile("CSS", ["css"])
        {
            // `/* */` is the only comment form CSS has; `//` is not one, whatever a preprocessor does.
            BlockComments = [_cBlockComment],
            Strings = [_doubleQuoted, _singleQuotedEscaped],
            // A declaration here is `property: value`, which is the nearest thing CSS has to a write.
            AssignmentOperators = [":"],
            // `@import "a.css"`, `@import url("a.css")` and `url(a.png)` are three spellings of one
            // thing, and each gets its own form. The middle one is a form and not a rule about
            // forms nesting inside each other: the longest opener is tried first, so it is read
            // before the `@import` it begins with, and no other language pays for CSS's shape.
            ImportForms =
            [
                new ImportForm("@import url(", ImportShape.Path) { Closer = ")" },
                new ImportForm("@import", ImportShape.Path),
                new ImportForm("url(", ImportShape.Path) { Anywhere = true, Closer = ")" }
            ],
            CaseInsensitiveKeywords = true
        },
        // `.sql` alone is SQL. PL/SQL is told apart by the package and program-unit extensions, which
        // is the only signal an extension carries: a `.sql` file holding a package body is counted as
        // SQL, because deciding otherwise would mean reading it.
        new LanguageProfile("SQL", ["sql"])
        {
            LineComments = ["--"],
            BlockComments = [_cBlockComment],
            // A SQL literal may legally hold a newline, and it is not marked as spanning one anyway.
            // A literal that spans is read as open until its closing quote, so one odd quote — in a
            // dialect this does not know, in a string this misreads — would turn the rest of the file
            // into a string. The multi-line literal is rare in this code and the cost of getting it
            // wrong is every line below it.
            Strings = [_singleQuoted],
            AssignmentOperators = ["="],
            MemberAccessOperators = ["."],
            DeclarationModifiers = _sqlModifiers,
            DeclarationNamesFollowKeyword = true,
            // A routine here opens its body with a word where the C family opens one with a bracket.
            DeclarationBodyOpeners = _sqlBodyOpeners,
            CaseInsensitiveKeywords = true,
            // A `.sql` script may hold an Oracle package spec, a body, or both, and the headers are
            // the only thing that says which. Without the split declared here, the half of this
            // dialect that is written into `.sql` files answers that it has no halves.
            SectionMarkers = _packageSections
        },
        new LanguageProfile("PL/SQL", ["pks", "pkb", "plsql", "prc", "fnc", "trg"])
        {
            LineComments = ["--"],
            BlockComments = [_cBlockComment],
            Strings = [_singleQuoted],
            AssignmentOperators = [":="],
            MemberAccessOperators = ["."],
            DeclarationModifiers = [.. _sqlModifiers, "package", "body", "cursor", "exception"],
            DeclarationNamesFollowKeyword = true,
            DeclarationBodyOpeners = _sqlBodyOpeners,
            CaseInsensitiveKeywords = true,
            // A package spec announces its routines and the body writes them, usually in two files,
            // which is why the marker is the header line and not the extension: neither file knows
            // about the other.
            SectionMarkers = _packageSections
        }
    ];

    private static ILanguageAnalyzer Fallback { get; } = new TextAnalyzer(_fallbackProfile);

    /// <summary>The set every caller resolves against unless it was handed another.</summary>
    public static LanguageRegistry Default { get; } =
        new([.. _registered.Select(profile => (ILanguageAnalyzer)new TextAnalyzer(profile))], Fallback);

    /// <summary>
    ///     The language this extension means, or null when no profile covers it. The extension is
    ///     spelled the way <c>files.extension</c> stores it — lowercase, no leading dot — and a leading
    ///     dot is tolerated so a caller quoting a file name does not have to strip it.
    /// </summary>
    public static string? Of(string extension) => Default.For(extension).Language;

    /// <summary>
    ///     The extension of a path as <c>files.extension</c> stores it: lowercase, without the dot.
    ///     Written once because the build derives it when it writes the column and every read derives
    ///     it again from the path, and two spellings of one rule are a rule that drifts — the same
    ///     reason <see cref="CodeExplorer.Reading.ProjectPaths" /> both parses and formats a qualified path.
    /// </summary>
    public static string ExtensionOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Path.GetExtension(path.AsSpan()).TrimStart('.').ToString().ToLowerInvariant();
    }

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
