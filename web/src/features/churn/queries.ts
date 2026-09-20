import { queryOptions } from '@tanstack/react-query'
import type { ChurnParameters } from '@/lib/urls/churnParams'
import { api } from '@/lib/api'
import { projectKey } from '@/lib/queryKeys'

/**
 * A churn ranking, keyed under the project so the build that appends commits invalidates it without
 * this feature having to hear about builds.
 */
export function churnQuery(project: string, parameters: ChurnParameters) {
  return queryOptions({
    queryFn: () => api.churn(project, parameters.days, parameters.repository),
    queryKey: [...projectKey(project), 'churn', parameters],
  })
}
