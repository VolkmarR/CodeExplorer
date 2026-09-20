import type { IndexState } from '@/features/projects/indexState'

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
