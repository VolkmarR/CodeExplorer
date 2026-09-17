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

    /// <summary>
    ///     How deeply comments, literals and the interpolation holes inside them may nest before the
    ///     scan stops claiming to know where it is. A literal and its hole are two, so eight is four
    ///     literals inside each other's holes — past anything a person writes, and the point at which
    ///     a text scan with no grammar behind it should stop being believed. Past it the rest of the
    ///     file is <see cref="Lexical.Unknown" />, which is the answer that keeps a reference rather
    ///     than placing it wrongly.
    /// </summary>
    private const int MaxNesting = 8;

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

    /// <summary>
    ///     Everything that, at the start of a line's text, makes the whole line a comment. Only the
    ///     line-start forms: a <c>//</c> or a <c>/*</c> there is found by the scan like any other,
    ///     while a bare <c>*</c> is a comment at the start of a line and a multiplication anywhere else.
    /// </summary>
    private readonly string[] _lineStartComments;

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
        _importPrefixes = [.. profile.ImportPrefixes];
        _declarationModifiers = [.. profile.DeclarationModifiers];
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
        _start = new TextPosition(this, []);

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

    public FilePosition Start => _start;

    public FilePosition After(FilePosition position, string line)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(line);
        Span<Frame> frames = stackalloc Frame[MaxNesting];
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
        Span<Frame> frames = stackalloc Frame[MaxNesting];
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
    /// </summary>
    private readonly record struct Frame(int Index, int Depth, bool Hole);

    /// <summary>
    ///     What earlier lines of one file left open, as this analyser records it. It names the analyser
    ///     that made it because the frames index that analyser's own tables: one handed to another
    ///     language would point at whatever happens to sit at those indexes, so it is read as
    ///     <see cref="FilePosition.Unknown" /> instead.
    ///     This is also the record the declaration/implementation section becomes a field on: a Delphi
    ///     unit's <c>interface</c> against its <c>implementation</c> is another thing earlier lines
    ///     decided, and adding it changes nothing above.
    /// </summary>
    private sealed record TextPosition(TextAnalyzer Owner, Frame[] Frames) : FilePosition;

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
        ///     The rest of this line is a comment: a line comment opened on it, or one of the forms
        ///     that means a comment only at the start of a line did. Neither carries to the next line,
        ///     which is why both are this one flag and not two.
        /// </summary>
        private bool _lineCommented;

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
            carried.Frames.CopyTo(frames);
            _depth = carried.Frames.Length;
            int start = FirstNonSpace(line);
            // Only where the line begins outside everything: a `*` inside an open block comment is
            // the comment's own continuation marker and decides nothing.
            _lineCommented = _depth == 0 && start < line.Length && analyzer.OpensAWholeLine(line, start);
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
            if (depth == 0) return _analyzer._start;
            // A line inside a long block comment or a license header leaves the file exactly where it
            // found it, and there are a thousand such lines in a row. Handing the carried position
            // back is what keeps those from allocating a copy apiece to say nothing changed.
            return Unchanged(depth) ? _carried : new TextPosition(_analyzer, _frames[..depth].ToArray());
        }

        /// <summary>Whether the frames that carry are the ones this line began with.</summary>
        private readonly bool Unchanged(int depth) =>
            depth == _carried.Frames.Length && _frames[..depth].SequenceEqual(_carried.Frames);

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
                Push(opened, false);
                _at += _analyzer._forms[opened].Form.Open.Length;
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
                    Push(frame.Index, true);
                    _at += hole.Open.Length;
                }

                return;
            }

            if (At(_line, _at, open.Close))
            {
                // A doubled delimiter stands for itself and does not close the literal.
                if (open.Escape == StringEscape.Doubled && At(_line, _at + open.Close.Length, open.Close))
                {
                    _at += 2 * open.Close.Length;
                    return;
                }

                _depth--;
                _at += open.Close.Length;
                return;
            }

            _at++;
        }

        private void Push(int index, bool hole)
        {
            if (_depth == _frames.Length)
            {
                _unknown = true;
                return;
            }

            _frames[_depth++] = new Frame(index, 0, hole);
        }
    }

    /// <summary>
    ///     Which comment or literal opens here, as an index into <see cref="_forms" />, or -1. A
    ///     compiler directive that begins the way a comment does opens none — Delphi's <c>{$IFDEF}</c>
    ///     against its <c>{ }</c> comment — and the exemption is checked wherever the opener is found
    ///     and not only at the start of a line, because a directive is written after code too.
    /// </summary>
    private int OpenerAt(string line, int index)
    {
        // A loop rather than a LINQ predicate: this runs once per candidate character of every line.
        for (int i = 0; i < _forms.Length; i++)
            if (At(line, index, _forms[i].Form.Open))
                return _forms[i].Prose && IsDirectiveAt(line, index) ? -1 : i;
        return -1;
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
    public IReadOnlyList<Answer<ReferenceKind>> Occurrences(FilePosition position, string line, string symbol)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(symbol);

        // What the line is, asked once. Whether it is an import and what type it declares do not
        // change between one appearance of the symbol and the next, and the type regex alone cost
        // milliseconds per appearance on a long line when it was asked per appearance.
        bool import = IsImportLine(line);
        string? typeDeclared = _typeDeclaration.Match(line) is { Success: true } t ? t.Groups[1].Value : null;
        Span<Frame> frames = stackalloc Frame[MaxNesting];
        var cursor = new LineCursor(this, position, line, frames);

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

    /// <summary>
    ///     Whether one of the line-start comment forms opens at the start of the line's text, at
    ///     <paramref name="start" />, making the whole line prose. The forms found anywhere on a line
    ///     are the scan's business; this is only the ones that mean nothing elsewhere.
    /// </summary>
    private bool OpensAWholeLine(string line, int start)
    {
        if (IsDirectiveAt(line, start)) return false;
        for (int i = 0; i < _lineStartComments.Length; i++)
            if (At(line, start, _lineStartComments[i]))
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
