using System.Text.RegularExpressions;

namespace CodeExplorer.Language;

/// <summary>
///     What each appearance of an identifier on a line looks like: a write, a call, a read, a type use or
///     a declaration (ADR-0008). Apart from the scan, the declarations and the imports because it is the
///     question <c>find_references</c> asks of every candidate line, and the head-and-tail shapes below
///     are read by nothing else.
/// </summary>
public sealed partial class TextAnalyzer
{
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
