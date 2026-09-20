import { queryOptions } from '@tanstack/react-query'
import type { HistoryParameters } from '@/lib/urls/historyParams'
import { api } from '@/lib/api'
import { projectKey } from '@/lib/queryKeys'

/**
 * A page of the change log, keyed under the project so the build that appends commits invalidates it
 * without this feature having to hear about builds.
 */
export function commitsQuery(project: string, parameters: HistoryParameters) {
  return queryOptions({
    queryFn: () => api.commits(project, parameters.page, parameters.repository),
    queryKey: [...projectKey(project), 'commits', parameters],
  })
}

/**
 * One commit's own record, for the page a link to a SHA opens. A commit never changes, so this stays
 * fresh for as long as the page is open; only a rebuild, which invalidates the project, replaces it.
 */
export function commitQuery(project: string, sha: string) {
  return queryOptions({
    queryFn: () => api.commit(project, sha),
    queryKey: [...projectKey(project), 'commit', sha, 'detail'],
    staleTime: Infinity,
  })
}

/**
 * The files one commit touched, beside the commit itself rather than with it: the list is as long as
 * the commit is wide, and the page draws the message and the sums without waiting for it.
 *
 * A sibling of the record above and not a key under it. Both hang off `…, 'commit', sha`, and this
 * one was that prefix exactly — so invalidating the record, whose key it was a prefix of, refetched
 * the file list with it, and anything invalidating the file list took the record too. Two requests
 * that answer separately are keyed separately.
 */
export function commitFilesQuery(project: string, sha: string) {
  return queryOptions({
    queryFn: () => api.commitFiles(project, sha),
    queryKey: [...projectKey(project), 'commit', sha, 'files'],
    staleTime: Infinity,
  })
}
