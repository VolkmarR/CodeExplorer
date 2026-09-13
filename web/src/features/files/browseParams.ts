/** What the browse view needs from the URL: which files, and within which repository. */
export interface BrowseParameters {
  glob: string
  repository?: string
}

/**
 * `*` lists everything, which is the right resting state for a view whose job is to show what is in
 * there. Glob matching is the SQL `GLOB` operator (ADR-0004), so `*` crosses `/` and a bare `*.cs`
 * finds sources at any depth.
 */
export function validateBrowseSearch(search: Record<string, unknown>): BrowseParameters {
  return {
    glob: typeof search.glob === 'string' && search.glob !== '' ? search.glob : '*',
    repository:
      typeof search.repository === 'string' && search.repository !== ''
        ? search.repository
        : undefined,
  }
}
