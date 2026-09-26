using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeExplorer.Language;

/// <summary>
///     The <see cref="ILanguageAnalyzer" /> that answers from a <see cref="LanguageProfile" /> and the
///     line in front of it. There is no compiler and no symbol table here, which is why every answer
///     it gives is <see cref="Evidence.Text" /> and why CONTEXT.md calls a reference strong evidence
///     and never proof.
///     It stays a first-class implementation rather than a stopgap: a Roslyn analyser will one day
///     answer C# better and tree-sitter the C family, but X# has no grammar to be had, and a text
///     profile is the only thing that will ever read it.
///     .NET <see cref="Regex" /> appears here and only here, and only over a line DuckDB already
///     picked out, which is the division CODING_STANDARDS draws: the candidate set is chosen by the
///     engine, and this says what each candidate is. The declaration, assignment and generated-path
///     patterns are built per profile rather than declared with <c>[GeneratedRegex]</c>, because their
///     alternations come from the profile's own keyword lists; one analyser is built per language at
///     startup and the cost is paid once. The shapes no profile changes are source-generated below.
/// </summary>
public sealed partial class TextAnalyzer : ILanguageAnalyzer
{
    /// <summary>
    ///     An RE2-legal pattern that matches nothing, for a profile that declares no modifiers or no
    ///     type keywords. An empty alternation would match the empty string — every line — which is
    ///     the opposite of what "this language declares nothing I can read" means. It is never built
    ///     as a .NET regex: <see cref="PatternOrNull" /> reads it as "no pattern" and the candidate
    ///     predicate leaves it out.
    /// </summary>
    private const string _matchesNothing = @"[^\s\S]";

    /// <summary>
    ///     The compound assignments, which are the same list wherever a language has any of them and
    ///     are therefore not a profile fact. Written without the trailing <c>=</c>, which is added
    ///     once below.
    /// </summary>
    private static readonly string[] _compoundOperators =
        ["+", "-", "*", "/", "%", "|", "&", "^", "??", "<<", ">>"];

    /// <summary>
    ///     How deeply comments, literals and the interpolation holes inside them may nest before the
    ///     scan stops claiming to know where it is. A literal and its hole are two, so eight is four
    ///     literals inside each other's holes — past anything a person writes, and the point at which
    ///     a text scan with no grammar behind it should stop being believed. Past it the rest of the
    ///     file is <see cref="Lexical.Unknown" />, which is the answer that keeps a reference rather
    ///     than placing it wrongly.
    /// </summary>
    private const int _maxNesting = 8;

    /// <summary>
    ///     How deeply a type argument list may nest before the member shape stops reading it. A
    ///     regular expression cannot balance brackets and RE2 has no recursion to fake it with, so the
    ///     nesting is written out to a bound. Three is <c>Dictionary&lt;string, List&lt;Foo&lt;int&gt;&gt;&gt;</c>,
    ///     past what these codebases write, and a type nested deeper is a declaration this does not
    ///     find rather than one reported wrongly.
    /// </summary>
    private const int _maxTypeArgumentDepth = 3;

    // Each null where the profile gives the shape nothing to match, rather than a compiled pattern
    // that matches nothing and is still run on every candidate line.
    private readonly Regex? _assignment;
    private readonly Func<string, CandidateLines> _declarationCandidatesFor;
    private readonly Regex? _generated;
    private readonly StringComparison _keywordComparison;
    private readonly Regex? _keywordDeclaration;
    private readonly Regex? _memberDeclaration;
    private readonly LanguageProfile _profile;
    private readonly Regex? _precedingTypeDeclaration;
    private readonly Regex? _typeDeclaration;

    // The profile's lists as arrays. <see cref="Scan" /> walks them once per character of every line
    // examined, and an IReadOnlyList<string> there is an interface dispatch per opener per character —
    // which on a minified bundle, where the whole file is one line, is most of the time spent.
    private readonly string[] _declarationModifiers;

    /// <summary>
    ///     The modifiers that introduce a name and open no scope: what
    ///     <see cref="LanguageProfile.DeclarationModifiers" /> holds and
    ///     <see cref="LanguageProfile.ScopeModifiers" /> does not. Empty where the profile named one
    ///     list, which is the answer it gave before there were two and costs nothing to keep.
    ///     Kept as the difference rather than as the narrower list because that is what a line is read
    ///     against: one of these words in a declaration head says the line declares a constant, a field
    ///     or a variable whatever else stands beside it, and <c>private const string Pattern = "…";</c>
    ///     would otherwise open a scope on the strength of its <c>private</c>.
    /// </summary>
    private readonly string[] _nameOnlyModifiers;

    private readonly string[] _directivePrefixes;
    private readonly string[] _instantiationKeywords;
    private readonly string[] _lineComments;
    private readonly string[] _memberAccess;

    /// <summary>
    ///     Everything that, at the start of a line's text, makes the whole line a comment. Only the
    ///     line-start forms: a <c>//</c> or a <c>/*</c> there is found by the scan like any other,
    ///     while a bare <c>*</c> is a comment at the start of a line and a multiplication anywhere else.
    /// </summary>
    private readonly string[] _lineStartComments;

    /// <summary>
    ///     What moves the file from one side of the declaration/implementation split to the other,
    ///     longest phrase first so that <c>create package body</c> is not read as the
    ///     <c>create package</c> it begins with. Empty for every language without a split, which is
    ///     what makes this cost nothing there.
    /// </summary>
    private readonly SectionMarker[] _sectionMarkers;

    /// <summary>
    ///     How this language names the files it depends on, longest opener first so that
    ///     <c>global using </c> is tried before the <c>using </c> it begins with. The profile is free
    ///     to write them in whatever order reads best.
    /// </summary>
    private readonly ImportForm[] _importForms;

    /// <summary>
    ///     Which of <see cref="_importForms" /> may run past the end of its line, or -1 where none
    ///     does — which is every language here but Delphi. It is one and not a set because carrying
    ///     two open clauses at once is a shape no language writes, and a bool on the position is what
    ///     a walk can afford where a stack of them is not.
    /// </summary>
    private readonly int _spanningImport;

    /// <summary>
    ///     Everything a line can be inside: the block comments and the string literals, in one table
    ///     because the scan does one thing with them — find the opener, then look for the closer. What
    ///     tells them apart is <see cref="Delimited.Prose" />, which is what the state on the far side
    ///     of the seam is called, and it is read in one place.
    ///     Longest opener first, so that a form which begins with another — C#'s <c>"""</c> against
    ///     its <c>"</c> — is tried before the one it contains and a raw literal is never read as an
    ///     empty one. The profile is free to list its forms in whatever order reads best.
    /// </summary>
    private readonly Delimited[] _forms;

    private readonly string[] _typePrefixes;

    /// <summary>
    ///     The characters that can begin anything <see cref="LineCursor" /> cares about in code. The
    ///     scan jumps between them instead of testing every character against every delimiter:
    ///     ordinary code is nearly all characters that start nothing, and skipping those runs is what
    ///     keeps a one-line minified bundle from costing a delimiter probe per character per match on it.
    /// </summary>
    private readonly SearchValues<char> _codeJump;

    /// <summary>
    ///     The top of a file, and the one every line outside anything ends at. One instance, because a
    ///     file of ordinary code would otherwise allocate a position per line to say nothing changed.
    /// </summary>
    private readonly TextPosition _start;

