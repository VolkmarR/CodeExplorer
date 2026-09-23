namespace CodeExplorer.Index;

/// <summary>
///     How far a running refresh has got, as the status endpoint reports it and the project page draws
///     it. It lives in <c>Index/</c> and not in <c>Refresh/</c> because the steps that can count their
///     work are the build's: a refresh orchestrates a build, so the build cannot depend on it, and a
///     refresh already reaches <c>Index/</c> (ADR-0005). It was in <c>Infrastructure/</c> until that
///     folder was cut back to host plumbing, which a progress report of a build is not.
///     <see cref="Step" /> of <see cref="TotalSteps" /> is always honest — the steps are fixed and
///     known before anything starts.
///     <see cref="Done" /> and <see cref="Total" /> are filled only by the steps that can count what
///     they are doing: fetching knows how many repositories there are, reading knows how many files the
///     tree holds, and attributing knows how many commits it has to replay. Storing and swapping
///     cannot, and the commit walk knows only how far it has got — a walk's length is not known until
///     it ends — so those carry a null total rather than an estimated one, because a bar moving at a
///     rate nobody measured is worse than no bar.
///     There is deliberately no overall percentage. The steps are wildly unequal, a first history
///     import dwarfing everything else, so one figure weighted as though they were equal would race to
///     most of the way and then sit still — the progress bar people have learned not to believe.
/// </summary>
public sealed record RefreshProgress(int Step, int TotalSteps, string Phase, long? Done = null, long? Total = null)
{
    /// <summary>
    ///     How many steps a refresh has: fetching, reading, history, the overview, the full-text index,
    ///     storing, swapping. Fixed and known before it starts, which is what makes "step 3 of 7" a fact
    ///     rather than an estimate.
    ///     It was 5 until the overview and the full-text build were split out of the history step. #91
    ///     had already given them phases of their own and deliberately left them reporting as step 3, so
    ///     that the numbering stayed still while the labels got finer; the counter then sat on 3 for most
    ///     of a large refresh, because the two longest pieces of work on a real project are the two that
    ///     the third step did not name. A step is what the counter is read for, so they are steps now.
    ///     A server with no <c>fts</c> extension never reports <see cref="FullTextStep" />: there is no
    ///     BM25 index to build, and the counter goes from the overview to the store. The step space is
    ///     still the same seven — whether this process has the extension is known at startup, so the
    ///     step being empty is as fixed as the rest — and a phase that flashed past instantly would read
    ///     as a full-text index that was somehow free.
    /// </summary>
    public const int TotalStepCount = 7;

    public const int FetchStep = 1;

    public const int IngestStep = 2;

    public const int HistoryStep = 3;

    public const int OverviewStep = 4;

    public const int FullTextStep = 5;

    public const int StoreStep = 6;

    public const int SwapStep = 7;

    /// <summary>
    ///     The phases, as the status endpoint hands them to an operator: what is happening, in an
    ///     operator's words. Constants rather than literals at the call site, so a test waiting for a
    ///     phase is not waiting on a wording that a rewrite of the sentence quietly breaks. They live
    ///     here rather than in <c>Refresh/</c> for the reason this whole type does — half of them are
    ///     reported from inside the build, which cannot depend on the refresh that orchestrates it.
    ///     A step reports several of them where it does several separable things (#91): step 3 walks the
    ///     history, attributes the lines and writes that attribution onto them, and a reader who cannot
    ///     tell them apart cannot say what a refresh spent its time on. The overview and the full-text
    ///     index were the other two pieces of that step, and are steps of their own now.
    /// </summary>
    public const string IngestPhase = "Reading the repositories into the shadow index";

    /// <summary>
    ///     Turning the import names the walk recorded into the files they name, which runs once the
    ///     whole project is in the shadow and is therefore not part of any one repository's reading.
    ///     A phase of the reading step rather than a step of its own: it finishes the job the walk
    ///     started — the names were appended by it — and the seven steps are what the counter is read
    ///     for, which nothing here asks to change.
    ///     It had no phase at all until this, so its cost was billed to whichever repository the walk
    ///     happened to report last. That is the fault #91 fixed for the overview and the full-text
    ///     build, left in the one pass between them: on a project with paths to resolve it builds a
    ///     lowercased copy of every path in it, so a timeline that does not name it is a timeline
    ///     handing an operator one pass's cost under another pass's label (#92).
    /// </summary>
    public const string ResolveImportsPhase = "Resolving the imports to the files they name";

    /// <summary>
    ///     The moment the refresh has the rebuild slot and has not yet reached the first repository.
    ///     It lasts milliseconds and was a literal at its call site while the only way to see it was
    ///     to poll inside them; it is a constant now that the timeline keeps what each phase cost
    ///     (#92) and a reader finds it on every finished refresh.
    /// </summary>
    public const string StartPhase = "Starting";

    public const string AttributionPhase = "Writing attribution onto the lines";

    public const string OverviewPhase = "Building the project overview";

    /// <summary>
    ///     Reported only where this process has the <c>fts</c> extension, because only then is there a
    ///     BM25 index to build. A project searched by substring scan passes straight from the overview
    ///     to the store, and says so by never naming this phase — a phase that flashed past instantly
    ///     would read as a full-text index that was somehow free.
    /// </summary>
    public const string FullTextPhase = "Building the full-text index";

    public const string SwapPhase = "Swapping the new index in";

    /// <summary>
    ///     Writing the durable copy, which happens before the swap: a store that cannot be reached is a
    ///     build that failed, and an index nothing could make durable is not one to put in front of
    ///     agents on a server whose disk is wiped on every stop (#9).
    /// </summary>
    public const string StorePhase = "Storing the durable copy of the new index";

    /// <summary>
    ///     How often a counting step reports, in items. The status is polled rather than streamed, so a
    ///     finer grain would only cost dictionary writes nobody ever reads.
    /// </summary>
    public const int ReportEvery = 200;
}
