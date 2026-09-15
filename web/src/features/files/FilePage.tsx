import { useSuspenseQuery } from '@tanstack/react-query'
import { useParams, useSearch } from '@tanstack/react-router'
import { Copy, WrapText } from 'lucide-react'
import { useState } from 'react'
import { CodeView } from '@/features/files/CodeView'
import { PathBreadcrumb } from '@/features/files/PathBreadcrumb'
import { fileQuery } from '@/features/files/queries'
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

  return (
    <div className="space-y-4">
      {/* Sticky, so on a long file the path and the tools are there when the reader wants the next
          file or a copy of this one's name. The header above scrolls away; this bar stays. */}
      <div className="sticky top-0 z-10 -mx-6 flex flex-wrap items-center justify-between gap-x-4 gap-y-2 border-b bg-background px-6 py-3">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
          <PathBreadcrumb project={project} path={file.qualifiedPath} />
          <span className="text-xs text-muted-foreground">
            {formatCount(file.lineCount)} lines · {formatBytes(file.sizeBytes)}
          </span>
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
        </div>
      </div>

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
        />
      )}
    </div>
  )
}
