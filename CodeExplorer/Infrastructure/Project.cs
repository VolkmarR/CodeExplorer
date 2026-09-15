namespace CodeExplorer;

/// <summary>
///     A project (CONTEXT.md): the stable slug agents address it by, and a display name that can
///     change freely.
///     It sits in <c>Infrastructure/</c> rather than in <c>Control/</c> even though the control
///     database is what stores it, because it is the one thing every module is handed —
///     <c>BoundProject</c> resolves it from the route, a handler declares it as a parameter and every
///     tool takes it from there. A record in <c>Control/</c> would make the module boundary test in
///     the test project fail for a type that crosses no boundary: it carries no secret, no connection
///     and no behaviour beyond reading itself off the request.
/// </summary>
/// <param name="Slug">The stable name agents address the project by; it never changes.</param>
/// <param name="Name">The display name, which can change freely.</param>
/// <param name="SingleRepository">
///     Declared at creation and read-only afterwards (ADR-0006): the project holds one repository and
///     names its files without a repository slug. Changing it either way would rename every file
///     agents have been quoting, so nothing updates it and a second repository is refused.
/// </param>
public sealed record Project(string Slug, string Name, bool SingleRepository)
{
    /// <summary>
    ///     How a minimal API handler receives the project its route named: declare a <c>Project</c>
    ///     parameter, and it is the one <c>BoundProject</c> resolved. On an endpoint no group bound
    ///     there is nothing to read and the parameter fails to bind, which is the loud answer wanted.
    /// </summary>
    public static ValueTask<Project?> BindAsync(HttpContext context) => ValueTask.FromResult(BoundProject.Bound(context));
}
