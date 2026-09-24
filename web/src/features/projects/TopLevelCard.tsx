import { FilePathLink } from '@/components/FilePathLink'
import type { IndexOverview } from '@/features/projects/api'
import { TopLevelRow } from '@/features/projects/TopLevelRow'
import { formatBytes, formatCount } from '@/lib/format'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

/**
 * How the project is laid out: the folders at the top level of each repository, with everything
 * beneath each one counted, each repository's root files as one line after its folders, and the
 * files big enough that a reader should know before opening one. A repository with no root files
 * has no such line.
 */
export function TopLevelCard({ project, overview }: { project: string; overview: IndexOverview }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Top level</CardTitle>
      </CardHeader>
      <CardContent className="space-y-1 font-mono text-xs">
        {overview.tree.flatMap((root) => [
          ...root.folders.map((folder) => (
            <TopLevelRow
              key={folder.qualifiedPath}
              project={project}
              path={folder.qualifiedPath}
              label={`${folder.qualifiedPath}/`}
              files={folder.files}
              bytes={folder.sizeBytes}
            />
          )),
          root.rootFiles > 0 ? (
            <TopLevelRow
              key={`${root.qualifiedPath}:root`}
              project={project}
              path={root.qualifiedPath}
              label={
                root.qualifiedPath === '' ? 'at the root' : `at the root of ${root.qualifiedPath}`
              }
              files={root.rootFiles}
              bytes={root.rootBytes}
              muted
            />
          ) : null,
        ])}
        {overview.otherFolders > 0 ? (
          <p className="pt-1 font-sans text-xs text-muted-foreground">
            and {formatCount(overview.otherFolders)} more
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
