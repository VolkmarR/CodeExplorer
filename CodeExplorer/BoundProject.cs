using ModelContextProtocol;

namespace CodeExplorer;

/// <summary>
///     The project a request is bound to (ADR-0002). The route filter in <c>Program.cs</c> stores it
///     here; every MCP tool class reads it from here, so the binding rule lives in one place.
/// </summary>
internal static class BoundProject
{
    public const string ItemKey = "CodeExplorer.Project";

    /// <summary>
    ///     The Streamable HTTP transport runs each handler inside the HTTP request that carried it, so
    ///     the project the route filter resolved is on the current context. If the SDK ever dispatches
    ///     off-request this fails loudly rather than binding to nothing.
    /// </summary>
    public static Project Get(IHttpContextAccessor httpContextAccessor) =>
        httpContextAccessor.HttpContext?.Items[ItemKey] as Project
        ?? throw new McpException("No project is bound to this request. Connect through /projects/{slug}/mcp.");
}
