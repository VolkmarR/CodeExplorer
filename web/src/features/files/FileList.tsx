import { useSuspenseQuery } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { fileSearch } from '@/features/files/fileParams'
import type { BrowseParameters } from '@/features/files/browseParams'
import { browseQuery } from '@/features/files/queries'
import { Button } from '@/components/ui/button'
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
  const navigate = useNavigate()
  const { data: listing } = useSuspenseQuery(browseQuery(project, search))

  if (listing.total === 0) {
    return <p className="text-sm text-muted-foreground">Nothing matches that glob.</p>
  }

  const lastPage = Math.max(1, Math.ceil(listing.total / listing.pageSize))
  const first = (listing.page - 1) * listing.pageSize + 1

  return (
    <div className="space-y-3">
      <p className="text-sm text-muted-foreground">
        {formatCount(listing.total)} {listing.total === 1 ? 'file' : 'files'}
        {/* Which rows these are, not how many: a page in the middle of a wide match is otherwise
            indistinguishable from the whole of a narrow one. */}
        {lastPage > 1
          ? `, showing ${formatCount(first)}–${formatCount(first + listing.files.length - 1)}`
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

      {/* The same pager the search results carry, because the two views page the same way and an
          operator moves between them. */}
      {lastPage > 1 ? (
        <div className="flex items-center gap-4 pt-1">
          <span className="text-sm text-muted-foreground tabular-nums">
            Page {listing.page} of {formatCount(lastPage)}
          </span>
          <div className="ml-auto flex gap-2">
            <Button
              variant="outline"
              size="sm"
              disabled={listing.page <= 1}
              onClick={() =>
                void navigate({
                  params: { project },
                  search: { ...search, page: listing.page - 1 },
                  to: '/projects/$project/files',
                })
              }
            >
              Previous
            </Button>
            <Button
              variant="outline"
              size="sm"
              disabled={listing.page >= lastPage}
              onClick={() =>
                void navigate({
                  params: { project },
                  search: { ...search, page: listing.page + 1 },
                  to: '/projects/$project/files',
                })
              }
            >
              Next
            </Button>
          </div>
        </div>
      ) : null}
    </div>
  )
}
