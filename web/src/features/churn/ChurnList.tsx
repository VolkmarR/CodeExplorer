import { DiffStat } from '@/components/DiffStat'
import { FilePathLink } from '@/components/FilePathLink'
import type { Churn } from '@/lib/api'
import { formatCount } from '@/lib/format'

/**
 * The ranking itself: most commits first, with the lines each file gained and lost.
 *
 * Ranked by number of commits rather than by lines, for the reason blame gives — a reformat is a
 * change, and this says where work happened, not where the logic did (CONTEXT.md, Churn).
 */
export function ChurnList({ project, ranking }: { project: string; ranking: Churn }) {
  return (
    <ol className="divide-y rounded-lg border bg-card font-mono text-xs">
      {ranking.files.map((file) => (
        <li key={file.qualifiedPath} className="flex items-baseline gap-4 px-4 py-2">
          <span className="w-16 shrink-0 text-right tabular-nums text-muted-foreground">
            {formatCount(file.commits)}&times;
          </span>
          <span className="w-28 shrink-0 tabular-nums">
            <DiffStat added={file.added} deleted={file.deleted} />
          </span>
          <FilePathLink project={project} qualifiedPath={file.qualifiedPath} atHead={file.atHead} />
        </li>
      ))}
    </ol>
  )
}
