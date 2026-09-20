import { queryOptions } from '@tanstack/react-query'
import type { ChurnParameters } from '@/lib/urls/churnParams'
import { fetchChurn } from '@/features/churn/api'
import { projectKey } from '@/lib/queryKeys'

/**
 * A churn ranking, keyed under the project so the build that appends commits invalidates it without
 * this feature having to hear about builds, and fresh until one does. The window is counted from the
 * newest commit in the index rather than from now (CONTEXT.md, History), so no clock makes this
 * answer older than the build that produced it.
 */
export function churnQuery(project: string, parameters: ChurnParameters) {
  return queryOptions({
    queryFn: () => fetchChurn(project, parameters),
    queryKey: [...projectKey(project), 'churn', parameters],
    staleTime: Infinity,
  })
}
