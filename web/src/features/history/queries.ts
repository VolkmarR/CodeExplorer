import { queryOptions } from '@tanstack/react-query'
import type { HistoryParameters } from '@/features/history/historyParams'
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
 */
export function commitFilesQuery(project: string, sha: string) {
  return queryOptions({
    queryFn: () => api.commitFiles(project, sha),
    queryKey: [...projectKey(project), 'commit', sha],
    staleTime: Infinity,
  })
}
