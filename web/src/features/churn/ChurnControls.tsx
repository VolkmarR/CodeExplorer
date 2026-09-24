import {
  CHURN_WINDOWS,
  DEFAULT_CHURN_DEPTH,
  describeWindow,
  windowsWith,
  type ChurnParameters,
} from '@/lib/urls/churnParams'
import type { RepositoryDetail } from '@/features/projects/api'
import { RepositorySelect } from '@/features/projects/RepositorySelect'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'

/**
 * The three controls in the churn card's header: how far back, what the rows are, and which
 * repository. Its own component because the page grew a filter and a scope trail under it (#161) and
 * the header's branches made the view itself hard to read — the lint rule that fails on a complex
 * component is the one that said so.
 *
 * Each one hands back a change to the ranking rather than a whole one, so nothing a reader set
 * elsewhere is dropped by touching a different control.
 */
export function ChurnControls({
  search,
  repositories,
  onChange,
}: {
  search: ChurnParameters
  repositories: RepositoryDetail[]
  onChange: (change: Partial<ChurnParameters>) => void
}) {
  // One repository needs no filter; the choice is offered only where there is one to make. Nor is it
  // offered once a directory is chosen, which carries its own repository — the trail moves the
  // reader then, and two controls on one scope could disagree.
  const filterable = repositories.length > 1 && search.directory === undefined

  return (
    <div className="flex flex-wrap items-center gap-3">
      <div className="flex items-center gap-2">
        <Label htmlFor="churn-window" className="text-xs text-muted-foreground">
          Window
        </Label>
        <Select
          value={String(search.days)}
          onValueChange={(next) => onChange({ days: Number(next) })}
        >
          <SelectTrigger id="churn-window" className="w-36">
            <SelectValue>{describeWindow(search.days)}</SelectValue>
          </SelectTrigger>
          <SelectContent>
            {windowsWith(CHURN_WINDOWS, search.days).map((days) => (
              <SelectItem key={days} value={String(days)}>
                {describeWindow(days)}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>

      {/* Before the repository, because it changes what every row below means where the repository
          only changes which of them there are. */}
      <div className="flex items-center gap-2">
        <Label htmlFor="churn-rows" className="text-xs text-muted-foreground">
          Rank
        </Label>
        <Select
          value={search.depth === undefined ? FILES : String(search.depth)}
          onValueChange={(next) => onChange({ depth: next === FILES ? undefined : Number(next) })}
        >
          <SelectTrigger id="churn-rows" className="w-44">
            <SelectValue>
              {search.depth === undefined ? 'Files' : describeDepth(search.depth)}
            </SelectValue>
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={FILES}>Files</SelectItem>
            {[DEFAULT_CHURN_DEPTH, 2, 3].map((depth) => (
              <SelectItem key={depth} value={String(depth)}>
                {describeDepth(depth)}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>

      {filterable ? (
        <div className="flex items-center gap-2">
          <Label htmlFor="churn-repository" className="text-xs text-muted-foreground">
            Repository
          </Label>
          <div className="w-48">
            <RepositorySelect
              id="churn-repository"
              repositories={repositories}
              value={search.repository ?? ''}
              onChange={(repository) => onChange({ repository })}
            />
          </div>
        </div>
      ) : null}
    </div>
  )
}

/**
 * Ranking files rather than directories, as the one value a select can hold for it. Not `'0'` or
 * `''`: the parameter is absent for files, and a number here would be a depth the server clamps
 * back up to one — a different ranking than the reader asked for.
 */
const FILES = 'files'

/** How a rollup level is named, so the select and the card's hint cannot disagree about a depth. */
export function describeDepth(depth: number): string {
  return depth === 1 ? 'Directories' : `Directories, ${depth} deep`
}
