import type { CSSProperties } from 'react'
import type { IndexOverview, OverviewHotspots } from '@/features/projects/api'
import { ExcludedNote } from '@/features/projects/ExcludedNote'
import { HotspotScatter } from '@/features/projects/HotspotScatter'
import { FilePathLink } from '@/components/FilePathLink'
import { formatCount } from '@/lib/format'
import { NO_HISTORY } from '@/features/projects/noHistory'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

/**
 * The files at HEAD that are both large and busy (#211): commits in the filter bar's window times
 * lines at HEAD. Where large files keep changing, and not what is unstable (CONTEXT.md, Hotspot).
 * The window is Most changed's, so whether there is history to rank is read off the same churn
 * section that card reads it from.
 */
export function HotspotsCard({
  project,
  overview,
  hotspots,
}: {
  project: string
  overview: IndexOverview
  hotspots: OverviewHotspots
}) {
  const { churn } = overview
  const { files } = hotspots
  const top = files[0]?.score ?? 0
  return (
    <Card>
      <CardHeader>
        <CardTitle>Hotspots</CardTitle>
      </CardHeader>
      <CardContent>
        {!churn.since ? (
          <p className="text-sm text-muted-foreground">{NO_HISTORY}</p>
        ) : (
          <>
            <p className="pb-3 text-xs text-muted-foreground">
              Large files that keep changing: commits in the {churn.days} days to the newest
              recorded commit, times lines at HEAD. Counted in commits, so a sweep adds one to every
              file it touches and moves nothing to the top.
            </p>
            {files.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                No file at HEAD was changed in this window.
              </p>
            ) : (
              <>
                <HotspotScatter files={files} />
                <ol className="mt-3 divide-y rounded-lg border bg-card font-mono text-xs">
                  {files.map((file, i) => (
                    <li key={file.qualifiedPath} className="flex items-center gap-3 px-4 py-2">
                      <span className="flex size-5 shrink-0 items-center justify-center rounded-full bg-primary font-sans text-xs font-semibold text-primary-foreground">
                        {i + 1}
                      </span>
                      <span className="w-12 shrink-0 text-right tabular-nums text-muted-foreground">
                        {formatCount(file.commits)}&times;
                      </span>
                      <span className="w-24 shrink-0 text-right tabular-nums text-muted-foreground">
                        {formatCount(file.lines)} lines
                      </span>
                      <FilePathLink
                        project={project}
                        qualifiedPath={file.qualifiedPath}
                        atHead
                        origin={{ view: 'overview' }}
                      />
                      {/* Relative to the top file, so it reads as how far behind it each one is. */}
                      <span className="ml-auto h-1.5 w-16 shrink-0 overflow-hidden rounded-full bg-muted">
                        <span
                          className="block h-full w-(--share) rounded-full bg-primary"
                          style={
                            { '--share': `${top ? (file.score / top) * 100 : 0}%` } as CSSProperties
                          }
                        />
                      </span>
                    </li>
                  ))}
                </ol>
              </>
            )}
            <ExcludedNote
              project={project}
              files={hotspots.excluded ?? undefined}
              what="this window at HEAD"
            />
          </>
        )}
      </CardContent>
    </Card>
  )
}
