import { flag, positiveInteger, text, textOrEmpty } from '@/lib/urls/coerce'

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
  return {
    caseSensitive: flag(search.caseSensitive),
    extension: text(search.extension),
    page: positiveInteger(search.page) ?? 1,
    path: text(search.path),
    q: textOrEmpty(search.q),
    regex: flag(search.regex),
  }
}

/**
 * The URL of an empty search. Written here rather than spelled out at each link, for the reason
 * `treeSearch` gives in `browseParams.ts`: four fields with no obvious defaults, written out by
 * hand at every link to the view, is four chances to link to a search nobody asked for.
 */
export function searchSearch(q = ''): SearchParameters {
  return { caseSensitive: false, page: 1, q, regex: false }
}
