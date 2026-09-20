/**
 * What the change log needs from the URL: which page, and which repository if not all of them. In
 * the URL and nowhere else, like search, so a page of history can be pasted and opens as the same
 * page (ADR-0004).
 */
export interface HistoryParameters {
  page: number
  repository?: string
}

/** A hand-edited or truncated URL still opens the first page of everything rather than throwing. */
export function validateHistorySearch(search: Record<string, unknown>): HistoryParameters {
  const page = Number(search.page)
  return {
    page: Number.isInteger(page) && page > 0 ? page : 1,
    repository:
      typeof search.repository === 'string' && search.repository !== ''
        ? search.repository
        : undefined,
  }
}

/**
 * The URL of the first page of the log, of every repository or of the one named, so no link has to
 * remember the page it starts on — page 7 of one repository is not page 7 of another.
 *
 * The empty slug is the select's way of saying "every repository" and is not what this parameter
 * means by one, so it is translated here for the reason `globSearch` gives at length.
 */
export function historySearch(repository?: string): HistoryParameters {
  return { page: 1, repository: repository === '' ? undefined : repository }
}
