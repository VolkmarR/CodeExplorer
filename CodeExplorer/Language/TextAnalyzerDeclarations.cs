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
}
