using System.Text.RegularExpressions;

namespace CodeExplorer;

/// <summary>
///     What one appearance of an identifier looks like. The names are the ones CONTEXT.md uses for a
///     reference: a place where an identifier appears in code in a way that looks like real use, as
///     against a mention in a comment, a string or an import. <see cref="Other" /> is deliberate —
///     one this cannot place is kept and labelled unplaced rather than dropped, because a dropped
///     reference is a wrong answer shaped like a right one.
/// </summary>
public enum ReferenceKind
{
    /// <summary>The line appears to declare the identifier.</summary>
    Definition,

    /// <summary>
    ///     The identifier is assigned to: <c>x.Status = …</c>, <c>Status += …</c>, or the receiver-less
    ///     object-initializer form <c>Status = dao.Status</c>. This is the one that answers "what
    ///     changes this?", so it is kept apart from a plain read.
    /// </summary>
    Write,

    /// <summary>The identifier is invoked: <c>Symbol(</c> or <c>x.Symbol(</c>.</summary>
    Call,

    /// <summary>A type is constructed: <c>new Symbol(...)</c>.</summary>
    Instantiation,

    /// <summary>Used as a type: <c>: Symbol</c>, <c>Symbol x</c>, <c>List&lt;Symbol&gt;</c>.</summary>
    TypeUse,

    /// <summary>Read as a member: <c>x.Symbol</c> with no call parentheses and no assignment.</summary>
    MemberAccess,

    /// <summary>A using, import, include or namespace line.</summary>
    Import,

    /// <summary>In a comment, so it is a mention and not a use.</summary>
    Comment,

    /// <summary>Inside a string literal, which is a mention too — though the one that often matters.</summary>
    StringLiteral,

    /// <summary>A real reference in code that none of the shapes above fits.</summary>
    Other
}

/// <summary>
///     One line that could be a declaration, as the index handed it over: which line it is, how far it
///     is indented, and what it seems to declare. Either name may be null — a member declaration names
///     no type and a type declaration no member — and a line that reads as both fills both.
/// </summary>
/// <param name="LineNumber">1-based, as everything an agent is shown is.</param>
/// <param name="Indent">Columns of leading whitespace, a tab counted as four.</param>
/// <param name="Type">The type this line declares, if it declares one.</param>
/// <param name="Member">The member this line declares, if it declares one.</param>
internal sealed record DeclarationLine(int LineNumber, int Indent, string? Type, string? Member);

/// <summary>
///     Heuristic, text-only classification of a reference (CONTEXT.md). There is no compiler and no
///     symbol table here: this reads one line at a time and decides what each appearance looks like,
///     which is why a reference is strong evidence and never proof.
///     .NET <see cref="Regex" /> appears here and only here, and only over a line DuckDB already
///     picked out, which is the division CODING_STANDARDS draws: the candidate set is chosen by the
///     engine, and this says what each candidate is.
/// </summary>
internal static partial class ReferenceClassifier
{
    /// <summary>Everything that may legally sit between the start of a declaration and its name.</summary>
    private const string DeclarationPrefixPattern = @"^[\s\w<>,\[\]\?\.]*$";

