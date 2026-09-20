/**
 * How old an index may be before it is called stale, in milliseconds. A day: these projects are
 * refreshed on a schedule of hours, so anything that has gone a whole day without one has had a run
 * fail or a schedule stop — which is exactly what an operator opening the app wants told.
 *
 * Read against the clock, which every *window* in this app deliberately is not (CONTEXT.md,
 * _Window_) — and the difference is the point. A window over commits must not use the clock,
 * because the newest commit is the end of what the index knows and today is not. When the index
 * was *built* is the other kind of fact: a wall-clock event, and how long ago it happened is a
 * wall-clock question. It is still not a claim that the index is wrong. The index cannot make that
 * claim, because everything it could compare itself against comes out of itself.
 */
const STALE_AFTER = 24 * 60 * 60 * 1000

/**
 * What an index can be, in one word. The whole vocabulary lives here, with the rule that decides
 * it, because two places say it — the sidebar on every page and the badge on the project page —
 * and a project the frame calls stale must not be a project the page calls built.
 *
 * `refreshing` is not a state of the index but of the job beside it; the mark that draws these takes
 * it in the same union because the two are shown in the same spot and only one can be true at a time.
 */
export type IndexState = 'fresh' | 'stale' | 'not built'

export function indexState(builtAt: string | null): IndexState {
  if (!builtAt) return 'not built'
  return Date.now() - new Date(builtAt).getTime() > STALE_AFTER ? 'stale' : 'fresh'
}
