using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeExplorer;

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
///     engine, and this says what each candidate is. The patterns are built per profile rather than
///     declared with <c>[GeneratedRegex]</c>, because their alternations come from the profile's own
///     keyword lists; one analyser is built per language at startup and the cost is paid once.
/// </summary>
public sealed class TextAnalyzer : ILanguageAnalyzer
{
    /// <summary>
    ///     Everything that may legally sit between the start of a declaration and its name. Shared by
    ///     every profile: it describes the shape of a declaration head, not any language's punctuation.
    /// </summary>
    private const string DeclarationPrefixPattern = @"^[\s\w<>,\[\]\?\.]*$";

    /// <summary>
    ///     An RE2- and .NET-legal pattern that matches nothing, for a profile that declares no
    ///     modifiers or no type keywords. An empty alternation would match the empty string — every
    ///     line — which is the opposite of what "this language declares nothing I can read" means.
    /// </summary>
    private const string MatchesNothing = @"[^\s\S]";

    /// <summary>
    ///     The compound assignments, which are the same list wherever a language has any of them and
    ///     are therefore not a profile fact. Written without the trailing <c>=</c>, which is added
    ///     once below.
    /// </summary>
    private static readonly string[] CompoundOperators =
        ["+", "-", "*", "/", "%", "|", "&", "^", "??", "<<", ">>"];

    private readonly Regex _assignment;
    private readonly Regex _declarationPrefix;
    private readonly Regex? _generated;
    private readonly StringComparison _keywordComparison;
    private readonly Regex _keywordDeclaration;
    private readonly Regex _memberDeclaration;
    private readonly LanguageProfile _profile;
    private readonly Regex _typeDeclaration;
    private readonly Regex _typedDeclarationTail;

    // The profile's lists as arrays. <see cref="Scan" /> walks them once per character of every line
    // examined, and an IReadOnlyList<string> there is an interface dispatch per opener per character —
    // which on a minified bundle, where the whole file is one line, is most of the time spent.
    private readonly string[] _declarationModifiers;
    private readonly string[] _directivePrefixes;
    private readonly string[] _importPrefixes;
    private readonly string[] _instantiationKeywords;
    private readonly string[] _lineComments;
    private readonly string[] _memberAccess;

    /// <summary>Everything that, at the start of a line's text, makes the whole line a comment.</summary>
    private readonly string[] _opensALine;
    private readonly StringDelimiter[] _strings;
    private readonly string[] _typePrefixes;

    /// <summary>
    ///     The characters that can begin anything <see cref="LineCursor" /> cares about outside a literal,
    ///     and the ones that can end each literal from inside it. The scan jumps between them instead
    ///     of testing every character against every delimiter: ordinary code is nearly all characters
    ///     that start nothing, and skipping those runs is what keeps a one-line minified bundle from
    ///     costing a delimiter probe per character per match on it.
    /// </summary>
    private readonly SearchValues<char> _opensSomething;

    private readonly SearchValues<char>[] _closesLiteral;

