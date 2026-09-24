import { useQuery, useSuspenseQuery } from '@tanstack/react-query'
import { useNavigate, useParams, useSearch } from '@tanstack/react-router'
import { commitFilesPage } from '@/features/history/commitFilesPage'
import { commitFilesQuery, commitQuery } from '@/features/history/queries'
import { DiffStat } from '@/components/DiffStat'
import { ErrorPanel } from '@/components/ErrorPanel'
import { FilePathLink } from '@/components/FilePathLink'
import { PageCard } from '@/components/PageCard'
import { Pager } from '@/components/Pager'
import { Badge } from '@/components/ui/badge'
import { Skeleton } from '@/components/ui/skeleton'
import { formatCount, formatDate, shortSha } from '@/lib/format'

/**
 * One commit, by SHA. It exists because a commit was the one thing the UI named everywhere and could
 * open nowhere: History expanded a row in place, the file page printed `last changed <sha>` as dead
 * text, and the rail's recent commits were text too. Now all of them link here.
 *
 * What it answers is what the change log's row used to open to, plus the things a row had no room
 * for: the full id, the author's address, the repository the commit belongs to. The file list is a
 * second request, so a commit touching hundreds of paths draws its message at once, and it is drawn
 * a page at a time (`commitFilesPage`), because a commit can touch tens of thousands.
 */
export function CommitPage() {
  const { project } = useParams({ from: '/projects/$project/commit' })
  const { sha, from, page } = useSearch({ from: '/projects/$project/commit' })
  const navigate = useNavigate()
  const { data: commit } = useSuspenseQuery(commitQuery(project, sha))
  const files = useQuery(commitFilesQuery(project, sha))
  const shown = files.data ? commitFilesPage(files.data.files, page ?? 1) : undefined

  return (
    <PageCard
      title={shortSha(commit.sha)}
      hint={commit.subject}
      badges={
        <div className="flex flex-wrap items-center gap-2">
          <Badge variant="outline">
            <span className="font-mono">{commit.repositorySlug}</span>
          </Badge>
          <span className="text-xs text-muted-foreground">
            {commit.authorName} <span title={commit.authorEmail}>&lt;{commit.authorEmail}&gt;</span>{' '}
            · {formatDate(commit.authoredAt)}
          </span>
          <span className="text-xs tabular-nums">
            <DiffStat added={commit.added} deleted={commit.deleted} /> in{' '}
            {formatCount(commit.filesChanged)} {commit.filesChanged === 1 ? 'file' : 'files'}
          </span>
        </div>
      }
    >
      <div className="space-y-4">
        {/* The full id, because this is the page that has room for it and the one place a reader
            comes to copy it — everywhere else in the app abbreviates. */}
        <p className="font-mono text-xs break-all text-muted-foreground">{commit.sha}</p>

        {commit.body ? (
          <pre className="max-w-prose font-sans text-sm whitespace-pre-wrap text-muted-foreground">
            {commit.body}
          </pre>
        ) : null}

        {files.error ? (
          <ErrorPanel error={files.error} title="The files this commit touched could not be read" />
        ) : shown ? (
          <div className="space-y-3">
            {/* Which rows these are, as the file listing says it: a page in the middle of a wide
                commit is otherwise indistinguishable from the whole of a narrow one. */}
            {shown.lastPage > 1 ? (
              <p className="text-sm text-muted-foreground">
                Showing {formatCount(shown.first)}–
                {formatCount(shown.first + shown.rows.length - 1)} of {formatCount(shown.total)}{' '}
                files
              </p>
            ) : null}
            <ul className="space-y-0.5 font-mono text-xs">
              {shown.rows.map((file) => (
                <li key={file.path} className="flex items-baseline gap-3">
                  <span className="w-16 shrink-0 text-muted-foreground">{file.changeKind}</span>
                  <span className="w-24 shrink-0 tabular-nums text-muted-foreground">
                    +{file.added} &minus;{file.deleted}
                  </span>
                  {/* Labelled with the path inside the repository: a commit's own file list is
                      already under one repository, so the slug would repeat on every row. The
                      origin carries this commit, so the file opens with
                      `… › History › <sha> › <file>` above it. */}
                  <FilePathLink
                    project={project}
                    qualifiedPath={file.qualifiedPath ?? file.path}
                    atHead={file.qualifiedPath !== null}
                    label={file.path}
                    origin={{ commit: commit.sha, view: from ?? 'history' }}
                  />
                </li>
              ))}
            </ul>
            {/* The page drawn rather than the one the URL asked for: a page past the end is shown
                as the last one, and the pager must offer to leave where the reader is. */}
            <Pager
              page={shown.page}
              lastPage={shown.lastPage}
              onPage={(next) =>
                void navigate({
                  params: { project },
                  search: { from, page: next === 1 ? undefined : next, sha },
                  to: '/projects/$project/commit',
                })
              }
            />
          </div>
        ) : (
          <Skeleton className="h-4 w-64" />
        )}
      </div>
    </PageCard>
  )
}
