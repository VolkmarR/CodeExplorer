using System.Data.Common;
using DuckDB.NET.Data;

namespace CodeExplorer;

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

/// <summary>
///     One path a commit touched. <see cref="Path" /> is the path inside its repository, as the commit
///     recorded it, and <see cref="QualifiedPath" /> is how the project names that path (ADR-0006) —
///     always set, with <see cref="AtHead" /> saying whether there is still a file at it, which is the
///     shape <see cref="ChurnedFile" /> already uses and for the same reason. A page can label a row
///     with the bare path because the commit above it says which repository that is; a tool reply has
///     no such column, and a bare <c>src/Old.cs</c> in a multi-repository project names a file an
///     agent cannot ask about.
/// </summary>
public sealed record CommitFile(string Path, string ChangeKind, int Added, int Deleted, string QualifiedPath,
    bool AtHead);

/// <summary>
///     One file that kept changing alongside another: how many of the anchor's commits also touched
///     it. <see cref="QualifiedPath" /> is how the project names it (ADR-0006) and is set even for a
///     path HEAD no longer holds, which <see cref="AtHead" /> is what says — coupling that happened
///     is still coupling, and an agent sent to read a file that is gone has been told something false.
/// </summary>
public sealed record CoChangedFile(string QualifiedPath, bool AtHead, int SharedCommits);

/// <summary>
///     What one file's coupling amounts to over a window, and what the answer had to leave out to say
///     it. <see cref="Paired" /> is the commits the ranking is drawn from and <see cref="Commits" />
///     every commit that touched the file, so the difference is the mass commits the ceiling excluded
///     — a number the reply says out loud, because a ranking drawn from a third of a file's history
///     without saying so is the one that misleads.
/// </summary>
/// <param name="Commits">Commits of the window that touched the anchor file, ceiling or no ceiling.</param>
/// <param name="Paired">How many of those were small enough to pair.</param>
/// <param name="Files">The co-changed files, most shared commits first.</param>
public sealed record CoChanges(int Commits, int Paired, IReadOnlyList<CoChangedFile> Files)
{
    /// <summary>The commits the ceiling kept out of the pairing.</summary>
    public int Excluded => Commits - Paired;
}

/// <summary>Everything a page of the log asks for. A null repository covers every one in the project.</summary>
public sealed record LogRequest(string? Repository, int Limit, int Page, string? Author = null,
    string? Message = null);

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
public sealed record AuthorsRequest(string? Repository, int Limit);

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
    IReadOnlyList<RecordedAuthor> Authors) : Outcome;

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
    string? Message = null) : Outcome;

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

/// <summary>Everything one file's history asks for.</summary>
public sealed record FileHistoryRequest(string Path, int Limit);

/// <summary>
///     The commits that changed one file, newest first. Empty with <see cref="HasHistory" /> set is a
///     file whose history is older than what was imported or that reached its path by a rename; empty
///     without it is a project that has no history to look in.
/// </summary>
public sealed record FileHistoryAnswer(
    IndexedFile File,
    bool HasHistory,
    IReadOnlyList<RecordedChange> Commits) : Outcome;

/// <summary>
///     Everything a blame asks for. <see cref="EndLine" /> null — or anything below 1 — is the end of
///     the file, which the module bounds rather than the caller.
/// </summary>
public sealed record BlameRequest(string Path, int StartLine, int? EndLine);

/// <summary>
///     Who last changed each line of a window of a file. <see cref="First" /> and <see cref="Last" />
///     are the window that was read, after clamping, with <see cref="Last" /> null for the end of the
///     file, so a reply naming an empty answer's bounds names the ones it looked at. Empty runs is a
///     file the build kept without lines as much as it is a window past the end, which
///     <see cref="IndexedFile.SkipReason" /> tells apart.
/// </summary>
public sealed record BlameAnswer(
    IndexedFile File,
    bool HasHistory,
    int First,
    int? Last,
    IReadOnlyList<AttributedLines> Runs) : Outcome;

/// <summary>
///     Everything a churn ranking asks for. <see cref="Directory" /> is a qualified path, a bare
///     repository slug, or null for the whole project — one field, because a repository is a directory
///     and both callers hand over whichever of the two their surface spells.
/// </summary>
public sealed record ChurnRequest(string? Directory, int Days, int Limit);

/// <summary>
///     The most-changed files of a window, and everything needed to say what the ranking does not
///     cover. The four ways it can come back empty are kept apart rather than collapsed, because each
///     is a different sentence and an agent shown the wrong one learns a wrong fact (CONTEXT.md,
///     History): <see cref="HasHistory" /> false is a project nothing was imported for,
///     <see cref="Window" /> null is a scope with no commit at all, an empty <see cref="Files" /> is a
///     window in which nothing changed, and <see cref="Coverage" /> names the repositories the ranking
///     could not speak for whichever of those it is.
///     <see cref="ScopeSpelled" /> is what the answer calls the scope, so a ranking and a miss name it
///     the same way.
/// </summary>
public sealed record ChurnAnswer(
    string ScopeSpelled,
    bool HasHistory,
    HistoryWindow? Window,
    IReadOnlyList<ChurnedFile> Files,
    HistoryCoverage Coverage) : Outcome;

/// <summary>Everything a co-change ranking asks for.</summary>
public sealed record CoChangeRequest(string Path, int Days, int Limit);

/// <summary>
///     What one file changed alongside over a window. <see cref="MaxCommitPaths" /> is the ceiling the
///     pairing ran under, carried so that the reply explaining what was excluded quotes the number that
///     was actually used rather than reading the configuration a second time.
/// </summary>
public sealed record CoChangeAnswer(
    IndexedFile File,
    bool HasHistory,
    HistoryWindow? Window,
    CoChanges Coupling,
    int MaxCommitPaths) : Outcome;

