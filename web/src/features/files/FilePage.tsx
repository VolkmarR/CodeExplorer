import { useSuspenseQuery } from '@tanstack/react-query'
import { Link, useParams, useSearch } from '@tanstack/react-router'
import { CodeView } from '@/features/files/CodeView'
import { fileQuery } from '@/features/files/queries'
import { Button } from '@/components/ui/button'
import { formatBytes, formatCount } from '@/lib/format'

/**
 * One file of the index, by qualified path. The path and the line both come from the URL, so a link
 * to a particular line of a particular repository's file is the ordinary way to share one.
 */
export function FilePage() {
  const { project } = useParams({ from: '/projects/$project/file' })
  const { line, path } = useSearch({ from: '/projects/$project/file' })
  const { data: file } = useSuspenseQuery(fileQuery(project, path))

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="font-mono text-lg break-all">{file.qualifiedPath}</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            {formatCount(file.lineCount)} lines, {formatBytes(file.sizeBytes)}
          </p>
        </div>
        {/* Back to the folder this file is in, which is where the reader came from and where the
            sibling they want next is. The qualified path already names it. */}
        <Button
          render={
            <Link
              to="/projects/$project/files"
              params={{ project }}
              search={{ glob: '', path: path.slice(0, path.lastIndexOf('/')) }}
            />
          }
          variant="ghost"
        >
          Back to folder
        </Button>
      </div>

      {/* A file committed but not indexed — binary, or over the size cap — has no lines to show, and
          saying which is more use than an empty pane. */}
      {file.skipReason ? (
        <p className="rounded-lg border bg-card px-4 py-3 text-sm text-muted-foreground">
          Not indexed: {file.skipReason}.
        </p>
      ) : (
        <CodeView content={file.content} path={file.qualifiedPath} line={line} />
      )}
    </div>
  )
}
