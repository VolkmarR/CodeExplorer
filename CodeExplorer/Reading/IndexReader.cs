using System.Data.Common;
using DuckDB.NET.Data;

namespace CodeExplorer;
/// <summary>
///     One project's index, open for one call. <see cref="IndexReaders.OverIndexAsync{T}" /> is the seam
///     every reader crosses — the MCP tools, the operator UI's endpoints and the three text searches —
///     and what it hands back is this: a connection bound to the project for one call (ADR-0003)
///     together with the answers every reader used to derive for itself: which repositories the build
///     read, how this project spells a path (ADR-0006), and whether a <c>repo</c> argument names one of
///     them. The read-only questions that are not text searches — what exists, what a file says, how big
///     things are — are asked of the reader too. Glob matching is the SQL <c>GLOB</c> operator
///     (ADR-0004), so <c>*</c> crosses <c>/</c> and there is no brace expansion.
///     Callers never construct or keep one: <see cref="IndexReaders" /> opens it, hands it over for the
///     length of one call and disposes it, and that dispose is what lets a swap proceed.
/// </summary>
public sealed partial class IndexReader : IDisposable
{
    /// <summary>
    ///     Ceiling on one file listing, for the agent's glob and the operator's browse alike. Enough to
    ///     show every handler in a mid-sized service or browse a repository's whole source tree in one
    ///     page; past it the listing is not a thing to read, and the total in the answer says so.
    /// </summary>
    public const int MaxFiles = 2000;

    // The join is for the slug only; qualified_path already carries it as a prefix, but splitting a
    // string to recover what a column holds would be the worse choice.
    private const string FileColumns =
        "SELECT f.file_id, f.qualified_path, r.slug, f.path, f.line_count, f.size_bytes, f.skip_reason, f.module";

    private const string FileSource = "FROM files f JOIN repositories r USING (repo_id)";

    // The lease itself, held only to be disposed. Its type is named in IndexReaders and nowhere else
    // in Reading, which is what keeps the one allowed arrow into Index to a single file.
    private readonly IDisposable _lease;

    private readonly bool _fullTextLoaded;

    // Loaded together, once, on first need: which repositories the build read and how it named their
    // files are one statement's worth of the same row set, and a read_file that only needs a line
    // range should not pay for either.
    private IReadOnlyList<IndexedRepository>? _repositories;
    private ProjectPaths? _paths;

    internal IndexReader(DuckDBConnection connection, bool fullTextLoaded, IDisposable lease, string projectSlug)
    {
        Connection = connection;
        _fullTextLoaded = fullTextLoaded;
        _lease = lease;
        ProjectSlug = projectSlug;
    }

    /// <summary>The project the connection is bound to, named in every explanation so an agent on the wrong endpoint sees it.</summary>
    public string ProjectSlug { get; }

    /// <summary>
    ///     The repository the <c>repo</c> argument resolved to, in the spelling the index holds, or null
    ///     when the call covers every repository. Set by <see cref="ScopeToAsync" />; an unknown slug
    ///     never gets this far.
    /// </summary>
    public IndexedRepository? Repository { get; private set; }

    /// <summary>A connection already <c>USE</c>ing the project, for the searches that write their own SQL.</summary>
    public DuckDBConnection Connection { get; }

    /// <summary>
    ///     Whether a full-text search can answer from this index: the file holds a BM25 index and this
    ///     process has the extension to query it. Both are asked, because they come apart: an index
    ///     built with full text survives a restart under <c>Index:SearchEngine=Substring</c>, and one
    ///     built without it is not given one by loading the extension later. The decision is per index,
    ///     not per process, and this is the one place it is made.
    /// </summary>
    public async Task<bool> HasFullTextAsync(CancellationToken cancellationToken)
    {
        if (!_fullTextLoaded) return false;
        using var command = Connection.Query("SELECT fts_indexed FROM index_info", []);
        return await command.ScalarAsync(cancellationToken) is true;
    }

