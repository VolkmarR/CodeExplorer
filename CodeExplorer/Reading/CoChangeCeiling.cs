namespace CodeExplorer.Reading;

/// <summary>
///     <c>History:MaxCommitPaths</c>, the most paths a commit may touch and still be paired. Read here
///     because two modules pair under it — the co-change tool and the overview page's folder coupling
///     card (#213) — and they must not drift apart into two settings or a setting and a constant.
/// </summary>
public static class CoChangeCeiling
{
    /// <summary>
    ///     The default. A reformat, a vendor drop or an initial import couples every path it touched to
    ///     every other, and those pairs are one commit rather than evidence about any file in it.
    ///     Two hundred is the judgement: high enough that a feature landing across a module still
    ///     counts as coupling, low enough that nothing a person wrote by hand in one sitting reaches
    ///     it. It is a setting and not a constant because what counts as a mass commit differs between
    ///     a repository of two hundred files and one of eighty thousand.
    /// </summary>
    private const int _default = 200;

    /// <summary>The configured ceiling, or the default.</summary>
    public static int From(IConfiguration configuration) =>
        configuration.GetValue("History:MaxCommitPaths", _default);
}