/// <summary>Everything the paths of one commit ask for. The SHA is full: a listing is where it came from.</summary>
public sealed record CommitFilesRequest(string Sha);

/// <summary>Everything one commit's own record asks for. The SHA is full, like the file list's.</summary>
public sealed record CommitRequest(string Sha);

/// <summary>
///     One commit as the change log would list it, read on its own because something linked to it. A
///     SHA the index does not hold is a miss and never an answer with empty fields: a page drawn from
///     one would claim a commit that changed nothing and was written by nobody.
/// </summary>
public sealed record CommitAnswer(LoggedCommit Commit) : Outcome;

/// <summary>The paths one commit touched, in path order.</summary>
public sealed record CommitFilesAnswer(string Sha, IReadOnlyList<CommitFile> Files) : Outcome;

/// <summary>
///     The index-backed answers drawn from a project's history (CONTEXT.md): what changed, what changed
///     one file, who last changed each line, what keeps changing, and what keeps changing with what.
///     One module behind the MCP tools and the operator's pages alike, so the decisions inside — when a
///     project counts as having no history, how far a window reaches, which commits are too wide to
///     pair, what a scope is called — are taken once and both surfaces answer the same way; only the
///     rendering differs. Every answer is an <see cref="Outcome" />: a semantic failure is an answer,
///     never an exception (CODING_STANDARDS).
///     The SQL is private here rather than on <see cref="IndexReader" /> because each statement has one
///     caller, which is this module: a reader member per question is an interface as wide as the
///     implementation, and it was the shape that let two surfaces read the same tables in two orders.
///     What stays on the reader is what more than one module asks — a file's first and last commit,
///     which the file read carries, and the coverage rule, which the overview needs too.
/// </summary>
public sealed class HistoryQueries(ProjectIndexes indexes, IConfiguration configuration)
{
    /// <summary>Named on the search telemetry, so a dashboard can tell a history read apart from a scan.</summary>
    private const string Engine = "history";

    /// <summary>
    ///     The most commits one read may return, whoever asks. Both surfaces had settled on the same
    ///     number from opposite directions — a page of the change log and a tool reply are both read
    ///     top-down — so it is one ceiling here rather than two that can drift.
    /// </summary>
    private const int MaxCommits = 200;

    /// <summary>
    ///     Ceiling on the authors one answer names. A project of two hundred contributors is a
    ///     contributor list rather than "who knows this code", and the same ceiling bounds the
    ///     addresses a filter reports matching — where more than a handful means the caller asked for
    ///     something far too broad, and the count says so without the reply becoming the list.
    /// </summary>
    private const int MaxAuthors = 200;

    /// <summary>
    ///     The most files either ranking may return. A hundred is already more than anybody reads off a
    ///     ranking, and past it the reply cap or the page's own scroll is what would bite.
    /// </summary>
    private const int MaxRankedFiles = 100;

    /// <summary>
    ///     The most lines one blame may cover. The index refuses files over <c>Index:MaxFileBytes</c>
    ///     (4 MiB by default), which bounds this well below it; a caller that protects an agent's
    ///     context caps the runs it prints, and this one protects the server.
    /// </summary>
    private const int MaxLinesPerFile = 100_000;

    /// <summary>
    ///     Default for <c>History:MaxCommitPaths</c>, the most paths a commit may touch and still be
    ///     paired. A reformat, a vendor drop or an initial import couples every path it touched to
    ///     every other, and those pairs are one commit rather than evidence about any file in it.
    ///     Two hundred is the judgement: high enough that a feature landing across a module still
    ///     counts as coupling, low enough that nothing a person wrote by hand in one sitting reaches
    ///     it. It is a setting and not a constant because what counts as a mass commit differs between
    ///     a repository of two hundred files and one of eighty thousand.
    /// </summary>
    private const int DefaultMaxCommitPaths = 200;

    private readonly int _maxCommitPaths = configuration.GetValue("History:MaxCommitPaths", DefaultMaxCommitPaths);

    /// <summary>
    ///     A page of commits, newest first, scoped to one repository or covering every one. Whether the
    ///     project has history at all is read first and carried, because "no commit on this page" and
    ///     "no history was imported" are answered with opposite sentences.
    /// </summary>
    public async Task<Outcome> LogAsync(string slug, LogRequest request, CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await IndexReader.OverIndexAsync(indexes, slug, request.Repository, async (index, token) =>
        {
            bool hasHistory = await HasHistoryAsync(index, token);
            int limit = Math.Clamp(request.Limit, 1, MaxCommits);
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
                ? await MatchedAsync(index, scope, asked, token)
                : null;
            var commits = hasHistory && author?.Addresses != 0
                ? await CommitsAsync(index, scope, asked, mentions, limit, (page - 1) * limit, token)
                : [];
            return new LogAnswer(hasHistory, index.Repository, page, limit, commits, author, mentions);
        }, cancellationToken);
        if (outcome is LogAnswer answer) recording.Matched(Engine, answer.Commits.Count, 0);
        else recording.Problem();
        return outcome;
    }

