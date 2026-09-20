import type { QueryClient } from '@tanstack/react-query'
import { queryOptions } from '@tanstack/react-query'
import { fetchProject, fetchProjectOverview, fetchProjects } from '@/features/projects/api'
import { projectKey, projectsKey } from '@/lib/queryKeys'

/** The list of what exists. MCP has no discovery, so this is the only place a project is visible. */
export function projectsQuery() {
  return queryOptions({ queryFn: () => fetchProjects(), queryKey: projectsKey })
}

export function projectQuery(slug: string) {
  return queryOptions({ queryFn: () => fetchProject(slug), queryKey: projectKey(slug) })
}

/**
 * What the build computed about the project as a whole. Its own query rather than a field on the one
 * above, because the overview is the page's heaviest read and the only one that would grow with the
 * project — keeping it separate is what lets the header and the repository table be shown from a
 * cached `projectQuery` while this one is still in flight. It goes stale on exactly the same event,
 * so it sits under the project's key and `invalidateProject` already covers it.
 */
export function projectOverviewQuery(slug: string) {
  return queryOptions({
    queryFn: () => fetchProjectOverview(slug),
    queryKey: [...projectKey(slug), 'overview'],
    // What the build computed does not change until the next build, which invalidates this with the
    // rest of the project. Everything under the project's key is fresh on those terms; the project's
    // own record above is not, because its repository list and index status change without a build.
    staleTime: Infinity,
  })
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
