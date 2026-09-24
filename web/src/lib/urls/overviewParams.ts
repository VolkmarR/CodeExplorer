import { DEFAULT_CHURN_DAYS, text } from '@/lib/urls/churnParams'

/**
 * What the overview page needs from the URL: the window its most-changed ranking is counted over,
 * which repository if not all of them, and whether the project's excluded paths are shown anyway.
 * In the URL for the reason the churn page's filters are (ADR-0004): a view of a project worth
 * sending someone opens as the same view.
 *
 * Every field is optional, `days` included, because this is the project's own page: the sidebar and
 * the project list link to it bare, and absent is the default view rather than a link to repair.
 */
export interface OverviewParameters {
  /** Absent is the server's default window, `DEFAULT_OVERVIEW_DAYS`. */
  days?: number
  repository?: string
  /** Lifts the project's excluded paths for this view; absent is the setting applied. */
  showExcluded?: boolean
}

/**
 * The windows offered, in days: a month for what is being worked on now, the server's quarter, and
 * a year for a project's shape. The page is an overview, so the churn page's longer and shorter
 * windows are left to the churn page, which the ranking links to.
 */
export const OVERVIEW_WINDOWS = [30, 90, 365] as const

/** The server's own default, the one the stored overview is taken over: the churn page's too. */
export const DEFAULT_OVERVIEW_DAYS = DEFAULT_CHURN_DAYS

/**
 * A hand-edited or truncated URL still opens the default view rather than throwing. Parsed here and
 * put in its canonical form by `overviewSearch`, so `?days=90` and the bare URL are one view and one
 * cache entry.
 */
export function validateOverviewSearch(search: Record<string, unknown>): OverviewParameters {
  const days = Number(search.days)
  return overviewSearch({
    // Any positive number, as the churn page takes: the server clamps it, and 45 days is a question.
    days: Number.isInteger(days) && days > 0 ? days : undefined,
    repository: text(search.repository),
    // True or absent, never false: a link that says `showExcluded=false` asks for the default.
    showExcluded: search.showExcluded === true || search.showExcluded === 'true',
  })
}

/**
 * The URL of an overview, as a change to the one being read, so that picking a window keeps the
 * repository a reader chose. The default window, the empty string and `false` clear their fields,
 * so a view back at its defaults is the bare URL the sidebar links to — and one cache entry with it.
 */
export function overviewSearch(
  current: OverviewParameters,
  change: Partial<OverviewParameters> = {},
): OverviewParameters {
  const next = { ...current, ...change }
  return {
    days: next.days === DEFAULT_OVERVIEW_DAYS ? undefined : next.days,
    repository: text(next.repository),
    showExcluded: next.showExcluded ? true : undefined,
  }
}
