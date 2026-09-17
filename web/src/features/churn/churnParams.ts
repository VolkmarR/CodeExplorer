/**
 * What the churn page needs from the URL: how far back to look, and which repository if not all of
 * them. In the URL and nowhere else, like search and the change log, so a ranking can be pasted to a
 * colleague and opens as the same ranking (ADR-0004).
 */
export interface ChurnParameters {
  days: number
  repository?: string
}

/**
 * The windows offered, in days. A fortnight is "what is being worked on now", a quarter is the
 * default and roughly a release, and the two longer ones are for reading a project's shape rather
 * than its week. Ten years stands for "all of it": it is longer than the history of nearly every
 * repository, and the server clamps there anyway.
 */
export const CHURN_WINDOWS = [14, 90, 365, 3650] as const

/** The server's own default, repeated here so the URL a link carries is explicit about it. */
export const DEFAULT_CHURN_DAYS = 90

/** A hand-edited or truncated URL still opens a sensible ranking rather than throwing. */
export function validateChurnSearch(search: Record<string, unknown>): ChurnParameters {
  const days = Number(search.days)
  return {
    // Any positive number is accepted rather than only the offered ones: the server clamps it, and a
    // link someone wrote by hand asking for 45 days is a reasonable question, not a broken URL.
    days: Number.isInteger(days) && days > 0 ? days : DEFAULT_CHURN_DAYS,
    repository:
      typeof search.repository === 'string' && search.repository !== ''
        ? search.repository
        : undefined,
  }
}

/** How a window is named in the UI. Spelled here so the select and the heading cannot disagree. */
export function describeWindow(days: number): string {
  if (days >= 3650) return 'All history'
  if (days === 365) return 'Last year'
  if (days % 30 === 0) return `Last ${days / 30} months`
  if (days % 7 === 0) return `Last ${days / 7} weeks`
  return `Last ${days} days`
}
