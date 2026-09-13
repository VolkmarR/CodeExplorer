namespace CodeExplorer;

/// <summary>
///     A project (CONTEXT.md): the stable slug agents address it by, and a display name that can
///     change freely.
///     It sits at the root rather than in <c>Control/</c> even though the control database is what
///     stores it, because it is the one thing every module is handed — <c>BoundProject</c> resolves
///     it from the route and every tool takes it from there. A record in <c>Control/</c> would make
///     the module boundary test in the test project fail for a type that crosses no boundary: it
///     carries no secret, no connection and no behaviour.
/// </summary>
public sealed record Project(string Slug, string Name);
