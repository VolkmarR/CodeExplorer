import type {
  OverviewChurn,
  OverviewFolderCoupling,
  RepositoryCoupling,
} from '@/features/projects/api'
import { featuredRepository, pairShare, sharedCommits } from '@/features/projects/folderCoupling'
import { useMemo } from 'react'
import { cell, defineChart } from '@tanstack/charts'
import { Chart } from '@tanstack/charts/react'
import { scaleBand } from '@tanstack/charts/scales/band'
import { tooltip } from '@tanstack/charts/tooltip'
import { formatCount, formatPercent } from '@/lib/format'
import { NO_HISTORY } from '@/features/projects/noHistory'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

/** Pairs listed per repository: the strongest under the heatmap, and for each repository it does not draw. */
const PAIRS_SHOWN = 3

const title = <CardTitle>Folders that change together</CardTitle>

/**
 * The pairs of folders, within one repository, that the same commits keep touching (#213): boundaries
 * that are eroding. The top-level folders, or where one holds nearly everything, as `src/` does around
 * a whole solution, its children in its place. A heatmap for the repository selected in the filter bar, or the busiest
 * one, and the strongest pairs of the others. Whether there is history at all is read off Most
 * changed's section, as the other history cards read it.
 */
export function FolderCouplingCard({
  repository,
  churn,
  coupling,
}: {
  /** The filter bar's repository, or undefined for all. */
  repository: string | undefined
  /** Most changed's section, for whether there was history at all. */
  churn: OverviewChurn
  coupling: OverviewFolderCoupling
}) {
  const featured = featuredRepository(coupling, repository)
  const others = coupling.repositories.filter((r) => r !== featured)
  if (!churn.since) {
    return (
      <Card>
        <CardHeader>{title}</CardHeader>
        <CardContent>
          <p className="text-sm text-muted-foreground">{NO_HISTORY}</p>
        </CardContent>
      </Card>
    )
  }

  return (
    <Card>
      <CardHeader className="flex flex-row items-baseline justify-between">
        {title}
        {featured && (
          <span className="text-xs text-muted-foreground">{featured.repositorySlug}</span>
        )}
      </CardHeader>
      <CardContent>
        {!featured || featured.pairs.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            No commit in the window touched two folders of one repository.
          </p>
        ) : (
          <>
            <p className="pb-3 text-xs text-muted-foreground">
              Folders the same commits touched, darker for more shared commits. A folder holding
              nearly the whole repository is shown by its subfolders. One commit counts once however
              many files it touched in a folder.
            </p>
            <Heatmap repository={featured} />
            <ol className="mt-3 divide-y rounded-lg border bg-card font-mono text-xs">
              {featured.pairs.slice(0, PAIRS_SHOWN).map((pair) => (
                <li
                  key={`${pair.first}\u0000${pair.second}`}
                  className="flex items-center gap-3 px-4 py-2"
                >
                  <span className="min-w-0 flex-1 truncate">
                    {pair.first} + {pair.second}
                  </span>
                  <span className="tabular-nums text-muted-foreground">
                    {formatCount(pair.commits)} commits
                  </span>
                  <span
                    className="w-10 text-right tabular-nums"
                    title="Share of the quieter folder's commits"
                  >
                    {formatPercent(pairShare(featured, pair))}
                  </span>
                </li>
              ))}
            </ol>
          </>
        )}
        {others.length > 0 && (
          <div className="pt-3 text-xs">
            {others.map((other) => (
              <p key={other.repositorySlug} className="py-0.5">
                <span className="font-medium">{other.repositorySlug}</span>
                <span className="text-muted-foreground">
                  {': '}
                  {other.pairs.length === 0
                    ? 'no pairs'
                    : other.pairs
                        .slice(0, PAIRS_SHOWN)
                        .map((p) => `${p.first} + ${p.second} (${formatCount(p.commits)})`)
                        .join(', ')}
                </span>
              </p>
            ))}
          </div>
        )}
        <p className="pt-3 text-xs text-muted-foreground">
          Commits touching more than {formatCount(coupling.maxCommitPaths)} paths are left out
          (History:MaxCommitPaths): {formatCount(coupling.ceilingExcluded)} in this window.
        </p>
      </CardContent>
    </Card>
  )
}

/**
 * Heatmap shades by step: none shared first, then lightest to darkest. A cell takes the step its
 * share of the strongest pair reaches.
 */
const SHADES = [
  'var(--muted)',
  'color-mix(in oklch, var(--primary) 20%, transparent)',
  'color-mix(in oklch, var(--primary) 40%, transparent)',
  'color-mix(in oklch, var(--primary) 60%, transparent)',
  'color-mix(in oklch, var(--primary) 80%, transparent)',
  'var(--primary)',
]

/** The side of one cell, in pixels: the chart is sized from its folders rather than the card. */
const CELL = 22

/** Room for the folder names beside and under the cells, which are rotated below. */
const MARGIN = { bottom: 84, left: 116 }

/** A folder name short enough for its axis; the tooltip carries the whole of it. */
function shortName(folder: string) {
  return folder.length > 14 ? `${folder.slice(0, 13)}…` : folder
}

/** The upper triangle of shared commits, one row and column per folder, busiest first. */
function Heatmap({ repository }: { repository: RepositoryCoupling }) {
  const heatmap = useMemo(() => {
    const folders = repository.folders.map((f) => f.folder)
    const rows = folders.slice(0, -1)
    const columns = folders.slice(1)
    const strongest = Math.max(...repository.pairs.map((p) => p.commits))
    const cells = rows.flatMap((row, i) =>
      columns.slice(i).map((column) => {
        const shared = sharedCommits(repository, row, column)
        return {
          column,
          row,
          shared,
          shade: String(shared > 0 ? Math.ceil(((SHADES.length - 1) * shared) / strongest) : 0),
        }
      }),
    )
    const definition = defineChart({
      marks: [
        cell(cells, {
          x: 'column',
          y: 'row',
          color: 'shade',
          key: (c) => `${c.row}\u0000${c.column}`,
          inset: 1,
          radius: 2,
        }),
      ],
      scales: {
        x: {
          scale: scaleBand().domain(columns),
          axis: {
            line: false,
            ticks: { size: 0, format: shortName },
            tickLabels: { rotate: -45, anchor: 'end' },
          },
        },
        y: {
          scale: scaleBand().domain(rows),
          axis: { line: false, ticks: { size: 0, format: shortName } },
        },
      },
      color: { domain: SHADES.map((_, i) => String(i)), range: SHADES },
      margin: MARGIN,
      tooltip,
    })
    return { definition, rows, columns }
  }, [repository])

  return (
    <div className="overflow-x-auto">
      <Chart
        definition={heatmap.definition}
        width={MARGIN.left + CELL * heatmap.columns.length}
        height={MARGIN.bottom + CELL * heatmap.rows.length}
        className="font-mono text-xs text-muted-foreground"
        ariaLabel={`Shared commits between the folders of ${repository.repositorySlug}`}
      />
    </div>
  )
}
