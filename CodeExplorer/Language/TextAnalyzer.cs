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
    private readonly string[] _blockCommentOpeners;
    private readonly string[] _declarationModifiers;
    private readonly string[] _directivePrefixes;
    private readonly string[] _importPrefixes;
    private readonly string[] _lineComments;
    private readonly string[] _lineStartComments;
    private readonly string[] _memberAccess;
    private readonly StringDelimiter[] _strings;
    private readonly string[] _typePrefixes;

    /// <summary>
    ///     The characters that can begin anything <see cref="Scan" /> cares about outside a literal,
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
        _lineStartComments = [.. profile.LineStartComments];
        _directivePrefixes = [.. profile.DirectivePrefixes];
        _blockCommentOpeners = [.. profile.BlockComments.Select(b => b.Open)];
        _strings = [.. profile.Strings];
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
        DeclarationCandidatePattern =
            $"{flag}(?:(?:{memberPattern})|(?:{keywordPattern})|(?:{typePattern}))";

        _declarationPrefix = new Regex(DeclarationPrefixPattern, RegexOptions.CultureInvariant);
        _assignment = new Regex(AssignmentPattern(profile.AssignmentOperators), RegexOptions.CultureInvariant);
        // "Symbol x", "Symbol? x", "Symbol[] x", "Symbol<T> x" — a type followed by the thing it types.
        _typedDeclarationTail = new Regex(@"^(\??(\[\])?|<[^<>]*>)\s+\w", RegexOptions.CultureInvariant);
        _generated = profile.GeneratedPathPatterns.Count == 0
            ? null
            : new Regex(
                "^(?:" + string.Join("|", profile.GeneratedPathPatterns.Select(GlobToPattern)) + ")$",
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

    public Evidence Evidence => Evidence.Text;

    public bool SeparatesDeclarationFromImplementation => _profile.SeparatesDeclarationFromImplementation;

    public string DeclarationCandidatePattern { get; }

    public Answer<Lexical> StateAt(string line, int index)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (index < 0 || index > line.Length) return new Answer<Lexical>(Lexical.Code, Evidence.Text);
        return new Answer<Lexical>(Scan(line, index), Evidence.Text);
    }

    /// <summary>
    ///     What the position sits in, in one left-to-right pass of everything before it. One pass and
    ///     not one per question: a minified bundle is a single line of several million characters, and
    ///     an index built from <c>Index:MaxFileBytes</c> holds such lines, so re-walking the prefix per
    ///     comment opener turned one <c>find_references</c> into minutes inside a single tool call.
    ///     Nothing is allocated here for the same reason.
    /// </summary>
    private Lexical Scan(string line, int index)
    {
        int start = FirstNonSpace(line);
        if (start < line.Length && OpensAComment(line, start)) return Lexical.Comment;

        int openIndex = -1;
        int i = start;
        while (i < index)
        {
            if (openIndex < 0)
            {
                // Jump to the next character that could begin a comment or a literal. Everything
                // between is ordinary code and needs no decision.
                int next = line.AsSpan(i, index - i).IndexOfAny(_opensSomething);
                if (next < 0) break;
                i += next;

                // A comment opener outside a literal takes the rest of the line. Inside one it is
                // text: a URL in a string would otherwise turn everything after it into prose.
                for (int c = 0; c < _lineComments.Length; c++)
                    if (At(line, i, _lineComments[c]))
                        return Lexical.Comment;

                openIndex = OpenerAt(line, i);
                i += openIndex < 0 ? 1 : _strings[openIndex].Open.Length;
                continue;
            }

            var open = _strings[openIndex];
            int found = line.AsSpan(i, index - i).IndexOfAny(_closesLiteral[openIndex]);
            if (found < 0) break;
            i += found;

            if (open.Escape == StringEscape.Backslash && line[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (At(line, i, open.Close))
            {
                // A doubled delimiter stands for itself and does not close the literal.
                if (open.Escape == StringEscape.Doubled && At(line, i + open.Close.Length, open.Close))
                {
                    i += 2 * open.Close.Length;
                    continue;
                }

                i += open.Close.Length;
                openIndex = -1;
                continue;
            }

            i++;
        }

        return openIndex < 0 ? Lexical.Code : Lexical.Literal;
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
        // The role is always Declaration for now. Telling a Delphi interface section from its
        // implementation, or a PL/SQL package spec from its body, needs the file-level position #53
        // builds; until then saying "implementation" would be a guess, and a guess reported as a fact
        // is the one failure this seam exists to avoid.
        return new Answer<Declared?>(
            type is null && member is null ? null : new Declared(type, member, DeclarationRole.Declaration),
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
    public Answer<ReferenceKind> Occurrence(string line, int index, int length)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (index < 0 || length < 0 || index + length > line.Length) return Placed(ReferenceKind.Other);

        // Spans and not substrings. A line naming a common identifier a thousand times would otherwise
        // allocate a thousand copies of the line either side of the match, and the lines this reads
        // are whatever the index holds — a minified bundle is one line of several million characters.
        var prefix = line.AsSpan(0, index);
        var suffix = line.AsSpan(index + length);
        var head = prefix.TrimEnd();

        var state = Scan(line, index);
        if (state == Lexical.Comment) return Placed(ReferenceKind.Comment);
        if (state == Lexical.Literal) return Placed(ReferenceKind.StringLiteral);
        if (IsImportLine(line)) return Placed(ReferenceKind.Import);

        bool afterReceiver = EndsWithAny(head, _memberAccess);
        bool invoked = Invocation.IsMatch(suffix);

        if (!afterReceiver && IsDeclaration(prefix, suffix, line, index, length))
            return Placed(ReferenceKind.Definition);
        if (invoked && head.EndsWith("new", StringComparison.Ordinal)) return Placed(ReferenceKind.Instantiation);
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
            : string.Join("|", words.OrderByDescending(w => w.Length).Select(Escape));

    /// <summary>
    ///     A literal for a pattern both .NET and RE2 accept. Not <see cref="Regex.Escape" />, which
    ///     also escapes whitespace and <c>#</c> in ways RE2 rejects — and a modifier may be a phrase
    ///     with a space in it, which is exactly the case that would have failed inside DuckDB rather
    ///     than here.
    /// </summary>
    private static string Escape(string word) =>
        string.Concat(word.Select(c =>
            Metacharacters.Contains(c, StringComparison.Ordinal) ? $"\\{c}" : c.ToString()));

    /// <summary>The characters RE2 and .NET both read as pattern syntax.</summary>
    private const string Metacharacters = @"\.+*?()|[]{}^$";

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
            .OrderByDescending(op => op.Length).Select(Escape);
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
                _ => Regex.Escape(c.ToString())
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
        {
            string prefix = _importPrefixes[i];
            if (_profile.CaseInsensitiveKeywords
                    ? AtIgnoringCase(line, start, prefix)
                    : At(line, start, prefix))
                return prefix.Length;
        }

        return -1;
    }

    /// <summary>Whether a comment opens at the start of the line's text, at <paramref name="start" />.</summary>
    private bool OpensAComment(string line, int start)
    {
        for (int i = 0; i < _directivePrefixes.Length; i++)
            if (AtIgnoringCase(line, start, _directivePrefixes[i]))
                return false;
        for (int i = 0; i < _lineStartComments.Length; i++)
            if (At(line, start, _lineStartComments[i]))
                return true;
        for (int i = 0; i < _lineComments.Length; i++)
            if (At(line, start, _lineComments[i]))
                return true;
        for (int i = 0; i < _blockCommentOpeners.Length; i++)
            if (At(line, start, _blockCommentOpeners[i]))
                return true;
        return false;
    }

    private static bool At(string text, int index, string value) =>
        index >= 0 && index + value.Length <= text.Length
                   && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    private static bool AtIgnoringCase(string text, int index, string value) =>
        index >= 0 && index + value.Length <= text.Length
                   && text.AsSpan(index, value.Length).Equals(value, StringComparison.OrdinalIgnoreCase);

    private static Answer<ReferenceKind> Placed(ReferenceKind kind) => new(kind, Evidence.Text);

    private bool IsDeclaration(ReadOnlySpan<char> prefix, ReadOnlySpan<char> suffix, string line, int index,
        int length)
    {
        // "class Foo", "record Foo", "interface Foo" — the symbol is the thing being declared, and the
        // type regex already knows what may sit in front of the keyword.
        if (_typeDeclaration.Match(line) is { Success: true } declared
            && declared.Groups[1].ValueSpan.SequenceEqual(line.AsSpan(index, length)))
            return true;

        // Otherwise the prefix must look like a declaration head — modifiers and a return type and
        // nothing else. This is what keeps "return Foo(" and "x => Foo(" out.
        if (!_declarationPrefix.IsMatch(prefix)) return false;
        // The same comparison the patterns above were built with. Asking ordinally where the language
        // shouts its keywords answered "no declaration here" for every `CREATE PROCEDURE` in a project
        // — and then reported it as a call, which is a wrong answer shaped like a right one.
        bool introduced = false;
        for (int i = 0; i < _declarationModifiers.Length && !introduced; i++)
            introduced = SymbolText.ContainsWord(prefix, _declarationModifiers[i], _keywordComparison);
        if (!introduced) return false;

        return DeclarationTail.IsMatch(suffix);
    }

    private bool LooksLikeType(ReadOnlySpan<char> head, ReadOnlySpan<char> suffix) =>
        head.EndsWith("new", StringComparison.Ordinal)
        || EndsWithAny(head, _typePrefixes)
        || _typedDeclarationTail.IsMatch(suffix);
}
