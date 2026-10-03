import { Link } from '@tanstack/react-router'
import { ChurnList } from '@/features/churn/ChurnList'
import type { OverviewChurn } from '@/features/projects/api'
import { OverviewCard } from '@/features/projects/OverviewCard'
import { formatDate } from '@/lib/format'
import { CHURN_DEFAULTS, churnSearch } from '@/lib/urls/churnParams'
import { historyWindow } from '@/features/projects/noHistory'

/**
 * The same ranking the churn page shows, over the filter bar's window and without the project's
 * excluded paths. The window ends at the newest commit in the index and not at today, so a stale
 * index shows as one (CONTEXT.md, Window).
 */
export function MostChangedCard({
  project,
  repository,
  churn,
  excluded,
}: {
  project: string
  /** The repository the page is narrowed to, carried into the link to the whole ranking. */
  repository: string | undefined
  churn: OverviewChurn
  /** Files the window's commits touched that the excluded paths left out, if any were. */
  excluded: number | undefined
}) {
  // Read here as well as by the frame, which says there is no history where this is null, for the
  // dates the note names.
  const window = historyWindow(churn)
  return (
    <OverviewCard
      title="Most changed"
      history={churn}
      note={
        window ? (
          <>
            {formatDate(window.since)} to {formatDate(window.until)}, the {churn.days} days to the
            newest recorded commit.{' '}
            {/* The churn page knows nothing of the excluded paths, which are this page's setting,
                so its ranking is the whole of the window. */}
            <Link
              to="/projects/$project/churn"
              params={{ project }}
              search={churnSearch(CHURN_DEFAULTS, { days: churn.days, repository })}
              className="hover:text-primary hover:underline"
            >
              See the whole ranking
            </Link>
          </>
        ) : null
      }
      excluded={{ files: excluded, project, what: 'this window' }}
    >
      {churn.files.length === 0 ? (
        // Never "nothing changed" where the excluded paths took the rows: the window has commits by
        // construction, and a filtered list must not read as a quiet one.
        <p className="text-sm text-muted-foreground">
          {excluded
            ? 'Every file that changed in this window is left out by the excluded paths.'
            : 'No file changed in this window.'}
        </p>
      ) : (
        // The churn page's own list, so the same ranking reads the same in both places.
        <ChurnList project={project} files={churn.files} />
      )}
    </OverviewCard>
  )
}
