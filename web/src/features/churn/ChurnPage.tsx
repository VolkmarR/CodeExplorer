import { useSuspenseQuery } from '@tanstack/react-query'
import { useNavigate, useParams, useSearch } from '@tanstack/react-router'
import { ChurnControls } from '@/features/churn/ChurnControls'
import { ChurnList } from '@/features/churn/ChurnList'
import { ChurnNotes } from '@/features/churn/ChurnNotes'
import { ChurnScopeTrail } from '@/features/churn/ChurnScopeTrail'
import { ExtensionFilter } from '@/features/churn/ExtensionFilter'
import { churnSearch, type ChurnParameters } from '@/lib/urls/churnParams'
import { churnQuery } from '@/features/churn/queries'
import { projectQuery } from '@/features/projects/queries'
import { PageCard } from '@/components/PageCard'
import { WindowNote } from '@/components/WindowNote'
import { formatDate } from '@/lib/format'

/**
 * Which files a project is moving, over a window. It is its own view rather than a panel beside the
 * change log, because the two answer different questions: the change log is what happened, in order,
 * and this is what has been worked on, ranked — and the ranking is worth a window of its own rather
 * than whichever span a page of fifty commits happens to cover.
 *
 * Every control here is a search param and nothing else, so a ranking can be pasted. That matters
 * most for what #161 added: an unfiltered ranking of a real project is mostly project files and
 * translations, so the ranking worth sending someone is a narrowed one — and a filter held in
 * component state would be the one part of it a link could not carry.
 */
export function ChurnPage() {
  const { project } = useParams({ from: '/projects/$project/churn' })
  const search = useSearch({ from: '/projects/$project/churn' })
  const navigate = useNavigate()
  const { data: detail } = useSuspenseQuery(projectQuery(project))
  const { data: ranking } = useSuspenseQuery(churnQuery(project, search))

  // What the rows ARE, off the answer rather than off the request: it is what the query grouped by,
  // and a depth the server clamped would have the two disagree.
  const rolledUp = ranking.depth !== null

  function show(change: Partial<ChurnParameters>) {
    void navigate({
      params: { project },
      search: churnSearch(search, change),
      to: '/projects/$project/churn',
    })
  }

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
        hint={`${rolledUp ? 'directories' : 'files'} by how much they changed, most commits first`}
        actions={
          <ChurnControls search={search} repositories={detail.repositories} onChange={show} />
        }
      >
        <div className="space-y-4">
          {/* Where the reader is, above everything that changes what is in it: drilling in is one
              click and this is the only way back out that keeps the window and the filter. */}
          <ChurnScopeTrail project={project} search={search} />

          {/* The filter, above the ranking it narrows, drawn from what this window holds. */}
          <ExtensionFilter
            extensions={ranking.extensions}
            value={search.extensions ?? ''}
            onChange={(extensions) => show({ extensions })}
          />

          {ranking.files.length > 0 ? (
            <ChurnList
              project={project}
              files={ranking.files}
              // A rolled-up row is a directory and narrows the ranking; a file row opens the file.
              // The repository is cleared with it because the qualified path carries one.
              drillInto={
                rolledUp
                  ? (directory) => churnSearch(search, { directory, repository: '' })
                  : undefined
              }
            />
          ) : (
            <p className="py-6 text-center text-sm text-muted-foreground">
              {emptyReason(search, ranking.since !== null, ranking.hidden)}
            </p>
          )}

          <ChurnNotes ranking={ranking} />
        </div>
      </PageCard>
    </>
  )
}

/**
 * Why a ranking came back empty, which after #161 is four different facts and four different next
 * steps. No window at all is a project whose history was never imported and no window will fix it;
 * a filter that hid everything sends the reader to the filter, where telling them to look further
 * back would be advice that cannot work; a scope with nothing in it sends them up the trail; and
 * only the last of the four is the plain "widen the window".
 */
function emptyReason(search: ChurnParameters, hasWindow: boolean, hidden: number): string {
  if (!hasWindow) {
    return 'Churn is read from imported history, which arrives with a refresh. If the project has been refreshed and this is still empty, its repositories’ history could not be walked.'
  }
  if (hidden > 0) {
    return `Every one of the ${hidden} paths that changed in this window is filtered out. Clear an extension, or widen the window.`
  }
  return search.directory === undefined
    ? 'No commit in this window changed a file. Widen the window to look further back.'
    : 'No commit in this window changed anything here. Widen the window, or step back up to a wider scope.'
}
