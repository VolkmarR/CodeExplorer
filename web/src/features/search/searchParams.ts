/**
 * Everything a search is made of lives in the URL, so a result list can be pasted to a colleague and
 * opens as the same search (ADR-0004). It is defined here rather than in the route file so that the
 * form and the results can read the type without importing the route that renders them.
 */
export interface SearchParameters {
  q: string
  regex: boolean
  caseSensitive: boolean
  extension?: string
  path?: string
  page: number
}

/**
 * Unknown or malformed parameters fall back to the default rather than throwing: a hand-edited or
 * truncated URL should still open a page, and an empty query is the resting state of this one.
 */
export function validateSearch(search: Record<string, unknown>): SearchParameters {
  const page = Number(search.page)
  return {
    caseSensitive: search.caseSensitive === true || search.caseSensitive === 'true',
    extension:
      typeof search.extension === 'string' && search.extension !== ''
        ? search.extension
        : undefined,
    page: Number.isInteger(page) && page > 0 ? page : 1,
    path: typeof search.path === 'string' && search.path !== '' ? search.path : undefined,
    q: typeof search.q === 'string' ? search.q : '',
    regex: search.regex === true || search.regex === 'true',
  }
}