    /// <summary>
    ///     The authors of a project or of one repository, most commits first, with how many there are
    ///     in scope so a cut listing says what it left out.
    /// </summary>
    public async Task<Outcome> AuthorsAsync(string slug, AuthorsRequest request, CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await IndexReader.OverIndexAsync(indexes, slug, request.Repository, async (index, token) =>
        {
            bool hasHistory = await HasHistoryAsync(index, token);
            int limit = Math.Clamp(request.Limit, 1, MaxAuthors);
            string? scope = index.Repository?.Slug;
            var authors = hasHistory ? await AuthorsAsync(index, scope, null, limit, token) : [];
            long total = hasHistory ? await AuthorCountAsync(index, scope, token) : 0;
            return new AuthorsAnswer(hasHistory, index.Repository, total, limit, authors);
        }, cancellationToken);
        if (outcome is AuthorsAnswer answer) recording.Matched(Engine, answer.Authors.Count, 0);
        else recording.Problem();
        return outcome;
    }

    /// <summary>
    ///     A page of the change log: the same commits with each one's body and the sums of what it did,
    ///     and the total in scope so a page can say how many there are.
    /// </summary>
    public async Task<Outcome> ChangeLogAsync(string slug, ChangeLogRequest request,
        CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await IndexReader.OverIndexAsync(indexes, slug, request.Repository, async (index, token) =>
        {
            int pageSize = Math.Clamp(request.PageSize, 1, MaxCommits);
            int page = Math.Max(1, request.Page);
            string? scope = index.Repository?.Slug;
            long total = await CommitCountAsync(index, scope, token);
            var commits = await LoggedAsync(index, scope, pageSize, (page - 1) * pageSize, token);
            return new ChangeLogAnswer(total, page, pageSize, commits);
        }, cancellationToken);
        if (outcome is ChangeLogAnswer answer) recording.Matched(Engine, answer.Commits.Count, 0);
        else recording.Problem();
        return outcome;
    }

    /// <summary>
    ///     The commits that touched one path, newest first. The path is resolved before the history is
    ///     asked about, so an agent that got the path wrong is told so rather than told about history.
    /// </summary>
    public async Task<Outcome> FileHistoryAsync(string slug, FileHistoryRequest request,
        CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await IndexReader.OverFileAsync(indexes, slug, request.Path, false,
            async (index, file, token) =>
            {
                bool hasHistory = await HasHistoryAsync(index, token);
                var commits = hasHistory
                    ? await PathCommitsAsync(index, file.RepositorySlug, file.PathInRepository,
                        Math.Clamp(request.Limit, 1, MaxCommits), token)
                    : [];
                return new FileHistoryAnswer(file, hasHistory, commits);
            }, cancellationToken);
        if (outcome is FileHistoryAnswer answer) recording.Matched(Engine, answer.Commits.Count, 0);
        else recording.Problem();
        return outcome;
    }

    /// <summary>
    ///     Who last changed each line of a window of one file, as runs. A file the build kept without
    ///     lines answers with no runs rather than a refusal: it is in the index, and only the
    ///     attribution has nothing to fill.
    /// </summary>
    public async Task<Outcome> BlameAsync(string slug, BlameRequest request, CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await IndexReader.OverFileAsync(indexes, slug, request.Path, false,
            async (index, file, token) =>
            {
                bool hasHistory = await HasHistoryAsync(index, token);
                int first = Math.Max(1, request.StartLine);
                // Anything below 1 is the end of the file, which is the caller saying "all of it" in the
                // two spellings its surface allows: an omitted argument and a zero.
                int? last = request.EndLine is null or < 1 ? null : request.EndLine;
                IReadOnlyList<AttributedLines> runs = file.SkipReason is null
                    ? await RunsAsync(index, file.FileId, first, Math.Min(last ?? MaxLinesPerFile, MaxLinesPerFile),
                        token)
                    : [];
                return new BlameAnswer(file, hasHistory, first, last, runs);
            }, cancellationToken);
        if (outcome is BlameAnswer answer) recording.Matched(Engine, answer.Runs.Count, 0);
        else recording.Problem();
        return outcome;
    }

    /// <summary>
    ///     The files a window's commits touched, most commits first, within a scope the caller names
    ///     with a qualified path.
    ///     The order of the decisions is the answer's meaning: whether the project has history at all,
    ///     then what the scope is, then whether that scope holds a commit, then which repositories the
    ///     ranking cannot speak for, and only then the ranking. Each step's answer is carried rather
    ///     than turned into an empty ranking, because an empty ranking is a claim — that nothing changed
    ///     — and three of the four steps above would be making it falsely.
    /// </summary>
    public async Task<Outcome> ChurnAsync(string slug, ChurnRequest request, CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await IndexReader.OverDirectoryAsync(indexes, slug, request.Directory,
            async (index, directory, token) =>
            {
                bool hasHistory = await HasHistoryAsync(index, token);
                var (repositorySlug, directoryInRepository, spelled) = Scope(index, directory);

                var window = hasHistory
                    ? await IndexQueries.WindowAsync(index.Connection, request.Days, repositorySlug, token)
                    : null;
                // Read whether or not there is a ranking, and before the answer branches: a reader shown
                // nothing is the one most likely to conclude that nothing changed.
                var coverage = await index.HistoryCoverageAsync(repositorySlug, token);
                IReadOnlyList<ChurnedFile> ranked = window is null
                    ? []
                    : await IndexQueries.RankAsync(index.Connection, await index.PathsAsync(token), window,
                        repositorySlug, directoryInRepository, Math.Clamp(request.Limit, 1, MaxRankedFiles), token);
                return new ChurnAnswer(spelled, hasHistory, window, ranked, coverage);
            }, cancellationToken);
        if (outcome is ChurnAnswer answer) recording.Matched(Engine, answer.Files.Count, 0);
        else recording.Problem();
        return outcome;
    }

