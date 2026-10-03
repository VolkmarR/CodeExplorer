import type { IndexOverview } from '@/features/projects/api'
import { ExcludedNote } from '@/features/projects/ExcludedNote'
import { formatCount, formatDate } from '@/lib/format'
import { historyWindow, NO_HISTORY } from '@/features/projects/noHistory'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

/**
 * Who has touched the project most over the filter bar's window, as every card on the Activity page
 * counts. The stored overview an agent reads counts the whole imported history instead.
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
        {!historyWindow(overview.churn) ? (
          <p className="text-sm text-muted-foreground">{NO_HISTORY}</p>
        ) : overview.authors.length === 0 ? (
          // History, and no author left in this window. Where the excluded paths took every commit
          // that is said rather than "no commits", which would read as a quiet project rather than a
          // filtered one (CODING_STANDARDS, Errors).
          <p className="text-sm text-muted-foreground">
            {excluded
              ? 'Every commit in this window touched only files the excluded paths leave out.'
              : 'No commits in this window.'}
          </p>
        ) : (
          <>
            {/* Said once, at the top, rather than per row: it is a caveat about the whole list, and
                attribution is who touched the code last and never who wrote it (CONTEXT.md). */}
            <p className="pb-2 text-xs text-muted-foreground">
              Commits in the window. Who to ask about a file, never who wrote it &mdash; a reformat
              is a change and it becomes the answer.
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
          </>
        )}
        {/* Outside the branches, so an empty list says why it is empty. A commit is dropped only
            where every file it touched was excluded, so the count is of files and the rows above are
            what is left of each author. */}
        <ExcludedNote
          project={project}
          files={excluded}
          what="the window's commits (a commit still counts while it touched one file shown)"
        />
      </CardContent>
    </Card>
  )
}
