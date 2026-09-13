import { useMutation, useQueryClient } from '@tanstack/react-query'
import type { RepositoryDetail } from '@/lib/api'
import { api } from '@/lib/api'
import { formatCount, formatTime } from '@/lib/format'
import { ErrorPanel } from '@/components/ErrorPanel'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { invalidateProject } from '@/features/projects/queries'

/** What a row says in place of a value the last build has not produced yet. */
const NOT_INDEXED = 'not indexed yet'

/**
 * A project's repositories as configured, with where the last build found each. A repository added
 * since that build has no commit, no counts and no time, which is how the page shows a rebuild is
 * owed. The build time is the project's: a refresh rebuilds every repository together (CONTEXT.md),
 * so there is no per-repository time to report and the column repeats the one there is.
 */
export function RepositoryTable({
  project,
  repositories,
  builtAt,
}: {
  project: string
  repositories: RepositoryDetail[]
  builtAt: string | null
}) {
  const queryClient = useQueryClient()
  const remove = useMutation({
    mutationFn: (slug: string) => api.removeRepository(project, slug),
    onSuccess: () => invalidateProject(queryClient, project),
  })

  if (repositories.length === 0) {
    return <p className="text-sm text-muted-foreground">No repositories yet. Add one below.</p>
  }

  return (
    <div className="space-y-4">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Slug</TableHead>
            <TableHead>Git URL</TableHead>
            <TableHead>Credential</TableHead>
            <TableHead>Commit</TableHead>
            <TableHead>Indexed</TableHead>
            <TableHead className="text-right">Contents</TableHead>
            <TableHead />
          </TableRow>
        </TableHeader>
        <TableBody>
          {repositories.map((repository) => (
            <TableRow key={repository.slug}>
              <TableCell className="font-mono">{repository.slug}</TableCell>
              <TableCell className="max-w-xs truncate font-mono text-xs text-muted-foreground">
                {repository.url}
              </TableCell>
              <TableCell>
                <Badge variant={repository.hasCredential ? 'secondary' : 'outline'}>
                  {repository.hasCredential ? 'set' : 'not set'}
                </Badge>
              </TableCell>
              <TableCell className="font-mono text-xs">
                {/* Seven characters is what git itself abbreviates to, and what an operator compares
                    against a commit list. */}
                {repository.headCommit ? repository.headCommit.slice(0, 7) : NOT_INDEXED}
              </TableCell>
              <TableCell className="text-sm text-muted-foreground">
                {repository.headCommit ? formatTime(builtAt) : NOT_INDEXED}
              </TableCell>
              <TableCell className="text-right text-sm text-muted-foreground">
                {repository.fileCount === null
                  ? NOT_INDEXED
                  : `${formatCount(repository.fileCount)} files, ${formatCount(repository.lineCount ?? 0)} lines`}
              </TableCell>
              <TableCell className="text-right">
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={remove.isPending}
                  onClick={() => {
                    if (
                      globalThis.confirm(
                        `Remove repository '${repository.slug}' from '${project}'?`,
                      )
                    ) {
                      remove.mutate(repository.slug)
                    }
                  }}
                >
                  Remove
                </Button>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {remove.error ? <ErrorPanel error={remove.error} /> : null}
    </div>
  )
}
