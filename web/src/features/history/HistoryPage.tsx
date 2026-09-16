import { useSuspenseQuery } from '@tanstack/react-query'
import { useNavigate, useParams, useSearch } from '@tanstack/react-router'
import { CommitList } from '@/features/history/CommitList'
import { commitsQuery } from '@/features/history/queries'
import { projectQuery } from '@/features/projects/queries'
import { RepositorySelect } from '@/features/projects/RepositorySelect'
import { Label } from '@/components/ui/label'
import { formatCount } from '@/lib/format'

/**
 * A project's change log: the commits of every repository's default branch, newest first, a page at
 * a time. The page and the repository come from the URL and nowhere else, so a page of history can be
 * pasted to a colleague and opens as the same page.
 *
 * What it is not is a complete history, and the empty state says so rather than "nothing changed":
 * history arrives with a refresh and may not reach the beginning of the repository (CONTEXT.md).
 */
export function HistoryPage() {
  const { project } = useParams({ from: '/projects/$project/history' })
  const search = useSearch({ from: '/projects/$project/history' })
  const navigate = useNavigate()
  const { data: detail } = useSuspenseQuery(projectQuery(project))
  const { data: log } = useSuspenseQuery(commitsQuery(project, search))

  // One repository needs no filter; the choice is offered only where there is one to make.
  const filterable = detail.repositories.length > 1

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <p className="text-sm text-muted-foreground">
          {log.total === 0
            ? 'No history is recorded yet.'
            : `${formatCount(log.total)} ${log.total === 1 ? 'commit' : 'commits'} on the default branch, newest first.`}
        </p>
        {filterable ? (
          <div className="flex items-center gap-2">
            <Label htmlFor="history-repository" className="text-muted-foreground">
              Repository
            </Label>
            <div className="w-56">
              <RepositorySelect
                id="history-repository"
                repositories={detail.repositories}
                value={search.repository ?? ''}
                onChange={(repository) =>
                  // Back to the first page: page 7 of one repository is not page 7 of another.
                  void navigate({
                    params: { project },
                    search: { page: 1, repository: repository === '' ? undefined : repository },
                    to: '/projects/$project/history',
                  })
                }
              />
            </div>
          </div>
        ) : null}
      </div>

      {log.total === 0 ? (
        <p className="rounded-lg border bg-card px-4 py-3 text-sm text-muted-foreground">
          History is imported by a refresh. If the project has been refreshed and this is still
          empty, its repositories&apos; history could not be walked.
        </p>
      ) : (
        <CommitList
          project={project}
          log={log}
          search={search}
          showRepository={filterable && search.repository === undefined}
        />
      )}
    </div>
  )
}
