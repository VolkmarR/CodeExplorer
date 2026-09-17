import { Link } from '@tanstack/react-router'
import { DiffStat } from '@/components/DiffStat'
import { FilePathLink } from '@/components/FilePathLink'
import type { IndexOverview } from '@/lib/api'
import { formatCount, formatDate } from '@/lib/format'
import { NO_HISTORY } from '@/features/projects/noHistory'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

/**
 * The same ranking the churn page shows, over the window the build took it at. The window ends at the
 * newest commit in the index and not at today, so a stale index shows as one (CONTEXT.md, Window).
 */
export function MostChangedCard({
  project,
  overview,
}: {
  project: string
  overview: IndexOverview
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
              <Link
                to="/projects/$project/churn"
                params={{ project }}
                search={{ days: churn.days }}
                className="hover:text-primary hover:underline"
              >
                See the whole ranking
              </Link>
            </p>
            <ol className="space-y-1 font-mono text-xs">
              {churn.files.map((file) => (
                <li key={file.qualifiedPath} className="flex min-w-0 items-baseline gap-3">
                  <span className="w-10 shrink-0 text-right tabular-nums text-muted-foreground">
                    {formatCount(file.commits)}&times;
                  </span>
                  <span className="w-24 shrink-0 tabular-nums">
                    <DiffStat added={file.added} deleted={file.deleted} />
                  </span>
                  <FilePathLink
                    project={project}
                    qualifiedPath={file.qualifiedPath}
                    atHead={file.atHead}
                  />
                </li>
              ))}
            </ol>
          </>
        )}
      </CardContent>
    </Card>
  )
}