    public TextAnalyzer(LanguageProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _lineComments = [.. profile.LineComments];
        _directivePrefixes = [.. profile.DirectivePrefixes];
        _lineStartComments = [.. profile.LineStartComments];
        // Longest opener first, so `"""` is tried before `"` and `(*` before a `(` some profile may
        // one day add. Ordering here rather than in the table keeps a profile from having a silent
        // correctness rule in the order its forms are written.
        var forms = profile.BlockComments.Select(comment => (Form: comment, Prose: true))
            .Concat(profile.Strings.Select(literal => (Form: literal, Prose: false)))
            .OrderByDescending(entry => entry.Form.Open.Length)
            .ToArray();
        _instantiationKeywords = [.. profile.InstantiationKeywords];
        _memberAccess = [.. profile.MemberAccessOperators];
        _typePrefixes = [.. profile.TypePrefixOperators];
        _importForms = [.. profile.ImportForms.OrderByDescending(form => form.Opener.Length)];
        _spanningImport = Array.FindIndex(_importForms, form => form.SpansLines);
        _declarationModifiers = [.. profile.DeclarationModifiers];
        // A word in ScopeModifiers that is not a declaration modifier at all names no shape and is
        // therefore nothing this can read: the narrower list filters the wider one and never extends
        // it, which is what keeps the two from being two ways to add a keyword.
        _nameOnlyModifiers = profile.ScopeModifiers.Count == 0
            ? []
            : [
                .. profile.DeclarationModifiers.Where(modifier =>
                    // Under the profile's own case rule like every other keyword comparison here: the
                    // two lists are written in one file, but a language that shouts its keywords may
                    // shout one of them and not the other.
                    !profile.ScopeModifiers.Contains(modifier, profile.CaseInsensitiveKeywords
                        ? StringComparer.OrdinalIgnoreCase
                        : StringComparer.Ordinal))
            ];
        _sectionMarkers = [.. profile.SectionMarkers.OrderByDescending(marker => marker.Phrase.Length)];
        char[] opensInCode =
        [
            .. _lineComments.Concat(forms.Select(entry => entry.Form.Open)).Select(o => o[0]).Distinct()
        ];
        _codeJump = SearchValues.Create(opensInCode);
        _forms =
        [
            .. forms.Select(entry =>
            {
                var (form, prose) = entry;
                var ends = new List<char> { form.Close[0] };
                if (form.Escape == StringEscape.Backslash) ends.Add('\\');
                if (form.Hole is { } hole) ends.Add(hole.Open[0]);
                // Inside a hole everything that matters in code matters too, and the braces that end
                // it or nest inside it as well.
                var inside = new List<char>(opensInCode);
                if (form.Hole is { } holes) inside.AddRange([holes.Close[0], holes.Nest[0]]);
                // A comment carries to the next line whatever its profile said, because a block
                // comment that stopped at the end of its line would not be a block comment.
                return new Delimited(form, prose, prose || form.SpansLines,
                    SearchValues.Create(ends.ToArray()), SearchValues.Create(inside.ToArray()));
            })
        ];
        _start = new TextPosition(this, [], null);

        _keywordComparison = profile.CaseInsensitiveKeywords
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string flag = profile.CaseInsensitiveKeywords ? "(?i)" : "";
        string? modifiers = Alternation(profile.DeclarationModifiers);
        // The C-family shape: modifiers, a return type, the name, then what opens a body — or what
        // ends the line, because a field is a member and a member is a declaration (CONTEXT.md,
        // Declaration). Without the `;` the class ended at `=`, so the same field appeared or
        // disappeared according to whether it had been given an initialiser: `private const string
        // Pattern = "…";` was a declaration and `public int Count;` was not. The keyword shape beside
        // this one has always closed on `;` and was the half that was right.
        //
        // What keeps a statement out is the modifiers, which are required and which no statement
        // carries: `return count;` and `var total = 0;` match nothing here, `;` or no `;`.
        //
        // What the profile says is never a type refuses the line: TypeScript's `export default
        // thing;` is modifier-word-word-`;` — this shape exactly — while it names a binding declared
        // elsewhere. The word comes from `NonTypeKeywords` and is not written here, for the reason
        // `new` is not (ADR-0008): a keyword welded into the shared pattern is a language fact the
        // profile author cannot see.
        //
        // The refusal is a lookahead, so it lives only in the pattern .NET runs. The other is the
        // candidate predicate DuckDB runs, and RE2 has no lookaround (CODING_STANDARDS) — a
        // lookahead there is not a narrower filter but a query that throws. Leaving it out costs
        // nothing, because a candidate set is allowed to be wider than the answer: .NET classifies
        // every line it returns, which is the division the seam is built on.
        string? nonTypes = Alternation(profile.NonTypeKeywords);
        string typeGuard = nonTypes is null ? "" : $@"(?!(?:{nonTypes})\b)";

        // A type argument list, read as the bracketed group it is rather than as more characters of
        // the type. It was a character class holding `<` and `>` like any other letter, which meant
        // it could hold no space — and every formatter there is writes `Dictionary<string, int>`
        // with one, so a member typed that way was missed however it was written. It also meant the
        // `<` opening the list was one of the characters that could *close* the shape, so a generic
        // field was reported under its type's name.
        //
        // Nested to a fixed depth because a regular expression cannot balance brackets and RE2 has
        // no recursion to fake it with. Three is `Dictionary<string, List<Foo<int>>>` — past what
        // these codebases write — and a type nested deeper is a miss rather than a wrong answer,
        // which is the direction this module errs in everywhere (CONTEXT.md, Declaration).
        // A character of a name, as IsWordChar defines one. Not \w: these patterns also go to DuckDB as
        // the candidate predicate, where RE2's \w is ASCII-only, so `METHOD Größe()` was never a
        // candidate and the member was never declared (#235). .NET reads the class the same way.
        const string word = SymbolText.Re2WordChar;

        string arguments = "<[^<>]*>";
        for (int depth = 1; depth < _maxTypeArgumentDepth; depth++) arguments = $"<(?:[^<>]|{arguments})*>";

        // A name, the argument list where there is one, and the markers that ride after it: `?` for
        // a nullable, `[]` for an array, and both together.
        string type = $@"[{SymbolText.Re2WordClass}\.]+(?:{arguments})?[\[\]\?]*";

        // Every shape below takes its name slot as an argument, because two callers fill it: the
        // patterns .NET runs capture whatever word stands there and report it, and the candidate
        // predicate for one symbol spells that symbol there (#239). Without the second, a line shaped
        // like a declaration counted as a candidate whenever it mentioned the name anywhere — `public
        // void Run(OrderService s)` for `OrderService` — and a thousand of those sorting first used
        // up the candidate cap before the real declaration was read.
        string captured = $"({word}+)";

        string MemberPattern(string guard, string name) =>
            modifiers is null
                ? _matchesNothing
                : $@"^\s*(?:\[[^\]]*\]\s*)*(?:(?:{modifiers})\s+)+{guard}{type}\s+{name}\s*[\(<{{=;]";

        // The wider of the two, and the one published as a candidate predicate.
        string memberPattern = MemberPattern("", captured);
        string memberDeclarationPattern = MemberPattern(typeGuard, captured);
        // The xBase, Delphi and SQL shape: the introducing word and then the name, with the return
        // type — where there is one — after it rather than before. The name may be qualified, which
        // is how every language that splits declaration from implementation writes the second half:
        // `procedure TCustomer.Save;` and `create package body app.orders` name the type they belong
        // to, and reading only as far as the dot found no declaration on the line at all.
        // What opens the body comes from the profile where it is a word, because that is a language
        // fact; the brackets beside it are the shape of a declaration head in every language that has
        // one.
        string openers = Alternation(profile.DeclarationBodyOpeners) is { } words
            ? $@"|\s(?:{words})\b"
            : "";
        // The head is the name and the qualifier in front of it, because either is what the line
        // declares: `procedure TCustomer.Save;` is a site for `Save` and for `TCustomer`.
        string KeywordPattern(string head) =>
            modifiers is null || !profile.DeclarationNamesFollowKeyword
                ? _matchesNothing
                : $@"^\s*(?:(?:{modifiers})\s+)+{head}\s*(?:[\(<{{=;:]{openers})";
        string keywordPattern = KeywordPattern($@"(?:{captured}\s*\.\s*)?{captured}");
        string? typeKeywords = Alternation(profile.DeclarationKeywords);
        string TypePattern(string name) =>
            typeKeywords is null
                ? _matchesNothing
                : $@"^\s*(?:\[[^\]]*\]\s*)*(?:{word}+\s+)*\b(?:{typeKeywords})\s+{name}";
        string typePattern = TypePattern(captured);
        // Delphi's `TCustomer = class(TBase)`, where the name is in front of the word that says what
        // kind of thing it is. Written out as its own shape rather than folded into the one above: an
        // alternation covering both would match a line that is neither.
        string PrecedingTypePattern(string name) =>
            typeKeywords is null || !profile.TypeNamesPrecedeKeyword
                ? _matchesNothing
                : $@"^\s*{name}\s*=\s*(?:{typeKeywords})\b";
        string precedingTypePattern = PrecedingTypePattern(captured);

        _memberDeclaration = PatternOrNull(flag, memberDeclarationPattern);
        _keywordDeclaration = PatternOrNull(flag, keywordPattern);
        _typeDeclaration = PatternOrNull(flag, typePattern);
        _precedingTypeDeclaration = PatternOrNull(flag, precedingTypePattern);
        // Only the shapes this language actually writes. A language that declares nothing this can
        // read asks the engine for no lines at all, rather than for the lines a pattern that matches
        // nothing would return.
        CandidateLines Candidates(params string[] patterns)
        {
            string[] shapes = [.. patterns.Where(p => p != _matchesNothing)];
            return shapes.Length == 0
                ? CandidateLines.None
                : CandidateLines.Matching($"{flag}{string.Join("|", shapes.Select(p => $"(?:{p})"))}");
        }

        DeclarationCandidates = Candidates(memberPattern, keywordPattern, typePattern, precedingTypePattern);
        // The same four shapes with the symbol where the captured name was. A line the capturing
        // shapes read as declaring the symbol matches here at the same place, so this loses no
        // declaration; it is still a prefilter, and Declares stays the final word.
        _declarationCandidatesFor = symbol =>
        {
            string literal = SymbolText.Re2Literal(symbol);
            return Candidates(
                MemberPattern("", literal),
                KeywordPattern($@"(?:{literal}\s*\.\s*{word}+|(?:{word}+\s*\.\s*)?{literal})"),
                // The one shape with nothing after the name, so it needs the boundary the greedy
                // capture gave it: `class OrderServiceX` declares no `OrderService`.
                TypePattern(literal + SymbolText.Re2WordEnd),
                PrecedingTypePattern(literal));
        };

        _assignment = PatternOrNull("", AssignmentPattern(profile.AssignmentOperators));
        // Not compiled: nothing in production asks it, and a test asking a handful of paths does not
        // earn the cost of emitting one.
        _generated = profile.GeneratedPathPatterns.Count == 0
            ? null
            : new Regex(
                "^(?:" + string.Join("|", profile.GeneratedPathPatterns.Select(GlobToPattern)) + ")$",
                // A path is compared without regard to case, the way a file system does.
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    /// <summary>
    ///     How every per-profile pattern here but the generated-path one is built.
    ///     <see cref="RegexOptions.Compiled" /> because
    ///     these run against every line of every file a build ingests and every candidate line a
    ///     reference or definition search reads, which is the one place in this system where a regex is
    ///     hot. Measured on this machine, over the ten analysers (#149): building all of them went from
    ///     25 ms to 29 ms, and a scan of 120,000 lines from 244 ms to 110 ms. Four milliseconds once,
    ///     for a bit over twice the speed on every line after — and the once is the first touch of
    ///     <see cref="Languages.Default" />, which is a lazy static, so a replica waking up pays it on
    ///     the first request that reads a line rather than at startup. Leaving uncompiled the patterns
    ///     that cannot match, and source-generating the shared ones (#177), took that first touch from
    ///     40 ms to 26 ms and the 120,000-line scan from 110 ms to 92 ms.
    ///     These pattern strings are built per profile and the analysers are process-wide singletons,
    ///     so a source generator cannot build them; the shapes that are the same for every profile are
    ///     the <c>[GeneratedRegex]</c> methods below.
    /// </summary>
    private const RegexOptions _patternOptions = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>
    ///     The pattern built and compiled, or null where the profile gave it nothing to match. A shape
    ///     this language does not write costs neither the compile nor a run per candidate line.
    /// </summary>
    private static Regex? PatternOrNull(string flag, string pattern) =>
        pattern == _matchesNothing ? null : new Regex(flag + pattern, _patternOptions);

    /// <summary>
    ///     Everything that may legally sit between the start of a declaration and its name. Shared by
    ///     every profile: it describes the shape of a declaration head, not any language's punctuation.
    /// </summary>
    [GeneratedRegex(@"^[\s\w<>,\[\]\?\.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationPrefix();

    /// <summary>"Symbol x", "Symbol? x", "Symbol[] x", "Symbol&lt;T&gt; x" — a type followed by the thing it types.</summary>
    [GeneratedRegex(@"^(?:\??(?:\[\])?|<[^<>]*>)\s+\w", RegexOptions.CultureInvariant)]
    private static partial Regex TypedDeclarationTail();

    /// <summary>Call parentheses, with an optional generic argument list in front of them.</summary>
    [GeneratedRegex(@"^\s*(?:<[^<>()]*>)?\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex Invocation();

    /// <summary>
    ///     A declaration head is followed by a parameter list, a generic list, a property body or an
    ///     initialiser — never by an operator or the end of an expression.
    /// </summary>
    [GeneratedRegex(@"^\s*(?:[\(<{;=]|=>)", RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationTail();

    public string? Language => _profile.Name;

    public IReadOnlyList<string> Extensions => _profile.Extensions;

    public bool SeparatesDeclarationFromImplementation => _profile.SeparatesDeclarationFromImplementation;

    public CandidateLines DeclarationCandidates { get; }

    public CandidateLines DeclarationCandidatesFor(string symbol) => _declarationCandidatesFor(symbol);

    public FilePosition Start => _start;

    public FilePosition After(FilePosition position, string line)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(line);
        Span<Frame> frames = stackalloc Frame[_maxNesting];
        var cursor = new LineCursor(this, position, line, frames);
        // The whole line, because what it leaves open is decided by its last character and not by its
        // last match.
        cursor.StateAt(line.Length);
        return cursor.Position();
    }

    public Answer<Lexical> StateAt(FilePosition position, string line, int index)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(line);
        Span<Frame> frames = stackalloc Frame[_maxNesting];
        var cursor = new LineCursor(this, position, line, frames);
        // A position off the end of the line is answered with the state the line ends in rather than
        // with a default, because the default would be `Code` — the one claim that must never be a
        // guess, and the reason `Unknown` exists at all.
        return new Answer<Lexical>(cursor.StateAt(Math.Clamp(index, 0, line.Length)), Evidence.Text);
    }

    /// <summary>
    ///     One delimited form — a block comment or a string literal — and everything the scan needs to
    ///     walk it. A comment is a literal the scan never looks for an escape or a hole in, which is
    ///     what makes the two one record and one loop rather than two of each.
    /// </summary>
    /// <param name="Form">The pair as the profile wrote it.</param>
    /// <param name="Prose">Whether what is inside it is a comment rather than a string.</param>
    /// <param name="Spans">Whether an unclosed one carries on onto the next line.</param>
    /// <param name="Ends">What can end or interrupt it from inside: its closer, an escape, a hole.</param>
    /// <param name="InsideAHole">What matters in the live code of one of its holes.</param>
    private sealed record Delimited(
        StringDelimiter Form,
        bool Prose,
        bool Spans,
        SearchValues<char> Ends,
        SearchValues<char> InsideAHole);

    /// <summary>
    ///     One span the scan is inside. <see cref="Index" /> points into <see cref="_forms" /> — for a
    ///     hole, at the literal the hole was opened in, which is what says how the hole ends.
    ///     <see cref="Depth" /> counts the braces nested inside a hole, so that the <c>}</c> of a
    ///     collection expression in it does not end it.
    ///     <see cref="Extra" /> is how many more of its closer's last character a literal whose form
    ///     <see cref="StringDelimiter.OpenerRepeats" /> needs before it closes.
    /// </summary>
    private readonly record struct Frame(int Index, int Depth, bool Hole, int Extra);

    /// <summary>
    ///     What earlier lines of one file left open, as this analyser records it. It names the analyser
    ///     that made it because the frames index that analyser's own tables: one handed to another
    ///     language would point at whatever happens to sit at those indexes, so it is read as
    ///     <see cref="FilePosition.Unknown" /> instead.
    ///     <see cref="Section" /> is the other thing earlier lines decided: which side of the
    ///     declaration/implementation split the file is on — a Delphi unit's <c>interface</c> against
    ///     its <c>implementation</c>, a PL/SQL package spec against its body — and null until a marker
    ///     has said.
    ///     <see cref="OpenClause" /> is the third: whether an import clause that runs past the end of
    ///     its line is still open — Delphi's <c>uses</c>, which lists its units over as many lines as
    ///     it likes until a <c>;</c>. It is what makes the second line of one an import rather than a
    ///     list of bare identifiers, and it is on the position rather than in the extractor because
    ///     the shape of the clause is Delphi's fact and not its reader's (ADR-0008).
    /// </summary>
    private sealed record TextPosition(
        TextAnalyzer Owner, Frame[] Frames, DeclarationRole? Section, bool OpenClause = false)
        : FilePosition;

    /// <summary>
    ///     One left-to-right pass of a line, handing out the lexical state at each position asked
    ///     about in turn, and the position the file stands at once the line is done. It is a cursor
    ///     and not a function of the position because the positions arrive in ascending order: asked
    ///     as a function it re-walked the line from the start every time, which on a minified bundle —
    ///     one line of several million characters, well inside <c>Index:MaxFileBytes</c> — cost a walk
    ///     per match rather than a walk per line.
    ///     It lives here rather than on the analyser because an analyser answers every search at once
    ///     and can hold no per-file state of its own. The state it does hold arrives as a
    ///     <see cref="TextPosition" /> and leaves as one, so the walk of a file is the caller's loop
    ///     and the caller decides how much of the file it can afford to walk.
    ///     The open spans live in a buffer the caller stacks, so nothing is allocated per position and
    ///     nothing per line either, except where a line ends inside a comment or a literal.
    /// </summary>
    private ref struct LineCursor
    {
        private readonly TextAnalyzer _analyzer;
        private readonly string _line;
        private readonly Span<Frame> _frames;

        /// <summary>What the carried position was, to hand back unchanged where the line changes nothing.</summary>
        private readonly TextPosition _carried;

        private int _depth;
        private int _at;
        private bool _unknown;

        /// <summary>
        ///     Which side of the declaration/implementation split the file is on once this line has
        ///     been read. A marker takes effect on the line it stands on, because the header of a
        ///     package body is the first line of the body.
        /// </summary>
        private DeclarationRole? _section;

        /// <summary>
        ///     The rest of this line is a comment: a line comment opened on it, or one of the forms
        ///     that means a comment only at the start of a line did. Neither carries to the next line,
        ///     which is why both are this one flag and not two.
        /// </summary>
        private bool _lineCommented;

        /// <summary>
        ///     Where this line stops being code: the first position a comment opens at, or the length
        ///     of the line. Recorded by the walk as it passes rather than found by asking the cursor
        ///     per character afterwards — on a minified bundle, one line of several million characters
        ///     well inside <c>Index:MaxFileBytes</c>, the second form cost a call per character.
        ///     Only meaningful once the whole line has been read, which is what <see cref="Position" />
        ///     guarantees and what <see cref="CommentOpensAt" /> is read after.
        /// </summary>
        private int _commentOpensAt;

        public readonly int CommentOpensAt => Math.Min(_commentOpensAt, _line.Length);

        public LineCursor(TextAnalyzer analyzer, FilePosition position, string line, Span<Frame> frames)
        {
            _analyzer = analyzer;
            _line = line;
            _frames = frames;
            // A position from another analyser indexes tables this one does not have, and one nested
            // deeper than the buffer cannot be carried whole. Either is "I do not know", which is an
            // answer; reading it as code would be a guess.
            if (position is not TextPosition carried || carried.Owner != analyzer
                                                    || carried.Frames.Length > frames.Length)
            {
                _unknown = true;
                _carried = analyzer._start;
                return;
            }

            _carried = carried;
            _section = carried.Section;
            carried.Frames.CopyTo(frames);
            _depth = carried.Frames.Length;
            int start = FirstNonSpace(line);
            // Only where the line begins outside everything: a `*` inside an open block comment is
            // the comment's own continuation marker and decides nothing.
            _lineCommented = _depth == 0 && start < line.Length && analyzer.OpensAWholeLine(line, start);
            // For the same reason: the word `implementation` inside a comment opened further up moves
            // nothing, and a file that read it as a marker would report every routine below it as an
            // implementation.
            if (analyzer._sectionMarkers.Length > 0 && _depth == 0 && !_lineCommented
                && analyzer.MarkerOn(line) is { } entered)
                _section = entered;
            _commentOpensAt = _lineCommented ? start : int.MaxValue;
            _at = start;
        }

        public Lexical StateAt(int index)
        {
            if (_unknown) return Lexical.Unknown;
            Advance(index);
            if (_unknown) return Lexical.Unknown;
            if (_lineCommented) return Lexical.Comment;
            if (_depth == 0) return Lexical.Code;
            var frame = _frames[_depth - 1];
            // A hole is live code that happens to sit inside a literal: `${advance(1)}` calls
            // something, and reading it as a mention loses a real call.
            if (frame.Hole) return Lexical.Code;
            return _analyzer._forms[frame.Index].Prose ? Lexical.Comment : Lexical.Literal;
        }

        /// <summary>
        ///     Where the file stands after this line — which is only meaningful once the whole line
        ///     has been read, so callers ask <see cref="StateAt" /> for its end first.
        /// </summary>
        public FilePosition Position()
        {
            if (_unknown) return FilePosition.Unknown;
            // What carries is the run of spans from the outermost in that all carry. A literal that
            // cannot span lines and was not closed on this one is an unterminated literal — a typo, a
            // stray quote in prose the profile does not read as prose — and not a claim about the next
            // line; it is dropped, and so is everything opened inside it, whose own place depended on
            // it. Cutting at the lowest one rather than unwinding from the top says that once, at
            // every depth.
            int depth = 0;
            while (depth < _depth && _analyzer._forms[_frames[depth].Index].Spans) depth++;
            // Asked here rather than in the constructor, so that it is asked once the line has been
            // read — the walk is what says where prose begins on it — and so that it is not asked at
            // all by a caller that only wants the state at a position. `Occurrences` and `StateAt`
            // build a cursor per line too, and neither has any use for a clause.
            // The depth the line BEGAN at, which is what the carried position holds: a `uses` written
            // inside a block comment opened further up opens nothing, and a file that read it as a
            // clause would report the next twenty lines as imports.
            bool openClause = _analyzer._spanningImport >= 0 && _carried.Frames.Length == 0
                              && !_lineCommented
                              && _analyzer.ClauseAfter(_carried.OpenClause, _line, FirstNonSpace(_line),
                                  CommentOpensAt);
            // A line inside a long block comment or a license header leaves the file exactly where it
            // found it, and there are a thousand such lines in a row. Handing the carried position
            // back is what keeps those from allocating a copy apiece to say nothing changed.
            if (Unchanged(depth, openClause)) return _carried;
            if (depth == 0 && _section is null && !openClause) return _analyzer._start;
            return new TextPosition(_analyzer, depth == 0 ? [] : _frames[..depth].ToArray(), _section,
                openClause);
        }

        /// <summary>Whether what carries is what this line began with, section and open spans alike.</summary>
        private readonly bool Unchanged(int depth, bool openClause) =>
            _section == _carried.Section && openClause == _carried.OpenClause
            && depth == _carried.Frames.Length && _frames[..depth].SequenceEqual(_carried.Frames);

        /// <summary>
        ///     Walks from wherever the last question left off to this one. A skip over an escape or a
        ///     closing delimiter can carry <c>_at</c> a character or two past the position asked
        ///     about; the state is the same either side of one, because no identifier begins inside a
        ///     quote or an escape, so the overshoot is left rather than backtracked.
        /// </summary>
        private void Advance(int index)
        {
            while (_at < index && !_lineCommented && !_unknown)
            {
                if (_depth == 0)
                {
                    InCode(index, -1);
                    continue;
                }

                var frame = _frames[_depth - 1];
                if (frame.Hole) InCode(index, frame.Index);
                else InDelimited(index, frame);
            }
        }

        /// <summary>
        ///     Live code: outside everything, or inside an interpolation hole, which is the same thing
        ///     with two more characters to watch for. <paramref name="holeIn" /> is the literal the
        ///     hole was opened in, or -1 outside one.
        /// </summary>
        private void InCode(int index, int holeIn)
        {
            // Jump to the next character that could begin a comment or a literal. Everything between
            // is ordinary code and needs no decision.
            int next = _line.AsSpan(_at, index - _at)
                .IndexOfAny(holeIn < 0 ? _analyzer._codeJump : _analyzer._forms[holeIn].InsideAHole);
            if (next < 0)
            {
                _at = index;
                return;
            }

            _at += next;

            if (holeIn < 0)
            {
                // A comment opener outside a literal takes the rest of the line. Inside one it is
                // text: a URL in a string would otherwise turn everything after it into prose. Inside
                // a hole it is neither — no language lets a line comment close an interpolation.
                for (int c = 0; c < _analyzer._lineComments.Length; c++)
                    if (At(_line, _at, _analyzer._lineComments[c]))
                    {
                        _lineCommented = true;
                        if (_at < _commentOpensAt) _commentOpensAt = _at;
                        return;
                    }
            }
            else if (CloseOrNestTheHole())
            {
                return;
            }

            int opened = _analyzer.OpenerAt(_line, _at);
            if (opened >= 0)
            {
                var form = _analyzer._forms[opened].Form;
                int extra = form.OpenerRepeats ? RunOf(_line, _at + form.Open.Length, form.Open[^1]) : 0;
                Push(opened, false, extra);
                _at += form.Open.Length + extra;
                return;
            }

            _at++;
        }

        /// <summary>
        ///     Whether the hole ended or nested here. The braces of a collection expression inside a
        ///     hole are counted rather than acted on, so the first <c>}</c> of <c>{new[]{1}}</c> does
        ///     not put the scan back into the literal a character early.
        /// </summary>
        private bool CloseOrNestTheHole()
        {
            var frame = _frames[_depth - 1];
            var hole = _analyzer._forms[frame.Index].Form.Hole!;
            if (At(_line, _at, hole.Close))
            {
                if (frame.Depth == 0) _depth--;
                else _frames[_depth - 1] = frame with { Depth = frame.Depth - 1 };
                _at += hole.Close.Length;
                return true;
            }

            if (!At(_line, _at, hole.Nest)) return false;
            _frames[_depth - 1] = frame with { Depth = frame.Depth + 1 };
            _at += hole.Nest.Length;
            return true;
        }

        /// <summary>
        ///     Inside a block comment or a string literal: look for the closer, and for the escape and
        ///     the hole the form has where it has them, which a comment never does.
        /// </summary>
        private void InDelimited(int index, Frame frame)
        {
            var open = _analyzer._forms[frame.Index].Form;
            int found = _line.AsSpan(_at, index - _at).IndexOfAny(_analyzer._forms[frame.Index].Ends);
            if (found < 0)
            {
                _at = index;
                return;
            }

            _at += found;

            if (open.Escape == StringEscape.Backslash && _line[_at] == '\\')
            {
                _at += 2;
                return;
            }

            if (open.Hole is { } hole && At(_line, _at, hole.Open))
            {
                // The opener doubled stands for itself and opens nothing, which is how C# writes a
                // literal brace inside an interpolated string.
                if (At(_line, _at + hole.Open.Length, hole.Open)) _at += 2 * hole.Open.Length;
                else
                {
                    Push(frame.Index, true, 0);
                    _at += hole.Open.Length;
                }

                return;
            }

            if (At(_line, _at, open.Close))
            {
                if (frame.Extra > 0)
                {
                    // A shorter run than the one that opened it is text (StringDelimiter.OpenerRepeats),
                    // and so is every run inside it, so the whole of it is passed at once.
                    int run = RunOf(_line, _at + open.Close.Length, open.Close[^1]);
                    if (run < frame.Extra)
                    {
                        _at += open.Close.Length + run;
                        return;
                    }
                }

                // A doubled delimiter stands for itself and does not close the literal.
                if (open.Escape == StringEscape.Doubled && At(_line, _at + open.Close.Length, open.Close))
                {
                    _at += 2 * open.Close.Length;
                    return;
                }

                _depth--;
                _at += open.Close.Length + frame.Extra;
                return;
            }

            _at++;
        }

        private void Push(int index, bool hole, int extra)
        {
            if (_depth == _frames.Length)
            {
                _unknown = true;
                return;
            }

            // Where the line stops being code, recorded as the walk passes it rather than found by a
            // second walk asking per character: a block comment opening here is prose from here on.
            if (!hole && _analyzer._forms[index].Prose && _at < _commentOpensAt) _commentOpensAt = _at;
            _frames[_depth++] = new Frame(index, 0, hole, extra);
        }
    }

    /// <summary>How many times this character stands in a row from <paramref name="from" /> on.</summary>
    private static int RunOf(string line, int from, char c)
    {
        int at = from;
        while (at < line.Length && line[at] == c) at++;
        return at - from;
    }

    /// <summary>
    ///     Which comment or literal opens here, as an index into <see cref="_forms" />, or -1. A
    ///     compiler directive that begins the way a comment does opens none — Delphi's <c>{$IFDEF}</c>
    ///     against its <c>{ }</c> comment — and the exemption is checked wherever the opener is found
    ///     and not only at the start of a line, because a directive is written after code too.
    ///     A char literal opens only where it closes in its own shape, so a stray apostrophe opens
    ///     nothing rather than taking the rest of the line (#295).
    /// </summary>
    private int OpenerAt(string line, int index)
    {
        // A loop rather than a LINQ predicate: this runs once per candidate character of every line.
        for (int i = 0; i < _forms.Length; i++)
        {
            var form = _forms[i].Form;
            if (!At(line, index, form.Open)) continue;
            if (form.HoldsOneCharacter && !ClosesAsOneCharacter(line, index + form.Open.Length, form.Close))
                continue;
            return _forms[i].Prose && IsDirectiveAt(line, index) ? -1 : i;
        }

        return -1;
    }

    /// <summary>
    ///     The longest run of hex digits an escape carries: C#'s <c>\U</c> takes eight, <c>\u</c> four
    ///     and <c>\x</c> up to four. The letter is not checked against the count — a literal this
    ///     accepts and the compiler would not is still a literal, and nothing it hides was code.
    /// </summary>
    private const int _maxEscapeDigits = 8;

    /// <summary>
    ///     Whether what starts at <paramref name="body" /> is one character or one backslash escape
    ///     and then <paramref name="close" />.
    /// </summary>
    private static bool ClosesAsOneCharacter(string line, int body, string close)
    {
        if (body >= line.Length || At(line, body, close)) return false;
        int at = body + 1;
        if (line[body] == '\\')
        {
            // The escaped character, which may be the closer itself, and then its digits if any.
            if (at >= line.Length) return false;
            at++;
            int digitsEnd = Math.Min(line.Length, at + _maxEscapeDigits);
            while (at < digitsEnd && char.IsAsciiHexDigit(line[at])) at++;
        }

        return At(line, at, close);
    }

    /// <summary>
    ///     Whether a compiler directive begins here. One copy, because the whole-line comment test and
    ///     the scan both have to make the same exemption and a profile adding a directive form must
    ///     not have to be right in two places.
    /// </summary>
    private bool IsDirectiveAt(string line, int index)
    {
        for (int i = 0; i < _directivePrefixes.Length; i++)
            if (At(line, index, _directivePrefixes[i], _keywordComparison))
                return true;
        return false;
    }

    /// <summary>Where the line's text begins, without allocating the trimmed copy to find out.</summary>
    private static int FirstNonSpace(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        return i;
    }

    public Answer<Declared?> Declares(FilePosition position, string line)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(line);
        string? type = TypeOn(line);
        string? member = null;
        bool opensScope = true;
        // The C-family shape first: where a language writes both, it is the more specific of the two
        // and the keyword shape would stop at the return type.
        if (_memberDeclaration?.Match(line) is { Success: true } m)
        {
            member = m.Groups[1].Value;
            opensScope = OpensScope(line, m.Groups[1].Index);
        }
        else if (_keywordDeclaration?.Match(line) is { Success: true } k)
        {
            member = k.Groups[2].Value;
            opensScope = OpensScope(line, k.Groups[2].Index);
            // `procedure TCustomer.Save;` names both, and the type it names is the one the member
            // belongs to — which is what a caller looking for the enclosing scope of a line in that
            // routine needs, since a Delphi implementation section nests nothing by indentation.
            if (type is null && k.Groups[1].Success) type = k.Groups[1].Value;
        }

        // A type holds members whatever decorates it, so a line that names one opens a scope even when
        // a name-only modifier shares it. TypeScript's `export const enum Direction {` is the case:
        // its `const` describes how the enum's values are emitted and does not make the enum a
        // constant, and reading it as one would stop every member inside from being labelled with it.
        if (type is not null) opensScope = true;

        return new Answer<Declared?>(
            type is null && member is null
                ? null
                : new Declared(type, member, RoleAt(position, line)) { OpensScope = opensScope },
            Evidence.Text);
    }

    /// <summary>
    ///     Whether the member declared at <paramref name="nameAt" /> opens a scope the lines below it
    ///     sit in, or only introduces a name (#83). Read off the head of the line — everything in front
    ///     of the name, which is where a language writes its modifiers — because one name-only modifier
    ///     decides it whatever accompanies it: <c>private const string Pattern = "…";</c> declares a
    ///     constant and not a scope, and a rule that asked whether <em>any</em> modifier opened one
    ///     would have answered it on its <c>private</c>.
    ///     A type declaration is not asked about. Every language here writes one as a thing that holds
    ///     members, and there is no modifier that makes it otherwise.
    /// </summary>
    private bool OpensScope(string line, int nameAt)
    {
        if (_nameOnlyModifiers.Length == 0) return true;
        var head = line.AsSpan(0, nameAt);
        for (int i = 0; i < _nameOnlyModifiers.Length; i++)
            if (SymbolText.ContainsWord(head, _nameOnlyModifiers[i], _keywordComparison))
                return false;
        return true;
    }

    /// <summary>
    ///     The type this line declares, in whichever of the two shapes the language writes — the
    ///     keyword first, or the name first. One method, because a caller that read only one of them
    ///     would place a Delphi class as a declaration for the scope map and as something else for the
    ///     counts.
    /// </summary>
    private string? TypeOn(string line)
    {
        if (_typeDeclaration?.Match(line) is { Success: true } named) return named.Groups[1].Value;
        return _precedingTypeDeclaration?.Match(line) is { Success: true } preceding
            ? preceding.Groups[1].Value
            : null;
    }

    /// <summary>
    ///     Which side of the declaration/implementation split this line sits on, or null where the
    ///     language has no split, where no marker has been passed yet, or where the caller did not walk
    ///     the file to here. Null and never <see cref="DeclarationRole.Declaration" />: an
    ///     implementation labelled a declaration is a guess reported as a fact, and the announcement
    ///     sorts first in an answer, so the guess would win.
    ///     A marker on this very line decides it, because the header of a package body is itself the
    ///     first declaration in the body.
    /// </summary>
    private DeclarationRole? RoleAt(FilePosition position, string line)
    {
        if (!_profile.SeparatesDeclarationFromImplementation) return null;
        if (MarkerOn(line) is { } opened) return opened;
        return Own(position)?.Section;
    }

    /// <summary>
    ///     The position as this analyser's own, or null where another made it or none did: its frames
    ///     index this analyser's tables and nobody else's.
    /// </summary>
    private TextPosition? Own(FilePosition position) =>
        position is TextPosition text && text.Owner == this ? text : null;

    /// <summary>
    ///     The section this line moves the file into, or null when it moves it nowhere. Read at the
    ///     start of the line's text, which is where every language that has these writes them.
    /// </summary>
    private DeclarationRole? MarkerOn(string line)
    {
        int start = FirstNonSpace(line);
        for (int i = 0; i < _sectionMarkers.Length; i++)
            if (PhraseAt(line, start, _sectionMarkers[i].Phrase))
                return _sectionMarkers[i].Role;
        return null;
    }

    /// <summary>
    ///     Whether this phrase stands here as whole words: one run of whitespace in the phrase matches
    ///     any run in the line, so <c>CREATE  OR REPLACE   PACKAGE BODY</c> is the phrase a formatter
    ///     left behind and not a miss, and the word after it must not run on — <c>implementations</c>
    ///     is not <c>implementation</c>.
    /// </summary>
    private bool PhraseAt(string line, int index, ReadOnlySpan<char> phrase)
    {
        int at = index;
        for (int i = 0; i < phrase.Length; i++)
        {
            if (char.IsWhiteSpace(phrase[i]))
            {
                if (at >= line.Length || !char.IsWhiteSpace(line[at])) return false;
                while (at < line.Length && char.IsWhiteSpace(line[at])) at++;
                while (i + 1 < phrase.Length && char.IsWhiteSpace(phrase[i + 1])) i++;
                continue;
            }

            if (at >= line.Length || !SameLetter(line[at], phrase[i])) return false;
            at++;
        }

        return at >= line.Length || !SymbolText.IsWordChar(line[at]);
    }

    public bool HasImports => _importForms.Length > 0;

    public ImportPathRules ImportPaths => _profile.ImportPaths;

    public Answer<ImportsOnLine> ImportsOn(FilePosition position, string line)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(line);
        return new Answer<ImportsOnLine>(Extract(position, line), Evidence.Text);
    }

    public Answer<bool> IsGenerated(string qualifiedPath)
    {
        ArgumentNullException.ThrowIfNull(qualifiedPath);
        return new Answer<bool>(_generated?.IsMatch(qualifiedPath) ?? false, Evidence.Text);
    }

    /// <summary>
    ///     Order matters: noise first, so a commented-out call is never counted as a call, and the
    ///     declaration before the call so that the line declaring a method is not also one calling it.
    /// </summary>
    public IReadOnlyList<Answer<ReferenceKind>> Occurrences(FilePosition position, string line, string symbol)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(symbol);

        var placed = new List<Answer<ReferenceKind>>();
        if (symbol.Length == 0) return placed;
        Span<Frame> frames = stackalloc Frame[_maxNesting];
        var cursor = new LineCursor(this, position, line, frames);

        // What the line is, asked once and only once an appearance is known to be code: every other
        // state is placed without it. Whether it is an import and what type it declares do not change
        // between one appearance of the symbol and the next, and the type regex alone cost
        // milliseconds per appearance on a long line when it was asked per appearance.
        bool lineClassified = false;
        bool import = false;
        string? typeDeclared = null;
        // Every appearance and not only the first: `return Foo.Create(Foo.Default)` is a type use and
        // a read, and reporting it as one of them loses the other.
        for (int at = SymbolText.IndexOf(line, symbol); at >= 0; at = SymbolText.IndexOf(line, symbol, at + 1))
        {
            var state = cursor.StateAt(at);
            if (state == Lexical.Code && !lineClassified)
            {
                lineClassified = true;
                // A line inside a clause that opened further up is an import line too, which is what
                // the build already read it as: the second line of a Delphi `uses` holds no opener.
                import = Own(position)?.OpenClause == true || IsImportLine(line);
                typeDeclared = TypeOn(line);
            }

            placed.Add(Place(line, at, symbol.Length, state, import, typeDeclared));
        }

        return placed;
    }

    /// <summary>
    ///     What one appearance looks like, given what the line already said about itself. Order
    ///     matters: noise first, so a commented-out call is never counted as a call, and the
    ///     declaration before the call so that the line declaring a method is not also one calling it.
    /// </summary>
    private Answer<ReferenceKind> Place(string line, int index, int length, Lexical state, bool import,
        string? typeDeclared)
    {
        if (state == Lexical.Comment) return Placed(ReferenceKind.Comment);
        if (state == Lexical.Literal) return Placed(ReferenceKind.StringLiteral);
        // Not known to be code, so nothing below may run: every shape under it — a call, a write, a
        // declaration — is a claim that this is live code, and that is the claim in doubt. Unplaced
        // keeps the reference and says the truth about it.
        if (state == Lexical.Unknown) return Placed(ReferenceKind.Other);
        if (import) return Placed(ReferenceKind.Import);

        // Spans and not substrings. A line naming a common identifier a thousand times would otherwise
        // allocate a thousand copies of the line either side of the match, and the lines this reads
        // are whatever the index holds — a minified bundle is one line of several million characters.
        var prefix = line.AsSpan(0, index);
        var suffix = line.AsSpan(index + length);
        var head = prefix.TrimEnd();

        bool afterReceiver = EndsWithAny(head, _memberAccess);
        bool invoked = Invocation().IsMatch(suffix);

        if (!afterReceiver && IsDeclaration(prefix, suffix, line.AsSpan(index, length), typeDeclared))
            return Placed(ReferenceKind.Definition);
        if (invoked && EndsWithKeyword(head, _instantiationKeywords)) return Placed(ReferenceKind.Instantiation);
        if (invoked) return Placed(ReferenceKind.Call);
        if (_assignment?.IsMatch(suffix) == true) return Placed(ReferenceKind.Write);
        // "Foo.Bar()" — Foo itself is a reference to the type, not a member access on something else.
        if (!afterReceiver && StartsWithAny(suffix, _memberAccess)) return Placed(ReferenceKind.TypeUse);
        if (afterReceiver) return Placed(ReferenceKind.MemberAccess);
        if (LooksLikeType(head, suffix)) return Placed(ReferenceKind.TypeUse);
        return Placed(ReferenceKind.Other);
    }

    /// <summary>
    ///     The alternation of these words for a regex, or null when there are none to match. Longest
    ///     first, so a multi-word form such as SQL's <c>or replace</c> is not cut short by a shorter
    ///     alternative that is a prefix of it.
    /// </summary>
    private static string? Alternation(IReadOnlyList<string> words) =>
        words.Count == 0
            ? null
            : string.Join("|", words.OrderByDescending(w => w.Length).Select(SymbolText.Re2Literal));

    /// <summary>
    ///     The identifier as the left-hand side of an assignment, plain or compound. The negative
    ///     lookahead is what keeps comparisons (<c>==</c>, <c>&gt;=</c>, <c>!=</c>) and lambda arrows
    ///     (<c>=&gt;</c>) out — those are reads, and conflating them is what makes a "who changes
    ///     this?" answer useless. Longest operator first, so <c>+=</c> is not read as a bare <c>=</c>.
    /// </summary>
    private static string AssignmentPattern(IReadOnlyList<string> assignments)
    {
        if (assignments.Count == 0) return _matchesNothing;
        var operators = _compoundOperators.Select(op => op + "=").Concat(assignments)
            .OrderByDescending(op => op.Length).Select(SymbolText.Re2Literal);
        return $@"^\s*(?:{string.Join("|", operators)})(?!=|>)";
    }

    /// <summary>
    ///     A path glob as a regex. <c>*</c> crosses <c>/</c>, the same way the search filters' globs
    ///     do, so an operator who has learned one syntax has learned both.
    /// </summary>
    private static string GlobToPattern(string glob)
    {
        var pattern = new StringBuilder();
        foreach (char c in glob)
            pattern.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => SymbolText.Re2Literal(c.ToString())
            });
        return pattern.ToString();
    }

    private static bool EndsWithAny(ReadOnlySpan<char> text, string[] candidates)
    {
        for (int i = 0; i < candidates.Length; i++)
            if (text.EndsWith(candidates[i], StringComparison.Ordinal))
                return true;
        return false;
    }

    private static bool StartsWithAny(ReadOnlySpan<char> text, string[] candidates)
    {
        for (int i = 0; i < candidates.Length; i++)
            if (text.StartsWith(candidates[i], StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    ///     Whether this line is an import line at all, without reading the names out of it — which is
    ///     what <see cref="ReferenceKind.Import" /> is decided by, and the reason it is a question
    ///     about the line rather than about a position on it.
    ///     A form is looked for at the start of the line's text whether or not it may also be written
    ///     mid-line: a <c>require(</c> standing there is what the line is, while one found inside an
    ///     expression is one call among several and does not make every name beside it a mention.
    /// </summary>
    private bool IsImportLine(string line)
    {
        int start = FirstNonSpace(line);
        for (int i = 0; i < _importForms.Length; i++)
            if (OpenerLength(line, start, _importForms[i]) >= 0)
                return true;
        return false;
    }

    /// <summary>
    ///     What this line imports and what it declares this file to be. The walk of the file decided
    ///     the two things this cannot see for itself — whether the line begins inside a comment, and
    ///     whether a clause from further up is still open — and both arrive on the position.
    /// </summary>
    private ImportsOnLine Extract(FilePosition position, string line)
    {
        if (_importForms.Length == 0) return ImportsOnLine.Nothing;
        var carried = Own(position);
        // A line that begins inside a block comment or inside a literal opened further up is not
        // code, and a `using` written in one imports nothing. A hole is never the outermost span, so
        // this one test covers both.
        if (carried?.Frames.Length > 0) return ImportsOnLine.Nothing;

        bool continuing = carried?.OpenClause == true;
        if (!continuing && !MayHoldAnImport(line, FirstNonSpace(line))) return ImportsOnLine.Nothing;

        // Prose cut away, so a `using` behind a `//` imports nothing and a trailing note is not read
        // as part of the clause. One walk of the line, paid only by the lines that already look like
        // they hold an import.
        string code = line[..CodeEnd(position, line)];
        var names = new List<ImportedName>();

        if (continuing)
        {
            // The middle or the end of a clause that began further up. Nothing else is read from such
            // a line: a `uses` list and a second import form on one line is not written.
            var spanning = _importForms[_spanningImport];
            Collect(names, ClauseOf(code, 0, spanning), spanning);
            return Line(names, null);
        }

        int start = FirstNonSpace(code);
        if (start >= code.Length || OpensAWholeLine(code, start)) return ImportsOnLine.Nothing;

        // The line's own shape first. A form standing at the start of a line is what that line is,
        // and a form found further along it belongs to this clause rather than sitting beside it.
        // Only the forms that must stand there: a `<script>` at the start of a line is one tag among
        // however many follow it on the same line, and returning on the first would lose the rest.
        foreach (var form in _importForms)
        {
            if (form.Anywhere) continue;
            int opener = OpenerLength(code, start, form);
            if (opener < 0) continue;
            string clause = ClauseOf(code, start + opener, form);
            if (form.Declares) return Line(names, Single(clause, form));
            Collect(names, clause, form);
            return Line(names, null);
        }

        // Otherwise every occurrence of a form that may be written mid-line: `require("./a")` in an
        // assignment, a `<script src>` among the rest of a tag.
        foreach (var form in _importForms)
        {
            if (!form.Anywhere) continue;
            for (int at = IndexOfOpener(code, start, form); at >= 0; at = IndexOfOpener(code, at + 1, form))
                Collect(names, ClauseOf(code, at + OpenerLength(code, at, form), form), form);
        }

        return Line(names, null);
    }

    private static ImportsOnLine Line(List<ImportedName> names, string? declares) =>
        names.Count == 0 && declares is null ? ImportsOnLine.Nothing : new ImportsOnLine(names, declares);

    /// <summary>
    ///     Whether this line could hold an import at all. The cheap test that keeps the careful one
    ///     off the lines that cannot: a build reads every line of every file, and all but a handful
    ///     per file hold no opener anywhere.
    ///     A form that must stand at the start of the line is tested there and nowhere else, which
    ///     costs the length of its opener in character compares rather than a scan of the line. That
    ///     is the whole of the test for C#, X# and Delphi, whose forms are all anchored; only the
    ///     handful that may be written mid-line — <c>require(</c>, <c>url(</c>, <c>&lt;script</c> —
    ///     are searched for along it.
    /// </summary>
    private bool MayHoldAnImport(string line, int start)
    {
        for (int i = 0; i < _importForms.Length; i++)
        {
            var form = _importForms[i];
            if (form.Anywhere
                    ? line.Contains(form.Head, _keywordComparison)
                    : OpenerLength(line, start, form) >= 0)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Where the live code on this line ends: the first position a comment opens at, or the end
    ///     of the line. A literal is not an end — the name in <c>import x from "./y"</c> is written
    ///     inside one — so only prose cuts it.
    /// </summary>
    private int CodeEnd(FilePosition position, string line)
    {
        Span<Frame> frames = stackalloc Frame[_maxNesting];
        // A position this analyser did not make — Unknown among them — is a caller asking about the
        // line alone, which ImportsOn promises to answer from the line, so the line is walked as
        // though nothing were open above it. That is ImportsOn's exception to ADR-0008's Unknown
        // rule, not the lexical answer: StateAt still says Unknown there. Walked from Unknown
        // instead, the cursor knew nowhere code ended and answered 0, and every import a build read
        // past the nesting bound came back empty (#240).
        var cursor = new LineCursor(this, Own(position) ?? _start, line, frames);
        // One walk of the line, and the answer read off it. Asked per character instead — which is
        // what this did first — a minified bundle cost a call per character of a line several
        // million characters long, for a question the walk answers on its way past.
        cursor.StateAt(line.Length);
        return cursor.CommentOpensAt;
    }

    /// <summary>
    ///     The names one clause yields, appended in the order written. A clause that quotes its name
    ///     is read for the quoted part and one that does not is read whole — asked in that order
    ///     rather than declared per form, because <c>url(a.png)</c> and <c>url("a.png")</c> are one
    ///     form written two ways and a flag saying which would have to be right about both.
    /// </summary>
    private static void Collect(List<ImportedName> names, string clause, ImportForm form)
    {
        if (form.Attribute is { } attribute)
        {
            if (AttributeValue(clause, attribute) is { } value) Add(names, value, form);
            return;
        }

        if (!form.Separated)
        {
            Add(names, Quoted(clause) ?? clause, form);
            return;
        }

        foreach (string part in clause.Split(',')) Add(names, Quoted(part) ?? part, form);
    }

    /// <summary>The one name a clause that declares rather than imports holds, or null where it is empty.</summary>
    private static string? Single(string clause, ImportForm form) => NameOf(clause, form.Shape);

    private static void Add(List<ImportedName> names, string text, ImportForm form)
    {
        if (NameOf(text, form.Shape) is { } name) names.Add(new ImportedName(name, form.Shape));
    }

    /// <summary>
    ///     The name this clause holds, or null where it holds none. A path is whatever was written; a
    ///     module has a shape, and checking it is what keeps the word that opens a directive from
    ///     also opening a statement.
    ///     C# spells <c>using System.Text;</c>, <c>using var reader = new StreamReader(path);</c> and
    ///     <c>using (var scope = …)</c> with the same first word, and only the first is an import. An
    ///     opener alone cannot tell them apart — the clause can, because a module name is a dotted
    ///     identifier and an expression is not. The same test covers X#'s <c>using</c>, and it drops
    ///     the fragments a conditional Delphi <c>uses</c> clause leaves behind, at the cost of the
    ///     real unit name beside them: a missing edge, which is the error this module prefers.
    /// </summary>
    private static string? NameOf(string text, ImportShape shape)
    {
        var name = Clean(text);
        if (name.IsEmpty) return null;
        if (shape == ImportShape.Path) return name.ToString();

        // An alias names the module on the right of the `=` and binds it to the name on the left:
        // `using Grid = System.Windows.Controls.Grid;` imports the Grid, not the alias. The left has
        // to be one identifier, which is what a declaration written with the same word is not —
        // `var reader` is two.
        int equals = name.IndexOf('=');
        if (equals >= 0)
        {
            if (!IsIdentifier(name[..equals].Trim())) return null;
            name = name[(equals + 1)..].Trim();
        }

        // The last word, so that a qualifier in front of the name — C#'s `using static System.Math`
        // — is read past rather than read as part of it.
        int space = name.LastIndexOfAny(' ', '\t');
        if (space >= 0) name = name[(space + 1)..];
        return IsDottedIdentifier(name) ? name.ToString() : null;
    }

    /// <summary>
    ///     Whether this is a name and nothing else: letters, digits and underscores, not starting
    ///     with a digit. Deliberately narrow — a generic argument, a bracket or a parenthesis means
    ///     the line was an expression wearing an import's first word.
    /// </summary>
    private static bool IsIdentifier(ReadOnlySpan<char> text)
    {
        if (text.Length == 0 || char.IsAsciiDigit(text[0])) return false;
        foreach (char c in text)
            if (!SymbolText.IsWordChar(c))
                return false;
        return true;
    }

    /// <summary>Identifiers joined by dots, which is how every language here spells a module.</summary>
    private static bool IsDottedIdentifier(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return false;
        foreach (var part in text.Split('.'))
            if (!IsIdentifier(text[part]))
                return false;
        return true;
    }

    /// <summary>
    ///     A name as the answer carries it: trimmed, without what ends the statement it was written
    ///     in, and without the quotes around it — <c>url(a.png)</c> and <c>url('a.png')</c> name one
    ///     file and must not read as two. The trailing brace goes for the same reason: C# writes both
    ///     <c>namespace Foo;</c> and <c>namespace Foo {</c>.
    /// </summary>
    private static ReadOnlySpan<char> Clean(string text)
    {
        var name = text.AsSpan().Trim().TrimEnd(";{").Trim();
        if (name.Length >= 2 && (name[0] == '"' || name[0] == '\'') && name[^1] == name[0])
            name = name[1..^1].Trim();
        return name;
    }

    /// <summary>
    ///     The first quoted string in the clause, or null where it holds none. The angle brackets are
    ///     here with the quotes because <c>#include &lt;Set_Ansi.ch&gt;</c> is the dominant spelling
    ///     in X# and in C, and a reader that knew only <c>"…"</c> answered that such a file imports
    ///     nothing — which is the one sentence this must not say by accident.
    /// </summary>
    private static string? Quoted(string clause)
    {
        int open = clause.IndexOfAny(_quoteOpeners);
        if (open < 0) return null;
        char closer = clause[open] == '<' ? '>' : clause[open];
        int close = clause.IndexOf(closer, open + 1);
        return close < 0 ? null : clause[(open + 1)..close];
    }

    private static readonly char[] _quoteOpeners = ['"', '\'', '`', '<'];

    /// <summary>
    ///     The quoted value of one attribute inside a tag. Read no further than the tag's own
    ///     <c>&gt;</c>, so the next tag on the line is not read as part of this one, and matched on a
    ///     word boundary so that <c>data-src=</c> is not read as <c>src=</c>.
    /// </summary>
    private static string? AttributeValue(string clause, string attribute)
    {
        int close = clause.IndexOf('>', StringComparison.Ordinal);
        var tag = close < 0 ? clause.AsSpan() : clause.AsSpan(0, close);
        for (int at = 0; at < tag.Length; at++)
        {
            int found = tag[at..].IndexOf(attribute.AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (found < 0) return null;
            at += found;
            if (at > 0 && (char.IsLetterOrDigit(tag[at - 1]) || tag[at - 1] == '-' || tag[at - 1] == '_'))
                continue;
            int after = at + attribute.Length;
            while (after < tag.Length && char.IsWhiteSpace(tag[after])) after++;
            if (after >= tag.Length || tag[after] != '=') continue;
            return Quoted(tag[(after + 1)..].ToString());
        }

        return null;
    }

    /// <summary>
    ///     The text of one clause: from just past the opener to the form's closer, or to the end of
    ///     the line's code where the form has no closer or it is not on this line.
    /// </summary>
    private static string ClauseOf(string code, int from, ImportForm form)
    {
        if (from < 0 || from >= code.Length) return "";
        if (form.Closer is { } closer)
        {
            int end = code.IndexOf(closer, from, StringComparison.Ordinal);
            if (end >= 0) return code[from..end];
        }

        return code[from..];
    }

    /// <summary>
    ///     How many characters of the line this form's opener takes at <paramref name="index" />, or
    ///     -1 where it does not stand there. A space in the opener matches a run of whitespace, or
    ///     the end of the line: Delphi writes <c>uses</c> alone on its line with the units under it,
    ///     and an opener matched on one literal space would find no clause there at all.
    /// </summary>
    private int OpenerLength(string line, int index, ImportForm form)
    {
        if (index < 0) return -1;
        int at = index;
        string opener = form.Opener;
        for (int i = 0; i < opener.Length; i++)
        {
            if (char.IsWhiteSpace(opener[i]))
            {
                // The clause is on the next line, which is a shape only a spanning form has and which
                // the position carries for it.
                if (at >= line.Length) return at - index;
                if (!char.IsWhiteSpace(line[at])) return -1;
                while (at < line.Length && char.IsWhiteSpace(line[at])) at++;
                continue;
            }

            if (at >= line.Length || !SameLetter(line[at], opener[i])) return -1;
            at++;
        }

        return at - index;
    }

    /// <summary>The next index at or after <paramref name="from" /> where this form's opener stands, or -1.</summary>
    private int IndexOfOpener(string line, int from, ImportForm form)
    {
        string head = form.Head;
        int at = Math.Max(0, from);
        while (at <= line.Length - head.Length)
        {
            int found = line.AsSpan(at).IndexOf(head, _keywordComparison);
            if (found < 0) return -1;
            at += found;
            if (OpenerLength(line, at, form) >= 0) return at;
            at++;
        }

        return -1;
    }

    /// <summary>
    ///     Whether the one import form that may run past the end of its line is open once this line
    ///     has been read. Delphi's <c>uses</c> is that form: it lists its units comma-separated over
    ///     as many lines as it likes and ends at a <c>;</c>, and it is written twice in a unit.
    ///     The closer is what ends it, and so is a line that cannot be part of a name list. The
    ///     second rule is what bounds the damage: a clause whose <c>;</c> this cannot see — hidden in
    ///     a <c>{ }</c> comment, or simply never written — would otherwise stay open to the end of the
    ///     file and report every line below it as an import. One line is the most a missed terminator
    ///     can now cost.
    ///     The cheap rejection runs first, because every line of a Delphi unit but a handful is
    ///     neither opening a clause nor inside one and must pay only for finding that out.
    /// </summary>
    /// <param name="open">Whether a clause was already open when this line began.</param>
    /// <param name="line">The line, whole.</param>
    /// <param name="start">Its first non-space character.</param>
    /// <param name="codeEnd">Where prose begins on this line, which the walk has already established.</param>
    private bool ClauseAfter(bool open, string line, int start, int codeEnd)
    {
        var form = _importForms[_spanningImport];
        if (form.Closer is not { } closer) return false;
        int at = start;
        if (!open)
        {
            int opener = OpenerLength(line, start, form);
            if (opener < 0) return false;
            at = start + opener;
        }

        if (at > codeEnd) return open && IsNameList(line.AsSpan(start, Math.Max(0, codeEnd - start)));
        var rest = line.AsSpan(at, codeEnd - at);
        return rest.IndexOf(closer, StringComparison.Ordinal) < 0 && IsNameList(rest);
    }

    /// <summary>
    ///     Whether this is the shape a clause of names is written in: identifiers, the dots between
    ///     their parts, and the commas between them. Anything else — a bracket, an assignment, a
    ///     keyword's punctuation — says the clause ended further up and this line is ordinary code.
    /// </summary>
    private static bool IsNameList(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
            if (!SymbolText.IsWordChar(c) && c != '.' && c != ',' && !char.IsWhiteSpace(c))
                return false;
        return true;
    }

    /// <summary>
    ///     Whether one of the line-start comment forms opens at the start of the line's text, at
    ///     <paramref name="start" />, making the whole line prose. The forms found anywhere on a line
    ///     are the scan's business; this is only the ones that mean nothing elsewhere. One that ends in
    ///     a letter is a word, so <c>#regional</c> is not <c>#region</c>.
    /// </summary>
    private bool OpensAWholeLine(string line, int start)
    {
        if (IsDirectiveAt(line, start)) return false;
        for (int i = 0; i < _lineStartComments.Length; i++)
        {
            string opener = _lineStartComments[i];
            if (SymbolText.IsWordChar(opener[^1]) ? DirectiveWordAt(line, start, opener) : At(line, start, opener))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Whether this word form stands here, where the punctuation in front of its word may be
    ///     followed by whitespace: C# and X# both allow <c># region</c> for <c>#region</c>, and read as
    ///     anything else its label was code, so an apostrophe in it opened a char literal (#295).
    /// </summary>
    private bool DirectiveWordAt(string line, int index, string opener)
    {
        int sigil = 0;
        while (sigil < opener.Length && !SymbolText.IsWordChar(opener[sigil])) sigil++;
        if (sigil == 0) return PhraseAt(line, index, opener);
        // Spans, because this is asked of every line a scan begins outside everything.
        if (!line.AsSpan(index).StartsWith(opener.AsSpan(0, sigil), StringComparison.Ordinal)) return false;
        int word = index + sigil;
        while (word < line.Length && char.IsWhiteSpace(line[word])) word++;
        return PhraseAt(line, word, opener.AsSpan(sigil));
    }

    /// <summary>One character against another, under the profile's own case rule.</summary>
    private bool SameLetter(char actual, char expected) =>
        _keywordComparison == StringComparison.OrdinalIgnoreCase
            ? char.ToUpperInvariant(actual) == char.ToUpperInvariant(expected)
            : actual == expected;

    private static bool At(string text, int index, string value,
        StringComparison comparison = StringComparison.Ordinal) =>
        index >= 0 && index + value.Length <= text.Length
                   && text.AsSpan(index, value.Length).Equals(value, comparison);

    /// <summary>
    ///     Whether the text ends with one of these words, on a word boundary and under the profile's
    ///     own case rule — <c>renew(</c> does not end with <c>new</c>, and <c>NEW</c> does where the
    ///     language says case does not matter.
    /// </summary>
    private bool EndsWithKeyword(ReadOnlySpan<char> text, string[] words)
    {
        for (int i = 0; i < words.Length; i++)
        {
            string word = words[i];
            if (text.Length < word.Length) continue;
            if (!text[^word.Length..].Equals(word, _keywordComparison)) continue;
            if (text.Length == word.Length || !SymbolText.IsWordChar(text[^(word.Length + 1)])) return true;
        }

        return false;
    }

    private static Answer<ReferenceKind> Placed(ReferenceKind kind) => new(kind, Evidence.Text);

    private bool IsDeclaration(ReadOnlySpan<char> prefix, ReadOnlySpan<char> suffix, ReadOnlySpan<char> symbol,
        string? typeDeclared)
    {
        // "class Foo", "record Foo", "interface Foo" — the symbol is the thing being declared, and the
        // type regex already knows what may sit in front of the keyword.
        if (typeDeclared is not null && symbol.SequenceEqual(typeDeclared)) return true;

        // The tail first, though it is the last of the three conditions to have been written. All
        // three are ANDed, and this one is anchored and reads a few characters, where the two below
        // each walk a prefix that on a long line is most of the file; ordering the cheap gate first
        // turns most appearances away before either of them runs.
        if (!DeclarationTail().IsMatch(suffix)) return false;

        // The prefix must look like a declaration head — modifiers and a return type and nothing
        // else. This is what keeps "return Foo(" and "x => Foo(" out.
        if (!DeclarationPrefix().IsMatch(prefix)) return false;

        // The same comparison the patterns above were built with. Asking ordinally where the language
        // shouts its keywords answered "no declaration here" for every `CREATE PROCEDURE` in a project
        // — and then reported it as a call, which is a wrong answer shaped like a right one.
        for (int i = 0; i < _declarationModifiers.Length; i++)
            if (SymbolText.ContainsWord(prefix, _declarationModifiers[i], _keywordComparison))
                return true;
        return false;
    }

    private bool LooksLikeType(ReadOnlySpan<char> head, ReadOnlySpan<char> suffix) =>
        EndsWithKeyword(head, _instantiationKeywords)
        || EndsWithAny(head, _typePrefixes)
        || TypedDeclarationTail().IsMatch(suffix);
}
