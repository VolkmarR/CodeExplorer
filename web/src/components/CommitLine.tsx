import { Link } from '@tanstack/react-router'
import type { View } from '@/lib/urls/views'
import { commitSearch } from '@/lib/urls/commitParams'
import { formatDate, shortSha } from '@/lib/format'
import { cn } from '@/lib/utils'

/**
 * The four fields this line draws, declared here rather than imported: `components/` is shared by
 * every feature and may not depend on one, and the change log's `CommitRef` — which this matches
 * field for field — belongs to `features/history`. Anything with these four fields may be drawn,
 * which is what lets a blame run, a repository's newest and a file's first and last all pass theirs.
 *
 * The two are kept honest by the three call sites rather than by a test: each passes a shape read
 * from the API, so a field this line names that `CommitRef` stops carrying fails to typecheck there.
 * A field added to `CommitRef` and not drawn here is no drift — this is what the line renders, not a
 * copy of the record.
 */
interface Commit {
  sha: string
  authorName: string
  authoredAt: string
  subject: string
}

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
  commit: Commit
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
