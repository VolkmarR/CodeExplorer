using System.Buffers;

namespace CodeExplorer.Language;

/// <summary>
///     The lexical scan: where a line's comments and literals open and close, what a file position
///     carries to the next line, and the state at one index (#53). Apart from the declarations, the
///     imports and the occurrences because every one of them stands on it, and it is the code that
///     has to stay one pass that allocates almost nothing, even over a minified line (ADR-0008).
/// </summary>
public sealed partial class TextAnalyzer
{
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
    private static int FirstNonSpace(string line, int from = 0)
    {
        int i = from;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        return i;
    }

    /// <summary>
    ///     A line-start comment form, split once where the profile is read: the punctuation in front,
    ///     and the word after it where the form ends in one. Whitespace may stand between the two, as
    ///     C# and X# both allow <c># region</c> for <c>#region</c>; read as anything else its label was
    ///     code, so an apostrophe in it opened a char literal (#295).
    /// </summary>
    /// <param name="Sigil">What the form begins with, matched as written.</param>
    /// <param name="Word">The word after it, matched whole, or null where the form is punctuation only.</param>
    private readonly record struct LineStartForm(string Sigil, string? Word)
    {
        public static LineStartForm Of(string opener)
        {
            if (!SymbolText.IsWordChar(opener[^1])) return new LineStartForm(opener, null);
            int sigil = 0;
            while (!SymbolText.IsWordChar(opener[sigil])) sigil++;
            return new LineStartForm(opener[..sigil], opener[sigil..]);
        }
    }
}