    /// <summary>
    ///     The files a window's commits changed alongside one file, most shared commits first. Scoped to
    ///     the anchor's own repository, because that is the only one whose commits could have carried it
    ///     — and so the window is that repository's newest commit, not another's.
    /// </summary>
    public async Task<Outcome> CoChangedAsync(string slug, CoChangeRequest request,
        CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await IndexReader.OverFileAsync(indexes, slug, request.Path, false,
            async (index, file, token) =>
            {
                bool hasHistory = await HasHistoryAsync(index, token);
                var window = hasHistory
                    ? await IndexQueries.WindowAsync(index.Connection, request.Days, file.RepositorySlug, token)
                    : null;
                var coupling = window is null
                    ? new CoChanges(0, 0, [])
                    : await PairAsync(index, window, file.RepositorySlug, file.PathInRepository, _maxCommitPaths,
                        Math.Clamp(request.Limit, 1, MaxRankedFiles), token);
                return new CoChangeAnswer(file, hasHistory, window, coupling, _maxCommitPaths);
            }, cancellationToken);
        if (outcome is CoChangeAnswer answer) recording.Matched(Engine, answer.Coupling.Files.Count, 0);
        else recording.Problem();
        return outcome;
    }

    /// <summary>
    ///     One commit, by SHA. Its own read rather than a page of the log filtered down, because the
    ///     caller here arrived by a link and knows only the SHA — finding it in the log would mean
    ///     paging until it turned up, and a commit old enough is past the ceiling every page runs under.
    ///     Separate from its file list for the reason blame is separate from the file: the page draws
    ///     the message and the sums as soon as it has them, and a commit touching hundreds of paths
    ///     should not hold that back.
    /// </summary>
    public async Task<Outcome> CommitAsync(string slug, CommitRequest request, CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await IndexReader.OverIndexAsync(indexes, slug, null,
            async (index, token) => await OverShaAsync(index, slug, request.Sha, async (sha, inner) =>
            {
                // Read back rather than carried over: the resolution and this read are two statements,
                // and a swap between them leaves a SHA that was in the index and is not any more.
                var commit = await OneLoggedAsync(index, sha, inner);
                return commit is null
                    ? new Problem(NoSuchCommit(request.Sha, slug), ProblemKind.Missing)
                    : new CommitAnswer(commit);
            }, token), cancellationToken);
        if (outcome is CommitAnswer) recording.Matched(Engine, 1, 0);
        else recording.Problem();
        return outcome;
    }

    /// <summary>
    ///     The paths one commit touched. A SHA the index does not hold is a miss and not an empty
    ///     answer: every commit the walk records touched something, the root included, so nothing to
    ///     list means nothing to list it for. Resolving the SHA first is what lets the two be told
    ///     apart — a commit that really did touch nothing, an empty one or a merge that changed nothing
    ///     against its first parent, is a miss that says which of the two it is.
    /// </summary>
    public async Task<Outcome> CommitFilesAsync(string slug, CommitFilesRequest request,
        CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await IndexReader.OverIndexAsync(indexes, slug, null,
            async (index, token) => await OverShaAsync(index, slug, request.Sha, async (sha, inner) =>
            {
                var files = await PathsOfAsync(index, sha, inner);
                return files.Count == 0
                    // Kept a miss, and so a 404 on the route the operator's page reads: a page drawn
                    // from an empty list would say "this commit changed nothing" in its own layout,
                    // where this says it in words and says what that means.
                    ? new Problem(
                        $"Commit {sha} of project '{slug}' touched no path. It is an empty commit, or a merge "
                        + "that changed nothing against its first parent; history records what a commit did to "
                        + "its first parent, and nothing came in by that route.", ProblemKind.Missing)
                    : new CommitFilesAnswer(sha, files);
            }, token), cancellationToken);
        if (outcome is CommitFilesAnswer answer) recording.Matched(Engine, answer.Files.Count, 0);
        else recording.Problem();
        return outcome;
    }

    /// <summary>
    ///     How many commits a prefix may fit before the refusal stops naming them all. Four, because
    ///     the list is there to be compared against what the caller has and not to be complete: a
    ///     prefix that fits more than four is one the caller has to lengthen whatever the rest are.
    /// </summary>
    private const int MaxNamedShas = 4;

    /// <summary>
    ///     What git itself requires of an abbreviated SHA, and for the same reason: below four
    ///     characters a prefix that happens to fit one commit today fits two after the next refresh,
    ///     and the answer it gave was never about the commit the caller meant.
    /// </summary>
    private const int MinShaPrefix = 4;

