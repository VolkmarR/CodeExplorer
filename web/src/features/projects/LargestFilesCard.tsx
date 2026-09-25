import { FilePathLink } from '@/components/FilePathLink'
import type { IndexOverview } from '@/features/projects/api'
import { ExcludedNote } from '@/features/projects/ExcludedNote'
import { formatBytes } from '@/lib/format'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

/**
 * The files big enough that a reader should know before opening one. On the Risk page rather than
 * beside the layout, because a very large file is expensive to change whatever folder it sits in.
 */
export function LargestFilesCard({
  project,
  overview,
  excluded,
}: {
  project: string
  overview: IndexOverview
  /** Files at HEAD the excluded paths left out, if any were. */
  excluded: number | undefined
}) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Largest files</CardTitle>
      </CardHeader>
      <CardContent className="space-y-1 font-mono text-xs">
        {overview.largestFiles.length > 0 ? (
          overview.largestFiles.map((file) => (
            <div key={file.qualifiedPath} className="flex min-w-0 items-baseline gap-3">
              <span className="w-20 shrink-0 text-right tabular-nums text-muted-foreground">
                {formatBytes(file.sizeBytes)}
              </span>
              <FilePathLink project={project} qualifiedPath={file.qualifiedPath} atHead />
            </div>
          ))
        ) : (
          <p className="font-sans text-sm text-muted-foreground">No files indexed at HEAD.</p>
        )}
        <div className="font-sans">
          <ExcludedNote project={project} files={excluded} what="the files at HEAD" />
        </div>
      </CardContent>
    </Card>
  )
}
