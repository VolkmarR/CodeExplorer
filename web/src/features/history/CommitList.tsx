import { useQuery } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { ChevronRight } from 'lucide-react'
import { useState } from 'react'
import type { HistoryParameters } from '@/features/history/historyParams'
import { commitFilesQuery } from '@/features/history/queries'
import type { CommitEntry, CommitList as CommitPage } from '@/lib/api'
import { DiffStat } from '@/components/DiffStat'
import { ErrorPanel } from '@/components/ErrorPanel'
import { FilePathLink } from '@/components/FilePathLink'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { formatCount, formatDate, shortSha } from '@/lib/format'
import { cn } from '@/lib/utils'

/**
 * A page of commits, each a row that opens to its message body and the files it touched. The files
 * are fetched when a row is opened and not with the page: fifty commits touching a few hundred paths
 * each would be mostly paths nobody looks at.
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

      {lastPage > 1 ? (
        <div className="flex items-center justify-center gap-4">
          <Button
            variant="outline"
            size="sm"
            disabled={search.page <= 1}
            onClick={() =>
              void navigate({
                params: { project },
                search: { ...search, page: search.page - 1 },
                to: '/projects/$project/history',
              })
            }
          >
            Newer
          </Button>
          <span className="text-sm text-muted-foreground">
            Page {search.page} of {lastPage}
          </span>
          <Button
            variant="outline"
            size="sm"
            disabled={search.page >= lastPage}
            onClick={() =>
              void navigate({
                params: { project },
                search: { ...search, page: search.page + 1 },
                to: '/projects/$project/history',
              })
            }
          >
            Older
          </Button>
        </div>
      ) : null}
    </div>
  )
}

/**
 * One commit. Closed, it is the line `git log --oneline` would print plus who and when; open, the
 * body and the files. The sums are shown closed too, because "+2 −1 in 1 file" and "+1,400 −1,380 in
 * 90 files" are different kinds of commit and the subject rarely says which.
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
  const [open, setOpen] = useState(false)
  const files = useQuery({ ...commitFilesQuery(project, commit.sha), enabled: open })

  return (
    <li className="px-4 py-3">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen(!open)}
        className="group flex w-full items-start gap-3 text-left"
      >
        <ChevronRight
          className={cn(
            'mt-1 size-4 shrink-0 text-muted-foreground transition-transform',
            open && 'rotate-90',
          )}
          aria-hidden="true"
        />
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
      </button>

      {open ? (
        <div className="mt-3 ml-7 space-y-3">
          {commit.body ? (
            <pre className="max-w-prose font-sans text-sm whitespace-pre-wrap text-muted-foreground">
              {commit.body}
            </pre>
          ) : null}
          {files.error ? <ErrorPanel error={files.error} /> : null}
          {files.data ? (
            <ul className="space-y-0.5 font-mono text-xs">
              {files.data.files.map((file) => (
                <li key={file.path} className="flex items-baseline gap-3">
                  <span className="w-16 shrink-0 text-muted-foreground">{file.changeKind}</span>
                  <span className="w-24 shrink-0 tabular-nums text-muted-foreground">
                    +{file.added} −{file.deleted}
                  </span>
                  {/* Labelled with the path inside the repository: a commit's own file list is
                      already under one repository, so the slug would repeat on every row. */}
                  <FilePathLink
                    project={project}
                    qualifiedPath={file.qualifiedPath ?? file.path}
                    atHead={file.qualifiedPath !== null}
                    label={file.path}
                  />
                </li>
              ))}
            </ul>
          ) : files.isPending && !files.error ? (
            <Skeleton className="h-4 w-64" />
          ) : null}
        </div>
      ) : null}
    </li>
  )
}
