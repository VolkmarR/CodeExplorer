import { Link } from '@tanstack/react-router'
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
      <h2 className="text-sm font-medium">
        Most changed{' '}
        <span className="font-normal text-muted-foreground">
          {ranking.since && ranking.until
            ? `on this page, ${formatDate(ranking.since)} to ${formatDate(ranking.until)}`
            : 'on this page'}
        </span>
      </h2>
      <ul className="mt-2 space-y-0.5 font-mono text-xs empty:hidden">
        {ranking.files.map((file) => (
          <li key={file.qualifiedPath} className="flex items-baseline gap-3">
            <span className="w-20 shrink-0 tabular-nums text-muted-foreground">
              {formatCount(file.commits)}&times;
            </span>
            <span className="w-24 shrink-0 tabular-nums">
              <span className="text-emerald-600 dark:text-emerald-400">
                +{formatCount(file.added)}
              </span>{' '}
              <span className="text-rose-600 dark:text-rose-400">
                &minus;{formatCount(file.deleted)}
              </span>
            </span>
            {/* A path still at HEAD opens the file; one a later commit deleted or moved is named and
                not linked, the same way the files of a commit are. */}
            {file.atHead ? (
              <Link
                to="/projects/$project/file"
                params={{ project }}
                search={{ path: file.qualifiedPath }}
                className="truncate hover:text-primary hover:underline"
              >
                {file.qualifiedPath}
              </Link>
            ) : (
              <span className="truncate text-muted-foreground line-through decoration-muted-foreground/50">
                {file.qualifiedPath}
              </span>
            )}
          </li>
        ))}
      </ul>
      {ranking.withoutHistory.length > 0 ? (
        <p className="mt-2 text-xs text-muted-foreground">
          No history was imported for {ranking.withoutHistory.join(', ')}, so nothing from{' '}
          {ranking.withoutHistory.length === 1 ? 'it' : 'those'} can appear here however much{' '}
          {ranking.withoutHistory.length === 1 ? 'it' : 'they'} changed.
        </p>
      ) : null}
    </section>
  )
}
