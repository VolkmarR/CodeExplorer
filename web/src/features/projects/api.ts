import type { ChurnFile } from '@/features/churn/api'
import type { CommitRef } from '@/features/history/api'
import { http } from '@/lib/http'

/**
 * A project, its repositories, and what the build computed about it. Each shape mirrors a record in
 * the C# host; nothing is invented here.
 */

export interface ProjectIndexStatus {
  builtAt: string | null
  ftsIndexed: boolean
  files: number
  lines: number
}

export interface ProjectSummary {
  slug: string
  name: string
  /** Declared at creation and never editable: its files are named without a repository slug (ADR-0006). */
  singleRepository: boolean
  repositories: number
  index: ProjectIndexStatus
}

export interface RepositoryDetail {
  slug: string
  url: string
  hasCredential: boolean
  headCommit: string | null
  fileCount: number | null
  lineCount: number | null
  /** How many commits the index holds for it. Zero with a `headCommit` means history never arrived. */
  commits: number
  newestCommit: CommitRef | null
}

export interface ProjectDetail {
  slug: string
  name: string
  singleRepository: boolean
  index: ProjectIndexStatus
  repositories: RepositoryDetail[]
}

/** What POST /projects answers with: the row written, not the composed view the list returns. */
export interface CreatedProject {
  slug: string
  name: string
}

/** What POST /projects/{project}/repositories answers with. The credential is deliberately absent. */
export interface CreatedRepository {
  slug: string
  url: string
  hasCredential: boolean
}

/**
 * How much of a project one language accounts for. `mapped` is false when no language profile covers
 * the extension, in which case `name` is the extension itself — a weaker claim than a language name,
 * and shown as one.
 */
export interface LanguageShare {
  name: string
  mapped: boolean
  files: number
  lines: number
  /** Of `files`, how many are in the index without lines: binary or over the size ceiling. */
  skipped: number
}

/** One entry at the top level of a repository. `files` counts everything beneath a directory. */
export interface OverviewEntry {
  qualifiedPath: string
  isDirectory: boolean
  files: number
  lines: number
  sizeBytes: number
}

export interface OverviewFile {
  qualifiedPath: string
  lineCount: number
  sizeBytes: number
}

/**
 * The churn section of an overview. `since` and `until` are null together when the project had no
 * imported history when it was built, which is a different answer from a project nobody changed.
 *
 * Its rows are the churn feature's, and named as such: the overview shows a ranking it did not
 * compute, over a window it did not choose.
 */
export interface OverviewChurn {
  days: number
  since: string | null
  until: string | null
  files: ChurnFile[]
}

/** Who has touched the project most. Who to ask, never who wrote it (CONTEXT.md, Attribution). */
export interface OverviewAuthor {
  name: string
  email: string
  commits: number
  lastCommit: string
}

/**
 * What the build computed about the project as a whole and stored with the index, so this page shows
 * what an agent calling `project_overview` is told rather than a second opinion about it.
 */
export interface IndexOverview {
  languages: LanguageShare[]
  otherLanguages: number
  tree: OverviewEntry[]
  otherEntries: number
  largestFiles: OverviewFile[]
  churn: OverviewChurn
  authors: OverviewAuthor[]
}

/**
 * Exactly one of the two is set. `unavailable` is the server's own prose saying why there is nothing
 * to show — a project never built, or one whose first build is still running — so the page says what
 * an agent asking the same question is told, rather than leaving a gap the reader has to interpret.
 */
export interface ProjectOverviewDetail {
  overview: IndexOverview | null
  unavailable: string | null
}

export function fetchProjects() {
  return http.get('projects').json<ProjectSummary[]>()
}

export function fetchProject(slug: string) {
  return http.get(`projects/${slug}`).json<ProjectDetail>()
}

export function fetchProjectOverview(slug: string) {
  return http.get(`projects/${slug}/overview`).json<ProjectOverviewDetail>()
}

export function createProject(slug: string, name: string, singleRepository: boolean) {
  return http.post('projects', { json: { name, singleRepository, slug } }).json<CreatedProject>()
}

export function removeProject(slug: string) {
  return http.delete(`projects/${slug}`).then(() => undefined)
}

export function addRepository(
  project: string,
  slug: string,
  url: string,
  credential: string | null,
) {
  return http
    .post(`projects/${project}/repositories`, { json: { slug, url, credential } })
    .json<CreatedRepository>()
}

export function removeRepository(project: string, slug: string) {
  return http.delete(`projects/${project}/repositories/${slug}`).then(() => undefined)
}
