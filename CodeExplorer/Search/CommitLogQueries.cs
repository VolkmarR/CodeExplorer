using System.Data.Common;
using CodeExplorer.Infrastructure;
using CodeExplorer.Reading;
using DuckDB.NET.Data;

namespace CodeExplorer.Search;

/// <summary>One commit as a tool reports it: enough to name it and to say who and when, and no body.</summary>
public sealed record RecordedChange(
    string Sha,
    string RepositorySlug,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    string Subject);

/// <summary>
///     One commit as the change log lists it: a <see cref="RecordedChange" /> with its message body and
///     what it did to the tree, summed from <c>commit_files</c>. The sums are the commit's own added and
///     removed lines, so a reformat and a one-line fix read differently at a glance.
/// </summary>
public sealed record LoggedCommit(
    string Sha,
    string RepositorySlug,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    string Subject,
    string Body,
    int FilesChanged,
    int Added,
    int Deleted);

/// <summary>Everything a page of the log asks for. A null repository covers every one in the project.</summary>
public sealed record LogRequest(string? Repository, int Limit, int Page, string? Author = null,
    string? Message = null, string? Path = null);

/// <summary>
///     What an <c>author</c> filter matched, carried beside the page it narrowed so a reply can say
///     what it filtered by rather than presenting a narrowed log as the log.
///     <see cref="Matched" /> is empty for a filter that matched nobody, which is a different answer
///     from an empty page: the first says the address is wrong, the second that the page is past the
///     end (CODING_STANDARDS, Errors). A substring can match two addresses, so they are listed and not
///     collapsed into the text the caller supplied.
/// </summary>
/// <param name="Query">What the caller asked for, quoted back.</param>
/// <param name="Commits">Commits by everyone matched, in scope — the total the page walks.</param>
/// <param name="Addresses">How many addresses matched, which <see cref="Matched" /> may be cut short of.</param>
/// <param name="Matched">The matched addresses, most commits first, capped like any other listing.</param>
/// <param name="AuthorsInScope">How many authors there are, which is what a miss is read against.</param>
public sealed record AuthorFilter(string Query, long Commits, long Addresses,
    IReadOnlyList<RecordedAuthor> Matched, long AuthorsInScope);

/// <summary>Everything a listing of a project's authors asks for.</summary>
public sealed record AuthorsRequest(string? Repository, int Limit, string? Path = null);

/// <summary>
///     The authors of a project or one repository, most commits first. <see cref="Total" /> is how
///     many there are in scope, so a page cut at <see cref="Limit" /> says what it left out instead of
///     reading as the whole list. <see cref="HasHistory" /> is carried for the reason
///     <see cref="LogAnswer" /> carries it.
/// </summary>
public sealed record AuthorsAnswer(
    bool HasHistory,
    IndexedRepository? Repository,
    long Total,
    int Limit,
    IReadOnlyList<RecordedAuthor> Authors,
    PathScope? Path = null) : Outcome;

/// <summary>
///     A page of commits, newest first. <see cref="HasHistory" /> is the project's and not the page's:
///     an empty page of a project that has history means the page is past the end, and an empty page of
///     one that has none means nothing was ever imported — opposite claims, and the reason the flag is
///     carried rather than inferred from the count (CONTEXT.md, History). <see cref="Limit" /> and
///     <see cref="Page" /> are what the read actually used, after clamping, so a reply can say where it
///     stood without clamping a second time.
///     <see cref="Message" /> is the subject text the page was narrowed by, quoted back for the reason
///     <see cref="Author" /> is carried: a narrowed log must not introduce itself as the log. It has no
///     counts beside it, where the address filter has several — an address matching nobody is a
///     misspelling, worth measuring against the addresses that exist, and a subject nobody wrote is
///     just a subject nobody wrote.
/// </summary>
public sealed record LogAnswer(
    bool HasHistory,
    IndexedRepository? Repository,
    int Page,
    int Limit,
    IReadOnlyList<RecordedChange> Commits,
    AuthorFilter? Author = null,
    string? Message = null,
    PathScope? Path = null) : Outcome;

