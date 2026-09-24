import type { QueryClient } from '@tanstack/react-query'
import { queryOptions } from '@tanstack/react-query'
import {
  fetchExcludedPathSuggestions,
  fetchExcludedPaths,
  fetchProject,
  fetchProjectOverview,
  fetchProjects,
} from '@/features/projects/api'
import { projectKey, projectsKey } from '@/lib/queryKeys'
import type { OverviewParameters } from '@/lib/urls/overviewParams'

/** The list of what exists. MCP has no discovery, so this is the only place a project is visible. */
export function projectsQuery() {
  return queryOptions({ queryFn: () => fetchProjects(), queryKey: projectsKey })
}

export function projectQuery(slug: string) {
  return queryOptions({ queryFn: () => fetchProject(slug), queryKey: projectKey(slug) })
}

/**
 * The project as a whole, computed live over the page's filters (#216). Its own query rather than a
 * field on the one above, because the overview is the page's heaviest read and the only one that
 * would grow with the project — keeping it separate is what lets the header and the repository table
 * be shown from a cached `projectQuery` while this one is still in flight. It sits under the
 * project's key, so `invalidateProject` covers it after a build and after the excluded paths change.
 */
export function projectOverviewQuery(slug: string, parameters: OverviewParameters) {
  return queryOptions({
    queryFn: () => fetchProjectOverview(slug, parameters),
    queryKey: [...projectKey(slug), 'overview', parameters],
    // Computed from the index and the excluded paths, and neither changes without something that
    // invalidates the project's key: a build, or a save of the setting. The project's own record
    // above is not fresh on those terms, because its repository list and index status change
    // without either.
    staleTime: Infinity,
  })
}

/** The overview page's excluded paths, as the settings page edits them. */
export function excludedPathsQuery(slug: string) {
  return queryOptions({
    queryFn: () => fetchExcludedPaths(slug),
    queryKey: [...projectKey(slug), 'excluded-paths'],
  })
}

/**
 * Patterns proposed for that setting from the index (#217). The form runs it from its Suggest button,
 * never on load, since it reads the whole index to answer.
 */
export function excludedPathSuggestionsQuery(slug: string) {
  return queryOptions({
    queryFn: () => fetchExcludedPathSuggestions(slug),
    queryKey: [...projectKey(slug), 'excluded-paths', 'suggestions'],
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
