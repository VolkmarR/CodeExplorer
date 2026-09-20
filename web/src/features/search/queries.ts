import { queryOptions } from '@tanstack/react-query'
import type { SearchParameters } from '@/lib/urls/searchParams'
import type { GrepResult } from '@/features/search/api'
import { fetchSearch } from '@/features/search/api'
import { projectKey } from '@/lib/queryKeys'

/**
 * A search and how long it took to come back. The timing is measured here rather than reported by
 * the server, and it is the round trip and not the query: what it answers is "is this index fast
 * enough to search interactively", which is the question the number on the page is read for. It
 * rides with the result rather than being read off the query's timestamps, so a cached answer keeps
 * the time it actually took instead of reporting zero.
 */
export interface TimedSearch {
  result: GrepResult
  elapsedMs: number
}

/**
 * Keyed under the project, so the build that replaces the index invalidates every search over it
 * without this feature having to hear about builds — which is also why it never goes stale on its
 * own: the same query over the same index has the same answer, and the pager walking back to a page
 * already seen should cost nothing.
 */
export function searchQuery(project: string, query: SearchParameters) {
  return queryOptions({
    queryFn: async (): Promise<TimedSearch> => {
      const started = performance.now()
      const result = await fetchSearch(project, query)
      return { elapsedMs: Math.round(performance.now() - started), result }
    },
    queryKey: [...projectKey(project), 'search', query],
    staleTime: Infinity,
  })
}
