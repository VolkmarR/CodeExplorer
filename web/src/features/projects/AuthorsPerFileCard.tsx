import type { OverviewAuthorsPerFile, OverviewChurn } from '@/features/projects/api'
import { AUTHOR_SEGMENTS, AuthorSplit } from '@/features/projects/AuthorSplit'
import { ExcludedNote } from '@/features/projects/ExcludedNote'
import { RankedList } from '@/features/projects/RankedList'
import { RankedRow } from '@/features/projects/RankedRow'
import { FilePathLink } from '@/components/FilePathLink'
import { formatCountOf, formatPercent } from '@/lib/format'
import { cn } from '@/lib/utils'
import { historyWindow, NO_HISTORY } from '@/features/projects/noHistory'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

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
        {!historyWindow(churn) ? (
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
              {AUTHOR_SEGMENTS.map(({ label, className }) => (
                <span key={label} className="flex items-center gap-1.5">
                  <span className={cn('size-2.5 rounded-xs', className)} />
                  {label}
                </span>
              ))}
            </div>
            {authors.files.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                No file at HEAD has a recorded commit.
              </p>
            ) : (
              <RankedList>
                {authors.files.map((file) => (
                  <RankedRow key={file.qualifiedPath}>
                    <span className="w-20 shrink-0 text-right tabular-nums text-muted-foreground">
                      {formatCountOf(file.authors, 'author')}
                    </span>
                    <AuthorSplit shares={[file.first, file.second, file.third]} />
                    <span
                      className="w-10 shrink-0 text-right tabular-nums"
                      title={formatCountOf(file.commits, 'commit')}
                    >
                      {formatPercent(file.first)}
                    </span>
                    <FilePathLink
                      project={project}
                      qualifiedPath={file.qualifiedPath}
                      atHead
                      origin={{ view: 'overview' }}
                    />
                  </RankedRow>
                ))}
              </RankedList>
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
