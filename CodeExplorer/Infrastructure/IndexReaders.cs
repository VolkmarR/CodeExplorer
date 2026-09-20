namespace CodeExplorer;

/// <summary>
///     The one way into a project's index from outside <c>Index/</c>. Every reader — the MCP tools, the
///     operator UI's endpoints and the three text searches — is handed this and asks it to open an
///     index for the length of one call; the open, the refusal and the dispose are one contract, and it
///     lives here because twenty-four callers had each spelled it out: a second copy of it is a second
///     place for it to be got wrong, and a reader kept past the call is what holds up a swap (ADR-0003).
///     It is a class rather than a set of static methods, and injected rather than reached for, because
///     the alternative is what it replaced: static combinators taking <c>ProjectIndexes</c> as their
///     first argument, which made nine <c>Search/</c> files plus <c>Control/</c> and <c>Operator/</c>
///     name the attach-and-lease type — and <c>Control/</c> naming it closed a cycle with the
///     <c>Index/</c> → <c>Control/</c> arrow ADR-0005 allows. This file is now the only one in
///     <c>Infrastructure/</c> that names a type from <c>Index/</c>, which is the single arrow ADR-0005
///     grants it, and the module boundary test holds it to exactly that.
/// </summary>
public sealed class IndexReaders(ProjectIndexes indexes)
{
    /// <summary>
    ///     Opens the project's index for one call and hands it to <paramref name="read" />, or hands
    ///     <paramref name="refused" /> the <see cref="Problem" /> saying why it could not: never built,
    ///     or a <c>repo</c> that names no repository in it. A semantic failure is an answer, never an
    ///     exception (CODING_STANDARDS), and every reader — MCP tool, operator endpoint, search — is
    ///     refused in one wording rather than eleven.
    ///     The index is restored from its durable copy first when a replica's disk lost it (#9).
    ///     <paramref name="repository" /> is the caller's <c>repo</c> argument, narrowed by
    ///     <see cref="IndexReader.ScopeToAsync" />; null or blank covers every repository.
    /// </summary>
    public async Task<T> OverIndexAsync<T>(string projectSlug, string? repository,
        Func<IndexReader, CancellationToken, Task<T>> read, Func<Problem, T> refused,
        CancellationToken cancellationToken)
    {
        var lease = await indexes.OpenAsync(projectSlug, cancellationToken);
        if (lease is null) return refused(new Problem(IndexReader.NoIndex(projectSlug), ProblemKind.NoIndex));

        using var reader = new IndexReader(lease.Connection, lease.FullTextLoaded, lease, projectSlug);
        if (await reader.ScopeToAsync(repository, cancellationToken) is { } unknown) return refused(unknown);

        return await read(reader, cancellationToken);
    }

    /// <summary>The same for a caller whose answer is an <see cref="Outcome" />, which a problem already is.</summary>
    public Task<Outcome> OverIndexAsync(string projectSlug, string? repository,
        Func<IndexReader, CancellationToken, Task<Outcome>> read, CancellationToken cancellationToken) =>
        OverIndexAsync(projectSlug, repository, read, problem => problem, cancellationToken);

    /// <summary>
    ///     Opens the project's index, resolves <paramref name="path" /> the way
    ///     <see cref="IndexReader.LocateAsync" /> does, and hands <paramref name="read" /> the file — or
    ///     <paramref name="refused" /> the sentence saying why there is none. Every tool that takes one
    ///     file begins this way.
    /// </summary>
    public Task<T> OverFileAsync<T>(string projectSlug, string path, bool suggestions,
        Func<IndexReader, IndexedFile, CancellationToken, Task<T>> read, Func<Problem, T> refused,
        CancellationToken cancellationToken) =>
        OverIndexAsync(projectSlug, null, async (index, token) =>
        {
            var (file, problem) = await index.LocateAsync(path, suggestions, token);
            return file is null ? refused(problem!) : await read(index, file, token);
        }, refused, cancellationToken);

