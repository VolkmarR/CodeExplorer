import type { OverviewAuthorsPerFile, OverviewChurn } from '@/features/projects/api'
import { AUTHOR_SEGMENTS, AuthorSplit } from '@/features/projects/AuthorSplit'
import { OverviewCard } from '@/features/projects/OverviewCard'
import { RankedList } from '@/features/projects/RankedList'
import { RankedRow } from '@/features/projects/RankedRow'
import { FilePathLink } from '@/components/FilePathLink'
import { formatCountOf, formatPercent } from '@/lib/format'
import { cn } from '@/lib/utils'

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
    <OverviewCard
      title="Most authors per file"
      aside="whole imported history"
      history={churn}
      note={
        <>
          Files changed by the most different people. The bar splits each file&rsquo;s commits by
          author: a short first segment means nobody owns it. Commits, not lines, so a sweep adds
          one name and never makes its author the owner. Counted by the path each commit recorded,
          so a rename starts a file&rsquo;s count again.
        </>
      }
      excluded={{ files: authors.excluded ?? undefined, project, which: 'at HEAD with history' }}
    >
      <div className="flex flex-wrap gap-4 pb-3 text-xs text-muted-foreground">
        {AUTHOR_SEGMENTS.map(({ label, className }) => (
          <span key={label} className="flex items-center gap-1.5">
            <span className={cn('size-2.5 rounded-xs', className)} />
            {label}
          </span>
        ))}
      </div>
      {authors.files.length === 0 ? (
        <p className="text-sm text-muted-foreground">No file at HEAD has a recorded commit.</p>
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
    </OverviewCard>
  )
}
