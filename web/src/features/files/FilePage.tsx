import { useQuery, useSuspenseQuery } from '@tanstack/react-query'
import { useParams, useSearch } from '@tanstack/react-router'
import { Copy, History, WrapText } from 'lucide-react'
import { useState } from 'react'
import { CodeView } from '@/features/files/CodeView'
import { PathBreadcrumb } from '@/features/files/PathBreadcrumb'
import { blameQuery, fileQuery } from '@/features/files/queries'
import { CommitLine } from '@/components/CommitLine'
import { ErrorPanel } from '@/components/ErrorPanel'
import { Button } from '@/components/ui/button'
import { toast } from '@/components/ui/toast'
import { formatBytes, formatCount } from '@/lib/format'

/**
 * One file of the index, by qualified path. The path and the line both come from the URL, so a link
 * to a particular line of a particular repository's file is the ordinary way to share one.
 */
export function FilePage() {
  const { project } = useParams({ from: '/projects/$project/file' })
  const { line, path } = useSearch({ from: '/projects/$project/file' })
  const { data: file } = useSuspenseQuery(fileQuery(project, path))

  // Off by default: code is written for a wide column and a folded line loses its indentation, which
  // is what the reader of a C# file scans by. Local to the page and not the URL — how someone likes
  // to read is not part of what a shared link points at.
  const [wrap, setWrap] = useState(false)

  // Off by default too, and for a different reason: the gutter is a second column of text beside
  // the code, and most readers of a file are reading the code. The runs are fetched only once asked
  // for, and only for a file the index has lines of — a skipped file has nothing to attribute.
  const [blaming, setBlaming] = useState(false)
  const hasHistory = file.lastCommit !== null
  const blame = useQuery({
    ...blameQuery(project, path),
    enabled: blaming && hasHistory && file.skipReason === null,
  })

  return (
    <div className="space-y-4">
      {/* Sticky, so on a long file the path and the tools are there when the reader wants the next
          file or a copy of this one's name. The header above scrolls away; this bar stays. */}
      <div className="sticky top-0 z-10 -mx-6 flex flex-wrap items-center justify-between gap-x-4 gap-y-2 border-b bg-background px-6 py-3">
        <div className="flex min-w-0 flex-col gap-1">
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
            <PathBreadcrumb project={project} path={file.qualifiedPath} />
            <span className="text-xs text-muted-foreground">
              {formatCount(file.lineCount)} lines · {formatBytes(file.sizeBytes)}
            </span>
          </div>
          {/* The last commit is the one a reader asks about — "who touched this?" — so it carries the
              subject; the first is the file's age and gets the day alone. Nothing is shown for a file
              without history rather than "never changed", which would be a claim the index cannot
              make (CONTEXT.md, History). */}
          {file.lastCommit ? (
            <div className="flex min-w-0 flex-wrap items-baseline gap-x-3 text-xs">
              <span className="inline-flex min-w-0 items-baseline gap-1">
                <span className="text-muted-foreground">Last changed</span>
                <CommitLine commit={file.lastCommit} />
              </span>
              {file.firstCommit && file.firstCommit.sha !== file.lastCommit.sha ? (
                <span className="inline-flex items-baseline gap-1">
                  <span className="text-muted-foreground">· first</span>
                  <CommitLine commit={file.firstCommit} subject={false} />
                </span>
              ) : null}
            </div>
          ) : null}
        </div>
        <div className="flex items-center gap-1">
          <Button
            variant="ghost"
            size="sm"
            onClick={() => {
              // The qualified path is what an agent quotes and what the MCP tools take, so it is the
              // thing worth copying — not the URL, which the address bar already offers.
              void navigator.clipboard
                .writeText(file.qualifiedPath)
                .then(() => toast.add({ title: 'Path copied', type: 'success', timeout: 2000 }))
            }}
          >
            <Copy />
            Copy path
          </Button>
          <Button
            variant="ghost"
            size="sm"
            aria-pressed={wrap}
            className="aria-pressed:bg-muted aria-pressed:text-foreground"
            onClick={() => setWrap(!wrap)}
          >
            <WrapText />
            Wrap
          </Button>
          {/* Offered only where it can answer: a file whose repository has no history in the index
              would show an empty gutter, and a button that does nothing is worse than none. */}
          {hasHistory && file.skipReason === null ? (
            <Button
              variant="ghost"
              size="sm"
              aria-pressed={blaming}
              className="aria-pressed:bg-muted aria-pressed:text-foreground"
              onClick={() => setBlaming(!blaming)}
            >
              <History />
              Blame
            </Button>
          ) : null}
        </div>
      </div>

      {blame.error ? (
        <ErrorPanel error={blame.error} title="The attribution could not be read" />
      ) : null}

      {/* A file committed but not indexed — binary, or over the size cap — has no lines to show, and
          saying which is more use than an empty pane. */}
      {file.skipReason ? (
        <p className="rounded-lg border bg-card px-4 py-3 text-sm text-muted-foreground">
          Not indexed: {file.skipReason}.
        </p>
      ) : (
        <CodeView
          project={project}
          content={file.content}
          path={file.qualifiedPath}
          line={line}
          wrap={wrap}
          // Three states the gutter tells apart: not asked for, asked for and on its way, arrived.
          blame={blaming ? (blame.data?.runs ?? null) : undefined}
        />
      )}
    </div>
  )
}
