import { Link } from '@tanstack/react-router'
import { DiffStat } from '@/components/DiffStat'
import { FilePathLink } from '@/components/FilePathLink'
import type { ChurnFile } from '@/features/churn/api'
import type { ChurnParameters } from '@/lib/urls/churnParams'
import { formatCount } from '@/lib/format'

/**
 * The ranking itself: most commits first, with the lines each row gained and lost.
 *
 * Ranked by number of commits rather than by lines, for the reason blame gives — a reformat is a
 * change, and this says where work happened, not where the logic did (CONTEXT.md, Churn).
 *
 * It takes the files rather than the whole `Churn`, so the project page's overview — which carries
 * the same ranking from the stored row — shows it in the same shape. One ranking with two renderings
 * would let the churn page and the project page disagree about a file nobody changed twice.
 *
 * A row is a file or a directory, and `drillInto` is which (#161): given, each row is a directory and
 * the link narrows the ranking to it rather than opening it. A `Link` and not a click handler,
 * because where the ranking is scoped lives in the URL — so a drilled-in ranking is pasteable,
 * middle-clickable and reachable with the back button, like every other view here (ADR-0004).
 */
export function ChurnList({
  project,
  files,
  drillInto,
}: {
  project: string
  files: ChurnFile[]
  drillInto?: (qualifiedPath: string) => ChurnParameters
}) {
  return (
    <ol className="divide-y rounded-lg border bg-card font-mono text-xs">
      {files.map((file) => (
        <li key={file.qualifiedPath} className="flex items-baseline gap-4 px-4 py-2">
          <span className="w-16 shrink-0 text-right tabular-nums text-muted-foreground">
            {formatCount(file.commits)}&times;
          </span>
          <span className="w-28 shrink-0 tabular-nums">
            <DiffStat added={file.added} deleted={file.deleted} />
          </span>
          {drillInto ? (
            <Link
              to="/projects/$project/churn"
              params={{ project }}
              search={drillInto(file.qualifiedPath)}
              className="truncate hover:text-primary hover:underline"
            >
              {/* The trailing slash says this is a directory, in the one place the reader cannot
                  tell from the path alone: a repository root and a file both come back bare. */}
              {file.qualifiedPath}/
            </Link>
          ) : (
            <FilePathLink
              project={project}
              qualifiedPath={file.qualifiedPath}
              atHead={file.atHead}
              origin={{ view: 'churn' }}
            />
          )}
        </li>
      ))}
    </ol>
  )
}
