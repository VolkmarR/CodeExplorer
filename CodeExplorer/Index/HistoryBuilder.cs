using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>What a history pass added, for the log line and the refresh summary.</summary>
public sealed record HistorySummary(int Commits, long AttributedFiles);

/// <summary>
///     Fills a shadow index's history (CONTEXT.md): the commits of every repository, the paths each one
///     touched, and the attribution of every line at HEAD. It runs inside the ordinary build, before the
///     swap, so a project is never live with code at one commit and history at another — and so nothing
///     here ever writes to an index that is answering queries.
///     Attribution is not blamed, it is replayed. Each commit's edits are applied, oldest first, to a
///     per-file array of "which commit wrote this line"; after the last commit the arrays are the
///     attribution at HEAD. One pass over the patches the walk renders anyway, in place of a blame per
///     file that walked the same history once for every file (ADR-0007).
///     <see cref="ProjectIndexes" /> has already copied the live index's history into the shadow, so a
///     refresh appends the new commits and replays only those onto the attribution it was handed, which
///     for an unchanged repository is nothing at all.
/// </summary>
public sealed class HistoryBuilder(ILogger<HistoryBuilder> logger)
{
    /// <summary>
    ///     Appends every repository's new commits and attributes the files at HEAD. The caller has
    ///     already written <c>files</c> and <c>lines</c>, which this needs: attribution is written onto
    ///     them by path, and a file row is what says which paths are at HEAD at all.
    /// </summary>
    /// <param name="shadow">The shadow being built, its connection already bound to it.</param>
    /// <param name="repositories">The same opened copies the file walk read, in the same order.</param>
    /// <param name="report">How far the pass has got, for the status an operator polls.</param>
    /// <param name="cancellationToken">Checked per commit, which is where the time goes.</param>
    public async Task<HistorySummary> FillAsync(ShadowIndex shadow, IReadOnlyList<OpenedRepository> repositories,
        Action<RefreshProgress> report, CancellationToken cancellationToken)
    {
        using var recording = Telemetry.HistoryBuild(shadow.Slug);

        // The walk and the diffs are synchronous git calls, like the file walk: a worker thread keeps
        // them off the request thread and the token is checked inside.
        var summary = await Task.Run(
            () => Fill(shadow.Connection, shadow.Catalog, repositories, report, cancellationToken),
            cancellationToken);

        recording.Built(summary.Commits, summary.AttributedFiles);
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "History of project {Project}: {Commits} new commits, {Files} files attributed",
                shadow.Slug, summary.Commits, summary.AttributedFiles);
        return summary;
    }

    private static HistorySummary Fill(DuckDBConnection connection, string catalog,
        IReadOnlyList<OpenedRepository> repositories, Action<RefreshProgress> report,
        CancellationToken cancellationToken)
    {
        // A repository the operator removed since the last build left its commits in the carried-over
        // history. They are pruned before anything is appended, so the tables hold exactly the
        // repositories this build read — and so a slug reused for a different remote cannot inherit
        // the old one's commits or, worse, replay its own commits onto the old one's attribution.
        Prune(connection, repositories, cancellationToken);

        int appended = 0;
        long attributed = 0;
        foreach (var (repository, copy) in repositories)
        {
            var fresh = AppendCommits(connection, catalog, repository.Slug, copy, report, cancellationToken);
            appended += fresh.Count;
            attributed += Replay(connection, catalog, repository.Slug, fresh, report, cancellationToken);
        }

        report(new RefreshProgress(RefreshProgress.HistoryStep, RefreshProgress.TotalStepCount,
            "Writing attribution onto the lines"));
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
        // commit_files hangs off commit_id, so it follows whatever commits keeps. The delete is written
        // as a subquery rather than a join because DuckDB's DELETE takes no USING.
        Execute(connection, $"DELETE FROM commit_files WHERE commit_id NOT IN (SELECT commit_id FROM commits WHERE {kept})",
            cancellationToken);
        Execute(connection, $"DELETE FROM attribution WHERE NOT ({kept})", cancellationToken);
        Execute(connection, $"DELETE FROM commits WHERE NOT ({kept})", cancellationToken);
    }

    /// <summary>
    ///     Walks one repository back to the commits already recorded and writes what is new, oldest
    ///     first. The order is the point: <c>commit_id</c> is allocated in insertion order and never
    ///     renumbered (ADR-0007), so writing oldest first is what makes a higher id mean a later commit,
    ///     on a first build and on every increment alike. The walk hands them over newest first, so they
    ///     are buffered and reversed — which is also the memory ceiling of this class, one record per
    ///     new commit with its edits, paid once because a later refresh stops at the watermark.
    ///     Returns the new commits with the ids they were given, oldest first, for the replay.
    /// </summary>
    private static List<(int Id, RecordedCommit Commit)> AppendCommits(DuckDBConnection connection,
        string catalog, string slug, LocalCopy copy, Action<RefreshProgress> report,
        CancellationToken cancellationToken)
    {
        var known = Strings(connection, $"SELECT sha FROM commits WHERE repo_slug = {Literal(slug)}",
            cancellationToken);
        var fresh = new List<RecordedCommit>();
        foreach (var commit in copy.History(known, cancellationToken))
        {
            // The walk is where a first import spends its minutes, and how long it is cannot be known
            // before it ends, so the count runs without a total rather than against an invented one.
            if (fresh.Count % RefreshProgress.ReportEvery == 0)
                report(new RefreshProgress(RefreshProgress.HistoryStep, RefreshProgress.TotalStepCount,
                    $"Reading the history of '{slug}'", fresh.Count));
            fresh.Add(commit);
        }

        var numbered = new List<(int, RecordedCommit)>(fresh.Count);
        if (fresh.Count == 0) return numbered;
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
                numbered.Add((id, commit));
                commits.CreateRow().AppendValue(id).AppendValue(slug).AppendValue(commit.Sha)
                    .AppendValue(commit.AuthorName).AppendValue(commit.AuthorEmail)
                    .AppendValue(commit.AuthoredAt).AppendValue(commit.Subject).AppendValue(commit.Body)
                    .EndRow();
                foreach (var change in commit.Files)
                    files.CreateRow().AppendValue(id).AppendValue(change.Path).AppendValue(change.ChangeKind)
                        .AppendValue(change.Added).AppendValue(change.Deleted).EndRow();
            }
        }

        return numbered;
    }

    /// <summary>
    ///     Replays the new commits' edits onto the repository's attribution and writes the result back.
    ///     The carried-over <c>attribution</c> rows are the state as of the last recorded commit, so a
    ///     refresh loads them and applies only what is new. When the oldest new commit has no parent the
    ///     walk went back to a root — a first build, or a history rewritten under the watermark — and the
    ///     state starts empty instead, because whatever was carried over describes a tree no commit in
    ///     this walk descends from.
    ///     A file whose edits do not fit the lines the state has for it is dropped rather than guessed
    ///     at, and stays unattributed until a rebuild; one file the replay cannot follow must not cost a
    ///     project its history. Returns how many files hold attribution afterwards.
    /// </summary>
    private static long Replay(DuckDBConnection connection, string catalog, string slug,
        List<(int Id, RecordedCommit Commit)> fresh, Action<RefreshProgress> report,
        CancellationToken cancellationToken)
    {
        if (fresh.Count == 0)
            return Scalar(connection,
                $"SELECT count(DISTINCT path) FROM attribution WHERE repo_slug = {Literal(slug)}", cancellationToken);

        var state = fresh[0].Commit.ParentSha is null
            ? new Dictionary<string, List<int>>(StringComparer.Ordinal)
            : LoadState(connection, slug, cancellationToken);

        int done = 0;
        foreach (var (id, commit) in fresh)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (done++ % RefreshProgress.ReportEvery == 0)
                report(new RefreshProgress(RefreshProgress.HistoryStep, RefreshProgress.TotalStepCount,
                    $"Attributing the lines of '{slug}'", done, fresh.Count));
            foreach (var change in commit.Files) Apply(state, change, id);
        }

        Execute(connection, $"DELETE FROM attribution WHERE repo_slug = {Literal(slug)}", cancellationToken);
        using var appender = connection.CreateAppender(catalog, "main", "attribution");
        foreach (var (path, lines) in state)
        {
            // Runs, not lines: attribution is run-structured by construction, and the table is read
            // back the same way. A zero is a line no commit in the walk wrote — a carried-over gap —
            // and is left out rather than written as a commit that does not exist.
            int start = 0;
            for (int end = 1; end <= lines.Count; end++)
            {
                if (end < lines.Count && lines[end] == lines[start]) continue;
                if (lines[start] != 0)
                    appender.CreateRow().AppendValue(slug).AppendValue(path).AppendValue(start + 1)
                        .AppendValue(end).AppendValue(lines[start]).EndRow();
                start = end;
            }
        }

        return state.Count;
    }

    /// <summary>
    ///     One path of one commit onto the state. A deletion forgets the path; a rename moves its lines
    ///     to the new path first; a binary file has no lines to hold. Then the edits, applied in one
    ///     forward pass: the lines an edit removes are dropped, the lines it adds are this commit's, and
    ///     everything between edits is copied as it was.
    /// </summary>
    private static void Apply(Dictionary<string, List<int>> state, ChangedPath change, int commitId)
    {
        switch (change.ChangeKind)
        {
            case "deleted":
                state.Remove(change.Path);
                return;
            case "renamed":
                if (state.Remove(change.OldPath, out var moved)) state[change.Path] = moved;
                break;
            case "copied":
                if (state.TryGetValue(change.OldPath, out var source)) state[change.Path] = [..source];
                break;
        }

        if (change.IsBinary)
        {
            state.Remove(change.Path);
            return;
        }

        if (change.Edits.Count == 0) return;
        var old = state.GetValueOrDefault(change.Path) ?? [];
        var replayed = new List<int>(Math.Max(0, old.Count + change.Added - change.Deleted));
        int position = 0;
        foreach (var edit in change.Edits)
        {
            if (edit.OldLine < position || edit.OldLine + edit.Deleted > old.Count)
            {
                // The edit names lines the state does not have: the carried-over attribution and this
                // commit disagree about what the file looked like. Dropped, per the class summary.
                state.Remove(change.Path);
                return;
            }

            while (position < edit.OldLine) replayed.Add(old[position++]);
            position += edit.Deleted;
            for (int added = 0; added < edit.Added; added++) replayed.Add(commitId);
        }

        while (position < old.Count) replayed.Add(old[position++]);
        state[change.Path] = replayed;
    }

    /// <summary>
    ///     The carried-over attribution of one repository, expanded from runs to one entry per line.
    ///     A gap between runs — lines a build could not attribute — comes back as zeros, which no
    ///     commit_id is, so the replay carries the gap along and the writer leaves it out again.
    /// </summary>
    private static Dictionary<string, List<int>> LoadState(DuckDBConnection connection, string slug,
        CancellationToken cancellationToken)
    {
        var state = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT path, start_line, end_line, commit_id FROM attribution WHERE repo_slug = {Literal(slug)} ORDER BY path, start_line";
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string path = reader.GetString(0);
            int start = reader.GetInt32(1), end = reader.GetInt32(2), commitId = reader.GetInt32(3);
            if (!state.TryGetValue(path, out var lines)) state[path] = lines = [];
            while (lines.Count < start - 1) lines.Add(0);
            for (int line = start; line <= end; line++) lines.Add(commitId);
        }

        return state;
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
            FROM files f, repositories r, attribution a
            WHERE lines.file_id = f.file_id AND r.repo_id = f.repo_id
              AND a.repo_slug = r.slug AND a.path = f.path
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

    /// <summary>
    ///     A slug as a SQL string literal. Slugs are validated on the way into the control database and
    ///     never come from a tool call, but they are still the one value here that did not come from this
    ///     process, so they are escaped rather than trusted.
    /// </summary>
    private static string Literal(string value) => $"'{value.Replace("'", "''")}'";
}
