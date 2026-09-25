import type { IndexOverview } from '@/features/projects/api'
import { ExcludedNote } from '@/features/projects/ExcludedNote'
import { TopLevelRow } from '@/features/projects/TopLevelRow'
import { formatCount } from '@/lib/format'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

/**
 * How the project is laid out: the folders at the top level of each repository, with everything
 * beneath each one counted, and each repository's root files as one line after its folders. A
 * repository with no root files has no such line. The largest files are the Risk page's
 * (`LargestFilesCard`).
 */
export function TopLevelCard({
  project,
  overview,
  excluded,
}: {
  project: string
  overview: IndexOverview
  /** Files at HEAD the excluded paths left out, if any were: the same set both halves are read from. */
  excluded: number | undefined
}) {
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
              entry={folder}
              label={`${folder.qualifiedPath}/`}
            />
          )),
          root.rootFiles ? (
            <TopLevelRow
              key={`${root.qualifiedPath}:root`}
              project={project}
              entry={root.rootFiles}
              label={
                root.qualifiedPath === '' ? 'at the root' : `at the root of ${root.qualifiedPath}`
              }
              muted
            />
          ) : null,
        ])}
        {overview.otherFolders > 0 ? (
          <p className="pt-1 font-sans text-xs text-muted-foreground">
            and {formatCount(overview.otherFolders)} more
          </p>
        ) : null}
        <div className="font-sans">
          <ExcludedNote project={project} files={excluded} what="the files at HEAD" />
        </div>
      </CardContent>
    </Card>
  )
}
