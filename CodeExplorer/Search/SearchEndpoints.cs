namespace CodeExplorer;

/// <summary>
///     One file as the file view shows it. <paramref name="SkipReason" /> set means the file is
///     committed but has no lines in the index, so <paramref name="Content" /> is empty for a reason
///     the view can name.
/// </summary>
internal sealed record FileContentResponse(
    string QualifiedPath,
    string RepositorySlug,
    int LineCount,
    long SizeBytes,
    string? SkipReason,
    string Content,
    FileCommitResponse? FirstCommit,
    FileCommitResponse? LastCommit);

/// <summary>
///     The commit a file was first or last changed by, as the file view names it. Both are null where
///     no history was imported, which the view says rather than drawing an empty field: "no history"
///     and "never changed" are opposite claims (CONTEXT.md, History).
/// </summary>
internal sealed record FileCommitResponse(string Sha, string AuthorName, DateTimeOffset AuthoredAt, string Subject);

/// <summary>
///     One run of consecutive lines sharing an attribution, for the blame gutter.
///     <paramref name="By" /> is null for a run the build could not attribute.
/// </summary>
internal sealed record BlameRunResponse(int StartLine, int EndLine, FileCommitResponse? By);

/// <summary>
///     A file's blame. Its own response and its own request, because it is the one history read whose
///     size is the file's rather than a row's, and the file view draws the code without waiting for it.
/// </summary>
internal sealed record BlameResponse(string QualifiedPath, IReadOnlyList<BlameRunResponse> Runs);

/// <summary>
///     One name a file imports. <paramref name="TargetPath" /> and <paramref name="Unresolved" /> are
///     exclusive: the first is a qualified path the file route opens, the second the server's own
///     prose saying why there is no path to open. The raw <paramref name="Name" /> is there either
///     way, because an edge shown only when it resolves would read as a dependency the file does not
///     have (CONTEXT.md, Import).
/// </summary>
internal sealed record ImportEdgeResponse(string Name, int LineNumber, string? TargetPath, string? Unresolved);

/// <summary>
///     What a file imports, and what the server was able to say about the question at all.
///     <paramref name="Profiled" /> and <paramref name="HasImports" /> are the two ways an empty list
///     means something other than "this file imports nothing" — an extension no profile covers was
///     never read, and a language with no import concept has none to read — and the panel draws the
///     three apart. <paramref name="Capped" /> says the list stopped at the ceiling rather than at the
///     end of the file.
/// </summary>
internal sealed record FileImportsResponse(
    string QualifiedPath,
    string LanguageName,
    bool Profiled,
    bool HasImports,
    string? Module,
    bool Capped,
    IReadOnlyList<ImportEdgeResponse> Imports);

/// <summary>One file that imports the file asked about, and the line that does it.</summary>
internal sealed record DependentResponse(string QualifiedPath, string Name, int LineNumber);

/// <summary>
///     What imports a file. <paramref name="ShareTheModule" /> and <paramref name="Unplaced" /> are
///     why an empty list is not the sentence "nothing depends on this": a module several files declare
///     resolves to none of them, and an unresolved edge spelling this file's name may be a dependency
///     the index could not place. Both are zero where the question does not arise.
/// </summary>
internal sealed record FileDependentsResponse(
    string QualifiedPath,
    string? Module,
    int ShareTheModule,
    int Unplaced,
    bool Capped,
    IReadOnlyList<DependentResponse> Dependents);

/// <summary>
///     One declaration the file page's rail lists. <paramref name="Type" /> and
///     <paramref name="Member" /> are what the line declares — either may be null, and a line that
///     reads as both fills both — and <paramref name="Text" /> is the line itself, which is what the
///     panel shows.
///     <paramref name="Role" /> is <c>"declaration"</c> or <c>"implementation"</c> where the language
///     announces a routine in one place and writes it in another, and null otherwise; a string and
///     not the enum, because every other enum on this boundary is one and a number would be a value
///     the browser has to hold a table for.
/// </summary>
internal sealed record DeclarationResponse(
    int LineNumber,
    string Text,
    string? Type,
    string? Member,
    string? Role,
    string Evidence);

/// <summary>
///     What a file declares. <paramref name="Coverage" /> is what an empty list means and
///     <paramref name="Capped" /> whether the list is short, both as
///     <see cref="DeclarationsResult" /> explains them; it is one of <c>"unprofiled"</c>,
///     <c>"unreadable"</c> or <c>"read"</c>, a lowercase name like the two on
///     <see cref="DeclarationResponse" /> and for the same reason.
/// </summary>
internal sealed record FileDeclarationsResponse(
    string QualifiedPath,
    string LanguageName,
    string Coverage,
    bool Capped,
    IReadOnlyList<DeclarationResponse> Declarations);

