import { http, scoped } from '@/lib/http'

/** The churn ranking's shapes and call. Each shape mirrors a record in the C# host. */

/**
 * One file of the churn ranking. `qualifiedPath` is always set — a window ranks paths a later commit
 * removed, and those are named too — and `atHead` says whether there is still a file there to open.
 */
export interface ChurnFile {
  qualifiedPath: string
  repositorySlug: string
  atHead: boolean
  commits: number
  added: number
  deleted: number
}

/**
 * A churn ranking and the window it covers. `since` and `until` are null together when the scope
 * holds no commit at all, which is a project without imported history.
 *
 * They are part of the answer rather than an echo of the request: the window ends at the newest
 * commit the index holds, not today, so a stale index shows as one.
 */
export interface Churn {
  since: string | null
  until: string | null
  files: ChurnFile[]
  /**
   * The repositories the ranking cannot speak for, so half a project's churn is not read as all of
   * it. Empty where the question does not arise: one repository, or a view narrowed to one.
   */
  withoutHistory: string[]
}

/**
 * Days rather than a pair of dates: the window ends at the newest commit the index holds, and only
 * the index knows where that is — a client sending dates would be guessing at it.
 */
export function fetchChurn(project: string, days: number, repository?: string) {
  return http
    .get(`projects/${project}/churn`, { searchParams: scoped({ days: String(days) }, repository) })
    .json<Churn>()
}