    public TextAnalyzer(LanguageProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _lineComments = [.. profile.LineComments];
        _directivePrefixes = [.. profile.DirectivePrefixes];
        _opensALine =
        [
            .. profile.LineStartComments, .. profile.LineComments, .. profile.BlockComments.Select(b => b.Open)
        ];
        _strings = [.. profile.Strings];
        _instantiationKeywords = [.. profile.InstantiationKeywords];
        _memberAccess = [.. profile.MemberAccessOperators];
        _typePrefixes = [.. profile.TypePrefixOperators];
        _importPrefixes = [.. profile.ImportPrefixes];
        _declarationModifiers = [.. profile.DeclarationModifiers];
        _opensSomething = SearchValues.Create(
            [.. _lineComments.Concat(_strings.Select(s => s.Open)).Select(o => o[0]).Distinct()]);
        _closesLiteral =
        [
            .. _strings.Select(s => SearchValues.Create(s.Escape == StringEscape.Backslash
                ? new[] { s.Close[0], '\\' }
                : [s.Close[0]]))
        ];

        _keywordComparison = profile.CaseInsensitiveKeywords
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string flag = profile.CaseInsensitiveKeywords ? "(?i)" : "";
        string? modifiers = Alternation(profile.DeclarationModifiers);
        // The C-family shape: modifiers, a return type, the name, then what opens a body.
        string memberPattern = modifiers is null
            ? MatchesNothing
            : $@"^\s*(?:\[[^\]]*\]\s*)*(?:(?:{modifiers})\s+)+[\w<>,\[\]\?\.]+\s+(\w+)\s*[\(<{{=]";
        // The xBase, Delphi and SQL shape: the introducing word and then the name, with the return
        // type — where there is one — after it rather than before.
        string keywordPattern = modifiers is null || !profile.DeclarationNamesFollowKeyword
            ? MatchesNothing
            : $@"^\s*(?:(?:{modifiers})\s+)+(\w+)\s*[\(<{{=;:]";
        string typePattern = Alternation(profile.DeclarationKeywords) is { } keywords
            ? $@"^\s*(?:\[[^\]]*\]\s*)*(?:[\w]+\s+)*\b(?:{keywords})\s+(\w+)"
            : MatchesNothing;

        _memberDeclaration = new Regex(flag + memberPattern, RegexOptions.CultureInvariant);
        _keywordDeclaration = new Regex(flag + keywordPattern, RegexOptions.CultureInvariant);
        _typeDeclaration = new Regex(flag + typePattern, RegexOptions.CultureInvariant);
        // Only the shapes this language actually writes. A language that declares nothing this can
        // read asks the engine for no lines at all, rather than for the lines a pattern that matches
        // nothing would return.
        string[] shapes = [.. new[] { memberPattern, keywordPattern, typePattern }.Where(p => p != MatchesNothing)];
        DeclarationCandidates = shapes.Length == 0
            ? CandidateLines.None
            : CandidateLines.Matching($"{flag}{string.Join("|", shapes.Select(p => $"(?:{p})"))}");

        _declarationPrefix = new Regex(DeclarationPrefixPattern, RegexOptions.CultureInvariant);
        _assignment = new Regex(AssignmentPattern(profile.AssignmentOperators), RegexOptions.CultureInvariant);
        // "Symbol x", "Symbol? x", "Symbol[] x", "Symbol<T> x" — a type followed by the thing it types.
        _typedDeclarationTail = new Regex(@"^(\??(\[\])?|<[^<>]*>)\s+\w", RegexOptions.CultureInvariant);
        _generated = profile.GeneratedPathPatterns.Count == 0
            ? null
            : new Regex(
                "^(?:" + string.Join("|", profile.GeneratedPathPatterns.Select(GlobToPattern)) + ")$",
                // A path is compared without regard to case, the way a file system does.
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    /// <summary>Call parentheses, with an optional generic argument list in front of them.</summary>
    private static readonly Regex Invocation = new(@"^\s*(<[^<>()]*>)?\s*\(", RegexOptions.CultureInvariant);

    /// <summary>
    ///     A declaration head is followed by a parameter list, a generic list, a property body or an
    ///     initialiser — never by an operator or the end of an expression.
    /// </summary>
    private static readonly Regex DeclarationTail = new(@"^\s*([\(<{;=]|=>)", RegexOptions.CultureInvariant);

    public string? Language => _profile.Name;

    public IReadOnlyList<string> Extensions => _profile.Extensions;

    public bool SeparatesDeclarationFromImplementation => _profile.SeparatesDeclarationFromImplementation;

    public CandidateLines DeclarationCandidates { get; }

    public Answer<Lexical> StateAt(string line, int index)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (index < 0 || index > line.Length) return new Answer<Lexical>(Lexical.Code, Evidence.Text);
        var cursor = new LineCursor(this, line);
        return new Answer<Lexical>(cursor.StateAt(index), Evidence.Text);
    }

    /// <summary>
    ///     One left-to-right pass of a line, handing out the lexical state at each position asked
    ///     about in turn. It is a cursor and not a function of the position because the positions
    ///     arrive in ascending order: asked as a function it re-walked the line from the start every
    ///     time, which on a minified bundle — one line of several million characters, well inside
    ///     <c>Index:MaxFileBytes</c> — cost a walk per match rather than a walk per line.
    ///     It lives here rather than on the analyser because an analyser answers every search at once
    ///     and can hold no per-line state of its own.
    ///     Nothing is allocated per position.
    /// </summary>
    private struct LineCursor
    {
        private readonly TextAnalyzer _analyzer;
        private readonly string _line;
        private readonly bool _wholeLine;
        private int _at;
        private int _openIndex;
        private bool _commented;

        public LineCursor(TextAnalyzer analyzer, string line)
        {
            _analyzer = analyzer;
            _line = line;
            _openIndex = -1;
            int start = FirstNonSpace(line);
            _wholeLine = start < line.Length && analyzer.OpensAComment(line, start);
            _at = start;
        }

        public Lexical StateAt(int index)
        {
            if (_wholeLine || _commented) return Lexical.Comment;
            Advance(index);
            if (_commented) return Lexical.Comment;
            return _openIndex < 0 ? Lexical.Code : Lexical.Literal;
        }

        /// <summary>
        ///     Walks from wherever the last question left off to this one. A skip over an escape or a
        ///     closing delimiter can carry <c>_at</c> a character or two past the position asked
        ///     about; the state is the same either side of one, because no identifier begins inside a
        ///     quote or an escape, so the overshoot is left rather than backtracked.
        /// </summary>
        private void Advance(int index)
        {
            while (_at < index)
            {
                if (_openIndex < 0)
                {
                    // Jump to the next character that could begin a comment or a literal. Everything
                    // between is ordinary code and needs no decision.
                    int next = _line.AsSpan(_at, index - _at).IndexOfAny(_analyzer._opensSomething);
                    if (next < 0)
                    {
                        _at = index;
                        return;
                    }

                    _at += next;

                    // A comment opener outside a literal takes the rest of the line. Inside one it is
                    // text: a URL in a string would otherwise turn everything after it into prose.
                    for (int c = 0; c < _analyzer._lineComments.Length; c++)
                        if (At(_line, _at, _analyzer._lineComments[c]))
                        {
                            _commented = true;
                            return;
                        }

                    _openIndex = _analyzer.OpenerAt(_line, _at);
                    _at += _openIndex < 0 ? 1 : _analyzer._strings[_openIndex].Open.Length;
                    continue;
                }

                var open = _analyzer._strings[_openIndex];
                int found = _line.AsSpan(_at, index - _at).IndexOfAny(_analyzer._closesLiteral[_openIndex]);
                if (found < 0)
                {
                    _at = index;
                    return;
                }

                _at += found;

                if (open.Escape == StringEscape.Backslash && _line[_at] == '\\')
                {
                    _at += 2;
                    continue;
                }

                if (At(_line, _at, open.Close))
                {
                    // A doubled delimiter stands for itself and does not close the literal.
                    if (open.Escape == StringEscape.Doubled && At(_line, _at + open.Close.Length, open.Close))
                    {
                        _at += 2 * open.Close.Length;
                        continue;
                    }

                    _at += open.Close.Length;
                    _openIndex = -1;
                    continue;
                }

                _at++;
            }
        }
    }

    /// <summary>Which string literal opens here, as an index into <see cref="_strings" />, or -1.</summary>
    private int OpenerAt(string line, int index)
    {
        // A loop rather than a LINQ predicate: this runs once per candidate character of every line.
        for (int i = 0; i < _strings.Length; i++)
            if (At(line, index, _strings[i].Open))
                return i;
        return -1;
    }

    /// <summary>Where the line's text begins, without allocating the trimmed copy to find out.</summary>
    private static int FirstNonSpace(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        return i;
    }

    public Answer<Declared?> Declares(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string? type = _typeDeclaration.Match(line) is { Success: true } t ? t.Groups[1].Value : null;
        // The C-family shape first: where a language writes both, it is the more specific of the two
        // and the keyword shape would stop at the return type.
        string? member = _memberDeclaration.Match(line) is { Success: true } m ? m.Groups[1].Value
            : _keywordDeclaration.Match(line) is { Success: true } k ? k.Groups[1].Value
            : null;
        // The role is left unsaid. Telling a Delphi interface section from its implementation, or a
        // PL/SQL package spec from its body, needs the file-level position #53 builds; until then
        // either answer would be a guess, and a guess reported as a fact is the one failure this
        // seam exists to avoid.
        return new Answer<Declared?>(
            type is null && member is null ? null : new Declared(type, member, null),
            Evidence.Text);
    }

    public Answer<string?> ImportOn(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        int start = FirstNonSpace(line);
        // Tested against the line in place and only cut once one matches: this runs per occurrence,
        // and a project of import-free lines should not allocate a trimmed copy of every one of them.
        int matched = ImportPrefixLength(line, start);
        return matched < 0
            ? new Answer<string?>(null, Evidence.Text)
            : new Answer<string?>(line[(start + matched)..].Trim().TrimEnd(';'), Evidence.Text);
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
    public IReadOnlyList<Answer<ReferenceKind>> Occurrences(string line, string symbol)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(symbol);

        // What the line is, asked once. Whether it is an import and what type it declares do not
        // change between one appearance of the symbol and the next, and the type regex alone cost
        // milliseconds per appearance on a long line when it was asked per appearance.
        bool import = IsImportLine(line);
        string? typeDeclared = _typeDeclaration.Match(line) is { Success: true } t ? t.Groups[1].Value : null;
        var cursor = new LineCursor(this, line);

        var placed = new List<Answer<ReferenceKind>>();
        foreach (int at in SymbolText.Occurrences(line, symbol))
            placed.Add(Place(line, at, symbol.Length, cursor.StateAt(at), import, typeDeclared));
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
        if (import) return Placed(ReferenceKind.Import);

        // Spans and not substrings. A line naming a common identifier a thousand times would otherwise
        // allocate a thousand copies of the line either side of the match, and the lines this reads
        // are whatever the index holds — a minified bundle is one line of several million characters.
        var prefix = line.AsSpan(0, index);
        var suffix = line.AsSpan(index + length);
        var head = prefix.TrimEnd();

        bool afterReceiver = EndsWithAny(head, _memberAccess);
        bool invoked = Invocation.IsMatch(suffix);

        if (!afterReceiver && IsDeclaration(prefix, suffix, line.AsSpan(index, length), typeDeclared))
            return Placed(ReferenceKind.Definition);
        if (invoked && EndsWithKeyword(head, _instantiationKeywords)) return Placed(ReferenceKind.Instantiation);
        if (invoked) return Placed(ReferenceKind.Call);
        if (_assignment.IsMatch(suffix)) return Placed(ReferenceKind.Write);
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
        if (assignments.Count == 0) return MatchesNothing;
        var operators = CompoundOperators.Select(op => op + "=").Concat(assignments)
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

    /// <summary>Whether this line is an import line at all, without cutting what it imports out of it.</summary>
    private bool IsImportLine(string line) => ImportPrefixLength(line, FirstNonSpace(line)) >= 0;

    /// <summary>How long the import prefix on this line is, or -1 when it has none.</summary>
    private int ImportPrefixLength(string line, int start)
    {
        for (int i = 0; i < _importPrefixes.Length; i++)
            if (At(line, start, _importPrefixes[i], _keywordComparison))
                return _importPrefixes[i].Length;
        return -1;
    }

    /// <summary>Whether a comment opens at the start of the line's text, at <paramref name="start" />.</summary>
    private bool OpensAComment(string line, int start)
    {
        for (int i = 0; i < _directivePrefixes.Length; i++)
            if (At(line, start, _directivePrefixes[i], _keywordComparison))
                return false;
        for (int i = 0; i < _opensALine.Length; i++)
            if (At(line, start, _opensALine[i]))
                return true;
        return false;
    }

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
        if (!DeclarationTail.IsMatch(suffix)) return false;

        // The prefix must look like a declaration head — modifiers and a return type and nothing
        // else. This is what keeps "return Foo(" and "x => Foo(" out.
        if (!_declarationPrefix.IsMatch(prefix)) return false;

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
        || _typedDeclarationTail.IsMatch(suffix);
}