    /// <summary>Releases the lease as well as the connection, which is what lets a swap proceed.</summary>
    public void Dispose() => _lease.Dispose();

    /// <summary>
    ///     Narrows every later question to the repository the caller's <c>repo</c> argument names, or
    ///     answers the <see cref="Problem" /> saying it names none of them. A blank argument covers every
    ///     repository and is not a narrowing, so it answers null and changes nothing.
    ///     The match is case-insensitive the way a path is, so an agent quoting a slug from memory in the
    ///     wrong case is not told the repository does not exist.
    /// </summary>
    internal async Task<Problem?> ScopeToAsync(string? repository, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repository)) return null;

        string wanted = repository.Trim();
        if (await FindRepositoryAsync(wanted, cancellationToken) is not { } found)
            return new Problem(
                $"{await UnknownRepositoryAsync(wanted, cancellationToken)} Drop `repo` to cover every repository.");

        Repository = found;
        return null;
    }

    /// <summary>The one explanation every reader gives when there is nothing to read from.</summary>
    public static string NoIndex(string projectSlug) =>
        $"Project '{projectSlug}' has no index to read from right now: it was never built, or a refresh is still building the first one. "
        + $"Ask the operator to refresh it with POST /api/projects/{projectSlug}/refresh, or retry shortly.";

    /// <summary>
    ///     The explanation for the other way there is nothing to read: the file is there, and an older
    ///     version of this server wrote it. Told apart from <see cref="NoIndex" /> because "it was never
    ///     built" would send an agent looking for a project that is in fact indexed, and because the
    ///     remedy is the same refresh either way only once somebody knows which one it is (#164).
    /// </summary>
    public static string OutdatedIndex(string projectSlug) =>
        $"Project '{projectSlug}' has an index this version of the server cannot read: an older one built it, and only a refresh rewrites it. "
        + $"Ask the operator to refresh it with POST /api/projects/{projectSlug}/refresh, or retry shortly.";

    /// <summary>What the last build read, in build order, which is what a path may name.</summary>
    public async Task<IReadOnlyList<IndexedRepository>> RepositoriesAsync(CancellationToken cancellationToken)
    {
        await LoadShapeAsync(cancellationToken);
        return _repositories!;
    }

    /// <summary>
    ///     How this index named its files, read from the index itself rather than from the control
    ///     database: a reader answers from the index alone, and an index names things the way the build
    ///     that wrote it was told to, which is the only shape its rows can be read in (ADR-0005,
    ///     ADR-0006).
    /// </summary>
    public async Task<ProjectPaths> PathsAsync(CancellationToken cancellationToken)
    {
        await LoadShapeAsync(cancellationToken);
        return _paths!;
    }

    /// <summary>The repository a slug names, in the spelling the index holds; null when none does.</summary>
    public async Task<IndexedRepository?> FindRepositoryAsync(string slug, CancellationToken cancellationToken)
    {
        var repositories = await RepositoriesAsync(cancellationToken);
        return repositories.FirstOrDefault(r => string.Equals(r.Slug, slug, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     The sentence for a slug that names no repository: the fact and what would have been valid,
    ///     without advice, because what to do next depends on where the slug came from — a <c>repo</c>
    ///     argument is dropped, the first segment of a path is corrected.
    /// </summary>
    public async Task<string> UnknownRepositoryAsync(string slug, CancellationToken cancellationToken) =>
        $"No repository '{slug}' in project '{ProjectSlug}'. Repositories: {await SlugsAsync(cancellationToken)}.";

    /// <summary>
    ///     What a path in this project is made of, for the message that says one was not. The two
    ///     shapes are described in one place so a tool cannot explain one project's naming in the
    ///     other's words (ADR-0006).
    /// </summary>
    public async Task<string> PathRuleAsync(CancellationToken cancellationToken) =>
        (await PathsAsync(cancellationToken)).SingleRepository
            ? "this project holds one repository, so a path is the path inside it."
            : $"a qualified path must start with a repository slug, then the path inside it. Repositories: {await SlugsAsync(cancellationToken)}.";

    /// <summary>
    ///     The sentence for the one wrong path an agent writes over and over: a qualified path in a
    ///     single-repository project, prefixed with the slug the project is known by. ADR-0006 names
    ///     files there without one, so the prefix parses as a directory inside the repository and the
    ///     miss that follows names the very repository the agent meant — which reads as confirmation
    ///     that the prefix was right. Three agents in the Edilverso evaluation lost a round-trip to it.
    ///     Null where the path is not that mistake, so a caller adds this and keeps its own wording
    ///     for everything else.
    ///     The corrected path is only offered once something is found at it. A repository whose slug is
    ///     also a directory inside it — a repository called `src` — would otherwise be told to drop a
    ///     segment that was correct, which is the same wrong turn pointing the other way.
    /// </summary>
    /// <param name="path">The path an agent wrote, as it wrote it.</param>
    /// <param name="cancellationToken">Threaded to the probe.</param>
    public async Task<string?> FileSlugPrefixAdviceAsync(string path, CancellationToken cancellationToken)
    {
        if (await SlugPrefixAsync(path, cancellationToken) is not { } prefix) return null;
        return await FindFileAsync(prefix.Corrected, cancellationToken) is null
            ? null
            : SlugAdvice(prefix.Slug, prefix.Corrected);
    }

    /// <inheritdoc cref="FileSlugPrefixAdviceAsync" />
    /// <remarks>
    ///     The same mistake made of a directory, which is there when anything is under it. Beside the
    ///     file flavour rather than probed by the caller: the tree's reader is here, and a caller that
    ///     built the probe itself would be rebuilding the parse this already did.
    /// </remarks>
    public async Task<string?> DirectorySlugPrefixAdviceAsync(string path, CancellationToken cancellationToken)
    {
        if (await SlugPrefixAsync(path, cancellationToken) is not { } prefix) return null;
        var paths = await PathsAsync(cancellationToken);
        // A file at the corrected path counts too. `list_tree("alpha/README.md")` is the slug mistake
        // and a file named as a directory at once, and suppressing the diagnosis because the corrected
        // path holds no children would answer the smaller of the two questions. The path probe takes
        // the exact spelling and the file lookup the case an agent misremembered, so both are asked.
        if (!await HoldsPathAsync(paths.RepositorySlug, prefix.Corrected, cancellationToken)
            && await FindFileAsync(prefix.Corrected, cancellationToken) is null) return null;
        return SlugAdvice(prefix.Slug, prefix.Corrected);
    }

    /// <summary>
    ///     The slug an agent prefixed and the path without it, or null where the path is not that
    ///     mistake. The probe is the caller's because only the kind of thing being looked for differs;
    ///     everything up to it is one rule.
    /// </summary>
    private async Task<(string Slug, string Corrected)?> SlugPrefixAsync(string path,
        CancellationToken cancellationToken)
    {
        var paths = await PathsAsync(cancellationToken);
        // A multi-repository project is the case where the first segment really is a repository slug,
        // so there is nothing here to diagnose.
        if (!paths.SingleRepository) return null;

        string written = path.Trim().Replace('\\', '/').Trim('/');
        int slash = written.IndexOf('/');
        // No second segment is no correction to offer: `read_file("edilverso")` is a path that names a
        // directory, which the miss it gets already says.
        if (slash < 0) return null;

        // One comparison covers both halves of the mistake: a single-repository project's one
        // repository is named after the project (ADR-0006, ControlDatabase.AddRepositoryAsync), so the
        // slug an agent read off a project list and the slug it read off a path are the same string.
        string first = written[..slash];
        if (!first.Equals(paths.RepositorySlug, StringComparison.OrdinalIgnoreCase)) return null;

        string corrected = written[(slash + 1)..];
        return corrected.Length == 0 ? null : (first, corrected);
    }

    /// <summary>
    ///     The sentence itself, written once because the file miss and the tree miss are one mistake
    ///     and an agent that meets it in both must be told the same rule in the same words.
    /// </summary>
    private static string SlugAdvice(string slug, string corrected) =>
        $"'{slug}' is the repository, not a directory in it: this project holds one repository "
        + $"and names its files without the slug, so '{slug}/' was read as a folder and matched "
        + $"nothing. The path is '{corrected}'.";

    /// <summary>
    ///     Compared lower-cased on both sides: git paths are case-sensitive, but an agent quoting a path
    ///     from memory gets the case wrong far more often than a repository holds two files differing
    ///     only by case. An exact match wins if both exist.
    /// </summary>
    public async Task<IndexedFile?> FindFileAsync(string qualifiedPath, CancellationToken cancellationToken)
    {
        using var command = Connection.Query($"""
                                              {FileColumns}
                                              {FileSource}
                                              WHERE lower(f.qualified_path) = lower($p)
                                              ORDER BY f.qualified_path = $p DESC
                                              LIMIT 1
                                              """, [new DuckDBParameter("p", qualifiedPath)]);
        using var reader = await command.ReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFile(reader) : null;
    }

    /// <summary>
    ///     The file a qualified path names, or the sentence saying why it names none. A path cannot
    ///     be read without knowing how the project names its files (ADR-0006), and an agent that got
    ///     it wrong needs the rule rather than an empty answer — so the misses are answers here and
    ///     not exceptions, the way CODING_STANDARDS asks.
    ///     It is on the reader rather than in each tool because three tools were resolving a path by
    ///     spelling this sequence out, and the three refusal sentences an agent reads had begun to
    ///     drift apart. Most callers reach it through <see cref="IndexReaders.OverFileAsync{T}" />; this is for the
    ///     one that resolves several paths over one open.
    /// </summary>
    /// <param name="path">The qualified path an agent wrote.</param>
    /// <param name="suggestions">
    ///     Whether a miss should offer the files elsewhere in the project with the same leaf name.
    ///     Worth it where the agent chose the path, and noise where it came from a list this
    ///     produced.
    /// </param>
    /// <param name="cancellationToken">Threaded to every command this runs.</param>
    public async Task<(IndexedFile? File, Problem? Problem)> LocateAsync(string path, bool suggestions,
        CancellationToken cancellationToken)
    {
        var paths = await PathsAsync(cancellationToken);
        var qualified = paths.Parse(path);
        if (qualified is null || qualified.PathInRepository.Length == 0)
            return (null, new Problem(
                $"'{path}' names no file: {await PathRuleAsync(cancellationToken)} Write it like `{paths.Example()}`."));

        var (repository, spelled, unknown) = await RespellAsync(qualified, cancellationToken);
        if (repository is null) return (null, unknown);
        if (await FindFileAsync(spelled, cancellationToken) is { } file) return (file, null);

        string explanation = $"No indexed file '{spelled}' in repository '{repository.Slug}' of project "
                             + $"'{ProjectSlug}'. ";

        // Ahead of both tails below, and instead of them: where this fires it is the whole reason the
        // path missed, and a "did you mean" under it would offer the same file as a guess after the
        // sentence that already named it.
        if (await FileSlugPrefixAdviceAsync(path, cancellationToken) is { } slugged)
            return (null, new Problem(explanation + slugged, ProblemKind.Missing));

        if (!suggestions)
            return (null, new Problem(explanation + "Use glob or list_tree to locate it.", ProblemKind.Missing));

        string name = qualified.PathInRepository[(qualified.PathInRepository.LastIndexOf('/') + 1)..];
        var similar = await FilesNamedAsync(name, MaxSuggestions, cancellationToken);
        return (null, new Problem(explanation + (similar.Count > 0
                ? $"Did you mean {string.Join(" or ", similar)}? Otherwise use glob or list_tree to locate it."
                : "Use glob or list_tree to locate it; the path is case-insensitive here but must otherwise match the committed path."),
            ProblemKind.Missing));
    }

    /// <summary>
    ///     The directory a path names, or the sentence saying why it names none. A blank path is the
    ///     project level, and a repository slug alone is that repository's root. Whether
    ///     the directory holds anything is not asked: that is a fact about the tree, and the tree's
    ///     reader is the one place to say "this is a file" or "nothing is here" in one wording.
    ///     It exists for the reason <see cref="LocateAsync" /> does: three tools had spelled out the
    ///     parse, the repository match and the respelling for themselves.
    /// </summary>
    public async Task<(IndexedDirectory? Directory, Problem? Problem)> LocateDirectoryAsync(string? path,
        CancellationToken cancellationToken)
    {
        var paths = await PathsAsync(cancellationToken);
        // Null from Parse is the project level, which only a multi-repository project has and which a
        // non-blank path cannot mean: one that parses to it — a bare `/` — names nothing.
        // Blank is the project level whatever the project's shape; what a single-repository project,
        // which has no such level, makes of it is the caller's question (ADR-0006).
        if (string.IsNullOrWhiteSpace(path)) return (new IndexedDirectory(null, "", ""), null);
        var qualified = paths.Parse(path);
        if (qualified is null)
            return (null, new Problem($"'{path}' names no directory: {await PathRuleAsync(cancellationToken)}"));

        var (repository, spelled, unknown) = await RespellAsync(qualified, cancellationToken);
        return repository is null
            ? (null, unknown)
            : (new IndexedDirectory(repository, qualified.PathInRepository, spelled), null);
    }

    /// <summary>
    ///     The half of locating a file and a directory that is one rule: the first segment must name a
    ///     repository of this index, and the path is then spelled the way the index holds that
    ///     repository's slug, whatever case the agent wrote it in. The parse before it is each caller's,
    ///     because what a malformed path is told differs between a file and a directory.
    /// </summary>
    private async Task<(IndexedRepository? Repository, string Spelled, Problem? Unknown)> RespellAsync(
        QualifiedPath qualified, CancellationToken cancellationToken)
    {
        if (await FindRepositoryAsync(qualified.RepositorySlug, cancellationToken) is not { } repository)
            return (null, "", new Problem(
                $"{await UnknownRepositoryAsync(qualified.RepositorySlug, cancellationToken)} The first path segment must be one of these."));

        var paths = await PathsAsync(cancellationToken);
        return (repository, paths.Format(qualified with { RepositorySlug = repository.Slug }), null);
    }

    /// <summary>
    ///     Whether the index holds anything at this repository-relative path: a file of that name, or
    ///     any file beneath it. It is what tells "this scope has no recorded commit" from "this scope
    ///     names nothing here" — opposite facts that an empty history answer would otherwise share a
    ///     sentence for (CODING_STANDARDS, Errors). An empty path is the repository's own root, which
    ///     is there as long as the repository the caller already resolved is.
    ///     <c>starts_with</c> and not GLOB or LIKE: the path is a name, and `[slug]` or `my_module`
    ///     read as a pattern would answer about a sibling (#122).
    /// </summary>
    public async Task<bool> HoldsPathAsync(string repositorySlug, string pathInRepository,
        CancellationToken cancellationToken)
    {
        if (pathInRepository.Length == 0) return true;
        // EXISTS and not count(*) > 0, for the reason RecordsPathAsync gives: a count cannot stop early.
        using var command = Connection.Query("""
                                             SELECT EXISTS (
                                                 SELECT 1 FROM files f JOIN repositories r USING (repo_id)
                                                 WHERE r.slug = $r AND (f.path = $p OR starts_with(f.path, $p || '/'))
                                             )
                                             """,
            [new DuckDBParameter("r", repositorySlug), new DuckDBParameter("p", pathInRepository)]);
        return await command.ScalarAsync(cancellationToken) is true;
    }

    /// <summary>
    ///     Whether any commit recorded a path at or beneath this repository-relative path — the
    ///     history-aware sibling of <see cref="HoldsPathAsync" />, which asks the current file tree.
    ///     The two are separate on purpose. A path a later commit deleted or renamed away has commits
    ///     to read and nothing to open, so the history tools scope to it and the read side
    ///     (<c>read_file</c>, <c>list_tree</c>, <c>glob</c>) keeps refusing it: widening the one check
    ///     both use would have <c>read_file</c> offer a file that is not there (#132).
    ///     Matched on the path a commit recorded, the way every history scope is, and with
    ///     <c>starts_with</c> rather than GLOB or LIKE for the reason <see cref="HoldsPathAsync" />
    ///     gives: the path is a name, not a pattern (#122).
    /// </summary>
    public async Task<bool> RecordsPathAsync(string repositorySlug, string pathInRepository,
        CancellationToken cancellationToken)
    {
        if (pathInRepository.Length == 0) return true;
        // EXISTS and not count(*) > 0: a count cannot stop early, and commit_files is the largest
        // table here with no index on path. The scan this saves is the one a mistyped scope pays —
        // the common case, and the one that used to be refused after reading files alone.
        using var command = Connection.Query("""
                                             SELECT EXISTS (
                                                 SELECT 1
                                                 FROM commit_files cf JOIN commits c USING (commit_id)
                                                 WHERE c.repo_slug = $r
                                                   AND (cf.path = $p OR starts_with(cf.path, $p || '/'))
                                             )
                                             """,
            [new DuckDBParameter("r", repositorySlug), new DuckDBParameter("p", pathInRepository)]);
        return await command.ScalarAsync(cancellationToken) is true;
    }

    /// <summary>
    ///     The path a commit recorded a file at, as git spells it, for a caller that wrote this
    ///     repository-relative path — or null where no commit recorded one. The single-file flavour of
    ///     <see cref="RecordsPathAsync" />, which answers about a scope and so matches everything
    ///     beneath a directory too.
    ///     The tools that take one file need the narrower question (#136). `one/legacy` is a scope
    ///     git_log answers for and not a file file_history can: under the scope-shaped check it would
    ///     read as a historical file and then list none of the commits beneath it, which is a quiet
    ///     wrong answer where a refusal is the right one.
    ///     Matched lower-cased on both sides, for the reason <see cref="FindFileAsync" /> gives and more
    ///     so: the caller is asking about a file that is not there to be listed, so they are quoting a
    ///     path from memory or from an older reply almost by definition. It returns the recorded
    ///     spelling rather than a bool so the answer names the path git has, not the one that was typed.
    /// </summary>
    public async Task<string?> RecordedFilePathAsync(string repositorySlug, string pathInRepository,
        CancellationToken cancellationToken)
    {
        if (pathInRepository.Length == 0) return null;
        using var command = Connection.Query("""
                                             SELECT cf.path
                                             FROM commit_files cf JOIN commits c USING (commit_id)
                                             WHERE c.repo_slug = $r AND lower(cf.path) = lower($p)
                                             ORDER BY cf.path = $p DESC
                                             LIMIT 1
                                             """,
            [new DuckDBParameter("r", repositorySlug), new DuckDBParameter("p", pathInRepository)]);
        return await command.ScalarAsync(cancellationToken) as string;
    }

    /// <summary>A "did you mean" longer than this is a glob result, and glob is the better tool for it.</summary>
    private const int MaxSuggestions = 5;

    /// <summary>
    ///     The commits a file was first and last changed by, both null where no history was imported for
    ///     it. One query for both, because they are two columns of the same row.
    /// </summary>
    public async Task<FileCommits> FileCommitsAsync(long fileId, CancellationToken cancellationToken)
    {
        // Both attributions are aliased to the names ReaderColumns.Attribution reads, prefixed, so that
        // the one helper reads them and a rename in this SELECT is a rename it follows.
        using var command = Connection.Query("""
                                             SELECT first.sha AS first_sha, first.author_name AS first_author_name,
                                                    first.authored_at AS first_authored_at,
                                                    first.subject AS first_subject,
                                                    last.sha AS last_sha, last.author_name AS last_author_name,
                                                    last.authored_at AS last_authored_at,
                                                    last.subject AS last_subject
                                             FROM files f
                                             LEFT JOIN commits first ON first.commit_id = f.first_commit
                                             LEFT JOIN commits last ON last.commit_id = f.last_commit
                                             WHERE f.file_id = $f
                                             """, [new DuckDBParameter("f", fileId)]);
        using var reader = await command.ReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new FileCommits(null, null);
        return new FileCommits(reader.Attribution("first_"), reader.Attribution("last_"));
    }

    /// <summary>
    ///     The repositories of this project that have no imported history, in build order — the one
    ///     place the rule lives, because an answer covering half a project reads as covering all of it
    ///     and every surface that ranks over history has to say so in its own words (CONTEXT.md,
    ///     History).
    ///     Empty where the question does not arise, which is the caller's answer and not its judgement:
    ///     a call already scoped to one repository is not speaking for the others, and a project of one
    ///     repository has nothing to contrast with.
    ///     Not <see cref="RepositoriesAsync" />'s rows, whose <c>Commits</c> is filled only on the
    ///     <see cref="IndexReaders.StatusAsync" /> path and is silently zero on this one — reading it here would
    ///     report every repository as historyless.
    /// </summary>
    /// <param name="scopedTo">The repository the caller narrowed to, or null for the whole project.</param>
    /// <param name="cancellationToken">Threaded through to the command.</param>
    public async Task<HistoryCoverage> HistoryCoverageAsync(string? scopedTo,
        CancellationToken cancellationToken)
    {
        if (scopedTo is not null) return HistoryCoverage.NothingToSay;

        // EXISTS rather than a count: the question is whether a repository was walked at all, and a
        // semi-join stops at the first commit where count(*) reads every one of them. Matched on the
        // slug, not the id, because that is what commits records (ADR-0007).
        using var command = Connection.Query("""
                                             SELECT r.slug,
                                                    EXISTS (SELECT 1 FROM commits c WHERE c.repo_slug = r.slug)
                                                        AS walked
                                             FROM repositories r
                                             ORDER BY r.repo_id
                                             """, []);
        using var reader = await command.ReaderAsync(cancellationToken);
        var all = new List<(string Slug, bool Walked)>();
        while (await reader.ReadAsync(cancellationToken)) all.Add((reader.Text("slug"), reader.Flag("walked")));
        // One repository cannot be contrasted with another, so there is nothing to say about it here;
        // that a project has no history at all is a different sentence, said before this is reached.
        if (all.Count < 2) return HistoryCoverage.NothingToSay;

        var without = all.Where(r => !r.Walked).Select(r => r.Slug).ToList();
        return without.Count == 0
            ? HistoryCoverage.NothingToSay
            : new HistoryCoverage(all.Where(r => r.Walked).Select(r => r.Slug).ToList(), without);
    }

    /// <summary>Files anywhere in the project with this leaf name, for a "did you mean" after a miss.</summary>
    public async Task<IReadOnlyList<string>> FilesNamedAsync(string name, int limit,
        CancellationToken cancellationToken)
    {
        using var command = Connection.Query(
            $"SELECT qualified_path FROM files WHERE lower(name) = lower($n) ORDER BY qualified_path LIMIT {limit}",
            [new DuckDBParameter("n", name)]);
        using var reader = await command.ReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.Text("qualified_path"));
        return result;
    }

    private async Task<string> SlugsAsync(CancellationToken cancellationToken) =>
        string.Join(", ", (await RepositoriesAsync(cancellationToken)).Select(r => r.Slug));

    /// <summary>
    ///     One statement for the repositories and the naming rule. Both halves answer empty on an index
    ///     still being filled — no <c>index_info</c> row until the build completes, no repositories
    ///     until the first one is ingested — and then the shape falls back to a multi-repository
    ///     project anchored on the project slug, which is the only name there is to anchor on.
    /// </summary>
    private async Task LoadShapeAsync(CancellationToken cancellationToken)
    {
        if (_repositories is not null) return;

        using var command = Connection.Query("""
                                             SELECT r.slug, r.url, r.head_commit, r.file_count, r.line_count,
                                                    (SELECT single_repository FROM index_info) AS single_repository
                                             FROM repositories r
                                             ORDER BY r.repo_id
                                             """, []);
        using var reader = await command.ReaderAsync(cancellationToken);
        var repositories = new List<IndexedRepository>();
        bool singleRepository = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            repositories.Add(ReadRepository(reader));
            singleRepository = reader.FlagOrFalse("single_repository");
        }

        _paths = new ProjectPaths(singleRepository, repositories.Count > 0 ? repositories[0].Slug : ProjectSlug);
        _repositories = repositories;
    }

    internal static async Task<IReadOnlyList<IndexedRepository>> ReadRepositoriesAsync(DuckDBConnection connection,
        CancellationToken cancellationToken)
    {
        // The history each repository has, joined on the slug rather than the id, because that is what
        // commits records (ADR-0007). A repository with no commits keeps a null newest commit and a zero
        // count, which the page draws as "no history" rather than as a repository that never changed.
        using var command = connection.Query(
            """
            SELECT r.slug, r.url, r.head_commit, r.file_count, r.line_count,
                   coalesce(h.commits, 0) AS commits, h.sha, h.author_name, h.authored_at, h.subject,
                   h.first_at, h.last_at
            FROM repositories r
            LEFT JOIN (SELECT repo_slug, count(*) AS commits,
                              argMax(sha, commit_id) AS sha, argMax(author_name, commit_id) AS author_name,
                              argMax(authored_at, commit_id) AS authored_at, argMax(subject, commit_id) AS subject,
                              -- The span, by date rather than by walk order: min and max, not the first
                              -- and last commit_id, because an author date does not ascend with history
                              -- (ADR-0007) and "how far back does this go" is asked about dates.
                              min(authored_at) AS first_at, max(authored_at) AS last_at
                       FROM commits GROUP BY repo_slug) h ON h.repo_slug = r.slug
            ORDER BY r.repo_id
            """, []);
        using var reader = await command.ReaderAsync(cancellationToken);
        var result = new List<IndexedRepository>();
        // The history columns are read only here. The other caller, LoadShapeAsync, is the path every
        // tool takes to learn the repository names, and it has no use for a join onto commits.
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadRepository(reader) with
            {
                Commits = reader.Int64("commits"),
                NewestCommit = reader.Attribution(),
                FirstCommitAt = reader.TimestampOrNull("first_at"),
                LastCommitAt = reader.TimestampOrNull("last_at")
            });
        return result;
    }

    private static IndexedRepository ReadRepository(DbDataReader reader) => new(
        reader.Text("slug"), reader.Text("url"), reader.Text("head_commit"),
        reader.Int32("file_count"), reader.Int64("line_count"));

    /// <summary>
    ///     Reads the columns <see cref="FileColumns" /> selects, by name: the glob query appends a
    ///     window count to that list, so a positional read here would break the moment another caller
    ///     prepends anything to its own projection.
    /// </summary>
    private static IndexedFile ReadFile(DbDataReader reader) => new(
        reader.Int64("file_id"), reader.Text("qualified_path"), reader.Text("slug"), reader.Text("path"),
        reader.Int32("line_count"), reader.Int64("size_bytes"), reader.TextOrNull("skip_reason"),
        reader.TextOrNull("module"));
}
