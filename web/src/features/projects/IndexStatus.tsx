import type { ProjectIndexStatus } from '@/lib/api'
import { formatCount, formatTime } from '@/lib/format'
import { Badge } from '@/components/ui/badge'

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
 * it, because two places say it — the sidebar on every page and this badge on the project page —
 * and a project the frame calls stale must not be a project the page calls built.
 *
 * `refreshing` is not a state of the index but of the job beside it; it is in the same union
 * because the two are shown in the same spot and only one of them can be true at a time.
 */
export type IndexState = 'fresh' | 'stale' | 'not built'

export function indexState(builtAt: string | null): IndexState {
  if (!builtAt) return 'not built'
  return Date.now() - new Date(builtAt).getTime() > STALE_AFTER ? 'stale' : 'fresh'
}

const DOT: Record<IndexState | 'refreshing', string> = {
  fresh: 'bg-success',
  refreshing: 'animate-pulse bg-primary',
  stale: 'bg-warning',
  'not built': 'bg-muted-foreground/50',
}

/**
 * The mark beside the word. Decorative on purpose: colour is never the only carrier of state here,
 * the word is always beside it, and where the word is hidden — the collapsed sidebar rail — the
 * `title` on its container is what a reader is left with.
 */
export function StateDot({ state }: { state: IndexState | 'refreshing' }) {
  return <span aria-hidden="true" className={`size-2 shrink-0 rounded-full ${DOT[state]}`} />
}

/**
 * A project's index in one line. A project that has never been built is the ordinary starting state,
 * not an error, so it reads as "not built" rather than as a warning.
 */
export function IndexStatus({ status }: { status: ProjectIndexStatus }) {
  if (!status.builtAt) {
    return (
      <Badge variant="outline" className="text-muted-foreground">
        not built
      </Badge>
    )
  }

  return (
    <span className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
      <Badge variant="secondary">{status.ftsIndexed ? 'full-text' : 'substring scan'}</Badge>
      <span>
        {formatCount(status.files)} files, {formatCount(status.lines)} lines
      </span>
      <span className="text-muted-foreground/70">built {formatTime(status.builtAt)}</span>
    </span>
  )
}
