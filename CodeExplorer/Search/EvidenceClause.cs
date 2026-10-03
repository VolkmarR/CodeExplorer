using CodeExplorer.Language;

namespace CodeExplorer.Search;

/// <summary>
///     How a reply says its answers were reached (ADR-0008), decided once for every tool that reports
///     answers from the language seam: <c>find_definition</c>, <c>find_references</c>, <c>imports</c>
///     and <c>list_declarations</c>. Each tool keeps its own three sentences; what they share is which
///     one is true. Derived from the answers rather than written as a fact, so the day a parser-backed
///     analyser is registered for a language no reply has to change to stop understating what it knows.
///     Three ways and not two: a two-way check prints the mixed sentence for a reply that is entirely a
///     parser's, which talks about a textual half that is not there.
///     In <c>Search/</c> and not <c>Infrastructure/</c>, which may not reach <c>Language/</c> (ADR-0005).
/// </summary>
internal static class EvidenceClause
{
    /// <param name="evidence">How each answer in the reply was reached.</param>
    /// <param name="textual">
    ///     Said where every answer was read from the text, and where there is no answer at all: a reply
    ///     with nothing of its own to report was still read the way the scan reads.
    /// </param>
    /// <param name="parsed">Said where every answer was a parser's.</param>
    /// <param name="mixed">Said where some answers were a parser's and some were read from the text.</param>
    public static string Of(IEnumerable<Evidence> evidence, string textual, string parsed, string mixed)
    {
        bool read = false, byParser = false;
        foreach (var answer in evidence)
            if (answer == Evidence.Parsed) byParser = true;
            else read = true;
        return !byParser ? textual : read ? mixed : parsed;
    }
}
