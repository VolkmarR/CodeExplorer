import { queryOptions } from '@tanstack/react-query'
import type { BrowseParameters } from '@/lib/urls/browseParams'
import {
  fetchBlame,
  fetchBrowse,
  fetchDeclarations,
  fetchFile,
  fetchImports,
  fetchTree,
  type FileContent,
} from '@/features/files/api'
import { projectKey } from '@/lib/queryKeys'

/**
 * A read of one file, keyed under the project by what it reads and the path. A file's content never
 * changes under a given index — only a build replaces it, and a build invalidates the whole project
 * key — so it and everything read beside it stay fresh for as long as the page is open.
 *
 * The blame, the imports and the declarations are requests of their own beside the content rather
 * than part of it: each takes longer than the content (the runs are the size of the file, placing a
 * declaration means reading the lines above it), and the code should be on screen before they are.
 */
function fileReadQuery<T>(read: string, fetch: (project: string, path: string) => Promise<T>) {
  return (project: string, path: string) =>
    queryOptions({
      queryFn: () => fetch(project, path),
      queryKey: [...projectKey(project), read, path],
      staleTime: Infinity,
    })
}

export const fileQuery = fileReadQuery('file', fetchFile)
export const importsQuery = fileReadQuery('imports', fetchImports)
export const declarationsQuery = fileReadQuery('declarations', fetchDeclarations)
const blameRead = fileReadQuery('blame', fetchBlame)

type Blameable = Pick<FileContent, 'qualifiedPath' | 'lastCommit' | 'skipReason'>

/**
 * Whether a file has an attribution to ask for: not where its repository has no history in the
 * index, and not for a file the build skipped, which has no lines to attribute. Decided once, so the
 * rail's recent commits, the gutter and the button that offers it cannot disagree.
 */
export function canBlame(file: Blameable) {
  return file.lastCommit !== null && file.skipReason === null
}

/**
 * A file's attribution, asked for only where `canBlame` says there is one. `wanted` is the caller's
 * own reason to hold back, such as a gutter that is switched off.
 */
export function blameQuery(project: string, file: Blameable, wanted = true) {
  return queryOptions({
    ...blameRead(project, file.qualifiedPath),
    enabled: wanted && canBlame(file),
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
    queryFn: () => fetchBrowse(project, parameters),
    queryKey: [...projectKey(project), 'browse', parameters],
    staleTime: Infinity,
  })
}
