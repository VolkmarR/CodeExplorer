import { queryOptions } from '@tanstack/react-query'
import type { BrowseParameters } from '@/lib/urls/browseParams'
import {
  fetchBlame,
  fetchBrowse,
  fetchDeclarations,
  fetchDependents,
  fetchFile,
  fetchImports,
  fetchTree,
} from '@/features/files/api'
import { projectKey } from '@/lib/queryKeys'

/**
 * A file's content never changes under a given index — only a build replaces it, and a build
 * invalidates the whole project key — so this stays fresh for as long as the page is open.
 */
export function fileQuery(project: string, path: string) {
  return queryOptions({
    queryFn: () => fetchFile(project, path),
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
    queryFn: () => fetchBlame(project, path),
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
    queryFn: () => fetchImports(project, path),
    queryKey: [...projectKey(project), 'imports', path],
    staleTime: Infinity,
  })
}

export function dependentsQuery(project: string, path: string) {
  return queryOptions({
    queryFn: () => fetchDependents(project, path),
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
    queryFn: () => fetchDeclarations(project, path),
    queryKey: [...projectKey(project), 'declarations', path],
    staleTime: Infinity,
  })
}

/**
 * One level of the tree. Keyed by the level, so walking back up is already in the cache — and fresh
 * for as long as the page is open, for the reason the content above is: the tree is the index's own
 * listing, and only a build replaces it.
 */
export function treeQuery(project: string, path: string) {
  return queryOptions({
    queryFn: () => fetchTree(project, path),
    queryKey: [...projectKey(project), 'tree', path],
    staleTime: Infinity,
  })
}

/**
 * The listing behind the browse view, keyed under the project like everything else read from it, and
 * fixed under a given index like everything else read from it.
 */
export function browseQuery(project: string, parameters: BrowseParameters) {
  return queryOptions({
    queryFn: () => fetchBrowse(project, parameters.glob, parameters.page, parameters.repository),
    queryKey: [...projectKey(project), 'browse', parameters],
    staleTime: Infinity,
  })
}
