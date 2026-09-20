import type { CommitRef } from '@/features/history/api'
import { http, scoped } from '@/lib/http'

/** What the browse, tree and file views read. Each shape mirrors a record in the C# host. */

/** One file of a listing, as the browse view shows it. */
export interface FileListEntry {
  qualifiedPath: string
  repositorySlug: string
  lineCount: number
  sizeBytes: number
  skipReason: string | null
}

/**
 * `total` counts every match; `files` holds one page of them. `pageSize` is the server's answer and
 * not the row count, because the last page is short and dividing by it would lose a page.
 */
export interface FileList {
  total: number
  page: number
  pageSize: number
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
 * How much of a file's declarations the server was in a position to read, which is what keeps an
 * empty list from reading as "this file declares nothing":
 *
 * - `unprofiled` — no profile covers the extension, so it was read with the conservative default
 *   shapes. A list may still come back, thinner than a covered language's would be.
 * - `unreadable` — the language is covered and its declarations cannot be read from a line (CSS).
 *   Nothing was scanned, which is not the same as scanning and finding nothing.
 * - `read` — the language is covered and its declaration shapes were read.
 *
 * One field rather than two flags, because only these three of four combinations are reachable and a
 * fourth would be a state the panel could render the wrong sentence for.
 */
export type DeclarationCoverage = 'unprofiled' | 'unreadable' | 'read'

/** What a file declares. `capped` says the list is short of what the file declares. */
export interface FileDeclarations {
  qualifiedPath: string
  languageName: string
  coverage: DeclarationCoverage
  capped: boolean
  declarations: Declaration[]
}

export function fetchBrowse(project: string, glob: string, page: number, repository?: string) {
  return http
    .get(`projects/${project}/files`, {
      searchParams: scoped({ glob, page: String(page) }, repository),
    })
    .json<FileList>()
}

export function fetchTree(project: string, path: string) {
  return http.get(`projects/${project}/tree`, { searchParams: { path } }).json<TreeLevel>()
}

export function fetchFile(project: string, path: string) {
  return http.get(`projects/${project}/file`, { searchParams: { path } }).json<FileContent>()
}

export function fetchBlame(project: string, path: string) {
  return http.get(`projects/${project}/file/blame`, { searchParams: { path } }).json<Blame>()
}

export function fetchImports(project: string, path: string) {
  return http
    .get(`projects/${project}/file/imports`, { searchParams: { path } })
    .json<FileImports>()
}

/**
 * The direction a codebase cannot be read for: every import line in the project, resolved at index
 * time and looked up backwards.
 */
export function fetchDependents(project: string, path: string) {
  return http
    .get(`projects/${project}/file/dependents`, { searchParams: { path } })
    .json<FileDependents>()
}

export function fetchDeclarations(project: string, path: string) {
  return http
    .get(`projects/${project}/file/declarations`, { searchParams: { path } })
    .json<FileDeclarations>()
}
