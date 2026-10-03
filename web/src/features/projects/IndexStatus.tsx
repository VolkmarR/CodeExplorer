import type { ProjectIndexStatus } from '@/features/projects/api'
import { formatCountOf, formatTime } from '@/lib/format'
import { Badge } from '@/components/ui/badge'

/**
 * A project's index in one line. A project that has never been built is the ordinary starting state,
 * not an error, so it reads as "not built" rather than as a warning.
 */
export function IndexStatus({ status }: { status: ProjectIndexStatus }) {
  if (!status.builtAt) {
    return <Badge variant="muted">not built</Badge>
  }

  return (
    <span className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
      <Badge variant="secondary">{status.ftsIndexed ? 'full-text' : 'substring scan'}</Badge>
      <span>
        {formatCountOf(status.files, 'file')}, {formatCountOf(status.lines, 'line')}
      </span>
      <span className="text-muted-foreground/70">built {formatTime(status.builtAt)}</span>
    </span>
  )
}
