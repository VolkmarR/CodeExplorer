import { pageSpan } from '@/lib/paging'

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
 * A page past the end is answered as the last page, as `pageSpan` answers every list.
 */
export function commitFilesPage<T>(files: readonly T[], requested: number) {
  const { page, lastPage, first, last } = pageSpan(requested, COMMIT_FILES_PAGE_SIZE, files.length)
  return { first, lastPage, page, rows: files.slice(first - 1, last), total: files.length }
}