    /// <summary>
    ///     Runs an answer against the one commit a caller meant, from as much of the SHA as they had.
    ///     A prefix and not the whole SHA, because every reply an agent reads abbreviates to eight
    ///     characters — git_log, file_history and blame all do — so the full forty is a thing no tool
    ///     surface ever hands out, and a read that insisted on it could not be reached from any of
    ///     them. The API and the operator's pages send whole SHAs and are unaffected in what they get
    ///     back, a whole SHA being a prefix of itself; what they gain is this method's two refusals,
    ///     for a SHA too short to be one and for one that fits several.
    ///     More than one match is refused rather than resolved to the first. Two commits sharing eight
    ///     characters is rare and a confident answer about the wrong one is undetectable, which is the
    ///     trade every git client makes the same way.
    ///     Shaped as a continuation rather than as a SHA-or-problem pair for the reason
    ///     <see cref="IndexReader.OverFileAsync" /> is: the caller that gets a SHA is the only one that
    ///     runs, so there is no second state for it to have to rule out first.
    /// </summary>
    private static async Task<Outcome> OverShaAsync(IndexReader index, string slug, string given,
        Func<string, CancellationToken, Task<Outcome>> answer, CancellationToken cancellationToken)
    {
        // Lower-cased because git writes SHAs in hex lower case and a pasted one may not be, and
        // trimmed because a SHA copied out of a reply brings its spacing with it.
        string prefix = given.Trim().ToLowerInvariant();
        if (prefix.Length < MinShaPrefix)
            return new Problem(
                (prefix.Length == 0
                    ? $"No commit SHA was given, so no commit of project '{slug}' can be named. "
                    : $"'{given}' is too short to name a commit of project '{slug}': give at least {MinShaPrefix} characters of the SHA. ")
                + "git_log, file_history and blame each print the first eight, which is enough.",
                ProblemKind.Invalid);

        var matches = await ShasAsync(index, prefix, MaxNamedShas, cancellationToken);
        if (matches.Count == 0) return new Problem(NoSuchCommit(given, slug), ProblemKind.Missing);
        if (matches.Count == 1) return await answer(matches[0], cancellationToken);

        // The candidates rather than a count: a caller holding eight characters of one of them can see
        // which it meant and lengthen its argument, where a count only says to try again.
        return new Problem(
            $"'{given}' starts the SHA of more than one commit of project '{slug}': "
            + $"{string.Join(", ", matches.Select(sha => sha[..12]))}"
            + (matches.Count == MaxNamedShas ? ", and possibly others" : "")
            + ". Give more of the SHA.", ProblemKind.Invalid);
    }

    /// <summary>
    ///     The one sentence a SHA that names no commit is answered with, wherever it was asked. Written
    ///     once because the record and the file list are two halves of one workflow: a caller that
    ///     tries the second after the first has to read one fact, not two shapes of it.
    /// </summary>
    private static string NoSuchCommit(string sha, string slug) =>
        $"No commit '{sha}' in the history of project '{slug}'.";