/// <summary>Everything a page of the change log asks for.</summary>
public sealed record ChangeLogRequest(string? Repository, int Page, int PageSize);

/// <summary>
///     A page of the change log with the body and sums of each commit, and <see cref="Total" /> so the
///     page can say how many there are. Zero is a project or repository without history rather than an
///     error, which the page draws as its own starting state.
/// </summary>
public sealed record ChangeLogAnswer(
    long Total,
    int Page,
    int PageSize,
    IReadOnlyList<LoggedCommit> Commits) : Outcome;

/// <summary>
///     The commit listings: <c>git_log</c>, <c>authors</c> and the operator's change log. All three
///     read the same <c>commits</c> rows through the same scope, and differ in what they group by and
///     in what they say about what they left out, so they are written beside each other rather than
///     beside the reads about one file.
/// </summary>
public sealed partial class HistoryQueries
{
    /// <summary>
    ///     A page of commits, newest first, scoped to one repository or covering every one. Whether the
    ///     project has history at all is read first and carried, because "no commit on this page" and
    ///     "no history was imported" are answered with opposite sentences.
    /// </summary>
    public Task<Outcome> LogAsync(string slug, LogRequest request, CancellationToken cancellationToken) =>
        Telemetry.Search(slug, _engine, () => readers.OverIndexAsync(slug, request.Repository, async (index, token) =>
        {
            var (path, unresolved) = await ScopeAsync(index, request.Path, token);
            if (unresolved is not null) return unresolved;

            bool hasHistory = await HasHistoryAsync(index, token);
            int limit = Math.Clamp(request.Limit, 1, _maxCommits);
            int page = Math.Max(1, request.Page);
            string? scope = index.Repository?.Slug;
            // Who the filter matched is read before the page is, so that a page which comes back empty
            // can be told apart from an address that matches nobody — and so the reply names the
            // addresses rather than the caller's own text, which may have matched two of them.
            // Whitespace is no filter: `author: ""` would otherwise become ILIKE '%%', match every
            // commit, and be reported as a filtered log — the unfiltered answer to a filtered question
            // this tool is careful about everywhere else (#86).
            string? asked = string.IsNullOrWhiteSpace(request.Author) ? null : request.Author.Trim();
            // Whitespace is no filter here for the same reason, and the failure would be the same
            // shape: ILIKE '%%' matches every subject, and the reply would introduce the whole log as
            // the commits that mention something.
            string? mentions = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim();
            var author = hasHistory && asked is not null
                ? await MatchedAsync(index, scope, asked, path, token)
                : null;
            var commits = hasHistory && author?.Addresses != 0
                ? await CommitsAsync(index, scope, asked, mentions, path, limit, (page - 1) * limit, token)
                : [];
            return new LogAnswer(hasHistory, index.Repository, page, limit, commits, author, mentions, path);
        }, cancellationToken), (LogAnswer answer) => new Telemetry.Measured(answer.Commits.Count, 0));

    /// <summary>
    ///     The authors of a project or of one repository, most commits first, with how many there are
    ///     in scope so a cut listing says what it left out.
    /// </summary>
    public Task<Outcome> AuthorsAsync(string slug, AuthorsRequest request, CancellationToken cancellationToken) =>
        Telemetry.Search(slug, _engine, () => readers.OverIndexAsync(slug, request.Repository, async (index, token) =>
        {
            var (path, unresolved) = await ScopeAsync(index, request.Path, token);
            if (unresolved is not null) return unresolved;

            bool hasHistory = await HasHistoryAsync(index, token);
            int limit = Math.Clamp(request.Limit, 1, _maxAuthors);
            string? scope = index.Repository?.Slug;
            var tally = hasHistory ? await AuthorsAsync(index, scope, null, path, limit, token) : AuthorTally.None;
            return new AuthorsAnswer(hasHistory, index.Repository, tally.Addresses, limit, tally.Authors, path);
        }, cancellationToken), (AuthorsAnswer answer) => new Telemetry.Measured(answer.Authors.Count, 0));

