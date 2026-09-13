import ky, { HTTPError } from 'ky'
import type { SearchParameters } from '@/features/search/searchParams'

/** The shapes `/api` answers with. Each mirrors a record in the C# host; nothing is invented here. */

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
}

export interface ProjectDetail {
  slug: string
  name: string
  singleRepository: boolean
  index: ProjectIndexStatus
  repositories: RepositoryDetail[]
}

export interface IndexSummary {
  repositories: number
  files: number
  lines: number
  skipped: string[]
}

export interface GrepLine {
  lineNumber: number
  text: string
  isMatch: boolean
}

export interface GrepFile {
  qualifiedPath: string
  matchCount: number
  matchesShown: number
  lines: GrepLine[]
}

export interface GrepResult {
  engine: string
  totalFiles: number
  totalLines: number
  page: number
  pageSize: number
  files: GrepFile[]
  filesMatchingWithoutFilters: number | null
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

/** One file of a listing, as the browse view shows it. */
export interface FileListEntry {
  qualifiedPath: string
  repositorySlug: string
  lineCount: number
  sizeBytes: number
  skipReason: string | null
}

/** <Total> counts every match; <files> holds the first page of them. */
export interface FileList {
  total: number
  files: FileListEntry[]
}

/**
 * One row of a tree listing. `files` is null for a file and counts everything beneath for a
 * directory, so it is what tells the two apart.
 */
export interface TreeEntry {
  name: string
  qualifiedPath: string
  files: number | null
  lines: number
  sizeBytes: number
  skipReason: string | null
}

/** One level of the tree. `path` is empty at the project root. */
export interface TreeLevel {
  path: string
  /**
   * Whether the entries are repositories rather than directories. An empty `path` no longer implies
   * it: a single-repository project's root is already inside its one repository (ADR-0006).
   */
  repositoryLevel: boolean
  entries: TreeEntry[]
}

export interface FileContent {
  qualifiedPath: string
  repositorySlug: string
  lineCount: number
  sizeBytes: number
  skipReason: string | null
  content: string
}

/**
 * A failed request reaches the UI as ky's own `HTTPError`, re-exported under the name the components
 * use. `beforeError` below has already replaced its message with the server's prose, so a component
 * renders `error.message` and says nothing of its own.
 */
export { HTTPError as ApiError } from 'ky'

const http = ky.create({
  hooks: {
    beforeError: [
      ({ error }) => {
        // A timeout or a dropped connection is not an HTTPError and has no body to read.
        if (!(error instanceof HTTPError)) return error

        // A semantic failure answers `{ error }` with prose naming what to try instead (an unknown
        // project, a pattern RE2 rejects, an index still to be built). ky's default message is the
        // status line, which would throw that away.
        //
        // Read `error.data`, never the response: ky parses the body into `data` before this hook
        // runs, which consumes it, so `response.clone()` here throws "Response body is already
        // used" and every server message becomes that TypeError instead.
        const body: unknown = error.data
        if (body && typeof body === 'object' && 'error' in body && typeof body.error === 'string') {
          error.message = body.error
        }
        return error
      },
    ],
  },
  prefix: '/api',
  // No retries: every failure this API produces is a decision it made about the request, not a
  // transient one, and repeating a rejected pattern only delays the explanation.
  retry: 0,
  // A first index build clones every repository of the project, which is minutes on a large one.
  timeout: 300_000,
})

export const api = {
  addRepository: (project: string, slug: string, url: string, credential: string | null) =>
    http
      .post(`projects/${project}/repositories`, { json: { slug, url, credential } })
      .json<CreatedRepository>(),

  createProject: (slug: string, name: string, singleRepository: boolean) =>
    http.post('projects', { json: { name, singleRepository, slug } }).json<CreatedProject>(),

  browse: (project: string, glob: string, repository?: string) =>
    http
      .get(`projects/${project}/files`, {
        searchParams: repository ? { glob, repository } : { glob },
      })
      .json<FileList>(),

  tree: (project: string, path: string) =>
    http.get(`projects/${project}/tree`, { searchParams: { path } }).json<TreeLevel>(),

  file: (project: string, path: string) =>
    http.get(`projects/${project}/file`, { searchParams: { path } }).json<FileContent>(),

  index: (project: string) => http.post(`projects/${project}/index`).json<IndexSummary>(),

  project: (slug: string) => http.get(`projects/${slug}`).json<ProjectDetail>(),

  projects: () => http.get('projects').json<ProjectSummary[]>(),

  removeProject: (slug: string) => http.delete(`projects/${slug}`).then(() => undefined),

  removeRepository: (project: string, slug: string) =>
    http.delete(`projects/${project}/repositories/${slug}`).then(() => undefined),

  search: (project: string, query: SearchParameters) => {
    const searchParams: Record<string, string> = {
      caseSensitive: String(query.caseSensitive),
      page: String(query.page),
      q: query.q,
      regex: String(query.regex),
    }
    if (query.extension) searchParams.extension = query.extension
    if (query.path) searchParams.path = query.path
    return http.get(`projects/${project}/search`, { searchParams }).json<GrepResult>()
  },
}
