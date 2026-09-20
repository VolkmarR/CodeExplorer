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
public sealed class IndexBuilder(
    IConfiguration configuration, HistoryBuilder history, OverviewBuilder overview, ImportBuilder imports)
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
    /// <param name="report">How far the build has got, for the status an operator polls.</param>
    /// <param name="cancellationToken">Checked between files, which is the granularity of the walk.</param>
    public async Task<IndexSummary> FillAsync(ShadowIndex shadow, IReadOnlyList<OpenedRepository> repositories,
        bool singleRepository, Action<RefreshProgress> report, CancellationToken cancellationToken)
    {
        // Every build is recorded here and nowhere else: a second caller gets the same span and the
        // same metrics by calling this, which is the only way to fill an index at all.
        using var recording = Telemetry.IndexBuild(shadow.Slug);

        // The tree walk and the appender are synchronous git and DuckDB calls; a worker thread
        // keeps them off the request thread, and the token is checked between files.
        var (files, lines) = await Task.Run(
            () => Ingest(shadow.Connection, shadow.Catalog, singleRepository, repositories, report,
                cancellationToken),
            cancellationToken);
        // After the whole walk and not inside it: a name resolves against every other file in the
        // project, and resolving as the files arrive would answer the first repository's edges
        // against half a project.
        await imports.ResolveAsync(shadow, cancellationToken);
        // After the files, because attribution is joined onto them and a file row is what says which
        // blobs are at HEAD; before CompleteAsync, because the index_info row means the build finished
        // and an index that is live with no history would be one nothing ever goes back to fill in.
        await history.FillAsync(shadow, repositories, report, cancellationToken);
        // After the history, because the overview ranks it, and before CompleteAsync for the same
        // reason the history runs before it: an index that went live without an overview is one
        // nothing would ever go back and fill in.
        // Reported rather than left under the attribution's label (#91): these are still step 3, and
        // what a refresh spent its wall clock on can only be read back if whatever spent it was named
        // while it ran.
        report(new RefreshProgress(RefreshProgress.HistoryStep, RefreshProgress.TotalStepCount,
            RefreshProgress.OverviewPhase));
        await overview.FillAsync(shadow, singleRepository, cancellationToken);
        await shadow.CompleteAsync(singleRepository, report, cancellationToken);

        recording.Built(files, lines);
        return new IndexSummary(repositories.Count, files, lines, []);
    }

    private (long Files, long Lines) Ingest(
        DuckDBConnection connection, string catalog, bool singleRepository,
        IReadOnlyList<OpenedRepository> repositories, Action<RefreshProgress> report,
        CancellationToken cancellationToken)
    {
        long fileId = 0, lineId = 0, importId = 0;
        // Appenders target the attached catalog explicitly; after USE they would resolve there too, but
        // naming it keeps the write independent of connection state. It is the shadow's catalog, which
        // is not the project slug — writing to the live one is exactly the mistake to make impossible.
        using var files = connection.CreateAppender(catalog, "main", "files");
        using var lines = connection.CreateAppender(catalog, "main", "lines");
        using var repos = connection.CreateAppender(catalog, "main", "repositories");
        using var imported = connection.CreateAppender(catalog, "main", "imports");

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
            long byteCount = 0;
            // Sorted by path so a repository's files, and each file's lines, are contiguous: the zone
            // maps then prune by repo_id and file_id without an index. Materialised anyway by the sort,
            // so the count is free and the step can say how far through the tree it is.
            var entries = clone.Files().OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Every 200 files rather than every file: the status is polled, not streamed, so a
                // finer grain would only cost dictionary writes nobody reads.
                if (fileCount % RefreshProgress.ReportEvery == 0)
                    report(new RefreshProgress(RefreshProgress.IngestStep, RefreshProgress.TotalStepCount,
                        $"Reading '{repository.Slug}' into the shadow index", fileCount, entries.Count));
                fileId++;
                fileCount++;
                string? skipReason = entry.IsBinary ? "binary" :
                    entry.Size > _maxFileBytes ? $"larger than {_maxFileBytes / 1024 / 1024} MiB" : null;
                var text = skipReason is null ? SplitLines(entry.Text()) : [];
                for (int i = 0; i < text.Count; i++)
                    lines.CreateRow().AppendValue(++lineId).AppendValue(fileId).AppendValue(i + 1).AppendValue(text[i])
                        // Attribution is filled by the history pass, which runs after this one and before
                        // the swap (ADR-0007). A null that survives it is a line the history could not
                        // attribute, which is a different answer from "nobody touched this line".
                        .AppendNullValue()
                        .EndRow();
                lineCount += text.Count;
                // Every file the tree holds, skipped ones included: the root of a tree listing says
                // how big a repository is, and a binary nobody indexed still takes up the disk.
                byteCount += entry.Size;

                int slash = entry.Path.LastIndexOf('/');
                string name = entry.Path[(slash + 1)..];
                // Derived once and used twice: it decides which analyser reads the file below and it
                // is what the `files` row stores, and two derivations of one rule can only ever
                // disagree by accident.
                string extension = Languages.ExtensionOf(name);

                // The second reading of the same lines, and the only one the build does: what this
                // file depends on, in the words it used. It costs a walk of the file's lines for the
                // languages whose profile declares import forms and nothing at all for the rest,
                // which is what keeps it inside the budget ADR-0003 sets.
                var found = imports.Read(Languages.Default.For(extension), text);
                foreach (var edge in found.Edges)
                    imported.CreateRow().AppendValue(++importId).AppendValue(fileId)
                        .AppendValue(edge.LineNumber).AppendValue(edge.Name)
                        .AppendValue(ImportColumns.Column(edge.Shape))
                        // target_file and unresolved are the resolution pass's, which runs once the
                        // whole project is in the shadow; a row that still holds two nulls is one it
                        // never reached.
                        .AppendNullValue().AppendNullValue()
                        .AppendValue(ImportColumns.Column(edge.Evidence)).EndRow();

                var row = files.CreateRow()
                    .AppendValue(fileId).AppendValue(repoId)
                    // The stored qualified path is what every read path answers with, so the shape is
                    // decided once, here: a single-repository project stores the short one (ADR-0006)
                    // and nothing downstream has to know which kind of project it is reading.
                    .AppendValue(entry.Path)
                    .AppendValue(paths.Format(repository.Slug, entry.Path))
                    .AppendValue(slash < 0 ? "" : entry.Path[..slash]).AppendValue(name)
                    .AppendValue(extension)
                    .AppendValue(entry.Size).AppendValue(text.Count);
                row = skipReason is null ? row.AppendNullValue() : row.AppendValue(skipReason);
                // The two commit columns are the history pass's, like lines.commit_id above.
                row = row.AppendNullValue().AppendNullValue();
                (found.Module is null ? row.AppendNullValue() : row.AppendValue(found.Module)).EndRow();
            }

            repos.CreateRow().AppendValue(repoId).AppendValue(repository.Slug).AppendValue(repository.Url)
                .AppendValue(clone.HeadSha).AppendValue(fileCount).AppendValue(lineCount)
                .AppendValue(byteCount).EndRow();
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
        int start = 0;
        // Sliced on the newline rather than appended a character at a time through a builder: this is
        // every byte of every file a build reads, and the builder was copying each one twice (#149).
        while (true)
        {
            int newline = content.IndexOf('\n', start);
            if (newline < 0) break;
            result.Add(Line(content.AsSpan(start, newline - start)));
            start = newline + 1;
        }

        // Whatever follows the last newline, where there is any. An empty tail is the ordinary case —
        // a file ending in a newline — and is not a line.
        if (start < content.Length && Line(content.AsSpan(start)) is { Length: > 0 } tail) result.Add(tail);
        return result;
    }

    /// <summary>
    ///     One line's text with its carriage returns dropped. The one at the end is the CRLF ending and
    ///     is the only one nearly any file has; a CR anywhere else is a lone CR (classic Mac), which is
    ///     dropped where it stands rather than broken on, because treating it as no break is the lesser
    ///     surprise. That is what this has always done, so a file indexed before this change and after
    ///     it holds the same lines — which is the whole licence for rewriting the loop above.
    /// </summary>
    private static string Line(ReadOnlySpan<char> text)
    {
        if (text.Length > 0 && text[^1] == '\r') text = text[..^1];
        // Replace answers with the same instance when it matches nothing, which is nearly every line
        // once the ending above is off, so the ordinary case allocates the string and no more.
        return new string(text).Replace("\r", "", StringComparison.Ordinal);
    }
}
