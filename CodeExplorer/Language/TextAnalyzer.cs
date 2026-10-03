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
    private readonly LineStartForm[] _lineStartComments;

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
        _lineStartComments = [.. profile.LineStartComments.Select(LineStartForm.Of)];
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

    public string? Language => _profile.Name;

    public IReadOnlyList<string> Extensions => _profile.Extensions;

    public bool SeparatesDeclarationFromImplementation => _profile.SeparatesDeclarationFromImplementation;

    public CandidateLines DeclarationCandidates { get; }

    public CandidateLines DeclarationCandidatesFor(string symbol) => _declarationCandidatesFor(symbol);

    public FilePosition Start => _start;

    public Answer<bool> IsGenerated(string qualifiedPath)
    {
        ArgumentNullException.ThrowIfNull(qualifiedPath);
        return new Answer<bool>(_generated?.IsMatch(qualifiedPath) ?? false, Evidence.Text);
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

    /// <summary>One character against another, under the profile's own case rule.</summary>
    private bool SameLetter(char actual, char expected) =>
        _keywordComparison == StringComparison.OrdinalIgnoreCase
            ? char.ToUpperInvariant(actual) == char.ToUpperInvariant(expected)
            : actual == expected;

    private static bool At(string text, int index, string value,
        StringComparison comparison = StringComparison.Ordinal) =>
        index >= 0 && index + value.Length <= text.Length
                   && text.AsSpan(index, value.Length).Equals(value, comparison);
}
