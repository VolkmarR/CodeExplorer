/**
 * What the browse view needs from the URL. The view has two modes and `glob` picks between them: empty
 * walks the tree a level at a time from `path`, set flattens the whole project to what matches. Both
 * live in one route because they are one question asked two ways, and a link carries which was asked.
 */
export interface BrowseParameters {
  path: string
  glob: string
  repository?: string
  /** Which page of a glob's matches. The tree ignores it: a level is as deep as it is. */
  page: number
}

/**
 * Glob matching is the SQL `GLOB` operator (ADR-0004), so `*` crosses `/` and a bare `*.cs` finds
 * sources at any depth. An empty `path` is the project root, which lists its repositories: a qualified
 * path begins with one, so there is no level above them.
 */
/**
 * The URL of the tree at one level. Written here rather than spelled out at each link, so the empty
 * glob that selects the tree cannot be forgotten at one of them and quietly open the flat list.
 */
export function treeSearch(path = ''): BrowseParameters {
  return { glob: '', page: 1, path }
}

export function validateBrowseSearch(search: Record<string, unknown>): BrowseParameters {
  const page = Number(search.page)
  return {
    glob: typeof search.glob === 'string' ? search.glob : '',
    page: Number.isInteger(page) && page > 0 ? page : 1,
    path: typeof search.path === 'string' ? search.path : '',
    repository:
      typeof search.repository === 'string' && search.repository !== ''
        ? search.repository
        : undefined,
  }
}
