import { positiveInteger, textOrEmpty } from '@/lib/urls/coerce'
import { asView, type Origin } from '@/lib/urls/views'

/**
 * What the commit view needs from the URL: which commit, and which view linked to it. The SHA is a
 * search param and not a path segment, for the reason the file view's path is one — a link to a
 * thing the index holds is the same shape wherever the UI names one.
 */
export interface CommitParameters {
  sha: string
  /** The view the reader came from, so the trail says how they got here and not where the route is. */
  from?: Origin['view']
  /**
   * Which page of the commit's files. Optional, and undefined for the first, so `commitSearch` and
   * the four places that link to a commit need not name a page. Keeping page 1 out of the URL is not
   * this type's job but the root route's (`bareFirstPage`), as it is for every paged view.
   */
  page?: number
}

/** A hand-edited or truncated URL still opens, and the page says it names no commit rather than throwing. */
export function validateCommitSearch(search: Record<string, unknown>): CommitParameters {
  const page = positiveInteger(search.page)
  return {
    from: asView(search.from),
    page: page === 1 ? undefined : page,
    sha: textOrEmpty(search.sha),
  }
}

/**
 * The URL of one commit. Four places link to one — a history row, a file's last change, the rail's
 * recent commits, a blame run — and the origin travels with the SHA rather than being spelled out
 * beside it at each of them.
 */
export function commitSearch(sha: string, from?: Origin['view']): CommitParameters {
  return { from, sha }
}
