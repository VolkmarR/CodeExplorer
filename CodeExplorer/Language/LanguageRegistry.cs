using System.Collections.Frozen;

namespace CodeExplorer;

/// <summary>
///     Which analyser answers for a file, resolved from its extension. One of these holds the whole
///     set, so "what does this build support?" is a question about one object and not a walk of every
///     caller.
///     Two extension points meet here and they are deliberately different. A <em>new language</em> is
///     a new <see cref="LanguageProfile" /> in <see cref="Languages" />'s table. A <em>better
///     implementation for a language already covered</em> is a different
///     <see cref="ILanguageAnalyzer" /> laid over the same extensions with <see cref="With" /> — the
///     intended first use being C# through Roslyn and the C family through tree-sitter. Neither
///     touches a caller.
/// </summary>
public sealed class LanguageRegistry
{
    private readonly ILanguageAnalyzer _fallback;
    private readonly FrozenDictionary<string, ILanguageAnalyzer> _byExtension;

    /// <summary>
    ///     The registrations in the order they were made. Kept rather than recovered from
    ///     <see cref="_byExtension" />, whose order is a hash order: an overlay that claims some of a
    ///     profile's extensions leaves both in the map, and rebuilding from it would let the one that
    ///     lost take them back on the next <see cref="With" />.
    /// </summary>
    private readonly ILanguageAnalyzer[] _registrations;

    /// <param name="analyzers">In registration order; a later one wins the extensions it claims.</param>
    /// <param name="fallback">
    ///     What answers for an extension no analyser claims. It exists so that no caller has to hold a
    ///     null: an extension nobody declared gets a conservative answer, never no answer.
    /// </param>
    public LanguageRegistry(IEnumerable<ILanguageAnalyzer> analyzers, ILanguageAnalyzer fallback)
    {
        ArgumentNullException.ThrowIfNull(analyzers);
        ArgumentNullException.ThrowIfNull(fallback);
        _fallback = fallback;
        _registrations = [.. analyzers];
        var byExtension = new Dictionary<string, ILanguageAnalyzer>(StringComparer.OrdinalIgnoreCase);
        foreach (var analyzer in _registrations)
        foreach (string extension in analyzer.Extensions)
            byExtension[extension] = analyzer;
        _byExtension = byExtension.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        Claims =
        [
            .. byExtension.GroupBy(entry => entry.Value)
                .Select(group => new LanguageClaim(group.Key, [.. group.Select(entry => entry.Key).Order(
                    StringComparer.OrdinalIgnoreCase)]))
        ];
    }

    /// <summary>
    ///     One analyser and the extensions it actually answers for, after the overlays. What an
    ///     analyser <em>claims</em> and what it <em>won</em> differ once a second one is laid over some
    ///     of the same extensions, and a caller writing a query per language needs the second.
    /// </summary>
    public sealed record LanguageClaim(ILanguageAnalyzer Analyzer, IReadOnlyList<string> Extensions);

    /// <summary>
    ///     Every analyser in this set with the extensions it won, in no particular order. It exists for
    ///     the one caller that has to ask a question of every language at once —
    ///     <c>find_definition</c>, which narrows each file with its own language's candidate lines in a
    ///     single query — and not for a caller walking these to resolve one file, which is
    ///     <see cref="For" />'s job.
    /// </summary>
    public IReadOnlyList<LanguageClaim> Claims { get; }

    /// <summary>
    ///     What answers for an extension none of <see cref="Claims" /> covers. Named so that a caller
    ///     writing the per-language halves of one query can write the remainder too; everything else
    ///     reaches it through <see cref="For" /> without knowing it exists.
    /// </summary>
    public ILanguageAnalyzer Fallback => _fallback;

    /// <summary>
    ///     The same set with one more analyser laid over the extensions it claims. A new registry
    ///     rather than a mutation, so a test that registers a stub cannot leak it into another test
    ///     running beside it.
    /// </summary>
    public LanguageRegistry With(ILanguageAnalyzer analyzer)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        return new LanguageRegistry([.. _registrations, analyzer], _fallback);
    }

    /// <summary>
    ///     Who answers for this extension — the fallback where no profile covers it, never null. The
    ///     extension is spelled the way <c>files.extension</c> stores it, lowercase and without the
    ///     dot, and a leading dot is tolerated so a caller quoting a file name does not have to strip
    ///     it.
    /// </summary>
    public ILanguageAnalyzer For(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return _byExtension.GetValueOrDefault(extension.TrimStart('.'), _fallback);
    }
}
