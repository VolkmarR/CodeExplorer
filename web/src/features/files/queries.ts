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

/**
 * A file's attribution, fetched beside its content rather than with it: the runs are the size of the
 * file, and the code should be on screen before they are. Fresh for as long as the content is, and
 * for the same reason.
 */
export function blameQuery(project: string, path: string) {
  return queryOptions({
    queryFn: () => api.blame(project, path),
    queryKey: [...projectKey(project), 'blame', path],
    staleTime: Infinity,
  })
}

/**
 * The two directions of the import graph, a query each because they are two requests: the rail draws
 * what a file imports as soon as that arrives rather than waiting on the reverse lookup of a hub.
 * Both are fixed under a given index, like the content and the blame beside them.
 */
export function importsQuery(project: string, path: string) {
  return queryOptions({
    queryFn: () => api.imports(project, path),
    queryKey: [...projectKey(project), 'imports', path],
    staleTime: Infinity,
  })
}

export function dependentsQuery(project: string, path: string) {
  return queryOptions({
    queryFn: () => api.dependents(project, path),
    queryKey: [...projectKey(project), 'dependents', path],
    staleTime: Infinity,
  })
}

/**
 * What a file declares, its own request beside the file's content for the reason the blame runs are:
 * placing a candidate line means reading the lines above it, and the code should be on screen before
 * that comes back. Fixed under a given index, like everything else read from it.
 */
export function declarationsQuery(project: string, path: string) {
  return queryOptions({
    queryFn: () => api.declarations(project, path),
    queryKey: [...projectKey(project), 'declarations', path],
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
    queryFn: () => api.browse(project, parameters.glob, parameters.page, parameters.repository),
    queryKey: [...projectKey(project), 'browse', parameters],
  })
}
