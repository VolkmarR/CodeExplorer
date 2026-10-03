import type { RepositoryDetail } from '@/features/projects/api'
import { RepositoryFilter } from '@/features/projects/RepositoryFilter'
import {
  DEFAULT_OVERVIEW_DAYS,
  OVERVIEW_WINDOWS,
  type OverviewParameters,
} from '@/lib/urls/overviewParams'
import { WindowSelect } from '@/components/WindowSelect'
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
  showWindow = true,
  onChange,
}: {
  search: OverviewParameters
  repositories: RepositoryDetail[]
  excludedPatterns: number
  /** False on a page whose numbers the window does not change. */
  showWindow?: boolean
  onChange: (change: Partial<OverviewParameters>) => void
}) {
  return (
    <div className="flex flex-wrap items-center gap-3">
      {showWindow ? (
        <WindowSelect
          id="overview-window"
          offered={OVERVIEW_WINDOWS}
          days={search.days ?? DEFAULT_OVERVIEW_DAYS}
          onChange={(days) => onChange({ days })}
        />
      ) : null}

      <RepositoryFilter
        id="overview-repository"
        repositories={repositories}
        value={search.repository ?? ''}
        onChange={(repository) => onChange({ repository })}
      />

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
