import type { OverviewChurn, OverviewHotspots } from '@/features/projects/api'
import { HotspotScatter } from '@/features/projects/HotspotScatter'
import { FilePathLink } from '@/components/FilePathLink'
import { OverviewCard } from '@/features/projects/OverviewCard'
import { RankedList } from '@/features/projects/RankedList'
import { RankedRow } from '@/features/projects/RankedRow'
import { ShareBar } from '@/features/projects/ShareBar'
import { formatCount, formatCountOf } from '@/lib/format'

/**
 * The files at HEAD that are both large and busy (#211): commits in the filter bar's window times
 * lines at HEAD. Where large files keep changing, and not what is unstable (CONTEXT.md, Hotspot).
 * The window is Most changed's, so whether there is history to rank is read off the same churn
 * section that card reads it from.
 */
export function HotspotsCard({
  project,
  churn,
  hotspots,
}: {
  project: string
  /** Most changed's section, for its window: how far back, and whether there was history at all. */
  churn: OverviewChurn
  hotspots: OverviewHotspots
}) {
  const { files } = hotspots
  const top = files[0]?.score ?? 0
  return (
    <OverviewCard
      title="Hotspots"
      history={churn}
      note={
        <>
          Large files that keep changing: commits in the {churn.days} days to the newest recorded
          commit, times lines at HEAD. Counted in commits, so a sweep adds one to every file it
          touches and moves nothing to the top.
        </>
      }
      excluded={{
        files: hotspots.excluded ?? undefined,
        project,
        which: 'at HEAD changed in this window',
      }}
    >
      {files.length === 0 ? (
        <p className="text-sm text-muted-foreground">No file at HEAD was changed in this window.</p>
      ) : (
        <>
          <HotspotScatter files={files} />
          <RankedList className="mt-3">
            {files.map((file, i) => (
              <RankedRow key={file.qualifiedPath}>
                <span className="flex size-5 shrink-0 items-center justify-center rounded-full bg-primary font-sans text-xs font-semibold text-primary-foreground">
                  {i + 1}
                </span>
                <span className="w-12 shrink-0 text-right tabular-nums text-muted-foreground">
                  {formatCount(file.commits)}&times;
                </span>
                <span className="w-24 shrink-0 text-right tabular-nums text-muted-foreground">
                  {formatCountOf(file.lines, 'line')}
                </span>
                <FilePathLink
                  project={project}
                  qualifiedPath={file.qualifiedPath}
                  atHead
                  origin={{ view: 'overview' }}
                />
                {/* Relative to the top file, so it reads as how far behind it each one is. */}
                <ShareBar
                  share={top ? (file.score / top) * 100 : 0}
                  className="ml-auto w-16 shrink-0"
                />
              </RankedRow>
            ))}
          </RankedList>
        </>
      )}
    </OverviewCard>
  )
}
