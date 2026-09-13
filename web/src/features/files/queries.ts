import { queryOptions } from '@tanstack/react-query'
import type { BrowseParameters } from '@/features/files/browseParams'
import { api } from '@/lib/api'
import { projectKey } from '@/lib/queryKeys'

/**
 * A file's content never changes under a given index — only a build replaces it, and a build
 * invalidates the whole project key — so this stays fresh for as long as the page is open.
 */
export function fileQuery(project: string, path: string) {
  return queryOptions({
    queryFn: () => api.file(project, path),
    queryKey: [...projectKey(project), 'file', path],
    staleTime: Infinity,
  })
}

/** One level of the tree. Keyed by the level, so walking back up is already in the cache. */
export function treeQuery(project: string, path: string) {
  return queryOptions({
    queryFn: () => api.tree(project, path),
    queryKey: [...projectKey(project), 'tree', path],
  })
}

/** The listing behind the browse view, keyed under the project like everything else read from it. */
export function browseQuery(project: string, parameters: BrowseParameters) {
  return queryOptions({
    queryFn: () => api.browse(project, parameters.glob, parameters.repository),
    queryKey: [...projectKey(project), 'browse', parameters],
  })
}
