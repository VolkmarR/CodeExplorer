using System.Text.RegularExpressions;

namespace CodeExplorer.Language;

/// <summary>
///     What a line declares, and whether it is the declaration or the implementation side of a split
///     (#54). Apart from the scan, the imports and the occurrences because <c>find_definition</c> and
///     <c>list_declarations</c> read it, and a reference only for the label of the scope it sits in.
/// </summary>
public sealed partial class TextAnalyzer
{
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
    private bool PhraseAt(string line, int index, string phrase)
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

    /// <summary>
    ///     Builds the declaration shapes this profile writes. Out of the constructor because it is most
    ///     of it, and because what it builds is what <see cref="Declares" /> runs.
    /// </summary>
    private static DeclarationShapes ShapesOf(LanguageProfile profile)
    {
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

        return new DeclarationShapes(
            PatternOrNull(flag, memberDeclarationPattern),
            PatternOrNull(flag, keywordPattern),
            PatternOrNull(flag, typePattern),
            PatternOrNull(flag, precedingTypePattern),
            Candidates(memberPattern, keywordPattern, typePattern, precedingTypePattern),
            // The same four shapes with the symbol where the captured name was. A line the capturing
            // shapes read as declaring the symbol matches here at the same place, so this loses no
            // declaration; it is still a prefilter, and Declares stays the final word.
            ForSymbol);

        CandidateLines ForSymbol(string symbol)
        {
            string literal = SymbolText.Re2Literal(symbol);
            return Candidates(
                MemberPattern("", literal),
                KeywordPattern($@"(?:{literal}\s*\.\s*{word}+|(?:{word}+\s*\.\s*)?{literal})"),
                // The one shape with nothing after the name, so it needs the boundary the greedy
                // capture gave it: `class OrderServiceX` declares no `OrderService`.
                TypePattern(literal + SymbolText.Re2WordEnd),
                PrecedingTypePattern(literal));
        }
    }

    /// <summary>
    ///     What <see cref="ShapesOf" /> builds from a profile: the four declaration shapes compiled for
    ///     <see cref="Declares" />, each null where the profile gives it nothing to match, and the same
    ///     shapes published as the candidate predicates the engine narrows a search with.
    /// </summary>
    private sealed record DeclarationShapes(
        Regex? Member,
        Regex? Keyword,
        Regex? Type,
        Regex? PrecedingType,
        CandidateLines Candidates,
        Func<string, CandidateLines> CandidatesFor);
}
