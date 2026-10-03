using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeExplorer.Reading;
using DuckDB.NET.Data;

namespace CodeExplorer.Search;

/// <summary>
///     How a grep query becomes the predicate the engine filters lines with: the token and word tests
///     of a text query, and the literal a multiline pattern requires, which narrows the files it reads
///     whole. Apart from the two reads because they are tested on their own and change for different
///     reasons: these with the tokenizer and RE2, the reads with what a reply shows.
/// </summary>
public sealed partial class GrepSearch
{
    /// <summary>
    ///     The predicate a text query matches lines with: with <paramref name="useTokens" />, every
    ///     identifier piece as a whole token, then every whitespace-separated word verified with
    ///     <c>contains</c>. The tests are ANDed, so each distinct piece and word is tested once however
    ///     often the query repeats it. Exposed for tests.
    /// </summary>
    internal static string TextMatch(string[] words, bool caseSensitive, bool useTokens,
        List<DuckDBParameter> parameters)
    {
        var tests = new List<string>();
        if (useTokens)
        {
            // The same rule BM25 applied, without reading the BM25 index. The index is built with
            // stemmer='none', stopwords='none' and ignore='[^a-z0-9_]+' (FtsExtension), so a token
            // is a maximal run of [a-z0-9_] — which is exactly what \b bounds on lower-cased text.
            // Conjunctive: every piece of the query must be present, in any order, which is why
            // this is one test per piece and not one \b…\b around the whole string.
            // Always case-insensitive, as the lower-cased index was, and the contains() below
            // restores exactness either way.
            string[] pieces = words.SelectMany(word => IdentifierPieces().Split(word.ToLowerInvariant()))
                .Where(piece => piece.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            for (int i = 0; i < pieces.Length; i++)
            {
                tests.Add(string.Create(CultureInfo.InvariantCulture, $"regexp_matches(lower(l.content), $w{i})"));
                // No escaping: a piece is [a-z0-9_] by construction, so nothing in it is a
                // metacharacter. A piece that could carry one would be a piece the split kept.
                parameters.Add(new DuckDBParameter($"w{i}", $@"\b{pieces[i]}\b"));
            }
        }

        string content = caseSensitive ? "l.content" : "lower(l.content)";
        string[] verified = words.Select(word => caseSensitive ? word : word.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal).ToArray();
        for (int i = 0; i < verified.Length; i++)
        {
            tests.Add(string.Create(CultureInfo.InvariantCulture, $"contains({content}, $t{i})"));
            parameters.Add(new DuckDBParameter($"t{i}", verified[i]));
        }

        return string.Join(" AND ", tests);
    }

    /// <summary>
    ///     The candidate filter a multiline search puts in front of RE2: a file is read whole only when one
    ///     of its lines holds the literal the pattern requires (<see cref="RequiredLiteral" />), or every
    ///     file is when there is none. Its parameter is added to <paramref name="parameters" />. Exposed
    ///     for tests.
    /// </summary>
    internal static string LiteralPrefilter(string query, bool caseSensitive, List<DuckDBParameter> parameters)
    {
        if (RequiredLiteral(query) is not { } literal) return "";

        // The literal holds no newline, so a whole-file match must contain it within one line. Compared
        // lower-cased regardless of case mode: a (?i) inside the pattern would otherwise defeat it.
        // lower() is not RE2's case folding, though: RE2 folds s, S and ſ (U+017F) together and lower('ſ')
        // stays ſ, so a file matching only through the long s was dropped before RE2 saw it (#266). When
        // the search folds case and the literal has an s, the literal maps ſ to s, and so does a line,
        // but only a line that holds a ſ: the plain test answers every other one, so almost no line pays
        // for the replace. The Kelvin sign, RE2's other fold beyond ASCII, needs nothing: lower()
        // already maps it to k.
        string lowered = literal.ToLowerInvariant();
        bool foldsLongS = (!caseSensitive || Re2.MayFoldCase(query)) && lowered.AsSpan().IndexOfAny('s', 'ſ') >= 0;
        parameters.Add(new DuckDBParameter("lit", foldsLongS ? lowered.Replace('ſ', 's') : lowered));
        string test = foldsLongS
            ? "(contains(lower(l.content), $lit) OR (contains(l.content, 'ſ') AND contains(replace(lower(l.content), 'ſ', 's'), $lit)))"
            : "contains(lower(l.content), $lit)";
        return $" AND EXISTS (SELECT 1 FROM lines l WHERE l.file_id = f.file_id AND {test})";
    }

    /// <summary>
    ///     A literal every match of the pattern must contain, or null. Ripgrep's "inner literal" idea,
    ///     reduced to what is provably sound: only characters at the top level (outside any group or
    ///     class), not under a quantifier that allows zero, and never when the top level has an
    ///     alternation. Whatever it does not fully understand ends the current run, never adds to it: a
    ///     shorter literal than ripgrep would find costs a weaker prefilter, a wrong one drops a file
    ///     that matches (#234). Exposed for tests.
    /// </summary>
    internal static string? RequiredLiteral(string pattern)
    {
        var best = new StringBuilder();
        var current = new StringBuilder();
        int depth = 0;

        void Break()
        {
            if (current.Length > best.Length) best.Clear().Append(current);
            current.Clear();
        }

        // Keeps c only when the next character cannot let it match zero times; '+' keeps it but ends
        // the run, because "ab+c" matches "abbc", which does not contain "abc".
        void Literal(char c, char next)
        {
            if (depth != 0) return;
            // The filter tests one line at a time, so a literal holding a line break matches nothing. A
            // surrogate is half a character: a quantifier after the pair would guard only the low half,
            // leaving a lone high surrogate in a literal that "a😀?" does not require.
            if (next is '?' or '*' or '{' || c is '\n' or '\r' || char.IsSurrogate(c))
            {
                Break();
                return;
            }

            current.Append(c);
            if (next == '+') Break();
        }

        char At(int index) => index < pattern.Length ? pattern[index] : '\0';

        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            char next = At(i + 1);
            switch (c)
            {
                case '|' when depth == 0:
                    return null;
                case '\\':
                    // \. and \( are the character itself. Every other escape (\d, \b, \x41, \pL, \Q..\E,
                    // octal) is consumed whole and ends the run: kept, its tail would read as literals.
                    if (next != '\0' && !char.IsLetterOrDigit(next))
                    {
                        i++;
                        Literal(next, At(i + 1));
                        break;
                    }

                    i = Re2.EscapeEnd(pattern, i);
                    Break();
                    break;
                case '(':
                    depth++;
                    Break();
                    break;
                case ')':
                    depth--;
                    Break();
                    break;
                case '[':
                    // An unterminated class is a pattern RE2 rejects; no prefilter rather than a guess.
                    i = Re2.ClassEnd(pattern, i);
                    if (i < 0) return null;
                    Break();
                    break;
                case '{':
                    // A counted repetition; its digits are not literals.
                    while (i < pattern.Length && pattern[i] != '}') i++;
                    Break();
                    break;
                case '.' or '^' or '$' or '?' or '*' or '+' or '}':
                    Break();
                    break;
                default:
                    Literal(c, next);
                    break;
            }
        }

        Break();
        string literal = best.ToString();
        return literal.Trim().Length == 0 ? null : literal;
    }

    /// <summary>The FTS tokenizer keeps letters, digits and underscore; a token with none of them has no index entry.</summary>
    private static bool HasIndexableChars(string token) => token.Any(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>
    ///     How the full-text index splits text into tokens — <c>ignore = '[^a-z0-9_]+'</c> in
    ///     <see cref="CodeExplorer.Index.FtsExtension" /> — applied to the query so the word tests match what BM25 matched.
    ///     The two have to stay in step: a query split one way and an index built another would answer
    ///     with lines that do not contain what was asked for.
    /// </summary>
    [GeneratedRegex("[^a-z0-9_]+")]
    private static partial Regex IdentifierPieces();
}
