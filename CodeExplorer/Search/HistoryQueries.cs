namespace CodeExplorer;

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
public sealed partial class HistoryQueries(IndexReaders readers, IConfiguration configuration)

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
    ///     How many extensions a churn answer offers as a filter. A window holds far fewer distinct
    ///     extensions than files, and past a couple of dozen the list has stopped being a menu and
    ///     become a second thing to search — the tail of it is one-off fixtures, not what the project
    ///     is written in.
    /// </summary>
    private const int MaxRankedExtensions = 24;

    /// <summary>
    ///     Previous paths a reply names before it starts counting the rest. Three is a rename, a
    ///     reorganisation and the thing before that, which is more history than any reply is read for;
    ///     past it the note would be a listing and the combined total — the number the agent actually
    ///     wanted — would be below it (#131).
    /// </summary>
    private const int MaxPreviousPaths = 3;

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

}
