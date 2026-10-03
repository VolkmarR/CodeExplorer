namespace CodeExplorer.Search;

/// <summary>
///     The parameter descriptions more than one tool declares, written once. They are agent-facing prose
///     (CODING_STANDARDS, Comments), and a copy edited in one tool and not in the others is how the
///     <c>commit</c> tool's description went stale. Each is the text every tool that takes the parameter
///     had already, so the schema an agent is sent is the same as before they were shared.
/// </summary>
internal static class ParameterText
{
    public const string Repository = "Repository slug to scope to. Default: every repository in the project.";

    public const string LookIn =
        "Only look in files whose qualified path matches; comma-separated terms are OR-ed. Same syntax as grep.";

    public const string Exclude =
        "Skip files whose qualified path matches any of these comma-separated terms, e.g. \"*.g.cs,/tests/\".";

    /// <summary>The file a history tool is asked about.</summary>
    public const string OneFile = "Qualified path of one file, e.g. \"main/src/Api/Foo.cs\".";

    /// <summary>The file a tool reads the index for.</summary>
    public const string TheFile = "Qualified path of the file, e.g. \"main/src/Api/Orders.cs\".";

    public const string Days = "Days back from the newest recorded commit, 1-3650. Default 90.";

    /// <summary>The folder or file a commit listing is narrowed to.</summary>
    public const string CommitScope =
        "Qualified path of a folder or a file to scope to, e.g. \"main/src/Api\" or \"main/src/Api/Orders.cs\". Matched by the path each commit recorded, so it begins where a file was last renamed. A path HEAD no longer holds still scopes, because history recorded it. Default: the whole project.";
}
