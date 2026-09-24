import type {
  OverviewChurn,
  OverviewFolderCoupling,
  RepositoryCoupling,
} from '@/features/projects/api'
import { featuredRepository, pairShare, sharedCommits } from '@/features/projects/folderCoupling'
import { formatCount, formatPercent } from '@/lib/format'
import { cn } from '@/lib/utils'
import { NO_HISTORY } from '@/features/projects/noHistory'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

/** Pairs listed per repository: the strongest under the heatmap, and for each repository it does not draw. */
const PAIRS_SHOWN = 3

const title = <CardTitle>Folders that change together</CardTitle>

/**
 * The pairs of top-level folders, within one repository, that the same commits keep touching (#213):
 * boundaries that are eroding. A heatmap for the repository selected in the filter bar, or the busiest
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
            No commit in the window touched two top-level folders of one repository.
          </p>
        ) : (
          <>
            <p className="pb-3 text-xs text-muted-foreground">
              Top-level folders the same commits touched, darker for more shared commits. One commit
              counts once however many files it touched in a folder.
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

/** Heatmap shades, lightest first; a cell takes the one its share of the strongest pair reaches. */
const SHADES = ['bg-primary/20', 'bg-primary/40', 'bg-primary/60', 'bg-primary/80', 'bg-primary']

/** The upper triangle of shared commits, one row and column per folder, busiest first. */
function Heatmap({ repository }: { repository: RepositoryCoupling }) {
  const folders = repository.folders.map((f) => f.folder)
  const strongest = Math.max(...repository.pairs.map((p) => p.commits))
  return (
    <div className="overflow-x-auto">
      <table
        aria-label={`Shared commits between the top-level folders of ${repository.repositorySlug}`}
        className="border-separate border-spacing-0.5 font-mono text-xs"
      >
        <thead>
          <tr>
            <th>
              <span className="sr-only">Folder</span>
            </th>
            {folders.slice(1).map((folder) => (
              <th key={folder} className="h-20 align-bottom font-normal text-muted-foreground">
                <span className="inline-block max-w-20 -rotate-45 truncate">{folder}</span>
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {folders.slice(0, -1).map((row, i) => (
            <tr key={row}>
              <th className="max-w-28 truncate pr-1 text-right font-normal text-muted-foreground">
                {row}
              </th>
              {folders.slice(1).map((column, j) => {
                if (j < i)
                  return (
                    <td key={column}>
                      <span className="sr-only">below the diagonal</span>
                    </td>
                  )
                const shared = sharedCommits(repository, row, column)
                const label = `${row} + ${column}: ${formatCount(shared)} commits`
                return (
                  <td
                    key={column}
                    className={cn(
                      'size-5 rounded-xs',
                      shared > 0
                        ? SHADES[Math.ceil((SHADES.length * shared) / strongest) - 1]
                        : 'bg-muted',
                    )}
                    title={label}
                  >
                    <span className="sr-only">{label}</span>
                  </td>
                )
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
