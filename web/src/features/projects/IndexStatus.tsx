import type { ProjectIndexStatus } from '@/lib/api'
import { formatCount, formatTime } from '@/lib/format'
import { Badge } from '@/components/ui/badge'

/**
 * A project's index in one line. A project that has never been built is the ordinary starting state,
 * not an error, so it reads as "not built" rather than as a warning.
 */
export function IndexStatus({ status }: { status: ProjectIndexStatus }) {
  if (!status.builtAt) {
    return (
      <Badge variant="outline" className="text-muted-foreground">
        not built
      </Badge>
    )
  }

  return (
    <span className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
      <Badge variant="secondary">{status.ftsIndexed ? 'full-text' : 'substring scan'}</Badge>
      <span>
        {formatCount(status.files)} files, {formatCount(status.lines)} lines
      </span>
      <span className="text-muted-foreground/70">built {formatTime(status.builtAt)}</span>
    </span>
  )
}
