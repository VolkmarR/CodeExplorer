import { Link } from '@tanstack/react-router'
import type { View } from '@/components/appNavigation'
import { commitSearch } from '@/features/history/commitParams'
import type { CommitRef } from '@/lib/api'
import { formatDate, shortSha } from '@/lib/format'
import { cn } from '@/lib/utils'

/**
 * One commit in one line: the abbreviated id, the day, the author, and the subject when there is
 * room for it. The same rendering wherever the UI names a commit — a repository's newest, a file's
 * first and last, the run of lines in a blame gutter — so a reader learns the shape once. The full id
 * is in the title, for the one time it is needed.
 *
 * With `project` the whole line opens the commit's own page. That is the only thing a link needs —
 * the project and the SHA — so every place the line is drawn gains the link from here rather than
 * from four edits that would each have to remember the route.
 */
export function CommitLine({
  commit,
  project,
  from,
  subject = true,
  sha = true,
  className,
}: {
  commit: CommitRef
  /** The project the commit is in. Given, the line links to it; left out, it is plain text. */
  project?: string
  /** Which view the reader is on, so the commit page's trail says where they came from. */
  from?: View
  /** Whether to show the subject after the author. Off where the line has to fit a gutter. */
  subject?: boolean
  /** Whether to show the abbreviated id. Off where a column beside it already shows the same one. */
  sha?: boolean
  className?: string
}) {
  const line = (
    <>
      {sha ? (
        <span className="font-mono text-xs text-muted-foreground">{shortSha(commit.sha)}</span>
      ) : null}
      <span className="shrink-0 text-muted-foreground">{formatDate(commit.authoredAt)}</span>
      <span className="shrink-0">{commit.authorName}</span>
      {subject ? <span className="truncate text-muted-foreground">— {commit.subject}</span> : null}
    </>
  )

  const classes = cn('inline-flex min-w-0 items-baseline gap-2', className)

  if (!project) {
    return (
      <span className={classes} title={commit.sha}>
        {line}
      </span>
    )
  }

  return (
    <Link
      to="/projects/$project/commit"
      params={{ project }}
      search={commitSearch(commit.sha, from)}
      title={commit.sha}
      className={cn(classes, 'hover:text-primary hover:underline')}
    >
      {line}
    </Link>
  )
}
