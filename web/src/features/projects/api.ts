import type { ChurnFile } from '@/features/churn/api'
import type { CommitRef } from '@/features/history/api'
import { http, scoped } from '@/lib/http'
import type { OverviewParameters } from '@/lib/urls/overviewParams'

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

/** One folder at the top level of a repository. `files` counts everything beneath it. */
export interface OverviewFolder {
  qualifiedPath: string
  files: number
  lines: number
  sizeBytes: number
}

/**
 * The top level of one repository: its folders listed, its root files counted instead of listed.
 * `qualifiedPath` is the repository's root, empty in a single-repository project; `rootFiles` counts
 * the files directly there as one entry at that path, and is null for a repository with none.
 */
export interface OverviewRoot {
  qualifiedPath: string
  folders: OverviewFolder[]
  rootFiles: OverviewFolder | null
}

/** One of the largest indexed files; a file the build skipped is never ranked here. */
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
 * What a project's index says about the project as a whole. The page computes it live, over its own
 * filters and without the project's excluded paths (#216), so it can differ from what an agent calling
 * `project_overview` is told from the stored row once either applies; with neither, the two agree.
 */
export interface IndexOverview {
  languages: LanguageShare[]
  otherLanguages: number
  tree: OverviewRoot[]
  otherFolders: number
  largestFiles: OverviewFile[]
  churn: OverviewChurn
  authors: OverviewAuthor[]
}

/**
 * How many files the excluded paths kept out of each kind of section: the files at HEAD (Languages,
 * Top level, Largest files), and the files the window's commits touched: once for Most changed and
 * once for Most commits, which count those commits differently.
 */
export interface OverviewExcluded {
  files: number
  changedFiles: number
  committedFiles: number
}

/**
 * One file of the Hotspots card (#211): its commits in the window times its lines at HEAD. Only a
 * file at HEAD has one, so every row links to a file that is there.
 */
export interface Hotspot {
  qualifiedPath: string
  commits: number
  lines: number
  score: number
}

/**
 * The Hotspots card. The page's alone: the stored overview and `project_overview` have no hotspots.
 * `excluded` counts the files at HEAD the window touched that the excluded paths left out, and is null
 * where nothing was excluded.
 */
export interface OverviewHotspots {
  files: Hotspot[]
  excluded: number | null
}

/**
 * One file of the Most authors per file card (#212), over the whole imported history. `first`,
 * `second` and `third` are the commit shares of its three most frequent authors, 0 to 1, and 0 where
 * it has fewer.
 */
export interface AuthoredFile {
  qualifiedPath: string
  authors: number
  commits: number
  first: number
  second: number
  third: number
}

/**
 * The Most authors per file card. The page's alone, like the hotspots. `excluded` counts the files at
 * HEAD a commit touched that the excluded paths left out, and is null where nothing was excluded.
 */
export interface OverviewAuthorsPerFile {
  files: AuthoredFile[]
  excluded: number | null
}

/**
 * Either `overview` or `unavailable` is set. `unavailable` is the server's own prose saying why there
 * is nothing to show — a project never built, one whose first build is still running, a repository
 * the project does not have — so the page says what an agent asking the same question is told,
 * rather than leaving a gap the reader has to interpret.
 *
 * `excludedPatterns` counts the project's setting whether or not it was applied, which is what the
 * page offers the "show excluded" switch on; `excluded` is null wherever nothing was left out.
 */
export interface ProjectOverviewDetail {
  overview: IndexOverview | null
  unavailable: string | null
  excludedPatterns: number
  excluded: OverviewExcluded | null
  /** The cards only this page draws. Set with `overview`. */
  cards: OverviewCards | null
}

/** The cards only the overview page draws: the stored overview and `project_overview` have none of them. */
export interface OverviewCards {
  hotspots: OverviewHotspots
  authorsPerFile: OverviewAuthorsPerFile
  folderCoupling: OverviewFolderCoupling
  fileChanges: OverviewFileChanges
}

/** How long one bar of the Files added and deleted card is: a UTC day, a week from Monday, or a month. */
export type ChangePeriod = 'Day' | 'Week' | 'Month'

/** One bar of the Files added and deleted card. */
export interface PeriodChanges {
  /** `yyyy-mm-dd`, the day the period starts. The first and last are clipped to the window. */
  start: string
  added: number
  deleted: number
  /** Files git detected as moved, counted apart from adds and deletes. */
  renamed: number
}

/**
 * The Files added and deleted card (#214): the window's periods oldest first, with the true counts;
 * empty where there is no history.
 */
export interface OverviewFileChanges {
  period: ChangePeriod
  periods: PeriodChanges[]
}

/**
 * A folder of one repository and the commits in the window that touched it: a top-level folder, or a
 * child of the folder that holds nearly all of the repository, such as `src/Api` under `src`.
 */
export interface FolderCommits {
  folder: string
  commits: number
}

/** Two folders of one repository and the distinct commits that touched both. */
export interface FolderPair {
  first: string
  second: string
  commits: number
}

/** One repository's folder coupling: its busiest folders, capped, and the pairs among them. */
export interface RepositoryCoupling {
  repositorySlug: string
  commits: number
  folders: FolderCommits[]
  pairs: FolderPair[]
}

/**
 * The Folders that change together card (#213). Repositories come most commits first. Commits that
 * touched more than `maxCommitPaths` paths are left out of the pairing and counted in
 * `ceilingExcluded`.
 */
export interface OverviewFolderCoupling {
  repositories: RepositoryCoupling[]
  maxCommitPaths: number
  ceilingExcluded: number
}

/** The overview page's setting, read and written whole. */
export interface ExcludedPathsBody {
  patterns: string[]
}

/** One pattern the Suggest button proposes (#217): the rule behind it, why, and the files at HEAD it matches. */
export interface ExcludedPathSuggestion {
  pattern: string
  rule: 'GitAttributes' | 'WellKnownName' | 'History'
  reason: string
  files: number
}

/** The proposals, or the sentence saying why a project with no index has none. */
export interface ExcludedPathSuggestionsDetail {
  suggestions: ExcludedPathSuggestion[]
  unavailable: string | null
}

export function fetchProjects() {
  return http.get('projects').json<ProjectSummary[]>()
}

export function fetchProject(slug: string) {
  return http.get(`projects/${slug}`).json<ProjectDetail>()
}

/** Every field of the page's URL is a query parameter of the read, so a view is one request. */
export function fetchProjectOverview(slug: string, parameters: OverviewParameters) {
  const search: Record<string, string> = {}
  if (parameters.days !== undefined) search.days = String(parameters.days)
  if (parameters.showExcluded) search.showExcluded = 'true'
  return http
    .get(`projects/${slug}/overview`, { searchParams: scoped(search, parameters.repository) })
    .json<ProjectOverviewDetail>()
}

export function fetchExcludedPaths(slug: string) {
  return http.get(`projects/${slug}/excluded-paths`).json<ExcludedPathsBody>()
}

/** Proposals only: nothing is stored until the form's own save. */
export function fetchExcludedPathSuggestions(slug: string) {
  return http
    .get(`projects/${slug}/excluded-paths/suggestions`)
    .json<ExcludedPathSuggestionsDetail>()
}

/** Answers the list as the server stored it: trimmed, with blanks and repeats dropped. */
export function saveExcludedPaths(slug: string, patterns: string[]) {
  return http
    .put(`projects/${slug}/excluded-paths`, { json: { patterns } })
    .json<ExcludedPathsBody>()
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
