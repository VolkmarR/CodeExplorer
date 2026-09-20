import { Link } from '@tanstack/react-router'
import { FilePathLink } from '@/components/FilePathLink'
import type { IndexOverview, OverviewEntry } from '@/features/projects/api'
import { formatBytes, formatCount } from '@/lib/format'
import { treeSearch } from '@/lib/urls/browseParams'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

/**
 * How the project is laid out: the top level of each repository, with everything beneath each entry
 * counted, and the files big enough that a reader should know before opening one.
 */
export function TopLevelCard({ project, overview }: { project: string; overview: IndexOverview }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Top level</CardTitle>
      </CardHeader>
      <CardContent className="space-y-1 font-mono text-xs">
        {overview.tree.map((entry) => (
          <TopLevelRow key={entry.qualifiedPath} project={project} entry={entry} />
        ))}
        {overview.otherEntries > 0 ? (
          <p className="pt-1 font-sans text-xs text-muted-foreground">
            and {formatCount(overview.otherEntries)} more
          </p>
        ) : null}
        {overview.largestFiles.length > 0 ? (
          <div className="space-y-1 border-t pt-3">
            <p className="font-sans text-xs font-medium">Largest files</p>
            {overview.largestFiles.map((file) => (
              <div key={file.qualifiedPath} className="flex min-w-0 items-baseline gap-3">
                <span className="w-20 shrink-0 text-right tabular-nums text-muted-foreground">
                  {formatBytes(file.sizeBytes)}
                </span>
                <FilePathLink project={project} qualifiedPath={file.qualifiedPath} atHead />
              </div>
            ))}
          </div>
        ) : null}
      </CardContent>
    </Card>
  )
}

/** A directory opens the tree at that path; a file at the root opens the file itself. */
function TopLevelRow({ project, entry }: { project: string; entry: OverviewEntry }) {
  const counts = (
    <span className="ml-auto shrink-0 tabular-nums text-muted-foreground">
      {formatCount(entry.files)} files &middot; {formatBytes(entry.sizeBytes)}
    </span>
  )
  if (!entry.isDirectory) {
    return (
      <div className="flex min-w-0 items-baseline gap-3">
        <FilePathLink project={project} qualifiedPath={entry.qualifiedPath} atHead />
        {counts}
      </div>
    )
  }

  return (
    <div className="flex min-w-0 items-baseline gap-3">
      <Link
        to="/projects/$project/files"
        params={{ project }}
        search={treeSearch(entry.qualifiedPath)}
        className="truncate hover:text-primary hover:underline"
      >
        {entry.qualifiedPath}/
      </Link>
      {counts}
    </div>
  )
}
