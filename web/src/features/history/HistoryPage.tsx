import { useSuspenseQuery } from '@tanstack/react-query'
import { useNavigate, useParams, useSearch } from '@tanstack/react-router'
import { CommitList } from '@/features/history/CommitList'
import { newestImportedAt } from '@/features/history/historyWindow'
import { commitsQuery } from '@/features/history/queries'
import { historySearch } from '@/lib/urls/historyParams'
import { projectQuery } from '@/features/projects/queries'
import { RepositoryFilter } from '@/features/projects/RepositoryFilter'
import { PageCard } from '@/components/PageCard'
import { WindowNote } from '@/components/WindowNote'
import { formatCountOf, formatDate } from '@/lib/format'

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

  // The filter and the rows' repository only where there is more than one. Checked here as well as
  // in the filter, because the card draws its actions slot for anything it is handed.
  const several = detail.repositories.length > 1
  const scoped = search.repository
    ? detail.repositories.filter((r) => r.slug === search.repository)
    : detail.repositories
  const newest = newestImportedAt(scoped)
  // The page's own span, oldest to newest, which is what the rows below actually cover.
  const page = log.commits.at(-1)?.authoredAt

  return (
    <>
      {newest ? (
        <WindowNote>
          History is counted from the newest commit imported — <b>{formatDate(newest)}</b> — and not
          from today.
          {page
            ? ` This page covers ${formatDate(page)} to ${formatDate(log.commits[0].authoredAt)}.`
            : ''}
        </WindowNote>
      ) : null}

      <PageCard
        title="History"
        hint={
          log.total === 0
            ? 'no history is recorded yet'
            : `${formatCountOf(log.total, 'commit')} on the default branch, newest first`
        }
        actions={
          several ? (
            <RepositoryFilter
              id="history-repository"
              repositories={detail.repositories}
              value={search.repository ?? ''}
              onChange={(repository) =>
                void navigate({
                  params: { project },
                  search: historySearch(repository),
                  to: '/projects/$project/history',
                })
              }
            />
          ) : null
        }
      >
        {log.total === 0 ? (
          <p className="py-6 text-center text-sm text-muted-foreground">
            History is imported by a refresh. If the project has been refreshed and this is still
            empty, its repositories&apos; history could not be walked.
          </p>
        ) : (
          <CommitList
            project={project}
            log={log}
            showRepository={several && search.repository === undefined}
          />
        )}
      </PageCard>
    </>
  )
}
