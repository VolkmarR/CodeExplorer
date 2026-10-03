namespace CodeExplorer.Infrastructure;

/// <summary>
///     The one shape an API failure answers in, <c>{ "error": "…" }</c> under the matching status code
///     (CODING_STANDARDS, Errors). The middleware that answers before any handler runs — the route
///     binding, the origin check and the authentication challenges — and the handlers after it wrote it
///     out by hand twenty times, and one more spelling of the property is a failure the web UI's
///     <c>beforeError</c> hook cannot read.
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
    public static IResult Result(int statusCode, string error) => Results.Json(new { error }, statusCode: statusCode);

    /// <summary>A handler's 400: the request asked the project something wrong.</summary>
    public static IResult BadRequest(string? error) => Results.BadRequest(new { error });

    /// <summary>A handler's 404: what the request names is not there.</summary>
    public static IResult NotFound(string error) => Results.NotFound(new { error });

    /// <summary>A handler's 409: what the request would create is there already.</summary>
    public static IResult Conflict(string error) => Results.Conflict(new { error });
}
