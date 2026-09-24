import { Link } from '@tanstack/react-router'
import type { OverviewFolder } from '@/features/projects/api'
import { formatBytes, formatCount } from '@/lib/format'
import { treeSearch } from '@/lib/urls/browseParams'

/**
 * One row of the top level, opening the tree at the entry's path: a folder, or a repository's root
 * files counted as one entry at the root. The root-file line is muted and set in the body font, so
 * it cannot be read as a folder's name.
 */
export function TopLevelRow({
  project,
  entry,
  label,
  muted = false,
}: {
  project: string
  entry: OverviewFolder
  label: string
  muted?: boolean
}) {
  return (
    <div className="flex min-w-0 items-baseline gap-3">
      <Link
        to="/projects/$project/files"
        params={{ project }}
        search={treeSearch(entry.qualifiedPath)}
        className={
          muted
            ? 'truncate font-sans text-muted-foreground hover:text-primary hover:underline'
            : 'truncate hover:text-primary hover:underline'
        }
      >
        {label}
      </Link>
      <span className="ml-auto shrink-0 tabular-nums text-muted-foreground">
        {formatCount(entry.files)} {entry.files === 1 ? 'file' : 'files'} &middot;{' '}
        {formatBytes(entry.sizeBytes)}
      </span>
    </div>
  )
}
