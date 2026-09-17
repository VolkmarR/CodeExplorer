using System.Text.Json;
using ModelContextProtocol;

namespace CodeExplorer;

/// <summary>
///     How much of a project one language accounts for. <see cref="Mapped" /> is false when no language
///     profile covers the extension, in which case <see cref="Name" /> is the extension itself — a
///     weaker claim than a language name, and said as one rather than guessed at.
/// </summary>
public sealed record LanguageShare(string Name, bool Mapped, int Files, long Lines, int Skipped);

/// <summary>
///     One entry at the top level of a repository. <see cref="Files" /> counts everything beneath a
///     directory rather than its immediate children, the way a tree listing does, so the number is what
///     the area is worth reading and not how it happens to be nested.
/// </summary>
public sealed record OverviewEntry(
    string QualifiedPath,
    bool IsDirectory,
    int Files,
    long Lines,
    long SizeBytes);

/// <summary>
///     One of the largest files in the project, by bytes rather than by lines: the point is what a
///     read costs, and a file the build skipped for its size has no lines at all — which makes it
///     exactly the file a caller most needs warning about.
/// </summary>
public sealed record OverviewFile(string QualifiedPath, int LineCount, long SizeBytes);

/// <summary>
///     The churn section of an overview: the ranking and the window it was taken over.
///     <see cref="Since" /> and <see cref="Until" /> are null together when the project had no imported
///     history at build time, which is a different answer from a project nobody changed and is reported
///     as one (CONTEXT.md, History).
/// </summary>
public sealed record OverviewChurn(
    int Days,
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    IReadOnlyList<ChurnedFile> Files)
{
    /// <summary>What a project with no imported history has instead of a ranking.</summary>
    public static OverviewChurn None(int days) => new(days, null, null, []);

    /// <summary>
    ///     The window the ranking was taken over, or null where no history was imported to rank. The
    ///     two dates are stored flat because that is the shape the browser reads them in, and put back
    ///     together here so that a reply says what it covered in <see cref="HistoryWindow.Describe" />'s
    ///     words rather than a second phrasing of the same three fields.
    /// </summary>
    public HistoryWindow? Window() =>
        Since is { } since && Until is { } until ? new HistoryWindow(since, until, Days) : null;
}

/// <summary>
///     One of the authors who has touched the project most, over the whole imported history rather than
///     the churn window: "who knows this code" is a longer question than "what is moving now".
///     It is who touched it, not who wrote it — a reformat counts, the same way it counts as
///     attribution (CONTEXT.md, Attribution) — so an overview says so rather than letting a name read
///     as authorship.
/// </summary>
public sealed record OverviewAuthor(string Name, string Email, int Commits, DateTimeOffset LastCommit);

/// <summary>
///     What a caller connecting to a project for the first time is told, computed once by the build
///     that produced the index and stored with it. Everything here is a fact about the index as it was
///     built, so none of it can disagree with what a search then answers.
///     It carries no repository list: the repositories and the commit each was indexed at are already
///     one join-free table, and a second copy of them here would be a second definition of "what this
///     project holds" — the same reason the tree listing reads its counts off <c>repositories</c>
///     rather than recounting <c>files</c>.
/// </summary>
/// <param name="Languages">Counts by language, largest first, with unmapped extensions standing for themselves.</param>
/// <param name="OtherLanguages">How many further languages or extensions the list above leaves out.</param>
/// <param name="Tree">The top level of every repository, with what lies beneath each entry.</param>
/// <param name="OtherEntries">How many further top-level entries the tree above leaves out.</param>
/// <param name="LargestFiles">The files most likely to swamp a read, so a caller knows before opening one.</param>
/// <param name="Churn">Where work has been happening, over the window the build ranked.</param>
/// <param name="Authors">Who has touched the project most, over the whole imported history.</param>
public sealed record IndexOverview(
    IReadOnlyList<LanguageShare> Languages,
    int OtherLanguages,
    IReadOnlyList<OverviewEntry> Tree,
    int OtherEntries,
    IReadOnlyList<OverviewFile> LargestFiles,
    OverviewChurn Churn,
    IReadOnlyList<OverviewAuthor> Authors)
{
    /// <summary>
    ///     How the row is written and read. The overview is one document read whole and never queried
    ///     into — no tool asks "which projects are mostly X#" — so it is stored as JSON in one column
    ///     rather than as a set of DuckDB <c>LIST</c>s of <c>STRUCT</c>s. Nested columns would buy a
    ///     queryability nothing uses and cost every read a nested-value reader, and the shape is pinned
    ///     by the index schema version either way: a document an older build wrote never reaches this,
    ///     because its durable copy is rebuilt rather than restored.
    ///     Indented off, not for the bytes but because the column is read by machines only; the sections
    ///     are what a human reads, and they are rendered.
    /// </summary>
    private static readonly JsonSerializerOptions Format = new(JsonSerializerDefaults.Web);

    public string ToDocument() => JsonSerializer.Serialize(this, Format);

    /// <summary>
    ///     Back from the stored column. A document this build cannot read is a build mismatch the schema
    ///     version was supposed to catch — so it is an infrastructure failure and throws with the
    ///     remediation in the message (CODING_STANDARDS), rather than answering an empty overview. An
    ///     empty one would read as a project with nothing in it, which is the wrong thing to say twice
    ///     over: it is neither true nor actionable.
    /// </summary>
    public static IndexOverview FromDocument(string document)
    {
        try
        {
            return JsonSerializer.Deserialize<IndexOverview>(document, Format) ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw Unreadable("its stored overview is not a document this build understands", exception);
        }
    }

    /// <summary>
    ///     The one way an overview that should be there is reported missing. Both callers reach it for
    ///     the same underlying cause — an index written by a build this one is not — so both say so in
    ///     the same words and end with the same move.
    /// </summary>
    /// <param name="cause">What was found instead, in a clause that follows "because".</param>
    /// <param name="inner">The parse failure, where there was one.</param>
    public static McpException Unreadable(string cause, Exception? inner = null) =>
        new($"This project's overview cannot be read, because {cause}. Ask the operator to refresh the "
            + "project; every other tool still answers from the index meanwhile.", inner);
}
