/**
 * The key roots every feature's queries hang off. They live here rather than in the features
 * themselves because they are shared: a build replaces a project's whole index, so the project page
 * invalidates `projectKey(slug)` and thereby the searches and file reads that features/search and
 * features/files keyed under it. A prefix written twice would eventually be written differently.
 */
export const projectsKey = ['projects'] as const

/**
 * Its own root and under nothing: who is signed in belongs to the session rather than to any project,
 * and no refresh, creation or deletion makes it stale.
 */
export const authKey = ['auth'] as const

export function projectKey(slug: string) {
  return ['project', slug] as const
}

/**
 * Deliberately its own root rather than one under `projectKey`: a refresh's progress is a job, not
 * part of the project, and nesting it would make every invalidation after a successful refresh
 * refetch the status that triggered it.
 */
export function refreshKey(slug: string) {
  return ['refresh', slug] as const
}

/**
 * Tool-call counts are their own root for the same reason: they live in the server's memory and are
 * not part of the index, so nothing that invalidates a project after a build should refetch them.
 * The project list's summary sits under the root and each project's page beside it.
 */
export const toolActivityKey = ['tool-calls'] as const

export function toolCallsKey(slug: string) {
  return ['tool-calls', slug] as const
}
