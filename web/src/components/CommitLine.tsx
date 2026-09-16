import type { CommitRef } from '@/lib/api'
import { formatDate, shortSha } from '@/lib/format'
import { cn } from '@/lib/utils'

/**
 * One commit in one line: the abbreviated id, the day, the author, and the subject when there is
 * room for it. The same rendering wherever the UI names a commit — a repository's newest, a file's
 * first and last, the run of lines in a blame gutter — so a reader learns the shape once. The full id
 * is in the title, for the one time it is needed.
 */
export function CommitLine({
  commit,
  subject = true,
  sha = true,
  className,
}: {
  commit: CommitRef
  /** Whether to show the subject after the author. Off where the line has to fit a gutter. */
  subject?: boolean
  /** Whether to show the abbreviated id. Off where a column beside it already shows the same one. */
  sha?: boolean
  className?: string
}) {
  return (
    <span className={cn('inline-flex min-w-0 items-baseline gap-2', className)} title={commit.sha}>
      {sha ? (
        <span className="font-mono text-xs text-muted-foreground">{shortSha(commit.sha)}</span>
      ) : null}
      <span className="shrink-0 text-muted-foreground">{formatDate(commit.authoredAt)}</span>
      <span className="shrink-0">{commit.authorName}</span>
      {subject ? <span className="truncate text-muted-foreground">— {commit.subject}</span> : null}
    </span>
  )
}
