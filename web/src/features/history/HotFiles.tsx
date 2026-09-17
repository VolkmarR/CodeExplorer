import { DiffStat } from '@/components/DiffStat'
import { FilePathLink } from '@/components/FilePathLink'
import type { HotFiles as Ranking } from '@/lib/api'
import { formatCount, formatDate } from '@/lib/format'

/**
 * What moved most over the commits the page below is showing. The window is that page's own span and
 * not a period of its own: an operator reading a ranking beside a list of commits takes the two to be
 * about the same changes, so they are, and paging moves both.
 *
 * It ranks by number of commits rather than by lines, for the reason blame gives — a reformat is a
 * change, and this says where work happened, not where the logic did.
 */
export function HotFiles({ project, ranking }: { project: string; ranking: Ranking }) {
  // Nothing ranked and nothing to caveat is nothing to draw. A repository whose history is missing is
  // still worth saying, though, and is exactly what an empty panel would otherwise be read as denying.
  if (ranking.files.length === 0 && ranking.withoutHistory.length === 0) return null

  return (
    <section className="rounded-lg border bg-card px-4 py-3">
      {ranking.files.length > 0 ? (
        <>
          <h2 className="text-sm font-medium">
            Most changed{' '}
            <span className="font-normal text-muted-foreground">
              {/* Both dates or neither: the server sends the span of the page, and a page with no
                  commits has none — which is the branch above, not a half-filled window here. */}
              {ranking.since && ranking.until
                ? `on this page, ${formatDate(ranking.since)} to ${formatDate(ranking.until)}`
                : 'on this page'}
            </span>
          </h2>
          <ul className="mt-2 space-y-0.5 font-mono text-xs">
            {ranking.files.map((file) => (
              <li key={file.qualifiedPath} className="flex items-baseline gap-3">
                <span className="w-20 shrink-0 tabular-nums text-muted-foreground">
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
          </ul>
        </>
      ) : null}
      {ranking.withoutHistory.length > 0 ? (
        <p className="text-xs text-muted-foreground not-first:mt-2">
          No history was imported for {ranking.withoutHistory.join(', ')}, so nothing from{' '}
          {ranking.withoutHistory.length === 1 ? 'it' : 'those'} can appear here however much{' '}
          {ranking.withoutHistory.length === 1 ? 'it' : 'they'} changed.
        </p>
      ) : null}
    </section>
  )
}
