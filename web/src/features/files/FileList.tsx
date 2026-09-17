import { useSuspenseQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { fileSearch } from '@/features/files/fileParams'
import type { BrowseParameters } from '@/features/files/browseParams'
import { browseQuery } from '@/features/files/queries'
import { formatBytes, formatCount } from '@/lib/format'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'

/** The files a glob matched, each linking through to its content by qualified path. */
export function FileList({ project, search }: { project: string; search: BrowseParameters }) {
  const { data: listing } = useSuspenseQuery(browseQuery(project, search))

  if (listing.total === 0) {
    return <p className="text-sm text-muted-foreground">Nothing matches that glob.</p>
  }

  return (
    <div className="space-y-3">
      <p className="text-sm text-muted-foreground">
        {formatCount(listing.total)} {listing.total === 1 ? 'file' : 'files'}
        {/* The server caps the page; saying so beats a listing that silently stops. */}
        {listing.files.length < listing.total
          ? `, showing the first ${formatCount(listing.files.length)}`
          : ''}
      </p>
      <div className="overflow-x-auto rounded-lg border bg-card">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Qualified path</TableHead>
              <TableHead className="text-right">Lines</TableHead>
              <TableHead className="text-right">Size</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {listing.files.map((file) => (
              <TableRow key={file.qualifiedPath}>
                <TableCell className="font-mono text-xs">
                  <Link
                    to="/projects/$project/file"
                    params={{ project }}
                    search={fileSearch(file.qualifiedPath)}
                    className="hover:underline"
                  >
                    {file.qualifiedPath}
                  </Link>
                  {/* A file committed but not indexed still exists, and the reason is what the
                      operator needs in order to decide whether it matters. */}
                  {file.skipReason ? (
                    <span className="ml-2 font-sans text-muted-foreground">
                      ({file.skipReason})
                    </span>
                  ) : null}
                </TableCell>
                <TableCell className="text-right text-sm text-muted-foreground">
                  {formatCount(file.lineCount)}
                </TableCell>
                <TableCell className="text-right text-sm text-muted-foreground">
                  {formatBytes(file.sizeBytes)}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
    </div>
  )
}
