/**
 * What the project page needs from the URL: which of its two halves is open.
 *
 * One route and two sidebar items, rather than two routes: what a project holds and how it is
 * configured are the same page in the server's eyes (`/api/projects/{project}` answers both) and
 * splitting them would change a path an operator may have saved. The tab is a search param for the
 * reason every other view's state is one — a link reproduces what the sender was looking at
 * (ADR-0004) — and it defaults to the overview, so a link written before this existed still opens.
 */
export type ProjectTab = 'overview' | 'settings'

export interface ProjectParameters {
  tab: ProjectTab
}

/** Anything unrecognised opens the overview, which is what a bare `/projects/{slug}` already did. */
export function validateProjectSearch(search: Record<string, unknown>): ProjectParameters {
  return { tab: search.tab === 'settings' ? 'settings' : 'overview' }
}

/** The URL of one half of the project page, so the tab is never spelled out at a link. */
export function projectSearch(tab: ProjectTab): ProjectParameters {
  return { tab }
}
