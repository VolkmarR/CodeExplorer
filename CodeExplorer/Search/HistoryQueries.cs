using System.Data.Common;
using System.Globalization;
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
    string? Message = null, string? Path = null);

/// <summary>
///     A path a history read was narrowed to: the qualified path as the project spells it, and the
///     repository and repository-relative path it resolved to. Carried on the answer rather than kept
///     by the caller, because a reply that says which scope it covered must not be able to name a
///     different one from the query.
///     The scope is matched on the path a commit recorded, so it begins where a file was last renamed
///     — the caveat <c>file_history</c> already states, and one every reply here repeats: a
///     reorganised directory would otherwise read as a quiet one.
///     <see cref="AtHead" /> is why this is a record and not a bool: a path history recorded and HEAD
///     no longer holds is a scope with commits and nothing to open, and a reply that did not say so
///     would offer an agent a file that is not there (#132). It is the same fact <c>hot_files</c> and
///     <c>commit_files</c> mark on a ranked row, and it is marked in their words.
/// </summary>
/// <param name="Spelled">The qualified path, respelled by the project (ADR-0006).</param>
/// <param name="RepositorySlug">The repository the path resolved to.</param>
/// <param name="PathInRepository">The path inside it; empty is the repository's own root.</param>
/// <param name="AtHead">Whether the current file tree still holds anything at or under it.</param>
/// <param name="Lineage">
///     What this scope was called before, where a rename leads back to an earlier spelling, and null
///     where it does not or where the lookup was not run. See <see cref="PathLineage" />.
/// </param>
public sealed record PathScope(string Spelled, string RepositorySlug, string PathInRepository, bool AtHead = true,
    PathLineage? Lineage = null);

/// <summary>
///     One earlier spelling of a path scope, reached by a rename edge a commit recorded.
/// </summary>
/// <param name="Spelled">The qualified path as the project spells one (ADR-0006), so a reply can print the call.</param>
/// <param name="PathInRepository">The path inside the repository, which is what a further hop is resolved against.</param>
/// <param name="Commits">How many commits history records under it — the number the scope's own count is missing.</param>
public sealed record PreviousPath(string Spelled, string PathInRepository, int Commits);

/// <summary>
///     What a path scope was called before. A directory renamed mid-history answers for its post-rename
///     slice alone, matched by the path each commit recorded, and nothing in the reply used to say the
///     rest existed — measured at 9 commits reported for a directory with 1,252 (#131).
///     Renames are <b>signalled, not followed</b>: the scope's own count stays literal and this is
///     additive, because following them would invert a contract five tool descriptions state.
///     All three parts are needed together. <see cref="Previous" /> without
///     <see cref="CombinedCommits" /> reproduces the original fault one level up — an agent that wanted
///     a number is handed a path instead — and a reply must also spell out the call that reads the
///     previous path, or it has described work rather than handed it over.
/// </summary>
/// <param name="Previous">The chain, newest first, so the immediately previous name leads and the oldest is last.</param>
/// <param name="Omitted">Further hops the cap kept out, stated the way the churn ranking states what its exclude hid.</param>
/// <param name="CombinedCommits">Distinct commits recorded across the scope and its whole chain, cap or no cap.</param>
/// <param name="Chain">
///     Every earlier path the walk found, repository-relative and <b>untruncated</b> — the same set
///     <see cref="CombinedCommits" /> was counted over, and not the capped
///     <paramref name="Previous" /> a reply prints. A reader that paired over the printed list while
///     quoting a total taken over this one would hand back a ranking and a number that disagree, so
///     anything computing over the chain uses this (#143).
/// </param>
public sealed record PathLineage(IReadOnlyList<PreviousPath> Previous, int Omitted, int CombinedCommits,
    IReadOnlyList<string> Chain);

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

/// <summary>Everything one file's history asks for.</summary>
public sealed record FileHistoryRequest(string Path, int Limit);

