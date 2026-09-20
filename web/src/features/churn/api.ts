import type { ChurnParameters } from '@/lib/urls/churnParams'
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
  /**
   * What the rows are: the number of path segments they were rolled up to, or null where each row is
   * a file. The page draws the two differently — a directory is clicked into, a file is opened — so
   * it reads this rather than guessing from the path, which cannot tell them apart.
   */
  depth: number | null
  /** What the window is written in, most-changed first, which is what the extension filter offers. */
  extensions: ChurnedExtension[]
  /** How many paths the filters kept out, so a narrowed ranking is not read as the whole window. */
  hidden: number
}

/**
 * One extension a window holds. `commits` is what the list is ranked by and `files` how many paths
 * those commits were, which is the pair that shows a handful of generated files accounting for
 * hundreds of commits. The empty string is a file whose name has no extension.
 */
export interface ChurnedExtension {
  extension: string
  commits: number
  files: number
}

/**
 * Days rather than a pair of dates: the window ends at the newest commit the index holds, and only
 * the index knows where that is — a client sending dates would be guessing at it.
 *
 * Takes the whole parameter set rather than an argument each: every field of it is a search param,
 * and a call listing five of six is how one gets dropped without the types noticing.
 */
export function fetchChurn(project: string, parameters: ChurnParameters) {
  const search: Record<string, string> = { days: String(parameters.days) }
  if (parameters.directory) search.directory = parameters.directory
  if (parameters.depth !== undefined) search.depth = String(parameters.depth)
  if (parameters.extensions) search.extensions = parameters.extensions
  return http
    .get(`projects/${project}/churn`, {
      searchParams: scoped(search, parameters.repository),
    })
    .json<Churn>()
}