    /// <summary>The same for a caller whose answer is an <see cref="Outcome" />.</summary>
    public Task<Outcome> OverFileAsync(string projectSlug, string path, bool suggestions,
        Func<IndexReader, IndexedFile, CancellationToken, Task<Outcome>> read, CancellationToken cancellationToken) =>
        OverFileAsync(projectSlug, path, suggestions, read, problem => problem, cancellationToken);

    /// <summary>
    ///     Opens the project's index, resolves <paramref name="path" /> the way
    ///     <see cref="IndexReader.LocateDirectoryAsync" /> does, and hands <paramref name="read" /> the
    ///     directory — or <paramref name="refused" /> the sentence saying why there is none.
    /// </summary>
    public Task<T> OverDirectoryAsync<T>(string projectSlug, string? path,
        Func<IndexReader, IndexedDirectory, CancellationToken, Task<T>> read, Func<Problem, T> refused,
        CancellationToken cancellationToken) =>
        OverIndexAsync(projectSlug, null, async (index, token) =>
        {
            var (directory, problem) = await index.LocateDirectoryAsync(path, token);
            return directory is null ? refused(problem!) : await read(index, directory, token);
        }, refused, cancellationToken);

    /// <summary>The same for a caller whose answer is an <see cref="Outcome" />.</summary>
    public Task<Outcome> OverDirectoryAsync(string projectSlug, string? path,
        Func<IndexReader, IndexedDirectory, CancellationToken, Task<Outcome>> read,
        CancellationToken cancellationToken) =>
        OverDirectoryAsync(projectSlug, path, read, problem => problem, cancellationToken);

    /// <summary>
    ///     What the index holds, for a reader describing the project rather than reading from it. Null
    ///     when there is no index, and null when the file exists but has no <c>index_info</c> row: the
    ///     row is written last, so its absence is what an interrupted build leaves behind, and reading
    ///     the half-built tables as the project would be a wrong answer shaped like a right one.
    /// </summary>
    /// <param name="projectSlug">The project to describe.</param>
    /// <param name="restore">
    ///     Whether a project whose file is absent is restored from its durable copy first. True for a
    ///     read about one project, false for one that walks every project — the operator's list
    ///     touches all of them, and restoring every durable copy to draw one page is exactly what lazy
    ///     attach exists to avoid (#9). A project whose file the last shutdown wiped reads as not
    ///     indexed there until someone opens it.
    /// </param>
    /// <param name="cancellationToken">Threaded through the restore and the queries.</param>
    public async Task<IndexStatus?> StatusAsync(string projectSlug, bool restore,
        CancellationToken cancellationToken)
    {
        using var lease = restore
            ? await indexes.OpenAsync(projectSlug, cancellationToken)
            : await indexes.PeekAsync(projectSlug, cancellationToken);
        if (lease is null) return null;

        // epoch() hands back seconds as a double, which is the one representation of a TIMESTAMPTZ that
        // does not depend on whether the ICU extension is loaded to decide the session time zone.
        using var command = lease.Connection.Query(
            "SELECT epoch(built_at) AS built_seconds, fts_indexed, single_repository FROM index_info", []);
        using var reader = await command.ReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var builtAt = DateTimeOffset.FromUnixTimeSeconds((long)reader.Double("built_seconds"));
        bool ftsIndexed = reader.Flag("fts_indexed");
        bool singleRepository = reader.Flag("single_repository");
        return new IndexStatus(builtAt, ftsIndexed, singleRepository,
            await IndexReader.ReadRepositoriesAsync(lease.Connection, cancellationToken));
    }

    /// <summary>
    ///     Forgets a project's index entirely: detach it, delete the file, drop the durable copy. Not a
    ///     read, and here anyway, because deleting a project is the one other thing a module outside
    ///     <c>Index/</c> asks of an index — and a pass-through on this class is what lets
    ///     <c>Operator/</c> ask for it without naming the attach-and-lease type (ADR-0005).
    /// </summary>
    public Task DiscardAsync(string projectSlug, CancellationToken cancellationToken) =>
        indexes.DiscardAsync(projectSlug, cancellationToken);
}