    private const string MemberPattern =
        @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|static|async|override|virtual|abstract|sealed|partial|extern|new|readonly|export|declare|function|def|val|let)\s+)+[\w<>,\[\]\?\.]+\s+(\w+)\s*[\(<{=]";

    private const string TypePattern =
        @"^\s*(?:\[[^\]]*\]\s*)*(?:[\w]+\s+)*\b(?:class|interface|struct|record|enum)\s+(\w+)";

    /// <summary>
    ///     The two shapes above as one RE2 pattern, for the <c>regexp_matches</c> that narrows a file's
    ///     lines to the few that could enclose a reference. It is built from the same constants the
    ///     .NET regexes below use, so the set DuckDB hands over and the set this can read apart cannot
    ///     drift: a line the engine skipped is a scope this would never have found anyway.
    /// </summary>
    public const string DeclarationCandidatePattern = $"(?:{MemberPattern})|(?:{TypePattern})";

    /// <summary>
    ///     Words that may introduce a declaration. Keeping the list short and boring is the point: a
    ///     modifier this does not know costs an <see cref="ReferenceKind.Other" />, while one it invents
    ///     costs a call reported as a declaration.
    /// </summary>
    private static readonly string[] DeclarationModifiers =
    [
        "public", "private", "protected", "internal", "static", "async", "override", "virtual",
        "abstract", "sealed", "partial", "extern", "readonly", "const", "export", "declare",
        "function", "def", "val", "let"
    ];

    private static readonly string[] ImportPrefixes =
        ["using ", "import ", "namespace ", "#include", "from ", "package ", "require("];

    /// <summary>
    ///     Where <paramref name="symbol" /> sits on this line, on word boundaries, every time it does.
    ///     Every one is classified and not only the first: <c>return Foo.Create(Foo.Default)</c> is a
    ///     type use and a read, and reporting it as one of them loses the other.
    /// </summary>
    public static IEnumerable<int> Occurrences(string line, string symbol)
    {
        if (symbol.Length == 0) yield break;
        for (int i = IndexOfSymbol(line, symbol); i >= 0; i = IndexOfSymbol(line, symbol, i + 1))
            yield return i;
    }

    /// <summary>
    ///     What the appearance of <paramref name="symbol" /> at <paramref name="index" /> looks like.
    ///     Order matters: noise first, so a commented-out call is never counted as a call.
    /// </summary>
    public static ReferenceKind Classify(string line, string symbol, int index)
    {
        if (index < 0 || index + symbol.Length > line.Length) return ReferenceKind.Other;

        string prefix = line[..index];
        string suffix = line[(index + symbol.Length)..];
        string trimmed = line.TrimStart();

        if (IsComment(trimmed, prefix)) return ReferenceKind.Comment;
        if (InString(prefix)) return ReferenceKind.StringLiteral;
        if (ImportPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.Ordinal))) return ReferenceKind.Import;

        bool afterDot = prefix.TrimEnd().EndsWith('.');
        bool invoked = Invocation().IsMatch(suffix);

        if (!afterDot && IsDeclaration(prefix, suffix, symbol, line)) return ReferenceKind.Definition;
        if (invoked && prefix.TrimEnd().EndsWith("new", StringComparison.Ordinal)) return ReferenceKind.Instantiation;
        if (invoked) return ReferenceKind.Call;
        if (Assignment().IsMatch(suffix)) return ReferenceKind.Write;
        // "Foo.Bar()" — Foo itself is a reference to the type, not a member access on something else.
        if (!afterDot && suffix.StartsWith('.')) return ReferenceKind.TypeUse;
        if (afterDot) return ReferenceKind.MemberAccess;
        if (LooksLikeType(prefix, suffix)) return ReferenceKind.TypeUse;
        return ReferenceKind.Other;
    }

    /// <summary>
    ///     Where the identifier next sits on the line, on word boundaries, or -1. Done by hand rather
    ///     than with a per-call <c>\b…\b</c> regex because the symbol is caller text: escaping it into
    ///     a pattern to find something that is not a pattern buys nothing and can only go wrong.
    /// </summary>
    public static int IndexOfSymbol(string line, string symbol, int from = 0)
    {
        if (symbol.Length == 0 || from > line.Length) return -1;
        for (int i = line.IndexOf(symbol, from, StringComparison.Ordinal);
             i >= 0;
             i = line.IndexOf(symbol, i + 1, StringComparison.Ordinal))
        {
            bool startsClean = !IsWord(symbol[0]) || i == 0 || !IsWord(line[i - 1]);
            int after = i + symbol.Length;
            bool endsClean = !IsWord(symbol[^1]) || after >= line.Length || !IsWord(line[after]);
            if (startsClean && endsClean) return i;
        }

        return -1;
    }

    /// <summary>
    ///     What a candidate line declares, or null when it turns out to declare nothing. The engine's
    ///     prefilter is the wider of the two nets, so a line reaching here may still be neither.
    /// </summary>
    public static DeclarationLine? Declaration(int lineNumber, string content)
    {
        string? type = TypeDeclaration().Match(content) is { Success: true } t ? t.Groups[1].Value : null;
        string? member = MemberDeclaration().Match(content) is { Success: true } m ? m.Groups[1].Value : null;
        return type is null && member is null ? null : new DeclarationLine(lineNumber, Indent(content), type, member);
    }

    /// <summary>
    ///     The nearest enclosing declaration above a reference, as <c>Type.Member</c>. Indentation is
    ///     the only structure available without a parser, so a declaration counts only if it is
    ///     indented less than the reference itself, and the type only if it is further out than the
    ///     member.
    /// </summary>
    /// <param name="declarations">This file's declaration lines in ascending line order.</param>
    /// <param name="lineNumber">The line the reference is on.</param>
    /// <param name="indent">That line's indent, from the content the search already read.</param>
    public static string? EnclosingScope(IReadOnlyList<DeclarationLine> declarations, int lineNumber, int indent)
    {
        string? member = null, type = null;
        int memberIndent = int.MaxValue;

        for (int i = declarations.Count - 1; i >= 0; i--)
        {
            var declaration = declarations[i];
            if (declaration.LineNumber >= lineNumber) continue;

            if (member is null && declaration.Member is not null && declaration.Indent < indent)
            {
                member = declaration.Member;
                memberIndent = declaration.Indent;
                continue;
            }

            if (declaration.Type is not null && declaration.Indent < memberIndent && declaration.Indent < indent)
            {
                type = declaration.Type;
                break;
            }
        }

        return (type, member) switch
        {
            (null, null) => null,
            (not null, null) => type,
            (null, _) => member,
            _ => $"{type}.{member}"
        };
    }

    /// <summary>Columns of leading whitespace, a tab counted as four — the width most source is written to.</summary>
    public static int Indent(string line)
    {
        int columns = 0;
        foreach (char c in line)
            if (c == ' ') columns++;
            else if (c == '\t') columns += 4;
            else break;
        return columns;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsComment(string trimmed, string prefix)
    {
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*')
                                                               || trimmed.StartsWith("/*", StringComparison.Ordinal)
                                                               || trimmed.StartsWith("--", StringComparison.Ordinal)
                                                               || (trimmed.StartsWith('#') &&
                                                                   !trimmed.StartsWith("#include",
                                                                       StringComparison.Ordinal)))
            return true;

        // A trailing comment opened earlier on the same line, as long as that "//" is not itself
        // inside a string — a URL in a literal would otherwise turn the rest of the line into prose.
        int slashes = prefix.IndexOf("//", StringComparison.Ordinal);
        return slashes >= 0 && !InString(prefix[..slashes]);
    }

    /// <summary>An odd number of unescaped double quotes before the match means we are inside a literal.</summary>
    private static bool InString(string prefix)
    {
        int quotes = 0;
        for (int i = 0; i < prefix.Length; i++)
            if (prefix[i] == '"' && (i == 0 || prefix[i - 1] != '\\'))
                quotes++;
        return quotes % 2 == 1;
    }

    private static bool IsDeclaration(string prefix, string suffix, string symbol, string line)
    {
        // "class Foo", "record Foo", "interface Foo" — the symbol is the thing being declared, and the
        // type regex already knows what may sit in front of the keyword.
        if (TypeDeclaration().Match(line) is { Success: true } declared
            && string.Equals(declared.Groups[1].Value, symbol, StringComparison.Ordinal))
            return true;

        // Otherwise the prefix must look like a declaration head — modifiers and a return type and
        // nothing else. This is what keeps "return Foo(" and "x => Foo(" out.
        if (!DeclarationPrefix().IsMatch(prefix)) return false;
        if (!DeclarationModifiers.Any(m => ContainsWord(prefix, m))) return false;

        // A declaration head is followed by a parameter list, a generic list, a property body or an
        // initialiser — never by an operator or the end of an expression.
        return DeclarationTail().IsMatch(suffix);
    }

    private static bool ContainsWord(string text, string word)
    {
        int at = IndexOfSymbol(text, word);
        return at >= 0;
    }

    private static bool LooksLikeType(string prefix, string suffix) =>
        prefix.TrimEnd().EndsWith("new", StringComparison.Ordinal)
        || prefix.TrimEnd().EndsWith(':')
        || prefix.TrimEnd().EndsWith('<')
        || prefix.TrimEnd().EndsWith(',')
        || TypedDeclarationTail().IsMatch(suffix);

    [GeneratedRegex(DeclarationPrefixPattern)]
    private static partial Regex DeclarationPrefix();

    [GeneratedRegex(MemberPattern)]
    private static partial Regex MemberDeclaration();

    [GeneratedRegex(TypePattern)]
    private static partial Regex TypeDeclaration();

    /// <summary>Call parentheses, with an optional generic argument list in front of them.</summary>
    [GeneratedRegex(@"^\s*(<[^<>()]*>)?\s*\(")]
    private static partial Regex Invocation();

    /// <summary>
    ///     The identifier as the left-hand side of an assignment, plain or compound. The character
    ///     class after the <c>=</c> is what keeps comparisons (<c>==</c>, <c>&gt;=</c>, <c>!=</c>) and
    ///     lambda arrows (<c>=&gt;</c>) out — those are reads, and conflating them is what makes a
    ///     "who changes this?" answer useless.
    /// </summary>
    [GeneratedRegex(@"^\s*(?:\+|-|\*|/|%|\||&|\^|\?\?|<<|>>)?=(?!=|>)")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"^\s*([\(<{;=]|=>)")]
    private static partial Regex DeclarationTail();

    /// <summary>"Symbol x", "Symbol? x", "Symbol[] x", "Symbol&lt;T&gt; x" — a type followed by the thing it types.</summary>
    [GeneratedRegex(@"^(\??(\[\])?|<[^<>]*>)\s+\w")]
    private static partial Regex TypedDeclarationTail();
}
