namespace CodeExplorer.Infrastructure;

/// <summary>
///     The one shape an API failure answers in, <c>{ "error": "…" }</c> under the matching status code
///     (CODING_STANDARDS, Errors). The middleware that answers before any handler runs — the route
///     binding, the origin check and the authentication challenges — wrote it out by hand four times,
///     and a fifth spelling of the property is a failure the web UI's <c>beforeError</c> hook cannot read.
/// </summary>
public static class ApiError
{
    /// <summary>Answers the request with <paramref name="error" /> from middleware, where no handler result is returned.</summary>
    public static Task WriteAsync(HttpResponse response, int statusCode, string error,
        CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        return response.WriteAsJsonAsync(new { error }, cancellationToken);
    }

    /// <summary>The same answer as a handler's result, for a refusal that carries its own status code.</summary>
    public static IResult Result(string error, int statusCode) => Results.Json(new { error }, statusCode: statusCode);
}
