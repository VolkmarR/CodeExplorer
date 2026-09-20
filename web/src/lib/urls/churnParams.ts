/**
 * What the churn page needs from the URL: how far back to look, which repository or directory if not
 * all of them, whether the rows are files or directories, and which extensions are worth ranking. In
 * the URL and nowhere else, like search and the change log, so a ranking can be pasted to a colleague
 * and opens as the same ranking (ADR-0004) — a filtered ranking most of all, since the filter is the
 * part a colleague would otherwise have to be told in prose.
 */
export interface ChurnParameters {
  days: number
  repository?: string
  /** A qualified path to rank within. It carries its own repository, so it wins over `repository`. */
  directory?: string
  /** Segments to roll rows up by, or undefined to rank files. */
  depth?: number
  /** The only extensions to rank, comma-separated, or undefined for all of them. */
  extensions?: string
}

/**
 * How far a rollup groups by default when a reader switches to directories. One segment beneath the
 * scope, which is the question they asked — "which area is moving" — and the level they can then
 * click into for the next one. Deeper by default would answer a question nobody has got to yet.
 */
export const DEFAULT_CHURN_DEPTH = 1

/**
 * Where a rollup stops. The server clamps at ten; past three or four the rows have stopped being
 * areas and become directories, which is what ranking files already answers better.
 */
export const MAX_CHURN_DEPTH = 6

/**
 * The windows offered, in days. A fortnight is "what is being worked on now", a quarter is the
 * default and roughly a release, and the two longer ones are for reading a project's shape rather
 * than its week. Ten years stands for "all of it": it is longer than the history of nearly every
 * repository, and the server clamps there anyway.
 */
export const CHURN_WINDOWS = [14, 90, 365, 3650] as const

/** The server's own default, repeated here so the URL a link carries is explicit about it. */
export const DEFAULT_CHURN_DAYS = 90

/**
 * The ranking a link from somewhere else opens: the default window, nothing scoped and nothing
 * filtered. A named starting point rather than an empty object at each call site, because
 * `churnSearch` takes the ranking being changed and a link that starts one has none to change.
 */
export const CHURN_DEFAULTS: ChurnParameters = { days: DEFAULT_CHURN_DAYS }

/** A hand-edited or truncated URL still opens a sensible ranking rather than throwing. */
export function validateChurnSearch(search: Record<string, unknown>): ChurnParameters {
  const days = Number(search.days)
  const depth = Number(search.depth)
  return {
    // Any positive number is accepted rather than only the offered ones: the server clamps it, and a
    // link someone wrote by hand asking for 45 days is a reasonable question, not a broken URL.
    days: Number.isInteger(days) && days > 0 ? days : DEFAULT_CHURN_DAYS,
    // Absent and not zero for "rank files": the server reads null as files, and a 0 in the URL would
    // be a depth it clamps up to 1, which is a different ranking than the one the link asked for.
    depth: Number.isInteger(depth) && depth > 0 ? Math.min(depth, MAX_CHURN_DEPTH) : undefined,
    directory: text(search.directory),
    extensions: text(search.extensions),
    repository: text(search.repository),
  }
}

/** A search param that is a string or is not there. The empty string is not there, like everywhere. */
function text(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined
}

/** How a window is named in the UI. Spelled here so the select and the heading cannot disagree. */
export function describeWindow(days: number): string {
  if (days >= 3650) return 'All history'
  if (days === 365) return 'Last year'
  if (days % 30 === 0) return `Last ${days / 30} months`
  if (days % 7 === 0) return `Last ${days / 7} weeks`
  return `Last ${days} days`
}

/**
 * The URL of a ranking, as a change to the one being read. Every control on the page is one field of
 * this and the rest have to survive it: a reader who picks a window after filtering to `.cs` asked
 * for that window of that filter, and a `churnSearch(days)` that dropped the rest is how a page
 * quietly undoes what someone set. The empty string clears a field, for the reason `globSearch`
 * gives at length.
 */
export function churnSearch(
  current: ChurnParameters,
  change: Partial<ChurnParameters> = {},
): ChurnParameters {
  const next = { ...current, ...change }
  return {
    days: next.days,
    depth: next.depth,
    directory: blank(next.directory),
    extensions: blank(next.extensions),
    // A directory carries its own repository (ADR-0006), so holding both would let them disagree —
    // and it is the directory a reader walked into that says where they are.
    repository: next.directory ? undefined : blank(next.repository),
  }
}

/** The empty string as absence, so a cleared control leaves no `&extensions=` behind in the URL. */
function blank(value: string | undefined): string | undefined {
  return value === '' ? undefined : value
}
