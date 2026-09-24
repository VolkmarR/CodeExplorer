import { Link } from '@tanstack/react-router'
import { ChurnList } from '@/features/churn/ChurnList'
import type { IndexOverview } from '@/features/projects/api'
import { ExcludedNote } from '@/features/projects/ExcludedNote'
import { formatDate } from '@/lib/format'
import { CHURN_DEFAULTS, churnSearch } from '@/lib/urls/churnParams'
import { NO_HISTORY } from '@/features/projects/noHistory'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

/**
 * The same ranking the churn page shows, over the filter bar's window and without the project's
 * excluded paths. The window ends at the newest commit in the index and not at today, so a stale
 * index shows as one (CONTEXT.md, Window).
 */
export function MostChangedCard({
  project,
  repository,
  overview,
  excluded,
}: {
  project: string
  /** The repository the page is narrowed to, carried into the link to the whole ranking. */
  repository: string | undefined
  overview: IndexOverview
  /** Files the window's commits touched that the excluded paths left out, if any were. */
  excluded: number | undefined
}) {
  const { churn } = overview
  // Both or neither, which is what the pair means: null together is "no history was imported", and
  // reading one of them alone would let an empty ranking pass for a quiet quarter.
  const window = churn.since && churn.until ? { since: churn.since, until: churn.until } : null
  return (
    <Card>
      <CardHeader>
        <CardTitle>Most changed</CardTitle>
      </CardHeader>
      <CardContent>
        {!window ? (
          <p className="text-sm text-muted-foreground">{NO_HISTORY}</p>
        ) : (
          <>
            <p className="pb-3 text-xs text-muted-foreground">
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
            </p>
            {/* The churn page's own list, so the same ranking reads the same in both places. */}
            <ChurnList project={project} files={churn.files} />
            <ExcludedNote project={project} files={excluded} what="this window" />
          </>
        )}
      </CardContent>
    </Card>
  )
}