/// <summary>
///     The commits that changed one file, newest first. Empty with <see cref="HasHistory" /> set is a
///     file whose history is older than what was imported or that reached its path by a rename; empty
///     without it is a project that has no history to look in.
///     <see cref="Path" /> and not the indexed file (#136): this answers for a path only the recorded
///     commit paths hold as readily as for a live one, and such a path has no row in <c>files</c> to
///     carry. <see cref="PathScope.AtHead" /> is which of the two it was, and the reply has to say it.
/// </summary>
public sealed record FileHistoryAnswer(
    PathScope Path,
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
///     <see cref="Depth" /> rolls the ranking up to the directories that many segments beneath
///     <see cref="Directory" />; null ranks files, which is what every surface but the tool asks for.
/// </summary>
/// <remarks>
///     <c>Exclude</c> is the comma-separated path terms every other filtered search here takes, and a
///     ranking needs them more than a search does: a machine-authored commit counts exactly like a
///     hand-written one, so regenerated output, a mechanical version bump or a bulk rename outranks
///     the code that drives it (#117).
/// </remarks>
public sealed record ChurnRequest(string? Directory, int Days, int Limit, int? Depth = null,
    string? Exclude = null);

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
///     <see cref="Depth" /> is the depth the rows were rolled up to, or null where they are files. It
///     is carried rather than inferred from the request, because what a reply calls its rows has to be
///     what the query grouped them by.
///     <see cref="Hidden" /> is how many paths the <c>exclude</c> terms kept out, zero where there were
///     none. A filtered ranking reads exactly like an unfiltered one, so the number is carried and said
///     rather than left for the caller to remember it asked.
/// </summary>
public sealed record ChurnAnswer(
    string ScopeSpelled,
    bool HasHistory,
    HistoryWindow? Window,
    IReadOnlyList<ChurnedFile> Files,
    HistoryCoverage Coverage,
    // Not defaulted: an answer always knows its own grain, and a default is a later construction
    // quietly calling a ranking of directories a ranking of files.
    int? Depth,
    int Hidden = 0,
    // What the scope was called before (#131), null where it was called nothing else or where no
    // directory was named. The ranking's own counts are untouched by it: renames are signalled here,
    // never followed.
    PathLineage? Lineage = null) : Outcome;

/// <summary>Everything a co-change ranking asks for.</summary>
public sealed record CoChangeRequest(string Path, int Days, int Limit);

/// <summary>
///     What the index holds for a path outside any window: how many commits recorded it at all, and
///     when the newest of them was. Read only where a window came back empty, because that is the one
///     place the two ways of being empty have opposite meanings — a path git has never recorded under
///     this spelling, usually because a rename severed its history, against one whose commits are
///     simply older than the days asked for.
/// </summary>
/// <param name="Commits">Commits recorded for the path, over the whole imported history.</param>
/// <param name="Newest">When the newest of them was authored, or null where there are none.</param>
public sealed record RecordedPath(int Commits, DateTimeOffset? Newest);

/// <summary>
///     What one file changed alongside over a window. <see cref="MaxCommitPaths" /> is the ceiling the
///     pairing ran under, carried so that the reply explaining what was excluded quotes the number that
///     was actually used rather than reading the configuration a second time.
///     <see cref="Recorded" /> is what the path has outside the window, filled only where the window
///     reached none of its commits — the branch where an empty answer has to say which kind of empty
///     it is, and the only one that pays for the extra read.
/// </summary>
public sealed record CoChangeAnswer(
    IndexedFile File,
    bool HasHistory,
    HistoryWindow? Window,
    CoChanges Coupling,
    int MaxCommitPaths,
    RecordedPath? Recorded = null,
    PathLineage? Lineage = null) : Outcome;

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
public sealed class HistoryQueries(IndexReaders readers, IConfiguration configuration)
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
    ///     Previous paths a reply names before it starts counting the rest. Three is a rename, a
    ///     reorganisation and the thing before that, which is more history than any reply is read for;
    ///     past it the note would be a listing and the combined total — the number the agent actually
    ///     wanted — would be below it (#131).
    /// </summary>
    private const int MaxPreviousPaths = 3;

    /// <summary>
    ///     Hops the chain walk takes before it gives up, whatever it has found. Each hop is one query,
    ///     and a repository whose paths were renamed in a ring — which a rename edge permits and git
    ///     does not forbid — would otherwise walk forever. Visited paths are tracked as well; this is
    ///     the second guard, and the one that bounds the cost of an honestly long chain.
    /// </summary>
    private const int MaxLineageHops = 8;

    /// <summary>
    ///     What makes an earlier prefix the scope's <i>previous path</i> rather than somewhere two files
    ///     happened to move in from: <b>more</b> than this share of the paths the scope has ever
    ///     recorded came from it — a majority. Written down rather than tuned, because the two cases it
    ///     separates are far apart: a directory rename moves nearly everything under it at once
    ///     (measured: 479 of 511 paths in one commit), and a stray file moved in over the years is a
    ///     handful out of hundreds.
    ///     A majority and not "at least half", which are the same bar everywhere except the small scopes
    ///     where the difference decides: a folder of two files that took one in from elsewhere would
    ///     clear "at least half" and be told it used to be elsewhere. A majority also guarantees that
    ///     only one candidate can qualify, so the ordering the query falls back on decides nothing.
    /// </summary>
    private const double PreviousPathShare = 0.5;

    /// <summary>
    ///     The deepest a churn ranking may be rolled up to. Ten segments below the scope is past the
    ///     depth of any layout anybody nests by hand, and past it the rollup is the file ranking with a
    ///     different name on it — which is the call the caller should be making instead.
    /// </summary>
    private const int MaxRollupDepth = 10;

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
        var outcome = await readers.OverIndexAsync(slug, request.Repository, async (index, token) =>
        {
            var (path, unresolved) = await ScopeAsync(index, request.Path, token);
            if (unresolved is not null) return unresolved;

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
                ? await MatchedAsync(index, scope, asked, path, token)
                : null;
            var commits = hasHistory && author?.Addresses != 0
                ? await CommitsAsync(index, scope, asked, mentions, path, limit, (page - 1) * limit, token)
                : [];
            return new LogAnswer(hasHistory, index.Repository, page, limit, commits, author, mentions, path);
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
        var outcome = await readers.OverIndexAsync(slug, request.Repository, async (index, token) =>
        {
            var (path, unresolved) = await ScopeAsync(index, request.Path, token);
            if (unresolved is not null) return unresolved;

            bool hasHistory = await HasHistoryAsync(index, token);
            int limit = Math.Clamp(request.Limit, 1, MaxAuthors);
            string? scope = index.Repository?.Slug;
            var authors = hasHistory ? await AuthorsAsync(index, scope, null, path, limit, token) : [];
            long total = hasHistory ? await AuthorCountAsync(index, scope, path, token) : 0;
            return new AuthorsAnswer(hasHistory, index.Repository, total, limit, authors, path);
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
        var outcome = await readers.OverIndexAsync(slug, request.Repository, async (index, token) =>
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
    ///     A path only the recorded commit paths hold is listed too, the same way a live one is (#136):
    ///     this is the log-shaped read of one path, and refusing the very path git_log had just answered
    ///     for told an agent it had made a typo it had not made.
    /// </summary>
    public async Task<Outcome> FileHistoryAsync(string slug, FileHistoryRequest request,
        CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await readers.OverIndexAsync(slug, null, async (index, token) =>
        {
            var (file, recorded, unresolved) = await OnePathAsync(index, request.Path, token);
            if (unresolved is not null) return unresolved;
            var scope = recorded
                        ?? new PathScope(file!.QualifiedPath, file.RepositorySlug, file.PathInRepository);
            scope = scope with
            {
                Lineage = await LineageAsync(index, scope.RepositorySlug, scope.PathInRepository,
                    await SpellerAsync(index, scope.RepositorySlug, token), token)
            };

            bool hasHistory = await HasHistoryAsync(index, token);
            var commits = hasHistory
                ? await PathCommitsAsync(index, scope.RepositorySlug, scope.PathInRepository,
                    Math.Clamp(request.Limit, 1, MaxCommits), token)
                : [];
            return new FileHistoryAnswer(scope, hasHistory, commits);
        }, cancellationToken);
        if (outcome is FileHistoryAnswer answer) recording.Matched(Engine, answer.Commits.Count, 0);
        else recording.Problem();
        return outcome;
    }

    /// <summary>
    ///     Where one path resolved for a tool that takes a single file. Exactly one of the three is set:
    ///     the file HEAD holds, the path only the recorded commit paths hold — always with
    ///     <see cref="PathScope.AtHead" /> false — or the refusal for a path that is neither.
    /// </summary>
    private readonly record struct OnePath(IndexedFile? File, PathScope? Recorded, Problem? Unresolved);

    /// <summary>
    ///     The three states are the ones <see cref="ScopeAsync" /> already tells apart for a scope, asked
    ///     of an exact path rather than of everything beneath one, and reached the same way — through
    ///     the file locator first, so a live path costs nothing extra and answers exactly as it did.
    ///     Only a <see cref="ProblemKind.Missing" /> is looked at twice: a path that did not parse, or
    ///     named no repository, failed before there was a path to have recorded, and its own refusal is
    ///     the right one.
    /// </summary>
    private static async Task<OnePath> OnePathAsync(IndexReader index, string path,
        CancellationToken cancellationToken)
    {
        var (file, problem) = await index.LocateAsync(path, false, cancellationToken);
        if (file is not null) return new OnePath(file, null, null);
        if (problem!.Kind is not ProblemKind.Missing) return new OnePath(null, null, problem);

        // The parse and the repository match again, over a locator that does not require a file row —
        // the only way to learn the repository-relative path of something HEAD does not hold.
        var (directory, _) = await index.LocateDirectoryAsync(path, cancellationToken);
        if (directory is not { Repository: { } repository }) return new OnePath(null, null, problem);
        if (await index.RecordedFilePathAsync(repository.Slug, directory.PathInRepository, cancellationToken)
            is not { } recorded)
            return new OnePath(null, null, problem);

        // Spelled as git recorded it and not as the caller wrote it: the lookup is case-insensitive, and
        // a reply that quoted the caller's casing back would hand them a path no later call resolves.
        var paths = await index.PathsAsync(cancellationToken);
        return new OnePath(null,
            new PathScope(paths.Format(repository.Slug, recorded), repository.Slug, recorded, false), null);
    }

    /// <summary>
    ///     The refusal the two tools that cannot answer for a historical path give instead of the
    ///     locator's (#136). The locator's says the path names no file and advises on how to spell one,
    ///     which is a report of a typo the caller did not make; this names the real reason and the reads
    ///     that do answer, so a refusal here and a signal from elsewhere tell one story.
    ///     <paramref name="because" /> is the only part that varies, because the two tools refuse for
    ///     two different reasons and a shared sentence that said neither would be worth less than both.
    /// </summary>
    private static Problem GoneFromHead(string spelled, string tool, string because) =>
        new($"'{spelled}' is recorded in this project's history and HEAD no longer holds it — a later "
            + $"commit deleted it or renamed it away. {tool} cannot answer for it: {because} "
            + "git_log and file_history read the recorded history and still answer for this path.",
            ProblemKind.Historical);

    /// <summary>
    ///     Why blame refuses a path only history records. ADR-0007 keys attribution to line ranges as of
    ///     the newest recorded commit, so there is genuinely nothing to attribute here; the refusal is
    ///     the right answer and only its wording was wrong (#136).
    /// </summary>
    private const string BlameCannot =
        "attribution is line ranges of the file as of the newest recorded commit (ADR-0007), and there is no file here to have lines.";

    /// <summary>
    ///     Why co_changed refuses one. Its pairing reads recorded commit paths and so could answer,
    ///     which is why this says the decision rather than an inability: a fourth state here would meet
    ///     the rename-severed branches of #115 and #127, and that is its own question.
    /// </summary>
    private const string CoChangedCannot =
        "its pairing is not answered for an anchor HEAD has lost, because coupling reported for a path a rename severed would read as the coupling of the file that replaced it.";

    /// <summary>
    ///     Who last changed each line of a window of one file, as runs. A file the build kept without
    ///     lines answers with no runs rather than a refusal: it is in the index, and only the
    ///     attribution has nothing to fill.
    ///     A path only history records is refused, and says why (#136). That is not a gap: attribution
    ///     is keyed to the newest recorded commit, so there is nothing here to attribute.
    /// </summary>
    public async Task<Outcome> BlameAsync(string slug, BlameRequest request, CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await readers.OverIndexAsync(slug, null,
            async (index, token) =>
            {
                var (file, recorded, unresolved) = await OnePathAsync(index, request.Path, token);
                if (unresolved is not null) return unresolved;
                if (recorded is { } gone) return GoneFromHead(gone.Spelled, "blame", BlameCannot);

                bool hasHistory = await HasHistoryAsync(index, token);
                int first = Math.Max(1, request.StartLine);
                // Anything below 1 is the end of the file, which is the caller saying "all of it" in the
                // two spellings its surface allows: an omitted argument and a zero.
                int? last = request.EndLine is null or < 1 ? null : request.EndLine;
                IReadOnlyList<AttributedLines> runs = file!.SkipReason is null
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
        var outcome = await readers.OverDirectoryAsync(slug, request.Directory,
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
                int? depth = request.Depth is { } requested ? Math.Clamp(requested, 1, MaxRollupDepth) : null;
                int limit = Math.Clamp(request.Limit, 1, MaxRankedFiles);
                var paths = await index.PathsAsync(token);
                IReadOnlyList<ChurnedFile> ranked = window is null
                    ? []
                    : depth is { } rollup
                        ? await IndexQueries.RankDirectoriesAsync(index.Connection, paths, window, repositorySlug,
                            directoryInRepository, rollup, request.Exclude, limit, token)
                        : await IndexQueries.RankAsync(index.Connection, paths, window, repositorySlug,
                            directoryInRepository, request.Exclude, limit, token);
                int hidden = window is null
                    ? 0
                    : await IndexQueries.HiddenByExcludeAsync(index.Connection, paths, window, repositorySlug,
                        directoryInRepository, request.Exclude, token);

                // A ranking of a scope that does not exist is the emptiest kind of empty answer, and a
                // path prefixed with the project's slug is the commonest way to ask for one (#111). The
                // diagnosis is the reader's, so hot_files says what read_file and list_tree say. Asked
                // of both grains: a rollup of a scope that is not there is empty for the same reason.
                if (ranked.Count == 0 && request.Directory is { } wanted
                                      && await index.DirectorySlugPrefixAdviceAsync(wanted, token) is { } slugged)
                    return new Problem(slugged);

                // Only where a directory was named: the whole project and a repository root have no
                // previous path to have, and an unscoped ranking must not pay for asking.
                var lineage = repositorySlug is null || directoryInRepository is null
                    ? null
                    : await LineageAsync(index, repositorySlug, directoryInRepository,
                        await SpellerAsync(index, repositorySlug, token), token);
                return new ChurnAnswer(spelled, hasHistory, window, ranked, coverage, depth, hidden, lineage);
            }, cancellationToken);
        if (outcome is ChurnAnswer answer) recording.Matched(Engine, answer.Files.Count, 0);
        else recording.Problem();
        return outcome;
    }

    /// <summary>
    ///     The files a window's commits changed alongside one file, most shared commits first. Scoped to
    ///     the anchor's own repository, because that is the only one whose commits could have carried it
    ///     — and so the window is that repository's newest commit, not another's.
    ///     A path only history records is refused, in the same words and with the same redirect blame
    ///     gives it (#136). The pairing could read it, and deliberately does not: an agent meeting one
    ///     story from both tools is worth more than a fourth state settled by symmetry.
    /// </summary>
    public async Task<Outcome> CoChangedAsync(string slug, CoChangeRequest request,
        CancellationToken cancellationToken)
    {
        using var recording = Telemetry.Search(slug);
        var outcome = await readers.OverIndexAsync(slug, null,
            async (index, token) =>
            {
                var (found, historical, unresolved) = await OnePathAsync(index, request.Path, token);
                if (unresolved is not null) return unresolved;
                if (historical is { } gone) return GoneFromHead(gone.Spelled, "co_changed", CoChangedCannot);
                var file = found!;

                bool hasHistory = await HasHistoryAsync(index, token);
                var window = hasHistory
                    ? await IndexQueries.WindowAsync(index.Connection, request.Days, file.RepositorySlug, token)
                    : null;
                // Resolved before the pairing rather than after it: the pairing spans the chain now, so
                // it is an input and no longer only something the reply says (#143).
                var lineage = await LineageAsync(index, file.RepositorySlug, file.PathInRepository,
                    await SpellerAsync(index, file.RepositorySlug, token), token);
                // The untruncated chain, so the ranking covers exactly what the combined total counted.
                IReadOnlyList<string> anchored = [file.PathInRepository, ..lineage?.Chain ?? []];
                var coupling = window is null
                    ? new CoChanges(0, 0, [])
                    : await PairAsync(index, window, file.RepositorySlug, anchored, _maxCommitPaths,
                        Math.Clamp(request.Limit, 1, MaxRankedFiles), token);
                // One extra read, and only on the branch that cannot answer without it: a window that
                // reached nothing has to say whether the path has any history at all.
                // Over the same chain the pairing ran on, not the current path alone: every other number
                // in this answer is chain-wide now, and a reason-for-nothing quoting the post-rename
                // slice would explain a chain-wide emptiness with a path-wide count (#143).
                var recorded = window is not null && coupling.Commits == 0
                    ? await RecordedPathAsync(index, file.RepositorySlug, anchored, token)
                    : null;
                return new CoChangeAnswer(file, hasHistory, window, coupling, _maxCommitPaths, recorded, lineage);
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
        var outcome = await readers.OverIndexAsync(slug, null,
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
        var outcome = await readers.OverIndexAsync(slug, null,
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
    ///     <see cref="IndexReaders.OverFileAsync" /> is: the caller that gets a SHA is the only one that
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
    ///     A <c>path</c> argument resolved to the scope the commit reads narrow by, or the sentence
    ///     saying why it names nothing. Both halves are null for a call that passed no path.
    ///     Whether the index holds the path is asked here and not left to an empty answer: "nothing in
    ///     this folder has been committed" and "there is no such folder" are opposite facts, and a
    ///     reply that shared one sentence for them would have an agent take a typo for a quiet module
    ///     (CODING_STANDARDS, Errors). The slug-prefix diagnosis is the reader's, so a path this
    ///     refuses is refused in the words read_file and list_tree use.
    ///     "Holds" is asked of the current tree and of the recorded commit paths both, because a path a
    ///     later commit deleted or renamed away is a scope with history and nothing to open — the third
    ///     state, which was folded into the refusal until #132 and so read as a misspelling.
    /// </summary>
    private static async Task<(PathScope? Scope, Problem? Unresolved)> ScopeAsync(IndexReader index, string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return (null, null);

        var (directory, problem) = await index.LocateDirectoryAsync(path, cancellationToken);
        if (directory is null) return (null, problem);
        // A repository root resolves with a null repository only at the project level, which a
        // non-blank path cannot name.
        if (directory.Repository is not { } repository)
            return (null, new Problem($"'{path}' names no file or directory to scope to."));

        // `repo` and `path` naming different repositories is a contradiction, and answering it would
        // make the quietest possible answer: two clauses that cannot both hold return no commit, and
        // the reply names the path alone — "no commits under 'one/src'" about a folder that is busy.
        // Refused instead, naming both, because only the caller knows which of the two it meant.
        if (index.Repository is { } scoped && !string.Equals(scoped.Slug, repository.Slug, StringComparison.Ordinal))
            return (null, new Problem(
                $"repo '{scoped.Slug}' and path '{directory.QualifiedPath}' name different repositories, so nothing can be in both. Drop repo, or pass a path inside '{scoped.Slug}'."));

        // Three states, not two. A path the tree holds is live; a path the tree does not hold but a
        // commit recorded is history with nothing left to open, which hot_files already ranks and
        // which these tools refused as a typo until #132; a path in neither is the refusal below.
        // Which of the first two matched is carried on the scope, because the answer has to say it.
        bool atHead = await index.HoldsPathAsync(repository.Slug, directory.PathInRepository, cancellationToken);
        if (!atHead && !await index.RecordsPathAsync(repository.Slug, directory.PathInRepository, cancellationToken))
            return (null, new Problem(
                await index.DirectorySlugPrefixAdviceAsync(path, cancellationToken)
                ?? $"'{directory.QualifiedPath}' names nothing in this index — no file is at that path and none is under it. Use glob or list_tree to locate it.",
                ProblemKind.Missing));

        // The previous-path lookup runs here and nowhere else, so every reply that renders a scope
        // renders the note the same way — and only for a call that named a path, which is the branch
        // that needs it. Never gated on an empty answer: the case this exists for returned nine
        // commits, and a zero-gated check would have skipped it (#131).
        return (new PathScope(directory.QualifiedPath, repository.Slug, directory.PathInRepository, atHead,
            await LineageAsync(index, repository.Slug, directory.PathInRepository,
                await SpellerAsync(index, repository.Slug, cancellationToken), cancellationToken)), null);
    }

    /// <summary>
    ///     How this index turns a repository-relative path back into the qualified one an agent would
    ///     type (ADR-0006). Read once per scope rather than per hop: the rule is a property of the
    ///     project, and the chain walk would otherwise ask for it three times to spell three paths.
    /// </summary>
    private static async Task<Func<string, string>> SpellerAsync(IndexReader index, string repositorySlug,
        CancellationToken cancellationToken)
    {
        var paths = await index.PathsAsync(cancellationToken);
        return path => paths.Format(repositorySlug, path);
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
        string? author, string? message, PathScope? path, int limit, int skip, CancellationToken cancellationToken)
    {
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug, author, message,
            path?.RepositorySlug, path?.PathInRepository);
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

    /// <summary>
    ///     What the whole imported history holds for one path of one repository, matched the same way
    ///     <see cref="PathCommitsAsync" /> matches it — on the path the commit recorded, so a rename
    ///     severs it. That is the point: a path with zero commits here is almost always a path that was
    ///     renamed, and a path with commits older than the window is a quiet file. The two are opposite
    ///     facts and this is what tells them apart.
    /// </summary>
    /// <summary>
    ///     What a path scope was called before, or null where nothing leads back. Walked hop by hop: the
    ///     dominant previous prefix of the scope, then of that prefix, and so on, so
    ///     <c>src/Model</c> ← <c>model</c> ← <c>Model</c> comes back as a chain rather than one step.
    ///     Bounded three ways, because a rename edge is data and data can be shaped badly:
    ///     <see cref="MaxLineageHops" /> hops, a visited set against a rename ring, and
    ///     <see cref="MaxPreviousPaths" /> reported with the rest counted as omitted.
    ///     Signalled and not followed (#131): nothing here changes what the scope's own query counts.
    ///     The combined total is read last and covers the whole chain the walk found, including the hops
    ///     the cap does not name — it is the number the caller wanted, and naming fewer paths must not
    ///     shrink it.
    /// </summary>
    /// <param name="index">The index being read; the walk is several queries over one open.</param>
    /// <param name="repositorySlug">The repository the scope resolved to. A rename never crosses one.</param>
    /// <param name="pathInRepository">The scope inside it. Empty — a repository root — has no previous path.</param>
    /// <param name="spell">How to turn a repository-relative path back into a qualified one (ADR-0006).</param>
    /// <param name="cancellationToken">Threaded to every query the walk runs.</param>
    private static async Task<PathLineage?> LineageAsync(IndexReader index, string repositorySlug,
        string pathInRepository, Func<string, string> spell, CancellationToken cancellationToken)
    {
        if (pathInRepository.Length == 0) return null;

        var found = new List<PreviousPath>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { pathInRepository };
        string current = pathInRepository;
        for (int hop = 0; hop < MaxLineageHops; hop++)
        {
            if (await PreviousPathAsync(index, repositorySlug, current, cancellationToken) is not { } previous
                || !visited.Add(previous))
                break;
            found.Add(new PreviousPath(spell(previous), previous,
                await PathCommitCountAsync(index, repositorySlug, previous, cancellationToken)));
            current = previous;
        }

        if (found.Count == 0) return null;
        var chain = found.Select(p => p.PathInRepository).ToList();
        int combined = await CombinedCommitCountAsync(index, repositorySlug, [pathInRepository, ..chain],
            cancellationToken);
        return new PathLineage(found.Take(MaxPreviousPaths).ToList(),
            Math.Max(0, found.Count - MaxPreviousPaths), combined, chain);
    }

    /// <summary>
    ///     The one path a scope was renamed from, or null where no earlier prefix accounts for enough of
    ///     it. Derived from file-level rename rows by <b>prefix mapping</b>: a row whose new path is the
    ///     scope, or sits under it, and whose old path ends with the same remainder, differs from it by
    ///     a leading prefix alone — and that prefix is the candidate.
    ///     Candidates are then weighed against how many distinct paths the scope has ever recorded, not
    ///     against how many rows were renamed, which is the denominator that separates a directory
    ///     rename from two files that moved in: renamed rows alone would make any scope whose only
    ///     rename edges came from one place look like it had been that place.
    ///     <see cref="PreviousPathShare" /> is the bar and it must be cleared outright, which leaves at
    ///     most one candidate for any scope of two paths or more. A scope of one — a file — has a
    ///     denominator of one, so every candidate clears it, and a file renamed more than once over its
    ///     history offers several; the newest rename wins, which is the one the caller is standing on,
    ///     with the path itself as a last tiebreak so the answer is the same every time it is asked.
    ///     A candidate on the scope's own branch is rejected whichever way it points. An ancestor —
    ///     <c>src</c> offered for <c>src/Model</c>, because most of <c>src</c> was moved down into it —
    ///     would report a combined total covering all of <c>src</c>, which is the inflated number this
    ///     feature exists to correct, pointing backwards. A descendant is the mirror: the scope's own
    ///     count already prefix-matches it, so "N across the whole chain" would equal "this path's
    ///     alone" and the note would say nothing while looking like it said something.
    ///     Only <c>renamed</c> edges count. A <c>copied</c> one is stored (ADR-0007) and is not a
    ///     previous path: both sides still exist, so "this scope was renamed" would be false and the
    ///     combined total would sum two live paths.
    /// </summary>
    private static async Task<string?> PreviousPathAsync(IndexReader index, string repositorySlug, string path,
        CancellationToken cancellationToken)
    {
        using var command = index.Connection.Query("""
                                                   WITH scoped AS (
                                                       SELECT cf.path, cf.change_kind, cf.old_path, c.commit_id
                                                       FROM commit_files cf JOIN commits c USING (commit_id)
                                                       WHERE c.repo_slug = $r
                                                         AND (cf.path = $p OR starts_with(cf.path, $p || '/'))
                                                   ),
                                                   -- Every path the scope has ever recorded. The share a
                                                   -- candidate has to clear is measured against this and
                                                   -- not against the renamed rows, so a scope of five
                                                   -- hundred files that took two in from elsewhere does
                                                   -- not read as having been elsewhere.
                                                   recorded AS (SELECT count(DISTINCT path) AS paths FROM scoped),
                                                   -- The remainder each row sits at below the scope, and
                                                   -- the prefix its old path would have had to carry to
                                                   -- hold the same remainder. An old path that does not
                                                   -- end in that remainder moved sideways, not wholesale,
                                                   -- and drops out with a NULL.
                                                   mapped AS (
                                                       SELECT CASE
                                                           WHEN path = $p THEN old_path
                                                           WHEN ends_with(old_path, '/' || substr(path, length($p) + 2))
                                                               THEN substr(old_path, 1,
                                                                    length(old_path) - length(substr(path, length($p) + 2)) - 1)
                                                       END AS previous,
                                                       path, commit_id
                                                       FROM scoped
                                                       -- renamed only. A copied edge is stored too and
                                                       -- is not a previous path: both sides still exist.
                                                       WHERE old_path IS NOT NULL AND change_kind = 'renamed'
                                                   )
                                                   SELECT previous
                                                   FROM mapped, recorded
                                                   WHERE previous IS NOT NULL AND previous <> ''
                                                     -- Never the scope's own branch, either way up it.
                                                     AND previous <> $p
                                                     AND NOT starts_with(previous, $p || '/')
                                                     AND NOT starts_with($p, previous || '/')
                                                   GROUP BY previous, recorded.paths
                                                   HAVING count(DISTINCT path) > recorded.paths * $share
                                                   -- Share first, then the newest rename, then the path
                                                   -- itself: a file scope has a denominator of one, so
                                                   -- several candidates can qualify and the answer must
                                                   -- not depend on the order rows came back in.
                                                   ORDER BY count(DISTINCT path) DESC, max(commit_id) DESC, previous
                                                   LIMIT 1
                                                   """,
            [
                new DuckDBParameter("r", repositorySlug), new DuckDBParameter("p", path),
                new DuckDBParameter("share", PreviousPathShare)
            ]);
        return await command.ScalarAsync(cancellationToken) as string;
    }

    /// <summary>Commits recorded at or under one repository-relative path, over the whole history.</summary>
    private static async Task<int> PathCommitCountAsync(IndexReader index, string repositorySlug, string path,
        CancellationToken cancellationToken) =>
        await CombinedCommitCountAsync(index, repositorySlug, [path], cancellationToken);

    /// <summary>
    ///     Distinct commits recorded at or under any of these repository-relative paths. Distinct
    ///     because one commit is very often the rename itself, which touches both sides of the chain and
    ///     would otherwise be counted once per path and inflate the very number the note exists to get
    ///     right.
    /// </summary>
    private static async Task<int> CombinedCommitCountAsync(IndexReader index, string repositorySlug,
        IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var parameters = new List<DuckDBParameter> { new("r", repositorySlug) };
        var clauses = new List<string>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            parameters.Add(new DuckDBParameter("p" + i.ToString(CultureInfo.InvariantCulture), paths[i]));
            clauses.Add($"cf.path = $p{i} OR starts_with(cf.path, $p{i} || '/')");
        }

        using var command = index.Connection.Query($"""
                                                    SELECT count(DISTINCT cf.commit_id)
                                                    FROM commit_files cf JOIN commits c USING (commit_id)
                                                    WHERE c.repo_slug = $r AND ({string.Join(" OR ", clauses)})
                                                    """, parameters);
        return (int)((await command.ScalarAsync(cancellationToken) as long?) ?? 0);
    }

    /// <param name="index">The index being read.</param>
    /// <param name="repositorySlug">The repository the paths belong to.</param>
    /// <param name="paths">
    ///     The anchor's path, and every earlier one a rename leads back to. A list and not one path
    ///     because the pairing this explains the emptiness of spans the same list (#143), and the two
    ///     have to be counted over the same thing or the explanation contradicts the answer.
    /// </param>
    /// <param name="cancellationToken">Threaded to the command.</param>
    private static async Task<RecordedPath> RecordedPathAsync(IndexReader index, string repositorySlug,
        IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var parameters = new List<DuckDBParameter> { new("r", repositorySlug) };
        var names = new List<string>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            parameters.Add(new DuckDBParameter("p" + i.ToString(CultureInfo.InvariantCulture), paths[i]));
            names.Add("$p" + i.ToString(CultureInfo.InvariantCulture));
        }

        using var command = index.Connection.Query($"""
                                                    SELECT count(DISTINCT cf.commit_id) AS commits,
                                                           max(c.authored_at) AS newest
                                                    FROM commit_files cf JOIN commits c USING (commit_id)
                                                    WHERE c.repo_slug = $r AND cf.path IN ({string.Join(", ", names)})
                                                    """, parameters);
        using var reader = await command.ReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new RecordedPath(0, null);
        int commits = (int)reader.Int64("commits");
        return new RecordedPath(commits,
            commits == 0 || reader.IsNull("newest")
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("newest")));
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
        string? author, PathScope? path, int limit, CancellationToken cancellationToken)
    {
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug, author, null,
            path?.RepositorySlug, path?.PathInRepository);
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
    private static async Task<long> AuthorCountAsync(IndexReader index, string? repositorySlug, PathScope? path,
        CancellationToken cancellationToken)
    {
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug, null, null,
            path?.RepositorySlug, path?.PathInRepository);
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
        PathScope? path, CancellationToken cancellationToken)
    {
        var (addresses, commits) = await MatchCountsAsync(index, repositorySlug, author, path, cancellationToken);
        var matched = addresses == 0
            ? []
            : await AuthorsAsync(index, repositorySlug, author, path, MaxAuthors, cancellationToken);
        return new AuthorFilter(author, commits, addresses, matched,
            addresses > 0 ? 0 : await AuthorCountAsync(index, repositorySlug, path, cancellationToken));
    }

    /// <summary>How many addresses a filter matched and how many commits they have between them.</summary>
    private static async Task<(long Addresses, long Commits)> MatchCountsAsync(IndexReader index,
        string? repositorySlug, string author, PathScope? path, CancellationToken cancellationToken)
    {
        var (scope, parameters) = IndexQueries.CommitScope(repositorySlug, author, null,
            path?.RepositorySlug, path?.PathInRepository);
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
    /// <remarks>
    ///     <paramref name="anchorPaths" /> is the anchor's own path and every earlier one a rename leads
    ///     back to, so coupling survives a move (#143). It is the one place in this system where a
    ///     rename is followed rather than signalled: every other path-scoped read counts what its own
    ///     path recorded (CONTEXT.md, Previous Path), and the exception is here because <c>git_log</c>
    ///     and <c>file_history</c> can answer for a previous path while nothing can answer its coupling.
    ///     In the commit that moved the anchor, a counterpart that <b>also moved</b> is dropped and one
    ///     that was edited is kept. Dropping the whole commit was the first shape and it was too blunt:
    ///     a commit that renames a file and updates its two callers is three paths, nowhere near the
    ///     ceiling, and is the strongest coupling evidence the anchor has. What is not evidence is the
    ///     other half of the same commit — "these moved together" is one commit and not a relationship,
    ///     which is the paths ceiling's reasoning applied per path instead of per commit. Splitting it
    ///     this way needs no second threshold, and leaves the commit counted and paired as it is.
    /// </remarks>
    private static async Task<CoChanges> PairAsync(IndexReader index, HistoryWindow window, string repositorySlug,
        IReadOnlyList<string> anchorPaths, int maxCommitPaths, int limit, CancellationToken cancellationToken)
    {
        // Epoch seconds rather than timestamp parameters, for the reason IndexQueries compares them
        // that way: it keeps the comparison off the session time zone and a DateTimeOffset out of the
        // driver's parameter mapping.
        var parameters = new List<DuckDBParameter>
        {
            new("since", window.Since.ToUnixTimeSeconds()),
            new("until", window.Until.ToUnixTimeSeconds()),
            new("r", repositorySlug),
            new("c", maxCommitPaths)
        };
        // One parameter per path of the chain, which is a handful at most: the walk is capped and a
        // path is a name, so there is nothing here to build a list literal out of by hand.
        var names = new List<string>(anchorPaths.Count);
        for (int i = 0; i < anchorPaths.Count; i++)
        {
            string name = "p" + i.ToString(CultureInfo.InvariantCulture);
            parameters.Add(new DuckDBParameter(name, anchorPaths[i]));
            names.Add("$" + name);
        }

        string anchored = string.Join(", ", names);

        using var command = index.Connection.Query($"""
                                                    -- The anchor's own commits first, and everything after
                                                    -- reads only those. Narrowing here rather than later is
                                                    -- what keeps the work proportional to one file's history
                                                    -- instead of to the whole window's.
                                                    WITH anchor AS (
                                                        SELECT cf.commit_id,
                                                               -- Whether this commit is the one that moved
                                                               -- the anchor. A rename is one row, carrying
                                                               -- the new path, so only the commit that did
                                                               -- the moving matches.
                                                               bool_or(cf.change_kind = 'renamed') AS moved
                                                        FROM commit_files cf
                                                        JOIN commits c USING (commit_id)
                                                        WHERE c.repo_slug = $r
                                                          AND epoch(c.authored_at) BETWEEN $since AND $until
                                                          AND cf.path IN ({anchored})
                                                        GROUP BY cf.commit_id),
                                                    -- Every path those commits touched. The window and the
                                                    -- repository are not repeated: a commit_id from anchor
                                                    -- already satisfies both. MATERIALIZED because two CTEs
                                                    -- below read this one, and inlined it would be a second
                                                    -- scan of commit_files to produce the same rows.
                                                    -- `moved` is carried through rather than joined again
                                                    -- below: anchor groups over commit_files, and reading
                                                    -- it twice would run that scan twice for one answer.
                                                    touched AS MATERIALIZED (
                                                        SELECT cf.commit_id, cf.path, cf.change_kind,
                                                               anchor.moved
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
                                                        -- The whole chain and not just the current path:
                                                        -- an anchor's own earlier name is not a file it
                                                        -- co-changed with.
                                                        WHERE s.paired AND t.path NOT IN ({anchored})
                                                          -- In the commit that moved the anchor, a path
                                                          -- that moved with it says only "these moved
                                                          -- together"; one that was edited in the same
                                                          -- commit is real coupling and stays.
                                                          AND NOT (t.moved AND t.change_kind = 'renamed')
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
