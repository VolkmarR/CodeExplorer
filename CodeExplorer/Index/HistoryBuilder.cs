using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>What a history pass added, for the log line and the refresh summary.</summary>
public sealed record HistorySummary(int Commits, long AttributedFiles);

/// <summary>
///     Fills a shadow index's history (CONTEXT.md): the commits of every repository, the paths each one
///     touched, and the attribution of every line at HEAD. It runs inside the ordinary build, before the
///     swap, so a project is never live with code at one commit and history at another — and so nothing
///     here ever writes to an index that is answering queries.
///     What it does not do is walk the whole history every time. <see cref="ProjectIndexes" /> has
///     already copied the live index's history into the shadow, so this appends what is new and blames
///     only the blobs that have no attribution yet, which for a refresh of an unchanged repository is
///     nothing at all (ADR-0007).
/// </summary>
public sealed class HistoryBuilder(ILogger<HistoryBuilder> logger)
{
    /// <summary>
    ///     Appends every repository's new commits and attributes the files at HEAD. The caller has
    ///     already written <c>files</c> and <c>lines</c>, which this needs: attribution is joined onto
    ///     them by blob hash, and a file row is what says which blobs are at HEAD at all.
    /// </summary>
    /// <param name="shadow">The shadow being built, its connection already bound to it.</param>
    /// <param name="repositories">The same opened copies the file walk read, in the same order.</param>
    /// <param name="cancellationToken">Checked per commit and per blamed file, which is where the time goes.</param>
    public async Task<HistorySummary> FillAsync(ShadowIndex shadow, IReadOnlyList<OpenedRepository> repositories,
        CancellationToken cancellationToken)
    {
        using var recording = Telemetry.HistoryBuild(shadow.Slug);

        // The walk, the diffs and the blames are synchronous git calls, like the file walk: a worker
        // thread keeps them off the request thread and the token is checked inside.
        var summary = await Task.Run(
            () => Fill(shadow.Connection, shadow.Catalog, repositories, cancellationToken),
            cancellationToken);

        recording.Built(summary.Commits, summary.AttributedFiles);
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "History of project {Project}: {Commits} new commits, {Files} files attributed",
                shadow.Slug, summary.Commits, summary.AttributedFiles);
        return summary;
    }

    private static HistorySummary Fill(DuckDBConnection connection, string catalog,
        IReadOnlyList<OpenedRepository> repositories, CancellationToken cancellationToken)
    {
        // A repository the operator removed since the last build left its commits in the carried-over
        // history. They are pruned before anything is appended, so the table holds exactly the
        // repositories this build read — and so a slug reused for a different remote cannot inherit
        // the old one's commits.
        Prune(connection, repositories, cancellationToken);

        int appended = 0;
        foreach (var (repository, copy) in repositories)
            appended += AppendCommits(connection, catalog, repository.Slug, copy, cancellationToken);

        long attributed = Attribute(connection, catalog, repositories, cancellationToken);
        Materialise(connection, cancellationToken);
        return new HistorySummary(appended, attributed);
    }

    private static void Prune(DuckDBConnection connection, IReadOnlyList<OpenedRepository> repositories,
        CancellationToken cancellationToken)
    {
        string slugs = string.Join(", ", repositories.Select(open => Literal(open.Repository.Slug)));
        // An empty list is a project whose every repository failed to open, which the refresh refuses
        // before it gets here; guarding anyway, because `IN ()` is a syntax error and not an empty set.
        string kept = slugs.Length == 0 ? "false" : $"repo_slug IN ({slugs})";
        // commit_files and attribution hang off commit_id, so they follow whatever commits keeps. The
        // delete is written as a subquery rather than a join because DuckDB's DELETE takes no USING.
        Execute(connection, $"DELETE FROM commit_files WHERE commit_id NOT IN (SELECT commit_id FROM commits WHERE {kept})",
            cancellationToken);
        Execute(connection, $"DELETE FROM attribution WHERE commit_id NOT IN (SELECT commit_id FROM commits WHERE {kept})",
            cancellationToken);
        Execute(connection, $"DELETE FROM commits WHERE NOT ({kept})", cancellationToken);
    }

    /// <summary>
    ///     Walks one repository back to the commits already recorded and writes what is new, oldest
    ///     first. The order is the point: <c>commit_id</c> is allocated in insertion order and never
    ///     renumbered (ADR-0007), so writing oldest first is what makes a higher id mean a later commit,
    ///     on a first build and on every increment alike. The walk hands them over newest first, so they
    ///     are buffered and reversed — which is also the memory ceiling of this class, one record per
    ///     new commit, paid once because a later refresh stops at the watermark.
    /// </summary>
    private static int AppendCommits(DuckDBConnection connection, string catalog, string slug, LocalCopy copy,
        CancellationToken cancellationToken)
    {
        var known = Strings(connection, $"SELECT sha FROM commits WHERE repo_slug = {Literal(slug)}",
            cancellationToken);
        var fresh = copy.History(known, cancellationToken).ToList();
        if (fresh.Count == 0) return 0;
        fresh.Reverse();

        int nextId = Scalar(connection, "SELECT coalesce(max(commit_id), 0) FROM commits", cancellationToken) + 1;
        // The shadow's catalog is named rather than left to USE, for the reason IndexBuilder names it:
        // writing to the live catalog is the mistake worth making impossible.
        using (var commits = connection.CreateAppender(catalog, "main", "commits"))
        using (var files = connection.CreateAppender(catalog, "main", "commit_files"))
        {
            foreach (var commit in fresh)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int id = nextId++;
                commits.CreateRow().AppendValue(id).AppendValue(slug).AppendValue(commit.Sha)
                    .AppendValue(commit.AuthorName).AppendValue(commit.AuthorEmail)
                    .AppendValue(commit.AuthoredAt).AppendValue(commit.Subject).AppendValue(commit.Body)
                    .EndRow();
                foreach (var change in commit.Files)
                    files.CreateRow().AppendValue(id).AppendValue(change.Path).AppendValue(change.ChangeKind)
                        .AppendValue(change.Added).AppendValue(change.Deleted).EndRow();
            }
        }

        return fresh.Count;
    }

    /// <summary>
    ///     Blames every file at HEAD whose blob has no attribution yet. Keying by blob hash is what makes
    ///     this cheap on a refresh: an unchanged file has the same content and therefore the same
    ///     attribution, which was carried over, so it is not blamed again — and a file that only moved
    ///     keeps its ranges without any rename detection (ADR-0007).
    ///     A file with a <c>skip_reason</c> has no lines to attribute and is not blamed; a blame that
    ///     fails is recorded as no attribution rather than as a failed build, because one unreadable file
    ///     must not cost a project its index.
    /// </summary>
    private static long Attribute(DuckDBConnection connection, string catalog,
        IReadOnlyList<OpenedRepository> repositories, CancellationToken cancellationToken)
    {
        // The commit a SHA belongs to, for turning a blame's SHA into the id the tables use. Read once:
        // a blame answers in SHAs and every range needs the lookup.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT sha, commit_id FROM commits";
            using var reader = command.ExecuteReader();
            while (reader.Read()) ids[reader.GetString(0)] = reader.GetInt32(1);
        }

        long blamed = 0;
        using var appender = connection.CreateAppender(catalog, "main", "attribution");
        int repoId = 0;
        foreach (var (_, copy) in repositories)
        {
            repoId++;
            // Only the blobs this repository has at HEAD, only those still unattributed, and each blob
            // once however many paths hold it — two identical files share one set of ranges.
            var pending = Pairs(connection,
                $"""
                 SELECT min(path), blob_sha FROM files
                 WHERE repo_id = {repoId} AND skip_reason IS NULL
                   AND blob_sha NOT IN (SELECT blob_sha FROM attribution)
                 GROUP BY blob_sha
                 """, cancellationToken);

            foreach ((string path, string sha) in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<AttributedRange> ranges;
                try
                {
                    ranges = copy.Attribution(path);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Safe to swallow: blame is the one part of a build that can fail on a single file
                    // — libgit2 refuses some histories it cannot follow — and the answer for that file
                    // is a null commit_id, which already means "not attributed" everywhere it is read.
                    continue;
                }

                foreach (var range in ranges)
                    if (ids.TryGetValue(range.Sha, out int id))
                        appender.CreateRow().AppendValue(sha).AppendValue(range.StartLine)
                            .AppendValue(range.EndLine).AppendValue(id).EndRow();
                // A SHA the walk never recorded is a commit off the first-parent line — a merge brought
                // the line in from a side branch. It attributes to nothing rather than to the merge,
                // which would name a commit that did not write the line.

                blamed++;
            }
        }

        return blamed;
    }

    /// <summary>
    ///     Writes attribution onto the rows that answer queries: the commit of every line, and the first
    ///     and last commit of every file. Materialised rather than joined at read time, the same
    ///     reasoning <c>files.qualified_path</c> follows (ADR-0003) — with the difference that this one
    ///     also collapses a range join into an equality, which is what keeps a grep with history the same
    ///     shape as a grep without one.
    /// </summary>
    private static void Materialise(DuckDBConnection connection, CancellationToken cancellationToken)
    {
        Execute(connection,
            """
            UPDATE lines SET commit_id = a.commit_id
            FROM files f, attribution a
            WHERE lines.file_id = f.file_id AND a.blob_sha = f.blob_sha
              AND lines.line_number BETWEEN a.start_line AND a.end_line
            """, cancellationToken);

        // Bounded by commit_id and not by date: the ids ascend with history by construction, while an
        // author date is whatever the committer's clock said and goes backwards across a rebase.
        Execute(connection,
            """
            UPDATE files SET first_commit = h.first_commit, last_commit = h.last_commit
            FROM (SELECT c.repo_slug, cf.path, min(c.commit_id) AS first_commit, max(c.commit_id) AS last_commit
                  FROM commit_files cf JOIN commits c USING (commit_id)
                  GROUP BY c.repo_slug, cf.path) h
            JOIN repositories r ON r.slug = h.repo_slug
            WHERE files.repo_id = r.repo_id AND files.path = h.path
            """, cancellationToken);
    }

    private static void Execute(DuckDBConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        cancellationToken.ThrowIfCancellationRequested();
        command.ExecuteNonQuery();
    }

    private static int Scalar(DuckDBConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static HashSet<string> Strings(DuckDBConnection connection, string sql,
        CancellationToken cancellationToken)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = command.ExecuteReader();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    private static List<(string First, string Second)> Pairs(DuckDBConnection connection, string sql,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string, string)>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    /// <summary>
    ///     A slug as a SQL string literal. Slugs are validated on the way into the control database and
    ///     never come from a tool call, but they are still the one value here that did not come from this
    ///     process, so they are escaped rather than trusted.
    /// </summary>
    private static string Literal(string value) => $"'{value.Replace("'", "''")}'";
}
