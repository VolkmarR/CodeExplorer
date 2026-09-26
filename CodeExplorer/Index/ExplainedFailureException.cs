namespace CodeExplorer.Index;

/// <summary>
///     A failure whose message was written for whoever reads the refresh status: it names the project
///     or repository, says what to do, and carries no path on the server's disk. The refresh reports
///     it in its own words; any other exception it reports only as the phase it failed in, because
///     a message nobody wrote for that reader — .NET's or DuckDB's — names the files it could not
///     write (#262). It lives here and not in <c>Refresh/</c> for the reason
///     <see cref="RefreshProgress" /> does: the index throws half of them, and cannot depend on the
///     refresh that reports them. An <see cref="InvalidOperationException" />, so a catch written for
///     one still matches; not an <c>McpException</c>, because no MCP tool is on these paths. A failure
///     on a path an agent's tool call shares, such as the swap and restore refusals and the restore
///     under a read (#291), throws an <c>McpException</c> instead. The refresh reports those in their
///     own words as well.
/// </summary>
public sealed class ExplainedFailureException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
