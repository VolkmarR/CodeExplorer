using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     The three things a search over the index settles before or after it queries: whether the name
///     it was given is a name at all, how a language's declaration candidates become SQL, and whether
///     the name it found on a line is really on it.
///     All three are here rather than in one of the searches because each has more than one caller and
///     none is a fact about any one of them. A second copy of the refusals would let
///     <c>find_references</c> and <c>find_definition</c> explain the same malformed input two ways, a
///     second copy of the decoder would let a case added to <see cref="CandidateLines" /> compile
///     silently in one of them — with the cast that throws in the branch nobody updated — and a second
///     copy of the prose check would let two readers disagree about whether a commented-out
///     declaration is one.
/// </summary>
internal static class SearchQuery
{
    /// <summary>
    ///     Why this is not something to look for, or null. Malformed input must never come back as an
    ///     empty result: a caller acts on "nothing uses this" and on "nothing declares this", and a
    ///     refusal shaped like either teaches the agent something false.
    /// </summary>
    /// <param name="symbol">Already trimmed.</param>
    /// <param name="tool">The tool to name in the refusal, which is the only thing that differs.</param>
    public static string? Unusable(string symbol, string tool)
    {
        if (symbol.Length == 0)
            return "No symbol given. Pass the identifier to look for, such as \"OrderStatus\".";
        if (symbol.Any(char.IsWhiteSpace))
            return $"\"{symbol}\" is not one identifier. {tool} looks for a single name; "
                   + "use grep for a phrase, or regex=true for a pattern.";
        if (!symbol.Any(c => char.IsLetterOrDigit(c) || c == '_'))
            return $"\"{symbol}\" holds no identifier characters, so it names no symbol. "
                   + "Use grep for punctuation and operators.";
        return null;
    }

    /// <summary>
    ///     The <c>AND</c> that narrows a language's files to the lines it could declare something on:
    ///     empty for an analyser that reads every line — a parser-backed one narrows nothing — and
    ///     null for one that reads none, which is not the same as an empty narrowing and must not
    ///     become one. A language that declares nothing this can read is asked for no lines at all
    ///     rather than for lines that would be thrown away.
    /// </summary>
    /// <param name="candidates">What the analyser says it needs to see (ADR-0008).</param>
    /// <param name="name">The parameter to bind the pattern to, unique within the caller's command.</param>
    /// <param name="parameters">The command's parameters, appended to where there is a pattern.</param>
    public static string? Narrowing(CandidateLines candidates, string name, List<DuckDBParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(parameters);
        switch (candidates)
        {
            case CandidateLines.NoLine:
                return null;
            case CandidateLines.EveryLine:
                return "";
            case CandidateLines.Re2Pattern pattern:
                parameters.Add(new DuckDBParameter(name, pattern.Pattern));
                return $" AND regexp_matches(content, ${name}, '')";
            default:
                // A case added to CandidateLines and not to this: no lines is the answer that costs a
                // missing declaration, where every line is one that costs reading the whole index.
                return null;
        }
    }

    /// <summary>
    ///     Whether the name sits only in a comment or a string on this line, which the declaration
    ///     patterns cannot see: a commented-out <c>procedure Advance;</c> is shaped exactly like the
    ///     live one, and so is one quoted inside a generated string.
    ///     A line the scan never reached is kept — unknown is not prose — because the alternative is
    ///     dropping every declaration in a file too long to walk.
    /// </summary>
    /// <param name="analyzer">The file's analyser, which is what decides what a comment is (ADR-0008).</param>
    /// <param name="position">Where the file stood at the start of this line.</param>
    /// <param name="line">The line's text.</param>
    /// <param name="symbol">The name to ask about, which is the one the line was read as declaring.</param>
    public static bool OnlyInProse(ILanguageAnalyzer analyzer, FilePosition position, string line, string symbol)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        // Asked once for the whole line rather than once per appearance. Asked per appearance — which
        // is what `StateAt` is — a line naming the symbol N times costs N walks of it, and the lines
        // here are whatever the index holds: the cost the analyser's own cursor exists to avoid.
        var appearances = analyzer.Occurrences(position, line, symbol);
        // Every appearance and not the first: a line that names the symbol in a trailing comment and
        // then declares it is a declaration, and reading only the first would lose it.
        return appearances.Count > 0
               && appearances.All(a => a.Value is ReferenceKind.Comment or ReferenceKind.StringLiteral);
    }
}
