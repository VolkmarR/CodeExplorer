import { Link } from '@tanstack/react-router'
import type { Origin } from '@/lib/urls/views'
import { fileSearch } from '@/lib/urls/fileParams'

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
  wrap = false,
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
  /**
   * Whether to wrap the label onto a second line rather than cut it off at the width available.
   * Off by default, because a list of paths that each wrap is a list whose rows no longer line up —
   * and a path truncated from the left still shows the directory the reader is scanning for.
   * On where the label is a name whose END is what distinguishes it, which is every dotted
   * namespace: `…MasterData.Infrastructure.McSerializer` and `…MasterData.Infrastructure.McReader`
   * truncate to the same string, so two different imports draw as one row twice (#160).
   */
  wrap?: boolean
}) {
  // One word either way, so the two states cannot drift apart: the deleted-file span below and the
  // link are the same label under the same rule.
  const fit = wrap ? 'wrap-anywhere' : 'truncate'

  if (!atHead) {
    return (
      <span className={`${fit} text-muted-foreground line-through decoration-muted-foreground/50`}>
        {label ?? qualifiedPath}
      </span>
    )
  }

  return (
    <Link
      to="/projects/$project/file"
      params={{ project }}
      search={fileSearch(qualifiedPath, line, origin)}
      className={`${fit} hover:text-primary hover:underline`}
    >
      {label ?? qualifiedPath}
    </Link>
  )
}
