using System.Text;
using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     What a build produced. <paramref name="Skipped" /> names every repository left out and why, so
///     one bad repository does not fail the project and is not silently missing from it either.
/// </summary>
public sealed record IndexSummary(int Repositories, long Files, long Lines, IReadOnlyList<string> Skipped);

/// <summary>One repository of a project and the open local copy a build reads it from.</summary>
public sealed record OpenedRepository(ProjectRepository Repository, LocalCopy LocalCopy);

/// <summary>
///     Reads open local copies into an index, which is all this module does: the files come as
///     <see cref="LocalCopy" /> hands them out — committed at HEAD, with no working copy behind them
///     (ADR-0003) — and nothing here knows what git library read them. Fetching the copies and deciding
///     what becomes of the result belong to a refresh and live in <c>Refresh/</c> (ADR-0005).
/// </summary>
public sealed class IndexBuilder(IConfiguration configuration)
{
    /// <summary>
    ///     Default for <c>Index:MaxFileBytes</c>. Text blobs above it are generated code, data dumps or
    ///     vendored bundles far more often than source, and one such file adds enough lines to swamp
    ///     BM25 ranking and grep output for the whole project. The file still appears in <c>files</c> with
    ///     the reason, so a tree listing and a search can tell the agent about it. A project of large
    ///     hand-written sources raises the setting rather than losing them.
    /// </summary>
    private const long DefaultMaxFileBytes = 4 * 1024 * 1024;

    private readonly long _maxFileBytes = configuration.GetValue("Index:MaxFileBytes", DefaultMaxFileBytes);

    /// <summary>
    ///     Fills a shadow index from the open clones and finishes the build. The caller owns the shadow
    ///     and decides what becomes of it, which is what keeps this a build and not a refresh.
    /// </summary>
    /// <param name="shadow">The index to write into, created by the caller and disposed by it.</param>
    /// <param name="repositories">The clones to read, in the order their repositories are to be numbered.</param>
    /// <param name="singleRepository">How this project names its files (ADR-0006), recorded in the index.</param>
    /// <param name="cancellationToken">Checked between files, which is the granularity of the walk.</param>
    public async Task<IndexSummary> FillAsync(ShadowIndex shadow, IReadOnlyList<OpenedRepository> repositories,
        bool singleRepository, CancellationToken cancellationToken)
    {
        // Every build is recorded here and nowhere else: a second caller gets the same span and the
        // same metrics by calling this, which is the only way to fill an index at all.
        using var recording = Telemetry.IndexBuild(shadow.Slug);

        // The tree walk and the appender are synchronous git and DuckDB calls; a worker thread
        // keeps them off the request thread, and the token is checked between files.
        var (files, lines) = await Task.Run(
            () => Ingest(shadow.Connection, shadow.Catalog, singleRepository, repositories, cancellationToken),
            cancellationToken);
        await shadow.CompleteAsync(singleRepository, cancellationToken);

        recording.Built(files, lines);
        return new IndexSummary(repositories.Count, files, lines, []);
    }

    private (long Files, long Lines) Ingest(
        DuckDBConnection connection, string catalog, bool singleRepository,
        IReadOnlyList<OpenedRepository> repositories, CancellationToken cancellationToken)
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
            // The naming rule itself lives in ProjectPaths, which is also what every read path parses
            // with. Spelling it out here as well is how the two halves drift: a change to how a
            // single-repository project names its files would have to be made in both (ADR-0006).
            var paths = new ProjectPaths(singleRepository, repository.Slug);
            int fileCount = 0;
            long lineCount = 0;
            // Sorted by path so a repository's files, and each file's lines, are contiguous: the zone
            // maps then prune by repo_id and file_id without an index.
            foreach (var entry in clone.Files().OrderBy(e => e.Path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                fileId++;
                fileCount++;
                string? skipReason = entry.IsBinary ? "binary" :
                    entry.Size > _maxFileBytes ? $"larger than {_maxFileBytes / 1024 / 1024} MiB" : null;
                var text = skipReason is null ? SplitLines(entry.Text()) : [];
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
                    .AppendValue(paths.Format(repository.Slug, entry.Path))
                    .AppendValue(slash < 0 ? "" : entry.Path[..slash]).AppendValue(name)
                    .AppendValue(Path.GetExtension(name).TrimStart('.').ToLowerInvariant())
                    .AppendValue(entry.Size).AppendValue(text.Count);
                if (skipReason is null) row.AppendNullValue().EndRow();
                else row.AppendValue(skipReason).EndRow();
            }

            repos.CreateRow().AppendValue(repoId).AppendValue(repository.Slug).AppendValue(repository.Url)
                .AppendValue(clone.HeadSha).AppendValue(fileCount).AppendValue(lineCount).EndRow();
        }

        return (fileId, lineId);
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