    /// <summary>
    ///     A page of the change log: the same commits with each one's body and the sums of what it did,
    ///     and the total in scope so a page can say how many there are.
    /// </summary>
    public Task<Outcome> ChangeLogAsync(string slug, ChangeLogRequest request,
        CancellationToken cancellationToken) =>
        Telemetry.Search(slug, _engine, () => readers.OverIndexAsync(slug, request.Repository, async (index, token) =>
        {
            int pageSize = Math.Clamp(request.PageSize, 1, _maxCommits);
            int page = Math.Max(1, request.Page);
            string? scope = index.Repository?.Slug;
            long total = await CommitCountAsync(index, scope, token);
            var commits = await LoggedAsync(index, scope, pageSize, (page - 1) * pageSize, token);
            return new ChangeLogAnswer(total, page, pageSize, commits);
        }, cancellationToken), (ChangeLogAnswer answer) => new Telemetry.Measured(answer.Commits.Count, 0));

    /// <summary>
    ///     The commits of a repository, newest first — or of every repository when none is named. A page
    ///     of history, ordered by <c>commit_id</c> because it ascends with history by construction while
    ///     an author date does not (ADR-0007).
    /// </summary>
    private static async Task<IReadOnlyList<RecordedChange>> CommitsAsync(IndexReader index, string? repositorySlug,
        string? author, string? message, PathScope? path, int limit, int skip, CancellationToken cancellationToken)
    {
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug, author, message,
            path?.RepositorySlug, path?.PathInRepository);
        await using var command = index.Connection.Query($"""
                                                          SELECT sha, repo_slug, author_name, author_email, authored_at,
                                                                 subject
                                                          FROM commits {scope}
                                                          ORDER BY commit_id DESC
                                                          LIMIT {limit} OFFSET {skip}
                                                          """, parameters);
        return await ChangesAsync(command, cancellationToken);
    }

    /// <summary>
    ///     The commits that touched one path of one repository, newest first. Matched on the path as the
    ///     commit recorded it, so history stops where the file was last renamed — which is the half of
    ///     rename-following ADR-0007 does not pay for, and which the reply says out loud.
    /// </summary>
    private static async Task<IReadOnlyList<RecordedChange>> PathCommitsAsync(IndexReader index,
        string repositorySlug, string path, int limit, CancellationToken cancellationToken)
    {
        await using var command = index.Connection.Query($"""
                                                          SELECT c.sha, c.repo_slug, c.author_name, c.author_email,
                                                                 c.authored_at, c.subject
                                                          FROM commit_files cf JOIN commits c USING (commit_id)
                                                          WHERE c.repo_slug = $r AND cf.path = $p
                                                          ORDER BY c.commit_id DESC
                                                          LIMIT {limit}
                                                          """,
            [new DuckDBParameter("r", repositorySlug), new DuckDBParameter("p", path)]);
        return await ChangesAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<RecordedChange>> ChangesAsync(DuckDBCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ReaderAsync(cancellationToken);
        var changes = new List<RecordedChange>();
        while (await reader.ReadAsync(cancellationToken))
            changes.Add(new RecordedChange(reader.Text("sha"), reader.Text("repo_slug"), reader.Text("author_name"),
                reader.Text("author_email"),
                reader.Timestamp("authored_at"), reader.Text("subject")));
        return changes;
    }

    /// <summary>
    ///     The authors of the commits in scope, most commits first, with the newest name each address
    ///     committed under. Grouped by address for the reason the overview groups by it: the address is
    ///     the identity git records, and a person who respells their name is one author, not two.
    ///     <paramref name="author" /> narrows it to the addresses a filter matched, which is the same
    ///     read with the same grouping — so what a filtered log says it matched cannot disagree with
    ///     what the authors listing says is there.
    ///     The totals ride on every row as window aggregates, which DuckDB evaluates over all the groups
    ///     before the limit cuts them, so they are the untruncated counts from the same scan (#179). A
    ///     scope with no commits returns no row to carry them, and its totals are zero.
    /// </summary>
    private static async Task<AuthorTally> AuthorsAsync(IndexReader index, string? repositorySlug,
        string? author, PathScope? path, int limit, CancellationToken cancellationToken)
    {
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug, author,
            pathRepositorySlug: path?.RepositorySlug, pathInRepository: path?.PathInRepository);
        await using var command = index.Connection.Query($"""
                                                          SELECT author_email,
                                                                 arg_max(author_name, authored_at) AS author_name,
                                                                 count(*) AS commits,
                                                                 -- epoch() for the reason ReaderColumns.EpochInstant
                                                                 -- gives.
                                                                 epoch(max(authored_at)) AS last_commit,
                                                                 -- Over the groups, before LIMIT: one row per
                                                                 -- address, so this counts addresses. The sum
                                                                 -- is cast because DuckDB widens it to HUGEINT,
                                                                 -- which the driver hands back as a BigInteger.
                                                                 count(*) OVER () AS addresses,
                                                                 (sum(count(*)) OVER ())::BIGINT AS matched_commits
                                                          FROM commits {scope}
                                                          GROUP BY author_email
                                                          ORDER BY commits DESC, author_email
                                                          LIMIT {limit}
                                                          """, parameters);
        await using var reader = await command.ReaderAsync(cancellationToken);
        var authors = new List<RecordedAuthor>();
        long addresses = 0, commits = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            authors.Add(new RecordedAuthor(reader.Text("author_name"), reader.Text("author_email"),
                reader.Int64("commits"),
                reader.EpochInstant("last_commit")));
            (addresses, commits) = (reader.Int64("addresses"), reader.Int64("matched_commits"));
        }

        return new AuthorTally(authors, addresses, commits);
    }

    /// <summary>
    ///     The authors a grouped read listed, and how many addresses and commits there are in all —
    ///     which the listing may be cut short of.
    /// </summary>
    private sealed record AuthorTally(IReadOnlyList<RecordedAuthor> Authors, long Addresses, long Commits)
    {
        public static readonly AuthorTally None = new([], 0, 0);
    }

    /// <summary>How many authors are recorded in scope, which is what a filter matching none is read against.</summary>
    private static async Task<long> AuthorCountAsync(IndexReader index, string? repositorySlug, PathScope? path,
        CancellationToken cancellationToken)
    {
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug,
            pathRepositorySlug: path?.RepositorySlug, pathInRepository: path?.PathInRepository);
        return await index.Connection.CountAsync($"SELECT count(DISTINCT author_email) FROM commits {scope}",
            parameters, cancellationToken);
    }

    /// <summary>
    ///     Who an <c>author</c> filter matched, and how many commits they have in scope.
    ///     The two totals are counted over every matched group rather than summed over the rows: the
    ///     rows are capped like any listing, and a broad substring past the cap would otherwise report
    ///     the sum of the first two hundred addresses as though it were the whole match — a number too
    ///     low, with nothing saying so, which is the reply this change exists to stop. Only a filter
    ///     that matched nobody reads again, for the authors it is measured against.
    /// </summary>
    private static async Task<AuthorFilter> MatchedAsync(IndexReader index, string? repositorySlug, string author,
        PathScope? path, CancellationToken cancellationToken)
    {
        var matched = await AuthorsAsync(index, repositorySlug, author, path, _maxAuthors, cancellationToken);
        return new AuthorFilter(author, matched.Commits, matched.Addresses, matched.Authors,
            matched.Addresses > 0 ? 0 : await AuthorCountAsync(index, repositorySlug, path, cancellationToken));
    }

    /// <summary>How many commits are recorded in scope, so a page can say how many there are.</summary>
    private static async Task<long> CommitCountAsync(IndexReader index, string? repositorySlug,
        CancellationToken cancellationToken)
    {
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug);
        return await index.Connection.CountAsync($"SELECT count(*) FROM commits {scope}", parameters,
            cancellationToken);
    }

    /// <summary>
    ///     A page of the change log, newest first, with each commit's body and the sums of what it did.
    ///     Ordered by <c>commit_id</c> for the reason <see cref="CommitsAsync" /> gives.
    /// </summary>
    private static async Task<IReadOnlyList<LoggedCommit>> LoggedAsync(IndexReader index, string? repositorySlug,
        int limit, int skip, CancellationToken cancellationToken)
    {
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug);
        await using var command = index.Connection.Query(
            LoggedStatement(scope, limit, skip, newestFirst: true), parameters);
        await using var reader = await command.ReaderAsync(cancellationToken);
        var commits = new List<LoggedCommit>();
        while (await reader.ReadAsync(cancellationToken)) commits.Add(Logged(reader));
        return commits;
    }

    /// <summary>
    ///     One logged commit by SHA, or null where the index holds none. The same projection the page of
    ///     the log uses, so the sums a reader saw in the list are the sums the commit's own page shows.
    ///     Unscoped by repository, because a link carries a SHA and not the repository it was listed
    ///     under — and ordered with a ceiling of one, because two repositories of a project can hold the
    ///     same commit and the answer must not depend on which of them DuckDB reached first.
    /// </summary>
    private static async Task<LoggedCommit?> OneLoggedAsync(IndexReader index, string sha,
        CancellationToken cancellationToken)
    {
        await using var command = index.Connection.Query(
            LoggedStatement("WHERE sha = $sha", 1, 0, newestFirst: false), [new DuckDBParameter("sha", sha)]);
        await using var reader = await command.ReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Logged(reader) : null;
    }

    /// <summary>
    ///     The statement for the logged commits that <paramref name="scope" />, <paramref name="limit" /> and
    ///     <paramref name="skip" /> pick out of <c>commits</c>, ordered by <c>commit_id</c>, with the sums of
    ///     what each one did. One
    ///     statement rather than a copy per caller, because the page of the log and a commit's own page
    ///     must count the same way — two spellings would drift the first time one of them learned to
    ///     count something else. The direction is one flag for the same reason: the page is cut in one
    ///     order and returned in the same order, and two spellings of it could disagree.
    ///     The page is chosen before anything is summed (#171). Grouped first and limited after, a page
    ///     of fifty aggregated every commit in scope, because DuckDB cannot push a limit beneath the
    ///     aggregate it follows. The sums are cast because DuckDB widens <c>sum</c> of an INTEGER to
    ///     HUGEINT, which the driver hands back as a BigInteger.
    /// </summary>
    private static string LoggedStatement(string scope, int limit, int skip, bool newestFirst)
    {
        // A literal chosen here and two integers, never caller text, so all three are safe to inline.
        string order = newestFirst ? "DESC" : "ASC";
        return $"""
         -- page holds the commits being listed and nothing else, so the join and the sums below
         -- are over its rows of commit_files alone.
         WITH page AS (SELECT * FROM commits {scope} ORDER BY commit_id {order} LIMIT {limit} OFFSET {skip})
         SELECT c.sha, c.repo_slug, c.author_name, c.author_email, c.authored_at, c.subject, c.body,
                count(cf.path)::INTEGER AS files_changed,
                coalesce(sum(cf.added), 0)::INTEGER AS added,
                coalesce(sum(cf.deleted), 0)::INTEGER AS deleted
         FROM page c LEFT JOIN commit_files cf USING (commit_id)
         GROUP BY c.commit_id, c.sha, c.repo_slug, c.author_name, c.author_email, c.authored_at,
                  c.subject, c.body
         ORDER BY c.commit_id {order}
         """;
    }

    private static LoggedCommit Logged(DbDataReader reader) =>
        new(reader.Text("sha"), reader.Text("repo_slug"), reader.Text("author_name"),
            reader.Text("author_email"), reader.Timestamp("authored_at"),
            reader.Text("subject"), reader.Text("body"), reader.Int32("files_changed"), reader.Int32("added"),
            reader.Int32("deleted"));

}
