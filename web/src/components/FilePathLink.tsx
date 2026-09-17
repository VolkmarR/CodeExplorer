import { Link } from '@tanstack/react-router'
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
}: {
  project: string
  qualifiedPath: string
  atHead: boolean
  /** What to show, when that is shorter than the qualified path — a commit lists paths within itself. */
  label?: string
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
      search={fileSearch(qualifiedPath)}
      className="truncate hover:text-primary hover:underline"
    >
      {label ?? qualifiedPath}
    </Link>
  )
}
