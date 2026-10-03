import { useSuspenseQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { fileSearch } from '@/lib/urls/fileParams'
import type { BrowseParameters } from '@/lib/urls/browseParams'
import { browseQuery } from '@/features/files/queries'
import { Pager } from '@/components/Pager'
import { formatBytes, formatCount, formatCountOf } from '@/lib/format'
import { pageSpan } from '@/lib/paging'
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

  const span = pageSpan(listing.page, listing.pageSize, listing.total)

  return (
    <div className="space-y-3">
      <p className="text-sm text-muted-foreground">
        {formatCountOf(listing.total, 'file')}
        {/* Which rows these are, not how many: a page in the middle of a wide match is otherwise
            indistinguishable from the whole of a narrow one. */}
        {span.lastPage > 1 ? `, showing ${formatCount(span.first)}–${formatCount(span.last)}` : ''}
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

      <Pager page={listing.page} total={listing.total} pageSize={listing.pageSize} />
    </div>
  )
}
