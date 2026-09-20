import { Link, useNavigate } from '@tanstack/react-router'
import { ChevronRight } from 'lucide-react'
import { commitSearch } from '@/lib/urls/commitParams'
import type { HistoryParameters } from '@/lib/urls/historyParams'
import type { CommitEntry, CommitList as CommitPage } from '@/lib/api'
import { DiffStat } from '@/components/DiffStat'
import { Pager } from '@/components/Pager'
import { Badge } from '@/components/ui/badge'
import { formatCount, formatDate, shortSha } from '@/lib/format'

/**
 * A page of commits, each a row that opens the commit's own page. The row used to expand in place
 * and fetch the files under it; the page shows the same body and file list with room for the rest of
 * what a commit is, and two renderings of one commit would drift.
 */
export function CommitList({
  project,
  log,
  search,
  showRepository,
}: {
  project: string
  log: CommitPage
  search: HistoryParameters
  /** Whether rows name their repository: only when the page mixes more than one. */
  showRepository: boolean
}) {
  const navigate = useNavigate()
  const lastPage = Math.max(1, Math.ceil(log.total / log.pageSize))

  return (
    <div className="space-y-4">
      <ul className="divide-y rounded-lg border bg-card">
        {log.commits.map((commit) => (
          <CommitRow
            key={commit.sha}
            project={project}
            commit={commit}
            showRepository={showRepository}
          />
        ))}
      </ul>

      {/* Newer and older rather than previous and next: a page of a log is a stretch of time, and
          which way is "back" in one is the opposite of what a reader would guess. */}
      <Pager
        page={search.page}
        lastPage={lastPage}
        previousLabel="Newer"
        nextLabel="Older"
        onPage={(page) =>
          void navigate({
            params: { project },
            search: { ...search, page },
            to: '/projects/$project/history',
          })
        }
      />
    </div>
  )
}

/**
 * One commit: the line `git log --oneline` would print plus who, when, and what it did to the tree
 * in sums — "+2 −1 in 1 file" and "+1,400 −1,380 in 90 files" are different kinds of commit and the
 * subject rarely says which.
 *
 * The whole row is the link to the commit's page. It expanded in place before, which meant the body
 * and the file list were drawn here as well as there; the chevron stays because it is what says the
 * row leads somewhere, and now it points at a page rather than at a fold.
 */
function CommitRow({
  project,
  commit,
  showRepository,
}: {
  project: string
  commit: CommitEntry
  showRepository: boolean
}) {
  return (
    <li>
      <Link
        to="/projects/$project/commit"
        params={{ project }}
        search={commitSearch(commit.sha, 'history')}
        className="group flex w-full items-start gap-3 px-4 py-3 text-left hover:bg-muted/40"
      >
        <ChevronRight className="mt-1 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
        <span className="flex min-w-0 flex-1 flex-col gap-1">
          <span className="flex min-w-0 items-baseline gap-2">
            {showRepository ? (
              <Badge variant="outline" className="shrink-0 font-mono">
                {commit.repositorySlug}
              </Badge>
            ) : null}
            <span className="truncate text-sm group-hover:underline" title={commit.subject}>
              {commit.subject}
            </span>
          </span>
          <span className="flex flex-wrap items-baseline gap-x-3 text-xs text-muted-foreground">
            <span className="font-mono" title={commit.sha}>
              {shortSha(commit.sha)}
            </span>
            <span>{formatDate(commit.authoredAt)}</span>
            <span title={commit.authorEmail}>{commit.authorName}</span>
            <span className="tabular-nums">
              <DiffStat added={commit.added} deleted={commit.deleted} /> in{' '}
              {formatCount(commit.filesChanged)} {commit.filesChanged === 1 ? 'file' : 'files'}
            </span>
          </span>
        </span>
      </Link>
    </li>
  )
}
