/**
 * The key roots every feature's queries hang off. They live here rather than in the features
 * themselves because they are shared: a build replaces a project's whole index, so the project page
 * invalidates `projectKey(slug)` and thereby the searches and file reads that features/search and
 * features/files keyed under it. A prefix written twice would eventually be written differently.
 */
export const projectsKey = ['projects'] as const

export function projectKey(slug: string) {
  return ['project', slug] as const
}
