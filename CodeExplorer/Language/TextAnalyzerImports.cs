namespace CodeExplorer.Language;

/// <summary>
///     What a line imports, read from the profile's import forms: the clause after the opener, the names
///     in it and the one form that may run past the end of its line. Apart from the scan, the
///     declarations and the occurrences because a line is read for its imports only while a build
///     fills the imports table.
/// </summary>
public sealed partial class TextAnalyzer
{
    public bool HasImports => _importForms.Length > 0;

    public ImportPathRules ImportPaths => _profile.ImportPaths;

    public Answer<ImportsOnLine> ImportsOn(FilePosition position, string line)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(line);
        return new Answer<ImportsOnLine>(Extract(position, line), Evidence.Text);
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
            var (sigil, word) = _lineStartComments[i];
            if (!At(line, start, sigil)) continue;
            if (word is null || PhraseAt(line, FirstNonSpace(line, start + sigil.Length), word)) return true;
        }

        return false;
    }
}