/// <summary>One commit of the change log, with the message body and what it did to the tree in sums.</summary>
internal sealed record CommitResponse(
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
///     A page of the change log. <see cref="Total" /> counts every commit in scope, so the page can say
///     how many there are; zero is a project or repository without history, not an error.
/// </summary>
internal sealed record CommitListResponse(long Total, int Page, int PageSize, IReadOnlyList<CommitResponse> Commits);

/// <summary>One path a commit touched; <see cref="QualifiedPath" /> links to the file when it is still at HEAD.</summary>
internal sealed record CommitFileResponse(string Path, string ChangeKind, int Added, int Deleted, string? QualifiedPath);

internal sealed record CommitFilesResponse(string Sha, IReadOnlyList<CommitFileResponse> Files);

/// <summary>
///     One file of the churn ranking. <see cref="QualifiedPath" /> is how the project names the path
///     (ADR-0006) and is always set, because a window ranks paths that are no longer at HEAD and those
///     have to be named too; <see cref="AtHead" /> is what says whether there is a file there to open.
/// </summary>
internal sealed record ChurnFileResponse(
    string QualifiedPath,
    string RepositorySlug,
    bool AtHead,
    int Commits,
    long Added,
    long Deleted);

/// <summary>
///     A churn ranking and the window it covers. <see cref="Since" /> and <see cref="Until" /> are
///     null together when the scope holds no commit at all, which is a project or a repository without
///     imported history rather than an error.
///     The window ends at the newest recorded commit rather than today (CONTEXT.md, Window), so the
///     dates are part of the answer and not an echo of the request: a reader who asked for ninety days
///     and is shown a window ending two months ago has learned that the index is stale.
///     <see cref="WithoutHistory" /> names the repositories the ranking cannot speak for, so that a
///     ranking covering half a project is not read as covering all of it (CONTEXT.md, History). Empty
///     where the question does not arise: a project of one repository, or a scoped request.
/// </summary>
internal sealed record ChurnResponse(
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    IReadOnlyList<ChurnFileResponse> Files,
    IReadOnlyList<string> WithoutHistory);

/// <summary>One file in a listing. The index's internal file id is deliberately not in it.</summary>
internal sealed record FileListEntry(
    string QualifiedPath,
    string RepositorySlug,
    int LineCount,
    long SizeBytes,
    string? SkipReason);

/// <summary>
///     A page of a listing. <paramref name="Total" /> counts every match and <paramref name="Files" />
///     holds one page of them, so the view can both page and say how much it is not showing.
///     <paramref name="PageSize" /> is echoed rather than inferred from the row count: the last page
///     is short, and a view dividing by what it received would lose a page at the end.
/// </summary>
internal sealed record FileListResponse(int Total, int Page, int PageSize, IReadOnlyList<FileListEntry> Files);

/// <summary>
///     One row of a tree listing. <paramref name="Files" /> is null for a file and counts everything
///     beneath for a directory, which is how the view tells them apart without a second field.
/// </summary>
internal sealed record TreeEntryResponse(
    string Name,
    string QualifiedPath,
    int? Files,
    long Lines,
    long SizeBytes,
    string? SkipReason);

/// <summary>
///     One level of the tree. <paramref name="Path" /> echoes the level that was asked for — empty at
///     the project root — so the view can draw a breadcrumb from the response alone.
///     <paramref name="RepositoryLevel" /> says whether the entries are repositories rather than
///     directories, which an empty path no longer implies: a single-repository project's root is
///     already inside its one repository (ADR-0006).
/// </summary>
internal sealed record TreeResponse(
    string Path,
    bool RepositoryLevel,
    IReadOnlyList<TreeEntryResponse> Entries);

/// <summary>
///     Browsing, searching and file reads for the operator UI. The same services answer the MCP tools;
///     what differs is the shape, because the browser renders the result itself where an agent is
///     handed prose.
/// </summary>
internal static class SearchEndpoints
{
    /// <summary>
    ///     Files in one page of a listing, where the client names none. Fifty rows is about a screen
    ///     and a half of the table the browse view draws, so the page below the fold is short enough
    ///     to be worth scrolling rather than a second page nobody asked for. A glob over a large
    ///     project matches thousands, and every row of them rendered at once is what this replaces.
    /// </summary>
    private const int DefaultFilePageSize = 50;

    public static void MapSearch(this RouteGroupBuilder api)
    {
        // The project is bound from the route (BoundProject), so an unknown slug is a 404 before the
        // index is asked; only a real project can answer "no index" below.
        var project = api.MapProject();

        // A project with no index answers with the explanation rather than an empty page of results:
        // "nothing matched" and "there is nothing to match against" mean opposite things. The search
        // route says it as a 400 like every other problem it has; the browsing routes below tell a 404
        // for no index from a 400 for a repository that does not exist, because the view draws them
        // differently.
        project.MapGet("/search", async (
                Project project, string q, GrepSearch search, CancellationToken ct,
                bool regex = false, bool caseSensitive = false, string? path = null,
                string? extension = null, int page = 1, int pageSize = 20) =>
            Answer<GrepResult>(await search.SearchAsync(project.Slug,
                new GrepRequest(q, regex, caseSensitive, path, Extension: extension, Page: page,
                    PageSize: pageSize), ct), Results.Ok));

        // A page and not the whole match: a glob over a large project matches thousands of files, and
        // the view was rendering every row of them. The page size is the view's, so the API answers
        // what was asked rather than a ceiling the client then has to live within.
        project.MapGet("/files",
            async (Project project, FileQueries files, CancellationToken ct, string glob = "*",
                    string? repository = null, int page = 1, int pageSize = DefaultFilePageSize) =>
                Answer<GlobListing>(
                    await files.GlobAsync(project.Slug, new GlobRequest(glob, repository, pageSize, page), ct),
                    listing => FileList(listing, pageSize)));

        project.MapGet("/tree",
            async (Project project, FileQueries files, CancellationToken ct, string path = "") =>
                Answer<TreeListing>(await files.TreeAsync(project.Slug, new TreeRequest(path, 1), ct), Tree));

        // The whole file rather than a window, because the view scrolls; the query module's ceiling is
        // what bounds it. No suggestions on a miss: the path came from a link this server printed.
        project.MapGet("/file",
            async (Project project, string path, FileQueries files, CancellationToken ct) =>
                Answer<ReadResult>(await files.ReadAsync(project.Slug,
                    new ReadRequest([new FileWindow(path, 1, int.MaxValue)], true, false), ct), FileContent));

        // Its own route rather than a flag on /file: the runs are the size of the file, and the view
        // renders the code without them and fills the gutter in when they arrive.
        project.MapGet("/file/blame",
            async (Project project, string path, HistoryQueries history, CancellationToken ct) =>
                Answer<BlameAnswer>(
                    await history.BlameAsync(project.Slug, new BlameRequest(path, 1, null), ct), Blame));

        // The two directions of the import graph, a route each rather than one that answers both:
        // they are two reads, the panels draw as each arrives, and a file page that had to wait for
        // the reverse lookup of a hub before showing what the file itself imports would be slower
        // than either answer is.
        project.MapGet("/file/imports",
            async (Project project, string path, ImportGraph graph, CancellationToken ct) =>
                Answer<ImportsResult>(await graph.ImportsAsync(project.Slug, path, ct), Imports));

        project.MapGet("/file/dependents",
            async (Project project, string path, ImportGraph graph, CancellationToken ct) =>
                Answer<DependentsResult>(await graph.DependentsAsync(project.Slug, path, ct), Dependents));

        // What the file declares, beside the two import directions, because the rail asks all three
        // of one file and draws each as it arrives. A route of its own rather than a field on /file:
        // the scan reads the lines above every candidate to place it, and the code should be on
        // screen before that comes back — the same reason blame is its own route.
        project.MapGet("/file/declarations",
            async (Project project, string path, FileDeclarations declarations, CancellationToken ct,
                    int offset = 0) =>
                Answer<DeclarationsResult>(await declarations.ForFileAsync(project.Slug, path, offset, ct),
                    Declarations));

        // The change log, paged. The files a commit touched are their own route, like blame is: a
        // page of fifty commits touching a few hundred paths each would be mostly paths nobody opens.
        project.MapGet("/commits",
            async (Project project, HistoryQueries history, CancellationToken ct, string? repository = null,
                    int page = 1, int pageSize = DefaultCommitPage) =>
                Answer<ChangeLogAnswer>(
                    await history.ChangeLogAsync(project.Slug, new ChangeLogRequest(repository, page, pageSize), ct),
                    Commits));

        // The churn ranking (CONTEXT.md, Churn), over a window of days rather than a page of commits:
        // it is a view of its own now, with nothing beside it to agree with. `days` and not a pair of
        // dates, because the window is anchored to the newest recorded commit and only the index knows
        // where that is — a client sending dates would be guessing at it.
        //
        // The route is named for the concept and the MCP tool is named `hot_files`, which is not an
        // oversight: a tool name is agent-facing and trades on the shell verbs a model already knows
        // (CODING_STANDARDS, Comments), where a URL the UI holds follows the vocabulary.
        project.MapGet("/churn",
            async (Project project, HistoryQueries history, CancellationToken ct, string? repository = null,
                    int days = HistoryWindow.DefaultDays, int limit = ChurnFilesShown) =>
                Answer<ChurnAnswer>(
                    await history.ChurnAsync(project.Slug, new ChurnRequest(repository, days, limit), ct), Churn));

        // One commit, for the page a link to a SHA opens. Its own route rather than a filter on the
        // change log: the page arrives knowing only the SHA, and finding it in the log would mean
        // paging until it turned up. Beside its files and not with them, like blame beside the file —
        // the message and the sums draw as soon as they are there, whatever the commit touched.
        project.MapGet("/commits/{sha}",
            async (Project project, string sha, HistoryQueries history, CancellationToken ct) =>
                Answer<CommitAnswer>(await history.CommitAsync(project.Slug, new CommitRequest(sha), ct), OneCommit));

        project.MapGet("/commits/{sha}/files",
            async (Project project, string sha, HistoryQueries history, CancellationToken ct) =>
                Answer<CommitFilesAnswer>(
                    await history.CommitFilesAsync(project.Slug, new CommitFilesRequest(sha), ct), CommitFiles));
    }

    /// <summary>Commits per page of the change log when the caller does not say. A screen and a bit.</summary>
    private const int DefaultCommitPage = 50;

    private static IResult Commits(ChangeLogAnswer answer) =>
        Results.Ok(new CommitListResponse(answer.Total, answer.Page, answer.PageSize,
            answer.Commits.Select(Logged).ToList()));

    /// <summary>
    ///     One logged commit as the API spells it. Written once because a page of the log and a commit's
    ///     own page answer the same ten fields, and the read behind them already goes to the trouble of
    ///     counting them once — two spellings here would undo that a layer up.
    /// </summary>
    private static CommitResponse Logged(LoggedCommit commit) =>
        new(commit.Sha, commit.RepositorySlug, commit.AuthorName, commit.AuthorEmail, commit.AuthoredAt,
            commit.Subject, commit.Body, commit.FilesChanged, commit.Added, commit.Deleted);

    /// <summary>
    ///     Files in a churn ranking when the caller does not say. A screenful: the ranking is read from
    ///     the top down, and its tail is noise.
    /// </summary>
    private const int ChurnFilesShown = 25;

    /// <summary>
    ///     The most-changed files of a window. A scope with no commits at all draws an empty ranking and
    ///     no dates rather than a failure: that is a project whose history has not been imported, which
    ///     the page says in its own words. The repositories the ranking cannot speak for ride along
    ///     either way, because an empty ranking is where a reader is most likely to conclude that
    ///     nothing changed.
    /// </summary>
    private static IResult Churn(ChurnAnswer answer) =>
        Results.Ok(new ChurnResponse(answer.Window?.Since, answer.Window?.Until,
            answer.Files
                .Select(f => new ChurnFileResponse(f.QualifiedPath, f.RepositorySlug, f.AtHead, f.Commits, f.Added,
                    f.Deleted))
                .ToList(), answer.Coverage.Without));

    /// <summary>
    ///     One commit, in the same shape the change log lists it in, so the page a link opens and the row
    ///     it was linked from read the same commit the same way. That sameness is <see cref="Logged" />
    ///     and not a second spelling of the projection, for the reason the read behind it gives.
    /// </summary>
    private static IResult OneCommit(CommitAnswer answer) => Results.Ok(Logged(answer.Commit));

    /// <summary>
    ///     The paths one commit touched. A SHA the index does not hold is a problem of kind
    ///     <see cref="ProblemKind.Missing" /> and therefore a 404 here, rather than an empty list the
    ///     page would draw as "this commit changed nothing".
    /// </summary>
    private static IResult CommitFiles(CommitFilesAnswer answer) =>
        Results.Ok(new CommitFilesResponse(answer.Sha,
            answer.Files
                // Null where HEAD no longer holds the path, which is how this response says "not a
                // link"; the read names every path either way, because a tool reply has to.
                .Select(f => new CommitFileResponse(f.Path, f.ChangeKind, f.Added, f.Deleted,
                    f.AtHead ? f.QualifiedPath : null))
                .ToList()));

    private static IResult FileList(GlobListing listing, int pageSize) =>
        Results.Ok(new FileListResponse(listing.Total, listing.Page, pageSize,
            listing.Files
                .Select(f => new FileListEntry(f.QualifiedPath, f.RepositorySlug, f.LineCount, f.SizeBytes,
                    f.SkipReason))
                .ToList()));

    /// <summary>
    ///     A level of the tree. A path that names no directory is the query module's problem and a 400
    ///     here, like a path whose first segment names no repository: a stale link is told what changed
    ///     rather than shown an empty tree, and the view draws the same "nothing here" for both.
    /// </summary>
    private static IResult Tree(TreeListing listing) =>
        Results.Ok(new TreeResponse(listing.Directory.QualifiedPath, listing.RepositoryLevel,
            listing.Entries
                .Select(e => new TreeEntryResponse(e.Name, e.QualifiedPath, e.Files, e.Lines, e.SizeBytes,
                    e.SkipReason))
                .ToList()));

    /// <summary>
    ///     The one file the view asked for. A read answers per entry, so the miss is on the entry and not
    ///     on the outcome; with one entry it is the page's answer.
    /// </summary>
    private static IResult FileContent(ReadResult result)
    {
        var read = result.Files[0];
        if (read.File is not { } file) return Status(read.Problem!);
        return Results.Ok(new FileContentResponse(file.QualifiedPath, file.RepositorySlug, file.LineCount,
            file.SizeBytes, file.SkipReason, string.Join('\n', read.Lines), Commit(read.History?.First),
            Commit(read.History?.Last)));
    }

    private static IResult Imports(ImportsResult result) =>
        Results.Ok(new FileImportsResponse(result.QualifiedPath, result.LanguageName,
            result.Profiled, result.HasImports, result.Module, result.Capped,
            result.Imports
                .Select(i => new ImportEdgeResponse(i.Name, i.LineNumber, i.TargetPath, i.Unresolved))
                .ToList()));

    /// <summary>
    ///     What a file declares, in the order the file writes them. The role and the evidence are
    ///     lowercase names rather than numbers, so the panel reads the answer instead of decoding it.
    /// </summary>
    private static IResult Declarations(DeclarationsResult result) =>
        Results.Ok(new FileDeclarationsResponse(result.QualifiedPath, result.LanguageName,
            result.Coverage.ToString().ToLowerInvariant(), result.Capped,
            result.Declarations
                .Select(d => new DeclarationResponse(d.LineNumber, d.Text, d.Type, d.Member,
                    d.Role?.ToString().ToLowerInvariant(), d.Evidence.ToString().ToLowerInvariant()))
                .ToList()));

    private static IResult Dependents(DependentsResult result) =>
        Results.Ok(new FileDependentsResponse(result.QualifiedPath, result.Module,
            result.ShareTheModule, result.Unplaced, result.Capped,
            result.Dependents
                .Select(d => new DependentResponse(d.QualifiedPath, d.Name, d.LineNumber))
                .ToList()));

    /// <summary>
    ///     A file's attribution as runs. A file with no history answers with no runs rather than a
    ///     failure: the gutter simply does not draw, and the header line is what says why. So does a
    ///     file the build kept without lines, which the query module answers the same way.
    /// </summary>
    private static IResult Blame(BlameAnswer answer) =>
        Results.Ok(new BlameResponse(answer.File.QualifiedPath,
            answer.Runs.Select(r => new BlameRunResponse(r.StartLine, r.EndLine, Commit(r.By))).ToList()));

    /// <summary>
    ///     An outcome as JSON: a problem becomes the status its kind says, and a result the shape the
    ///     route owns. The problem-to-status rule is written once here; each route supplies its own
    ///     mapping, because that is what a reader of the route wants to see beside it. The cast is safe
    ///     while every query module answers with its one result type or a problem, and throws rather
    ///     than lies if one ever answers with something else.
    /// </summary>
    private static IResult Answer<T>(Outcome outcome, Func<T, IResult> answer) where T : Outcome =>
        outcome is Problem problem ? Status(problem) : answer((T)outcome);

    private static FileCommitResponse? Commit(AttributedBy? by) =>
        by is null ? null : new FileCommitResponse(by.Sha, by.AuthorName, by.AuthoredAt, by.Subject);

    /// <summary>
    ///     No index is a 404 — the view renders it as the starting state a new project is in — and so is
    ///     a file or commit that is not there, because a link to it is a page that is not there. Every
    ///     other problem is a 400: the project is there and the request asked it something wrong.
    /// </summary>
    private static IResult Status(Problem problem) => problem.Kind is ProblemKind.NoIndex or ProblemKind.Missing
        ? Results.NotFound(new { error = problem.Explanation })
        : Results.BadRequest(new { error = problem.Explanation });
}
