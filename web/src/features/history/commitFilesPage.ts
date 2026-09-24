/**
 * How many of a commit's files one page draws: the page size every other list in the app gets from
 * the server, so a page of this one is as long as a page of History or of the file listing.
 */
export const COMMIT_FILES_PAGE_SIZE = 50

/**
 * One page of a commit's files, cut from the whole list in the browser.
 *
 * The server answers with every file in one response, and it answers quickly even for a commit that
 * touched eighty thousand; what took the time was drawing a row and a router link for each of them.
 * So the list is paged where it is drawn rather than where it is read, and the request stays the
 * one it was.
 *
 * A page past the end is answered as the last page, the way the server answers the file listing, so
 * a stale or hand-edited URL shows rows and a pager that can leave rather than an empty list.
 */
export function commitFilesPage<T>(files: readonly T[], requested: number) {
  const lastPage = Math.max(1, Math.ceil(files.length / COMMIT_FILES_PAGE_SIZE))
  const page = Math.min(Math.max(1, requested), lastPage)
  const start = (page - 1) * COMMIT_FILES_PAGE_SIZE
  return {
    page,
    lastPage,
    /** The 1-based position of the page's first row in the whole list, for "showing 51–100". */
    first: start + 1,
    rows: files.slice(start, start + COMMIT_FILES_PAGE_SIZE),
    total: files.length,
  }
}
