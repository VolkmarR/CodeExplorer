import type { OverviewChurn } from '@/features/projects/api'

/**
 * What every history-derived section says when there was none to derive from. The one thing history
 * must never say by accident is "nothing changed" (CONTEXT.md, History), and a section that simply
 * disappears says exactly that — so the absence is spelled out where the answer would have been, in
 * each section, from one sentence rather than two that could come to disagree.
 */
export const NO_HISTORY =
  'This project’s index holds no history, so no file can be ranked by how much it changed and no ' +
  'author can be named. Refresh the project to import it.'

/**
 * The window the churn section covers, or null where the index holds no history: the one check every
 * history card makes before it says {@link NO_HISTORY}. Both ends or neither, which is what the pair
 * means. The cards used to decide it three ways — one end, the other, or an empty list — and an empty
 * list is also what filters that excluded everything leave, so a filtered card said "no history".
 */
export function historyWindow(churn: OverviewChurn): { since: string; until: string } | null {
  return churn.since && churn.until ? { since: churn.since, until: churn.until } : null
}
