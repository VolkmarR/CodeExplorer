using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     The two things an identifier search settles before it can query: whether the name it was given
///     is a name at all, and how a language's declaration candidates become SQL.
///     Both are here rather than in one of the searches because both have two callers and neither is
///     a fact about either one. A second copy of the refusals would let <c>find_references</c> and
///     <c>find_definition</c> explain the same malformed input two ways, and a second copy of the
///     decoder would let a case added to <see cref="CandidateLines" /> compile silently in one of
///     them — with the cast that throws in the branch nobody updated.
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
}
