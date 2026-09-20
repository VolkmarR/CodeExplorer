import { http, scoped } from '@/lib/http'

/** The change log's shapes and calls. Each shape mirrors a record in the C# host; nothing is invented here. */

/**
 * One commit as the operator UI names it: enough to recognise it and to say who and when, never the
 * body. The same four fields whether it is a repository's newest, a file's first or last, or the
 * one a run of lines attributes to.
 *
 * It lives with the change log because that is the feature the concept belongs to, and the two
 * features that name a commit without being the change log — a repository's newest, a file's first
 * and last — import it from here rather than restating it.
 */
export interface CommitRef {
  sha: string
  authorName: string
  authoredAt: string
  subject: string
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

export function fetchCommits(project: string, page: number, repository?: string) {
  return http
    .get(`projects/${project}/commits`, {
      searchParams: scoped({ page: String(page) }, repository),
    })
    .json<CommitList>()
}

/**
 * One commit, for the page a link to a SHA opens. Its files are a second request, like the file
 * page's blame: the message and the sums are one row and draw at once, whatever the commit touched.
 */
export function fetchCommit(project: string, sha: string) {
  return http.get(`projects/${project}/commits/${sha}`).json<CommitEntry>()
}

export function fetchCommitFiles(project: string, sha: string) {
  return http.get(`projects/${project}/commits/${sha}/files`).json<CommitFiles>()
}
