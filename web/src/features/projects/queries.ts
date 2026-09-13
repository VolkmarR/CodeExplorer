import type { QueryClient } from '@tanstack/react-query'
import { queryOptions } from '@tanstack/react-query'
import { api } from '@/lib/api'
import { projectKey, projectsKey } from '@/lib/queryKeys'

/** The list of what exists. MCP has no discovery, so this is the only place a project is visible. */
export function projectsQuery() {
  return queryOptions({ queryFn: () => api.projects(), queryKey: projectsKey })
}

export function projectQuery(slug: string) {
  return queryOptions({ queryFn: () => api.project(slug), queryKey: projectKey(slug) })
}

/**
 * Everything one change to a project makes stale. The project's own key covers its page and the
 * searches and file reads keyed under it; the list is a separate key carrying the same index status
 * and repository count, so it has to go too or it keeps showing the state from before the change.
 * One function, because three call sites getting this right independently is three chances not to.
 */
export async function invalidateProject(queryClient: QueryClient, slug: string) {
  await queryClient.invalidateQueries({ queryKey: projectKey(slug) })
  await queryClient.invalidateQueries(projectsQuery())
}
