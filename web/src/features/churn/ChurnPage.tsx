import { useSuspenseQuery } from '@tanstack/react-query'
import { useNavigate, useParams, useSearch } from '@tanstack/react-router'
import { ChurnList } from '@/features/churn/ChurnList'
import { CHURN_WINDOWS, churnSearch, describeWindow } from '@/lib/urls/churnParams'
import { churnQuery } from '@/features/churn/queries'
import { projectQuery } from '@/features/projects/queries'
import { RepositorySelect } from '@/features/projects/RepositorySelect'
import { PageCard } from '@/components/PageCard'
import { WindowNote } from '@/components/WindowNote'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { formatDate } from '@/lib/format'

/**
 * Which files a project is moving, over a window. It is its own view rather than a panel beside the
 * change log, because the two answer different questions: the change log is what happened, in order,
 * and this is what has been worked on, ranked — and the ranking is worth a window of its own rather
 * than whichever span a page of fifty commits happens to cover.
 *
 * The window and the repository come from the URL and nowhere else, so a ranking can be pasted.
 */
export function ChurnPage() {
  const { project } = useParams({ from: '/projects/$project/churn' })
  const search = useSearch({ from: '/projects/$project/churn' })
  const navigate = useNavigate()
  const { data: detail } = useSuspenseQuery(projectQuery(project))
  const { data: ranking } = useSuspenseQuery(churnQuery(project, search))

  // One repository needs no filter; the choice is offered only where there is one to make.
  const filterable = detail.repositories.length > 1

  return (
    <>
      {/* The dates, not just the window asked for: it ends at the newest commit the index holds
          rather than today, so an index nobody has refreshed shows as one. */}
      {ranking.since && ranking.until ? (
        <WindowNote>
          Counted over <b>{formatDate(ranking.since)}</b> to <b>{formatDate(ranking.until)}</b> —
          the window ends at the newest commit imported, not at today.
        </WindowNote>
      ) : null}

      <PageCard
        title="Churn"
        hint="files by how much they changed, most commits first"
        actions={
          <div className="flex flex-wrap items-center gap-3">
            <div className="flex items-center gap-2">
              <Label htmlFor="churn-window" className="text-xs text-muted-foreground">
                Window
              </Label>
              <Select
                value={String(search.days)}
                onValueChange={(next) =>
                  void navigate({
                    params: { project },
                    search: churnSearch(Number(next), search.repository),
                    to: '/projects/$project/churn',
                  })
                }
              >
                <SelectTrigger id="churn-window" className="w-36">
                  <SelectValue>{describeWindow(search.days)}</SelectValue>
                </SelectTrigger>
                <SelectContent>
                  {/* A hand-written `days` outside the offered set still shows as itself rather than
                      snapping the select to a value the URL does not carry. */}
                  {(CHURN_WINDOWS.includes(search.days as (typeof CHURN_WINDOWS)[number])
                    ? CHURN_WINDOWS
                    : [...CHURN_WINDOWS, search.days].toSorted((a, b) => a - b)
                  ).map((days) => (
                    <SelectItem key={days} value={String(days)}>
                      {describeWindow(days)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            {filterable ? (
              <div className="flex items-center gap-2">
                <Label htmlFor="churn-repository" className="text-xs text-muted-foreground">
                  Repository
                </Label>
                <div className="w-48">
                  <RepositorySelect
                    id="churn-repository"
                    repositories={detail.repositories}
                    value={search.repository ?? ''}
                    onChange={(repository) =>
                      void navigate({
                        params: { project },
                        search: churnSearch(search.days, repository),
                        to: '/projects/$project/churn',
                      })
                    }
                  />
                </div>
              </div>
            ) : null}
          </div>
        }
      >
        <div className="space-y-4">
          {ranking.files.length > 0 ? (
            <ChurnList project={project} files={ranking.files} />
          ) : (
            <p className="py-6 text-center text-sm text-muted-foreground">
              {ranking.since
                ? 'No commit in this window changed a file. Widen the window to look further back.'
                : 'Churn is read from imported history, which arrives with a refresh. If the project has been refreshed and this is still empty, its repositories’ history could not be walked.'}
            </p>
          )}

          {ranking.withoutHistory.length > 0 ? (
            <p className="text-xs text-muted-foreground">
              No history was imported for {ranking.withoutHistory.join(', ')}, so nothing from{' '}
              {ranking.withoutHistory.length === 1 ? 'it' : 'those'} can appear here however much{' '}
              {ranking.withoutHistory.length === 1 ? 'it' : 'they'} changed.
            </p>
          ) : null}
        </div>
      </PageCard>
    </>
  )
}
