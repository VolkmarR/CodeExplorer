import ky, { HTTPError } from 'ky'
import type { SearchParameters } from '@/features/search/searchParams'

/** The shapes `/api` answers with. Each mirrors a record in the C# host; nothing is invented here. */

/**
 * Whether this server has a tenant at all, and who is signed in to it. Two questions and not one: a
 * development server is deliberately unauthenticated (ADR-0004), so a UI reading only `signedIn`
 * would offer every developer a sign-in that goes nowhere.
 *
 * There is no token here, and that is the design rather than an omission: the browser holds a cookie
 * this server issued and never sees a token at all.
 */
export interface AuthStatus {
  enabled: boolean
  signedIn: boolean
  name: string | null
}

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

/**
 * One commit as the operator UI names it: enough to recognise it and to say who and when, never the
 * body. The same four fields whether it is a repository's newest, a file's first or last, or the
 * one a run of lines attributes to.
 */
export interface CommitRef {
  sha: string
  authorName: string
  authoredAt: string
  subject: string
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

export interface IndexSummary {
  repositories: number
  files: number
  lines: number
  skipped: string[]
}

/** The states the server names, serialised as words rather than ordinals so this union is stable. */
export type RefreshState = 'NeverRun' | 'Queued' | 'Running' | 'Succeeded' | 'Failed'

/**
 * Where a refresh stands. It is in-memory on the server, so a replica that scaled to zero comes back
 * saying `NeverRun` even for a project with an index; when that index was built is on the project.
 */
/**
 * How far a running refresh has got. `step` of `totalSteps` is always there; `done` and `total`
 * only for the steps that can count their work, and `total` alone may be null for a step that knows
 * how far it has got but not how far it is going — the commit walk.
 */
export interface RefreshProgress {
  step: number
  totalSteps: number
  phase: string
  done: number | null
  total: number | null
}

export interface RefreshStatus {
  project: string
  state: RefreshState
  phase: string
  startedAt: string | null
  finishedAt: string | null
  summary: IndexSummary | null
  error: string | null
  /** Set while the refresh runs; null before it starts and after it ends. */
  progress: RefreshProgress | null
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
  /**
   * The commits this file was first and last changed by. Both null where the repository has no
   * history in the index, which is a different answer from a file nobody changed and is shown as one.
   */
  firstCommit: CommitRef | null
  lastCommit: CommitRef | null
}

/**
 * One run of consecutive lines last changed by the same commit. `by` is null for lines the history
 * could not attribute — a file a merge brought in from a branch the walk did not follow, say.
 */
export interface BlameRun {
  startLine: number
  endLine: number
  by: CommitRef | null
}

/** A file's attribution as runs. A file without history answers with no runs, not with an error. */
export interface Blame {
  qualifiedPath: string
  runs: BlameRun[]
}

/**
 * One name a file imports. `targetPath` and `unresolved` are exclusive: the first is a qualified
 * path the file view opens, the second the server's prose saying why there is none. The `name` is
 * there either way — an edge shown only when it resolves would read as a dependency the file does
 * not have.
 */
export interface ImportEdge {
  name: string
  lineNumber: number
  targetPath: string | null
  unresolved: string | null
}

/**
 * What a file imports. `profiled` and `hasImports` are why an empty list is not the sentence "this
 * file imports nothing": an extension no profile covers was never read for imports, and a language
 * with no import concept has none to read. `capped` says the list stopped at the server's ceiling.
 */
export interface FileImports {
  qualifiedPath: string
  languageName: string
  profiled: boolean
  hasImports: boolean
  module: string | null
  capped: boolean
  imports: ImportEdge[]
}

/** One file that imports the file being looked at, and the line that does it. */
export interface Dependent {
  qualifiedPath: string
  name: string
  lineNumber: number
}

/**
 * What imports a file. `shareTheModule` and `unplaced` are why an empty list is not "nothing depends
 * on this": a module several files declare resolves to none of them, and an unresolved edge spelling
 * this file's name may be a dependency the index could not place.
 */
export interface FileDependents {
  qualifiedPath: string
  module: string | null
  shareTheModule: number
  unplaced: number
  capped: boolean
  dependents: Dependent[]
}

/**
 * One name a file introduces. `type` and `member` are what the line declares — either may be null,
 * and a line that reads as both fills both — and `text` is the line itself, which is what the panel
 * shows. `role` is which side of a declaration/implementation split the line sits on, for the
 * languages that have one, and null otherwise. `evidence` is how the reading was reached, so a list
 * read from line shape cannot read like one a parser produced.
 */
export interface Declaration {
  lineNumber: number
  text: string
  type: string | null
  member: string | null
  role: 'declaration' | 'implementation' | null
  evidence: 'text' | 'parsed'
}

/**
 * What a file declares. `profiled` and `readsDeclarations` are why an empty list is not the sentence "this file
 * declares nothing": an extension no profile covers was read with the conservative default shapes,
 * and a language whose declarations this cannot read was never scanned. `capped` says the list is
 * short of what the file declares.
 */
export interface FileDeclarations {
  qualifiedPath: string
  languageName: string
  profiled: boolean
  readsDeclarations: boolean
  capped: boolean
  declarations: Declaration[]
}

/** One commit of the change log: who, when, what it said, and what it did to the tree in sums. */
export interface CommitEntry extends CommitRef {
  repositorySlug: string
  authorEmail: string
  body: string
  filesChanged: number
  added: number
  deleted: number
}

/** A page of the change log. `total` is zero for a project or repository without history. */
export interface CommitList {
  total: number
  page: number
  pageSize: number
  commits: CommitEntry[]
}

/** One path a commit touched. `qualifiedPath` links to the file while it is still at HEAD. */
export interface CommitFile {
  path: string
  changeKind: string
  added: number
  deleted: number
  qualifiedPath: string | null
}

export interface CommitFiles {
  sha: string
  files: CommitFile[]
}

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

/**
 * A failed request reaches the UI as ky's own `HTTPError`, re-exported under the name the components
 * use. `beforeError` below has already replaced its message with the server's prose, so a component
 * renders `error.message` and says nothing of its own.
 */
export { HTTPError as ApiError } from 'ky'

/**
 * Sign-in and sign-out are navigations and not calls, so they are URLs the browser goes to rather
 * than methods on the client below. They have to be: the server answers each with a redirect to the
 * tenant, and only the address bar can follow one cross-origin.
 */
export function signInHref(returnTo: string) {
  return `/api/auth/signin?returnUrl=${encodeURIComponent(returnTo)}`
}

/**
 * A form action and not an href: the server takes sign-out as a POST, so that a cross-site page
 * cannot force one and the cookie's SameSite=Lax is what stops it. Submitted, never linked.
 */
export const signOutAction = '/api/auth/signout'

const http = ky.create({
  hooks: {
    afterResponse: [
      ({ request, response }) => {
        // The cookie expired while the page was open. The server refuses in prose rather than
        // redirecting, because a cross-origin 302 to the tenant would fail CORS and arrive here as a
        // network error — so the navigation that fixes it has to be made from this side.
        //
        // `/api/auth` is exempt: it is anonymous and never 401s, and a loop through the sign-in
        // endpoint is the one failure this hook could cause.
        if (response.status === 401 && !request.url.includes('/api/auth/')) {
          globalThis.location.assign(
            signInHref(globalThis.location.pathname + globalThis.location.search),
          )
        }
        return response
      },
    ],
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
  // No request here waits on a rebuild any more — a refresh answers as soon as it is queued and the
  // progress is polled — but a search over a large project is still seconds rather than milliseconds.
  timeout: 30_000,
})

/**
 * Adds the repository to a request that has one, and leaves it off entirely when there is none:
 * the server reads a blank `repository` as a request to scope to one named "".
 */
function scoped(params: Record<string, string>, repository?: string): Record<string, string> {
  return repository ? { ...params, repository } : params
}

export const api = {
  /** Anonymous, and mapped whether or not there is a tenant: it is what says which of those it is. */
  auth: () => http.get('auth/me').json<AuthStatus>(),

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

  blame: (project: string, path: string) =>
    http.get(`projects/${project}/file/blame`, { searchParams: { path } }).json<Blame>(),

  imports: (project: string, path: string) =>
    http.get(`projects/${project}/file/imports`, { searchParams: { path } }).json<FileImports>(),

  // The direction a codebase cannot be read for: every import line in the project, resolved at index
  // time and looked up backwards.
  dependents: (project: string, path: string) =>
    http
      .get(`projects/${project}/file/dependents`, { searchParams: { path } })
      .json<FileDependents>(),

  declarations: (project: string, path: string) =>
    http
      .get(`projects/${project}/file/declarations`, { searchParams: { path } })
      .json<FileDeclarations>(),

  commits: (project: string, page: number, repository?: string) =>
    http
      .get(`projects/${project}/commits`, {
        searchParams: scoped({ page: String(page) }, repository),
      })
      .json<CommitList>(),

  // Days rather than a pair of dates: the window ends at the newest commit the index holds, and only
  // the index knows where that is — a client sending dates would be guessing at it.
  churn: (project: string, days: number, repository?: string) =>
    http
      .get(`projects/${project}/churn`, {
        searchParams: scoped({ days: String(days) }, repository),
      })
      .json<Churn>(),

  commitFiles: (project: string, sha: string) =>
    http.get(`projects/${project}/commits/${sha}/files`).json<CommitFiles>(),

  refresh: (project: string) => http.post(`projects/${project}/refresh`).json<RefreshStatus>(),

  refreshStatus: (project: string) => http.get(`projects/${project}/refresh`).json<RefreshStatus>(),

  project: (slug: string) => http.get(`projects/${slug}`).json<ProjectDetail>(),

  projectOverview: (slug: string) =>
    http.get(`projects/${slug}/overview`).json<ProjectOverviewDetail>(),

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
