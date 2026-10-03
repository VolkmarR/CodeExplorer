/**
 * Where one page of a paged list sits in the whole of it: the page, the last page, and the 1-based
 * positions of its first and last rows, for "showing 51–100". Every paged view needs the same sums,
 * and each used to do them itself.
 *
 * A page past either end is answered as the nearest page there is, the way the server answers the
 * file listing, so a stale or hand-edited URL shows rows and a pager that can leave rather than an
 * empty list. For a page the server already answered, that is a no-op.
 */
export function pageSpan(page: number, pageSize: number, total: number) {
  const lastPage = Math.max(1, Math.ceil(total / pageSize))
  const current = Math.min(Math.max(1, page), lastPage)
  const first = (current - 1) * pageSize + 1
  return { first, last: Math.min(total, first + pageSize - 1), lastPage, page: current }
}
