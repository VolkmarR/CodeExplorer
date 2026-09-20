import { DiffStat } from '@/components/DiffStat'
import { FilePathLink } from '@/components/FilePathLink'
import type { ChurnFile } from '@/features/churn/api'
import { formatCount } from '@/lib/format'

/**
 * The ranking itself: most commits first, with the lines each file gained and lost.
 *
 * Ranked by number of commits rather than by lines, for the reason blame gives — a reformat is a
 * change, and this says where work happened, not where the logic did (CONTEXT.md, Churn).
 *
 * It takes the files rather than the whole `Churn`, so the project page's overview — which carries
 * the same ranking from the stored row — shows it in the same shape. One ranking with two renderings
 * would let the churn page and the project page disagree about a file nobody changed twice.
 */
export function ChurnList({ project, files }: { project: string; files: ChurnFile[] }) {
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
          <FilePathLink
            project={project}
            qualifiedPath={file.qualifiedPath}
            atHead={file.atHead}
            origin={{ view: 'churn' }}
          />
        </li>
      ))}
    </ol>
  )
}
