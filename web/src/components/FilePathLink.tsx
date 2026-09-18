import { Link } from '@tanstack/react-router'
import type { Origin } from '@/components/appNavigation'
import { fileSearch } from '@/features/files/fileParams'

/**
 * A path that opens the file when there is still a file there, and is named and struck through when
 * there is not. Both the change log and the churn ranking list paths out of history, and history
 * holds paths a later commit deleted or moved — so both need the same two states, and the decision
 * that an absent file is shown rather than dropped belongs in one place.
 */
export function FilePathLink({
  project,
  qualifiedPath,
  atHead,
  label,
  line,
  origin,
}: {
  project: string
  qualifiedPath: string
  atHead: boolean
  /** What to show, when that is shorter than the qualified path — a commit lists paths within itself. */
  label?: string
  /** Where in the file to land, for a link that points at one line of it rather than at the file. */
  line?: number
  /** Where this link is, so the file's trail says how the reader got there. Absent reads as Files. */
  origin?: Origin
}) {
  if (!atHead) {
    return (
      <span className="truncate text-muted-foreground line-through decoration-muted-foreground/50">
        {label ?? qualifiedPath}
      </span>
    )
  }

  return (
    <Link
      to="/projects/$project/file"
      params={{ project }}
      search={fileSearch(qualifiedPath, line, origin)}
      className="truncate hover:text-primary hover:underline"
    >
      {label ?? qualifiedPath}
    </Link>
  )
}
