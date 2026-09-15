import { useSuspenseQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { FileText, Folder, GitBranch } from 'lucide-react'
import { treeSearch } from '@/features/files/browseParams'
import { PathBreadcrumb } from '@/features/files/PathBreadcrumb'
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

  return (
    <div className="space-y-3">
      <PathBreadcrumb project={project} path={level.path} />

      {level.entries.length === 0 ? (
        <p className="text-sm text-muted-foreground">
          {level.repositoryLevel
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
              {level.entries.map((entry) => {
                // A directory is a level to walk into; a file is a thing to read. The server has
                // already told us which by whether it counted the files beneath.
                const files = entry.files
                const isFile = files === null
                const Icon = isFile ? FileText : level.repositoryLevel ? GitBranch : Folder
                return (
                  <TableRow key={entry.qualifiedPath} className="hover:bg-muted/40">
                    <TableCell className="py-1.5 font-mono text-xs">
                      <Link
                        to={isFile ? '/projects/$project/file' : '/projects/$project/files'}
                        params={{ project }}
                        search={
                          isFile ? { path: entry.qualifiedPath } : treeSearch(entry.qualifiedPath)
                        }
                        className="flex items-center gap-2 hover:underline"
                      >
                        <Icon className="size-3.5 shrink-0 text-muted-foreground" />
                        {entry.name}
                        {files === null ? null : (
                          <span className="font-sans text-muted-foreground">
                            ({formatCount(files)})
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
                    <TableCell className="py-1.5 text-right text-sm text-muted-foreground tabular-nums">
                      {formatCount(entry.lines)}
                    </TableCell>
                    <TableCell className="py-1.5 text-right text-sm text-muted-foreground tabular-nums">
                      {formatBytes(entry.sizeBytes)}
                    </TableCell>
                  </TableRow>
                )
              })}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  )
}
