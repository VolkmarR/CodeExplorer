import { queryOptions } from '@tanstack/react-query'
import type { SearchParameters } from '@/features/search/searchParams'
import { api } from '@/lib/api'
import { projectKey } from '@/lib/queryKeys'

/**
 * Keyed under the project, so the build that replaces the index invalidates every search over it
 * without this feature having to hear about builds.
 */
export function searchQuery(project: string, query: SearchParameters) {
  return queryOptions({
    queryFn: () => api.search(project, query),
    queryKey: [...projectKey(project), 'search', query],
  })
}
