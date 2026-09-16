namespace CodeExplorer;

/// <summary>
///     How far a running refresh has got, as the status endpoint reports it and the project page draws
///     it. It lives in <c>Infrastructure/</c> and not in <c>Refresh/</c> because the steps that can
///     count their work are in <c>Index/</c>: a refresh orchestrates a build, so the build cannot
///     depend on it, and the vocabulary they share belongs to neither (ADR-0005).
///     <see cref="Step" /> of <see cref="TotalSteps" /> is always honest — the steps are fixed and
///     known before anything starts.
///     <see cref="Done" /> and <see cref="Total" /> are filled only by the steps that can count what
///     they are doing: fetching knows how many repositories there are, reading knows how many files the
///     tree holds, and attributing knows how many blobs it has to blame. Storing, swapping and the
///     commit walk cannot — a walk's length is not known until it ends — and they are null there rather
///     than estimated, because a bar moving at a rate nobody measured is worse than no bar.
///     There is deliberately no overall percentage. The steps are wildly unequal, a first history
///     import dwarfing everything else, so one figure weighted as though they were equal would race to
///     most of the way and then sit still — the progress bar people have learned not to believe.
/// </summary>
public sealed record RefreshProgress(int Step, int TotalSteps, string Phase, long? Done = null, long? Total = null)
{
    /// <summary>
    ///     How many steps a refresh has: fetching, reading, history, storing, swapping. Fixed and known
    ///     before it starts, which is what makes "step 3 of 5" a fact rather than an estimate.
    /// </summary>
    public const int TotalStepCount = 5;

    public const int FetchStep = 1;

    public const int IngestStep = 2;

    public const int HistoryStep = 3;

    public const int StoreStep = 4;

    public const int SwapStep = 5;

    /// <summary>
    ///     How often a counting step reports, in items. The status is polled rather than streamed, so a
    ///     finer grain would only cost dictionary writes nobody ever reads.
    /// </summary>
    public const int ReportEvery = 200;
}
