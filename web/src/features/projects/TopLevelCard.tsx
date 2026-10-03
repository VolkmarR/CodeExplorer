import type { OverviewRoot } from '@/features/projects/api'
import { OverviewCard } from '@/features/projects/OverviewCard'
import { TopLevelRow } from '@/features/projects/TopLevelRow'
import { formatCount } from '@/lib/format'

/**
 * How the project is laid out: the folders at the top level of each repository, with everything
 * beneath each one counted, and each repository's root files as one line after its folders. A
 * repository with no root files has no such line. The largest files are the Risk page's
 * (`LargestFilesCard`).
 */
export function TopLevelCard({
  project,
  tree,
  otherFolders,
  excluded,
}: {
  project: string
  tree: OverviewRoot[]
  /** Top-level folders past the ones `tree` lists. */
  otherFolders: number
  /** Files at HEAD the excluded paths left out, if any were: the same set both halves are read from. */
  excluded: number | undefined
}) {
  return (
    <OverviewCard
      title="Top level"
      excluded={{ files: excluded, project, what: 'the files at HEAD' }}
      className="space-y-1 font-mono text-xs"
    >
      {tree.flatMap((root) => [
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
      {otherFolders > 0 ? (
        <p className="pt-1 font-sans text-xs text-muted-foreground">
          and {formatCount(otherFolders)} more
        </p>
      ) : null}
    </OverviewCard>
  )
}
