using System.Diagnostics;
using CodeExplorer.Index;

namespace CodeExplorer.Refresh;

/// <summary>
///     What one phase of a refresh cost, as the status reports it once that phase has ended.
///     <see cref="Seconds" /> rather than a <see cref="TimeSpan" /> because the readers are the web UI
///     and whatever drives the cron: a serialised <see cref="TimeSpan" /> is a string, and neither of
///     them can compare two of those or add them up.
///     <see cref="Step" /> travels with the phase so a reader can group the timeline the way the
///     progress reports it — a step reports several of these where it does several separable things,
///     as the history step does and as fetching does once a project has more than one repository (#91).
/// </summary>
public sealed record PhaseCost(int Step, string Phase, double Seconds);

/// <summary>
///     Collects what a refresh spent, phase by phase, out of the reports it already makes.
///     #91 gave every piece of work inside the history step its own phase, which lets an operator watching a
///     running refresh see what is running. It did not let anyone ask afterwards what a refresh cost:
///     the status holds one phase at a time, and a refresh that has finished has moved past all of
///     them, so the whole timeline collapses to "Done" the moment the swap returns. Attributing one
///     real refresh therefore still needed a poller fast enough to catch a phase lasting a few
///     milliseconds — measured here at a 25 ms poll against a live server, which is exactly the
///     bespoke harness #91's last item said should not be necessary.
///     This is the half of #92 that was chosen: the full-text rebuild — 27.9 s of a Radix refresh,
///     the largest single item in the window — is not made faster, it is made legible. An operator
///     reads its share off a finished refresh instead of re-deriving it by measurement, which is what
///     #80 got wrong and what a day of triage went into correcting.
///     Not thread-safe, and does not need to be: one of these belongs to one refresh, and a refresh
///     reports from the single task that runs it.
/// </summary>
internal sealed class PhaseTimeline
{
    /// <summary>
    ///     Decimal places on a phase's seconds: milliseconds, which is the grain the phases are
    ///     actually separated at — a swap and an overview build are single-digit milliseconds apart on
    ///     a small project, and rounding to hundredths would report the two as the same number. How
    ///     few of them a reader is shown is the UI's judgement and not this one; what is stored is the
    ///     measurement.
    /// </summary>
    private const int _secondsPrecision = 3;

    private readonly List<PhaseCost> _ended = [];

    private RefreshProgress? _running;

    private long _startedAt;

    /// <summary>
    ///     Takes a progress report and answers the phases that have ended, oldest first — or null
    ///     where the report did not turn the page, which is most of them: a counting step reports
    ///     every <see cref="RefreshProgress.ReportEvery" /> items under a phase that has not changed.
    ///     Null rather than the unchanged list so that the one call both records and says whether
    ///     there is anything new to publish; a caller that had to ask those separately would be
    ///     keeping half of this type's bookkeeping.
    ///     The phase still running is never among them: what it has cost so far is not what it cost,
    ///     and a figure that keeps growing while it is read is one people learn to distrust.
    /// </summary>
    public IReadOnlyList<PhaseCost>? Record(RefreshProgress progress)
    {
        if (_running is not null && _running.Phase == progress.Phase) return null;

        End();
        _running = progress;
        _startedAt = Stopwatch.GetTimestamp();
        return _ended.ToArray();
    }

    /// <summary>
    ///     Ends the phase still running and answers the whole timeline, for a refresh that has stopped.
    ///     Called for a failure as well as a success: a refresh that died says how far it got and what
    ///     the phases before the failure cost, which is most of what an operator wants from it.
    /// </summary>
    public IReadOnlyList<PhaseCost> Close()
    {
        End();
        return _ended.ToArray();
    }

    private void End()
    {
        if (_running is null) return;

        _ended.Add(new PhaseCost(_running.Step, _running.Phase,
            Math.Round(Stopwatch.GetElapsedTime(_startedAt).TotalSeconds, _secondsPrecision)));
        _running = null;
    }
}
