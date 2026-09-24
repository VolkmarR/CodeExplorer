import type { RepositoryDetail } from '@/features/projects/api'
import { RepositorySelect } from '@/features/projects/RepositorySelect'
import { describeWindow, windowsWith } from '@/lib/urls/churnParams'
import {
  DEFAULT_OVERVIEW_DAYS,
  OVERVIEW_WINDOWS,
  type OverviewParameters,
} from '@/lib/urls/overviewParams'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Toggle } from '@/components/ui/toggle'

/**
 * The overview's filter bar (#216): the window the most-changed ranking is counted over, the
 * repository every section is narrowed to, and the switch that shows the project's excluded paths
 * anyway. Each control hands back a change rather than a whole view, so touching one keeps the rest.
 *
 * The repository is offered only where there is a choice, and the switch only where the project
 * excludes something: a control that can change nothing reads as one that is broken.
 */
export function OverviewControls({
  search,
  repositories,
  excludedPatterns,
  onChange,
}: {
  search: OverviewParameters
  repositories: RepositoryDetail[]
  excludedPatterns: number
  onChange: (change: Partial<OverviewParameters>) => void
}) {
  const days = search.days ?? DEFAULT_OVERVIEW_DAYS
  return (
    <div className="flex flex-wrap items-center gap-3">
      <div className="flex items-center gap-2">
        <Label htmlFor="overview-window" className="text-xs text-muted-foreground">
          Window
        </Label>
        <Select value={String(days)} onValueChange={(next) => onChange({ days: Number(next) })}>
          <SelectTrigger id="overview-window" className="w-36">
            <SelectValue>{describeWindow(days)}</SelectValue>
          </SelectTrigger>
          <SelectContent>
            {windowsWith(OVERVIEW_WINDOWS, days).map((offered) => (
              <SelectItem key={offered} value={String(offered)}>
                {describeWindow(offered)}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>

      {repositories.length > 1 ? (
        <div className="flex items-center gap-2">
          <Label htmlFor="overview-repository" className="text-xs text-muted-foreground">
            Repository
          </Label>
          <div className="w-48">
            <RepositorySelect
              id="overview-repository"
              repositories={repositories}
              value={search.repository ?? ''}
              onChange={(repository) => onChange({ repository })}
            />
          </div>
        </div>
      ) : null}

      {excludedPatterns > 0 ? (
        <Toggle
          variant="outline"
          size="sm"
          pressed={search.showExcluded === true}
          onPressedChange={(pressed) => onChange({ showExcluded: pressed })}
        >
          Show excluded paths
        </Toggle>
      ) : null}
    </div>
  )
}
