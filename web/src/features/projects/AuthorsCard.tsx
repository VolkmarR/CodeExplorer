import type { IndexOverview } from '@/features/projects/api'
import { ExcludedNote } from '@/features/projects/ExcludedNote'
import { formatCount, formatDate } from '@/lib/format'
import { NO_HISTORY } from '@/features/projects/noHistory'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

/**
 * Who has touched the project most, over the whole imported history rather than the churn window —
 * so the filter bar's window does not reach it, and the card says which span it covers.
 */
export function AuthorsCard({
  project,
  overview,
  excluded,
}: {
  project: string
  overview: IndexOverview
  /** Files any commit touched that the excluded paths left out, if any were. */
  excluded: number | undefined
}) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Most commits</CardTitle>
      </CardHeader>
      <CardContent className="space-y-1 text-sm">
        {overview.authors.length === 0 ? (
          <p className="text-sm text-muted-foreground">{NO_HISTORY}</p>
        ) : (
          <>
            {/* Said once, at the top, rather than per row: it is a caveat about the whole list, and
                attribution is who touched the code last and never who wrote it (CONTEXT.md). */}
            <p className="pb-2 text-xs text-muted-foreground">
              Over the whole imported history. Who to ask about a file, never who wrote it &mdash; a
              reformat is a change and it becomes the answer.
            </p>
            {overview.authors.map((author) => (
              <div key={author.email} className="flex min-w-0 items-baseline gap-3">
                <span className="w-10 shrink-0 text-right tabular-nums text-muted-foreground">
                  {formatCount(author.commits)}
                </span>
                <span className="truncate">
                  {author.name}{' '}
                  <span className="text-muted-foreground">&lt;{author.email}&gt;</span>
                </span>
                <span className="ml-auto shrink-0 text-xs text-muted-foreground">
                  last on {formatDate(author.lastCommit)}
                </span>
              </div>
            ))}
            {/* A commit is dropped only where every file it touched was excluded, so the count is
                of files and the rows above are what is left of each author. */}
            <ExcludedNote
              project={project}
              files={excluded}
              what="the history (a commit still counts while it touched one file shown)"
            />
          </>
        )}
      </CardContent>
    </Card>
  )
}
