using System.Text;
using DuckDB.NET.Data;
using LibGit2Sharp;
using ModelContextProtocol;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CodeExplorer;

/// <summary>
///     What a build produced. <paramref name="Skipped" /> names every repository left out and why, so
///     one bad repository does not fail the project and is not silently missing from it either.
/// </summary>
public sealed record IndexSummary(int Repositories, long Files, long Lines, IReadOnlyList<string> Skipped);

/// <summary>
///     Refreshes a project (CONTEXT.md): brings every local copy up to date, reads them into a shadow
///     index beside the live one, and swaps it in. Files come from the HEAD tree and content from
///     blobs (ADR-0003); there is no working copy to walk. The live index answers every query
///     throughout, so a project is never searchable in a half-built state.
/// </summary>
public sealed class IndexBuilder(
    IConfiguration configuration,
    ControlDatabase control,
    GitClones clones,
    ProjectIndexes indexes,
    ILogger<IndexBuilder> logger)
{
    /// <summary>
    ///     Default for <c>Index:MaxFileBytes</c>. Text blobs above it are generated code, data dumps or
    ///     vendored bundles far more often than source, and one such file adds enough lines to swamp
    ///     BM25 ranking and grep output for the whole project. The file still appears in <c>files</c> with
    ///     the reason, so a tree listing and a search can tell the agent about it. A project of large
    ///     hand-written sources raises the setting rather than losing them.
    /// </summary>
    private const long DefaultMaxFileBytes = 4 * 1024 * 1024;

    /// <summary>
    ///     What <see cref="RefreshAsync" /> reports while it reads the repositories, and while it puts
    ///     the result in place. Constants rather than literals at the call site: the status endpoint
    ///     hands this prose to an operator, and a test waiting for a phase must not be waiting on a
    ///     wording that a rewrite of the sentence quietly breaks.
    /// </summary>
    public const string IngestPhase = "Reading the repositories into the shadow index";

    public const string SwapPhase = "Swapping the new index in";

    private readonly long _maxFileBytes = configuration.GetValue("Index:MaxFileBytes", DefaultMaxFileBytes);

    /// <param name="project">The project to rebuild, as the control database holds it.</param>
    /// <param name="report">
    ///     Called with the phase the refresh has reached, for the status endpoint the web UI and an
    ///     external cron poll. Synchronous, so a status read straight after a phase change sees it.
    /// </param>
    /// <param name="cancellationToken">Threaded through the fetch, the ingest and the swap.</param>
    public async Task<IndexSummary> RefreshAsync(Project project, Action<string> report,
        CancellationToken cancellationToken)
    {
        var repositories = await control.ListRepositoriesAsync(project.Slug, cancellationToken);
        var skipped = new List<string>();
        var opened = new List<(ProjectRepository Repository, Repository Clone)>();
        try
        {
            // Fetch before touching the index, so a fetch failure leaves the previous index serving.
            int fetched = 0;
            foreach (var repository in repositories)
            {
                report($"Fetching '{repository.Slug}' ({++fetched} of {repositories.Count})");
                Repository? clone = null;
                try
                {
                    clone = await clones.OpenRefreshedAsync(repository, cancellationToken);
                    string? reason = !GitClones.HasCommits(clone)
                        ? $"Repository '{repository.Slug}' has no commits yet."
                        : clones.DeclaresLfs(clone)
                            ? $"Repository '{repository.Slug}': {GitClones.LfsRefusal}"
                            : null;
                    if (reason is null)
                    {
                        opened.Add((repository, clone));
                        clone = null;
                    }
                    else
                    {
                        skipped.Add(reason);
                    }
                }
                catch (McpException ex)
                {
                    // Safe to swallow: the reason is reported in the summary in place of the repository,
                    // and the other repositories still get indexed.
                    skipped.Add(ex.Message);
                }
                finally
                {
                    // Still set when the clone was refused or the LFS scan threw; the list owns the rest.
                    clone?.Dispose();
                }
            }

            // Every repository failed, so the shadow would be an empty index and the swap would throw
            // the project's whole searchable history away over what is usually a transient network
            // fault. Leaving the old index serving, and saying so, is the answer an operator can act on.
            // InvalidOperationException and not McpException: no MCP tool is on this path, and the
            // refresh reports a failure through its status, which is where this message ends up.
            if (repositories.Count > 0 && opened.Count == 0)
                throw new InvalidOperationException(
                    $"No repository of project '{project.Slug}' could be read, so its index was left as it was: "
                    + string.Join(" ", skipped));

            long files, lines;
            bool fts;
            try
            {
                report(IngestPhase);
                // Scoped so the shadow connection is closed before the swap: the file cannot be moved
                // over the live one while the instance still holds it open.
                using (var shadow = await indexes.CreateShadowAsync(project.Slug, cancellationToken))
                {
                    // The tree walk and the appender are synchronous libgit2 and DuckDB calls; a worker thread
                    // keeps them off the request thread, and the token is checked between files.
                    (files, lines) = await Task.Run(
                        () => Ingest(shadow.Connection, shadow.Catalog, project.SingleRepository, opened,
                            cancellationToken), cancellationToken);
                    fts = await indexes.CompleteBuildAsync(shadow.Connection, project.SingleRepository,
                        cancellationToken);
                }

                report(SwapPhase);
                await indexes.SwapShadowAsync(project.Slug, cancellationToken);
            }
            catch
            {
                // The live index is untouched and still serving; only the half-written shadow goes.
                await indexes.DiscardShadowAsync(project.Slug, CancellationToken.None);
                throw;
            }

            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "Refreshed project {Project}: {Repositories} repositories, {Files} files, {Lines} lines, full-text {Fts}",
                    project.Slug, opened.Count, files, lines, fts);
            return new IndexSummary(opened.Count, files, lines, skipped);
        }
        finally
        {
            foreach (var (_, clone) in opened) clone.Dispose();
        }
    }

    private (long Files, long Lines) Ingest(
        DuckDBConnection connection, string catalog, bool singleRepository,
        IReadOnlyList<(ProjectRepository Repository, Repository Clone)> repositories,
        CancellationToken cancellationToken)
    {
        long fileId = 0, lineId = 0;
        // Appenders target the attached catalog explicitly; after USE they would resolve there too, but
        // naming it keeps the write independent of connection state. It is the shadow's catalog, which
        // is not the project slug — writing to the live one is exactly the mistake to make impossible.
        using var files = connection.CreateAppender(catalog, "main", "files");
        using var lines = connection.CreateAppender(catalog, "main", "lines");
        using var repos = connection.CreateAppender(catalog, "main", "repositories");

        int repoId = 0;
        foreach (var (repository, clone) in repositories)
        {
            repoId++;
            int fileCount = 0;
            long lineCount = 0;
            // Sorted by path so a repository's files, and each file's lines, are contiguous: the zone
            // maps then prune by repo_id and file_id without an index.
            foreach (var entry in Blobs(clone.Head.Tip.Tree, "").OrderBy(e => e.Path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                fileId++;
                fileCount++;
                var blob = entry.Blob;
                string? skipReason = blob.IsBinary ? "binary" :
                    blob.Size > _maxFileBytes ? $"larger than {_maxFileBytes / 1024 / 1024} MiB" : null;
                var text = skipReason is null ? SplitLines(blob.GetContentText()) : [];
                for (int i = 0; i < text.Count; i++)
                    lines.CreateRow().AppendValue(++lineId).AppendValue(fileId).AppendValue(i + 1).AppendValue(text[i])
                        .EndRow();
                lineCount += text.Count;

                int slash = entry.Path.LastIndexOf('/');
                string name = entry.Path[(slash + 1)..];
                var row = files.CreateRow()
                    .AppendValue(fileId).AppendValue(repoId)
                    // The stored qualified path is what every read path answers with, so the shape is
                    // decided once, here: a single-repository project stores the short one (ADR-0006)
                    // and nothing downstream has to know which kind of project it is reading.
                    .AppendValue(entry.Path)
                    .AppendValue(singleRepository ? entry.Path : $"{repository.Slug}/{entry.Path}")
                    .AppendValue(slash < 0 ? "" : entry.Path[..slash]).AppendValue(name)
                    .AppendValue(Path.GetExtension(name).TrimStart('.').ToLowerInvariant())
                    .AppendValue(blob.Size).AppendValue(text.Count);
                if (skipReason is null) row.AppendNullValue().EndRow();
                else row.AppendValue(skipReason).EndRow();
            }

            repos.CreateRow().AppendValue(repoId).AppendValue(repository.Slug).AppendValue(repository.Url)
                .AppendValue(clone.Head.Tip.Sha).AppendValue(fileCount).AppendValue(lineCount).EndRow();
        }

        return (fileId, lineId);
    }

    /// <summary>Every blob under the tree with its repository-relative path. Submodules are not files and are left out.</summary>
    private static IEnumerable<(string Path, Blob Blob)> Blobs(Tree tree, string prefix)
    {
        foreach (var entry in tree)
            switch (entry.TargetType)
            {
                case TreeEntryTargetType.Blob:
                    yield return (prefix + entry.Name, (Blob)entry.Target);
                    break;
                case TreeEntryTargetType.Tree:
                    foreach (var child in Blobs((Tree)entry.Target, prefix + entry.Name + "/")) yield return child;
                    break;
            }
    }

    /// <summary>
    ///     Splits on LF and drops a CR before it, so CRLF files index like LF files. A trailing newline
    ///     ends the last line rather than starting an empty one, matching how editors count lines.
    /// </summary>
    private static List<string> SplitLines(string content)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (char c in content)
            if (c == '\n')
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else if (c != '\r')
            {
                // A CR is dropped wherever it stands: before an LF it is the CRLF ending, and a lone CR
                // (classic Mac) is rare enough that treating it as no break is the lesser surprise.
                current.Append(c);
            }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }
}