    /// <summary>
    ///     The commits whose SHA starts with what the caller gave, at most <paramref name="ceiling" />
    ///     of them. Distinct, because two repositories of one project can hold the same commit and that
    ///     is one commit to a caller rather than an ambiguous prefix.
    ///     <c>starts_with</c> and not <c>LIKE</c>: the prefix is caller text, and under LIKE a stray
    ///     <c>%</c> in it would widen the match instead of failing to find one.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ShasAsync(IndexReader index, string prefix, int ceiling,
        CancellationToken cancellationToken)
    {
        using var command = index.Connection.Query($"""
                                                    SELECT DISTINCT sha FROM commits
                                                    WHERE starts_with(sha, $p)
                                                    ORDER BY sha
                                                    LIMIT {ceiling}
                                                    """, [new DuckDBParameter("p", prefix)]);
        using var reader = await command.ReaderAsync(cancellationToken);
        var shas = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) shas.Add(reader.Text("sha"));
        return shas;
    }

    /// <summary>
    ///     What a <c>directory</c> narrows to: a repository, a directory inside it, or neither for the
    ///     whole project — with what the answer calls it, so the ranking and the misses name it the same
    ///     way. A path that names no directory is refused before this, by the open itself.
    /// </summary>
    private static (string? RepositorySlug, string? DirectoryInRepository, string Spelled) Scope(
        IndexReader index, IndexedDirectory directory)
    {
        string project = $"project '{index.ProjectSlug}'";
        if (directory.Repository is null) return (null, null, project);

        // Null rather than an empty path: the repository's own root is the whole repository, which the
        // ranking scopes with the slug alone and names as such.
        string? directoryInRepository = directory.PathInRepository.Length == 0 ? null : directory.PathInRepository;
        return (directory.Repository.Slug, directoryInRepository,
            directoryInRepository is null
                ? $"repository '{directory.Repository.Slug}' of {project}"
                : $"'{directory.QualifiedPath}' in {project}");
    }

    /// <summary>
    ///     Whether this index holds any history at all. An index built before ADR-0007, or one whose
    ///     every repository failed to walk, has the tables and nothing in them — and "no commits
    ///     recorded" must never be answered as "this file was never changed", which reads as a fact.
    /// </summary>
    private static async Task<bool> HasHistoryAsync(IndexReader index, CancellationToken cancellationToken)
    {
        using var command = index.Connection.Query("SELECT count(*) > 0 FROM commits", []);
        return await command.ScalarAsync(cancellationToken) is true;
    }

    /// <summary>
    ///     The commits of a repository, newest first — or of every repository when none is named. A page
    ///     of history, ordered by <c>commit_id</c> because it ascends with history by construction while
    ///     an author date does not (ADR-0007).
    /// </summary>
    private static async Task<IReadOnlyList<RecordedChange>> CommitsAsync(IndexReader index, string? repositorySlug,
        string? author, string? message, int limit, int skip, CancellationToken cancellationToken)
    {
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug, author, message);
        using var command = index.Connection.Query($"""
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
        using var command = index.Connection.Query($"""
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
        using var reader = await command.ReaderAsync(cancellationToken);
        var changes = new List<RecordedChange>();
        while (await reader.ReadAsync(cancellationToken))
            changes.Add(new RecordedChange(reader.Text("sha"), reader.Text("repo_slug"), reader.Text("author_name"),
                reader.Text("author_email"),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("authored_at")), reader.Text("subject")));
        return changes;
    }

    /// <summary>
    ///     The authors of the commits in scope, most commits first, with the newest name each address
    ///     committed under. Grouped by address for the reason the overview groups by it: the address is
    ///     the identity git records, and a person who respells their name is one author, not two.
    ///     <paramref name="author" /> narrows it to the addresses a filter matched, which is the same
    ///     read with the same grouping — so what a filtered log says it matched cannot disagree with
    ///     what the authors listing says is there.
    /// </summary>
    private static async Task<IReadOnlyList<RecordedAuthor>> AuthorsAsync(IndexReader index, string? repositorySlug,
        string? author, int limit, CancellationToken cancellationToken)
    {
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug, author);
        using var command = index.Connection.Query($"""
                                                    SELECT author_email,
                                                           arg_max(author_name, authored_at) AS author_name,
                                                           count(*) AS commits,
                                                           -- epoch() because seconds as a double do not
                                                           -- depend on whether ICU is loaded to decide
                                                           -- the session time zone.
                                                           epoch(max(authored_at)) AS last_commit
                                                    FROM commits {scope}
                                                    GROUP BY author_email
                                                    ORDER BY commits DESC, author_email
                                                    LIMIT {limit}
                                                    """, parameters);
        using var reader = await command.ReaderAsync(cancellationToken);
        var authors = new List<RecordedAuthor>();
        while (await reader.ReadAsync(cancellationToken))
            authors.Add(new RecordedAuthor(reader.Text("author_name"), reader.Text("author_email"),
                reader.GetFieldValue<long>(reader.GetOrdinal("commits")),
                DateTimeOffset.FromUnixTimeSeconds((long)reader.Double("last_commit"))));
        return authors;
    }

    /// <summary>How many authors are recorded in scope, which is what a filter matching none is read against.</summary>
    private static async Task<long> AuthorCountAsync(IndexReader index, string? repositorySlug,
        CancellationToken cancellationToken)
    {
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug);
        using var command = index.Connection.Query($"SELECT count(DISTINCT author_email) FROM commits {scope}",
            parameters);
        return (long)(await command.ScalarAsync(cancellationToken) ?? 0L);
    }

    /// <summary>
    ///     Who an <c>author</c> filter matched, and how many commits they have in scope.
    ///     The two totals are counted rather than summed over the rows: the rows are capped like any
    ///     listing, and a broad substring past the cap would otherwise report the sum of the first two
    ///     hundred addresses as though it were the whole match — a number too low, with nothing saying
    ///     so, which is the reply this change exists to stop.
    /// </summary>
    private static async Task<AuthorFilter> MatchedAsync(IndexReader index, string? repositorySlug, string author,
        CancellationToken cancellationToken)
    {
        var (addresses, commits) = await MatchCountsAsync(index, repositorySlug, author, cancellationToken);
        var matched = addresses == 0
            ? []
            : await AuthorsAsync(index, repositorySlug, author, MaxAuthors, cancellationToken);
        return new AuthorFilter(author, commits, addresses, matched,
            addresses > 0 ? 0 : await AuthorCountAsync(index, repositorySlug, cancellationToken));
    }

    /// <summary>How many addresses a filter matched and how many commits they have between them.</summary>
    private static async Task<(long Addresses, long Commits)> MatchCountsAsync(IndexReader index,
        string? repositorySlug, string author, CancellationToken cancellationToken)
    {
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug, author);
        using var command = index.Connection.Query(
            $"SELECT count(DISTINCT author_email) AS addresses, count(*) AS commits FROM commits {scope}", parameters);
        using var reader = await command.ReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (0, 0);

        return (reader.GetFieldValue<long>(reader.GetOrdinal("addresses")),
            reader.GetFieldValue<long>(reader.GetOrdinal("commits")));
    }

    /// <summary>How many commits are recorded in scope, so a page can say how many there are.</summary>
    private static async Task<long> CommitCountAsync(IndexReader index, string? repositorySlug,
        CancellationToken cancellationToken)
    {
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug);
        using var command = index.Connection.Query($"SELECT count(*) FROM commits {scope}", parameters);
        return (long)(await command.ScalarAsync(cancellationToken) ?? 0L);
    }

    /// <summary>
    ///     A page of the change log, newest first, with each commit's body and the sums of what it did.
    ///     Ordered by <c>commit_id</c> for the reason <see cref="CommitsAsync" /> gives.
    /// </summary>
    private static async Task<IReadOnlyList<LoggedCommit>> LoggedAsync(IndexReader index, string? repositorySlug,
        int limit, int skip, CancellationToken cancellationToken)
    {
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug);
        using var command = index.Connection.Query($"""
                                                    {LoggedProjection}
                                                    {scope}
                                                    {LoggedGrouping}
                                                    ORDER BY c.commit_id DESC
                                                    LIMIT {limit} OFFSET {skip}
                                                    """, parameters);
        using var reader = await command.ReaderAsync(cancellationToken);
        var commits = new List<LoggedCommit>();
        while (await reader.ReadAsync(cancellationToken)) commits.Add(Logged(reader));
        return commits;
    }

    /// <summary>
    ///     One logged commit by SHA, or null where the index holds none. The same projection the page of
    ///     the log uses, so the sums a reader saw in the list are the sums the commit's own page shows.
    ///     Unscoped by repository, because a link carries a SHA and not the repository it was listed
    ///     under — and ordered with a ceiling of one, because two repositories of a project can hold the
    ///     same commit and the answer must not depend on which of them DuckDB grouped first.
    /// </summary>
    private static async Task<LoggedCommit?> OneLoggedAsync(IndexReader index, string sha,
        CancellationToken cancellationToken)
    {
        using var command = index.Connection.Query($"""
                                                    {LoggedProjection}
                                                    WHERE c.sha = $sha
                                                    {LoggedGrouping}
                                                    ORDER BY c.commit_id
                                                    LIMIT 1
                                                    """, [new DuckDBParameter("sha", sha)]);
        using var reader = await command.ReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Logged(reader) : null;
    }

    /// <summary>
    ///     The columns of a logged commit, and the sums of what it did. One string rather than a copy
    ///     per caller, because the page of the log and a commit's own page must count the same way — two
    ///     spellings would drift the first time one of them learned to count something else. The sums
    ///     are cast because DuckDB widens <c>sum</c> of an INTEGER to HUGEINT, which the driver hands
    ///     back as a BigInteger.
    /// </summary>
    private const string LoggedProjection = """
                                            SELECT c.sha, c.repo_slug, c.author_name, c.author_email,
                                                   c.authored_at, c.subject, c.body,
                                                   count(cf.path)::INTEGER AS files_changed,
                                                   coalesce(sum(cf.added), 0)::INTEGER AS added,
                                                   coalesce(sum(cf.deleted), 0)::INTEGER AS deleted
                                            FROM commits c LEFT JOIN commit_files cf USING (commit_id)
                                            """;

    /// <summary>The grouping <see cref="LoggedProjection" />'s sums need, beside it for the same reason.</summary>
    private const string LoggedGrouping = """
                                          GROUP BY c.commit_id, c.sha, c.repo_slug, c.author_name,
                                                   c.author_email, c.authored_at, c.subject, c.body
                                          """;

    private static LoggedCommit Logged(DbDataReader reader) =>
        new(reader.Text("sha"), reader.Text("repo_slug"), reader.Text("author_name"),
            reader.Text("author_email"), reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("authored_at")),
            reader.Text("subject"), reader.Text("body"), reader.Int32("files_changed"), reader.Int32("added"),
            reader.Int32("deleted"));

    /// <summary>
    ///     Who last changed each line of a file, as runs rather than as one row per line: consecutive
    ///     lines sharing a commit are one entry, which is how blame reads and a fraction of the output.
    /// </summary>
    private static async Task<IReadOnlyList<AttributedLines>> RunsAsync(IndexReader index, long fileId, int first,
        int last, CancellationToken cancellationToken)
    {
        // The runs are rebuilt from lines rather than read from attribution, because attribution is
        // keyed by blob and holds the ranges of the whole file: a window of it would have to be clipped
        // here anyway, and a line the build could not attribute is absent there but present here.
        // Gaps and islands: within one commit, consecutive line numbers have a constant difference from
        // their position in that commit's lines, so that difference is the run. The window function is
        // computed in a subquery because it is evaluated after grouping and cannot appear in GROUP BY.
        // Lines with no commit fall in one NULL partition, which islands correctly for the same reason.
        using var command = index.Connection.Query("""
                                                   SELECT min(line_number) AS start_line,
                                                          max(line_number) AS end_line,
                                                          sha, author_name, authored_at, subject
                                                   FROM (SELECT l.line_number, c.sha, c.author_name, c.authored_at,
                                                                c.subject,
                                                                l.line_number - row_number() OVER (
                                                                    PARTITION BY c.sha ORDER BY l.line_number) AS run
                                                         FROM lines l LEFT JOIN commits c USING (commit_id)
                                                         WHERE l.file_id = $f AND l.line_number BETWEEN $a AND $b)
                                                   GROUP BY sha, author_name, authored_at, subject, run
                                                   ORDER BY start_line
                                                   """,
            [new DuckDBParameter("f", fileId), new DuckDBParameter("a", first), new DuckDBParameter("b", last)]);
        using var reader = await command.ReaderAsync(cancellationToken);
        var runs = new List<AttributedLines>();
        while (await reader.ReadAsync(cancellationToken))
            runs.Add(new AttributedLines(
                reader.GetInt32(reader.GetOrdinal("start_line")), reader.GetInt32(reader.GetOrdinal("end_line")),
                await reader.IsDBNullAsync(reader.GetOrdinal("sha"), cancellationToken)
                    ? null
                    : new AttributedBy(reader.Text("sha"), reader.Text("author_name"),
                        reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("authored_at")),
                        reader.Text("subject"))));
        return runs;
    }

    /// <summary>
    ///     The paths one commit touched, each named the way the project names it and marked with
    ///     whether HEAD still holds a file there. Matched by whole SHA, which is what
    ///     <see cref="OverShaAsync" /> has already resolved whatever the caller gave.
    ///     The naming is built from the commit's own repository rather than read from <c>files</c>,
    ///     because the paths that need it most are exactly the ones with no row there.
    ///     One <c>commit_id</c> and not every row sharing the SHA: two repositories of a project can
    ///     hold the same commit, and listing both would name every path twice and count them twice. The
    ///     lowest id is the one <see cref="OneLoggedAsync" /> reports, so the record and the paths are
    ///     about the same copy of it.
    /// </summary>
    private static async Task<IReadOnlyList<CommitFile>> PathsOfAsync(IndexReader index, string sha,
        CancellationToken cancellationToken)
    {
        var paths = await index.PathsAsync(cancellationToken);
        using var command = index.Connection.Query("""
                                                   SELECT cf.path, cf.change_kind, cf.added, cf.deleted,
                                                          c.repo_slug, f.qualified_path
                                                   FROM commits c JOIN commit_files cf USING (commit_id)
                                                   LEFT JOIN repositories r ON r.slug = c.repo_slug
                                                   LEFT JOIN files f ON f.repo_id = r.repo_id AND f.path = cf.path
                                                   WHERE c.commit_id = (SELECT min(commit_id) FROM commits
                                                                        WHERE sha = $sha)
                                                   ORDER BY cf.path
                                                   """, [new DuckDBParameter("sha", sha)]);
        using var reader = await command.ReaderAsync(cancellationToken);
        var files = new List<CommitFile>();
        while (await reader.ReadAsync(cancellationToken))
            files.Add(new CommitFile(reader.Text("path"), reader.Text("change_kind"), reader.Int32("added"),
                reader.Int32("deleted"), paths.Format(reader.Text("repo_slug"), reader.Text("path")),
                !reader.IsNull("qualified_path")));
        return files;
    }

    /// <summary>
    ///     The files a window's commits changed alongside one file, most shared commits first. The
    ///     coupling the code itself does not show — a constant and the three places that read it, two
    ///     files that have simply always moved together.
    ///     Pairing is anchored rather than a self-join of <c>commit_files</c> against itself. A
    ///     self-join is the obvious reading of "which files change together" and it is quadratic in
    ///     every commit of the window at once: one vendor drop of five thousand paths is twenty-five
    ///     million pairs on its own, computed to answer a question about one file. Anchored, the
    ///     pairing is the anchor's own commits times what each of them touched, and reading them costs
    ///     two passes over <c>commit_files</c> rather than a pass per commit in the window.
    ///     The ceiling is still needed for what it was needed for: one mass commit that happens to
    ///     touch the anchor pairs it with every path in the repository at once, and those pairs are not
    ///     coupling, they are one commit. Excluding them is the reply's to explain, which is why the
    ///     count of what was excluded comes back rather than being quietly dropped.
    ///     Here rather than in <see cref="IndexQueries" />: that class is for the reads a build makes
    ///     too, and no build pairs anything.
    /// </summary>
    private static async Task<CoChanges> PairAsync(IndexReader index, HistoryWindow window, string repositorySlug,
        string pathInRepository, int maxCommitPaths, int limit, CancellationToken cancellationToken)
    {
        // Epoch seconds rather than timestamp parameters, for the reason IndexQueries compares them
        // that way: it keeps the comparison off the session time zone and a DateTimeOffset out of the
        // driver's parameter mapping.
        var parameters = new List<DuckDBParameter>
        {
            new("since", window.Since.ToUnixTimeSeconds()),
            new("until", window.Until.ToUnixTimeSeconds()),
            new("r", repositorySlug),
            new("p", pathInRepository),
            new("c", maxCommitPaths)
        };

        using var command = index.Connection.Query($"""
                                                    -- The anchor's own commits first, and everything after
                                                    -- reads only those. Narrowing here rather than later is
                                                    -- what keeps the work proportional to one file's history
                                                    -- instead of to the whole window's.
                                                    WITH anchor AS (
                                                        SELECT cf.commit_id
                                                        FROM commit_files cf
                                                        JOIN commits c USING (commit_id)
                                                        WHERE c.repo_slug = $r
                                                          AND epoch(c.authored_at) BETWEEN $since AND $until
                                                          AND cf.path = $p),
                                                    -- Every path those commits touched. The window and the
                                                    -- repository are not repeated: a commit_id from anchor
                                                    -- already satisfies both. MATERIALIZED because two CTEs
                                                    -- below read this one, and inlined it would be a second
                                                    -- scan of commit_files to produce the same rows.
                                                    touched AS MATERIALIZED (
                                                        SELECT cf.commit_id, cf.path
                                                        FROM commit_files cf JOIN anchor USING (commit_id)),
                                                    sized AS (
                                                        SELECT commit_id,
                                                               count(*) <= $c AS paired
                                                        FROM touched GROUP BY commit_id),
                                                    -- One row per commit that touched the anchor, so this
                                                    -- counts commits and not paths.
                                                    counts AS (
                                                        SELECT count(*)::INTEGER AS commits,
                                                               count(*) FILTER (WHERE paired)::INTEGER AS paired
                                                        FROM sized),
                                                    ranked AS (
                                                        SELECT t.path, count(*)::INTEGER AS shared
                                                        FROM touched t JOIN sized s USING (commit_id)
                                                        WHERE s.paired AND t.path <> $p
                                                        GROUP BY t.path
                                                        -- Spelled out rather than ordered by the alias, for
                                                        -- the reason IndexQueries spells its ORDER BY out.
                                                        ORDER BY count(*) DESC, t.path
                                                        -- Inlined and not parameterised: it is an int the
                                                        -- module has already clamped to a range, so there is
                                                        -- nothing to escape, and the churn ranking inlines
                                                        -- its own the same way.
                                                        LIMIT {limit})
                                                    -- LEFT JOIN ON TRUE so the counts survive an empty
                                                    -- ranking: a file that moves alone still has to say how
                                                    -- many commits it was looked at over. The cross join
                                                    -- does not carry ranked's order, so the outer ORDER BY
                                                    -- is what makes the ranking a ranking.
                                                    SELECT counts.commits, counts.paired, ranked.path,
                                                           ranked.shared,
                                                           {IndexQueries.AtHeadExists("$r")} AS at_head
                                                    FROM counts LEFT JOIN ranked ON TRUE
                                                    ORDER BY ranked.shared DESC, ranked.path
                                                    """, parameters);
        using var reader = await command.ReaderAsync(cancellationToken);
        var paths = await index.PathsAsync(cancellationToken);
        var files = new List<CoChangedFile>();
        int commits = 0, paired = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            commits = reader.Int32("commits");
            paired = reader.Int32("paired");
            // Null where the join found no co-changed file at all, which is one row and not none.
            if (reader.IsNull("path")) continue;
            files.Add(new CoChangedFile(paths.Format(repositorySlug, reader.Text("path")), reader.Flag("at_head"),
                reader.Int32("shared")));
        }

        return new CoChanges(commits, paired, files);
    }
}
