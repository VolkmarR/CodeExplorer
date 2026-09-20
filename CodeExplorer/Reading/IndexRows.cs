namespace CodeExplorer;

/// <summary>
///     A row of <c>files</c>. <see cref="SkipReason" /> is set when the file is committed but has no lines in the
///     index. <see cref="Module" /> is what the file declares itself to be — a C# namespace, a Delphi
///     unit — or null where it declares none (#55); it is on this row rather than fetched when wanted,
///     because every caller that wants it has already read this row to find the file at all.
/// </summary>
public sealed record IndexedFile(
    long FileId,
    string QualifiedPath,
    string RepositorySlug,
    string PathInRepository,
    int LineCount,
    long SizeBytes,
    string? SkipReason,
    string? Module);

/// <summary>
///     A row of <c>repositories</c>: one repository as the last build left it. <paramref name="Commits" /> is how much history was
///     imported for it and <paramref name="NewestCommit" /> the last one recorded; zero and null mean
///     none was, which is a different thing from a repository nobody has changed.
///     <paramref name="FirstCommitAt" /> and <paramref name="LastCommitAt" /> are the dates that
///     history spans — the earliest and latest authored date, not the first and last commit of the
///     walk. The two differ: <c>commit_id</c> ascends with history by construction and an author date
///     does not (ADR-0007), so the tip of the walk is not always the newest date. A span is a question
///     about dates, and the dates are what it answers with.
/// </summary>
public sealed record IndexedRepository(
    string Slug,
    string Url,
    string HeadCommit,
    int FileCount,
    long LineCount,
    long Commits = 0,
    AttributedBy? NewestCommit = null,
    DateTimeOffset? FirstCommitAt = null,
    DateTimeOffset? LastCommitAt = null);

/// <summary>
///     What an index holds, for the readers that describe a project rather than read from it:
///     <c>repo_info</c> and the operator's pages. <see cref="Repositories" /> is what the last build
///     read; <see cref="Files" /> and <see cref="Lines" /> are the sums the build recorded on them,
///     not a second count over <c>files</c>, so the two cannot disagree. <see cref="SingleRepository" />
///     is how the build named its files (ADR-0006), read from the index rather than the control
///     database because an index names things the way the build that wrote it was told to.
/// </summary>
public sealed record IndexStatus(
    DateTimeOffset BuiltAt,
    bool FtsIndexed,
    bool SingleRepository,
    IReadOnlyList<IndexedRepository> Repositories)
{
    public int Files => Repositories.Sum(r => r.FileCount);
    public long Lines => Repositories.Sum(r => r.LineCount);
}

/// <summary><see cref="Skipped" /> of the <see cref="Files" /> have no lines; <see cref="Lines" /> covers the rest.</summary>
public sealed record ExtensionCount(string Extension, int Files, long Lines, int Skipped);

/// <summary>The commit a run of lines is attributed to, or null for lines the build could not attribute.</summary>
public sealed record AttributedBy(string Sha, string AuthorName, DateTimeOffset AuthoredAt, string Subject);

/// <summary>
///     One author of a project's history, identified by the address git records rather than by the
///     display name: a person who respells their name is one author under their newest spelling, and
///     two people who share a first name are two. The name is the one on their most recent commit.
///     Here rather than beside the query that reads it, because three surfaces draw the same row
///     through <see cref="ToolReply.AuthorRow" /> and the renderer may not reach into Search
///     (ADR-0005).
/// </summary>
public sealed record RecordedAuthor(string Name, string Email, long Commits, DateTimeOffset LastCommit);

/// <summary>
///     One row of a churn ranking: how many commits of the window touched it and what they did to
///     it. The row is a file, or a directory where the ranking was rolled up to one — the same six
///     numbers and the same mark either way, which is what lets one surface draw both.
///     <see cref="QualifiedPath" /> is how the project names that path (ADR-0006) and is always
///     set, because a window ranks paths a later commit deleted or renamed away and those have to be
///     named too; <see cref="AtHead" /> is what says whether there is still something there to read.
/// </summary>
public sealed record ChurnedFile(
    string QualifiedPath,
    string RepositorySlug,
    bool AtHead,
    int Commits,
    long Added,
    long Deleted);

/// <summary>
///     One extension a churn window's scope holds, and how much of the window it is (#161).
///     <see cref="Commits" /> is the commits that touched a path spelled this way and is what the list
///     is ranked by; <see cref="Files" /> is how many distinct paths those were, which is the number
///     that says a handful of generated files can account for a great many commits.
///     Not <see cref="ExtensionCount" />, which counts the whole index at HEAD: this one is about a
///     window and counts paths a later commit deleted, because those are what a churn ranking ranks.
///     <see cref="Extension" /> is empty for a path whose last segment has no dot, which is a file
///     with no extension and not a missing answer.
/// </summary>
public sealed record ChurnedExtension(string Extension, int Commits, int Files);

/// <summary>
///     Which repositories of a project an answer drawn from history can speak for, and which it
///     cannot. Both sides, because "says which is which" is the point: naming only the repositories
///     that were not walked leaves a reader to infer the rest from a ranking, which is the inference
///     the caveat exists to prevent (CONTEXT.md, History).
///     <see cref="NothingToSay" /> where the question does not arise — a project of one repository, an
///     answer already scoped to one, or every repository walked — so a caller tests one thing.
/// </summary>
public sealed record HistoryCoverage(IReadOnlyList<string> With, IReadOnlyList<string> Without)
{
    public static readonly HistoryCoverage NothingToSay = new([], []);
}

/// <summary>A run of consecutive lines sharing one attribution (CONTEXT.md, Attribution).</summary>
public sealed record AttributedLines(int StartLine, int EndLine, AttributedBy? By);

/// <summary>
///     What a file's own history amounts to: the commit it was first changed by within the imported
///     history, and the one it was last changed by. Both null where none was imported.
/// </summary>
public sealed record FileCommits(AttributedBy? First, AttributedBy? Last);

/// <summary>
///     One row of a directory listing. A directory carries what lies beneath it — <see cref="Files" />
///     counts every file at any depth, not just its immediate children — and a file carries its own
///     size, with <see cref="Files" /> null to tell the two apart. <see cref="QualifiedPath" /> is what
///     the next listing is asked for, or what the file view opens.
/// </summary>
public sealed record TreeItem(
    string Name,
    string QualifiedPath,
    int? Files,
    long Lines,
    long SizeBytes,
    string? SkipReason);

/// <summary>
///     <see cref="Total" /> counts every match, <see cref="Files" /> the first <c>limit</c> of them.
///     <see cref="MatchesInOtherRepositories" /> is filled only when a repository-scoped glob matched
///     nothing, so a scoped miss is told apart from a pattern that matches nowhere.
/// </summary>
public sealed record GlobResult(int Total, IReadOnlyList<IndexedFile> Files, int? MatchesInOtherRepositories);

/// <summary>
///     A directory an agent named, resolved to what the index holds. <see cref="Repository" /> is null
///     at the project level, which only a multi-repository project has; <see cref="PathInRepository" />
///     is empty at a repository's own root. <see cref="QualifiedPath" /> is how this project spells it
///     (ADR-0006), which is what a reply names it by.
/// </summary>
public sealed record IndexedDirectory(IndexedRepository? Repository, string PathInRepository, string QualifiedPath);

