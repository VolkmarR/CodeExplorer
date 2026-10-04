namespace CodeExplorer.Tests;

/// <summary>Patterns near RE2's program size limit, shared by the searches that must refuse them alike.</summary>
internal static class LargePatterns
{
    /// <summary>
    ///     A pattern RE2 compiles alone and refuses as too large once wrapped in whole-word boundaries,
    ///     with case folded, which is the default (#372): 87,200 repeated letters, where the bare form
    ///     stops fitting near 87,370 and the wrapped one near 86,980, measured against the RE2 that
    ///     DuckDB bundles. A DuckDB upgrade that changes RE2's size accounting moves both limits, and the
    ///     tests using this then fail on the bare or the wrapped assertion until it is measured again.
    ///     It requires a literal no file holds, so a search that does run it finds nothing; only the
    ///     multiline search skips the scan for it, because only it narrows by a required literal.
    /// </summary>
    public static readonly string WrappedTooLarge =
        "Unicorn" + string.Concat(Enumerable.Repeat("(?:[a-z]{1000})", 87)) + "[a-z]{200}";
}
