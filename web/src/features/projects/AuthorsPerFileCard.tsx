import type { OverviewAuthorsPerFile, OverviewChurn } from '@/features/projects/api'
import { AUTHOR_SEGMENTS, AuthorSplit } from '@/features/projects/AuthorSplit'
import { ExcludedNote } from '@/features/projects/ExcludedNote'
import { FilePathLink } from '@/components/FilePathLink'
import { formatCount } from '@/lib/format'
import { NO_HISTORY } from '@/features/projects/noHistory'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

const LEGEND = ['first author', 'second', 'third'] as const

/**
 * The files at HEAD changed by the most different people over the whole imported history (#212), each
 * with its commits split by author. The filter bar's window does not reach it, so the card says which
 * span it covers; whether there is history at all is read off Most changed's section, as the hotspots
 * read it.
 */
export function AuthorsPerFileCard({
  project,
  churn,
  authors,
}: {
  project: string
  /** Most changed's section, for whether there was history at all. */
  churn: OverviewChurn
  authors: OverviewAuthorsPerFile
}) {
  return (
    <Card>
      <CardHeader className="flex flex-row items-baseline justify-between">
        <CardTitle>Most authors per file</CardTitle>
        <span className="text-xs text-muted-foreground">whole imported history</span>
      </CardHeader>
      <CardContent>
        {!churn.since ? (
          <p className="text-sm text-muted-foreground">{NO_HISTORY}</p>
        ) : (
          <>
            <p className="pb-3 text-xs text-muted-foreground">
              Files changed by the most different people. The bar splits each file&rsquo;s commits
              by author: a short first segment means nobody owns it. Commits, not lines, so a sweep
              adds one name and never makes its author the owner. Counted by the path each commit
              recorded, so a rename starts a file&rsquo;s count again.
            </p>
            <div className="flex flex-wrap gap-4 pb-3 text-xs text-muted-foreground">
              {LEGEND.map((label, i) => (
                <span key={label} className="flex items-center gap-1.5">
                  <span className={`size-2.5 rounded-xs ${AUTHOR_SEGMENTS[i]}`} />
                  {label}
                </span>
              ))}
              <span className="flex items-center gap-1.5">
                <span className="size-2.5 rounded-xs bg-muted" />
                everyone else
              </span>
            </div>
            {authors.files.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                No file at HEAD has a recorded commit.
              </p>
            ) : (
              <ol className="divide-y rounded-lg border bg-card font-mono text-xs">
                {authors.files.map((file) => (
                  <li key={file.qualifiedPath} className="flex items-center gap-3 px-4 py-2">
                    <span className="w-20 shrink-0 text-right tabular-nums text-muted-foreground">
                      {formatCount(file.authors)} {file.authors === 1 ? 'author' : 'authors'}
                    </span>
                    <AuthorSplit shares={[file.first, file.second, file.third]} />
                    <span
                      className="w-10 shrink-0 text-right tabular-nums"
                      title={`${formatCount(file.commits)} commits`}
                    >
                      {Math.round(file.first * 100)}%
                    </span>
                    <FilePathLink
                      project={project}
                      qualifiedPath={file.qualifiedPath}
                      atHead
                      origin={{ view: 'overview' }}
                    />
                  </li>
                ))}
              </ol>
            )}
            <ExcludedNote
              project={project}
              files={authors.excluded ?? undefined}
              what="the history at HEAD"
            />
          </>
        )}
      </CardContent>
    </Card>
  )
}
