import { useQuery, useSuspenseQuery } from '@tanstack/react-query'
import { useParams, useSearch } from '@tanstack/react-router'
import { Copy, History, WrapText } from 'lucide-react'
import { useState } from 'react'
import { CodeView } from '@/features/files/CodeView'
import { FileRail } from '@/features/files/FileRail'
import { PathBreadcrumb } from '@/features/files/PathBreadcrumb'
import { blameQuery, fileQuery } from '@/features/files/queries'
import { CommitLine } from '@/components/CommitLine'
import { ErrorPanel } from '@/components/ErrorPanel'
import { PageCard } from '@/components/PageCard'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/toast'
import { languageFor } from '@/highlight/highlighter'
import { fileName, formatBytes, formatCount } from '@/lib/format'

/**
 * One file of the index, by qualified path. The path and the line both come from the URL, so a link
 * to a particular line of a particular repository's file is the ordinary way to share one.
 *
 * The code sits beside a rail rather than alone, so that the page answers the questions the MCP
 * tools answer over the same index rather than only "what does this file say".
 */
export function FilePage() {
  const { project } = useParams({ from: '/projects/$project/file' })
  const { line, path, from, fromCommit } = useSearch({ from: '/projects/$project/file' })
  const { data: file } = useSuspenseQuery(fileQuery(project, path))

  // Kept whole and passed on, so a line clicked in the code or a declaration jumped to in the rail
  // lands on the same file with the same trail above it rather than back under Files.
  const origin = { commit: fromCommit, view: from }

  // Off by default: code is written for a wide column and a folded line loses its indentation, which
  // is what the reader of a C# file scans by. Local to the page and not the URL — how someone likes
  // to read is not part of what a shared link points at.
  const [wrap, setWrap] = useState(false)

  // Off by default too, and for a different reason: the gutter is a second column of text beside
  // the code, and most readers of a file are reading the code. The runs are the same cache entry
  // the rail's recent commits read, so turning the gutter on costs nothing once the rail has them.
  const [blaming, setBlaming] = useState(false)
  const hasHistory = file.lastCommit !== null
  const blame = useQuery({
    ...blameQuery(project, path),
    enabled: blaming && hasHistory && file.skipReason === null,
  })

  return (
    <PageCard
      title={fileName(file.qualifiedPath)}
      hint={<PathBreadcrumb project={project} path={file.qualifiedPath} />}
      badges={
        <div className="flex flex-wrap items-center gap-2">
          <Badge variant="secondary">{languageFor(file.qualifiedPath)}</Badge>
          <span className="text-xs text-muted-foreground tabular-nums">
            {formatCount(file.lineCount)} lines · {formatBytes(file.sizeBytes)}
          </span>
          {/* The last commit is the one a reader asks about — "who touched this?" — so it carries
              the subject. Nothing is shown for a file without history rather than "never changed",
              which would be a claim the index cannot make (CONTEXT.md, History). */}
          {file.lastCommit ? (
            <span className="flex min-w-0 items-baseline gap-1 text-xs">
              <span className="text-muted-foreground">last changed</span>
              <CommitLine commit={file.lastCommit} project={project} from={from ?? 'files'} />
            </span>
          ) : null}
        </div>
      }
      actions={
        <>
          <Tooltip>
            <TooltipTrigger
              render={
                <Button
                  variant="ghost"
                  size="icon-sm"
                  onClick={() => {
                    // The qualified path is what an agent quotes and what the MCP tools take, so it
                    // is the thing worth copying — not the URL, which the address bar already offers.
                    void navigator.clipboard
                      .writeText(file.qualifiedPath)
                      .then(() =>
                        toast.add({ title: 'Path copied', type: 'success', timeout: 2000 }),
                      )
                  }}
                >
                  <Copy />
                  <span className="sr-only">Copy the qualified path</span>
                </Button>
              }
            />
            <TooltipContent>Copy the qualified path</TooltipContent>
          </Tooltip>
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
        </>
      }
    >
      <div className="flex flex-col gap-4 xl:flex-row">
        <div className="min-w-0 flex-1 space-y-4">
          {blame.error ? (
            <ErrorPanel error={blame.error} title="The attribution could not be read" />
          ) : null}

          {/* A file committed but not indexed — binary, or over the size cap — has no lines to show,
              and saying which is more use than an empty pane. */}
          {file.skipReason ? (
            <p className="rounded-lg border px-4 py-3 text-sm text-muted-foreground">
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
              origin={origin}
            />
          )}
        </div>
        <FileRail project={project} file={file} origin={origin} />
      </div>
    </PageCard>
  )
}
