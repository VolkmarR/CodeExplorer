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
 * The files that changed most over the span the same page of commits covers. Keyed by the same
 * parameters as the list, so paging or narrowing to a repository moves both together.
 */
export function hotFilesQuery(project: string, parameters: HistoryParameters) {
  return queryOptions({
    queryFn: () => api.hotFiles(project, parameters.page, parameters.repository),
    queryKey: [...projectKey(project), 'hotFiles', parameters],
  })
}

/**
 * The files one commit touched, fetched when a row is opened. A commit never changes, so this stays
 * fresh for as long as the page is open; only a rebuild, which invalidates the project, replaces it.
 */
export function commitFilesQuery(project: string, sha: string) {
  return queryOptions({
    queryFn: () => api.commitFiles(project, sha),
    queryKey: [...projectKey(project), 'commit', sha],
    staleTime: Infinity,
  })
}
