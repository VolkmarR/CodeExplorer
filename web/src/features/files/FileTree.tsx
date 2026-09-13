import { useSuspenseQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { ChevronRight, FileText, Folder, GitBranch } from 'lucide-react'
import { treeQuery } from '@/features/files/queries'
import { formatBytes, formatCount } from '@/lib/format'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'

/**
 * One level of a project as a tree: its repositories at the root, then directories and files. The unit
 * of navigation is the qualified path, which is also what the server answers with, so a row needs no
 * assembly here — the path it carries is the next level or the file to open.
 */
export function FileTree({ project, path }: { project: string; path: string }) {
  const { data: level } = useSuspenseQuery(treeQuery(project, path))
  const segments = level.path === '' ? [] : level.path.split('/')

  return (
    <div className="space-y-3">
      <nav className="flex flex-wrap items-center gap-1 text-sm" aria-label="Breadcrumb">
        <Link
          to="/projects/$project/files"
          params={{ project }}
          search={{ glob: '', path: '' }}
          className="text-muted-foreground hover:text-foreground hover:underline"
        >
          {project}
        </Link>
        {segments.map((segment, index) => (
          <span key={segments.slice(0, index + 1).join('/')} className="flex items-center gap-1">
            <ChevronRight className="size-3.5 text-muted-foreground/60" />
            {index === segments.length - 1 ? (
              <span className="font-mono font-medium">{segment}</span>
            ) : (
              <Link
                to="/projects/$project/files"
                params={{ project }}
                search={{ glob: '', path: segments.slice(0, index + 1).join('/') }}
                className="font-mono text-muted-foreground hover:text-foreground hover:underline"
              >
                {segment}
              </Link>
            )}
          </span>
        ))}
      </nav>

      {level.entries.length === 0 ? (
        <p className="text-sm text-muted-foreground">
          {level.path === ''
            ? 'This project has no repositories in its index.'
            : 'Nothing here in the index.'}
        </p>
      ) : (
        <div className="overflow-x-auto rounded-lg border bg-card">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead className="text-right">Lines</TableHead>
                <TableHead className="text-right">Size</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {level.entries.map((entry) => (
                <TableRow key={entry.qualifiedPath}>
                  <TableCell className="font-mono text-xs">
                    <Link
                      // A directory is a level to walk into; a file is a thing to read. The server has
                      // already told us which by whether it counted the files beneath.
                      to={
                        entry.files === null
                          ? '/projects/$project/file'
                          : '/projects/$project/files'
                      }
                      params={{ project }}
                      search={
                        entry.files === null
                          ? { path: entry.qualifiedPath }
                          : { glob: '', path: entry.qualifiedPath }
                      }
                      className="flex items-center gap-2 hover:underline"
                    >
                      {entry.files === null ? (
                        <FileText className="size-3.5 shrink-0 text-muted-foreground" />
                      ) : level.path === '' ? (
                        <GitBranch className="size-3.5 shrink-0 text-muted-foreground" />
                      ) : (
                        <Folder className="size-3.5 shrink-0 text-muted-foreground" />
                      )}
                      {entry.name}
                      {entry.files === null ? null : (
                        <span className="font-sans text-muted-foreground">
                          ({formatCount(entry.files)})
                        </span>
                      )}
                    </Link>
                    {/* A file committed but not indexed still exists, and the reason is what the
                        operator needs in order to decide whether it matters. */}
                    {entry.skipReason ? (
                      <span className="ml-5 font-sans text-muted-foreground">
                        ({entry.skipReason})
                      </span>
                    ) : null}
                  </TableCell>
                  <TableCell className="text-right text-sm text-muted-foreground">
                    {formatCount(entry.lines)}
                  </TableCell>
                  <TableCell className="text-right text-sm text-muted-foreground">
                    {formatBytes(entry.sizeBytes)}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  )
}
